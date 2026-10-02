namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Models;

/// <summary>
/// SNP0009 — Flags local variables that are declared/assigned but never read:
/// ordinary locals, out-var declarations, deconstruction elements, and pattern
/// variables. A local cannot be referenced from outside its declaring method, by
/// reflection, or from another file — zero references is a complete proof.
/// Tier 1 (Guaranteed).
/// </summary>
public sealed class UnusedLocalVariableAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0009"];

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

            progress?.Invoke($"UnusedLocalVariableAnalyser: scanning {project.Name}");

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

                // One walk per document buckets every identifier by text; each
                // candidate below then binds only the positions spelling its name.
                var identifierIndex = DocumentIdentifierIndex.Build(root);

                foreach (var node in root.DescendantNodes())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Syntax gate: local declarations (excluding using-declarations and
                    // ref locals, both of which carry side effects) plus single-variable
                    // designations (out var, deconstruction elements, pattern variables).
                    // Discard designations are a different node kind and never match.
                    var (name, _) = node switch
                    {
                        VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: LocalDeclarationStatementSyntax localDeclaration } } declarator
                            when !localDeclaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)
                                && localDeclaration.Declaration.Type is not RefTypeSyntax
                            => (declarator.Identifier.Text, true),
                        SingleVariableDesignationSyntax designation
                            => (designation.Identifier.Text, true),
                        _ => (null, false),
                    };

                    if (name is null or "_")
                    {
                        continue;
                    }

                    // A declaration in unreachable code belongs to SNP0002, which
                    // reports the root cause — don't double-report the variable.
                    // The syntax gate answers "provably reachable" for nearly all
                    // declarations; flow analysis runs only when it might not be.
                    var enclosingStatement = node.FirstAncestorOrSelf<StatementSyntax>();
                    if (enclosingStatement is not null
                        && UnreachableCodeGate.MayStartUnreachable(enclosingStatement)
                        && semanticModel.AnalyzeControlFlow(enclosingStatement) is { StartPointIsReachable: false })
                    {
                        continue;
                    }

                    if (ExclusionEngine.IsNamespaceExcluded(node, semanticModel, _exclusions, cancellationToken))
                    {
                        continue;
                    }

                    // Fast path: the name never appears in a usage position anywhere
                    // in the document — provably unread with no semantic search.
                    if (!usageIndex.IsNameUsedInDocument(document, name))
                    {
                        findings.Add(CreateFinding(name, node.GetLocation().GetLineSpan()));
                        continue;
                    }

                    // Slow path: the name appears in the document (possibly on another
                    // symbol sharing it) — confirm semantically. A local's references
                    // can only live in this document, so the document-scoped index
                    // answers it without a solution-wide candidate-set search.
                    var symbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
                    if (symbol is not ILocalSymbol local)
                    {
                        continue;
                    }

                    if (!identifierIndex.HasReference(semanticModel, local, name))
                    {
                        findings.Add(CreateFinding(name, node.GetLocation().GetLineSpan()));
                    }
                }
            }
        }

        return findings;
    }

    private static SnipperFinding CreateFinding(string name, FileLinePositionSpan lineSpan)
    {
        return new SnipperFinding(
            RuleId: "SNP0009",
            Title: "Unused Local Variable",
            Message: $"Local variable '{name}' is declared but never read.",
            Certainty: CertaintyTier.Guaranteed,
            Category: FindingCategory.UnusedLocalVariable,
            FilePath: lineSpan.Path ?? string.Empty,
            LineNumber: lineSpan.StartLinePosition.Line + 1,
            CharacterOffset: lineSpan.StartLinePosition.Character + 1,
            Symbol: null);
    }
}
