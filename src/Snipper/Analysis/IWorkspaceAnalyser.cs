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

    Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null);
}
