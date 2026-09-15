namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Snipper.Models;

public interface IWorkspaceAnalyser
{
    Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null);
}
