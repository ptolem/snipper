namespace Snipper.Cli;

using Snipper.Analysis;

/// <summary>
/// Best-effort git metadata for report stamping (FP-5, 1.6.1): the analysed
/// commit lets consumers detect drift between the report and their checkout,
/// and the dirty flag warns that uncommitted edits may already have shifted
/// line numbers. Every failure mode (no git, no repo, timeout) degrades to
/// null/false — never to a failed run.
/// </summary>
internal static class GitMetadata
{
    public static string? TryResolveCommitSha(string? directory, out bool workingTreeDirty)
    {
        workingTreeDirty = false;
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var sha = RunGit(directory, "rev-parse HEAD");
        if (string.IsNullOrWhiteSpace(sha))
        {
            return null;
        }

        workingTreeDirty = RunGit(directory, "status --porcelain") is { Length: > 0 };
        return sha.Trim();
    }

    /// <summary>
    /// The repository root containing <paramref name="directory"/>, or null when it is not inside
    /// a checkout. Used by 4C so history paths and analysed paths share a base.
    /// </summary>
    public static string? TryResolveRepositoryRoot(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var root = RunGit(directory, "rev-parse --show-toplevel");
        return string.IsNullOrWhiteSpace(root) ? null : root.Trim();
    }

    /// <summary>
    /// Per-file added/deleted line counts between two revisions, for the 4B entropy-rate
    /// denominator. Returns null on any failure, and skips binary files: numstat prints
    /// <c>-</c> for them, and counting a 4 MB asset as changed *lines* would make the
    /// denominator meaningless.
    /// </summary>
    public static IReadOnlyList<GitNumstatEntry>? TryGetNumstat(
        string? directory,
        string fromRevision,
        string toRevision)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var output = RunGit(directory, $"diff --numstat {fromRevision} {toRevision}");
        if (output is null)
        {
            return null;
        }

        var entries = new List<GitNumstatEntry>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "added\tdeleted\tpath"; a rename appends "=> old => new" braces, and the path may
            // itself contain a tab, so only the first two separators are structural.
            var firstTab = line.IndexOf('\t', StringComparison.Ordinal);
            if (firstTab < 0)
            {
                continue;
            }

            var secondTab = line.IndexOf('\t', firstTab + 1);
            if (secondTab < 0)
            {
                continue;
            }

            // The two count columns are sliced as spans and handed to int.TryParse's span
            // overload, so neither needs a Trim() string. Only the path is materialised, because
            // it is the one field that outlives this loop.
            var lineSpan = line.AsSpan();
            var addedText = lineSpan[..firstTab].Trim();
            var deletedText = lineSpan[(firstTab + 1)..secondTab].Trim();

            // Binary entries are "-" in both columns; skipping them is the point.
            if (!int.TryParse(addedText, out var added) || !int.TryParse(deletedText, out var deleted))
            {
                continue;
            }

            var path = lineSpan[(secondTab + 1)..].Trim().ToString();
            if (path.Length > 0)
            {
                entries.Add(new GitNumstatEntry(path, added, deleted));
            }
        }

        return entries;
    }

    private static string? RunGit(string directory, string arguments) =>
        GitProcess.RunTrimmed(directory, [.. arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)]);
}

/// <summary>
/// One file's line-level change between two revisions, as reported by <c>git diff --numstat</c>.
/// Binary files are omitted by <see cref="GitMetadata.TryGetNumstat"/>.
/// </summary>
/// <param name="Path">Repository-relative path, using forward slashes as git emits it.</param>
/// <param name="Added">Lines added.</param>
/// <param name="Deleted">Lines deleted.</param>
internal sealed record GitNumstatEntry(string Path, int Added, int Deleted);