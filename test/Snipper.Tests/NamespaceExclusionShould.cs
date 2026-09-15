namespace Snipper.Tests;

using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Snipper.Analysis;
using Xunit;

[Collection("SampleSolution")]
public sealed class NamespaceExclusionShould(SampleSolutionFixture fixture)
{
    private static readonly AnalysisExclusions Exclusions = AnalysisExclusions.Create(["Excluded.Fake"]);

    [Fact]
    public async Task Suppress_Unused_Private_Members_In_Excluded_Namespaces_For_AnalyzeAsync()
    {
        var analyser = new UnusedPrivateMemberAnalyser(Exclusions);

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0001" && f.Message.Contains("_excludedUnusedField", StringComparison.Ordinal));
        findings.Should().NotContain(f => f.RuleId == "SNP0001" && f.Message.Contains("ExcludedUnusedMethod", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_Unused_Types_And_Members_In_Excluded_Namespaces_For_AnalyzeAsync()
    {
        var analyser = new UnusedNonPrivateMemberAnalyser(Exclusions);

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("ExcludedDeadCode", StringComparison.Ordinal));
        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("ExcludedInternalType", StringComparison.Ordinal));
        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("ExcludedConsumer", StringComparison.Ordinal));
        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("NeverConfigured", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_Unused_Sub_Namespace_Members_When_The_Parent_Is_Excluded_For_AnalyzeAsync()
    {
        var analyser = new UnusedNonPrivateMemberAnalyser(AnalysisExclusions.Create(["Excluded"]));

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        // Excluding "Excluded" must also cover "Excluded.Fake".
        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("ExcludedDeadCode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retain_Usage_Evidence_From_Excluded_Namespaces_For_AnalyzeAsync()
    {
        var analyser = new UnusedNonPrivateMemberAnalyser(Exclusions);

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        // KeptAliveByExcludedCode is only referenced from Excluded.Fake code — the
        // reference still counts, so the type must NOT be flagged.
        findings.Should().NotContain(f => f.RuleId == "SNP0005" && f.Message.Contains("KeptAliveByExcludedCode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_Unread_Locals_In_Excluded_Namespaces_For_AnalyzeAsync()
    {
        var analyser = new UnusedLocalVariableAnalyser(Exclusions);

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0009" && f.Message.Contains("excludedUnusedLocal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_Unused_Parameters_In_Excluded_Namespaces_For_AnalyzeAsync()
    {
        var analyser = new UnusedParameterAnalyser(Exclusions);

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0010" && f.Message.Contains("excludedUnusedParam", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_Unconfigured_Options_Properties_In_Excluded_Namespaces_For_AnalyzeAsync()
    {
        var analyser = new ConfigurationBindingAnalyser(Exclusions);

        var findings = await analyser.AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0008" && f.Message.Contains("NeverConfigured", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_Unreachable_Code_In_Excluded_Namespaces_For_AnalyzeAsync()
    {
        // Adhoc workspace: unreachable statement inside an excluded namespace.
        using var workspace = new AdhocWorkspace();
        var corlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var projectId = ProjectId.CreateNewId("Demo");
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(), "Demo", "Demo", LanguageNames.CSharp,
                filePath: @"C:\repo\Demo\Demo.csproj"))
            .AddMetadataReference(projectId, corlib)
            .AddDocument(DocumentId.CreateNewId(projectId), "Code.cs", SourceText.From(
                "namespace Excluded.Fake; public sealed class C { public int M(int v) { return v; var dead = v; } }"),
                filePath: @"C:\repo\Demo\Code.cs");

        var withoutExclusions = await new UnreachableCodeAnalyser().AnalyzeAsync(solution, CancellationToken.None);
        var withExclusions = await new UnreachableCodeAnalyser(Exclusions).AnalyzeAsync(solution, CancellationToken.None);

        withoutExclusions.Should().ContainSingle(f => f.RuleId == "SNP0002");
        withExclusions.Should().BeEmpty();
    }
}
