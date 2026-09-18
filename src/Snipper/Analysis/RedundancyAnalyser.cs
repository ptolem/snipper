namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Models;

/// <summary>
/// SNP0022/SNP0025/SNP0026 — Redundancy sweep. One umbrella pass over
/// invocations and casts with per-pattern evaluators, each shipping only when
/// its soundness gate holds (see <see cref="RedundantDefaultArgumentEvaluator"/>,
/// <see cref="RedundantTypeArgumentEvaluator"/>, and
/// <see cref="RedundantCastEvaluator"/>). Per-rule `off` in snipper.json is
/// honoured by the report-time FindingFilter; semantic work happens only on
/// syntax-pre-filtered candidates. Tier 2 (High).
/// </summary>
public sealed class RedundancyAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0022", "SNP0025", "SNP0026"];

    private readonly AnalysisExclusions _exclusions = exclusions ?? AnalysisExclusions.None;

    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var findings = new List<SnipperFinding>();
        var analysisRoots = ExclusionEngine.GetAnalysisRootDirectories(solution);

        // Sequential binding per the workspace's ConcurrentBuild=false contract.
        // Candidate sets are tiny (trailing literals / explicit type lists), so
        // speculation stays cheap even on large solutions.
        foreach (var project in solution.Projects)
        {
            if (!project.SupportsCompilation)
            {
                continue;
            }

            progress?.Invoke($"RedundancyAnalyser: scanning {project.Name}");

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

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (ExclusionEngine.IsNamespaceExcluded(invocation, semanticModel, _exclusions, cancellationToken))
                    {
                        continue;
                    }

                    if (RedundantDefaultArgumentEvaluator.TryEvaluate(invocation, semanticModel, cancellationToken) is { } argumentFinding)
                    {
                        findings.Add(argumentFinding);
                    }

                    if (RedundantTypeArgumentEvaluator.TryEvaluate(invocation, semanticModel, cancellationToken) is { } typeArgumentFinding)
                    {
                        findings.Add(typeArgumentFinding);
                    }
                }

                foreach (var cast in root.DescendantNodes().OfType<CastExpressionSyntax>())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (ExclusionEngine.IsNamespaceExcluded(cast, semanticModel, _exclusions, cancellationToken))
                    {
                        continue;
                    }

                    if (RedundantCastEvaluator.TryEvaluate(cast, semanticModel, cancellationToken) is { } castFinding)
                    {
                        findings.Add(castFinding);
                    }
                }
            }
        }

        return findings;
    }
}
