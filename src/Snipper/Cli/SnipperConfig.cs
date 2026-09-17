namespace Snipper.Cli;

using System.Collections.Frozen;
using Snipper.Models;

/// <summary>
/// Resolved snipper.json configuration (schema "version": 1). Disabled rules are
/// skipped entirely (their analysers never run); severity overrides and path-glob
/// exclusions are applied to findings at report time, after baseline fingerprinting,
/// so toggling config never churns the baseline. Path globs filter findings only —
/// usage evidence from excluded paths is always retained.
/// </summary>
internal sealed record SnipperConfig
{
    public static readonly SnipperConfig Empty = new();

    public FrozenSet<string> DisabledRules { get; init; } = FrozenSet<string>.Empty;

    public FrozenDictionary<string, CertaintyTier> SeverityOverrides { get; init; } =
        FrozenDictionary<string, CertaintyTier>.Empty;

    public FrozenSet<string> ExcludedNamespaces { get; init; } = FrozenSet<string>.Empty;

    public FrozenSet<string> ExcludedPathGlobs { get; init; } = FrozenSet<string>.Empty;
}
