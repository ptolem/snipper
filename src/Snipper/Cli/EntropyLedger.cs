namespace Snipper.Cli;

using System.Globalization;
using System.Text.Json;

/// <summary>
/// One recorded entropy-rate run (Wave 4 / 4B).
///
/// Only <em>scored</em> runs are recorded. A run that seeded a baseline or sat below the floor
/// has no rate, and committing rows of nulls would make the ledger look busier than the data is.
/// </summary>
/// <param name="CommitSha">Commit the run analysed; also the ledger's primary key.</param>
/// <param name="RecordedAtUtc">When the entry was written, for human reference.</param>
/// <param name="NewFindings">Findings added across the baseline range.</param>
/// <param name="ResolvedFindings">Fingerprints that disappeared across the range.</param>
/// <param name="LinesChanged">Denominator for the range.</param>
/// <param name="BaselineCommitSha">Range start.</param>
/// <param name="FindingsPerKloc">The measured rate.</param>
/// <param name="Scored">Always true for a recorded entry; retained so the field is explicit.</param>
internal sealed record EntropyLedgerEntry(
    string CommitSha,
    string RecordedAtUtc,
    int NewFindings,
    int ResolvedFindings,
    int LinesChanged,
    string? BaselineCommitSha,
    double? FindingsPerKloc,
    bool Scored);

/// <summary>
/// The committed ledger file. Reviewable in a pull request, per locked decision 2a — no
/// service and no dashboard.
/// </summary>
internal sealed record EntropyLedgerFile(string Version, EntropyLedgerEntry[] Entries);

/// <summary>
/// A month's aggregated rate. The denominator here is large by construction, which is what
/// makes it the stable trend the per-run rate deliberately is not.
/// </summary>
/// <param name="Month">ISO month, <c>yyyy-MM</c>.</param>
/// <param name="NewFindings">Findings added across the month.</param>
/// <param name="ResolvedFindings">Fingerprints that disappeared across the month.</param>
/// <param name="LinesChanged">Lines changed across the month.</param>
/// <param name="FindingsPerKloc">Monthly rate, or null when no text lines changed.</param>
internal sealed record EntropyMonthlyRate(
    string Month,
    int NewFindings,
    int ResolvedFindings,
    int LinesChanged,
    double? FindingsPerKloc);

/// <summary>
/// Reads and writes the committed entropy ledger.
///
/// Entries are canonicalised **sorted by commit SHA**, not chronologically. A ledger is a state
/// file that appears in diffs, and sorting by content means a re-run of the same commit
/// produces a byte-identical file instead of reshuffling rows. Consumers that want a series
/// sort by <see cref="EntropyLedgerEntry.RecordedAtUtc"/>; <see cref="MonthlyRates"/> does.
///
/// Writes are replace-or-insert keyed on commit SHA, so a CI re-run on one commit updates its
/// row rather than appending a duplicate that would double-count the rate.
/// </summary>
internal static class EntropyLedger
{
    /// <summary>Current ledger schema version.</summary>
    private const string CurrentVersion = "1";

    /// <summary>
    /// Loads the ledger, or an empty one. A missing or damaged state file must never fail a
    /// run — same contract as <c>BaselineService.Load</c>, and for the same reason: the ledger
    /// is an optimisation for humans, not a correctness input.
    /// </summary>
    public static EntropyLedgerFile Load(string ledgerPath)
    {
        if (string.IsNullOrEmpty(ledgerPath) || !File.Exists(ledgerPath))
        {
            return new EntropyLedgerFile(CurrentVersion, []);
        }

        try
        {
            var json = File.ReadAllText(ledgerPath);
            var ledger = JsonSerializer.Deserialize(json, JsonReportSerializerContext.Default.EntropyLedgerFile);
            return ledger?.Entries is { } entries
                ? new EntropyLedgerFile(
                    string.IsNullOrWhiteSpace(ledger.Version) ? CurrentVersion : ledger.Version,
                    entries)
                : new EntropyLedgerFile(CurrentVersion, []);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new EntropyLedgerFile(CurrentVersion, []);
        }
    }

    /// <summary>
    /// Inserts or replaces the entry for <paramref name="entry"/>'s commit, then rewrites the
    /// file in canonical order.
    /// </summary>
    public static void Append(string ledgerPath, EntropyLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrEmpty(entry.CommitSha))
        {
            throw new ArgumentException("A ledger entry requires a commit SHA.", nameof(entry));
        }

        var parentDirectory = Path.GetDirectoryName(ledgerPath);
        if (!string.IsNullOrEmpty(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        var existing = Load(ledgerPath);
        var entries = new List<EntropyLedgerEntry>(existing.Entries.Length + 1);

        var replaced = false;
        foreach (var candidate in existing.Entries)
        {
            if (string.Equals(candidate.CommitSha, entry.CommitSha, StringComparison.Ordinal))
            {
                // Replace in place rather than appending, so re-running one commit is idempotent.
                entries.Add(entry);
                replaced = true;
            }
            else
            {
                entries.Add(candidate);
            }
        }

        if (!replaced)
        {
            entries.Add(entry);
        }

        entries.Sort(static (left, right) => string.CompareOrdinal(left.CommitSha, right.CommitSha));

        var json = JsonSerializer.Serialize(
            new EntropyLedgerFile(CurrentVersion, [.. entries]),
            JsonReportSerializerContext.Default.EntropyLedgerFile);

        File.WriteAllText(ledgerPath, json);
    }

    /// <summary>
    /// Aggregates entries into monthly rates, ordered chronologically.
    ///
    /// This is the aggregate worth acting on. The per-run rate is bounded by the scoring floor
    /// precisely because its denominator is small; a month of changes is not.
    /// </summary>
    public static IReadOnlyList<EntropyMonthlyRate> MonthlyRates(EntropyLedgerFile ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        var months = new SortedDictionary<string, (int New, int Resolved, int Lines)>(StringComparer.Ordinal);

        foreach (var entry in ledger.Entries)
        {
            if (TryMonth(entry.RecordedAtUtc, out var month))
            {
                var accumulator = months.TryGetValue(month, out var existing)
                    ? existing
                    : (New: 0, Resolved: 0, Lines: 0);

                months[month] = (
                    accumulator.New + entry.NewFindings,
                    accumulator.Resolved + entry.ResolvedFindings,
                    accumulator.Lines + Math.Max(entry.LinesChanged, 0));
            }
        }

        var rates = new List<EntropyMonthlyRate>(months.Count);
        foreach (var (month, totals) in months)
        {
            rates.Add(new EntropyMonthlyRate(
                Month: month,
                NewFindings: totals.New,
                ResolvedFindings: totals.Resolved,
                LinesChanged: totals.Lines,
                FindingsPerKloc: totals.Lines > 0 ? totals.New * 1000.0 / totals.Lines : null));
        }

        return rates;
    }

    /// <summary>Extracts the <c>yyyy-MM</c> prefix of an ISO-8601 timestamp.</summary>
    private static bool TryMonth(string recordedAtUtc, out string month)
    {
        month = string.Empty;
        if (string.IsNullOrEmpty(recordedAtUtc) || recordedAtUtc.Length < 7)
        {
            return false;
        }

        var candidate = recordedAtUtc[..7];
        if (!DateTime.TryParseExact(
                candidate,
                "yyyy-MM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            return false;
        }

        month = candidate;
        return true;
    }
}
