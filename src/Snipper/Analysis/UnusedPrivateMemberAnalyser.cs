namespace Snipper.Analysis;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Models;

public sealed class UnusedPrivateMemberAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0001"];

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

        // Binding is deliberately sequential: workspace compilations are built with
        // ConcurrentBuild=false, so concurrent semantic binding is unsupported and
        // silently loses symbol information. Parallelism lives in the syntax-only
        // SolutionUsageIndex; reference searches stay sequential but are restricted
        // to the handful of documents that textually contain the member name.
        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation)
            {
                continue;
            }

            progress?.Invoke($"UnusedPrivateMemberAnalyser: scanning {project.Name}");

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

                    // Syntax-level gate: only declaration nodes that can be private reach
                    // the semantic model. This avoids a GetDeclaredSymbol call per node.
                    if (!IsPotentiallyPrivateDeclaration(node))
                    {
                        continue;
                    }

                    var declaredSymbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
                    if (declaredSymbol is null || declaredSymbol.DeclaredAccessibility != Accessibility.Private)
                    {
                        continue;
                    }

                    if (declaredSymbol is not (IMethodSymbol or IPropertySymbol or IFieldSymbol))
                    {
                        continue;
                    }

                    // Explicit interface implementations are Private accessibility but
                    // are dispatched through the interface contract (e.g. protobuf
                    // IMessage.Descriptor, GraphQL JsonTypeInfo) — never unused.
                    if (declaredSymbol is IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 }
                        || declaredSymbol is IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 })
                    {
                        continue;
                    }

                    // Local functions declared after an unconditional exit (the common
                    // "helpers at the bottom of the method" pattern) are hoisted
                    // declarations, not dead members.
                    if (declaredSymbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction } localFunction
                        && IsDeclaredAtUnreachablePoint(localFunction, semanticModel, cancellationToken))
                    {
                        continue;
                    }

                    if (ExclusionEngine.ShouldExclude(declaredSymbol))
                    {
                        continue;
                    }

                    if (ExclusionEngine.IsNamespaceExcluded(declaredSymbol, _exclusions))
                    {
                        continue;
                    }

                    // Private members carrying serialization attributes ([JsonInclude]
                    // private setters, [JsonConstructor], …) are framework-invoked —
                    // skip the reference search.
                    if (FrameworkEvidenceIndex.HasMemberSerializationAttribute(declaredSymbol))
                    {
                        continue;
                    }

                    // The usage index restricts the search to documents that textually
                    // contain the member name; an empty set proves the member is unused
                    // with no semantic search at all. Indexers have no usable source
                    // name (consumed via this[...]) — search the whole project (rare).
                    var candidateDocuments = declaredSymbol is IPropertySymbol { IsIndexer: true }
                        ? project.Documents.ToImmutableHashSet()
                        : usageIndex.GetDocumentsUsingName(project, declaredSymbol.Name);

                    var hasReference = candidateDocuments.Count > 0
                        && await SymbolReferenceQuery.HasAnyReferenceAsync(declaredSymbol, solution, candidateDocuments, cancellationToken).ConfigureAwait(false);

                    if (!hasReference)
                    {
                        var lineSpan = declaredSymbol.Locations[0].GetLineSpan();
                        findings.Add(new SnipperFinding(
                            RuleId: "SNP0001",
                            Title: "Unused Private Member",
                            Message: $"Private member '{declaredSymbol.Name}' is declared but never referenced.",
                            Certainty: CertaintyTier.Guaranteed,
                            Category: FindingCategory.UnusedPrivateMember,
                            FilePath: lineSpan.Path ?? string.Empty,
                            LineNumber: lineSpan.StartLinePosition.Line + 1,
                            CharacterOffset: lineSpan.StartLinePosition.Character + 1,
                            Symbol: declaredSymbol));
                    }
                }
            }
        }

        return findings;
    }

    private static bool IsDeclaredAtUnreachablePoint(
        IMethodSymbol localFunction,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var declaration = localFunction.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken);
        if (declaration is not LocalFunctionStatementSyntax localFunctionStatement)
        {
            return false;
        }

        var flow = semanticModel.AnalyzeControlFlow(localFunctionStatement);
        return flow is { StartPointIsReachable: false };
    }

    private static bool IsPotentiallyPrivateDeclaration(SyntaxNode node)
    {
        return node switch
        {
            LocalFunctionStatementSyntax => true,
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: FieldDeclarationSyntax field } }
                => HasPrivateAccessibility(field),
            MemberDeclarationSyntax member when member is not FieldDeclarationSyntax
                => HasPrivateAccessibility(member),
            _ => false,
        };
    }

    private static bool HasPrivateAccessibility(MemberDeclarationSyntax member)
    {
        foreach (var modifier in member.Modifiers)
        {
            if (modifier.IsKind(SyntaxKind.PrivateKeyword))
            {
                return true;
            }

            if (modifier.IsKind(SyntaxKind.PublicKeyword)
                || modifier.IsKind(SyntaxKind.InternalKeyword)
                || modifier.IsKind(SyntaxKind.ProtectedKeyword))
            {
                return false;
            }
        }

        // No explicit modifier: members of classes/structs default to private;
        // interface members and top-level types do not.
        return member.Parent is TypeDeclarationSyntax type
            && !type.IsKind(SyntaxKind.InterfaceDeclaration);
    }
}
