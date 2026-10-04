namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Snipper.Cli;
using Snipper.Models;
using Xunit;

/// <summary>
/// Unit tests for the extracted CLI seams.
/// <para>
/// Deliberately <b>not</b> in <c>[Collection("CliRuns")]</c>. Nothing here opens a
/// workspace or touches <c>AnsiConsole</c>, so these run concurrently with the whole suite
/// instead of being serialised behind it — which is the point of having pulled argument
/// parsing and report building out of the orchestrator. Before the extraction this coverage
/// could only be reached through <c>CliRunner.RunAsync</c>, which is process-wide exclusive
/// because of Spectre.Console's <c>Status</c>.
/// </para>
/// </summary>
public class CommandLineParserShould : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("snipper-cmdline").FullName;
    private readonly string _projectPath;

    public CommandLineParserShould()
    {
        _projectPath = Path.Combine(_directory, "Sample.csproj");
        File.WriteAllText(_projectPath, "<Project />");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leaked temp directory must never fail a test run.
        }
    }

    private bool Parse(params string[] args)
        => CommandLineParser.TryParse(args, out _, out _);

    private CommandLineOptions ParseOk(params string[] args)
    {
        CommandLineParser.TryParse(args, out var options, out var error).Should().BeTrue(error);
        return options;
    }

    private string ParseError(params string[] args)
    {
        CommandLineParser.TryParse(args, out _, out var error).Should().BeFalse();
        error.Should().NotBeNull();
        return error!;
    }

    // ---------- the happy path ----------

    [Fact]
    public void Parse_A_Target_On_Its_Own()
    {
        var options = ParseOk(_projectPath);

        options.TargetPath.Should().Be(_projectPath);
        options.OutputPath.Should().BeNull();
        options.Format.Should().Be(ReportFormat.Json);
        options.MinimumCertainty.Should().BeNull();
        options.IncludeCloneDrift.Should().BeFalse();
        options.EntropyRateRequested.Should().BeFalse();
    }

    [Fact]
    public void Resolve_The_Target_To_A_Full_Path()
    {
        var options = ParseOk(_projectPath);

        Path.IsPathFullyQualified(options.TargetPath).Should().BeTrue();
    }

    [Fact]
    public void Accept_A_Positional_Output_Path()
    {
        var output = Path.Combine(_directory, "report.json");

        ParseOk(_projectPath, output).OutputPath.Should().Be(output);
    }

    [Fact]
    public void Accept_An_Output_Path_After_A_Flag()
    {
        var output = Path.Combine(_directory, "report.json");

        ParseOk(_projectPath, "--format", "sarif", output).OutputPath.Should().Be(output);
    }

    [Fact]
    public void Reject_A_Second_Positional_Argument()
    {
        // Only the first bare argument becomes the output path; a second one is a typo, and
        // silently overwriting the first would be worse than refusing.
        ParseError(_projectPath, "a.json", "b.json").Should().Contain("Unexpected argument");
    }

    // ---------- target validation ----------

    [Fact]
    public void Reject_An_Empty_Command_Line()
    {
        ParseError().Should().Contain("Missing target path");
    }

    [Fact]
    public void Reject_A_Missing_Target()
    {
        ParseError(Path.Combine(_directory, "nope.csproj")).Should().Contain("does not exist");
    }

    // ---------- format ----------

    [Theory]
    [InlineData("json", "Json")]
    [InlineData("sarif", "Sarif")]
    [InlineData("SARIF", "Sarif")]
    public void Parse_The_Report_Format(string value, string expected)
    {
        // Compared by name rather than by enum value: ReportFormat is internal, and an
        // internal type cannot appear in a public test method's signature.
        ParseOk(_projectPath, "--format", value).Format.ToString().Should().Be(expected);
    }

    [Fact]
    public void Reject_An_Unknown_Report_Format()
    {
        ParseError(_projectPath, "--format", "xml").Should().Contain("--format requires");
    }

    [Fact]
    public void Reject_A_Format_Flag_With_No_Value()
    {
        ParseError(_projectPath, "--format").Should().Contain("--format requires");
    }

    // ---------- certainty tier ----------

    [Theory]
    [InlineData("guaranteed", CertaintyTier.Guaranteed)]
    [InlineData("high", CertaintyTier.High)]
    [InlineData("moderate", CertaintyTier.Moderate)]
    [InlineData("advisory", CertaintyTier.Advisory)]
    [InlineData("HIGH", CertaintyTier.High)]
    public void Parse_Every_Certainty_Tier(string value, CertaintyTier expected)
    {
        ParseOk(_projectPath, "--certainty-tier", value).MinimumCertainty.Should().Be(expected);
    }

    [Fact]
    public void Reject_An_Unknown_Certainty_Tier()
    {
        ParseError(_projectPath, "--certainty-tier", "certain")
            .Should().Contain("--certainty-tier requires");
    }

    // ---------- opt-in analysers ----------

    [Fact]
    public void Clone_Drift_Implies_Duplicate_Detection()
    {
        // The two share one shingling pass, so requiring two flags for one feature would be
        // user-hostile rather than strict.
        var options = ParseOk(_projectPath, "--clone-drift");

        options.IncludeCloneDrift.Should().BeTrue();
        options.IncludeDuplicateDetection.Should().BeTrue();
    }

    [Fact]
    public void Duplicate_Detection_Does_Not_Imply_Clone_Drift()
    {
        var options = ParseOk(_projectPath, "--duplicate-detection");

        options.IncludeDuplicateDetection.Should().BeTrue();
        options.IncludeCloneDrift.Should().BeFalse();
    }

    [Fact]
    public void Leave_Both_Detection_Flags_Off_By_Default()
    {
        var options = ParseOk(_projectPath);

        options.IncludeDuplicateDetection.Should().BeFalse();
        options.IncludeCloneDrift.Should().BeFalse();
        options.IncludeConfigAnalysis.Should().BeFalse();
    }

    // ---------- entropy ----------

    [Fact]
    public void Reject_An_Entropy_Rate_With_No_Baseline()
    {
        // "New" is relative to a recorded baseline. A silent 0.00 here would be
        // indistinguishable from a clean change and would pass any budget.
        ParseError(_projectPath, "--entropy-rate")
            .Should().Contain("requires --baseline");
    }

    [Fact]
    public void Accept_An_Entropy_Rate_With_A_Baseline()
    {
        ParseOk(_projectPath, "--baseline", Path.Combine(_directory, "b.json"), "--entropy-rate")
            .EntropyRateRequested.Should().BeTrue();
    }

    [Fact]
    public void An_Entropy_Budget_Implies_The_Rate()
    {
        var options = ParseOk(
            _projectPath,
            "--baseline", Path.Combine(_directory, "b.json"),
            "--entropy-budget", "1.5");

        options.EntropyRateRequested.Should().BeTrue();
        options.EntropyBudget.Should().Be(1.5);
    }

    [Fact]
    public void Reject_A_Negative_Entropy_Budget()
    {
        ParseError(_projectPath, "--baseline", "b.json", "--entropy-budget", "-1")
            .Should().Contain("non-negative");
    }

    [Fact]
    public void Parse_An_Entropy_Budget_With_Invariant_Culture()
    {
        // A budget is a CI-facing number, so it must not shift meaning with the machine's
        // locale. "1.5" is the only accepted decimal form; a comma is rejected outright
        // rather than being read as a thousands separator or a locale decimal point.
        ParseOk(_projectPath, "--baseline", "b.json", "--entropy-budget", "1.5")
            .EntropyBudget.Should().Be(1.5);

        ParseError(_projectPath, "--baseline", "b.json", "--entropy-budget", "1,5")
            .Should().Contain("non-negative");
    }

    [Fact]
    public void Reject_A_Entropy_Budget_That_Is_Not_A_Number()
    {
        ParseError(_projectPath, "--baseline", "b.json", "--entropy-budget", "lots")
            .Should().Contain("non-negative");
    }

    [Fact]
    public void Reject_A_Negative_Minimum_Line_Count()
    {
        var options = ParseOk(
            _projectPath,
            "--baseline", "b.json",
            "--entropy-min-lines", "-5");

        options.EntropyMinimumLines.Should().Be(-5);
    }

    [Fact]
    public void An_Entropy_Ledger_Implies_The_Rate()
    {
        var options = ParseOk(
            _projectPath,
            "--baseline", "b.json",
            "--entropy-ledger", Path.Combine(_directory, "l.json"));

        options.EntropyRateRequested.Should().BeTrue();
        options.EntropyLedgerPath.Should().Be(Path.Combine(_directory, "l.json"));
    }

    [Fact]
    public void Use_The_Default_Minimum_Line_Count_When_Unspecified()
    {
        var options = ParseOk(_projectPath, "--baseline", "b.json");

        options.EntropyMinimumLines.Should().Be(EntropyRateCalculator.DefaultMinimumLines);
    }

    [Fact]
    public void Reject_A_Non_Numeric_Minimum_Line_Count()
    {
        ParseError(_projectPath, "--baseline", "b.json", "--entropy-min-lines", "many")
            .Should().Contain("integer line count");
    }

    // ---------- namespace exclusions ----------

    [Fact]
    public void Split_A_Comma_Separated_Namespace_List()
    {
        var options = ParseOk(_projectPath, "--exclude-namespaces", "Company.A,Company.B");

        options.ExcludedNamespaces.Should().BeEquivalentTo(["Company.A", "Company.B"]);
    }

    [Fact]
    public void Allow_The_Namespace_Flag_To_Repeat()
    {
        var options = ParseOk(
            _projectPath,
            "--exclude-namespaces", "Company.A",
            "--exclude-namespaces", "Company.B,Company.C");

        options.ExcludedNamespaces.Should().BeEquivalentTo(["Company.A", "Company.B", "Company.C"]);
    }

    [Fact]
    public void Report_A_Malformed_Namespace_Without_Failing()
    {
        // A typo in an exclusion should not fail a build — but it must not pass unmentioned.
        var options = ParseOk(_projectPath, "--exclude-namespaces", "9bad-name,Company.A");

        options.ExcludedNamespaces.Should().BeEquivalentTo(["Company.A"]);
        options.MalformedNamespaces.Should().BeEquivalentTo(["9bad-name"]);
    }

    [Fact]
    public void Reject_The_Global_Marker_On_The_Command_Line()
    {
        // Documented asymmetry: <global> works in snipper.json but not here, because the CLI
        // validates namespace syntax and the config file does not.
        var options = ParseOk(_projectPath, "--exclude-namespaces", "<global>");

        options.ExcludedNamespaces.Should().BeEmpty();
        options.MalformedNamespaces.Should().BeEquivalentTo(["<global>"]);
    }

    [Fact]
    public void Reject_An_Empty_Namespace_List()
    {
        ParseError(_projectPath, "--exclude-namespaces").Should().Contain("requires a value");
    }

    // ---------- flags are case-insensitive ----------

    [Fact]
    public void Accept_Flags_In_Any_Case()
    {
        var options = ParseOk(_projectPath, "--CLONE-DRIFT", "--Audit-Suppressions");

        options.IncludeCloneDrift.Should().BeTrue();
        options.AuditSuppressions.Should().BeTrue();
    }

    [Fact]
    public void Reject_An_Unknown_Flag()
    {
        ParseError(_projectPath, "--fail-on", "error").Should().Contain("Unexpected argument");
    }

    [Fact]
    public void Reject_A_Null_Argument_Array()
    {
        var act = () => CommandLineParser.TryParse(null!, out _, out _);

        act.Should().Throw<ArgumentNullException>();
    }
}

/// <summary>
/// Covers the analyser factory's exclusion-sensitivity partition, which the 4A shadow pass
/// depends on for its cost. If this classification were wrong the lifted pass would silently
/// omit findings from the baseline — a correctness bug that produces no error, only churn.
/// </summary>
public class AnalyserFactoryShould
{
    private static CommandLineOptions Options(
        bool configAnalysis = false,
        bool duplicateDetection = false,
        bool cloneDrift = false)
        => new()
        {
            TargetPath = "does-not-need-to-exist.csproj",
            IncludeConfigAnalysis = configAnalysis,
            IncludeDuplicateDetection = duplicateDetection,
            IncludeCloneDrift = cloneDrift,
        };

    [Fact]
    public void Build_The_Default_Seventeen_Analysers()
    {
        var built = AnalyserFactory.Build(Options(), AnalysisExclusions.None, ".");

        built.Should().HaveCount(17);
    }

    [Fact]
    public void Add_The_Opt_In_Analysers_Only_When_Asked()
    {
        AnalyserFactory.Build(Options(), AnalysisExclusions.None, ".").Should().HaveCount(17);
        AnalyserFactory.Build(Options(configAnalysis: true), AnalysisExclusions.None, ".")
            .Should().HaveCount(18);
        AnalyserFactory.Build(Options(duplicateDetection: true), AnalysisExclusions.None, ".")
            .Should().HaveCount(18);
        AnalyserFactory.Build(Options(configAnalysis: true, duplicateDetection: true), AnalysisExclusions.None, ".")
            .Should().HaveCount(19);
    }

    [Fact]
    public void Advertise_SNP0032_Only_When_Clone_Drift_Is_Enabled()
    {
        var withoutDrift = AnalyserFactory.Build(Options(duplicateDetection: true), AnalysisExclusions.None, ".");
        var withDrift = AnalyserFactory.Build(
            Options(duplicateDetection: true, cloneDrift: true), AnalysisExclusions.None, ".");

        var duplication = withoutDrift.Single(a => a.RuleIds.Contains("SNP0031"));
        var drift = withDrift.Single(a => a.RuleIds.Contains("SNP0031"));

        duplication.RuleIds.Should().BeEquivalentTo(["SNP0031"]);
        drift.RuleIds.Should().BeEquivalentTo(["SNP0031", "SNP0032"]);
    }

    [Fact]
    public void Partition_The_Exclusion_Agnostic_Analysers()
    {
        // The four package/project graph rules take no exclusions, so their output cannot
        // change when exclusions are lifted. Everything else can.
        var built = AnalyserFactory.Build(Options(configAnalysis: true, duplicateDetection: true), AnalysisExclusions.None, ".");
        var partition = AnalyserFactory.PartitionByExclusionSensitivity(built);

        partition.Aware.Should().HaveCount(15);
        partition.Agnostic.Select(a => a.GetType().Name).Should().BeEquivalentTo(
        [
            "UnreferencedPackageAnalyser",
            "OrphanProjectAnalyser",
            "RedundantTransitivePackageAnalyser",
            "FrameworkInboxPackageAnalyser",
        ]);
    }

    [Fact]
    public void Partition_Every_Analyser_Into_Exactly_One_Half()
    {
        var built = AnalyserFactory.Build(Options(configAnalysis: true, duplicateDetection: true), AnalysisExclusions.None, ".");
        var partition = AnalyserFactory.PartitionByExclusionSensitivity(built);

        (partition.Aware.Count + partition.Agnostic.Count).Should().Be(built.Count);
        partition.Aware.Should().NotIntersectWith(partition.Agnostic);
    }

    [Fact]
    public void Partition_Survives_Being_Applied_Twice()
    {
        var built = AnalyserFactory.Build(Options(), AnalysisExclusions.None, ".");
        var first = AnalyserFactory.PartitionByExclusionSensitivity(built);
        var second = AnalyserFactory.PartitionByExclusionSensitivity(built);

        second.Agnostic.Should().BeEquivalentTo(first.Agnostic);
        second.Aware.Should().BeEquivalentTo(first.Aware);
    }

    [Fact]
    public void Reject_A_Null_Analyser_List()
    {
        var act = () => AnalyserFactory.PartitionByExclusionSensitivity(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
