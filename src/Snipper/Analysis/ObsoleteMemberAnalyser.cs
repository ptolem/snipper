namespace Snipper.Analysis;

using System.Collections.Concurrent;
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
public sealed class ObsoleteMemberAnalyser : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0018"];

    public SharedIndex SharedIndexes => SharedIndex.AnalysisScope | SharedIndex.SolutionUsageIndex | SharedIndex.FrameworkEvidenceIndex | SharedIndex.EnumOrdinalContractIndex;

    private const string ObsoleteAttributeMetadataName = "System.ObsoleteAttribute";

    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var findings = new ConcurrentBag<SnipperFinding>();
        var referenceSearch = await ReferenceSearch.GetAsync(solution, cancellationToken).ConfigureAwait(false);
        var frameworkEvidence = await FrameworkEvidenceIndex.GetAsync(solution, cancellationToken).ConfigureAwait(false);
        var enumOrdinals = await EnumOrdinalContractIndex.GetAsync(solution, cancellationToken).ConfigureAwait(false);

        var documents = await AnalysisScope.GetAsync(solution, cancellationToken).ConfigureAwait(false);
        progress?.Invoke($"ObsoleteMemberAnalyser: scanning {documents.Count} documents");
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
                            if (semanticModel.GetDeclaredSymbol(variable, documentToken) is { } fieldSymbol)
                            {
                                await EvaluateCandidateAsync(fieldSymbol, attribute, referenceSearch, frameworkEvidence, enumOrdinals, findings, documentToken).ConfigureAwait(false);
                            }
                        }

                        continue;
                    }

                    if (semanticModel.GetDeclaredSymbol(declaration, documentToken) is { } declaredSymbol)
                    {
                        await EvaluateCandidateAsync(declaredSymbol, attribute, referenceSearch, frameworkEvidence, enumOrdinals, findings, documentToken).ConfigureAwait(false);
                    }
                }
            }).ConfigureAwait(false);

        return [.. findings];
    }

    private static async Task EvaluateCandidateAsync(
        ISymbol symbol,
        AttributeSyntax attribute,
        ReferenceSearch referenceSearch,
        FrameworkEvidenceIndex frameworkEvidence,
        EnumOrdinalContractIndex enumOrdinals,
        ConcurrentBag<SnipperFinding> findings,
        CancellationToken cancellationToken)
    {
        if (!IsEligibleKind(symbol))
        {
            return;
        }

        // Confirm the attribute is really System.ObsoleteAttribute (a custom attribute
        // named "Obsolete" must not produce findings) and capture the error flag.
        var obsoleteAttribute = FindObsoleteAttribute(symbol);

        if (obsoleteAttribute is null || symbol.IsOverride || InterfaceImplementationQuery.IsInterfaceImplementation(symbol))
        {
            return;
        }

        if (ExclusionEngine.ShouldExcludeIgnoringObsolete(symbol))
        {
            return;
        }

        if (await IsLiveContractAsync(symbol, referenceSearch, frameworkEvidence, enumOrdinals, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (await referenceSearch.HasAnyAsync(symbol, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var isEnumMember = symbol is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum };

        var lineSpan = attribute.GetLocation().GetLineSpan();
        findings.Add(new SnipperFinding(
            RuleId: "SNP0018",
            Title: "Obsolete Unreferenced Member",
            Message: MessageFor(symbol, isEnumMember),
            Certainty: CertaintyFor(symbol, obsoleteAttribute, isEnumMember),
            Category: FindingCategory.ObsoleteUnreferencedMember,
            FilePath: lineSpan.Path ?? string.Empty,
            LineNumber: lineSpan.StartLinePosition.Line + 1,
            CharacterOffset: lineSpan.StartLinePosition.Character + 1));
    }

    private static AttributeData? FindObsoleteAttribute(ISymbol symbol)
    {
        foreach (var attributeData in symbol.GetAttributes())
        {
            if (attributeData.AttributeClass?.ToDisplayString() == ObsoleteAttributeMetadataName)
            {
                return attributeData;
            }
        }

        return null;
    }

    /// <summary>
    /// An enum member is Advisory whatever its accessibility: "no references"
    /// never implies "safe to delete" when deleting renumbers every later member.
    /// Any other tier would read as a removal instruction for a one-line change
    /// that silently rewrites the wire contract.
    /// </summary>
    private static CertaintyTier CertaintyFor(ISymbol symbol, AttributeData obsoleteAttribute, bool isEnumMember)
    {
        if (isEnumMember)
        {
            return CertaintyTier.Advisory;
        }

        var isError = obsoleteAttribute.ConstructorArguments.Length >= 2
            && obsoleteAttribute.ConstructorArguments[1].Value is true;

        return isError || symbol.DeclaredAccessibility != Accessibility.Public
            ? CertaintyTier.High
            : CertaintyTier.Moderate;
    }

    private static string MessageFor(ISymbol symbol, bool isEnumMember)
    {
        if (isEnumMember)
        {
            return $"Obsolete enum member '{symbol.Name}' has no references. Removing it renumbers every later member of '{symbol.ContainingType.Name}', so any ordinal already on the wire or in a payload will then resolve to a different member - retire it by renaming or leaving a placeholder instead of deleting.";
        }

        var kind = symbol is INamedTypeSymbol ? "type" : "member";
        return $"Obsolete {kind} '{symbol.Name}' is marked for removal and has no references.";
    }

    /// <summary>
    /// True when zero C# references is not evidence of anything, because something
    /// other than a C# call is what reaches the member.
    ///
    /// Three distinct reasons, kept together because from here they are one
    /// question — but each answers it differently, and each was a separate false
    /// positive class on milkrun:
    ///
    /// <list type="bullet">
    /// <item>A serializer, model binder, or minimal-API route writes it. [Obsolete]
    /// stops none of them: registering a converter does not imply
    /// <c>IgnoreReadOnlyProperties</c>, so the property still ships. Reporting it
    /// as removable is advice to delete a live contract.</item>
    /// <item>Its ordinal is the wire value. An enum member's number travels in
    /// messages, and deleting it renumbers every later member, so the next
    /// in-flight message decodes as a <em>different</em> case — silently.</item>
    /// <item>An extension holder is invoked through its methods, never through its
    /// own name. Same rescue SNP0006 applies, so the two rules agree on whether a
    /// heavily used holder is alive.</item>
    /// </list>
    /// </summary>
    private static async Task<bool> IsLiveContractAsync(
        ISymbol symbol,
        ReferenceSearch referenceSearch,
        FrameworkEvidenceIndex frameworkEvidence,
        EnumOrdinalContractIndex enumOrdinals,
        CancellationToken cancellationToken)
    {
        if (frameworkEvidence.IsUsed(symbol) || enumOrdinals.IsOrdinalBound(symbol))
        {
            return true;
        }

        return symbol is INamedTypeSymbol { IsStatic: true } obsoleteStaticType
            && await ExtensionMethodUsageQuery.HasAnyUsedExtensionMethodAsync(obsoleteStaticType, referenceSearch, cancellationToken).ConfigureAwait(false);
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
