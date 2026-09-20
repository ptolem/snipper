namespace Snipper.Analysis;

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
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
public sealed class UnusedNonPrivateMemberAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0005", "SNP0006"];

    private readonly AnalysisExclusions _exclusions = exclusions ?? AnalysisExclusions.None;

    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        progress?.Invoke("UnusedNonPrivateMemberAnalyser: scanning DI registrations");
        var diRegisteredTypes = await DiRegistrationScanner.ScanAsync(solution, cancellationToken).ConfigureAwait(false);
        var analysisRoots = ExclusionEngine.GetAnalysisRootDirectories(solution);
        var usageIndex = SolutionUsageIndex.Get(solution);
        var frameworkEvidence = FrameworkEvidenceIndex.Get(solution);

        // Phase A: enumerate candidates. Cheap semantic work (declared symbols,
        // evidence filters, usage-index search-document pre-computation),
        // sequential per the workspace's ConcurrentBuild=false contract.
        var candidates = new List<Candidate>();
        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation)
            {
                continue;
            }

            progress?.Invoke($"UnusedNonPrivateMemberAnalyser: scanning {project.Name}");

            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            var hasFriendAssemblies = compilation.Assembly.GetAttributes()
                .Any(static a => a.AttributeClass?.Name is "InternalsVisibleTo" or "InternalsVisibleToAttribute");

            foreach (var document in project.Documents)
            {
                if (!document.SupportsSyntaxTree)
                {
                    continue;
                }

                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                if (semanticModel is null || root is null || ExclusionEngine.ShouldSkipDocument(document.FilePath, root, analysisRoots))
                {
                    continue;
                }

                foreach (var node in root.DescendantNodes())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Syntax-level gate: only declaration nodes with a public/internal
                    // modifier reach the semantic model.
                    if (!IsPotentiallyNonPrivateDeclaration(node))
                    {
                        continue;
                    }

                    var symbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
                    if (symbol is null || !IsCandidate(symbol))
                    {
                        continue;
                    }

                    if (ExclusionEngine.ShouldExclude(symbol))
                    {
                        continue;
                    }

                    if (ExclusionEngine.IsNamespaceExcluded(symbol, _exclusions))
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

                    // Internal members without friend assemblies can only be referenced
                    // within their own project; public members can be referenced anywhere.
                    // The usage index restricts the search to documents that textually
                    // contain the symbol name; an empty set proves the symbol is unused.
                    var candidateDocuments = GetCandidateDocuments(usageIndex, project, symbol, hasFriendAssemblies);

                    candidates.Add(new Candidate(symbol, candidateDocuments, hasFriendAssemblies));
                }
            }
        }

        // Phase B: reference searches — the expensive FindReferencesAsync scans,
        // parallelised behind the revertible switch (SNIPPER_MAX_DOP=1 reverts to
        // sequential). Adoption gate, spike 2026-09-19: zero finding drift vs
        // sequential on Snipper.slnx + fixture2 + the monorepo. Findings are
        // sorted on exit, so scheduling cannot affect output.
        var findings = new ConcurrentBag<SnipperFinding>();
        await Parallel.ForEachAsync(
            candidates,
            AnalysisParallelism.CreateOptions(cancellationToken),
            async (candidate, searchToken) =>
            {
                var hasReference = candidate.CandidateDocuments.Count > 0
                    && await SymbolReferenceQuery.HasAnyReferenceAsync(candidate.Symbol, solution, candidate.CandidateDocuments, searchToken).ConfigureAwait(false);

                // Rescue passes run only when the symbol has no direct references of
                // its own — each costs its own FindReferencesAsync scan.
                if (!hasReference
                    && candidate.Symbol is IMethodSymbol method
                    && await HasUsedInterfaceContractAsync(method, solution, usageIndex, searchToken).ConfigureAwait(false))
                {
                    return;
                }

                if (!hasReference
                    && candidate.Symbol is INamedTypeSymbol { IsStatic: true } staticType
                    && await HasAnyUsedExtensionMethodAsync(staticType, solution, usageIndex, searchToken).ConfigureAwait(false))
                {
                    hasReference = true;
                }

                if (!hasReference)
                {
                    findings.Add(CreateFinding(candidate.Symbol, candidate.HasFriendAssemblies, diRegisteredTypes));
                }
            }).ConfigureAwait(false);

        return findings
            .OrderBy(static f => f.FilePath, StringComparer.Ordinal)
            .ThenBy(static f => f.LineNumber)
            .ThenBy(static f => f.CharacterOffset)
            .ThenBy(static f => f.RuleId, StringComparer.Ordinal)
            .ThenBy(static f => f.Message, StringComparer.Ordinal)
            .ToList();
    }

    private sealed record Candidate(ISymbol Symbol, IImmutableSet<Document> CandidateDocuments, bool HasFriendAssemblies);

    private static IImmutableSet<Document> GetCandidateDocuments(
        SolutionUsageIndex usageIndex,
        Project project,
        ISymbol symbol,
        bool hasFriendAssemblies)
    {
        var projectScoped = symbol.DeclaredAccessibility == Accessibility.Internal && !hasFriendAssemblies;
        var documents = projectScoped
            ? usageIndex.GetDocumentsUsingName(project, symbol.Name)
            : usageIndex.GetDocumentsUsingName(symbol.Name);

        // Attribute applications omit the "Attribute" suffix, so the textual
        // usage index only ever sees the short spelling — a full-name lookup
        // finds nothing and every applied attribute class looks unreferenced
        // (milkrun 1.6.1: test-priority attributes flagged while in active
        // use). Union the short spelling's documents; the semantic reference
        // search still does the real filtering.
        if (symbol is INamedTypeSymbol { Name: { Length: > 9 } typeName }
            && typeName.EndsWith("Attribute", StringComparison.Ordinal))
        {
            var shortName = typeName[..^"Attribute".Length];
            documents = documents.Union(projectScoped
                ? usageIndex.GetDocumentsUsingName(project, shortName)
                : usageIndex.GetDocumentsUsingName(shortName));
        }

        return documents;
    }

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
            CharacterOffset: lineSpan.StartLinePosition.Character + 1,
            Symbol: symbol);
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

    private static async Task<bool> HasAnyUsedExtensionMethodAsync(
        INamedTypeSymbol staticType,
        Solution solution,
        SolutionUsageIndex usageIndex,
        CancellationToken cancellationToken)
    {
        // Extension invocations (value.Method()) bind to the method symbol, not the
        // class — a heavily used extension-method holder shows zero type references
        // and its name may never appear in source. The class is alive when any of
        // its extension methods is.
        foreach (var member in staticType.GetMembers())
        {
            if (member is not IMethodSymbol { IsExtensionMethod: true } extensionMethod)
            {
                continue;
            }

            var candidateDocuments = usageIndex.GetDocumentsUsingName(extensionMethod.Name);
            if (candidateDocuments.Count > 0
                && await SymbolReferenceQuery.HasAnyReferenceAsync(extensionMethod, solution, candidateDocuments, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> HasUsedInterfaceContractAsync(
        IMethodSymbol method,
        Solution solution,
        SolutionUsageIndex usageIndex,
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
                var candidateDocuments = usageIndex.GetDocumentsUsingName(interfaceMember.Name);
                if (candidateDocuments.Count > 0
                    && await SymbolReferenceQuery.HasAnyReferenceAsync(interfaceMember, solution, candidateDocuments, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
