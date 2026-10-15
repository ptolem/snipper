namespace Snipper.Cli;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Snipper.Models;

/// <summary>
/// Baseline support for CI adoption: existing findings are recorded as accepted
/// so only NEW dead code is reported. Fingerprints are content hashes over
/// rule + relative path + message — deliberately excluding line numbers so
/// incidental edits don't churn the baseline.
///
/// Channel safety (4A-2, measured — see <c>docs/history/1_7_0_plan.md</c>): no suppression
/// channel can rewrite a baseline. <c>exclude.paths</c>, severity overrides and
/// <c>--certainty-tier</c> apply after fingerprinting. Namespace exclusions hide findings
/// the analysis still produced, and disabling a sole-rule analyser removes them before they
/// exist, so the caller fingerprints the suppression-independent set instead — see
/// <see cref="RequiresDisabledAnalyserPass"/>.
/// </summary>
internal static class BaselineService
{
    /// <summary>
    /// Whether the analysers a disabled rule removed must still run, so the baseline can
    /// record their findings and re-enabling the rule does not surface them as new.
    ///
    /// True only with a baseline and a removed analyser. No baseline means there is nothing to
    /// churn, so a user who has not adopted baselines never pays for the extra pass; and the
    /// other channels need no pass at all, because the findings they hide were produced by
    /// the normal one.
    /// </summary>
    public static bool RequiresDisabledAnalyserPass(bool baselineRequested, int removedAnalyserCount) =>
        baselineRequested && removedAnalyserCount > 0;

    public static string ComputeFingerprint(SnipperFinding finding, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var relativePath = finding.FilePath.Length > 0
            ? Path.GetRelativePath(baseDirectory, finding.FilePath).Replace('\\', '/')
            : string.Empty;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{finding.RuleId}|{relativePath}|{finding.Message}"));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Loads the whole baseline file, including the commit it was stamped at.
    ///
    /// That commit is what lets 4B bind its denominator to its numerator: the entropy rate's
    /// changed-line count is taken over exactly the range the recorded findings came from,
    /// instead of over a calendar window that may not match.
    /// </summary>
    public static BaselineFile Load(string baselinePath)
    {
        if (string.IsNullOrEmpty(baselinePath) || !File.Exists(baselinePath))
        {
            return new BaselineFile(Version: "1", Fingerprints: [], CommitSha: null);
        }

        try
        {
            var json = File.ReadAllText(baselinePath);
            var baseline = JsonSerializer.Deserialize(json, JsonReportSerializerContext.Default.BaselineFile);
            return baseline ?? new BaselineFile(Version: "1", Fingerprints: [], CommitSha: null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new BaselineFile(Version: "1", Fingerprints: [], CommitSha: null);
        }
    }

    public static void Write(string baselinePath, IReadOnlyCollection<string> fingerprints, string? commitSha = null)
    {
        var parentDirectory = Path.GetDirectoryName(baselinePath);
        if (!string.IsNullOrEmpty(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        // Distinct: the fingerprint deliberately carries no line number, so two findings
        // that render the same message in the same file hash to one value. Writing both
        // would grow the file on every run and imply two suppressed findings where there
        // is one.
        var baseline = new BaselineFile(
            Version: "1",
            Fingerprints: [.. fingerprints.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            CommitSha: commitSha);
        var json = JsonSerializer.Serialize(baseline, JsonReportSerializerContext.Default.BaselineFile);
        File.WriteAllText(baselinePath, json);
    }
}

/// <summary>
/// A committed baseline file.
/// </summary>
/// <param name="Version">Schema version.</param>
/// <param name="Fingerprints">Accepted findings, ordinal sorted.</param>
/// <param name="CommitSha">
/// Commit the fingerprints were taken at. Optional so files written before 4B still load; a
/// baseline without one is reported as <c>noBaselineReference</c> rather than guessed at.
/// </param>
internal sealed record BaselineFile(string Version, string[] Fingerprints, string? CommitSha = null);
