namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Cli;
using Snipper.Models;
using Xunit;

public sealed class BaselineServiceShould
{
    [Fact]
    public void Produce_Identical_Fingerprints_When_Only_The_Line_Number_Changes_For_ComputeFingerprint()
    {
        var baseDirectory = Path.GetTempPath();
        var first = CreateFinding(baseDirectory, lineNumber: 10);
        var second = CreateFinding(baseDirectory, lineNumber: 900);

        var firstFingerprint = BaselineService.ComputeFingerprint(first, baseDirectory);
        var secondFingerprint = BaselineService.ComputeFingerprint(second, baseDirectory);

        firstFingerprint.Should().Be(secondFingerprint);
    }

    [Fact]
    public void Produce_Different_Fingerprints_When_The_Rule_Changes_For_ComputeFingerprint()
    {
        var baseDirectory = Path.GetTempPath();
        var finding = CreateFinding(baseDirectory, lineNumber: 10);
        var otherRule = finding with { RuleId = "SNP0002" };

        BaselineService.ComputeFingerprint(finding, baseDirectory)
            .Should().NotBe(BaselineService.ComputeFingerprint(otherRule, baseDirectory));
    }

    [Fact]
    public void Roundtrip_Fingerprints_When_Baseline_Is_Written_Then_Loaded_For_Write()
    {
        var baselinePath = Path.Combine(Path.GetTempPath(), $"snipper-baseline-{Guid.NewGuid():N}.json");

        try
        {
            BaselineService.Write(baselinePath, ["abc123", "def456"]);

            var loaded = BaselineService.Load(baselinePath);

            loaded.Should().Contain("abc123").And.Contain("def456");
        }
        finally
        {
            if (File.Exists(baselinePath))
            {
                File.Delete(baselinePath);
            }
        }
    }

    [Fact]
    public void Return_Empty_Set_When_Baseline_File_Does_Not_Exist_For_Load()
    {
        var loaded = BaselineService.Load(Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json"));

        loaded.Should().BeEmpty();
    }

    private static SnipperFinding CreateFinding(string baseDirectory, int lineNumber)
    {
        return new SnipperFinding(
            RuleId: "SNP0001",
            Title: "Unused Private Member",
            Message: "Private member '_field' is declared but never referenced.",
            Certainty: CertaintyTier.Guaranteed,
            Category: FindingCategory.UnusedPrivateMember,
            FilePath: Path.Combine(baseDirectory, "Sample.cs"),
            LineNumber: lineNumber,
            CharacterOffset: 1);
    }
}
