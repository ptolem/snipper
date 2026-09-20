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

            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                }

                return null;
            }

            return process.ExitCode == 0 ? outputTask.Result : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
