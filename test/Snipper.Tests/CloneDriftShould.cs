namespace Snipper.Tests;

using System.Text.Json;
using FluentAssertions;
using Snipper.Cli;
using Snipper.Analysis;
using Xunit;

/// <summary>
/// Wave 4 / 4C: clone drift — the temporal one-sided fix.
///
/// SNP0031 proves that two regions are token-identical *now*. 4C adds the other half: a commit
/// that changed one copy of such a pair and left its siblings alone.
///
/// Two properties carry the feature, and the second is the one that keeps it honest.
///
/// 1. The classification. A one-sided, defensive-fix-shaped change is a real inconsistent-fix
///    window: the sibling shipped without the guard until it was caught up. Anything less
///    fix-shaped is only a prompt to look.
///
/// 2. **The coordinate discipline.** A member's clone region is anchored in HEAD line numbers,
///    but a patch carries its parent's. Rather than reconcile the two across arbitrary history —
///    where a plausible-looking bug would produce confident nonsense — 4C examines only the most
///    recent commit touching each copy. For that commit the post-image *is* HEAD, so both live in
///    one coordinate system. The tests below pin that scope rather than leaving it implied.
/// </summary>
// Shares a collection with the other CliRunner-driving classes: Spectre.Console's Status is
// process-wide exclusive, so two CliRunner.RunAsync calls cannot overlap in one test host.
[Collection("CliRuns")]
public sealed class CloneDriftShould : IDisposable
{
    private const string Guard =
        """
        public string Render(string label, int width, int height)
        {
            if (string.IsNullOrEmpty(label))
            {
                return string.Empty;
            }

            Span<char> buffer = stackalloc char[width * height + 64];
        """;

    private readonly string _workspace;

    public CloneDriftShould()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"snipper-drift-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
    }

    // ---------- classification ----------

    [Fact]
    public void Call_A_One_Sided_Defensive_Fix_High()
    {
        CloneDriftClassifier.Classify(oneSided: true, fixShaped: true)
            .Should().Be(CloneDriftTier.High);
    }

    [Fact]
    public void Call_A_One_Sided_Non_Fix_Change_Advisory()
    {
        // A one-sided refactor or optimisation is real drift but is not evidence of a defect,
        // and promoting it would be how a drift rule gets switched off.
        CloneDriftClassifier.Classify(oneSided: true, fixShaped: false)
            .Should().Be(CloneDriftTier.Advisory);
    }

    [Fact]
    public void Report_No_Drift_When_The_Change_Was_Not_One_Sided()
    {
        CloneDriftClassifier.Classify(oneSided: false, fixShaped: true).Should().BeNull();
        CloneDriftClassifier.Classify(oneSided: false, fixShaped: false).Should().BeNull();
    }

    // ---------- defensive-fix markers ----------

    [Theory]
    [InlineData("if (value is null)")]
    [InlineData("if (list.Count > 0)")]
    [InlineData("try { Run(); }")]
    [InlineData("catch (InvalidOperationException)")]
    [InlineData("throw new ArgumentNullException(nameof(value));")]
    [InlineData("var x = other ?? fallback;")]
    [InlineData("if (text.IsNullOrEmpty())")]
    public void Treat_A_Defensive_Construct_As_Fix_Shaped(string line)
    {
        DefensiveFixMarkers.IsFixShaped([line]).Should().BeTrue();
    }

    [Theory]
    [InlineData("var total = 0;")]
    [InlineData("return buffer[..cursor].TrimEnd('/');")]
    [InlineData("// renamed for clarity")]
    [InlineData("builder.Append(caption);")]
    public void Leave_Ordinary_Code_Unclassified(string line)
    {
        DefensiveFixMarkers.IsFixShaped([line]).Should().BeFalse();
    }

    [Fact]
    public void Treat_No_Added_Lines_As_Not_Fix_Shaped()
    {
        // A pure deletion carries no added text; calling that a defensive fix would be backwards.
        DefensiveFixMarkers.IsFixShaped([]).Should().BeFalse();
    }

    [Fact]
    public void Match_Markers_Regardless_Of_Case_Or_Indentation()
    {
        DefensiveFixMarkers.IsFixShaped(["        IF (VALUE IS NULL)"]).Should().BeTrue();
    }

    // ---------- patch parsing ----------

    [Fact]
    public void Parse_Hunk_Coordinates_And_Added_Lines()
    {
        // Exercises the real patch parser against real git output rather than a canned string:
        // a hand-written patch proves the parser agrees with a fixture, not with git.
        var repo = NewRepo();
        Write(repo, "A.cs", "one\ntwo\nthree\n");
        Commit(repo, "first");

        Write(repo, "A.cs", "one\ntwo\nthree\nfour\nfive\n");
        Commit(repo, "insert two lines");
        var second = Head(repo);

        var patches = GitHistory.TryGetPatches(repo, [second]);

        patches.Should().ContainSingle();
        var hunks = patches[0].HunksByPath["A.cs"];
        hunks.Should().ContainSingle();
        // git reports the insertion after line 3 as "@@ -3,0 +4,2 @@": nothing consumed from the
        // pre-image, two lines produced starting at 4.
        hunks[0].OldStart.Should().Be(3);
        hunks[0].OldCount.Should().Be(0);
        hunks[0].NewStart.Should().Be(4);
        hunks[0].NewCount.Should().Be(2);
        hunks[0].AddedLines.Should().HaveCount(2);
        hunks[0].AddedLines[0].Trim().Should().Be("four");
        hunks[0].AddedLines[1].Trim().Should().Be("five");
    }

    [Fact]
    public void Attribute_Each_Hunk_To_Its_Own_File_In_A_Multi_File_Commit()
    {
        // The parser walks one text stream, so a multi-file patch is where a hunk could be
        // credited to the wrong file. Both files change here.
        var repo = NewRepo();
        Write(repo, "A.cs", "one\n");
        Write(repo, "B.cs", "one\n");
        Commit(repo, "first");

        Write(repo, "A.cs", "one\nalpha\n");
        Write(repo, "B.cs", "one\nbeta\n");
        Commit(repo, "touch both");

        var patches = GitHistory.TryGetPatches(repo, [Head(repo)]);

        patches.Should().ContainSingle();
        var byPath = patches[0].HunksByPath;
        byPath.Should().ContainKey("A.cs");
        byPath.Should().ContainKey("B.cs");
        byPath["A.cs"].SelectMany(h => h.AddedLines).Should().ContainSingle()
            .Which.Should().Contain("alpha");
        byPath["B.cs"].SelectMany(h => h.AddedLines).Should().ContainSingle()
            .Which.Should().Contain("beta");
    }

    [Fact]
    public void Report_No_Hunks_For_A_Commit_That_Changes_Nothing()
    {
        var repo = NewRepo();
        Write(repo, "A.cs", "one\n");
        Commit(repo, "first");
        Run(repo, "commit -q --allow-empty -m \"empty\"");
        var empty = Head(repo);

        GitHistory.TryGetPatches(repo, [empty]).Should().BeEmpty(
            "a commit with no diff has no hunks and must not count as drift evidence");
    }


    // ---------- region containment ----------

    [Fact]
    public void Treat_A_Hunk_Inside_The_Region_As_Touching_It()
    {
        CloneDriftClassifier.HunkTouchesRegion(
            new PatchHunk(OldStart: 20, OldCount: 2, NewStart: 20, NewCount: 4, AddedLines: []),
            regionStart: 10, regionEnd: 30).Should().BeTrue();
    }

    [Fact]
    public void Treat_A_Hunk_At_The_Regions_Edge_As_Touching_It()
    {
        // A guard inserted immediately above a copied block is the canonical drift shape, and
        // git reports it as adjacent to the region rather than inside it.
        CloneDriftClassifier.HunkTouchesRegion(
            new PatchHunk(OldStart: 9, OldCount: 1, NewStart: 9, NewCount: 5, AddedLines: []),
            regionStart: 10, regionEnd: 30).Should().BeTrue();
    }

    [Fact]
    public void Treat_A_Hunk_Far_From_The_Region_As_Unrelated()
    {
        CloneDriftClassifier.HunkTouchesRegion(
            new PatchHunk(OldStart: 400, OldCount: 3, NewStart: 400, NewCount: 5, AddedLines: []),
            regionStart: 10, regionEnd: 30).Should().BeFalse();
    }

    // ---------- git history against a real repository ----------

    [Fact]
    public void Index_The_Most_Recent_Commit_Per_Path_In_One_Call()
    {
        var repo = NewRepo();
        Write(repo, "A.cs", "one\n");
        Commit(repo, "a");
        Write(repo, "B.cs", "one\n");
        Commit(repo, "b");
        Write(repo, "A.cs", "one\ntwo\n");
        Commit(repo, "a again");

        var index = GitHistory.TryGetMostRecentCommits(repo, ["A.cs", "B.cs", "Missing.cs"]);

        index.Should().NotBeNull();
        index!.Should().ContainKey("A.cs");
        index.Should().ContainKey("B.cs");
        index.Should().NotContainKey("Missing.cs", "a path with no history has no most-recent commit");
        index["A.cs"].Message.Should().Be("a again");
        index["B.cs"].Message.Should().Be("b");
    }

    [Fact]
    public void Return_No_Index_Outside_A_Git_Repository()
    {
        GitHistory.TryGetMostRecentCommits(_workspace, ["A.cs"]).Should().BeNull();
    }

    [Fact]
    public void Fetch_Patches_For_The_Requested_Commits()
    {
        var repo = NewRepo();
        Write(repo, "A.cs", "one\n");
        Commit(repo, "a");
        Write(repo, "A.cs", "one\ntwo\n");
        var second = Head(repo);
        Commit(repo, "a again");

        var patches = GitHistory.TryGetPatches(repo, [second]);

        patches.Should().NotBeNull();
        patches.Should().ContainSingle();
        patches![0].HunksByPath.Should().ContainKey("A.cs");
        patches[0].HunksByPath["A.cs"].Should().ContainSingle();
    }

    [Fact]
    public void Return_No_Patches_For_An_Unknown_Commit()
    {
        var repo = NewRepo();
        Write(repo, "A.cs", "one\n");
        Commit(repo, "a");

        GitHistory.TryGetPatches(repo, ["0000000000000000000000000000000000000000"])
            .Should().BeEmpty();
    }

    // ---------- end to end ----------

    [Fact]
    public async Task Report_A_One_Sided_Defensive_Fix_As_High()
    {
        var workspace = SampleWorkspace();
        try
        {
            SeedInconsistentFix(workspace);
            using var document = await RunAsync(workspace);

            var drift = document.RootElement.GetProperty("findings")
                .EnumerateArray()
                .Where(f => f.GetProperty("ruleId").GetString() == "SNP0032")
                .ToList();

            drift.Should().ContainSingle("the seeded scenario contains exactly one inconsistent fix");
            var finding = drift[0];
            finding.GetProperty("certainty").GetString().Should().Be("High");
            finding.GetProperty("category").GetString().Should().Be("CloneDrift");
            finding.GetProperty("message").GetString().Should().Contain("guard empty label");
            finding.GetProperty("message").GetString().Should().Contain("CloneFixtures.cs",
                "the message must name the copy that was left behind");
        }
        finally
        {
            Delete(workspace);
        }
    }

    [Fact]
    public async Task Report_No_Drift_When_Both_Copies_Were_Fixed_In_One_Commit()
    {
        var workspace = SampleWorkspace();
        try
        {
            // Same guard, applied to both copies in a single commit: consistent by construction.
            ApplyGuard(workspace, "App/CloneFixturesMirrored.cs");
            ApplyGuard(workspace, "CoreLib/CloneFixtures.cs");
            Commit(workspace, "fix both copies");

            using var document = await RunAsync(workspace);

            DriftFindings(document).Should().BeEmpty(
                "a change applied to every copy is not drift");
        }
        finally
        {
            Delete(workspace);
        }
    }

    [Fact]
    public async Task Report_No_Drift_For_An_Unrelated_Later_Edit()
    {
        var workspace = SampleWorkspace();
        try
        {
            Write(workspace, "App/CloneFixturesMirrored.cs",
                File.ReadAllText(Path.Combine(workspace, "App", "CloneFixturesMirrored.cs"))
                    + "\n// an unrelated trailing comment\n");
            Commit(workspace, "docs: trailing comment");

            using var document = await RunAsync(workspace);

            DriftFindings(document).Should().BeEmpty(
                "a comment outside the cloned region is not a one-sided fix");
        }
        finally
        {
            Delete(workspace);
        }
    }

    [Fact]
    public async Task Say_Nothing_Rather_Than_Failing_Outside_A_Git_Repository()
    {
        var workspace = SampleWorkspace(git: false);
        try
        {
            var exitCode = await CliRunner.RunAsync(
                [ProjectPath(workspace), Path.Combine(workspace, "report.json"), "--clone-drift"]);

            exitCode.Should().Be(0, "4C must degrade like GitMetadata, never fail the run");
            DriftFindings(JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(workspace, "report.json")))).Should().BeEmpty();
        }
        finally
        {
            Delete(workspace);
        }
    }

    [Fact]
    public async Task Enable_Duplicate_Detection_Implied_By_The_Flag()
    {
        var workspace = SampleWorkspace();
        try
        {
            // --clone-drift alone must be enough; asking for two flags for one feature would be
            // user-hostile, and the two share a single shingling pass.
            using var document = await RunAsync(workspace);

            document.RootElement.GetProperty("findings")
                .EnumerateArray()
                .Any(f => f.GetProperty("ruleId").GetString() == "SNP0031")
                .Should().BeTrue();
        }
        finally
        {
            Delete(workspace);
        }
    }

    // ---------- helpers ----------

    private static List<JsonElement> DriftFindings(JsonDocument document) =>
        document.RootElement.GetProperty("findings")
            .EnumerateArray()
            .Where(f => f.GetProperty("ruleId").GetString() == "SNP0032")
            .ToList();

    /// <summary>
    /// Runs the tool over <paramref name="workspace"/> and returns the parsed report. Builds no
    /// history: callers seed whatever scenario they are asserting about, so a negative test cannot
    /// accidentally create the very drift it is checking for.
    /// </summary>
    private static async Task<JsonDocument> RunAsync(string workspace)
    {
        var reportPath = Path.Combine(workspace, "report.json");
        var exitCode = await CliRunner.RunAsync(
            [ProjectPath(workspace), reportPath, "--clone-drift"]);
        exitCode.Should().Be(0);

        return JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
    }

    /// <summary>
    /// Seeds the fixture with a two-commit inconsistency: copy A is fixed, and copy B is only
    /// brought in line by a later commit. At HEAD they are clones again, which is precisely the
    /// state 4C can reason about — a fix still in place destroys the clone set.
    /// </summary>
    private static void SeedInconsistentFix(string workspace)
    {
        ApplyGuard(workspace, "App/CloneFixturesMirrored.cs");
        Commit(workspace, "fix(A): guard empty label");

        ApplyGuard(workspace, "CoreLib/CloneFixtures.cs");
        Commit(workspace, "fix(B): guard empty label (later)");
    }

    private static void ApplyGuard(string workspace, string relativePath)
    {
        var path = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var text = File.ReadAllText(path);
        var anchor = "Span<char> buffer = stackalloc char[width * height + 64];";

        text.Should().Contain(anchor, "the fixture's clone body must still contain its anchor line");
        var guard = string.Join(
            "\n",
            [
                "        if (string.IsNullOrEmpty(label))",
                "        {",
                "            return string.Empty;",
                "        }",
                string.Empty,
            ]);

        File.WriteAllText(path, text.Replace(anchor, guard + anchor, StringComparison.Ordinal));
    }

    private string SampleWorkspace(bool git = true)
    {
        var path = Path.Combine(Path.GetTempPath(), $"snipper-drift-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "TestAssets", "SampleApp"), path);

        if (git)
        {
            NewRepo(path);
            Write(path, ".gitignore", "report.json\nbin/\nobj/\n");
            Commit(path, "seed");
        }

        return path;
    }

    private static string ProjectPath(string workspace) =>
        Path.Combine(workspace, "App", "App.csproj");

    private string NewRepo()
    {
        NewRepo(_workspace);
        return _workspace;
    }

    private static void NewRepo(string path)
    {
        Directory.CreateDirectory(path);
        Run(path, "init -q");
        Run(path, "config user.email dev@snipper.invalid");
        Run(path, "config user.name Snipper Test");
    }

    private static void Write(string repo, string name, string content) =>
        File.WriteAllText(Path.Combine(repo, name.Replace('/', Path.DirectorySeparatorChar)), content);

    private static void Commit(string repo, string message)
    {
        Run(repo, "add -A");
        Run(repo, $"commit -q -m \"{message}\"");
    }

    private static string Head(string repo) => Run(repo, "rev-parse HEAD").Trim();

    private static string Run(string workingDirectory, string arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = System.Diagnostics.Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start git {arguments}");

        // Drain both pipes before waiting; reading one to end and then calling WaitForExit
        // deadlocks as soon as the child fills the other pipe's buffer.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"git {arguments} did not exit within 60s");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} exited {process.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
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

    /// <summary>
    /// Clears the read-only attribute git puts on its own object files; without it
    /// <c>Directory.Delete(recursive)</c> is refused on Windows and every run leaks a repository.
    /// </summary>
    private static void Delete(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => Delete(_workspace);
}
