namespace Snipper.Analysis;

using System.Collections.Frozen;
using System.Globalization;

/// <summary>One contiguous change region from a unified diff.</summary>
/// <param name="OldStart">First line in the pre-image (1-based).</param>
/// <param name="OldCount">Lines consumed in the pre-image.</param>
/// <param name="NewStart">First line in the post-image (1-based).</param>
/// <param name="NewCount">Lines produced in the post-image.</param>
/// <param name="AddedLines">Lines the change introduces, without the leading '+'.</param>
/// <param name="RemovedLines">Lines the change deletes, without the leading '-'. Used to tell a
/// rename apart from a behavioural change.</param>
/// <param name="ChangedNewLines">Post-image lines the hunk actually changed, in order.
/// <paramref name="NewStart"/> points at the hunk's first line, which git fills with unchanged
/// context, so it is not a usable anchor. Every changed line is kept rather than only the first: a
/// hunk can begin just before the region a finding is about, and the caller needs to be able to
/// choose the change that lands inside it. Null when the hunk changed nothing.</param>
internal sealed record PatchHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    string[] AddedLines,
    string[] RemovedLines,
    int[]? ChangedNewLines = null,
    bool IsFileCreation = false);

/// <summary>The most recent commit to touch one path.</summary>
/// <param name="Sha">Full commit SHA.</param>
/// <param name="Date">Author date, for reporting the inconsistency window.</param>
/// <param name="Message">Subject line.</param>
/// <param name="Paths">Every path the commit touched, so one-sidedness can be judged.</param>
/// <param name="Order">
/// Position in git log's newest-first output; smaller is newer. Ordering is taken from this and
/// never from <paramref name="Date"/>, because git timestamps have one-second resolution and two
/// commits in the same second tie — which silently broke "the sibling caught up later".
/// </param>
internal sealed record MostRecentCommit(
    string Sha,
    DateTimeOffset Date,
    string Message,
    FrozenSet<string> Paths,
    int Order);

/// <summary>A commit's hunks, keyed by repository-relative path.</summary>
/// <param name="Sha">Full commit SHA.</param>
/// <param name="Date">Author date.</param>
/// <param name="HunksByPath">Hunks per path, in post-image coordinates.</param>
internal sealed record CommitPatch(
    string Sha,
    DateTimeOffset Date,
    IReadOnlyDictionary<string, IReadOnlyList<PatchHunk>> HunksByPath);

/// <summary>
/// Read-only git history access for Wave 4 / 4C.
///
/// Every failure mode — no git, no repository, a bad revision, a timeout — degrades to null or
/// empty, never to an exception. Same contract as <c>GitMetadata</c>: a linter that crashes
/// because a directory is not a checkout is worse than one that says nothing.
///
/// Process-spawn cost is the whole design constraint. Measured on a 39-commit repository a
/// per-path <c>git log</c> costs ~90 ms, so per-member history lookups would swamp a run that
/// already spends ~53 s shingling. Both entry points therefore issue one git call per *batch of
/// paths*, which makes 4C's git cost independent of how many clone sets exist.
/// </summary>
internal static class GitHistory
{
    /// <summary>Paths per git invocation, to stay clear of the command-line length limit.</summary>
    private const int PathsPerCall = 256;

    /// <summary>
    /// The most recent commit touching each of <paramref name="paths"/>. A path with no history
    /// is simply absent. Returns null only when the repository itself could not be read.
    /// </summary>
    public static IReadOnlyDictionary<string, MostRecentCommit>? TryGetMostRecentCommits(
        string? directory,
        IReadOnlyList<string> paths)
    {
        if (string.IsNullOrEmpty(directory) || paths.Count == 0 || !Directory.Exists(directory))
        {
            return null;
        }

        var result = new Dictionary<string, MostRecentCommit>(StringComparer.Ordinal);
        var anyCallSucceeded = false;
        var nextOrder = 0;

        foreach (var chunk in Chunk(paths, PathsPerCall))
        {
            var output = GitProcess.Run(directory, ["log", "--no-merges", "--format=%x01%H%x02%aI%x02%s", "--name-only", "--", .. chunk]);
            if (output is null)
            {
                continue;
            }

            anyCallSucceeded = true;
            MergeMostRecent(result, output, chunk, ref nextOrder);
        }

        return anyCallSucceeded ? result : null;
    }

    /// <summary>Patches for the given commits, in one git call, keyed by SHA then by path.</summary>
    public static IReadOnlyList<CommitPatch> TryGetPatches(string? directory, IReadOnlyList<string> shas)
    {
        if (string.IsNullOrEmpty(directory) || shas.Count == 0 || !Directory.Exists(directory))
        {
            return [];
        }

        // --no-walk keeps this to exactly the requested commits. The record separator is what
        // makes each patch attributable; a bare multi-rev `git show` concatenates them.
        var output = GitProcess.Run(
            directory,
            ["log", "-p", "--no-walk=unsorted", "--no-merges", "--format=%x01%H%x02%aI", "--unified=0", .. shas]);
        if (output is null)
        {
            return [];
        }

        var patches = new List<CommitPatch>();

        foreach (var record in output.Split('\u0001', StringSplitOptions.RemoveEmptyEntries))
        {
            var (sha, date, hunks) = ParseCommitRecord(record);
            if (hunks.Count > 0)
            {
                patches.Add(new CommitPatch(sha, date, hunks));
            }
        }

        return patches;
    }

    private static void MergeMostRecent(
        Dictionary<string, MostRecentCommit> result,
        string logOutput,
        IReadOnlyList<string> chunk,
        ref int nextOrder)
    {
        foreach (var record in logOutput.Split('\u0001', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = record.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2)
            {
                continue;
            }

            var header = lines[0].TrimEnd('\r');
            var firstSeparator = header.IndexOf('\u0002');
            var secondSeparator = firstSeparator < 0
                ? -1
                : header.IndexOf('\u0002', firstSeparator + 1);
            if (secondSeparator < 0)
            {
                continue;
            }

            var sha = header[..firstSeparator];
            if (!TryParseDate(header[(firstSeparator + 1)..secondSeparator], out var date))
            {
                continue;
            }

            var message = header[(secondSeparator + 1)..].Trim();

            // Everything after the subject line is one changed path per line.
            var paths = new List<string>(lines.Length - 1);
            for (var index = 1; index < lines.Length; index++)
            {
                paths.Add(lines[index].Trim());
            }

            var touched = paths.ToFrozenSet(StringComparer.Ordinal);

            // git log is newest-first, so the first commit seen for a path is the most recent.
            foreach (var path in chunk)
            {
                if (!result.ContainsKey(path) && touched.Contains(path))
                {
                    result[path] = new MostRecentCommit(sha, date, message, touched, nextOrder++);
                }
            }
        }
    }

    private static (string Sha, DateTimeOffset Date, IReadOnlyDictionary<string, IReadOnlyList<PatchHunk>> Hunks)
        ParseCommitRecord(string record)
    {
        var lines = record.Split('\n');
        var header = lines.Length > 0 ? lines[0].TrimEnd('\r') : string.Empty;
        var separator = header.IndexOf('\u0002');

        if (separator < 0 || !TryParseDate(header[(separator + 1)..], out var date))
        {
            return (string.Empty, default, new Dictionary<string, IReadOnlyList<PatchHunk>>(StringComparer.Ordinal));
        }

        var sha = header[..separator];
        var body = string.Join('\n', lines[1..]);
        return (sha, date, ParsePatchesByPath(body));
    }

    /// <summary>
    /// Splits a possibly multi-file patch into hunks per path, attributing each hunk's added
    /// lines. Walks the text once rather than slicing per file, so an unterminated final section
    /// cannot swallow the next commit's hunks.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<PatchHunk>> ParsePatchesByPath(string patchText)
    {
        var byPath = new Dictionary<string, List<PatchHunk>>(StringComparer.Ordinal);
        string? path = null;
        var added = new List<string>();
        var removed = new List<string>();

        // Whether the section now being walked added its file. Set from git's own
        // "new file mode" / "--- /dev/null" header rather than inferred from the hunk
        // coordinates: a creation and a pure insertion both consume nothing from the
        // pre-image ("@@ -0,0 +1,N @@" and "@@ -17,0 +18,4 @@"), so OldCount cannot
        // tell them apart, and 4C needs to. Reset per file, on "diff --git" - which
        // precedes "new file mode" and therefore "+++ b/" - and not on "+++ b/".
        var isFileCreation = false;

        // Post-image lines the open hunk really changed, and the running post-image position as
        // the hunk body is walked. Context advances the position but changes nothing.
        var changed = new List<int>();
        var newImageLine = 0;

        PatchHunk? open = null;

        foreach (var raw in patchText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                if (open is { } previous && path is not null)
                {
                    Append(byPath, path, Build(previous, added, removed, changed, isFileCreation));
                }

                added.Clear();
                removed.Clear();
                changed.Clear();
                open = null;
                path = line["+++ b/".Length..].Trim();
                continue;
            }

            if (line.StartsWith("new file mode", StringComparison.Ordinal)
                || line.StartsWith("--- /dev/null", StringComparison.Ordinal))
            {
                isFileCreation = true;
                continue;
            }

            if (line.StartsWith("diff --git", StringComparison.Ordinal))
            {
                // A deletion has no "+++ b/" line, so close the section on the header instead.
                // Built *before* the flag is reset below: this hunk belongs to the section that
                // is ending, so it must keep that section's creation verdict. Resetting first
                // silently unmarked the last hunk of every added file.
                if (open is { } closing && path is not null)
                {
                    Append(byPath, path, Build(closing, added, removed, changed, isFileCreation));
                }

                // A new file section begins, so the previous section's verdict is void.
                // Reset here rather than on "+++ b/": git emits "new file mode" *before*
                // the "+++ b/" line, so clearing it there would lose the evidence.
                isFileCreation = false;

                added.Clear();
                removed.Clear();
                changed.Clear();
                open = null;
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (open is { } finished && path is not null)
                {
                    Append(byPath, path, Build(finished, added, removed, changed, isFileCreation));
                }

                added.Clear();
                removed.Clear();
                changed.Clear();
                open = TryParseHunkHeader(line, out var hunk) ? hunk : null;
                newImageLine = open?.NewStart ?? 0;
                continue;
            }

            if (open is null)
            {
                continue;
            }

            if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                added.Add(line[1..]);
                changed.Add(newImageLine);
                newImageLine++;
            }
            else if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))
            {
                removed.Add(line[1..]);
                changed.Add(newImageLine);
            }
            else if (line.StartsWith(' '))
            {
                // Context advances the post-image position but changes nothing.
                newImageLine++;
            }
        }

        if (open is { } last && path is not null)
        {
            Append(byPath, path, Build(last, added, removed, changed, isFileCreation));
        }

        return byPath.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<PatchHunk>)entry.Value,
            StringComparer.Ordinal);
    }

    private static void Append(Dictionary<string, List<PatchHunk>> byPath, string path, PatchHunk hunk)
    {
        if (!byPath.TryGetValue(path, out var list))
        {
            list = [];
            byPath[path] = list;
        }

        list.Add(hunk);
    }

        private static PatchHunk Build(
            PatchHunk open, List<string> added, List<string> removed, List<int> changed,
            bool isFileCreation) =>
            open with
            {
                AddedLines = [.. added],
                RemovedLines = [.. removed],
                ChangedNewLines = changed.Count > 0 ? [.. changed] : null,
                IsFileCreation = isFileCreation,
            };

    /// <summary>Parses <c>@@ -old,count +new,count @@</c>, tolerating an omitted count of 1.</summary>
    private static bool TryParseHunkHeader(string line, out PatchHunk hunk)
    {
        hunk = new PatchHunk(0, 0, 0, 0, [], []);

        var close = line.IndexOf("@@", 2, StringComparison.Ordinal);
        if (close < 0)
        {
            return false;
        }

        var parts = line[2..close].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2
            || !TryParseRange(parts[0], '-', out var oldStart, out var oldCount)
            || !TryParseRange(parts[1], '+', out var newStart, out var newCount))
        {
            return false;
        }

        hunk = new PatchHunk(oldStart, oldCount, newStart, newCount, [], []);
        return true;
    }

    private static bool TryParseRange(string token, char marker, out int start, out int count)
    {
        start = count = 0;

        if (token.Length < 2 || token[0] != marker)
        {
            return false;
        }

        var comma = token.IndexOf(',', StringComparison.Ordinal);
        var startText = comma < 0 ? token[1..] : token[1..comma];
        var countText = comma < 0 ? "1" : token[(comma + 1)..];

        return int.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out start)
            && int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out count);
    }

    private static bool TryParseDate(string text, out DateTimeOffset date) =>
        DateTimeOffset.TryParse(
            text.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out date);

    private static IEnumerable<string[]> Chunk(IReadOnlyList<string> paths, int size)
    {
        for (var offset = 0; offset < paths.Count; offset += size)
        {
            var length = Math.Min(size, paths.Count - offset);
            var chunk = new string[length];
            for (var index = 0; index < length; index++)
            {
                chunk[index] = paths[offset + index];
            }

            yield return chunk;
        }
    }

}
