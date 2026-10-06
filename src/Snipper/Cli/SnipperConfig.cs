namespace Snipper.Cli;

using System.Collections.Frozen;
using Snipper.Models;

/// <summary>
/// Resolved snipper.json configuration (schema "version": 1). Disabled rules are
/// skipped entirely (their analysers never run). Path globs filter findings only —
/// usage evidence from excluded paths is always retained.
///
/// Baseline safety (measured 2026-10-03, see <c>docs/history/1_7_0_plan.md</c>): no suppression
/// channel can rewrite a baseline. Path globs and severity overrides apply after
/// fingerprinting, and as of 4A-2 namespace exclusions and disabled rules are
/// fingerprinted from the suppression-independent finding set, so a config toggle never
/// changes what the baseline records. The accepted cost is that a baseline legitimately
/// grows on first run to include findings for excluded code.
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
