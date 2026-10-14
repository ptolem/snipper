namespace Snipper.Tests;

using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

/// <summary>
/// The default test-code exclusion shared by SNP0033 and SNP0034.
/// <para>
/// Complexity thresholds are a review trigger, and a review trigger that fires on code
/// nobody reviews is worse than no trigger: it trains the reader to ignore the rule. Test
/// projects are where the worst case concentrates, because a test fixture is *supposed* to
/// be long and explicit - the MILKRUN sweep found a 39-cognitive <c>Doubles.cs</c> factory
/// that is nothing but 39 sibling null guards with no nesting at all.
/// </para>
/// <para>
/// Both complexity rules are covered by every test here. The exclusion is shared policy, so
/// a change that lands in one analyser and not the other would be a silent divergence in
/// exactly the place this repository keeps asking not to have one.
/// </para>
/// </summary>
public sealed class ComplexityTestExclusionShould
{
    /// <summary>
    /// A class holding one method that breaches both rules at their default threshold of 15.
    /// <para>
    /// Generated rather than written out, so the counts are arithmetic instead of
    /// hand-counted: each arm is an <c>if</c> plus one <c>&amp;&amp;</c>, which is +2 to
    /// both metrics, so 16 arms score 33 on each. A fixture that only breached one rule
    /// would let half of every test in this file pass vacuously.
    /// </para>
    /// </summary>
    private static string ComplexSource(int arms = 16)
    {
        var builder = new StringBuilder();
        builder.AppendLine("public class Widget");
        builder.AppendLine("{");
        builder.AppendLine("    public int Score(int a)");
        builder.AppendLine("    {");
        builder.AppendLine("        var total = 0;");

        for (var i = 0; i < arms; i++)
        {
            builder.AppendLine($"        if (a > {i} && a > {i + 100}) {{ total++; }}");
        }

        builder.AppendLine("        return total;");
        builder.AppendLine("    }");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static async Task<List<SnipperFinding>> AnalyzeAsync(
        IWorkspaceAnalyser analyser,
        string filePath,
        string projectName,
        int maxComplexity = 15)
    {
        using var workspace = new AdhocWorkspace();
        var corlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var projectId = ProjectId.CreateNewId(projectName);

        // The project file must sit beside the document. ExclusionEngine drops documents
        // outside the analysis roots, and a project rooted at C:\repo\Widget treats
        // C:\repo\src\Widget\Code.cs as external - which would make every assertion here
        // pass for the wrong reason.
        var directory = Path.GetDirectoryName(filePath)!;
        var projectPath = Path.Combine(directory, $"{projectName}.csproj");

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Create(), projectName, projectName, LanguageNames.CSharp,
                filePath: projectPath))
            .AddMetadataReference(projectId, corlib)
            .AddDocument(
                DocumentId.CreateNewId(projectId), "Code.cs", SourceText.From(ComplexSource()),
                filePath: filePath);

        var findings = await analyser.AnalyzeAsync(solution, CancellationToken.None);
        return [.. findings];
    }

    private static Task<List<SnipperFinding>> CyclomaticAsync(string filePath, string projectName) =>
        AnalyzeAsync(new CyclomaticComplexityAnalyser(), filePath, projectName);

    private static Task<List<SnipperFinding>> CognitiveAsync(string filePath, string projectName) =>
        AnalyzeAsync(new CognitiveComplexityAnalyser(), filePath, projectName);

    [Theory]
    [InlineData(@"C:\repo\tests\Widget\Code.cs")]
    [InlineData(@"C:\repo\test\Widget\Code.cs")]
    [InlineData(@"C:\repo\Widget\tests\Code.cs")]
    [InlineData(@"C:\repo\Widget\Tests\Code.cs")]
    [InlineData(@"C:\repo\TESTS\Widget\Code.cs")]
    public async Task Suppress_both_complexity_rules_under_a_test_path(string filePath)
    {
        (await CyclomaticAsync(filePath, "Widget")).Should().BeEmpty();
        (await CognitiveAsync(filePath, "Widget")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Widget.Tests")]
    [InlineData("Widget.UnitTests")]
    [InlineData("Widget.IntegrationTests")]
    [InlineData("Widget.E2ETests")]
    [InlineData("Widget.Test")]
    public async Task Suppress_both_complexity_rules_in_a_test_project(string projectName)
    {
        // Deliberately NOT under a test/ directory: the project name is the only signal.
        (await CyclomaticAsync($@"C:\repo\src\{projectName}\Code.cs", projectName)).Should().BeEmpty();
        (await CognitiveAsync($@"C:\repo\src\{projectName}\Code.cs", projectName)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"C:\repo\src\Widget\Code.cs", "Widget")]
    [InlineData(@"C:\repo\src\Contest\Code.cs", "Contest")]
    [InlineData(@"C:\repo\src\Testing\Widget\Code.cs", "Widget")]
    [InlineData(@"C:\repo\src\Widget\LatestService.cs", "Widget")]
    public async Task Keep_reporting_outside_test_code(string filePath, string projectName)
    {
        // "Contest" and "Testing" are the guard rails: a substring match on "test" would
        // silently drop production code, which is a far worse failure than a noisy finding.
        (await CyclomaticAsync(filePath, projectName)).Should().HaveCount(1);
        (await CognitiveAsync(filePath, projectName)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Report_the_same_way_when_only_the_extension_is_production()
    {
        // A file called MyTests.cs in a production project is production code. The
        // exclusion keys on directories and project names, never on file names.
        (await CyclomaticAsync(@"C:\repo\src\Widget\MyTests.cs", "Widget")).Should().HaveCount(1);
        (await CognitiveAsync(@"C:\repo\src\Widget\MyTests.cs", "Widget")).Should().HaveCount(1);
    }

    [Fact]
    public async Task Still_score_the_code_so_thresholds_become_per_rule_decisions()
    {
        // The exclusion is applied where findings are emitted, not by scoring differently.
        // Dropping the threshold to 1 must not resurrect test findings: otherwise the rule
        // would be enabled or disabled by a threshold, which is not a property any rule here
        // has.
        (await AnalyzeAsync(
            new CyclomaticComplexityAnalyser(maxComplexity: 1),
            @"C:\repo\tests\Widget\Code.cs", "Widget")).Should().BeEmpty();

        (await AnalyzeAsync(
            new CognitiveComplexityAnalyser(maxComplexity: 1),
            @"C:\repo\tests\Widget\Code.cs", "Widget")).Should().BeEmpty();

        // ...and the same lowered threshold on production code does report, so the two
        // assertions above are about the exclusion rather than about a threshold of 1
        // being ignored.
        (await AnalyzeAsync(
            new CyclomaticComplexityAnalyser(maxComplexity: 1),
            @"C:\repo\src\Widget\Code.cs", "Widget")).Should().HaveCount(1);
    }

    [Fact]
    public async Task Leave_the_other_rules_untouched()
    {
        // The exclusion belongs to the complexity rules only. A private member that is
        // genuinely dead is just as dead in a test project.
        using var workspace = new AdhocWorkspace();
        var corlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var projectId = ProjectId.CreateNewId("Widget.Tests");
        const string source = """
            public class Widget
            {
                private int Unused() => 42;
            }
            """;

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Create(), "Widget.Tests", "Widget.Tests", LanguageNames.CSharp,
                filePath: @"C:\repo\Widget.Tests\Widget.Tests.csproj"))
            .AddMetadataReference(projectId, corlib)
            .AddDocument(
                DocumentId.CreateNewId(projectId), "Code.cs", SourceText.From(source),
                filePath: @"C:\repo\Widget.Tests\Code.cs");

        var findings = await new UnusedPrivateMemberAnalyser()
            .AnalyzeAsync(solution, CancellationToken.None);

        findings.Should().ContainSingle(f => Regex.IsMatch(f.Message, "Unused"));
    }
}