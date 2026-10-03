namespace Snipper.Cli;

using System.Diagnostics;

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

            var addedText = line[..firstTab].Trim();
            var deletedText = line[(firstTab + 1)..secondTab].Trim();

            // Binary entries are "-" in both columns; skipping them is the point.
            if (!int.TryParse(addedText, out var added) || !int.TryParse(deletedText, out var deleted))
            {
                continue;
            }

            var path = line[(secondTab + 1)..].Trim();
            if (path.Length > 0)
            {
                entries.Add(new GitNumstatEntry(path, added, deleted));
            }
        }

        return entries;
    }

    private static string? RunGit(string directory, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            // Both pipes must be drained before waiting. A child that fills the stderr buffer
            // while we are blocked reading stdout will never exit, and the only symptom would
            // be a spurious 10s timeout on a large repository. Measured on a 39-commit repo,
            // `status --porcelain` is cheap, but 4B now invokes git on every baseline run.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(milliseconds: 10_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return null;
            }

            // Observe both tasks so an unread pipe cannot surface as an unobserved exception.
            _ = errorTask.IsCompletedSuccessfully ? errorTask.Result : null;

            return process.ExitCode == 0 ? outputTask.Result : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// One file's line-level change between two revisions, as reported by <c>git diff --numstat</c>.
/// Binary files are omitted by <see cref="GitMetadata.TryGetNumstat"/>.
/// </summary>
/// <param name="Path">Repository-relative path, using forward slashes as git emits it.</param>
/// <param name="Added">Lines added.</param>
/// <param name="Deleted">Lines deleted.</param>
internal sealed record GitNumstatEntry(string Path, int Added, int Deleted);
