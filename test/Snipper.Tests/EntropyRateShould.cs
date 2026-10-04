namespace Snipper.Tests;

using System.Text.Json;
using FluentAssertions;
using Snipper.Cli;
using Xunit;

/// <summary>
/// Wave 4 / 4B: entropy rate (findings added per kLOC changed) and its opt-in gate.
///
/// Two properties are under test, and the second is the one that matters more.
///
/// 1. The arithmetic. Trivial, but the floor and the divide-by-zero guard are not, because a
///    naive <c>new / (lines / 1000)</c> throws on a zero-line change and reports nonsense on a
///    small one.
///
/// 2. **The gate never fires on a number that was not measured.** Measured on this repository's
///    history: holding quality constant at one new finding, the per-change rate spans
///    200/kLOC at 5 lines to 0.5/kLOC at 2000 lines — a 400x spread driven purely by change
///    size. Worse, an unmeasurable denominator would otherwise compute to 0.00, and 0.00 passes
///    any budget. So every unavailable input gets a named status, and every non-scored status
///    must leave the gate silent.
///
/// This is deliberately the opposite of a normal gate, which fails closed. It fails open,
/// because a metric that breaks builds when git is missing trains people to switch it off.
/// </summary>
// Shares a collection with CliRunnerShould and BaselineChurnShould: Spectre.Console's Status
// is process-wide exclusive, so two CliRunner.RunAsync calls cannot overlap in one test host.
[Collection("CliRuns")]
public sealed class EntropyRateShould : IDisposable
{
    private const string Sha1 = "1111111111111111111111111111111111111111";
    private const string Sha2 = "2222222222222222222222222222222222222222";

    private readonly string _workspace;

    public EntropyRateShould()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"snipper-entropy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
    }

    // ---------- scoring ----------

    [Fact]
    public void Score_The_Rate_When_Both_Halves_Are_Measureable()
    {
        var result = EntropyRateCalculator.Compute(Measureable(newFindings: 5, changedLines: 250));

        result.Status.Should().Be(EntropyRateStatus.Scored);
        result.IsScored.Should().BeTrue();
        result.FindingsPerKloc.Should().Be(20.0);
    }

    [Fact]
    public void Express_The_Rate_As_Findings_Per_Thousand_Changed_Lines()
    {
        // 1 finding in a 251-line change - this repository's median commit - must read as
        // ~4/kLOC, which is the figure that made the per-change budget untenable.
        EntropyRateCalculator.Compute(Measureable(newFindings: 1, changedLines: 251))
            .FindingsPerKloc.Should().BeApproximately(3.984, 0.001);
    }

    // ---------- every unmeasurable input is named, never silently zero ----------

    [Fact]
    public void Refuse_To_Score_When_The_Baseline_Was_Just_Seeded()
    {
        // A first run has an empty recorded baseline, so all 256 fixture findings classify as
        // "new". Reporting that as a rate would be meaningless, not merely large.
        var result = EntropyRateCalculator.Compute(
            Measureable(newFindings: 256, changedLines: 500) with { BaselineFileExisted = false });

        result.Status.Should().Be(EntropyRateStatus.BaselineSeeded);
        result.IsScored.Should().BeFalse();
        result.FindingsPerKloc.Should().BeNull();
    }

    [Fact]
    public void Refuse_To_Score_Without_A_Baseline_Commit_Reference()
    {
        // A baseline written before 4B has fingerprints but no commit, so the change range
        // cannot be recovered. Re-baselining is the fix; guessing a range is not.
        var result = EntropyRateCalculator.Compute(
            Measureable(newFindings: 3, changedLines: 500) with { BaselineCommitSha = null });

        result.Status.Should().Be(EntropyRateStatus.NoBaselineReference);
        result.IsScored.Should().BeFalse();
    }

    [Fact]
    public void Refuse_To_Score_Outside_A_Git_Repository()
    {
        var result = EntropyRateCalculator.Compute(
            Measureable(newFindings: 3, changedLines: 500) with
            {
                IsGitRepository = false,
                HeadCommitSha = null,
            });

        result.Status.Should().Be(EntropyRateStatus.NotAGitRepository);
        result.IsScored.Should().BeFalse();
    }

    [Fact]
    public void Refuse_To_Score_A_Dirty_Working_Tree()
    {
        // The analysed bytes are not the commit, so a diff-derived denominator would describe
        // a different tree than the one that produced the numerator.
        var result = EntropyRateCalculator.Compute(
            Measureable(newFindings: 3, changedLines: 500) with { WorkingTreeDirty = true });

        result.Status.Should().Be(EntropyRateStatus.DirtyWorkingTree);
        result.IsScored.Should().BeFalse();
    }

    [Fact]
    public void Refuse_To_Score_When_The_Change_Lines_Are_Unavailable()
    {
        var result = EntropyRateCalculator.Compute(
            Measureable(newFindings: 3, changedLines: 500) with { ChangedLines = null });

        result.Status.Should().Be(EntropyRateStatus.NotAGitRepository);
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Refuse_To_Score_When_Nothing_Changed()
    {
        var result = EntropyRateCalculator.Compute(
            Measureable(newFindings: 3, changedLines: 500) with { HeadCommitSha = Sha1 });

        result.Status.Should().Be(EntropyRateStatus.UnchangedRange);
        result.IsScored.Should().BeFalse();
    }

    [Fact]
    public void Refuse_To_Score_Below_The_Minimum_Change()
    {
        var result = EntropyRateCalculator.Compute(
            Measureable(newFindings: 1, changedLines: 49, minimumLines: 50));

        result.Status.Should().Be(EntropyRateStatus.BelowMinimumChange);
        result.FindingsPerKloc.Should().NotBeNull("the figure stays visible, it is just not scored");
        result.IsScored.Should().BeFalse();
    }

    // ---------- the divide-by-zero guards ----------

    [Fact]
    public void Never_Divide_By_Zero_For_A_Zero_Line_Change()
    {
        // Reachable for real: a commit touching only file modes, or only binary files, has a
        // baseline SHA that differs from HEAD and zero changed lines.
        var result = EntropyRateCalculator.Compute(Measureable(newFindings: 0, changedLines: 0));

        result.IsScored.Should().BeFalse();
        result.Status.Should().Be(EntropyRateStatus.BelowMinimumChange);
        result.FindingsPerKloc.Should().BeNull();
    }

    [Fact]
    public void Clamp_A_Non_Positive_Minimum_So_The_Floor_Cannot_Be_Opened()
    {
        // --entropy-min-lines 0 must not turn a zero-line change into a division by zero.
        var result = EntropyRateCalculator.Compute(
            Measureable(newFindings: 4, changedLines: 0, minimumLines: 0));

        result.IsScored.Should().BeFalse();
        result.FindingsPerKloc.Should().BeNull();
    }

    [Fact]
    public void Report_The_Effective_Floor_When_It_Was_Clamped()
    {
        // --entropy-min-lines -5 must clamp to 1 and still refuse a zero-line change, rather
        // than opening the floor and dividing by zero.
        var result = EntropyRateCalculator.Compute(
            Measureable(newFindings: 4, changedLines: 0, minimumLines: -5));

        result.MinimumLines.Should().Be(1);
        result.Status.Should().Be(EntropyRateStatus.BelowMinimumChange);
    }

    // ---------- the gate ----------

    [Theory]
    [InlineData("BaselineSeeded")]
    [InlineData("NoBaselineReference")]
    [InlineData("NotAGitRepository")]
    [InlineData("DirtyWorkingTree")]
    [InlineData("UnchangedRange")]
    [InlineData("BelowMinimumChange")]
    public void Never_Engage_The_Gate_For_An_Unscored_Status(string statusName)
    {
        // Named as strings because xunit cannot pass an internal enum through InlineData.
        Enum.TryParse<EntropyRateStatus>(statusName, out var status).Should().BeTrue();

        var result = Unscored(status, newFindings: 9_999);

        result.IsScored.Should().BeFalse();
        result.Exceeds(budget: 0.0001).Should().BeFalse(
            "an unmeasured rate must never fail a build, however many findings were added");
    }

    [Fact]
    public void Engage_The_Gate_When_The_Scored_Rate_Exceeds_The_Budget()
    {
        var result = EntropyRateCalculator.Compute(Measureable(newFindings: 50, changedLines: 100));

        result.IsScored.Should().BeTrue();
        result.Exceeds(budget: 10.0).Should().BeTrue();
    }

    [Fact]
    public void Not_Engage_The_Gate_At_Exactly_The_Budget()
    {
        // A budget is a ceiling, so the boundary is inclusive. Rounding either way here would
        // make the same commit fail or pass depending on float noise.
        var result = EntropyRateCalculator.Compute(Measureable(newFindings: 10, changedLines: 100));

        result.FindingsPerKloc.Should().Be(100.0);
        result.Exceeds(budget: 100.0).Should().BeFalse();
        result.Exceeds(budget: 99.999).Should().BeTrue();
    }

    [Fact]
    public void Not_Engage_The_Gate_Without_A_Budget()
    {
        EntropyRateCalculator.Compute(Measureable(newFindings: 5_000, changedLines: 10))
            .Exceeds(budget: null).Should().BeFalse();
    }

    // ---------- resolved findings ----------

    [Fact]
    public void Count_Resolved_Findings_As_Known_Minus_Current()
    {
        var known = new HashSet<string>(["a", "b", "c"], StringComparer.Ordinal);
        var current = new HashSet<string>(["b", "c", "d"], StringComparer.Ordinal);

        EntropyRateCalculator.CountResolved(known, current).Should().Be(1);
    }

    [Fact]
    public void Resolve_Nothing_When_The_Baseline_Only_Grew()
    {
        var known = new HashSet<string>(["a"], StringComparer.Ordinal);
        var current = new HashSet<string>(["a", "b"], StringComparer.Ordinal);

        EntropyRateCalculator.CountResolved(known, current).Should().Be(0);
    }

    // ---------- ledger ----------

    [Fact]
    public void Replace_An_Existing_Entry_For_The_Same_Commit()
    {
        // A CI re-run on one commit must not append a second row, or the ledger grows without
        // bound and every rate is double-counted.
        var path = LedgerPath();
        EntropyLedger.Append(path, Entry(Sha1, newFindings: 3, lines: 100));
        EntropyLedger.Append(path, Entry(Sha1, newFindings: 4, lines: 100));

        var ledger = EntropyLedger.Load(path);
        ledger.Entries.Should().HaveCount(1);
        ledger.Entries[0].NewFindings.Should().Be(4);
    }

    [Fact]
    public void Sort_Ledger_Entries_By_Commit_For_Deterministic_Diffs()
    {
        var path = LedgerPath();
        EntropyLedger.Append(path, Entry(Sha2, newFindings: 1, lines: 10));
        EntropyLedger.Append(path, Entry(Sha1, newFindings: 2, lines: 20));

        var ledger = EntropyLedger.Load(path);
        ledger.Entries.Select(e => e.CommitSha).Should().ContainInOrder(Sha1, Sha2);
    }

    [Fact]
    public async Task Round_Trip_The_Ledger_Through_Json()
    {
        var path = LedgerPath();
        EntropyLedger.Append(path, Entry(Sha1, newFindings: 7, lines: 350, rate: 20.0));

        var json = await File.ReadAllTextAsync(path);
        using var document = JsonDocument.Parse(json);

        document.RootElement.GetProperty("version").GetString().Should().Be("1");
        var entry = document.RootElement.GetProperty("entries")[0];
        entry.GetProperty("commitSha").GetString().Should().Be(Sha1);
        entry.GetProperty("newFindings").GetInt32().Should().Be(7);
        entry.GetProperty("linesChanged").GetInt32().Should().Be(350);
        entry.GetProperty("findingsPerKloc").GetDouble().Should().Be(20.0);
        entry.GetProperty("scored").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Load_An_Absent_Ledger_As_Empty_Rather_Than_Failing()
    {
        var ledger = EntropyLedger.Load(Path.Combine(_workspace, "no-such-ledger.json"));

        ledger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Load_A_Corrupt_Ledger_As_Empty_Rather_Than_Failing()
    {
        // Same contract as BaselineService.Load: a damaged state file must not fail the run.
        var path = LedgerPath();
        File.WriteAllText(path, "{ this is not json");

        EntropyLedger.Load(path).Entries.Should().BeEmpty();
    }

    // ---------- monthly aggregation ----------

    [Fact]
    public void Aggregate_Ledger_Entries_By_Month()
    {
        var ledger = new EntropyLedgerFile(
            "1",
            [
                Entry(Sha1, newFindings: 2, lines: 100, recordedAt: "2026-08-14T10:00:00Z"),
                Entry(Sha2, newFindings: 8, lines: 300, recordedAt: "2026-08-31T23:00:00Z"),
            ]);

        var months = EntropyLedger.MonthlyRates(ledger);

        months.Should().HaveCount(1);
        months[0].Month.Should().Be("2026-08");
        months[0].NewFindings.Should().Be(10);
        months[0].LinesChanged.Should().Be(400);
        months[0].FindingsPerKloc.Should().Be(25.0);
    }

    [Fact]
    public void Order_Months_Chronologically()
    {
        var ledger = new EntropyLedgerFile(
            "1",
            [
                Entry(Sha2, newFindings: 1, lines: 100, recordedAt: "2026-09-02T00:00:00Z"),
                Entry(Sha1, newFindings: 1, lines: 100, recordedAt: "2026-08-02T00:00:00Z"),
            ]);

        EntropyLedger.MonthlyRates(ledger).Select(m => m.Month)
            .Should().ContainInOrder("2026-08", "2026-09");
    }

    [Fact]
    public void Report_No_Rate_For_A_Month_With_No_Changed_Lines()
    {
        var ledger = new EntropyLedgerFile(
            "1",
            [Entry(Sha1, newFindings: 0, lines: 0, recordedAt: "2026-08-02T00:00:00Z")]);

        var month = EntropyLedger.MonthlyRates(ledger)[0];

        month.LinesChanged.Should().Be(0);
        month.FindingsPerKloc.Should().BeNull();
    }

    // ---------- numstat (real git) ----------

    [Fact]
    public void Count_Changed_Lines_Between_Two_Commits()
    {
        var repo = InitRepo();
        Write(repo, "a.txt", "one\ntwo\n");
        Commit(repo, "first");
        var first = HeadSha(repo);

        // git computes a minimal diff, so "one" is untouched: one line added, one removed.
        Write(repo, "a.txt", "one\nthree\n");
        Commit(repo, "second");

        var entries = GitMetadata.TryGetNumstat(repo, first, HeadSha(repo));

        entries.Should().NotBeNull();
        entries!.Should().ContainSingle();
        entries[0].Added.Should().Be(1);
        entries[0].Deleted.Should().Be(1);
    }

    [Fact]
    public void Exclude_Binary_Files_From_The_Line_Count()
    {
        // numstat prints "-" for binary files. Parsing that as a number, or counting a 4 MB
        // asset as 4 million changed lines, would both wreck the denominator.
        var repo = InitRepo();
        File.WriteAllBytes(Path.Combine(repo, "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);
        Commit(repo, "binary");
        var first = HeadSha(repo);

        Write(repo, "a.txt", "one\ntwo\n");
        File.WriteAllBytes(Path.Combine(repo, "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0x02, 0x02]);
        Commit(repo, "change");

        var entries = GitMetadata.TryGetNumstat(repo, first, HeadSha(repo));

        entries.Should().ContainSingle();
        entries![0].Path.Should().Be("a.txt");
    }

    [Fact]
    public void Return_No_Numstat_Outside_A_Git_Repository()
    {
        GitMetadata.TryGetNumstat(_workspace, Sha1, Sha2).Should().BeNull();
    }

    [Fact]
    public void Return_No_Numstat_For_An_Unknown_Revision()
    {
        var repo = InitRepo();
        Write(repo, "a.txt", "one\n");
        Commit(repo, "first");

        GitMetadata.TryGetNumstat(repo, Sha1, HeadSha(repo)).Should().BeNull();
    }

    // ---------- CLI surface ----------

    [Fact]
    public async Task Reject_Entropy_Rate_Without_A_Baseline()
    {
        // "New" is defined relative to a recorded baseline. Without one there is no rate, and a
        // silent zero here would be indistinguishable from a genuinely clean change.
        var exitCode = await CliRunner.RunAsync([ProjectPath(), "--entropy-rate"]);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task Report_Not_Scored_When_The_Baseline_Is_Seeded()
    {
        var workspace = SampleWorkspace();
        try
        {
            var report = Path.Combine(workspace, "report.json");
            var exitCode = await CliRunner.RunAsync(
                [ProjectPath(workspace), report, "--baseline", BaselinePath(workspace), "--entropy-rate"]);

            exitCode.Should().Be(0);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(report));
            var entropy = document.RootElement.GetProperty("entropyRate");

            // PascalCase, matching the enum values 4A already emits in this same document
            // ("PathGlob", ReportSchemaShould.cs:116). Consistency inside one report beats a
            // prettier casing that would make 4A and 4B disagree.
            entropy.GetProperty("status").GetString().Should().Be("BaselineSeeded");
            entropy.GetProperty("scored").GetBoolean().Should().BeFalse();
        }
        finally
        {
            Delete(workspace);
        }
    }

    [Fact]
    public async Task Omit_The_Entropy_Section_Entirely_Without_The_Flag()
    {
        var workspace = SampleWorkspace();
        try
        {
            var report = Path.Combine(workspace, "report.json");
            await CliRunner.RunAsync([ProjectPath(workspace), report]);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(report));
            document.RootElement.TryGetProperty("entropyRate", out _).Should().BeFalse(
                "existing consumers must not see a new field they did not ask for");
        }
        finally
        {
            Delete(workspace);
        }
    }

    [Fact]
    public async Task Write_A_Ledger_Entry_For_A_Scored_Run()
    {
        var workspace = SampleWorkspace();
        try
        {
            var baseline = BaselinePath(workspace);
            var ledger = LedgerPathIn(workspace);

            // Run 1 seeds the baseline. That has no rate, so it must not be recorded.
            await CliRunner.RunAsync(
                [ProjectPath(workspace), "--baseline", baseline, "--entropy-ledger", ledger]);
            EntropyLedger.Load(ledger).Entries.Should().BeEmpty(
                "a seeded baseline has no meaningful rate, so it must not enter the ledger");

            // Commit a change big enough to clear the floor, then re-run.
            Write(workspace, "CoreLib", "Extra.cs", "// touched\n");
            Commit(workspace, "grow the change");

            await CliRunner.RunAsync(
                [ProjectPath(workspace), "--baseline", baseline, "--entropy-ledger", ledger,
                 "--entropy-min-lines", "1"]);

            var entries = EntropyLedger.Load(ledger).Entries;
            entries.Should().HaveCount(1);
            entries[0].LinesChanged.Should().BeGreaterThan(0);
            entries[0].Scored.Should().BeTrue();
            entries[0].FindingsPerKloc.Should().NotBeNull();
        }
        finally
        {
            Delete(workspace);
        }
    }

    [Fact]
    public async Task Refuse_To_Score_A_Dirty_Tree_End_To_End()
    {
        // The analysed bytes are not the commit, so a diff-derived denominator would describe
        // a different tree than the numerator. This is also the operational requirement it
        // creates: the baseline and ledger must be committed or gitignored, or the rate can
        // never be scored locally.
        var workspace = SampleWorkspace();
        try
        {
            var baseline = BaselinePath(workspace);
            await CliRunner.RunAsync([ProjectPath(workspace), "--baseline", baseline]);

            Write(workspace, "CoreLib", "Extra.cs", "// uncommitted\n");

            var report = Path.Combine(workspace, "report.json");
            var exitCode = await CliRunner.RunAsync(
                [ProjectPath(workspace), report, "--baseline", baseline,
                 "--entropy-rate", "--entropy-min-lines", "1"]);

            exitCode.Should().Be(0);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(report));
            var entropy = document.RootElement.GetProperty("entropyRate");
            entropy.GetProperty("status").GetString().Should().Be("DirtyWorkingTree");
            entropy.GetProperty("scored").GetBoolean().Should().BeFalse();
        }
        finally
        {
            Delete(workspace);
        }
    }

    [Fact]
    public async Task Stay_Silent_When_The_Rate_Is_Unscored_Even_With_A_Budget()
    {
        var workspace = SampleWorkspace();
        try
        {
            var exitCode = await CliRunner.RunAsync(
                [ProjectPath(workspace), "--baseline", BaselinePath(workspace),
                 "--entropy-budget", "0.0001"]);

            exitCode.Should().Be(0, "the run that seeds a baseline must not be able to fail a build");
        }
        finally
        {
            Delete(workspace);
        }
    }

    [Fact]
    public async Task Fail_With_Distinct_Exit_Code_When_The_Budget_Is_Exceeded()
    {
        var workspace = SampleWorkspace();
        try
        {
            var baseline = BaselinePath(workspace);
            await CliRunner.RunAsync([ProjectPath(workspace), "--baseline", baseline]);

            // Re-baseline at HEAD, then introduce a genuine new finding (an unreferenced
            // private field, SNP0001). A comment-only change would add no findings, so the
            // rate would legitimately be 0.00 and the budget would never be breached.
            Write(
                workspace,
                "CoreLib",
                "EntropyProbe.cs",
                """
                namespace CoreLib;

                internal sealed class EntropyProbe
                {
                    private int _neverRead() => 42;
                }
                """);
            Commit(workspace, "add dead code");

            var exitCode = await CliRunner.RunAsync(
                [ProjectPath(workspace), "--baseline", baseline,
                 "--entropy-budget", "0.0001", "--entropy-min-lines", "1"]);

            exitCode.Should().Be(3, "CI must be able to tell a policy failure from a snipper failure");
        }
        finally
        {
            Delete(workspace);
        }
    }

    // ---------- helpers ----------

    private static EntropyRateInputs Measureable(int newFindings, int changedLines, int minimumLines = 50) =>
        new(
            BaselineFileExisted: true,
            BaselineCommitSha: Sha1,
            HeadCommitSha: Sha2,
            IsGitRepository: true,
            WorkingTreeDirty: false,
            NewFindings: newFindings,
            ResolvedFindings: 0,
            ChangedLines: changedLines,
            MinimumLines: minimumLines);

    private static EntropyRateResult Unscored(EntropyRateStatus status, int newFindings) => status switch
    {
        EntropyRateStatus.BaselineSeeded => EntropyRateCalculator.Compute(
            Measureable(newFindings, 500) with { BaselineFileExisted = false }),
        EntropyRateStatus.NoBaselineReference => EntropyRateCalculator.Compute(
            Measureable(newFindings, 500) with { BaselineCommitSha = null }),
        EntropyRateStatus.NotAGitRepository => EntropyRateCalculator.Compute(
            Measureable(newFindings, 500) with { IsGitRepository = false, HeadCommitSha = null }),
        EntropyRateStatus.DirtyWorkingTree => EntropyRateCalculator.Compute(
            Measureable(newFindings, 500) with { WorkingTreeDirty = true }),
        EntropyRateStatus.UnchangedRange => EntropyRateCalculator.Compute(
            Measureable(newFindings, 500) with { HeadCommitSha = Sha1 }),
        EntropyRateStatus.BelowMinimumChange => EntropyRateCalculator.Compute(
            Measureable(newFindings, 500, minimumLines: 10_000)),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not an unscored status"),
    };

    private static EntropyLedgerEntry Entry(
        string sha,
        int newFindings,
        int lines,
        double? rate = null,
        string? recordedAt = null) =>
        new(
            CommitSha: sha,
            RecordedAtUtc: recordedAt ?? "2026-10-03T12:00:00Z",
            NewFindings: newFindings,
            ResolvedFindings: 0,
            LinesChanged: lines,
            BaselineCommitSha: null,
            FindingsPerKloc: rate,
            Scored: true);

    private string LedgerPath() => Path.Combine(_workspace, "entropy.json");

    private string SampleWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), $"snipper-entropy-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "TestAssets", "SampleApp"), path);
        InitRepo(path);

        // 4B refuses to score a dirty tree, and `git status --porcelain` reports untracked
        // files. A real repository commits its baseline or gitignores it; without this the
        // fixture's own artifacts would make every run refuse to score. `bin/` and `obj/` are
        // ignored for the same reason every .NET repo ignores them — otherwise the analysis
        // output itself is untracked, and `git add -A` would sweep it into the next commit.
        // Ignored files are not listed by `git status --porcelain`, which is the behaviour
        // being relied on.
        Write(
            path,
            ".gitignore",
            string.Join("\n", ["baseline.json", "entropy.json", "report.json", "bin/", "obj/", string.Empty]));

        Commit(path, "seed");
        return path;
    }

    private static string LedgerPathIn(string workspace) => Path.Combine(workspace, "entropy.json");

    private string ProjectPath() => ProjectPath(_workspace);

    private static string ProjectPath(string workspace) => Path.Combine(workspace, "CoreLib", "CoreLib.csproj");

    private static string BaselinePath(string workspace) => Path.Combine(workspace, "baseline.json");

    /// <summary>
    /// Creates a repository inside the per-test workspace, so <see cref="Dispose"/> reclaims it.
    /// Creating one directly in TEMP leaked a directory per call.
    /// </summary>
    private string InitRepo() => InitRepo(Path.Combine(_workspace, $"repo-{Guid.NewGuid():N}"));

    private static string InitRepo(string? path = null)
    {
        path ??= Path.Combine(Path.GetTempPath(), $"snipper-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        Run(path, "init -q");
        Run(path, "config user.email test@snipper.invalid");
        Run(path, "config user.name Snipper Test");
        return path;
    }

    private static void Write(string repo, string relativeDirectory, string name, string content)
    {
        var directory = Path.Combine(repo, relativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), content);
    }

    /// <summary>Writes a file at the repository root.</summary>
    private static void Write(string repo, string name, string content) =>
        Write(repo, string.Empty, name, content);

    private static void Commit(string repo, string message)
    {
        Run(repo, "add -A");
        Run(repo, $"commit -q -m \"{message}\"");
    }

    private static string HeadSha(string repo) => Run(repo, "rev-parse HEAD").Trim();

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

        // Drain both pipes *before* waiting. Reading one stream to end and then calling
        // WaitForExit deadlocks as soon as the child fills the other pipe's buffer.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"git {arguments} did not exit within 60s");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        // Fixture setup is strict on purpose. GitMetadata degrades silently by design, so a
        // broken fixture would otherwise present as a product bug three layers away.
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {arguments} exited {process.ExitCode}: {stderr.Trim()}");
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

    private static void Delete(string path)
    {
        ClearReadOnly(path);
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Clears the read-only attribute git puts on its own object files.
    ///
    /// Without this, <c>Directory.Delete(recursive)</c> is refused on Windows and every test
    /// run leaks a repository into TEMP — roughly a hundred per run here, which is how the
    /// problem was found. Swallowing the error alone would hide the leak rather than fix it.
    /// </summary>
    private static void ClearReadOnly(string path)
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        try
        {
            ClearReadOnly(_workspace);
            Directory.Delete(_workspace, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
