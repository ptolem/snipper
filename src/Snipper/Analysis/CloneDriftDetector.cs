namespace Snipper.Analysis;

using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using Snipper.Models;

/// <summary>Certainty of a clone-drift finding.</summary>
internal enum CloneDriftTier : byte
{
    /// <summary>A one-sided change that reads as a defensive fix.</summary>
    High = 1,

    /// <summary>A one-sided change that does not read as a fix: drift worth looking at.</summary>
    Advisory = 2,
}

/// <summary>
/// The 4C decision, isolated from git and from analysis so it can be tested directly.
///
/// Two facts carry a finding, and both must hold:
///
/// - **HEAD proves the copies are meant to be in sync.** SNP0031 proved token-identity over the
///   region, so divergence is a departure from a demonstrated invariant rather than a guess.
/// - **The patch proves they were out of sync at commit C.** C changed one copy's region and no
///   sibling. That is what makes it *temporal* rather than merely "these two files differ".
///
/// <c>competitive-analysis.md</c> §6.1 additionally requires that "the other copies are
/// byte-identical to the pre-change text". That clause was written for a present-tense framing
/// which cannot exist — a copy that received a fix stops being a clone and leaves the set — so
/// HEAD-identity stands in for it, and is the stronger of the two: it is a proof over the whole
/// region rather than a single commit's parent.
/// </summary>
internal static class CloneDriftClassifier
{
    /// <summary>
    /// Tier for a one-sided change, or null when the change was not drift at all.
    /// </summary>
    public static CloneDriftTier? Classify(bool oneSided, bool fixShaped)
    {
        if (!oneSided)
        {
            return null;
        }

        return fixShaped ? CloneDriftTier.High : CloneDriftTier.Advisory;
    }

    /// <summary>
    /// Whether a hunk falls inside a clone region, tolerating one line of adjacency.
    ///
    /// The canonical drift shape is a guard inserted immediately above a copied block, and git
    /// reports that as touching the line *before* the region. Requiring strict containment would
    /// miss precisely the case the rule exists for.
    /// </summary>
    public static bool HunkTouchesRegion(PatchHunk hunk, int regionStart, int regionEnd)
    {
        ArgumentNullException.ThrowIfNull(hunk);

        var hunkStart = hunk.NewCount == 0 ? hunk.NewStart : hunk.NewStart;
        var hunkEnd = hunkStart + Math.Max(hunk.NewCount, 1) - 1;

        return hunkEnd >= regionStart - 1 && hunkStart <= regionEnd + 1;
    }
}

/// <summary>
/// Recognises a defensive fix in a change's added lines, per <c>competitive-analysis.md</c> §6.1's
/// null/guard/try/catch/exception/bounds list.
///
/// Deliberately conservative. A single marker promotes a change to fix-shaped; a looser rule would
/// manufacture High findings, and a drift rule that cries wolf is a drift rule that gets switched
/// off. Kept separate from <see cref="GitHistory"/> because this is a judgement about code, not
/// about a patch.
/// </summary>
internal static class DefensiveFixMarkers
{
    private static readonly FrozenSet<string> Markers = new[]
    {
        "null", "IsNullOrEmpty", "IsNullOrWhiteSpace", "??", "?.", "try", "catch", "finally",
        "throw", "ArgumentNullException", "ArgumentOutOfRangeException",
        "NullReferenceException", "InvalidOperationException", "Length", "Count",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether the added lines read as a defensive fix. A pure deletion carries no added text and
    /// is never fix-shaped — calling that a fix would be backwards.
    ///
    /// Matching is substring and case-insensitive, which is looser than "conservative" sounds and
    /// is a deliberate trade: <c>null</c> alone matches <c>Nullable</c>. The looseness only ever
    /// promotes a change to fix-shaped, and it is gated by the one-sided requirement, which is the
    /// real filter. Tightening the matcher would cost recall without buying precision.
    /// </summary>
    public static bool IsFixShaped(IReadOnlyList<string> addedLines)
    {
        ArgumentNullException.ThrowIfNull(addedLines);

        foreach (var line in addedLines)
        {
            foreach (var marker in Markers)
            {
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>
/// Detects the temporal one-sided fix across a solution's clone sets (Wave 4 / 4C).
///
/// Scope is deliberately narrow and is stated rather than implied: **the most recent commit
/// touching each copy.** For such a commit the post-image is HEAD, so the hunk's coordinates and
/// the clone region's coordinates are one system and no reconciliation is needed. Reconciling a
/// patch's pre-image against a HEAD-anchored region across arbitrary history is where this design
/// would otherwise go wrong, and a plausible-looking bug there would produce confident nonsense.
/// The cost is recall — a one-sided fix followed by unrelated later edits to the same file is
/// missed — and widening it is a follow-up, not a redesign.
/// </summary>
public sealed class CloneDriftDetector
{
    private readonly string _repositoryRoot;

    public CloneDriftDetector(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        _repositoryRoot = repositoryRoot;
    }

    /// <summary>Drift findings for the given clone sets, deterministically ordered.</summary>
    internal IReadOnlyList<SnipperFinding> Detect(IReadOnlyList<CloneSet> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);

        if (sets.Count == 0)
        {
            return [];
        }

        var memberPaths = sets
            .SelectMany(set => set.Members)
            .Select(member => ToRepositoryRelative(member.Path))
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // One call answers "most recent commit per copy" for every member of every set.
        var mostRecent = GitHistory.TryGetMostRecentCommits(_repositoryRoot, memberPaths);
        if (mostRecent is null || mostRecent.Count == 0)
        {
            return [];
        }

        var wantedShas = mostRecent.Values
            .Select(commit => commit.Sha)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var patches = GitHistory.TryGetPatches(_repositoryRoot, wantedShas)
            .ToDictionary(patch => patch.Sha, StringComparer.Ordinal);

        var findings = new List<SnipperFinding>();

        foreach (var set in sets)
        {
            Evaluate(set, mostRecent, patches, findings);
        }

        // Overlapping clone sets are normal — SNP0031 reports a maximal run and its sub-runs, so the
        // same two files can appear in several sets. Deduplicating on (commit, copy) stops one
        // inconsistency being reported two or three times over, which is how a finding loses the
        // reader's trust. The first occurrence in the deterministic set order wins.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduplicated = new List<SnipperFinding>();

        foreach (var finding in findings
            .OrderBy(f => f.FilePath, StringComparer.Ordinal)
            .ThenBy(f => f.LineNumber)
            .ThenBy(f => f.Message, StringComparer.Ordinal))
        {
            if (seen.Add($"{CommitOf(finding)}|{finding.FilePath}"))
            {
                deduplicated.Add(finding);
            }
        }

        return deduplicated;
    }

    /// <summary>
    /// The commit a finding names, taken from its message. Findings are keyed by commit rather
    /// than by line because two overlapping sets can report the same commit at different lines.
    /// </summary>
    private static string CommitOf(SnipperFinding finding)
    {
        const string marker = "commit ";
        var start = finding.Message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return finding.Message;
        }

        start += marker.Length;
        var length = finding.Message.IndexOf(' ', start);
        return length < 0 ? finding.Message[start..] : finding.Message[start..length];
    }

    private void Evaluate(
        CloneSet set,
        IReadOnlyDictionary<string, MostRecentCommit> mostRecent,
        IReadOnlyDictionary<string, CommitPatch> patches,
        List<SnipperFinding> findings)
    {
        foreach (var member in set.Members)
        {
            var relative = ToRepositoryRelative(member.Path);
            if (!mostRecent.TryGetValue(relative, out var commit)
                || !patches.TryGetValue(commit.Sha, out var patch)
                || !patch.HunksByPath.TryGetValue(relative, out var hunks))
            {
                continue;
            }

            var siblingPaths = set.Members
                .Where(other => !string.Equals(other.Path, member.Path, StringComparison.Ordinal))
                .Select(other => ToRepositoryRelative(other.Path))
                .ToList();

            // One-sidedness is judged from the history index, which already knows every path the
            // commit touched; no sibling patch needs to be fetched to decide it.
            var oneSided = siblingPaths.All(path => !commit.Paths.Contains(path));

            foreach (var hunk in hunks)
            {
                if (!CloneDriftClassifier.HunkTouchesRegion(hunk, member.StartLine, member.EndLine))
                {
                    continue;
                }

                var fixShaped = DefensiveFixMarkers.IsFixShaped(hunk.AddedLines);
                var tier = CloneDriftClassifier.Classify(oneSided, fixShaped);
                if (tier is null)
                {
                    continue;
                }

                // A commit that brought this copy *into line* with a sibling fixed earlier is the
                // resolution of an inconsistency, not a new one. Reporting both links of a chain
                // would double every finding and bury the one that matters. Creation commits are
                // excluded: a commit that adds a file has not "fixed" a clone.
                if (SiblingWasFixedEarlier(set, member, commit, mostRecent, patches))
                {
                    continue;
                }

                var siblings = DescribeSiblings(set, member);

                // Whether the sibling was eventually brought in line, and which commit did it.
                // This dates the window in which the copies were out of sync — the actionable half
                // of the finding — or shows that no equivalent change ever arrived.
                var caughtUp = siblingPaths
                    .Select(path => mostRecent.TryGetValue(path, out var later) ? later : null)
                    .Where(later => later is not null && later.Order < commit.Order)
                    .Select(later => $"{later!.Sha[..7]} on {later.Date:yyyy-MM-dd}")
                    .ToList();

                var outcome = caughtUp.Count > 0
                    ? $"an equivalent change reached the sibling later ({string.Join("; ", caughtUp)})"
                    : "no equivalent change to the sibling appears in later history";

                var message = new StringBuilder()
                    .Append(tier == CloneDriftTier.High ? "One-sided defensive fix" : "One-sided change")
                    .Append(" in a clone set of ")
                    .Append(set.Members.Count)
                    .Append(" copies: commit ")
                    .Append(commit.Sha[..7])
                    .Append(" (\"")
                    .Append(commit.Message)
                    .Append("\", ")
                    .Append(commit.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    .Append(") changed this copy without touching ")
                    .Append(siblings)
                    .Append("; ")
                    .Append(outcome)
                    .Append('.')
                    .ToString();

                findings.Add(new SnipperFinding(
                    RuleId: "SNP0032",
                    Title: "Clone Set Fix Drift",
                    Message: message,
                    Certainty: tier == CloneDriftTier.High
                        ? CertaintyTier.High
                        : CertaintyTier.Advisory,
                    Category: FindingCategory.CloneDrift,
                    FilePath: member.Path,
                    LineNumber: member.StartLine,
                    CharacterOffset: member.StartCharacter,
                    Symbol: null));
            }
        }
    }

    /// <summary>
    /// Whether a sibling copy of this set was itself changed — at a region it shares with this
    /// member — by an *older* commit. That makes the current commit a catch-up rather than drift.
    ///
    /// Only hunks that modify existing lines count. The commit that created a file reports
    /// <c>@@ -0,0 +1,N @@</c>, whose pre-image is empty; treating that as "the sibling was fixed
    /// earlier" would suppress every finding in any repository whose clone set pre-dates the fix.
    /// </summary>
    private bool SiblingWasFixedEarlier(
        CloneSet set,
        CloneSetMember member,
        MostRecentCommit commit,
        IReadOnlyDictionary<string, MostRecentCommit> mostRecent,
        IReadOnlyDictionary<string, CommitPatch> patches)
    {
        foreach (var sibling in set.Members)
        {
            if (string.Equals(sibling.Path, member.Path, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = ToRepositoryRelative(sibling.Path);

            // Only a sibling that was fixed *before* this commit makes this commit a catch-up.
            // Order is newest-first, so "before" means a larger order. A sibling fixed after this
            // commit is the other half of the drift and must not suppress it.
            if (!mostRecent.TryGetValue(relative, out var siblingCommit)
                || siblingCommit.Order <= commit.Order
                || !patches.TryGetValue(siblingCommit.Sha, out var patch)
                || !patch.HunksByPath.TryGetValue(relative, out var hunks))
            {
                continue;
            }

            foreach (var hunk in hunks)
            {
                if (hunk.OldCount > 0
                    && CloneDriftClassifier.HunkTouchesRegion(hunk, sibling.StartLine, sibling.EndLine))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string DescribeSiblings(CloneSet set, CloneSetMember member)
    {
        var siblings = set.Members
            .Where(other => !string.Equals(other.Path, member.Path, StringComparison.Ordinal))
            .Select(other => $"{Path.GetFileName(other.Path)}:{other.StartLine}")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        return string.Join(", ", siblings);
    }

    private string ToRepositoryRelative(string path) =>
        Path.GetRelativePath(_repositoryRoot, path).Replace('\\', '/');
}

/// <summary>One copy of a clone set, as 4C needs it.</summary>
internal sealed record CloneSetMember(string Path, int StartLine, int StartCharacter, int EndLine);

/// <summary>A group of copies proven token-identical by SNP0031.</summary>
internal sealed record CloneSet(IReadOnlyList<CloneSetMember> Members);
