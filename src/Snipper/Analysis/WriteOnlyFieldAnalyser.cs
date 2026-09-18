namespace Snipper.Analysis;

using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
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
public sealed class WriteOnlyFieldAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0021"];

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

        // Sequential binding per the workspace's ConcurrentBuild=false contract —
        // same cost profile as UnusedPrivateMemberAnalyser.
        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation)
            {
                continue;
            }

            progress?.Invoke($"WriteOnlyFieldAnalyser: scanning {project.Name}");

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

                    if (semanticModel.GetDeclaredSymbol(node, cancellationToken) is not IFieldSymbol field
                        || field.DeclaredAccessibility != Accessibility.Private
                        || field.IsConst
                        || field.IsFixedSizeBuffer
                        || field.RefKind != RefKind.None
                        || field.IsImplicitlyDeclared)
                    {
                        continue;
                    }

                    if (ExclusionEngine.ShouldExclude(field)
                        || ExclusionEngine.IsNamespaceExcluded(field, _exclusions))
                    {
                        continue;
                    }

                    // A private field is reachable only from its containing type
                    // (including nested types), which lives in a single project — the
                    // project-scoped textual pre-filter is sound, and an empty set
                    // proves zero references (SNP0001's territory, not ours).
                    var candidateDocuments = usageIndex.GetDocumentsUsingName(project, field.Name);
                    if (candidateDocuments.Count == 0)
                    {
                        continue;
                    }

                    var usage = await ClassifyFieldUsageAsync(field, solution, candidateDocuments, cancellationToken).ConfigureAwait(false);
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
                        CharacterOffset: lineSpan.StartLinePosition.Character + 1,
                        Symbol: field));
                }
            }
        }

        return findings;
    }

    private static async Task<FieldUsage> ClassifyFieldUsageAsync(
        IFieldSymbol field,
        Solution solution,
        IImmutableSet<Document> candidateDocuments,
        CancellationToken cancellationToken)
    {
        var references = await SymbolFinder.FindReferencesAsync(field, solution, candidateDocuments, cancellationToken).ConfigureAwait(false);

        var sawWrite = false;
        foreach (var reference in references)
        {
            foreach (var location in reference.Locations)
            {
                // Unconfirmed name matches are unknown evidence — treat as a read.
                if (location.IsCandidateLocation)
                {
                    return FieldUsage.HasRead;
                }

                var node = location.Location.SourceTree?.GetRoot(cancellationToken)
                    .FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true) as SimpleNameSyntax;
                var kind = node is null
                    ? FieldReferenceKind.Read
                    : FieldReferenceClassifier.Classify(node);

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
