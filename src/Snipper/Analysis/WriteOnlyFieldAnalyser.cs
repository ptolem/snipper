namespace Snipper.Analysis;

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Models;

/// <summary>
/// SNP0021 — Flags private fields that are written but never read (IDE0052 parity).
/// Boundary with SNP0001: zero-reference fields stay with SNP0001 (a declarator
/// initializer is not a reference), so SNP0021 requires at least one write
/// reference. Compound assignment, ++/--, and ref count as reads because the old
/// value feeds the new one; out counts as a write. Serialization-attributed
/// fields demote to Moderate — serializers read and write reflectively.
/// Tier 2 (High).
/// </summary>
public sealed class WriteOnlyFieldAnalyser : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0021"];

    public SharedIndex SharedIndexes => SharedIndex.AnalysisScope | SharedIndex.SolutionUsageIndex;

    // Name-matched with/without the Attribute suffix so fixture stand-ins and real
    // serializer attributes behave identically: System.Text.Json's JsonInclude,
    // DataContractSerializer's DataMember, XmlSerializer's XmlElement, Newtonsoft's
    // JsonProperty.
    private static readonly FrozenSet<string> SerializationAttributeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "JsonInclude", "JsonIncludeAttribute",
        "DataMember", "DataMemberAttribute",
        "XmlElement", "XmlElementAttribute",
        "JsonProperty", "JsonPropertyAttribute",
    }.ToFrozenSet(StringComparer.Ordinal);

    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var findings = new ConcurrentBag<SnipperFinding>();
        var usageIndex = await SolutionUsageIndex.GetAsync(solution, cancellationToken).ConfigureAwait(false);

        // Same cost profile as UnusedPrivateMemberAnalyser.
        var documents = await AnalysisScope.GetAsync(solution, cancellationToken).ConfigureAwait(false);
        progress?.Invoke($"WriteOnlyFieldAnalyser: scanning {documents.Count} documents");
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

                    // Syntax gate: private field declarators only. Event fields are a
                    // different node kind; const fields can never be written.
                    if (node is not VariableDeclaratorSyntax
                        {
                            Parent: VariableDeclarationSyntax
                            {
                                Parent: FieldDeclarationSyntax fieldDeclaration,
                            },
                        }
                        || fieldDeclaration.Modifiers.Any(SyntaxKind.ConstKeyword)
                        || !HasPrivateAccessibility(fieldDeclaration))
                    {
                        continue;
                    }

                    if (semanticModel.GetDeclaredSymbol(node, documentToken) is not IFieldSymbol field
                        || field.DeclaredAccessibility != Accessibility.Private
                        || field.IsConst
                        || field.IsFixedSizeBuffer
                        || field.RefKind != RefKind.None
                        || field.IsImplicitlyDeclared)
                    {
                        continue;
                    }

                    if (ExclusionEngine.ShouldExclude(field))
                    {
                        continue;
                    }

                    // A private field is reachable only from its containing type
                    // (including nested types), which lives in a single project — the
                    // project-scopedDocument textual pre-filter is sound, and an empty set
                    // proves zero references (SNP0001's territory, not ours).
                    var candidateDocuments = usageIndex.GetDocumentsUsingName(document.Project, field.Name);
                    if (candidateDocuments.Count == 0)
                    {
                        continue;
                    }

                    var usage = await ClassifyFieldUsageAsync(field, solution, candidateDocuments, documentToken).ConfigureAwait(false);
                    if (usage is not FieldUsage.WriteOnly)
                    {
                        continue;
                    }

                    var tier = HasSerializationAttribute(field) ? CertaintyTier.Moderate : CertaintyTier.High;
                    var lineSpan = field.Locations[0].GetLineSpan();
                    findings.Add(new SnipperFinding(
                        RuleId: "SNP0021",
                        Title: "Write-Only Field",
                        Message: $"Private field '{field.Name}' is written but never read.",
                        Certainty: tier,
                        Category: FindingCategory.WriteOnlyField,
                        FilePath: lineSpan.Path ?? string.Empty,
                        LineNumber: lineSpan.StartLinePosition.Line + 1,
                        CharacterOffset: lineSpan.StartLinePosition.Character + 1));
                }
            }).ConfigureAwait(false);

        return [.. findings];
    }

    private static async Task<FieldUsage> ClassifyFieldUsageAsync(
        IFieldSymbol field,
        Solution solution,
        IImmutableSet<Document> candidateDocuments,
        CancellationToken cancellationToken)
    {
        var references = await FieldReferenceMap.GetReferencesAsync(field, solution, candidateDocuments, cancellationToken).ConfigureAwait(false);

        var sawWrite = false;
        foreach (var reference in references)
        {
            // Unconfirmed name matches are unknown evidence — treat as a read.
            if (reference.IsCandidate)
            {
                return FieldUsage.HasRead;
            }

            var kind = reference.Node is null
                ? FieldReferenceKind.Read
                : FieldReferenceClassifier.Classify(reference.Node);

            switch (kind)
            {
                case FieldReferenceKind.Read:
                case FieldReferenceKind.ReadWrite:
                    return FieldUsage.HasRead;
                case FieldReferenceKind.Write:
                    sawWrite = true;
                    break;
                case FieldReferenceKind.None:
                    break;
            }
        }

        // Zero references (or initializer-only) is SNP0001's case — never
        // double-report it here.
        return sawWrite ? FieldUsage.WriteOnly : FieldUsage.NoReferences;
    }

    private static bool HasSerializationAttribute(IFieldSymbol field)
    {
        foreach (var attribute in field.GetAttributes())
        {
            if (attribute.AttributeClass is not null && SerializationAttributeNames.Contains(attribute.AttributeClass.Name))
            {
                return true;
            }
        }

        return false;
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

    private enum FieldUsage
    {
        NoReferences,
        WriteOnly,
        HasRead,
    }
}
