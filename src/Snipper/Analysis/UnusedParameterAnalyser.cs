namespace Snipper.Analysis;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Snipper.Models;

/// <summary>
/// SNP0010 — Flags parameters of private methods and local functions that are
/// never used. Only methods referenced exclusively through direct invocations are
/// eligible: a method referenced as a method group (events, delegates, function
/// pointers) has its signature fixed by the target delegate type, and partial,
/// extern, and explicit-interface-implementation signatures are contractual too.
/// Tier 3 (Moderate).
/// </summary>
public sealed class UnusedParameterAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser
{
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

        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation)
            {
                continue;
            }

            progress?.Invoke($"UnusedParameterAnalyser: scanning {project.Name}");

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

                    // Syntax gate: only methods/local functions with a parameter list
                    // and no non-private modifier reach the semantic model.
                    var parameterList = node switch
                    {
                        MethodDeclarationSyntax methodDeclaration when !HasNonPrivateModifier(methodDeclaration.Modifiers) => methodDeclaration.ParameterList,
                        LocalFunctionStatementSyntax localFunction => localFunction.ParameterList,
                        _ => null,
                    };

                    if (parameterList is null || parameterList.Parameters.Count == 0)
                    {
                        continue;
                    }

                    var symbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
                    if (symbol is not IMethodSymbol method || !IsEligibleMethod(method))
                    {
                        continue;
                    }

                    if (ExclusionEngine.ShouldExclude(method))
                    {
                        continue;
                    }

                    if (ExclusionEngine.IsNamespaceExcluded(method, _exclusions))
                    {
                        continue;
                    }

                    // The method must be referenced somewhere (an entirely unreferenced
                    // method is SNP0001's finding, not this rule's), and every reference
                    // must be a direct invocation — a method-group reference fixes the
                    // signature through the delegate contract.
                    var methodDocuments = usageIndex.GetDocumentsUsingName(project, method.Name);
                    if (methodDocuments.Count == 0
                        || !await IsExclusivelyDirectInvokedAsync(method, solution, methodDocuments, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    foreach (var parameterSyntax in parameterList.Parameters)
                    {
                        var parameterName = parameterSyntax.Identifier.Text;
                        if (parameterName.Length == 0 || parameterName is "_")
                        {
                            continue;
                        }

                        var parameter = semanticModel.GetDeclaredSymbol(parameterSyntax, cancellationToken);
                        if (parameter is null)
                        {
                            continue;
                        }

                        // Fast path: the name never appears in a usage position anywhere
                        // in the document — provably unread with no semantic search.
                        if (!usageIndex.IsNameUsedInDocument(document, parameterName))
                        {
                            findings.Add(CreateFinding(parameterName, method, parameterSyntax.GetLocation().GetLineSpan()));
                            continue;
                        }

                        // Slow path: the name appears (possibly on another symbol) —
                        // confirm semantically. A parameter's references can only live
                        // in this document.
                        var referenced = await SymbolReferenceQuery.HasAnyReferenceAsync(
                            parameter, solution, ImmutableHashSet.Create(document), cancellationToken).ConfigureAwait(false);
                        if (!referenced)
                        {
                            findings.Add(CreateFinding(parameterName, method, parameterSyntax.GetLocation().GetLineSpan()));
                        }
                    }
                }
            }
        }

        return findings;
    }

    private static bool IsEligibleMethod(IMethodSymbol method)
    {
        return method.MethodKind is MethodKind.Ordinary or MethodKind.LocalFunction
            && method.DeclaredAccessibility == Accessibility.Private
            && !method.IsOverride
            && !method.IsExtern
            && !method.IsExtensionMethod
            && method.ExplicitInterfaceImplementations.Length == 0
            && method.PartialDefinitionPart is null
            && method.PartialImplementationPart is null
            && method.Parameters.Length > 0;
    }

    private static bool HasNonPrivateModifier(SyntaxTokenList modifiers)
    {
        foreach (var modifier in modifiers)
        {
            if (modifier.IsKind(SyntaxKind.PublicKeyword)
                || modifier.IsKind(SyntaxKind.InternalKeyword)
                || modifier.IsKind(SyntaxKind.ProtectedKeyword))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> IsExclusivelyDirectInvokedAsync(
        IMethodSymbol method,
        Solution solution,
        IImmutableSet<Document> candidateDocuments,
        CancellationToken cancellationToken)
    {
        var references = await SymbolFinder.FindReferencesAsync(method, solution, candidateDocuments, cancellationToken).ConfigureAwait(false);
        var anyReference = false;

        foreach (var reference in references)
        {
            foreach (var location in reference.Locations)
            {
                anyReference = true;

                var root = await location.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var node = root?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                if (node is null || !IsDirectInvocation(node))
                {
                    return false;
                }
            }
        }

        return anyReference;
    }

    private static bool IsDirectInvocation(SyntaxNode node)
    {
        foreach (var ancestor in node.AncestorsAndSelf())
        {
            if (ancestor is InvocationExpressionSyntax invocation)
            {
                // The reference must be the invoked expression itself, not an argument
                // (e.g. nameof(M) or list.Select(M) are method-group usages).
                return invocation.Expression.Span.Contains(node.Span);
            }
        }

        return false;
    }

    private static SnipperFinding CreateFinding(string parameterName, IMethodSymbol method, FileLinePositionSpan lineSpan)
    {
        var kind = method.MethodKind is MethodKind.LocalFunction ? "local function" : "method";
        return new SnipperFinding(
            RuleId: "SNP0010",
            Title: "Unused Parameter",
            Message: $"Parameter '{parameterName}' on private {kind} '{method.Name}' is never used.",
            Certainty: CertaintyTier.Moderate,
            Category: FindingCategory.UnusedParameter,
            FilePath: lineSpan.Path ?? string.Empty,
            LineNumber: lineSpan.StartLinePosition.Line + 1,
            CharacterOffset: lineSpan.StartLinePosition.Character + 1,
            Symbol: null);
    }
}
