namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
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
public sealed class EventNeverInvokedAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0030"];

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

            progress?.Invoke($"EventNeverInvokedAnalyser: scanning {project.Name}");

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

                foreach (var declarator in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Syntax gate: field-like event declarations only (custom
                    // add/remove accessors are EventDeclarationSyntax — v1 skips).
                    if (declarator.Parent is not VariableDeclarationSyntax { Parent: EventFieldDeclarationSyntax eventDeclaration }
                        || eventDeclaration.AttributeLists.Count > 0)
                    {
                        continue;
                    }

                    if (semanticModel.GetDeclaredSymbol(declarator, cancellationToken) is not IEventSymbol eventSymbol
                        || eventSymbol.IsImplicitlyDeclared
                        || eventSymbol.IsOverride
                        || eventSymbol.IsVirtual
                        || eventSymbol.ContainingType is not { } containingType
                        || containingType.TypeKind is TypeKind.Interface)
                    {
                        continue;
                    }

                    if (ExclusionEngine.ShouldExclude(eventSymbol)
                        || ExclusionEngine.IsNamespaceExcluded(eventSymbol, _exclusions)
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
                    var candidateDocuments = usageIndex.GetDocumentsUsingName(eventSymbol.Name);
                    if (candidateDocuments.Count > 0)
                    {
                        var references = await SymbolFinder.FindReferencesAsync(eventSymbol, solution, candidateDocuments, cancellationToken).ConfigureAwait(false);
                        foreach (var referencedSymbol in references)
                        {
                            foreach (var location in referencedSymbol.Locations)
                            {
                                if (location.IsCandidateLocation || !IsSubscription(location.Location, cancellationToken))
                                {
                                    sawRaiseShapedOrUnknown = true;
                                    break;
                                }

                                sawSubscription = true;
                            }

                            if (sawRaiseShapedOrUnknown)
                            {
                                break;
                            }
                        }
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
                        CharacterOffset: lineSpan.StartLinePosition.Character + 1,
                        Symbol: eventSymbol));
                }
            }
        }

        return findings;
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
