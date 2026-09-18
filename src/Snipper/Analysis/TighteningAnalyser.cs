namespace Snipper.Analysis;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Models;

/// <summary>
/// SNP0024 — Tightening invitations (CA1822 / IDE0044 / CA1852 parity): one
/// analyser, three sub-checks, flat Advisory — these are refactor invitations,
/// not dead code, and snipper.json can promote the severity per rule. (1) An
/// instance method that binds to no instance state of its containing hierarchy
/// can be static; any attribute on the method excludes it (fixed-signature
/// callbacks and hooks), as do virtuals, overrides, and interface
/// implementations. (2) A field whose only writes happen in the matching
/// constructor (and which is never passed by ref or compound-assigned) can be
/// readonly; the read requirement keeps zero-read fields with
/// SNP0001/SNP0021. (3) An internal class with no derived types in the
/// <see cref="InheritanceGraph"/> can be sealed; zero-reference classes stay
/// with SNP0005.
/// </summary>
public sealed class TighteningAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0024"];

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
        var graph = InheritanceGraph.Get(solution);

        // Sequential binding per the workspace's ConcurrentBuild=false contract.
        // Syntax gates run first everywhere: modifier/attribute checks and the
        // this/base token scan happen before any semantic binding.
        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation)
            {
                continue;
            }

            progress?.Invoke($"TighteningAnalyser: scanning {project.Name}");

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

                    switch (node)
                    {
                        case MethodDeclarationSyntax methodDeclaration:
                            if (await TryEvaluateCanBeStaticAsync(
                                    methodDeclaration,
                                    semanticModel,
                                    project,
                                    solution,
                                    usageIndex,
                                    cancellationToken).ConfigureAwait(false) is { } staticFinding)
                            {
                                findings.Add(staticFinding);
                            }

                            break;

                        case ClassDeclarationSyntax classDeclaration:
                            if (await TryEvaluateCanBeSealedAsync(
                                    classDeclaration,
                                    semanticModel,
                                    project,
                                    solution,
                                    usageIndex,
                                    graph,
                                    hasFriendAssemblies,
                                    cancellationToken).ConfigureAwait(false) is { } sealedFinding)
                            {
                                findings.Add(sealedFinding);
                            }

                            break;

                        case VariableDeclaratorSyntax
                        {
                            Parent: VariableDeclarationSyntax { Parent: FieldDeclarationSyntax fieldDeclaration },
                        }:
                            if (await TryEvaluateCanBeReadonlyAsync(
                                    node,
                                    fieldDeclaration,
                                    semanticModel,
                                    project,
                                    solution,
                                    usageIndex,
                                    cancellationToken).ConfigureAwait(false) is { } readonlyFinding)
                            {
                                findings.Add(readonlyFinding);
                            }

                            break;
                    }
                }
            }
        }

        return findings;
    }

    private async Task<SnipperFinding?> TryEvaluateCanBeStaticAsync(
        MethodDeclarationSyntax methodDeclaration,
        SemanticModel semanticModel,
        Project project,
        Solution solution,
        SolutionUsageIndex usageIndex,
        CancellationToken cancellationToken)
    {
        // Syntax gates: instance methods only (no static/abstract/virtual/
        // override/partial), no attributes (fixed-signature hooks), a body to
        // inspect, and no this/base tokens — the cheapest rejection first.
        if (methodDeclaration.Modifiers.Any(static m => m.IsKind(SyntaxKind.StaticKeyword)
                || m.IsKind(SyntaxKind.AbstractKeyword)
                || m.IsKind(SyntaxKind.VirtualKeyword)
                || m.IsKind(SyntaxKind.OverrideKeyword)
                || m.IsKind(SyntaxKind.PartialKeyword))
            || methodDeclaration.AttributeLists.Count > 0)
        {
            return null;
        }

        var body = (SyntaxNode?)methodDeclaration.Body ?? methodDeclaration.ExpressionBody;
        if (body is null || ContainsThisOrBase(body))
        {
            return null;
        }

        if (semanticModel.GetDeclaredSymbol(methodDeclaration, cancellationToken) is not { } method
            || method.MethodKind is not MethodKind.Ordinary
            || method.IsStatic
            || method.ContainingType?.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return null;
        }

        if (ExclusionEngine.ShouldExclude(method)
            || ExclusionEngine.IsNamespaceExcluded(method, _exclusions)
            || InterfaceImplementationQuery.IsInterfaceImplementation(method))
        {
            return null;
        }

        // Usage gate: a method nothing calls belongs to SNP0001/0005/0006 —
        // tightening it would double-report their finding.
        var candidateDocuments = method.DeclaredAccessibility == Accessibility.Private
            ? usageIndex.GetDocumentsUsingName(project, method.Name)
            : usageIndex.GetDocumentsUsingName(method.Name);
        if (candidateDocuments.Count == 0
            || !await SymbolReferenceQuery.HasAnyReferenceAsync(method, solution, candidateDocuments, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // Instance-state proof: every name in the body must resolve to something
        // other than an instance member of the containing hierarchy. Unbound
        // names (discards, dynamic member names) cannot be instance members, so
        // they prove nothing either way.
        foreach (var name in body.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var symbol = semanticModel.GetSymbolInfo(name, cancellationToken).Symbol;
            if (symbol is IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol
                && !symbol.IsStatic
                && IsInHierarchy(symbol.ContainingType, method.ContainingType))
            {
                return null;
            }
        }

        return CreateFinding(
            method,
            "Member Can Be Static",
            $"Method '{method.Name}' does not use instance state and can be static.");
    }

    private async Task<SnipperFinding?> TryEvaluateCanBeReadonlyAsync(
        SyntaxNode declaratorNode,
        FieldDeclarationSyntax fieldDeclaration,
        SemanticModel semanticModel,
        Project project,
        Solution solution,
        SolutionUsageIndex usageIndex,
        CancellationToken cancellationToken)
    {
        // Syntax gate: readonly/const/volatile are settled one way or the other.
        if (fieldDeclaration.Modifiers.Any(static m => m.IsKind(SyntaxKind.ReadOnlyKeyword)
            || m.IsKind(SyntaxKind.ConstKeyword)
            || m.IsKind(SyntaxKind.VolatileKeyword)))
        {
            return null;
        }

        if (semanticModel.GetDeclaredSymbol(declaratorNode, cancellationToken) is not IFieldSymbol field
            || field.IsConst
            || field.IsReadOnly
            || field.IsVolatile
            || field.IsFixedSizeBuffer
            || field.IsRequired
            || field.RefKind != RefKind.None
            || field.IsImplicitlyDeclared
            || field.ContainingType is not { } containingType
            || containingType.IsRefLikeType)
        {
            return null;
        }

        if (ExclusionEngine.ShouldExclude(field) || ExclusionEngine.IsNamespaceExcluded(field, _exclusions))
        {
            return null;
        }

        // Private fields are confined to their containing type's project;
        // everything else searches solution-wide.
        var candidateDocuments = field.DeclaredAccessibility == Accessibility.Private
            ? usageIndex.GetDocumentsUsingName(project, field.Name)
            : usageIndex.GetDocumentsUsingName(field.Name);
        if (candidateDocuments.Count == 0)
        {
            return null;
        }

        var references = await FieldReferenceMap.GetReferencesAsync(field, solution, candidateDocuments, cancellationToken).ConfigureAwait(false);
        var semanticModels = new Dictionary<SyntaxTree, SemanticModel>();
        var sawRead = false;

        foreach (var reference in references)
        {
            // Unconfirmed name matches are unknown evidence — never flag.
            if (reference.IsCandidate)
            {
                return null;
            }

            var kind = reference.Node is null
                ? FieldReferenceKind.Read
                : FieldReferenceClassifier.Classify(reference.Node);

            switch (kind)
            {
                case FieldReferenceKind.Read:
                    sawRead = true;
                    break;
                case FieldReferenceKind.None:
                    break;
                case FieldReferenceKind.ReadWrite:
                    // ref aliasing and compound assignment read the old value —
                    // readonly forbids the former and the latter stays a write.
                    return null;
                case FieldReferenceKind.Write:
                    if (!await IsWriteInsideMatchingConstructorAsync(reference.Node!, solution, containingType, field.IsStatic, semanticModels, cancellationToken).ConfigureAwait(false))
                    {
                        return null;
                    }

                    break;
            }
        }

        // A field nothing reads belongs to SNP0001/SNP0021 — never double-report.
        if (!sawRead)
        {
            return null;
        }

        return CreateFinding(
            field,
            "Field Can Be Read-Only",
            $"Field '{field.Name}' is assigned only during construction and can be readonly.");
    }

    private async Task<SnipperFinding?> TryEvaluateCanBeSealedAsync(
        ClassDeclarationSyntax classDeclaration,
        SemanticModel semanticModel,
        Project project,
        Solution solution,
        SolutionUsageIndex usageIndex,
        InheritanceGraph graph,
        bool hasFriendAssemblies,
        CancellationToken cancellationToken)
    {
        if (classDeclaration.Modifiers.Any(static m => m.IsKind(SyntaxKind.StaticKeyword)
            || m.IsKind(SyntaxKind.AbstractKeyword)
            || m.IsKind(SyntaxKind.SealedKeyword)))
        {
            return null;
        }

        if (semanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken) is not { } classSymbol
            || classSymbol.TypeKind is not TypeKind.Class
            || classSymbol.DeclaredAccessibility is not Accessibility.Internal
            || classSymbol.IsAbstract
            || classSymbol.IsSealed
            || classSymbol.IsImplicitlyDeclared)
        {
            return null;
        }

        if (ExclusionEngine.ShouldExclude(classSymbol)
            || ExclusionEngine.IsNamespaceExcluded(classSymbol, _exclusions)
            || graph.HasAnyDerivedClass(classSymbol))
        {
            return null;
        }

        // Usage gate: an unreferenced class is plain dead code (SNP0005), not a
        // tightening invitation. Friend assemblies widen the search to the
        // whole solution.
        var candidateDocuments = hasFriendAssemblies
            ? usageIndex.GetDocumentsUsingName(classSymbol.Name)
            : usageIndex.GetDocumentsUsingName(project, classSymbol.Name);
        if (candidateDocuments.Count == 0
            || !await SymbolReferenceQuery.HasAnyReferenceAsync(classSymbol, solution, candidateDocuments, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return CreateFinding(
            classSymbol,
            "Class Can Be Sealed",
            $"Internal class '{classSymbol.Name}' has no derived types and can be sealed.");
    }

    private static async Task<bool> IsWriteInsideMatchingConstructorAsync(
        SimpleNameSyntax node,
        Solution solution,
        INamedTypeSymbol containingType,
        bool isStaticField,
        Dictionary<SyntaxTree, SemanticModel> semanticModels,
        CancellationToken cancellationToken)
    {
        var constructor = node.FirstAncestorOrSelf<ConstructorDeclarationSyntax>();
        if (constructor is null)
        {
            return false;
        }

        if (!semanticModels.TryGetValue(constructor.SyntaxTree, out var model))
        {
            var document = solution.GetDocument(constructor.SyntaxTree);
            model = document is null ? null : await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (model is null)
            {
                return false;
            }

            semanticModels[constructor.SyntaxTree] = model;
        }

        return model.GetDeclaredSymbol(constructor, cancellationToken) is { } constructorSymbol
            && constructorSymbol.IsStatic == isStaticField
            && SymbolEqualityComparer.Default.Equals(constructorSymbol.ContainingType, containingType);
    }

    private static bool ContainsThisOrBase(SyntaxNode body)
    {
        return body.DescendantNodesAndSelf().Any(static n => n is ThisExpressionSyntax or BaseExpressionSyntax);
    }

    private static bool IsInHierarchy(INamedTypeSymbol? memberContainingType, INamedTypeSymbol containingType)
    {
        for (var current = containingType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, memberContainingType?.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }

    private static SnipperFinding CreateFinding(ISymbol symbol, string title, string message)
    {
        var lineSpan = symbol.Locations[0].GetLineSpan();
        return new SnipperFinding(
            RuleId: "SNP0024",
            Title: title,
            Message: message,
            Certainty: CertaintyTier.Advisory,
            Category: FindingCategory.Tightening,
            FilePath: lineSpan.Path ?? string.Empty,
            LineNumber: lineSpan.StartLinePosition.Line + 1,
            CharacterOffset: lineSpan.StartLinePosition.Character + 1,
            Symbol: symbol);
    }
}
