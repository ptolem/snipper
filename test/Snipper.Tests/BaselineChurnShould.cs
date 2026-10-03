namespace Snipper.Tests;

using System.Text.Json;
using FluentAssertions;
using Snipper.Cli;
using Xunit;

/// <summary>
/// Wave 4 / 4A-2: baseline churn invariance.
///
/// The property under test is not "the extra pass runs" but **toggling any suppression
/// channel leaves the recorded baseline identical**. Measured on the SampleApp fixture
/// before the fix: 256 unconfigured, 226 with a sole-rule rule disabled (-30), 237 with
/// two namespaces excluded (-19). Those findings resurfaced as *new* the moment the
/// suppression was removed, which is the exact failure a baseline exists to prevent.
///
/// The policy predicate is unit-tested here too, because "when may we skip the extra
/// analysis pass" is the part a future change could quietly regress.
/// </summary>
// Shares a collection with CliRunnerShould: Spectre.Console's Status is process-wide
// exclusive, so two CliRunner.RunAsync calls cannot overlap in one test host.
[Collection("CliRuns")]
public sealed class BaselineChurnShould : IDisposable
{
    private const string DisableSnp0024 =
        """
        { "version": 1, "rules": { "SNP0024": "off" } }
        """;

    private const string ExcludeShadowing =
        """
        { "version": 1, "exclude": { "namespaces": ["CoreLib.Shadowing"] } }
        """;

    private const string ExcludeTwoNamespaces =
        """
        { "version": 1, "exclude": { "namespaces": ["CoreLib.Shadowing", "CoreLib.Controls"] } }
        """;

    private const string EverythingSuppressed =
        """
        {
          "version": 1,
          "rules": { "SNP0024": "off", "SNP0018": "advisory" },
          "exclude": {
            "namespaces": ["CoreLib.Shadowing"],
            "paths": ["**/Generated/**"]
          }
        }
        """;

    private readonly string _workspace;
    private readonly string _project;

    public BaselineChurnShould()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"snipper-churn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);

        var source = Path.Combine(AppContext.BaseDirectory, "TestAssets", "SampleApp");
        CopyDirectory(source, _workspace);

        _project = Path.Combine(_workspace, "CoreLib", "CoreLib.csproj");
    }

    [Fact]
    public void Skip_The_Suppression_Independent_Pass_Without_A_Baseline()
    {
        // No baseline means nothing to churn, so a user who has not adopted baselines must
        // never pay for the extra analysis pass no matter how much they suppress.
        BaselineService.RequiresSuppressionIndependentFingerprints(
                baselineRequested: false,
                excludedNamespaceCount: 5,
                removedAnalyserCount: 3)
            .Should().BeFalse();
    }

    [Fact]
    public void Skip_The_Suppression_Independent_Pass_For_Churn_Free_Channels()
    {
        // exclude.paths, severity overrides and --certainty-tier all apply after
        // fingerprinting, so they cannot churn the baseline and must not trigger a pass.
        BaselineService.RequiresSuppressionIndependentFingerprints(
                baselineRequested: true,
                excludedNamespaceCount: 0,
                removedAnalyserCount: 0)
            .Should().BeFalse();
    }

    [Fact]
    public void Require_The_Suppression_Independent_Pass_For_Namespace_Exclusions()
    {
        BaselineService.RequiresSuppressionIndependentFingerprints(
                baselineRequested: true,
                excludedNamespaceCount: 1,
                removedAnalyserCount: 0)
            .Should().BeTrue();
    }

    [Fact]
    public void Require_The_Suppression_Independent_Pass_For_A_Removed_Analyser()
    {
        BaselineService.RequiresSuppressionIndependentFingerprints(
                baselineRequested: true,
                excludedNamespaceCount: 0,
                removedAnalyserCount: 1)
            .Should().BeTrue();
    }

    [Fact]
    public async Task Keep_The_Baseline_Identical_When_A_Namespace_Exclusion_Is_Added()
    {
        var baseline = BaselinePath("ns");
        var unconfigured = await RunAsync(null, baseline);

        var excluded = await RunAsync(ExcludeTwoNamespaces, baseline);

        excluded.FingerprintCount.Should().Be(unconfigured.FingerprintCount);
    }

    [Fact]
    public async Task Keep_The_Baseline_Identical_When_A_Sole_Rule_Analyser_Is_Disabled()
    {
        // SNP0024 is the only rule TighteningAnalyser emits, so disabling it removes the
        // whole analyser before analysis. This is the case that measured -30.
        var baseline = BaselinePath("rule");
        var unconfigured = await RunAsync(null, baseline);

        var disabled = await RunAsync(DisableSnp0024, baseline);

        disabled.FingerprintCount.Should().Be(unconfigured.FingerprintCount);
    }

    [Fact]
    public async Task Keep_The_Baseline_Identical_Under_Every_Channel_At_Once()
    {
        var baseline = BaselinePath("all");
        var unconfigured = await RunAsync(null, baseline);

        var everything = await RunAsync(EverythingSuppressed, baseline);

        everything.FingerprintCount.Should().Be(unconfigured.FingerprintCount);
    }

    [Fact]
    public async Task Not_Resurface_Suppressed_Findings_As_New_When_A_Suppression_Is_Removed()
    {
        // The user-visible symptom of the churn bug. Pre-fix this reported 30 new
        // findings on code nobody had touched.
        var baseline = BaselinePath("resurface");
        await RunAsync(null, baseline);
        await RunAsync(DisableSnp0024, baseline);

        var afterRemoval = await RunAsync(null, baseline);

        afterRemoval.ReportedCount.Should().Be(0);
        afterRemoval.FingerprintCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Not_Resurface_Namespace_Excluded_Findings_As_New_When_The_Exclusion_Is_Removed()
    {
        var baseline = BaselinePath("resurface-ns");
        await RunAsync(null, baseline);
        await RunAsync(ExcludeShadowing, baseline);

        var afterRemoval = await RunAsync(null, baseline);

        afterRemoval.ReportedCount.Should().Be(0);
    }

    [Fact]
    public async Task Still_Hide_Suppressed_Findings_From_The_Report_While_Baselining_Them()
    {
        // The fix must not turn suppression into reporting: excluded findings belong in the
        // baseline but must stay out of the findings a user sees.
        var baseline = BaselinePath("hidden");
        var result = await RunAsync(ExcludeShadowing, baseline);

        result.FingerprintCount.Should().BeGreaterThan(result.ReportedCount);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<RunResult> RunAsync(string? configJson, string baselinePath)
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

        var report = Path.Combine(_workspace, "report.json");
        var exitCode = await CliRunner.RunAsync([_project, report, "--baseline", baselinePath]);
        exitCode.Should().Be(0);

        using var reportDocument = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        using var baselineDocument = JsonDocument.Parse(await File.ReadAllTextAsync(baselinePath));

        return new RunResult(
            FingerprintCount: baselineDocument.RootElement.GetProperty("fingerprints").GetArrayLength(),
            ReportedCount: reportDocument.RootElement.GetProperty("findings").GetArrayLength());
    }

    private string BaselinePath(string suffix) => Path.Combine(_workspace, $"baseline-{suffix}.json");

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

    private sealed record RunResult(int FingerprintCount, int ReportedCount);
}
