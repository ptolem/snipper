namespace Snipper.Analysis;

using System.Diagnostics;

/// <summary>
/// The single place Snipper shells out to git.
///
/// Extracted because the plumbing is easy to get subtly wrong and because **4C exists precisely
/// to catch two copies of it drifting apart**. That is not hypothetical: this class was created
/// after Snipper's own audit reported that the stderr-drain fix had been applied to
/// <c>GitMetadata.RunGit</c> and not to the copy in <c>GitHistory</c> — the exact one-sided-fix
/// pattern SNP0032 reports. Two copies of this method would have become two more.
///
/// Every failure degrades to null: no git, no repository, an unresolvable revision, a timeout.
/// A linter that crashes because a directory is not a checkout is worse than one that says nothing.
/// </summary>
internal static class GitProcess
{
    /// <summary>Generous enough for a large repository's history walk, short enough to not hang CI.</summary>
    private const int DefaultTimeoutMilliseconds = 30_000;

    /// <summary>
    /// Runs git in <paramref name="directory"/> and returns stdout, or null on any failure.
    /// Arguments are passed discretely, so a path containing a space or quote cannot corrupt the
    /// command line.
    /// </summary>
    public static string? Run(
        string directory,
        IReadOnlyList<string> arguments,
        int timeoutMilliseconds = DefaultTimeoutMilliseconds)
    {
        try
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            // Both pipes must be drained before waiting. A child that fills the stderr buffer while
            // we are blocked reading stdout will never exit, and the only symptom would be a
            // spurious timeout on a large repository.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMilliseconds))
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

            // Observe the stderr task too, so an unread pipe cannot surface as an unobserved
            // exception on the finalizer thread.
            _ = errorTask.IsCompletedSuccessfully ? errorTask.Result : null;

            return process.ExitCode == 0 ? outputTask.Result : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>Runs git and returns stdout with surrounding whitespace removed, or null.</summary>
    public static string? RunTrimmed(string directory, IReadOnlyList<string> arguments)
    {
        var output = Run(directory, arguments);
        return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
    }
}
