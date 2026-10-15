namespace Snipper.Tests;

using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Snipper.Analysis;
using Snipper.Cli;
using Snipper.Models;
using Xunit;

/// <summary>
/// An analyser's <see cref="IWorkspaceAnalyser.SharedIndexes"/> is what
/// <see cref="AnalysisRunner"/> builds before the fan-out. An index read but not declared
/// still works - it is built on first use - but its cost silently moves back into that
/// analyser's time, and an index declared but never read is built for nothing. Both are
/// invisible in the output, so they are pinned here.
/// </summary>
[Collection("SampleSolution")]
public sealed class SharedIndexDeclarationShould(SampleSolutionFixture fixture)
{
    public static TheoryData<string> AnalyserNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (var analyser in AllAnalysers())
            {
                names.Add(analyser.GetType().Name);
            }

            return names;
        }
    }

    [Theory]
    [MemberData(nameof(AnalyserNames))]
    public async Task Build_Exactly_The_Indexes_An_Analyser_Declares(string analyserName)
    {
        var analyser = AllAnalysers().Single(a => a.GetType().Name == analyserName);
        var solution = await ForkAsync(fixture.Solution);

        await analyser.AnalyzeAsync(solution, CancellationToken.None);

        SharedIndexes.Built(solution).Should().Be(
            analyser.SharedIndexes,
            $"{analyserName} should read exactly the shared indexes it declares");
    }

    [Fact]
    public async Task Build_The_Declared_Indexes_Before_Any_Analyser_Starts()
    {
        var solution = await ForkAsync(fixture.Solution);
        var declared = SharedIndex.SolutionUsageIndex | SharedIndex.InheritanceGraph;
        var probe = new IndexProbe(declared);
        var reports = new RecordingProgress();

        await AnalysisRunner.RunAsync([probe], solution, reports, CancellationToken.None);

        probe.BuiltWhenStarted.Should().Be(declared);
        reports.Completed.Should().Contain(["SolutionUsageIndex", "InheritanceGraph", "Shared indexes"]);
    }

    [Fact]
    public async Task Build_No_Index_When_No_Analyser_Declares_One()
    {
        var solution = await ForkAsync(fixture.Solution);
        var probe = new IndexProbe(SharedIndex.None);
        var reports = new RecordingProgress();

        await AnalysisRunner.RunAsync([probe], solution, reports, CancellationToken.None);

        probe.BuiltWhenStarted.Should().Be(SharedIndex.None);
        SharedIndexes.Built(solution).Should().Be(SharedIndex.None);
        reports.Completed.Should().NotContain("Shared indexes");
    }

    [Fact]
    public async Task Not_Report_Indexes_A_Previous_Run_Over_The_Same_Solution_Built()
    {
        // The disabled-rule pass runs analysers a second time over the same solution.
        var solution = await ForkAsync(fixture.Solution);
        var declared = SharedIndex.AnalysisScope | SharedIndex.SolutionUsageIndex;
        await AnalysisRunner.RunAsync([new IndexProbe(declared)], solution, progress: null, CancellationToken.None);
        var reports = new RecordingProgress();

        await AnalysisRunner.RunAsync([new IndexProbe(declared | SharedIndex.InheritanceGraph)], solution, reports, CancellationToken.None);

        reports.Completed.Should().Contain(["InheritanceGraph", "Shared indexes"]);
        reports.Completed.Should().NotContain(["AnalysisScope", "SolutionUsageIndex"]);
    }

    private static List<IWorkspaceAnalyser> AllAnalysers()
    {
        // Every analyser the factory can build except clone drift, which needs a git
        // repository; it only changes what DuplicateFragmentAnalyser does with clone sets.
        var options = new CommandLineOptions
        {
            TargetPath = "does-not-need-to-exist.csproj",
            IncludeConfigAnalysis = true,
            IncludeDuplicateDetection = true,
        };

        return AnalyserFactory.Build(options, ".", Path.GetTempPath());
    }

    /// <summary>
    /// The indexes are memoized per <see cref="Solution"/> instance, and the fixture's is
    /// shared with every other test in the collection, so each test needs its own. Editing
    /// one document of App, the top of the project graph, makes a new instance at the cost
    /// of recompiling App alone.
    /// </summary>
    private static async Task<Solution> ForkAsync(Solution solution)
    {
        var document = solution.Projects.Single(p => p.Name == "App").Documents.First();
        var text = await document.GetTextAsync();
        return solution.WithDocumentText(document.Id, SourceText.From(text + Environment.NewLine));
    }

    /// <summary>Records which shared indexes already exist when the fan-out reaches it.</summary>
    private sealed class IndexProbe(SharedIndex declared) : IWorkspaceAnalyser
    {
        public IReadOnlyCollection<string> RuleIds { get; } = ["SNP9999"];

        public SharedIndex SharedIndexes => declared;

        public SharedIndex BuiltWhenStarted { get; private set; } = (SharedIndex)(-1);

        public Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
            Solution solution,
            CancellationToken cancellationToken,
            Action<string>? progress = null)
        {
            BuiltWhenStarted = Analysis.SharedIndexes.Built(solution);
            return Task.FromResult<IReadOnlyList<SnipperFinding>>([]);
        }
    }

    private sealed class RecordingProgress : IProgress<AnalysisProgress>
    {
        private readonly Lock _gate = new();
        private readonly List<string> _completed = [];

        public IReadOnlyList<string> Completed
        {
            get
            {
                lock (_gate)
                {
                    return [.. _completed];
                }
            }
        }

        public void Report(AnalysisProgress value)
        {
            if (!value.Completed)
            {
                return;
            }

            lock (_gate)
            {
                _completed.Add(value.AnalyserName);
            }
        }
    }
}
