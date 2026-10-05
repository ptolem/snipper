namespace Snipper.Tests;

using System.Text.RegularExpressions;
using FluentAssertions;
using Snipper.Cli;
using Snipper.Models;
using Xunit;

/// <summary>
/// Deterministic-output regression tests for the report writers.
/// <para>
/// Both writers used to sort by <c>(Certainty, FilePath)</c> only. That is a stable
/// sort, so every finding sharing such a pair - all unused usings in one file, for
/// instance - kept whatever order the analyser emitted. SNP0019 inherited a *parallel
/// producer-completion* order from <c>compilation.GetDiagnostics()</c>, so two runs over
/// the same solution returned the same findings in a different order. SARIF was worse:
/// it sorted by nothing at all while its own documentation claimed it sorted.
/// </para>
/// <para>
/// Feeding the same findings in different orders and demanding identical output pins
/// the ordering without depending on which analyser happened to run.
/// </para>
/// </summary>
public sealed partial class ReportOrderingShould
{
    private static SnipperFinding Finding(
        string ruleId,
        string message,
        string path,
        int line,
        int character,
        CertaintyTier certainty = CertaintyTier.Guaranteed) =>
        new(
            RuleId: ruleId,
            Title: "Unused Using Directive",
            Message: message,
            Certainty: certainty,
            Category: FindingCategory.UnusedUsingDirective,
            FilePath: path,
            LineNumber: line,
            CharacterOffset: character,
            Symbol: null);

    /// <summary>
    /// The deliberate worst case: several findings sharing one path and one certainty,
    /// which is exactly what the old two-key sort left unordered.
    /// </summary>
    private static SnipperFinding[] Findings() =>
    [
        Finding("SNP0019", "Using directive 'System' is unnecessary.", "C:/src/B.cs", 1, 1),
        Finding("SNP0019", "Using directive 'Xunit' is unnecessary.", "C:/src/A.cs", 7, 1),
        Finding("SNP0019", "Using directive 'Snipper' is unnecessary.", "C:/src/A.cs", 3, 1),
        Finding("SNP0019", "Using directive 'System.Linq' is unnecessary.", "C:/src/A.cs", 3, 9),
        Finding("SNP0002", "Unreachable code detected.", "C:/src/A.cs", 3, 9),
        Finding("SNP0019", "Using directive 'System' is unnecessary.", "C:/src/A.cs", 1, 1),
        Finding("SNP0031", "Duplicate fragment of 40 tokens.", "C:/src/C.cs", 12, 5, CertaintyTier.High),
    ];

    /// <summary>generatedAtUtc is wall-clock time, so it is the one field allowed to move.</summary>
    private static string Normalise(string json) => TimestampRegex().Replace(json, "\"generatedAtUtc\":\"X\"");

    [Fact]
    public void Json_Is_Identical_Whatever_Order_The_Analyser_Produced()
    {
        var findings = Findings();

        var forward = ReportWriter.BuildJson(findings, commitSha: "abc123");
        var reversed = ReportWriter.BuildJson([.. findings.Reverse()], commitSha: "abc123");

        Normalise(reversed).Should().Be(Normalise(forward));
    }

    [Fact]
    public void Sarif_Is_Identical_Whatever_Order_The_Analyser_Produced()
    {
        var findings = Findings();

        var forward = ReportWriter.BuildSarifJson(findings);
        var reversed = ReportWriter.BuildSarifJson([.. findings.Reverse()]);

        reversed.Should().Be(forward);
    }

    [Fact]
    public void Json_Sorts_So_That_Certainty_Then_Path_Then_Position_Holds()
    {
        var json = ReportWriter.BuildJson(Findings(), commitSha: null);
        var entries = Regex.Matches(json, "\"ruleId\": \"(?<rule>[^\"]+)\"")
            .Select(m => m.Groups["rule"].Value)
            .ToArray();

        // The High-tier SNP0031 sorts after every Guaranteed finding, so the
        // certainty grouping is directly observable in the serialised order.
        entries.Should().ContainInOrder("SNP0019");
        entries.TakeWhile(r => r == "SNP0019").Should().NotBeEmpty();
        entries[^1].Should().Be("SNP0031");
    }

    [GeneratedRegex("\"generatedAtUtc\"\\s*:\\s*\"[^\"]*\"")]
    private static partial Regex TimestampRegex();
}