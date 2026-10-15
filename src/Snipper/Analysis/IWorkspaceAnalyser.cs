namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Snipper.Models;

public interface IWorkspaceAnalyser
{
    /// <summary>
    /// Rule ids this analyser can emit — the single source of truth used by
    /// snipper.json to skip fully disabled analysers.
    /// </summary>
    IReadOnlyCollection<string> RuleIds { get; }

    /// <summary>
    /// The shared solution-wide indexes this analyser reads. <c>AnalysisRunner</c> builds
    /// their union before the fan-out, so their cost is reported once, as its own stage,
    /// rather than charged to whichever analyser happened to touch an index first. An
    /// index read but not declared is still built on first use, just inside that
    /// analyser's time; <c>SharedIndexDeclarationShould</c> keeps the declarations honest.
    /// </summary>
    SharedIndex SharedIndexes { get; }

    Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null);
}
