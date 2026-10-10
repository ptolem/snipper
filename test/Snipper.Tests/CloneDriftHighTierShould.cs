namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Xunit;

/// <summary>
/// Gate 5 regression tests: four defects found in the SNP0032 High tier while validating 4C on a
/// 2,763-file monorepo. Each test names the defect it prevents from returning.
/// </summary>
public sealed class CloneDriftHighTierShould
{
    // ---------- defect 1: siblings must be self-locating ----------

    /// <summary>
    /// Every High finding on the monorepo cited its siblings by bare filename while its own
    /// <c>filePath</c> was absolute. The repository contains two <c>GetStoresQuery.cs</c>, so
    /// "GetStoresQuery.cs:23" named two different files and the reader had to go and search.
    /// </summary>
    [Fact]
    public void Sibling_References_Are_Repository_Relative_Not_Bare_Filenames()
    {
        CloneDriftClassifier.DescribeSiblings([("src/Beta/Query.cs", 1)])
            .Should().Be("src/Beta/Query.cs:1");
    }

    [Fact]
    public void Sibling_References_Survive_Duplicate_Basenames()
    {
        // Same basename, different directories - exactly the monorepo case that made the bare
        // filename ambiguous.
        CloneDriftClassifier.DescribeSiblings([("src/Two/GetStoresQuery.cs", 23)])
            .Should().Be("src/Two/GetStoresQuery.cs:23")
            .And.NotBe("GetStoresQuery.cs:23");
    }

    // ---------- defect 2: per-sibling commit attribution ----------

    /// <summary>
    /// The outcome clause listed only SHAs, so two siblings fixed by one commit read as the same
    /// fact twice - "92a2b30 on 2025-05-14; 92a2b30 on 2025-05-14" - indistinguishable from a bug
    /// attributing a single commit to two files.
    /// </summary>
    [Fact]
    public void Each_Sibling_Is_Named_In_The_Catch_Up_Attribution()
    {
        CloneDriftClassifier.DescribeCatchUps(
        [
            ("src/Beta/Query.cs", "92a2b30aa", new DateTimeOffset(2025, 5, 14, 0, 0, 0, TimeSpan.Zero)),
            ("src/Gamma/Query.cs", "bc9f452bb", new DateTimeOffset(2024, 4, 15, 0, 0, 0, TimeSpan.Zero)),
        ]).Should().Be(
            "src/Beta/Query.cs was fixed by 92a2b30 on 2025-05-14; "
            + "src/Gamma/Query.cs was fixed by bc9f452 on 2024-04-15");
    }

    [Fact]
    public void One_Commit_Fixing_Two_Siblings_Stays_Readable()
    {
        var shared = new DateTimeOffset(2025, 5, 14, 0, 0, 0, TimeSpan.Zero);

        var attribution = CloneDriftClassifier.DescribeCatchUps(
        [
            ("src/Beta/Query.cs", "92a2b30aa", shared),
            ("src/Gamma/Query.cs", "92a2b30aa", shared),
        ]);

        // One commit really can fix both siblings. The fix is to say *which* file each SHA refers
        // to, not to pretend the repetition was an error.
        attribution.Should().Be(
            "src/Beta/Query.cs was fixed by 92a2b30 on 2025-05-14; "
            + "src/Gamma/Query.cs was fixed by 92a2b30 on 2025-05-14");
    }

    // ---------- defect 3: the anchor must point at the change ----------

/// <summary>
    /// The finding was anchored at the start of the clone region, which for a maximal run is often
    /// wherever the duplicated text happened to begin - a stray ';' on line 1. Three of 111 High
    /// findings pointed at a line containing only ';', '{' or '}'.
    /// </summary>
    [Theory]
    [InlineData(1, 40, 20, 20)]    // change inside the region: use it
    [InlineData(38, 40, 39, 39)]   // change inside the region at its head: use it
    [InlineData(2, 40, 1, 1)]      // change only just before the region: keep the real line
    [InlineData(30, 40, 45, 45)]   // change only just after the region: keep the real line
    [InlineData(20, 25, 1, 1)]     // far before the region: still the real line, not a fabricated 20
    public void Anchor_Is_A_Line_The_Commit_Actually_Changed(int regionStart, int end, int newStart, int expected)
    {
        var hunk = new PatchHunk(0, 0, NewStart: newStart, NewCount: 1, AddedLines: [], RemovedLines: [], ChangedNewLines: [newStart]);
        CloneDriftClassifier.AnchorLine(hunk, regionStart, end).Should().Be(expected);
    }

    /// <summary>
    /// <c>NewStart</c> points at the hunk's first line, which git fills with unchanged context, so
    /// anchoring on it put 3 of 111 High findings on a blank line or a lone brace.
    /// <c>ChangedNewLines</c> holds the post-image line of every line the hunk really changed.
    /// </summary>
    [Fact]
    public void Anchor_Prefers_The_First_Changed_Line_Over_The_Hunk_Start()
    {
        // Hunk starts at 45 with three lines of context; the first real change is at 48.
        var hunk = new PatchHunk(
            OldStart: 45,
            OldCount: 1,
            NewStart: 45,
            NewCount: 4,
            AddedLines: ["    var total = count;"],
            RemovedLines: ["    var total = 0;"],
            ChangedNewLines: [48]);

        CloneDriftClassifier.AnchorLine(hunk, regionStart: 10, regionEnd: 90).Should().Be(48);
    }

    [Fact]
    public void Anchor_Falls_Back_To_The_Hunk_Start_When_No_Change_Was_Recorded()
    {
        var hunk = new PatchHunk(0, 0, NewStart: 45, NewCount: 0, AddedLines: [], RemovedLines: []);

        CloneDriftClassifier.AnchorLine(hunk, regionStart: 10, regionEnd: 90).Should().Be(45);
    }

    /// <summary>
    /// Live case: commit f63bf39's hunk began at post-image line 139 while the clone region started at
    /// 143, so clamping the hunk's first change forward to <c>regionStart</c> put the finding on the
    /// region's opening brace. The hunk's later change at 147 is inside the region, so that is the
    /// line the finding belongs on.
    /// </summary>
    [Fact]
    public void Anchor_Ignores_Changes_Before_The_Region_In_Favour_Of_One_Inside_It()
    {
        var hunk = new PatchHunk(
            OldStart: 137,
            OldCount: 6,
            NewStart: 138,
            NewCount: 6,
            AddedLines: ["    var total = count;"],
            RemovedLines: ["    var total = 0;"],
            ChangedNewLines: [139, 147]);

        CloneDriftClassifier.AnchorLine(hunk, regionStart: 143, regionEnd: 160).Should().Be(147);
    }

    [Fact]
    public void Anchor_Takes_The_First_Change_That_Lands_Inside_The_Region()
    {
        // The parser emits changed lines in post-image order, so the first in-region entry is the
        // earliest one inside the region.
        var hunk = new PatchHunk(
            OldStart: 140,
            OldCount: 8,
            NewStart: 143,
            NewCount: 8,
            AddedLines: ["    var total = count;"],
            RemovedLines: ["    var total = 0;"],
            ChangedNewLines: [147, 150, 152]);

        CloneDriftClassifier.AnchorLine(hunk, regionStart: 143, regionEnd: 160).Should().Be(147);
    }

    [Fact]
    public void Anchor_Falls_Back_To_The_Nearest_Changed_Line_When_None_Lands_Inside_The_Region()
    {
        var hunk = new PatchHunk(
            OldStart: 100,
            OldCount: 2,
            NewStart: 130,
            NewCount: 2,
            AddedLines: ["    var total = count;"],
            RemovedLines: ["    var total = 0;"],
            ChangedNewLines: [130, 139]);

        CloneDriftClassifier.AnchorLine(hunk, regionStart: 143, regionEnd: 160).Should().Be(139);
    }




    [Fact]
    public void Anchor_Rejects_An_Inverted_Region()
    {
        var hunk = new PatchHunk(0, 0, NewStart: 5, NewCount: 1, AddedLines: [], RemovedLines: []);

        var act = () => CloneDriftClassifier.AnchorLine(hunk, regionStart: 30, regionEnd: 10);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---------- defect 4: a rename is not a defensive fix ----------

    /// <summary>
    /// The verified gate 5 sample was a cosmetic <c>_lastOrderId</c> rename reported as
    /// "One-sided defensive fix" at High. The marker matcher matched substrings, so <c>null</c>
    /// fired on <c>Nullable</c> and <c>try</c> on <c>Country</c>.
    /// </summary>
    [Theory]
    [InlineData("Nullable<int> value", false)]
    [InlineData("var country = GetCountry();", false)]
    [InlineData("if (Discount > 0)", false)]
    [InlineData("var length = items.Length;", false)]
    [InlineData("value ?? fallback", true)]
    [InlineData("if (value is null) throw new ArgumentNullException();", true)]
    public void Markers_Match_On_Identifier_Boundaries(string line, bool expected)
    {
        DefensiveFixMarkers.IsFixShaped([line], []).Should().Be(expected);
    }

    [Fact]
    public void A_Rename_Is_Not_Tiered_High_However_Fix_Shaped_It_Looks()
    {
        var renameOnly = RenameDetector.IsRenameOnly(
            ["            var v = _lastOrderId ?? Guid.Empty.ToString();"],
            ["            var v = lastOrderId ?? Guid.Empty.ToString();"]);

        renameOnly.Should().BeTrue();

        // The text contains "??", so the old matcher called this a fix.
        CloneDriftClassifier.Classify(oneSided: true, fixShaped: true, renameOnly: true)
            .Should().Be(CloneDriftTier.Advisory);

        CloneDriftClassifier.Classify(oneSided: true, fixShaped: true, renameOnly: false)
            .Should().Be(CloneDriftTier.High);
    }

    [Fact]
    public void Adding_A_Guard_Is_Not_Mistaken_For_A_Rename()
    {
        RenameDetector.IsRenameOnly(
            [
                "    var v = _lastOrderId ?? Guid.Empty.ToString();",
                "    if (v is null) throw new ArgumentNullException(nameof(v));",
            ],
            ["    var v = lastOrderId;"]).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void A_Pure_Addition_Or_Deletion_Is_Not_A_Rename(int added, int removed)
    {
        // Nothing was renamed if one side is empty: there is no pair of texts to compare.
        var addedLines = added == 0 ? Array.Empty<string>() : ["    x = 1;"];
        var removedLines = removed == 0 ? Array.Empty<string>() : ["    y = 2;"];

        RenameDetector.IsRenameOnly(addedLines, removedLines).Should().BeFalse();
    }

[Fact]
    public void Marker_Matching_Stays_Case_Insensitive()
    {
        // Pre-existing contract, and cheap to keep once matching is boundary-anchored.
        DefensiveFixMarkers.IsFixShaped(["        IF (VALUE IS NULL)"], []).Should().BeTrue();
    }

    /// <summary>
    /// A hunk adding only whitespace, braces or semicolons changed no behaviour. It cannot be a
    /// defensive fix, and it is also why 4 High findings anchored on a blank line: the hunk had no
    /// code to point at.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("};")]
    [InlineData("        }")]
    public void A_Hunk_That_Adds_No_Code_Is_Not_A_Fix(params string[] addedLines)
    {
        DefensiveFixMarkers.IsFixShaped(addedLines, []).Should().BeFalse();
    }

    [Fact]
    public void One_Real_Line_Among_Blank_Ones_Still_Counts()
    {
        DefensiveFixMarkers.IsFixShaped(["", "{", "    var total = count ?? 0;"], []).Should().BeTrue();
    }

    // ---------- helpers: mirror the production formatting so the tests cannot drift from it ----------



}