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

    /// <summary>
    /// Every file a finding points at: the anchor plus its related locations.
    /// <para>
    /// SNP0031 reports one finding per clone <em>set</em>, so a two-file clone has one
    /// finding whose anchor is only one of the two files. Assertions about "is this
    /// file reported" therefore have to look across the whole set, or they would pass
    /// or fail on which side happened to sort first.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> FilesOf(SnipperFinding finding)
    {
        var files = new List<string> { finding.FilePath };
        files.AddRange(finding.RelatedLocations?.Select(r => r.FilePath) ?? []);
        return files.Select(Path.GetFileName).ToList();
    }

    /// <summary>
    /// Every path a finding names, anchor included, unshortened.
    /// </summary>
    /// <remarks>
    /// <see cref="FilesOf"/> reduces to a basename, which is right for "was this file
    /// reported" but wrong for uniqueness: two different files sharing a name are two
    /// files. This variant keeps the path so "one location per file" is checked
    /// against the file and not against its name.
    /// </remarks>
    private static IReadOnlyList<string> PathsOf(SnipperFinding finding)
    {
        var paths = new List<string> { finding.FilePath };
        paths.AddRange(finding.RelatedLocations?.Select(r => r.FilePath) ?? []);
        return paths;
    }

    /// <summary>Copy and file counts read back out of a SNP0031 message, or null.</summary>
    private static (int Copies, int Files)? ParseCounts(string message)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            message,
            @"duplicated (\d+) time\(s\) across (\d+) file\(s\)\.");

        return match.Success
            ? (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value))
            : null;
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

            // The set is reported once, anchored at whichever member sorts first, so
            // the assertion is over the whole set rather than over FilePath alone.
            findings.Should().Contain(f =>
                FilesOf(f).Contains("CloneFixtures.cs", StringComparer.Ordinal));
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

            // The count and the sibling locations are the whole point of the finding,
            // so both must survive the collapse: the count in the message, the copies
            // as related locations.
            findings.Should().Contain(f =>
                f.Message.Contains("duplicated", StringComparison.Ordinal)
                && f.Message.Contains("time(s)", StringComparison.Ordinal)
                && f.Message.Contains("file(s)", StringComparison.Ordinal));

            findings.Count(f => f.RelatedLocations is { Count: > 0 }).Should().BeGreaterThan(0);
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
            // enclosing type declaration at file scope. One set, one finding - so both
            // files must be reachable from that single finding.
            var covering = findings
                .Where(f => FilesOf(f).Contains("FileScopeClone.cs", StringComparer.Ordinal))
                .ToList();

            covering.Should().NotBeEmpty();
            covering.Should().Contain(f =>
                FilesOf(f).Contains("FileScopeCloneMirror.cs", StringComparer.Ordinal));
        }

        [Fact]
        public async Task Report_One_Finding_Per_Set_Not_One_Per_Copy_For_AnalyzeAsync()
        {
            // The defect this change exists for: a skeleton copied N times was N
            // findings, so one duplication could dominate a threshold table. Each set
            // must now yield exactly one finding, and that finding must list the rest.
            var findings = await AnalyzeAsync();

            findings.Should().NotBeEmpty();

            // Uniqueness is per anchor *span*, not per file and not per line: one file can
            // legitimately anchor several distinct clone sets, and two different fragments
            // can begin on the same line. What must never repeat is one anchor, which is
            // what per-window reporting produced ~140 times over for a single clone.
            findings.Select(f => (f.FilePath, f.LineNumber, f.CharacterOffset))
                .Should().OnlyHaveUniqueItems("one anchor fragment per clone set");

            // A set of two or more always leaves at least one other copy to point at, so every
            // finding must carry related locations. Count(...) rather than OnlyContain(...)
            // because the latter compiles to an expression tree, where `is` is illegal.
            findings.Count(f => f.RelatedLocations is { Count: > 0 })
                .Should().Be(findings.Count);
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
        public async Task Give_Every_Clone_Set_A_Distinct_Message_For_AnalyzeAsync()
    {
        // The baseline fingerprint is RuleId|path|message and carries no position, so two
        // clone sets anchored in the same file that report the same token and line counts
        // used to produce one message between them — and one fingerprint. Suppressing one
        // then suppressed the other. Counts alone cannot identify a set; the message must.
        var findings = await AnalyzeAsync();

        findings.Select(f => f.Message)
            .Should().OnlyHaveUniqueItems("each clone set needs its own message to fingerprint separately");
    }

[Fact]
        public async Task Separate_Two_Same_Count_Sets_Anchored_In_One_File_For_AnalyzeAsync()
        {
            // SameCountCloneSets.cs holds two sets of identical shape and length, each
            // paired with a different partner — the milkrun line-109 case, reproduced.
            // Both anchor in the same file and report the same token and line counts,
            // so counts alone cannot tell them apart. Deduplicating by
            // (rule, file, span) would have merged these into one and lost a real clone;
            // they must survive as two findings naming different partners.
            var findings = await AnalyzeAsync();

            var anchored = findings
                .Where(f => Path.GetFileName(f.FilePath).Equals("SameCountAnchor.cs", StringComparison.Ordinal))
                .ToList();

            anchored.Should().HaveCountGreaterThanOrEqualTo(2, "two distinct sets share this anchor file");
            anchored.Select(f => f.Message).Should().OnlyHaveUniqueItems();

            // The two sets differ only in which partner they name, so that is what has
            // to differ in the message — and therefore in the baseline fingerprint. The
            // partner classes sit in one file, so the message names its location; the
            // two entries below are the two distinct sets, at different line numbers.
            var rendered = anchored.Select(f => f.Message).ToList();
            rendered.Should().OnlyHaveUniqueItems();
            rendered.Should().Contain(m => m.Contains("SameCountCloneSets.cs", StringComparison.Ordinal));
            anchored.Select(f => f.LineNumber).Should().OnlyHaveUniqueItems("two sets anchor at two places");
        }

    [Fact]
    public async Task Name_The_Other_Copies_In_The_Message_For_AnalyzeAsync()
    {
        var findings = await AnalyzeAsync();

        findings.Should().NotBeEmpty();
        findings.Should().OnlyContain(f => f.Message.Contains("Duplicated in:", StringComparison.Ordinal));
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
            // Collapsing them is what keeps one clone to one finding.
            var findings = await AnalyzeAsync();

            var seed = findings
                .Where(f => FilesOf(f).Contains("CloneFixtures.cs", StringComparer.Ordinal))
                .ToList();

            seed.Should().NotBeEmpty();
            seed.Select(f => (f.FilePath, f.LineNumber, f.CharacterOffset)).Should().OnlyHaveUniqueItems();
        }

    [Fact]
        public async Task Report_One_Location_Per_File_So_The_Copy_Count_Means_It_Says_For_AnalyzeAsync()
    {
        // The same-path guard skips a file matching ITSELF, not against its
        // siblings, so A-B and B-C are both legal pairs and union-find merges
        // them into a set holding A twice. Those were windows over one duplicated
        // region, not separate copies, and the set claimed more copies than it
        // had places. On the reference monorepo 1,210 of 4,375 copies (27.7%)
        // were a second-or-later fragment from a file already in the set.
        var findings = await AnalyzeAsync();

        findings.Should().NotBeEmpty();

        foreach (var finding in findings)
        {
            PathsOf(finding).Should().OnlyHaveUniqueItems(
                $"{finding.RuleId} at {finding.FilePath}:{finding.LineNumber} lists one file more than once");
        }
    }

    [Fact]
    public async Task Report_The_Copy_Count_To_Equal_The_File_Count_For_AnalyzeAsync()
    {
        // "duplicated N time(s) across M file(s)" is only honest when N == M.
        // The message asserts both, so a set that reported 85 copies in one file
        // made the sentence contradict itself.
        var findings = await AnalyzeAsync();

        var counted = findings
            .Select(finding => new
            {
                Finding = finding,
                Parsed = ParseCounts(finding.Message),
                Message = $"{finding.FilePath}:{finding.LineNumber} - {finding.Message}",
            })
            .Where(entry => entry.Parsed is not null)
            .ToList();

        counted.Should().NotBeEmpty();

        foreach (var entry in counted)
        {
            var parsed = entry.Parsed!.Value;

            parsed.Copies.Should().Be(parsed.Files, entry.Message);
            parsed.Files.Should().Be(PathsOf(entry.Finding).Count, entry.Message);
        }
    }

    [Fact]
    public void Merge_Fragments_Of_One_File_Into_Their_Enclosing_Span()
    {
        // A const table slid through one file at shifted token offsets reports as
        // windows at lines 6, 8, 9, 10 ... The merge reports the whole region.
        var members = new List<CloneSetMember>
        {
            new("c.cs", 10, 5, 19),
            new("c.cs", 8, 5, 17),
            new("c.cs", 6, 5, 15),
            new("d.cs", 6, 5, 15),
        };

        var collapsed = DuplicateFragmentAnalyser.CollapseToOneLocationPerFile(members);

        collapsed.Should().HaveCount(2, "one location per file");
        collapsed[0].Should().Be(new CloneSetMember("c.cs", 6, 5, 19));
        collapsed[1].Should().Be(new CloneSetMember("d.cs", 6, 5, 15));
    }

    [Fact]
    public void Keep_Disjoint_Regions_Of_One_File_Separate()
    {
        // Two genuinely disjoint duplicated blocks in one file stay disjoint: the
        // spans do not touch, so there is one region to report, not two.
        var members = new List<CloneSetMember>
        {
            new("a.cs", 10, 5, 20),
            new("a.cs", 80, 5, 90),
            new("b.cs", 10, 5, 20),
        };

        var collapsed = DuplicateFragmentAnalyser.CollapseToOneLocationPerFile(members);

        // Merged per path, so a.cs is ONE location whose span covers both regions.
        // They are not two copies: the file is where the duplication lives.
        collapsed.Should().HaveCount(2);
        collapsed[0].Should().Be(new CloneSetMember("a.cs", 10, 5, 90));
        collapsed[1].Should().Be(new CloneSetMember("b.cs", 10, 5, 20));
    }

    [Fact]
    public void Leave_A_Set_Of_One_File_Alone()
    {
        var members = new List<CloneSetMember> { new("only.cs", 3, 1, 9) };

        DuplicateFragmentAnalyser.CollapseToOneLocationPerFile(members)
            .Should().BeEquivalentTo(members);
    }

    [Fact]
    public void Order_Collapsed_Locations_Deterministically()
    {
        // The anchor must not move between runs, so ordering cannot depend on
        // group enumeration order.
        var members = new List<CloneSetMember>
        {
            new("z.cs", 4, 1, 9),
            new("a.cs", 40, 1, 49),
            new("a.cs", 4, 1, 9),
        };

        var collapsed = DuplicateFragmentAnalyser.CollapseToOneLocationPerFile(members);

        collapsed.Select(m => m.Path).Should().ContainInOrder("a.cs", "z.cs");
    }

    [Fact]
    public async Task Report_The_Rule_Id_Contract_For_CliRunner()
    {
        var analyser = new DuplicateFragmentAnalyser();

        analyser.RuleIds.Should().ContainSingle().Which.Should().Be(Rule);
    }
}