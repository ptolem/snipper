namespace Snipper.Analysis;

using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snipper.Models;

/// <summary>
/// SNP0024 — Tightening invitations (CA1822 / IDE0044 / CA1852 parity): one
/// analyser, four sub-checks, flat Advisory — these are refactor invitations,
/// not dead code, and snipper.json can promote the severity per rule. (1) An
/// instance method that binds to no instance state of its containing hierarchy
/// can be static; any attribute on the method excludes it (fixed-signature
/// callbacks and hooks), as do virtuals, overrides, and interface
/// implementations. (2) A field whose only writes happen in the matching
/// constructor (and which is never passed by ref or compound-assigned) can be
/// readonly; the read requirement keeps zero-read fields with
/// SNP0001/SNP0021. (3) An internal class with no derived types in the
/// <see cref="InheritanceGraph"/> can be sealed; zero-reference classes stay
/// with SNP0005. (4) A public/internal member on a non-exported type whose
/// every confirmed reference lands inside its containing type's declaration
/// span(s) can be private; virtuals, attributed members, contract
/// implementations, and framework-evidence members are excluded, and the
/// ≥1-reference usage gate keeps unused members with SNP0001/0005/0006.
/// </summary>
public sealed class TighteningAnalyser : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0024"];

    public SharedIndex SharedIndexes => SharedIndex.AnalysisScope | SharedIndex.SolutionUsageIndex | SharedIndex.InheritanceGraph | SharedIndex.FrameworkEvidenceIndex;

    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var findings = new ConcurrentBag<SnipperFinding>();
        var referenceSearch = await ReferenceSearch.GetAsync(solution, cancellationToken).ConfigureAwait(false);
        var graph = await InheritanceGraph.GetAsync(solution, cancellationToken).ConfigureAwait(false);
        var frameworkEvidence = await FrameworkEvidenceIndex.GetAsync(solution, cancellationToken).ConfigureAwait(false);

        // Syntax gates run first everywhere: modifier/attribute checks and the
        // this/base token scan happen before any semantic binding.
        var documents = await AnalysisScope.GetAsync(solution, cancellationToken).ConfigureAwait(false);
        progress?.Invoke($"TighteningAnalyser: scanning {documents.Count} documents");
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

                    switch (node)
                    {
                        case MethodDeclarationSyntax methodDeclaration:
                            if (await TryEvaluateCanBeStaticAsync(
                                    methodDeclaration,
                                    semanticModel,
                                    referenceSearch,
                                    documentToken).ConfigureAwait(false) is { } staticFinding)
                            {
                                findings.Add(staticFinding);
                            }

                            if (HasPublicOrInternalModifier(methodDeclaration.Modifiers)
                                && semanticModel.GetDeclaredSymbol(methodDeclaration, documentToken) is { } methodSymbol
                                && await TryEvaluateCanBePrivateAsync(
                                    methodSymbol,
                                    referenceSearch,
                                    frameworkEvidence,
                                    documentToken).ConfigureAwait(false) is { } methodPrivateFinding)
                            {
                                findings.Add(methodPrivateFinding);
                            }

                            break;

                        case PropertyDeclarationSyntax propertyDeclaration:
                            if (HasPublicOrInternalModifier(propertyDeclaration.Modifiers)
                                && semanticModel.GetDeclaredSymbol(propertyDeclaration, documentToken) is { } propertySymbol
                                && await TryEvaluateCanBePrivateAsync(
                                    propertySymbol,
                                    referenceSearch,
                                    frameworkEvidence,
                                    documentToken).ConfigureAwait(false) is { } propertyPrivateFinding)
                            {
                                findings.Add(propertyPrivateFinding);
                            }

                            break;

                        case ClassDeclarationSyntax classDeclaration:
                            if (await TryEvaluateCanBeSealedAsync(
                                    classDeclaration,
                                    semanticModel,
                                    referenceSearch,
                                    graph,
                                    documentToken).ConfigureAwait(false) is { } sealedFinding)
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
                                    solution,
                                    referenceSearch,
                                    documentToken).ConfigureAwait(false) is { } readonlyFinding)
                            {
                                findings.Add(readonlyFinding);
                            }

                            if (HasPublicOrInternalModifier(fieldDeclaration.Modifiers)
                                && semanticModel.GetDeclaredSymbol(node, documentToken) is IFieldSymbol fieldSymbol
                                && await TryEvaluateCanBePrivateAsync(
                                    fieldSymbol,
                                    referenceSearch,
                                    frameworkEvidence,
                                    documentToken).ConfigureAwait(false) is { } fieldPrivateFinding)
                            {
                                findings.Add(fieldPrivateFinding);
                            }

                            break;
                    }
                }
            }).ConfigureAwait(false);

        return [.. findings];
    }

    private static async Task<SnipperFinding?> TryEvaluateCanBeStaticAsync(
        MethodDeclarationSyntax methodDeclaration,
        SemanticModel semanticModel,
        ReferenceSearch referenceSearch,
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
            || InterfaceImplementationQuery.IsInterfaceImplementation(method))
        {
            return null;
        }

        // Usage gate: a method nothing calls belongs to SNP0001/0005/0006 —
        // tightening it would double-report their finding.
        if (!await referenceSearch.HasAnyAsync(method, cancellationToken).ConfigureAwait(false))
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

            // A primary-constructor parameter read inside an instance method is
            // captured instance state, but it binds to an IParameterSymbol owned
            // by the type's constructor — none of the member kinds below — so the
            // hierarchy walk never saw it and the method was proposed as static
            // when making it static would not compile. Only a *constructor*-owned
            // parameter counts: the method's own parameters are parameters too,
            // and a method using only those is genuinely static (see ParamsOnly).
            if (symbol is IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Constructor } capturedBy }
                && IsInHierarchy(capturedBy.ContainingType, method.ContainingType))
            {
                return null;
            }

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

    private static async Task<SnipperFinding?> TryEvaluateCanBeReadonlyAsync(
        SyntaxNode declaratorNode,
        FieldDeclarationSyntax fieldDeclaration,
        SemanticModel semanticModel,
        Solution solution,
        ReferenceSearch referenceSearch,
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

        if (ExclusionEngine.ShouldExclude(field))
        {
            return null;
        }

        var references = await FieldReferenceMap.GetReferencesAsync(field, referenceSearch, cancellationToken).ConfigureAwait(false);
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

    private static async Task<SnipperFinding?> TryEvaluateCanBeSealedAsync(
        ClassDeclarationSyntax classDeclaration,
        SemanticModel semanticModel,
        ReferenceSearch referenceSearch,
        InheritanceGraph graph,
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
            || graph.HasAnyDerivedClass(classSymbol))
        {
            return null;
        }

        // Usage gate: an unreferenced class is plain dead code (SNP0005), not a
        // tightening invitation.
        if (!await referenceSearch.HasAnyAsync(classSymbol, cancellationToken).ConfigureAwait(false))
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

    private static async Task<SnipperFinding?> TryEvaluateCanBePrivateAsync(
        ISymbol member,
        ReferenceSearch referenceSearch,
        FrameworkEvidenceIndex frameworkEvidence,
        CancellationToken cancellationToken)
    {
        // Symbol gates: public/internal members on non-exported types only.
        // Virtuals (extensibility contract), attributed members (hooks),
        // indexers, non-ordinary methods, and implicit declarations are out;
        // a public containing type is exported API surface (same boundary as
        // can-be-sealed).
        if (member.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal)
            || member.IsOverride
            || member.IsImplicitlyDeclared
            || member is IMethodSymbol { MethodKind: not MethodKind.Ordinary }
            || member is IPropertySymbol { IsIndexer: true }
            || member.GetAttributes().Length > 0
            || member.ContainingType is not { } containingType
            || containingType.TypeKind is not (TypeKind.Class or TypeKind.Struct)
            || containingType.DeclaredAccessibility is Accessibility.Public)
        {
            return null;
        }

        if (member is IMethodSymbol { IsVirtual: true } or IPropertySymbol { IsVirtual: true })
        {
            return null;
        }

        if (ExclusionEngine.ShouldExclude(member)
            || InterfaceImplementationQuery.IsInterfaceImplementation(member)
            || frameworkEvidence.IsUsed(member))
        {
            return null;
        }

        // Usage gate: a member nothing references belongs to SNP0001/0005/0006.
        var references = await referenceSearch.FindAsync(member, cancellationToken).ConfigureAwait(false);
        if (references.IsEmpty)
        {
            return null;
        }

        // Locality proof: every confirmed reference must land inside the
        // containing type's declaration span(s) (partial types included).
        // Candidate (unconfirmed) locations suppress — unknown evidence is
        // never a tightening invitation. Nested types are conservative:
        // references from an enclosing type read as external and suppress,
        // even though private would remain legal there.
        var containingSpans = GetContainingTypeSpans(containingType, cancellationToken);
        foreach (var location in references)
        {
            // Unconfirmed (candidate) locations are unknown evidence —
            // never a tightening invitation.
            if (location.IsCandidateLocation)
            {
                return null;
            }

            if (!IsInsideAnySpan(location.Location, containingSpans))
            {
                return null;
            }
        }

        return CreateFinding(
            member,
            "Member Can Be Private",
            $"Member '{member.Name}' is referenced only within its containing type and can be private.");
    }

    private static bool HasPublicOrInternalModifier(SyntaxTokenList modifiers)
    {
        return modifiers.Any(static m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.InternalKeyword));
    }

    private static List<(SyntaxTree Tree, TextSpan Span)> GetContainingTypeSpans(
        INamedTypeSymbol containingType,
        CancellationToken cancellationToken)
    {
        var spans = new List<(SyntaxTree, TextSpan)>();
        foreach (var reference in containingType.DeclaringSyntaxReferences)
        {
            spans.Add((reference.SyntaxTree, reference.GetSyntax(cancellationToken).Span));
        }

        return spans;
    }

    private static bool IsInsideAnySpan(Location location, List<(SyntaxTree Tree, TextSpan Span)> spans)
    {
        foreach (var (tree, span) in spans)
        {
            if (location.SourceTree == tree && span.Contains(location.SourceSpan))
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
            CharacterOffset: lineSpan.StartLinePosition.Character + 1);
    }
}
