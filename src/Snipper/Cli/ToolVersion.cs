namespace Snipper.Cli;

using System.Reflection;

/// <summary>
/// The tool's own version string, computed once from assembly metadata.
/// <para>
/// Its own type rather than a member of <see cref="CliRunner"/> because both the entry
/// point and the report writer need it, and a report writer that had to reach through
/// the orchestrator to ask "what version are you" would make the dependency graph
/// circular. Reading assembly metadata touches no MSBuild or workspace type, so it is
/// safe before <c>MSBuildLocator</c> registration.
/// </para>
/// </summary>
internal static class ToolVersion
{
    private static readonly string Value = Compute();

    public static string Current => Value;

    private static string Compute()
    {
        var assembly = typeof(ToolVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip the "+<commit>" SourceLink suffix when present.
            var plusIndex = informational.IndexOf('+', StringComparison.Ordinal);
            return plusIndex > 0 ? informational[..plusIndex] : informational;
        }

        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}