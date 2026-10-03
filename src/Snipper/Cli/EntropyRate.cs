namespace Snipper.Cli;

using System.Globalization;

/// <summary>
/// Why an entropy rate is, or is not, a real measurement (Wave 4 / 4B).
///
/// Serialized in camelCase. Every value except <see cref="Scored"/> exists because an
/// unmeasured rate is dangerous: <c>new / (lines / 1000)</c> with an absent denominator
/// yields <c>0.00</c>, and <c>0.00</c> passes any budget. Each of these is a named,
/// reportable reason the gate stays silent instead.
/// </summary>
internal enum EntropyRateStatus
{
    /// <summary>Both halves were measured and the rate is meaningful.</summary>
    Scored,

    /// <summary>No baseline file existed, so every finding classified as new. Re-run to score.</summary>
    BaselineSeeded,

    /// <summary>The baseline predates commit stamping, so its change range is unrecoverable.</summary>
    NoBaselineReference,

    /// <summary>Not a git repository, or the revisions could not be read.</summary>
    NotAGitRepository,

    /// <summary>Uncommitted changes mean the analysed bytes are not the analysed commit.</summary>
    DirtyWorkingTree,

    /// <summary>The baseline commit is HEAD; nothing changed.</summary>
    UnchangedRange,

    /// <summary>The change is smaller than the scoring floor. The rate is shown but not scored.</summary>
    BelowMinimumChange,
}

/// <summary>
/// Raw inputs for <see cref="EntropyRateCalculator.Compute"/>, kept as plain values so the
/// decision logic is testable without a git repository or an analysis run.
/// </summary>
/// <param name="BaselineFileExisted">Whether a baseline file was present *before* this run.</param>
/// <param name="BaselineCommitSha">Commit the baseline was stamped at, or null if unknown.</param>
/// <param name="HeadCommitSha">Commit being analysed, or null if unavailable.</param>
/// <param name="IsGitRepository">Whether a repository was detected at all.</param>
/// <param name="WorkingTreeDirty">Whether uncommitted changes are present.</param>
/// <param name="NewFindings">Findings absent from the recorded baseline.</param>
/// <param name="ResolvedFindings">Fingerprints in the baseline but no longer present.</param>
/// <param name="ChangedLines">Lines changed across the baseline range, or null if unreadable.</param>
/// <param name="MinimumLines">Scoring floor; clamped to at least 1.</param>
internal sealed record EntropyRateInputs(
    bool BaselineFileExisted,
    string? BaselineCommitSha,
    string? HeadCommitSha,
    bool IsGitRepository,
    bool WorkingTreeDirty,
    int NewFindings,
    int ResolvedFindings,
    int? ChangedLines,
    int MinimumLines);

/// <summary>
/// An entropy-rate measurement, or a named reason there isn't one.
/// </summary>
/// <param name="Status">Measurement status.</param>
/// <param name="Reason">Human-readable explanation, present whenever the rate is not scored.</param>
/// <param name="NewFindings">Findings added since the baseline.</param>
/// <param name="ResolvedFindings">Fingerprints that disappeared since the baseline.</param>
/// <param name="LinesChanged">Denominator, when it could be read.</param>
/// <param name="BaselineCommitSha">Range start.</param>
/// <param name="HeadCommitSha">Range end.</param>
/// <param name="MinimumLines">The effective floor after clamping.</param>
internal sealed record EntropyRateResult(
    EntropyRateStatus Status,
    string? Reason,
    int NewFindings,
    int ResolvedFindings,
    int? LinesChanged,
    string? BaselineCommitSha,
    string? HeadCommitSha,
    int MinimumLines)
{
    /// <summary>Whether the rate may be compared against a budget.</summary>
    public bool IsScored => Status == EntropyRateStatus.Scored;

    /// <summary>
    /// Findings added per thousand changed lines. Reported for visibility even when below the
    /// floor, because hiding the number would leave a reader with no explanation for it.
    /// </summary>
    public double? FindingsPerKloc =>
        (Status is EntropyRateStatus.Scored or EntropyRateStatus.BelowMinimumChange)
        && LinesChanged is { } lines
        && lines > 0
            ? NewFindings * 1000.0 / lines
            : null;

    /// <summary>
    /// Whether this measurement breaches <paramref name="budget"/>.
    ///
    /// Fails open in two independent ways, both deliberate: an unscored measurement never
    /// breaches, and no budget means no gate. A metric that can fail a build when git is
    /// missing or the tree is dirty gets switched off, and then it protects nothing.
    /// </summary>
    public bool Exceeds(double? budget) =>
        IsScored && budget is { } limit && FindingsPerKloc is { } rate && rate > limit;
}

/// <summary>
/// Computes the churn-normalized entropy rate introduced by Wave 4 / 4B.
///
/// The denominator is deliberately **not** "lines changed in this pull request". Measured over
/// this repository's 39 commits, holding quality constant at one new finding, the per-change
/// rate spans 200/kLOC at 5 changed lines to 0.5/kLOC at 2000 — a 400x spread driven entirely
/// by change size, which would make any fixed budget a measure of PR size rather than of
/// entropy. Instead the range is bound to the numerator: the baseline records the commit it was
/// stamped at, so numerator and denominator provably cover the same span. Small ranges are then
/// handled by an explicit floor rather than by being silently averaged away.
/// </summary>
internal static class EntropyRateCalculator
{
    /// <summary>Floor used when the caller does not supply one.</summary>
    public const int DefaultMinimumLines = 50;

    /// <summary>
    /// Classifies the inputs and, where both halves are present, computes the rate.
    ///
    /// Checks run in a fixed precedence order, most-fundamental first. The two baseline checks
    /// come before the git checks because a seeded or unstamped baseline makes the *numerator*
    /// meaningless regardless of how well the denominator could be measured — and saying
    /// "baseline seeded, re-run to score" is more actionable than "not a git repository".
    /// </summary>
    public static EntropyRateResult Compute(EntropyRateInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        // A floor of zero would let a zero-line change reach the division, so clamp rather
        // than trust the caller.
        var minimumLines = Math.Max(inputs.MinimumLines, 1);

        int? lines = inputs.ChangedLines is { } value && value > 0 ? value : null;

        return inputs switch
        {
            // Numerator meaningless: nothing was recorded to be new against.
            { BaselineFileExisted: false } => NotScored(
                inputs, minimumLines, EntropyRateStatus.BaselineSeeded,
                "no baseline existed, so every finding counted as new; re-run to score"),

            // Denominator unbindable: a baseline written before 4B has no commit to diff from.
            { BaselineCommitSha: null } => NotScored(
                inputs, minimumLines, EntropyRateStatus.NoBaselineReference,
                "the recorded baseline has no commit reference; re-baseline to score"),

            { IsGitRepository: false } or { HeadCommitSha: null } => NotScored(
                inputs, minimumLines, EntropyRateStatus.NotAGitRepository,
                "not a git repository, so the change range could not be read"),

            // The analysed bytes are not the commit, so a diff-derived denominator would
            // describe a different tree than the one that produced the numerator.
            { WorkingTreeDirty: true } => NotScored(
                inputs, minimumLines, EntropyRateStatus.DirtyWorkingTree,
                "uncommitted changes mean the analysed tree is not the analysed commit"),

            { ChangedLines: null } => NotScored(
                inputs, minimumLines, EntropyRateStatus.NotAGitRepository,
                "changed lines could not be read for the baseline range"),

            { BaselineCommitSha: not null, HeadCommitSha: not null } when
                inputs.BaselineCommitSha == inputs.HeadCommitSha => NotScored(
                    inputs, minimumLines, EntropyRateStatus.UnchangedRange,
                    "the baseline commit is HEAD; nothing changed"),

            // Zero changed lines is reachable for real (mode-only or binary-only commits) and
            // is handled here rather than by dividing.
            { ChangedLines: not null } when lines is null => NotScored(
                inputs, minimumLines, EntropyRateStatus.BelowMinimumChange,
                "no text lines changed in the baseline range"),

            { ChangedLines: not null } when lines < minimumLines => NotScored(
                inputs, minimumLines, EntropyRateStatus.BelowMinimumChange,
                $"change of {lines} line(s) is below the {minimumLines}-line scoring floor"),
            _ => new EntropyRateResult(
                Status: EntropyRateStatus.Scored,
                Reason: null,
                NewFindings: inputs.NewFindings,
                ResolvedFindings: inputs.ResolvedFindings,
                LinesChanged: lines,
                BaselineCommitSha: inputs.BaselineCommitSha,
                HeadCommitSha: inputs.HeadCommitSha,
                MinimumLines: minimumLines),
        };
    }

    /// <summary>
    /// Fingerprints that were in the baseline and are no longer present.
    ///
    /// This is a count, not a claim that anyone fixed anything: a fingerprint also disappears
    /// when its file is deleted, or when an edit rewrites a finding's message. Reporting it as
    /// "resolved" would overstate what a diff of fingerprints can know.
    /// </summary>
    public static int CountResolved(IReadOnlySet<string> known, IReadOnlySet<string> current)
    {
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(current);

        var resolved = 0;
        foreach (var fingerprint in known)
        {
            if (!current.Contains(fingerprint))
            {
                resolved++;
            }
        }

        return resolved;
    }

    private static EntropyRateResult NotScored(
        EntropyRateInputs inputs,
        int minimumLines,
        EntropyRateStatus status,
        string reason) =>
        new(
            Status: status,
            Reason: reason,
            NewFindings: inputs.NewFindings,
            ResolvedFindings: inputs.ResolvedFindings,
            LinesChanged: inputs.ChangedLines,
            BaselineCommitSha: inputs.BaselineCommitSha,
            HeadCommitSha: inputs.HeadCommitSha,
            MinimumLines: minimumLines);

    /// <summary>
    /// Renders a rate for the console at a fixed precision, or a dash when absent.
    /// </summary>
    public static string Format(double? findingsPerKloc) =>
        findingsPerKloc is { } rate
            ? rate.ToString("0.00", CultureInfo.InvariantCulture)
            : "-";

    /// <summary>
    /// Projects a result into the JSON report shape, adding the gate outcome and any
    /// month-over-month history from the ledger.
    /// </summary>
    public static EntropyRateReport ToReport(
        EntropyRateResult result,
        double? budget,
        IReadOnlyList<EntropyMonthlyRate> monthly)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(monthly);

        return new EntropyRateReport(
            Status: result.Status,
            Scored: result.IsScored,
            Reason: result.Reason,
            NewFindings: result.NewFindings,
            ResolvedFindings: result.ResolvedFindings,
            LinesChanged: result.LinesChanged,
            BaselineCommitSha: result.BaselineCommitSha,
            HeadCommitSha: result.HeadCommitSha,
            MinimumLines: result.MinimumLines,
            FindingsPerKloc: result.FindingsPerKloc,
            Budget: budget,
            BudgetExceeded: result.Exceeds(budget),
            Monthly: [.. monthly]);
    }
}

/// <summary>
/// The <c>entropyRate</c> section of the JSON report. Emitted only when 4B is requested, so
/// existing consumers never see a field they did not ask for.
/// </summary>
/// <param name="Status">Why the rate is or is not scored.</param>
/// <param name="Scored">Whether the gate may compare against the budget.</param>
/// <param name="Reason">Explanation, present whenever <paramref name="Scored"/> is false.</param>
/// <param name="NewFindings">Findings added since the baseline.</param>
/// <param name="ResolvedFindings">Fingerprints that disappeared since the baseline.</param>
/// <param name="LinesChanged">Denominator for the baseline range.</param>
/// <param name="BaselineCommitSha">Range start.</param>
/// <param name="HeadCommitSha">Range end.</param>
/// <param name="MinimumLines">Effective scoring floor.</param>
/// <param name="FindingsPerKloc">Findings added per thousand changed lines.</param>
/// <param name="Budget">Configured ceiling, if any.</param>
/// <param name="BudgetExceeded">Whether the gate fired.</param>
/// <param name="Monthly">Month-over-month rates from the ledger, oldest first.</param>
internal sealed record EntropyRateReport(
    EntropyRateStatus Status,
    bool Scored,
    string? Reason,
    int NewFindings,
    int ResolvedFindings,
    int? LinesChanged,
    string? BaselineCommitSha,
    string? HeadCommitSha,
    int MinimumLines,
    double? FindingsPerKloc,
    double? Budget,
    bool BudgetExceeded,
    EntropyMonthlyRate[] Monthly);
