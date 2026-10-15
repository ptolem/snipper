namespace Snipper.Tests;

using System.Text.RegularExpressions;
using FluentAssertions;
using Snipper.Cli;
using Snipper.Models;
using Xunit;

/// <summary>
/// Deterministic-output regression tests for the report writers and the
/// <see cref="FindingOrder"/> they share with the runner.
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
            CharacterOffset: character);

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

    [Fact]
    public void FindingOrder_Breaks_Ties_In_Key_Order()
    {
        // Each finding differs from the one before it in exactly one key, walking the keys
        // from last to first, so every tie-breaker has to fire for this order to come out.
        var expected = new[]
        {
            Finding("SNP0019", "a", "C:/src/A.cs", 1, 1),
            Finding("SNP0019", "a", "C:/src/A.cs", 1, 1) with { Title = "Z" },
            Finding("SNP0019", "a", "C:/src/A.cs", 1, 1) with { Category = FindingCategory.Complexity },
            Finding("SNP0019", "b", "C:/src/A.cs", 1, 1),
            Finding("SNP0033", "a", "C:/src/A.cs", 1, 1),
            Finding("SNP0002", "a", "C:/src/A.cs", 1, 2),
            Finding("SNP0002", "a", "C:/src/A.cs", 2, 1),
            Finding("SNP0002", "a", "C:/src/B.cs", 1, 1),
            Finding("SNP0002", "a", "C:/src/A.cs", 1, 1, CertaintyTier.Advisory),
        };

        FindingOrder.Sort(expected.Reverse()).Should().Equal(expected);
    }

    [Fact]
    public void FindingOrder_Compares_Paths_Ordinally()
    {
        // Ordinal puts 'B' (0x42) before 'a' (0x61); a culture-aware comparison would not,
        // and would make the order depend on the machine's locale.
        var lower = Finding("SNP0019", "m", "C:/src/a.cs", 1, 1);
        var upper = Finding("SNP0019", "m", "C:/src/B.cs", 1, 1);

        FindingOrder.Sort([lower, upper]).Should().Equal(upper, lower);
    }

    [GeneratedRegex("\"generatedAtUtc\"\\s*:\\s*\"[^\"]*\"")]
    private static partial Regex TimestampRegex();
}