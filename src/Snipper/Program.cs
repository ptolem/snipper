namespace Snipper;

using Microsoft.Build.Locator;
using Spectre.Console;
using Snipper.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Started before anything else, including the MSBuild binding, so the reported
        // window covers the whole process. Null unless SNIPPER_PERF is set, and the
        // finally below is the only reason to care: every early return in CliRunner -
        // usage errors, unreadable targets, cancellation - still reports.
        var perf = PerfSummary.TryStart();

        try
        {
            // Version queries short-circuit before MSBuildLocator: the output stays
            // machine-clean (no SDK binding noise) and no MSBuild assemblies load.
            if (args.Any(static a => string.Equals(a, "--version", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase)))
            {
                AnsiConsole.WriteLine(CliRunner.GetToolVersion());
                return 0;
            }

            // MSBuildLocator MUST execute before referencing any MSBuild/Roslyn workspace types
            var instance = MSBuildLocator.RegisterDefaults();
            AnsiConsole.MarkupLine($"[grey]Bound to MSBuild SDK: {instance.Name} {instance.Version} ({instance.MSBuildPath})[/]");

            return await CliRunner.RunAsync(args).ConfigureAwait(false);
        }
        finally
        {
            perf?.Write();
        }
    }
}
