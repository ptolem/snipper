namespace Snipper.Analysis;

using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Models;

/// <summary>
/// SNP0030 — Event never invoked (Advisory). A field-like event is dead surface
/// when nothing raises it: subscriptions (<c>+=</c>/<c>-=</c>) alone keep only
/// the illusion of life. Subscription references never count as usage; any other
/// confirmed reference — an invocation, a conditional <c>?.Invoke</c> receiver,
/// a delegate read — is raise-shaped evidence and suppresses (conservative:
/// reads that merely guard a raise also suppress). Custom add/remove accessors,
/// interface events and implementations, virtual/override families, and
/// attributed events are out of scope (v1). Zero-reference events flag too —
/// no other rule covers events.
/// </summary>
public sealed class EventNeverInvokedAnalyser : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0030"];

    public SharedIndex SharedIndexes => SharedIndex.AnalysisScope | SharedIndex.SolutionUsageIndex;

    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var findings = new ConcurrentBag<SnipperFinding>();
        var referenceSearch = await ReferenceSearch.GetAsync(solution, cancellationToken).ConfigureAwait(false);

        var documents = await AnalysisScope.GetAsync(solution, cancellationToken).ConfigureAwait(false);
        progress?.Invoke($"EventNeverInvokedAnalyser: scanning {documents.Count} documents");
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

                foreach (var declarator in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
                {
                    documentToken.ThrowIfCancellationRequested();

                    // Syntax gate: field-like event declarations only (custom
                    // add/remove accessors are EventDeclarationSyntax — v1 skips).
                    if (declarator.Parent is not VariableDeclarationSyntax { Parent: EventFieldDeclarationSyntax eventDeclaration }
                        || eventDeclaration.AttributeLists.Count > 0)
                    {
                        continue;
                    }

                    if (semanticModel.GetDeclaredSymbol(declarator, documentToken) is not IEventSymbol eventSymbol
                        || eventSymbol.IsImplicitlyDeclared
                        || eventSymbol.IsOverride
                        || eventSymbol.IsVirtual
                        || eventSymbol.ContainingType is not { } containingType
                        || containingType.TypeKind is TypeKind.Interface)
                    {
                        continue;
                    }

                    if (ExclusionEngine.ShouldExclude(eventSymbol)
                        || InterfaceImplementationQuery.IsInterfaceImplementation(eventSymbol))
                    {
                        continue;
                    }

                    // Reference classification: subscriptions never count; any
                    // other confirmed reference is raise-shaped evidence.
                    // Unconfirmed (candidate) locations suppress — unknown
                    // evidence is never a finding.
                    var sawSubscription = false;
                    var sawRaiseShapedOrUnknown = false;
                    var references = await referenceSearch.FindAsync(eventSymbol, documentToken).ConfigureAwait(false);
                    foreach (var location in references)
                    {
                        if (location.IsCandidateLocation || !IsSubscription(location.Location, documentToken))
                        {
                            sawRaiseShapedOrUnknown = true;
                            break;
                        }

                        sawSubscription = true;
                    }

                    if (sawRaiseShapedOrUnknown)
                    {
                        continue;
                    }

                    var lineSpan = eventSymbol.Locations[0].GetLineSpan();
                    findings.Add(new SnipperFinding(
                        RuleId: "SNP0030",
                        Title: "Event Never Invoked",
                        Message: sawSubscription
                            ? $"Event '{eventSymbol.Name}' has subscribers but is never raised."
                            : $"Event '{eventSymbol.Name}' is never raised or subscribed.",
                        Certainty: CertaintyTier.Advisory,
                        Category: FindingCategory.UnusedEvent,
                        FilePath: lineSpan.Path ?? string.Empty,
                        LineNumber: lineSpan.StartLinePosition.Line + 1,
                        CharacterOffset: lineSpan.StartLinePosition.Character + 1));
                }
            }).ConfigureAwait(false);

        return [.. findings];
    }

    /// <summary>
    /// A subscription is the event name on the left of <c>+=</c> or <c>-=</c>.
    /// </summary>
    private static bool IsSubscription(Location location, CancellationToken cancellationToken)
    {
        var node = location.SourceTree?.GetRoot(cancellationToken).FindNode(location.SourceSpan, getInnermostNodeForTie: true);
        return node?.FirstAncestorOrSelf<AssignmentExpressionSyntax>() is { } assignment
            && (assignment.IsKind(SyntaxKind.AddAssignmentExpression) || assignment.IsKind(SyntaxKind.SubtractAssignmentExpression))
            && assignment.Left.Span.Contains(node.Span);
    }
}
