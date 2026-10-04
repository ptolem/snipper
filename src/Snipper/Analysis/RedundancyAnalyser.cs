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
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0022", "SNP0025", "SNP0026", "SNP0028", "SNP0029"];

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

                // ONE walk, five buckets, instead of five full DescendantNodes() traversals.
                // The buckets are then consumed below in the original shape order so the
                // order findings are appended in is unchanged - the report sorts by
                // (Certainty, FilePath) with a stable sort, so anything that reorders
                // insertion within a tie would change the JSON. Within a bucket, document
                // order is already the walk order, so each list matches exactly what
                // DescendantNodes().OfType<T>() would have produced.
                List<InvocationExpressionSyntax>? invocations = null;
                List<CastExpressionSyntax>? casts = null;
                List<MemberAccessExpressionSyntax>? accesses = null;
                List<QualifiedNameSyntax>? qualifiedNames = null;
                List<TypeDeclarationSyntax>? typeDeclarations = null;

                foreach (var node in root.DescendantNodes())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    switch (node)
                    {
                        case InvocationExpressionSyntax invocation:
                            (invocations ??= []).Add(invocation);
                            break;
                        case CastExpressionSyntax cast:
                            (casts ??= []).Add(cast);
                            break;
                        case MemberAccessExpressionSyntax access:
                            (accesses ??= []).Add(access);
                            break;
                        case QualifiedNameSyntax qualifiedName:
                            (qualifiedNames ??= []).Add(qualifiedName);
                            break;
                        case TypeDeclarationSyntax typeDeclaration:
                            (typeDeclarations ??= []).Add(typeDeclaration);
                            break;
                    }
                }

                foreach (var invocation in invocations ?? [])
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

                foreach (var cast in casts ?? [])
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
                    else if (RedundantUpcastEvaluator.TryEvaluate(cast, semanticModel, cancellationToken) is { } upcastFinding)
                    {
                        findings.Add(upcastFinding);
                    }
                }

                foreach (var access in accesses ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Syntax pre-filter: only 'this.'-qualified accesses are
                    // candidates — speculation never runs for anything else.
                    if (access.Expression is not ThisExpressionSyntax
                        || ExclusionEngine.IsNamespaceExcluded(access, semanticModel, _exclusions, cancellationToken))
                    {
                        continue;
                    }

                    if (RedundantQualifierEvaluator.TryEvaluateThisQualifier(access, semanticModel, cancellationToken) is { } qualifierFinding)
                    {
                        findings.Add(qualifierFinding);
                    }
                }

                foreach (var qualifiedName in qualifiedNames ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (ExclusionEngine.IsNamespaceExcluded(qualifiedName, semanticModel, _exclusions, cancellationToken))
                    {
                        continue;
                    }

                    if (RedundantQualifierEvaluator.TryEvaluateQualifiedName(qualifiedName, semanticModel, cancellationToken) is { } qualifiedNameFinding)
                    {
                        findings.Add(qualifiedNameFinding);
                    }
                }

                foreach (var typeDeclaration in typeDeclarations ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    foreach (var member in typeDeclaration.Members)
                    {
                        switch (member)
                        {
                            case ConstructorDeclarationSyntax constructor
                                when !ExclusionEngine.IsNamespaceExcluded(constructor, semanticModel, _exclusions, cancellationToken):
                                if (EmptyTypeMemberEvaluator.TryEvaluateConstructor(constructor, semanticModel, cancellationToken) is { } constructorFinding)
                                {
                                    findings.Add(constructorFinding);
                                }

                                break;
                            case DestructorDeclarationSyntax destructor
                                when !ExclusionEngine.IsNamespaceExcluded(destructor, semanticModel, _exclusions, cancellationToken):
                                if (EmptyTypeMemberEvaluator.TryEvaluateDestructor(destructor, semanticModel, cancellationToken) is { } destructorFinding)
                                {
                                    findings.Add(destructorFinding);
                                }

                                break;
                        }
                    }
                }
            }
        }

        return findings;
    }
}
