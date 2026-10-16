namespace Snipper.Analysis;

using System.Collections.Concurrent;
using System.Collections.Frozen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Models;

/// <summary>
/// SNP0005 (internal) / SNP0006 (public) — Flags members with zero references
/// across the solution. Certainty is demoted to Advisory when the member could
/// be reached via reflection, DI registration, friend assemblies, or when it
/// sits on an externally consumable public API surface.
/// </summary>
public sealed class UnusedNonPrivateMemberAnalyser : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0005", "SNP0006"];

    public SharedIndex SharedIndexes => SharedIndex.AnalysisScope | SharedIndex.SolutionUsageIndex | SharedIndex.FrameworkEvidenceIndex;

    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        progress?.Invoke("UnusedNonPrivateMemberAnalyser: scanning DI registrations");
        var diRegisteredTypes = await DiRegistrationScanner.ScanAsync(solution, cancellationToken).ConfigureAwait(false);
        var referenceSearch = await ReferenceSearch.GetAsync(solution, cancellationToken).ConfigureAwait(false);
        var frameworkEvidence = await FrameworkEvidenceIndex.GetAsync(solution, cancellationToken).ConfigureAwait(false);

        // Phase A: enumerate candidates. Cheap semantic work (declared symbols,
        // evidence filters), in parallel across documents. Phase B is
        // order-independent, so a bag will do.
        var candidates = new ConcurrentBag<Candidate>();
        var documents = await AnalysisScope.GetAsync(solution, cancellationToken).ConfigureAwait(false);
        progress?.Invoke($"UnusedNonPrivateMemberAnalyser: scanning {documents.Count} documents");
        await Parallel.ForEachAsync(
            documents,
            AnalysisParallelism.CreateOptions(cancellationToken),
            async (scopedDocument, documentToken) =>
            {
                var (document, root) = scopedDocument;
                var semanticModel = await document.GetSemanticModelAsync(documentToken).ConfigureAwait(false);
                if (semanticModel is null)
                {
                    return;
                }

                foreach (var node in root.DescendantNodes())
                {
                    documentToken.ThrowIfCancellationRequested();

                    // Syntax-level gate: only declaration nodes with a public/internal
                    // modifier reach the semantic model.
                    if (!IsPotentiallyNonPrivateDeclaration(node))
                    {
                        continue;
                    }

                    var symbol = semanticModel.GetDeclaredSymbol(node, documentToken);
                    if (symbol is null || !IsCandidate(symbol))
                    {
                        continue;
                    }

                    if (ExclusionEngine.ShouldExclude(symbol))
                    {
                        continue;
                    }

                    // Framework evidence (Wave 4): serializers, model binders, and
                    // DI-activated contracts invoke members without any C# reference.
                    // Checking here also skips the reference search entirely.
                    if (frameworkEvidence.IsUsed(symbol))
                    {
                        continue;
                    }

                    candidates.Add(new Candidate(symbol, scopedDocument.HasFriendAssemblies));
                }
            }).ConfigureAwait(false);

        // Phase B: reference searches — the expensive FindReferencesAsync scans,
        // parallelised behind the revertible switch (SNIPPER_MAX_DOP=1 reverts to
        // sequential). Adoption gate, spike 2026-09-19: zero finding drift vs
        // sequential on Snipper.slnx + fixture2 + the monorepo. The bag order
        // depends on scheduling; AnalysisRunner puts it in FindingOrder.
        var findings = new ConcurrentBag<SnipperFinding>();
        await Parallel.ForEachAsync(
            candidates,
            AnalysisParallelism.CreateOptions(cancellationToken),
            async (candidate, searchToken) =>
            {
                // ReferenceSearch decides where a reference could be: internal members
                // without friend assemblies only in their own project, and only in
                // documents that spell the name - none proves the symbol unused.
                var hasReference = await referenceSearch.HasAnyAsync(candidate.Symbol, searchToken).ConfigureAwait(false);

                // Rescue passes run only when the symbol has no direct references of
                // its own — each costs a reference search of its own.
                if (!hasReference
                    && candidate.Symbol is IMethodSymbol method
                    && await HasUsedInterfaceContractAsync(method, referenceSearch, searchToken).ConfigureAwait(false))
                {
                    return;
                }

                if (!hasReference
                    && candidate.Symbol is INamedTypeSymbol { IsStatic: true } staticType
                    && await ExtensionMethodUsageQuery.HasAnyUsedExtensionMethodAsync(staticType, referenceSearch, searchToken).ConfigureAwait(false))
                {
                    hasReference = true;
                }

                if (!hasReference)
                {
                    findings.Add(CreateFinding(candidate.Symbol, candidate.HasFriendAssemblies, diRegisteredTypes));
                }
            }).ConfigureAwait(false);

        return [.. findings];
    }

    private sealed record Candidate(ISymbol Symbol, bool HasFriendAssemblies);

    private static bool IsPotentiallyNonPrivateDeclaration(SyntaxNode node)
    {
        var member = node switch
        {
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: FieldDeclarationSyntax field } } => field,
            MemberDeclarationSyntax declaration when declaration is not FieldDeclarationSyntax => declaration,
            _ => null,
        };

        if (member is null)
        {
            return false;
        }

        foreach (var modifier in member.Modifiers)
        {
            if (modifier.IsKind(SyntaxKind.PublicKeyword) || modifier.IsKind(SyntaxKind.InternalKeyword))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCandidate(ISymbol symbol)
    {
        if (symbol.DeclaredAccessibility is not (Accessibility.Internal or Accessibility.Public))
        {
            return false;
        }

        if (symbol.ContainingType?.TypeKind is TypeKind.Interface)
        {
            return false;
        }

        return symbol switch
        {
            IMethodSymbol method => method.MethodKind is MethodKind.Ordinary
                && !method.IsOverride
                && !method.IsImplicitlyDeclared
                && method.Name is not ("Main" or "<Main>$")
                && !(method.Name == "Deconstruct" && method.ContainingType?.IsRecord == true),
            IPropertySymbol property => !property.IsIndexer && !property.IsOverride && !property.IsImplicitlyDeclared,
            INamedTypeSymbol type => type.TypeKind is TypeKind.Class or TypeKind.Struct && !type.IsImplicitlyDeclared,
            _ => false,
        };
    }

    private static SnipperFinding CreateFinding(
        ISymbol symbol,
        bool hasFriendAssemblies,
        FrozenSet<INamedTypeSymbol> diRegisteredTypes)
    {
        var isInternal = symbol.DeclaredAccessibility == Accessibility.Internal;
        var isDiRegistered = symbol.ContainingType is not null
            && diRegisteredTypes.Contains(symbol.ContainingType.OriginalDefinition);
        var onExportedType = IsOnExportedType(symbol);

        var certainty = CertaintyTier.Moderate;
        if (hasFriendAssemblies || isDiRegistered || (isInternal is false && onExportedType))
        {
            certainty = CertaintyTier.Advisory;
        }

        var lineSpan = symbol.Locations[0].GetLineSpan();
        var kind = symbol is INamedTypeSymbol ? "type" : "member";

        return new SnipperFinding(
            RuleId: isInternal ? "SNP0005" : "SNP0006",
            Title: isInternal ? "Unused Internal Member" : "Unused Public Member",
            Message: $"{(isInternal ? "Internal" : "Public")} {kind} '{symbol.Name}' has no references in the solution.",
            Certainty: certainty,
            Category: isInternal ? FindingCategory.UnusedInternalMember : FindingCategory.UnusedPublicMember,
            FilePath: lineSpan.Path ?? string.Empty,
            LineNumber: lineSpan.StartLinePosition.Line + 1,
            CharacterOffset: lineSpan.StartLinePosition.Character + 1);
    }

    private static bool IsOnExportedType(ISymbol symbol)
    {
        // For type symbols the walk starts at the type itself: a top-level public
        // type has no containing type but is still exported API surface.
        var current = symbol is INamedTypeSymbol typeSymbol ? typeSymbol : symbol.ContainingType;
        if (current is null)
        {
            return false;
        }

        for (var type = current; type is not null; type = type.ContainingType)
        {
            if (type.DeclaredAccessibility is not Accessibility.Public)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> HasUsedInterfaceContractAsync(
        IMethodSymbol method,
        ReferenceSearch referenceSearch,
        CancellationToken cancellationToken)
    {
        var containingType = method.ContainingType;
        if (containingType is null)
        {
            return false;
        }

        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var interfaceMember in iface.GetMembers(method.Name).OfType<IMethodSymbol>())
            {
                var implementation = containingType.FindImplementationForInterfaceMember(interfaceMember);
                if (!SymbolEqualityComparer.Default.Equals(implementation, method))
                {
                    continue;
                }

                // A call dispatched through the interface references the interface
                // member, whose name must appear at the call site.
                if (await referenceSearch.HasAnyAsync(interfaceMember, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
