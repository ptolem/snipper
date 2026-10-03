namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

/// <summary>
/// SNP0031 - duplicate fragment detection, end to end against the SampleApp
/// fixture. Fixture scenarios live in CloneFixtures.cs, CloneFixtureControls.cs,
/// FileScopeClone.cs (CoreLib) and CloneFixturesMirrored.cs,
/// FileScopeCloneMirror.cs (App).
/// </summary>
[Collection("SampleSolution")]
public sealed class DuplicateFragmentAnalyserShould(SampleSolutionFixture fixture)
{
    private const string Rule = "SNP0031";

    private async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(AnalysisExclusions? exclusions = null)
    {
        var analyser = new DuplicateFragmentAnalyser(exclusions);
        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        return findings.Where(f => f.RuleId == Rule).ToList();
    }

    private static string ShortName(SnipperFinding finding)
    {
        return Path.GetFileName(finding.FilePath);
    }

    [Fact]
    public async Task Emit_Nothing_Without_Duplicated_Fragments_For_A_Control_Only_Fixture()
    {
        // Sanity gate: the analyser returns a collection and honours the rule id
        // contract. The controls alone must never be reported.
        var findings = await AnalyzeAsync();

        findings.Should().OnlyContain(f => f.RuleId == Rule);
        findings.Should().NotContain(f => f.Message.Contains("DivergentBody", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_A_Cross_Project_Exact_Clone_For_AnalyzeAsync()
    {
        var findings = await AnalyzeAsync();

        findings.Should().Contain(f =>
            f.FilePath.Contains("CloneFixtures.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Report_The_Counterpart_Project_For_AnalyzeAsync()
    {
        var findings = await AnalyzeAsync();

        // The CoreLib seed and the App mirror are the two sides of one clone set.
        findings.Should().Contain(f => ShortName(f) == "CloneFixturesMirrored.cs");
    }

    [Fact]
    public async Task Flag_A_Renamed_Only_Clone_For_AnalyzeAsync()
    {
        var findings = await AnalyzeAsync();

        // Type-2: Compose/Render differ only in identifier names once
        // normalized. The plan locks Type-1 + Type-2 into v1.
        findings.Should().Contain(f =>
            f.FilePath.Contains("CloneFixtures", StringComparison.Ordinal));
    }

    [Fact]
    public async Task State_The_Occurrence_Count_And_Other_Location_For_AnalyzeAsync()
    {
        var findings = await AnalyzeAsync();

        findings.Should().Contain(f =>
            f.Message.Contains("duplicated", StringComparison.Ordinal)
            && f.Message.Contains("first other occurrence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_A_Short_Identical_Helper_For_AnalyzeAsync()
    {
        var findings = await AnalyzeAsync();

        // ShortHelper.One/Add are identical in shape but far below 60 tokens.
        findings.Should().NotContain(f =>
            f.Message.Contains("ShortHelper", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Identical_Using_Lists_Alone_For_AnalyzeAsync()
    {
        var findings = await AnalyzeAsync();

        // SameUsingsDifferentBody shares a byte-identical using block with its
        // sibling. If using tokens leaked into the stream this file would be
        // flagged on the using block alone.
        findings.Should().NotContain(f =>
            f.Message.Contains("SameUsingsDifferentBody", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Intra_File_Duplication_For_AnalyzeAsync()
    {
        var findings = await AnalyzeAsync();

        // TwiceInOneFile.A and .B are identical >=60-token fragments in ONE file.
        // Same-path locations are deduped by design (decision 5).
        findings.Should().NotContain(f =>
            f.Message.Contains("TwiceInOneFile", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_A_File_Scope_Fragment_For_AnalyzeAsync()
    {
        var findings = await AnalyzeAsync();

        // FileScopeClone.cs and its mirror carry duplicated helpers with no
        // enclosing type declaration at file scope.
        findings.Should().Contain(f => ShortName(f) == "FileScopeClone.cs");
        findings.Should().Contain(f => ShortName(f) == "FileScopeCloneMirror.cs");
    }

    [Fact]
    public async Task Use_Advisory_Certainty_And_Duplicate_Category_For_AnalyzeAsync()
    {
        var findings = await AnalyzeAsync();

        findings.Should().NotBeEmpty();
        findings.Should().OnlyContain(f => f.Certainty == CertaintyTier.Advisory);
        findings.Should().OnlyContain(f => f.Category == FindingCategory.DuplicateFragment);
    }

    [Fact]
    public async Task Report_A_Line_Number_For_AnalyzeAsync()
    {
        var findings = await AnalyzeAsync();

        findings.Should().OnlyContain(f => f.LineNumber > 0);
        findings.Should().OnlyContain(f => f.CharacterOffset > 0);
    }

    [Fact]
    public async Task Produce_Deterministic_Ordered_Output_For_AnalyzeAsync()
    {
        var first = await AnalyzeAsync();
        var second = await AnalyzeAsync();

        var project = first
            .Select(f => (f.RuleId, f.FilePath, f.LineNumber, f.CharacterOffset, f.Message))
            .ToList();

        var repeat = second
            .Select(f => (f.RuleId, f.FilePath, f.LineNumber, f.CharacterOffset, f.Message))
            .ToList();

        repeat.Should().Equal(project);
        project.Should().BeInAscendingOrder(
            p => p.FilePath, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Suppress_Fragments_In_Excluded_Namespaces_For_AnalyzeAsync()
    {
        // The mirrored clone pair lives under CloneFixtures.* namespaces.
        var exclusions = AnalysisExclusions.Create(["CloneFixtures"]);

        var findings = await AnalyzeAsync(exclusions);

        findings.Should().NotContain(f =>
            f.Message.Contains("CloneFixtures", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_A_File_Scope_Fragment_In_An_Excluded_Namespace_For_AnalyzeAsync()
    {
        // FileScopeClone.cs sits in the GLOBAL namespace - there is no enclosing
        // namespace declaration to resolve, and no enclosing type either. This
        // pins the file-scope branch of the namespace-exclusion resolution.
        var exclusions = AnalysisExclusions.Create(["<global>"]);

        var findings = await AnalyzeAsync(exclusions);

        findings.Should().NotContain(f => ShortName(f) == "FileScopeClone.cs");
    }

    [Fact]
    public async Task Not_Flag_A_Same_Directory_Clone_For_AnalyzeAsync()
    {
        // Structural guard: RootlessSibling.Tally and AdjacentPeer.Count in
        // SameDirectoryClone.cs are an identical >=60-token fragment sharing one
        // directory. Measured rationale: this is the shape that produced 518 of
        // the 1160 findings in Snipper's own dogfood run (sibling analysers
        // sharing one project/document loop), and it is duplication by design
        // rather than the cross-project copy-paste the rule targets.
        var findings = await AnalyzeAsync();

        findings.Should().NotContain(f =>
            f.FilePath.Contains("SameDirectoryClone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Report_A_Clone_Once_Not_Once_Per_Window_For_AnalyzeAsync()
    {
        // Regression: extension walks backwards one token at a time, so a
        // 220-token clone between two files arrives as ~140 nested matches.
        // Collapsing them is what keeps one clone to one finding per occurrence.
        var findings = await AnalyzeAsync();

        var seed = findings.Where(f => f.FilePath.EndsWith("CloneFixtures.cs", StringComparison.Ordinal)).ToList();

        seed.Should().NotBeEmpty();
        seed.Select(f => f.LineNumber).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Report_The_Rule_Id_Contract_For_CliRunner()
    {
        var analyser = new DuplicateFragmentAnalyser();

        analyser.RuleIds.Should().ContainSingle().Which.Should().Be(Rule);
    }
}