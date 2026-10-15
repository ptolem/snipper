namespace Snipper.Tests;

using System.Text.Json;
using FluentAssertions;
using Snipper.Cli;
using Xunit;

// Spectre.Console's Status has a process-wide exclusivity mode: two CliRunner.RunAsync
// calls cannot overlap in one test host, so this class is serialised with the other
// end-to-end CLI suites.
[Collection("CliRuns")]
/// <summary>
/// Wave 4 / 4A: counting what suppression hides, end to end.
/// <para>
/// The unit tests in <c>SuppressionAuditShould</c> pin the attribution arithmetic, and the
/// end-to-end tests in <c>CliRunnerShould</c> pin the <i>unconfigured</i> case — where
/// <c>shadowAnalysisRan</c> is correctly false. Neither exercised a real namespace
/// exclusion reaching the audit, which left a hole: when this was a lifted second pass, one
/// that was not actually lifted reported zero hidden findings and no test noticed.
/// </para>
/// <para>
/// That is the failure this file exists to catch, whatever the mechanism. Namespace
/// exclusion is now a filter over the findings of the one analysis pass; if what it hides
/// stops reaching the audit, hidden findings silently become zero — the audit keeps working,
/// the JSON stays valid, and the only symptom is a wrong number.
/// </para>
/// </summary>
public sealed class ShadowPassShould : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("snipper-shadow").FullName;
    private readonly string _project;

    public ShadowPassShould()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "TestAssets", "SampleApp");
        CopyDirectory(source, _workspace);
        _project = Path.Combine(_workspace, "App", "App.csproj");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a leaked temp directory must never fail a test run.
        }
    }

    private const string ExcludeShadowing =
        """
        { "version": 1, "exclude": { "namespaces": ["CoreLib.Shadowing"] } }
        """;

    private const string ExcludeTwoNamespaces =
        """
        { "version": 1, "exclude": { "namespaces": ["CoreLib.Shadowing", "CoreLib.Controls"] } }
        """;

    private async Task<JsonElement> AuditAsync(string? configJson)
    {
        var configPath = Path.Combine(_workspace, "snipper.json");
        if (configJson is null)
        {
            File.Delete(configPath);
        }
        else
        {
            await File.WriteAllTextAsync(configPath, configJson);
        }

        var report = Path.Combine(_workspace, $"report-{Guid.NewGuid():N}.json");
        var exitCode = await CliRunner.RunAsync([_project, report, "--audit-suppressions"]);
        exitCode.Should().Be(0);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        return document.RootElement.GetProperty("suppression").Clone();
    }

    [Fact]
    public async Task Run_No_Shadow_Pass_When_Nothing_Is_Suppressed()
    {
        // The control case: with no suppression configured there is nothing to shadow, and
        // the extra pass must not run. This is the behaviour the conditional exists for.
        var suppression = await AuditAsync(configJson: null);

        suppression.GetProperty("shadowAnalysisRan").GetBoolean().Should().BeFalse();
        suppression.GetProperty("totals").GetProperty("findingsHiddenByShadow").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Run_The_Shadow_Pass_And_Count_Hidden_Findings_For_An_Excluded_Namespace()
    {
        var suppression = await AuditAsync(ExcludeShadowing);

        // Both halves matter. shadowAnalysisRan says the namespace channel was measured; the
        // positive hidden count proves what the exclusion hid reached the audit and was
        // attributed to the namespace channel rather than dropped. Losing it would report
        // zero here and still look healthy.
        suppression.GetProperty("shadowAnalysisRan").GetBoolean().Should().BeTrue();
        suppression.GetProperty("totals").GetProperty("findingsHiddenByShadow").GetInt32()
            .Should().BeGreaterThan(0);

        var channel = suppression.GetProperty("namespaceExclusions").EnumerateArray().Should().ContainSingle().Subject;
        channel.GetProperty("selector").GetString().Should().Be("CoreLib.Shadowing");
        channel.GetProperty("suppressedCount").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Attribute_Hidden_Findings_To_The_Namespace_Channel_Not_To_A_Glob()
    {
        var suppression = await AuditAsync(ExcludeTwoNamespaces);

        // A namespace exclusion hides findings before the report-time filter sees them, so
        // its cost belongs in hidden-by-shadow. Charging it to findingsDropped instead would
        // double-count: the dropped total is computed from the report-time filter.
        suppression.GetProperty("totals").GetProperty("findingsDropped").GetInt32().Should().Be(0);
        suppression.GetProperty("totals").GetProperty("findingsHiddenByShadow").GetInt32()
            .Should().BeGreaterThan(0);

        // One row per namespace, each with its own count, and the rows account for every
        // hidden finding (no rule is disabled here, so nothing else is hidden).
        // CoreLib.Controls declares nothing in the fixture, so its row is a measured zero.
        var rows = suppression.GetProperty("namespaceExclusions").EnumerateArray()
            .ToDictionary(r => r.GetProperty("selector").GetString()!, r => r.GetProperty("suppressedCount").GetInt32());
        rows.Keys.Should().BeEquivalentTo(["CoreLib.Controls", "CoreLib.Shadowing"]);
        rows["CoreLib.Shadowing"].Should().BeGreaterThan(0);
        rows["CoreLib.Controls"].Should().Be(0);
        rows.Values.Sum().Should().Be(suppression.GetProperty("totals").GetProperty("findingsHiddenByShadow").GetInt32());
    }

    [Fact]
    public async Task Report_Hidden_Debt_As_A_Percentage_When_Something_Is_Hidden()
    {
        var suppression = await AuditAsync(ExcludeShadowing);

        // A non-zero hiddenDebtPercent is the number a reviewer watches. If hidden findings
        // were silently zero this would read 0 and look like a clean bill of health.
        suppression.GetProperty("totals").GetProperty("hiddenDebtPercent").GetInt32()
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Not_Run_The_Shadow_Pass_For_A_Path_Glob_Alone()
    {
        // exclude.paths applies at report time, so nothing is hidden before it and there is no
        // pre-report channel to measure. If this ever reports one, the conditioning has
        // regressed.
        var suppression = await AuditAsync(
            """
            { "version": 1, "exclude": { "paths": ["**/*.cs"] } }
            """);

        suppression.GetProperty("shadowAnalysisRan").GetBoolean().Should().BeFalse();
        suppression.GetProperty("totals").GetProperty("findingsHiddenByShadow").GetInt32().Should().Be(0);
        suppression.GetProperty("totals").GetProperty("findingsDropped").GetInt32().Should().BeGreaterThan(0);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}