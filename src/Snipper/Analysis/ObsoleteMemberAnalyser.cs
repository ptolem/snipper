namespace Snipper.Analysis;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Models;

/// <summary>
/// SNP0018 — Flags types and members carrying <c>[Obsolete]</c> that nothing references.
/// Existing member rules skip obsolete symbols by design (ExclusionEngine); this rule
/// completes the deprecation cycle by reporting them as removal candidates instead.
/// Tier: High for non-public members and <c>[Obsolete(error: true)]</c>; Moderate for
/// public members (external consumers may exist — same closed-world stance as SNP0006).
/// Skips overrides, interface implementations (polymorphic dispatch defeats static
/// zero-reference reasoning), constructors/accessors/operators, and symbols the
/// exclusion engine protects for other reasons (test classes, UsedImplicitly, ...).
/// Known limitation: references arriving only from other obsolete symbols still count
/// as references — mutually-referential dead islands are SNP0017 territory.
/// </summary>
public sealed class ObsoleteMemberAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser
{
    private const string ObsoleteAttributeMetadataName = "System.ObsoleteAttribute";

    private readonly AnalysisExclusions _exclusions = exclusions ?? AnalysisExclusions.None;

    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var findings = new List<SnipperFinding>();
        var analysisRoots = ExclusionEngine.GetAnalysisRootDirectories(solution);
        var usageIndex = SolutionUsageIndex.Get(solution);

        // Sequential binding — workspace compilations are built with ConcurrentBuild=false.
        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation)
            {
                continue;
            }

            progress?.Invoke($"ObsoleteMemberAnalyser: scanning {project.Name}");

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

                    // Syntax gate: only attributes spelled *Obsolete* reach the semantic model.
                    if (node is not AttributeSyntax attribute || !IsObsoleteAttributeName(attribute.Name))
                    {
                        continue;
                    }

                    // AttributeListSyntax → attributed declaration.
                    var declaration = attribute.Parent?.Parent;
                    if (declaration is null)
                    {
                        continue;
                    }

                    // Field/event-field declarations bind through their variable declarators.
                    if (declaration is BaseFieldDeclarationSyntax fieldDeclaration)
                    {
                        foreach (var variable in fieldDeclaration.Declaration.Variables)
                        {
                            if (semanticModel.GetDeclaredSymbol(variable, cancellationToken) is { } fieldSymbol)
                            {
                                await EvaluateCandidateAsync(fieldSymbol, attribute, project, solution, usageIndex, findings, cancellationToken).ConfigureAwait(false);
                            }
                        }

                        continue;
                    }

                    if (semanticModel.GetDeclaredSymbol(declaration, cancellationToken) is { } declaredSymbol)
                    {
                        await EvaluateCandidateAsync(declaredSymbol, attribute, project, solution, usageIndex, findings, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        return findings;
    }

    private async Task EvaluateCandidateAsync(
        ISymbol symbol,
        AttributeSyntax attribute,
        Project project,
        Solution solution,
        SolutionUsageIndex usageIndex,
        List<SnipperFinding> findings,
        CancellationToken cancellationToken)
    {
        if (!IsEligibleKind(symbol))
        {
            return;
        }

        // Confirm the attribute is really System.ObsoleteAttribute (a custom attribute
        // named "Obsolete" must not produce findings) and capture the error flag.
        AttributeData? obsoleteAttribute = null;
        foreach (var attributeData in symbol.GetAttributes())
        {
            if (attributeData.AttributeClass?.ToDisplayString() == ObsoleteAttributeMetadataName)
            {
                obsoleteAttribute = attributeData;
                break;
            }
        }

        if (obsoleteAttribute is null || symbol.IsOverride || IsInterfaceImplementation(symbol))
        {
            return;
        }

        if (ExclusionEngine.ShouldExcludeIgnoringObsolete(symbol) || ExclusionEngine.IsNamespaceExcluded(symbol, _exclusions))
        {
            return;
        }

        // Solution-wide candidate set: callers of public/internal obsolete members
        // routinely live in other projects (private members simply match nothing
        // outside their own). Indexers have no usable source name (consumed via
        // this[...]) — search the whole project (rare), as SNP0001 does.
        var candidateDocuments = symbol is IPropertySymbol { IsIndexer: true }
            ? project.Documents.ToImmutableHashSet()
            : usageIndex.GetDocumentsUsingName(symbol.Name);

        var hasReference = candidateDocuments.Count > 0
            && await SymbolReferenceQuery.HasAnyReferenceAsync(symbol, solution, candidateDocuments, cancellationToken).ConfigureAwait(false);
        if (hasReference)
        {
            return;
        }

        var isError = obsoleteAttribute.ConstructorArguments.Length >= 2
            && obsoleteAttribute.ConstructorArguments[1].Value is true;
        var certainty = isError || symbol.DeclaredAccessibility != Accessibility.Public
            ? CertaintyTier.High
            : CertaintyTier.Moderate;

        var lineSpan = attribute.GetLocation().GetLineSpan();
        findings.Add(new SnipperFinding(
            RuleId: "SNP0018",
            Title: "Obsolete Unreferenced Member",
            Message: $"Obsolete {(symbol is INamedTypeSymbol ? "type" : "member")} '{symbol.Name}' is marked for removal and has no references.",
            Certainty: certainty,
            Category: FindingCategory.ObsoleteUnreferencedMember,
            FilePath: lineSpan.Path ?? string.Empty,
            LineNumber: lineSpan.StartLinePosition.Line + 1,
            CharacterOffset: lineSpan.StartLinePosition.Character + 1,
            Symbol: symbol));
    }

    private static bool IsEligibleKind(ISymbol symbol)
    {
        return symbol switch
        {
            INamedTypeSymbol => true,
            IMethodSymbol { MethodKind: MethodKind.Ordinary } => true,
            IPropertySymbol => true,
            IFieldSymbol => true,
            IEventSymbol => true,
            _ => false,
        };
    }

    private static bool IsInterfaceImplementation(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 }
            || symbol is IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 }
            || symbol is IEventSymbol { ExplicitInterfaceImplementations.Length: > 0 })
        {
            return true;
        }

        if (symbol is not (IMethodSymbol or IPropertySymbol or IEventSymbol) || symbol.ContainingType is not { } containingType)
        {
            return false;
        }

        foreach (var contract in containingType.AllInterfaces)
        {
            foreach (var member in contract.GetMembers())
            {
                var implementation = containingType.FindImplementationForInterfaceMember(member);
                if (implementation is not null && SymbolEqualityComparer.Default.Equals(implementation, symbol))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsObsoleteAttributeName(NameSyntax name)
    {
        var identifier = name switch
        {
            IdentifierNameSyntax identifierName => identifierName.Identifier.Text,
            QualifiedNameSyntax qualifiedName => qualifiedName.Right.Identifier.Text,
            AliasQualifiedNameSyntax aliasQualifiedName => aliasQualifiedName.Name.Identifier.Text,
            _ => string.Empty,
        };

        return identifier is "Obsolete" or "ObsoleteAttribute";
    }
}
