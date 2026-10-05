namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Cli;
using Xunit;

// Spectre.Console's Status has a process-wide exclusivity mode: two CliRunner.RunAsync
// calls cannot overlap in one test host. Sharing a collection serialises this class
// against BaselineChurnShould, which also drives the CLI end to end.
[Collection("CliRuns")]
public sealed class CliRunnerShould
{
    [Fact]
    public async Task Return_One_When_No_Target_Path_Is_Supplied_For_RunAsync()
    {
        var exitCode = await CliRunner.RunAsync([]);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task Return_One_When_Target_File_Does_Not_Exist_For_RunAsync()
    {
        var exitCode = await CliRunner.RunAsync([Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.sln")]);

        exitCode.Should().Be(1);
    }

    /// <summary>
    /// A malformed <c>.slnx</c> used to escape as an unhandled <c>System.Xml.XmlException</c>. The
    /// open-path filter matched IO exceptions plus the two MSBuild invalid-file exceptions, and
    /// <c>XmlException</c> derives from <c>SystemException</c>, not <c>IOException</c>, so it reached
    /// none of those clauses. SolutionPersistence throws it from an <c>XmlDocument.Load</c> in the
    /// serializer's reader constructor and nothing above wraps, so the process died on a raw stack
    /// trace with exit -532462766 rather than the documented 1.
    /// <para>
    /// Asserting the exit code alone would not catch that, because an unhandled exception also exits
    /// non-zero - just the wrong way - which is why the assertion is on not throwing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Return_One_Rather_Than_Throwing_When_The_Solution_File_Is_Malformed_Xml_For_RunAsync()
    {
        var directory = Directory.CreateTempSubdirectory("snipper-malformed-slnx");
        try
        {
            var path = Path.Combine(directory.FullName, "Broken.slnx");
            // <Solution> is opened and never closed.
            await File.WriteAllTextAsync(path, "<Solution>\n  <Project Path=\"A.csproj\" />\n");

            var act = () => CliRunner.RunAsync([path]);

            (await act.Should().NotThrowAsync()).Which.Should().Be(1);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The same defect had a second shape: a <c>.slnx</c> that is <em>well-formed</em> XML with an
    /// invalid schema fails inside <c>SlnxFile.ToModel</c> with a <c>SolutionException</c>, which is
    /// not an IO type either. Valid XML is not a valid solution, and a wrong root element is the easy
    /// way to reach it.
    /// </summary>
    [Fact]
    public async Task Return_One_Rather_Than_Throwing_When_The_Solution_Has_An_Invalid_Schema_For_RunAsync()
    {
        var directory = Directory.CreateTempSubdirectory("snipper-bad-schema-slnx");
        try
        {
            var path = Path.Combine(directory.FullName, "WrongRoot.slnx");
            await File.WriteAllTextAsync(path, "<NotASolution><Project /></NotASolution>");

            var act = () => CliRunner.RunAsync([path]);

            (await act.Should().NotThrowAsync()).Which.Should().Be(1);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Return_One_When_Format_Value_Is_Invalid_For_RunAsync()
    {
        var target = Path.Combine(AppContext.BaseDirectory, "TestAssets", "SampleApp", "CoreLib", "CoreLib.csproj");

        var exitCode = await CliRunner.RunAsync([target, "--format", "yaml"]);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task Return_One_When_Certainty_Tier_Value_Is_Invalid_For_RunAsync()
    {
        var target = Path.Combine(AppContext.BaseDirectory, "TestAssets", "SampleApp", "CoreLib", "CoreLib.csproj");

        var exitCode = await CliRunner.RunAsync([target, "--certainty-tier", "cosmic"]);

        exitCode.Should().Be(1);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public async Task Return_Zero_When_Version_Is_Requested_For_RunAsync(string flag)
    {
        var exitCode = await CliRunner.RunAsync([flag]);

        exitCode.Should().Be(0);
    }

    [Fact]
    public void Return_The_Package_Version_For_GetToolVersion()
    {
        CliRunner.GetToolVersion().Should().MatchRegex(@"^\d+\.\d+\.\d+$");
    }

    [Fact]
    public async Task Return_One_When_Exclude_Namespaces_Value_Is_Missing_For_RunAsync()
    {
        var target = Path.Combine(AppContext.BaseDirectory, "TestAssets", "SampleApp", "CoreLib", "CoreLib.csproj");

        var exitCode = await CliRunner.RunAsync([target, "--exclude-namespaces"]);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task Omit_Duplicate_Findings_By_Default_For_RunAsync()
    {
        // SNP0031 is opt-in (--duplicate-detection). On a default run the rule
        // must not appear at all, which is what keeps every existing gate and
        // baseline stable for users who did not ask for clone detection.
        var target = Path.Combine(AppContext.BaseDirectory, "TestAssets", "SampleApp", "App", "App.csproj");
        var report = Path.Combine(Path.GetTempPath(), $"snipper-default-{Guid.NewGuid():N}.json");

        var exitCode = await CliRunner.RunAsync([target, report]);

        exitCode.Should().Be(0);
        ReadRuleIds(report).Should().NotContain("SNP0031");
    }

    [Fact]
    public async Task Include_Duplicate_Findings_When_Flag_Is_Passed_For_RunAsync()
    {
        var target = Path.Combine(AppContext.BaseDirectory, "TestAssets", "SampleApp", "App", "App.csproj");
        var report = Path.Combine(Path.GetTempPath(), $"snipper-duplicates-{Guid.NewGuid():N}.json");

        var exitCode = await CliRunner.RunAsync([target, report, "--duplicate-detection"]);

        exitCode.Should().Be(0);
        ReadRuleIds(report).Should().Contain("SNP0031");
    }

    [Fact]
    public async Task Omit_The_Suppression_Audit_By_Default_For_RunAsync()
    {
        // --audit-suppressions is opt-in and must cost nothing when absent: no shadow
        // pass runs and the report is byte-identical to a run without the feature.
        var target = Path.Combine(AppContext.BaseDirectory, "TestAssets", "SampleApp", "App", "App.csproj");
        var report = Path.Combine(Path.GetTempPath(), $"snipper-audit-off-{Guid.NewGuid():N}.json");

        var exitCode = await CliRunner.RunAsync([target, report]);

        exitCode.Should().Be(0);

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(report));
        document.RootElement.TryGetProperty("suppression", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Include_The_Suppression_Audit_When_The_Flag_Is_Passed_For_RunAsync()
    {
        var target = Path.Combine(AppContext.BaseDirectory, "TestAssets", "SampleApp", "App", "App.csproj");
        var report = Path.Combine(Path.GetTempPath(), $"snipper-audit-on-{Guid.NewGuid():N}.json");

        var exitCode = await CliRunner.RunAsync([target, report, "--audit-suppressions"]);

        exitCode.Should().Be(0);

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(report));
        var suppression = document.RootElement.GetProperty("suppression");

        // Nothing is configured in this fixture, so no channel may report activity and
        // the headline must be zero. ShadowAnalysisRan is false because there was nothing
        // to shadow — which is the whole point of the conditional passes.
        suppression.GetProperty("shadowAnalysisRan").GetBoolean().Should().BeFalse();
        suppression.GetProperty("totals").GetProperty("hiddenDebtPercent").GetInt32().Should().Be(0);
        suppression.GetProperty("totals").GetProperty("findingsDropped").GetInt32().Should().Be(0);
        suppression.GetProperty("pathGlobs").GetArrayLength().Should().Be(0);
        suppression.GetProperty("disabledRules").GetArrayLength().Should().Be(0);
        suppression.GetProperty("namespaceExclusions").GetArrayLength().Should().Be(0);
        suppression.GetProperty("obsolete").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Report_The_Audited_Finding_Count_Matching_The_Reported_Findings_For_RunAsync()
    {
        var target = Path.Combine(AppContext.BaseDirectory, "TestAssets", "SampleApp", "App", "App.csproj");
        var report = Path.Combine(Path.GetTempPath(), $"snipper-audit-count-{Guid.NewGuid():N}.json");

        var exitCode = await CliRunner.RunAsync([target, report, "--audit-suppressions"]);

        exitCode.Should().Be(0);

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(report));
        var root = document.RootElement;

        // With no suppressions configured, everything analysed is reported. This pins the
        // audit to the real finding set rather than a parallel count that could drift.
        root.GetProperty("suppression").GetProperty("totals").GetProperty("findingsAnalysed").GetInt32()
            .Should().Be(root.GetProperty("findings").GetArrayLength());
    }

    private static IReadOnlyList<string> ReadRuleIds(string reportPath)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(reportPath));

        return document.RootElement
            .GetProperty("findings")
            .EnumerateArray()
            .Select(element => element.GetProperty("ruleId").GetString() ?? string.Empty)
            .ToList();
    }
}
