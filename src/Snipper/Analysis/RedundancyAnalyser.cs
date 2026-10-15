namespace Snipper.Analysis;

using System.Collections.Concurrent;
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
public sealed class RedundancyAnalyser : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0022", "SNP0025", "SNP0026", "SNP0028", "SNP0029"];

    public SharedIndex SharedIndexes => SharedIndex.AnalysisScope;

    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var findings = new ConcurrentBag<SnipperFinding>();

        // Candidate sets are tiny (trailing literals / explicit type lists), so
        // speculation stays cheap even on large solutions.
        var documents = await AnalysisScope.GetAsync(solution, cancellationToken).ConfigureAwait(false);
        progress?.Invoke($"RedundancyAnalyser: scanning {documents.Count} documents");
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
                    documentToken.ThrowIfCancellationRequested();

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
                    documentToken.ThrowIfCancellationRequested();

                    if (RedundantDefaultArgumentEvaluator.TryEvaluate(invocation, semanticModel, documentToken) is { } argumentFinding)
                    {
                        findings.Add(argumentFinding);
                    }

                    if (RedundantTypeArgumentEvaluator.TryEvaluate(invocation, semanticModel, documentToken) is { } typeArgumentFinding)
                    {
                        findings.Add(typeArgumentFinding);
                    }
                }

                foreach (var cast in casts ?? [])
                {
                    documentToken.ThrowIfCancellationRequested();

                    if (RedundantCastEvaluator.TryEvaluate(cast, semanticModel, documentToken) is { } castFinding)
                    {
                        findings.Add(castFinding);
                    }
                    else if (RedundantUpcastEvaluator.TryEvaluate(cast, semanticModel, documentToken) is { } upcastFinding)
                    {
                        findings.Add(upcastFinding);
                    }
                }

                foreach (var access in accesses ?? [])
                {
                    documentToken.ThrowIfCancellationRequested();

                    // Syntax pre-filter: only 'this.'-qualified accesses are
                    // candidates — speculation never runs for anything else.
                    if (access.Expression is not ThisExpressionSyntax)
                    {
                        continue;
                    }

                    if (RedundantQualifierEvaluator.TryEvaluateThisQualifier(access, semanticModel, documentToken) is { } qualifierFinding)
                    {
                        findings.Add(qualifierFinding);
                    }
                }

                foreach (var qualifiedName in qualifiedNames ?? [])
                {
                    documentToken.ThrowIfCancellationRequested();

                    if (RedundantQualifierEvaluator.TryEvaluateQualifiedName(qualifiedName, semanticModel, documentToken) is { } qualifiedNameFinding)
                    {
                        findings.Add(qualifiedNameFinding);
                    }
                }

                foreach (var typeDeclaration in typeDeclarations ?? [])
                {
                    documentToken.ThrowIfCancellationRequested();

                    foreach (var member in typeDeclaration.Members)
                    {
                        switch (member)
                        {
                            case ConstructorDeclarationSyntax constructor:
                                if (EmptyTypeMemberEvaluator.TryEvaluateConstructor(constructor, semanticModel, documentToken) is { } constructorFinding)
                                {
                                    findings.Add(constructorFinding);
                                }

                                break;
                            case DestructorDeclarationSyntax destructor:
                                if (EmptyTypeMemberEvaluator.TryEvaluateDestructor(destructor, semanticModel, documentToken) is { } destructorFinding)
                                {
                                    findings.Add(destructorFinding);
                                }

                                break;
                        }
                    }
                }
            }).ConfigureAwait(false);

        return [.. findings];
    }
}
