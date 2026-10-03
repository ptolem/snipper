namespace Snipper.Tests;

using System.Collections.Frozen;
using System.Text.Json;
using FluentAssertions;
using Snipper.Cli;
using Snipper.Models;
using Xunit;

public sealed class ReportSchemaShould : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"snipper-report-schema-{Guid.NewGuid():N}");
    private readonly string _sourceFile;

    public ReportSchemaShould()
    {
        Directory.CreateDirectory(_directory);
        _sourceFile = Path.Combine(_directory, "Sample.cs");
        File.WriteAllLines(_sourceFile, ["namespace Sample;", "", "public static class Doomed", "{", "    private static void Unused() { }", "}"]);
    }

    [Fact]
    public void Wrap_Findings_With_Tool_Metadata_For_BuildJson()
    {
        var json = CliRunner.BuildJson([Finding()], commitSha: "abc123");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("toolVersion").GetString().Should().MatchRegex(@"^\d+\.\d+\.\d+$");
        root.GetProperty("commitSha").GetString().Should().Be("abc123");
        root.GetProperty("generatedAtUtc").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        root.GetProperty("findings").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void Omit_Commit_Sha_When_Unknown_For_BuildJson()
    {
        var json = CliRunner.BuildJson([Finding()], commitSha: null);

        using var document = JsonDocument.Parse(json);
        document.RootElement.TryGetProperty("commitSha", out _).Should().BeFalse();
    }

    [Fact]
    public void Include_The_Source_Line_Text_For_Each_Finding_For_BuildJson()
    {
        // FP-5: consumers verify the quoted line before applying a finding.
        var json = CliRunner.BuildJson([Finding()], commitSha: null);

        using var document = JsonDocument.Parse(json);
        var entry = document.RootElement.GetProperty("findings")[0];
        entry.GetProperty("lineText").GetString().Should().Be("private static void Unused() { }");
    }

    [Fact]
    public void Omit_Line_Text_When_The_File_Is_Missing_For_BuildJson()
    {
        var json = CliRunner.BuildJson([Finding(Path.Combine(_directory, "missing.cs"))], commitSha: null);

        using var document = JsonDocument.Parse(json);
        var entry = document.RootElement.GetProperty("findings")[0];
        entry.TryGetProperty("lineText", out _).Should().BeFalse();
    }

    [Fact]
    public void Include_A_Region_Snippet_For_BuildSarifJson()
    {
        var json = CliRunner.BuildSarifJson([Finding()]);

        using var document = JsonDocument.Parse(json);
        var region = document.RootElement
            .GetProperty("runs")[0]
            .GetProperty("results")[0]
            .GetProperty("locations")[0]
            .GetProperty("physicalLocation")
            .GetProperty("region");
        region.GetProperty("snippet").GetProperty("text").GetString().Should().Be("private static void Unused() { }");
    }

    [Fact]
    public void Omit_The_Suppression_Section_When_No_Audit_Was_Requested_For_BuildJson()
    {
        // Existing consumers must see byte-identical output when the flag is absent.
        var json = CliRunner.BuildJson([Finding()], commitSha: null);

        using var document = JsonDocument.Parse(json);
        document.RootElement.TryGetProperty("suppression", out _).Should().BeFalse();
    }

    [Fact]
    public void Include_The_Suppression_Audit_As_Its_Own_Section_For_BuildJson()
    {
        var audit = SuppressionAuditBuilder.Build(
            [Finding(Path.Combine(_directory, "Generated", "a.cs"))],
            Config(paths: ["**/Generated/**"]),
            minimumCertainty: null,
            disabledRuleShadowFindings: null,
            namespaceShadowFindings: null,
            shadowAnalysisRan: false,
            globMatchesNoFile: null,
            namespaceExists: null);

        var json = CliRunner.BuildJson([Finding()], commitSha: null, audit);

        using var document = JsonDocument.Parse(json);
        var suppression = document.RootElement.GetProperty("suppression");

        suppression.GetProperty("shadowAnalysisRan").GetBoolean().Should().BeFalse();
        suppression.GetProperty("totals").GetProperty("findingsAnalysed").GetInt32().Should().Be(1);
        suppression.GetProperty("totals").GetProperty("findingsDropped").GetInt32().Should().Be(1);
        suppression.GetProperty("totals").GetProperty("hiddenDebtPercent").GetInt32().Should().Be(100);

        var glob = suppression.GetProperty("pathGlobs")[0];
        glob.GetProperty("selector").GetString().Should().Be("**/Generated/**");
        glob.GetProperty("suppressedCount").GetInt32().Should().Be(1);
        glob.GetProperty("channel").GetString().Should().Be("PathGlob");
    }

    [Fact]
    public void Omit_A_Null_Certainty_Filter_From_The_Suppression_Audit_For_BuildJson()
    {
        var audit = SuppressionAuditBuilder.Build(
            [Finding()], SnipperConfig.Empty, null, null, null, false, null, null);

        var json = CliRunner.BuildJson([Finding()], commitSha: null, audit);

        using var document = JsonDocument.Parse(json);
        var suppression = document.RootElement.GetProperty("suppression");
        suppression.TryGetProperty("certaintyFilter", out _).Should().BeFalse();
    }

    private static SnipperConfig Config(string[] paths)
    {
        return new SnipperConfig
        {
            ExcludedPathGlobs = paths.ToFrozenSet(StringComparer.Ordinal),
        };
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private SnipperFinding Finding(string? path = null)
    {
        return new SnipperFinding(
            RuleId: "SNP0001",
            Title: "Unused Private Member",
            Message: "Private method 'Unused' is never used.",
            Certainty: CertaintyTier.Guaranteed,
            Category: FindingCategory.UnusedPrivateMember,
            FilePath: path ?? _sourceFile,
            LineNumber: 5,
            CharacterOffset: 5,
            Symbol: null);
    }
}
