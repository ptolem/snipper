namespace Snipper.Tests;

using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Snipper.Analysis;
using Snipper.Cli;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class NamespaceExclusionShould(SampleSolutionFixture fixture)
{
    private static readonly AnalysisExclusions Exclusions = AnalysisExclusions.Create(["Excluded.Fake"]);

    [Fact]
    public async Task Suppress_Unused_Private_Members_In_Excluded_Namespaces_For_AnalyzeAsync()
    {
        var findings = await AnalyseAsync(new UnusedPrivateMemberAnalyser(), fixture.Solution, Exclusions);

        findings.Should().NotContain(f => f.RuleId == "SNP0001" && f.Message.Contains("_excludedUnusedField", StringComparison.Ordinal));
        findings.Should().NotContain(f => f.RuleId == "SNP0001" && f.Message.Contains("ExcludedUnusedMethod", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_Unused_Types_And_Members_In_Excluded_Namespaces_For_AnalyzeAsync()
    {
        var findings = await AnalyseAsync(new UnusedNonPrivateMemberAnalyser(), fixture.Solution, Exclusions);

        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("ExcludedDeadCode", StringComparison.Ordinal));
        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("ExcludedInternalType", StringComparison.Ordinal));
        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("ExcludedConsumer", StringComparison.Ordinal));
        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("NeverConfigured", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_Unused_Sub_Namespace_Members_When_The_Parent_Is_Excluded_For_AnalyzeAsync()
    {
        var findings = await AnalyseAsync(new UnusedNonPrivateMemberAnalyser(), fixture.Solution, AnalysisExclusions.Create(["Excluded"]));

        // Excluding "Excluded" must also cover "Excluded.Fake".
        findings.Should().NotContain(f => (f.RuleId == "SNP0005" || f.RuleId == "SNP0006") && f.Message.Contains("ExcludedDeadCode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retain_Usage_Evidence_From_Excluded_Namespaces_For_AnalyzeAsync()
    {
        var findings = await AnalyseAsync(new UnusedNonPrivateMemberAnalyser(), fixture.Solution, Exclusions);

        // KeptAliveByExcludedCode is only referenced from Excluded.Fake code — the
        // reference still counts, so the type must NOT be flagged.
        findings.Should().NotContain(f => f.RuleId == "SNP0005" && f.Message.Contains("KeptAliveByExcludedCode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_Unread_Locals_In_Excluded_Namespaces_For_AnalyzeAsync()
    {
        var findings = await AnalyseAsync(new UnusedLocalVariableAnalyser(), fixture.Solution, Exclusions);

        findings.Should().NotContain(f => f.RuleId == "SNP0009" && f.Message.Contains("excludedUnusedLocal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_Unused_Parameters_In_Excluded_Namespaces_For_AnalyzeAsync()
    {
        var findings = await AnalyseAsync(new UnusedParameterAnalyser(), fixture.Solution, Exclusions);

        findings.Should().NotContain(f => f.RuleId == "SNP0010" && f.Message.Contains("excludedUnusedParam", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_Unconfigured_Options_Properties_In_Excluded_Namespaces_For_AnalyzeAsync()
    {
        var findings = await AnalyseAsync(new ConfigurationBindingAnalyser(), fixture.Solution, Exclusions);

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
        var withExclusions = await AnalyseAsync(new UnreachableCodeAnalyser(), solution, Exclusions);

        withoutExclusions.Should().ContainSingle(f => f.RuleId == "SNP0002");
        withExclusions.Should().BeEmpty();
    }

    private static readonly AnalysisExclusions GlobalMarker =
        AnalysisExclusions.Create([AnalysisExclusions.GlobalNamespaceMarker]);

    [Fact]
    public async Task Suppress_Unused_Private_Members_Of_Types_In_The_Global_Namespace_For_AnalyzeAsync()
    {
        // A type declared with no namespace at all binds to the global namespace, so no real
        // namespace name can match it. Before the <global> sentinel the exclusion was
        // unreachable from here, leaving the member flagged however many namespaces were
        // excluded.
        var solution = GlobalNamespaceSolution(
            """
            public sealed class GlobalProbe
            {
                private int globalUnusedField;
            }
            """);

        var findings = await AnalyseAsync(new UnusedPrivateMemberAnalyser(), solution, GlobalMarker);

        findings.Should().NotContain(f => f.RuleId == "SNP0001" && f.Message.Contains("globalUnusedField", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_Unread_Locals_In_Top_Level_Statements_For_AnalyzeAsync()
    {
        // Top-level statements have no enclosing type or namespace declaration, so only the
        // <global> sentinel can reach them; before it the finding escaped suppression.
        var solution = TopLevelStatementsSolution(
            """
            var unreadTopLevel = 1;
            Console.WriteLine("reachable");
            """);

        var findings = await AnalyseAsync(new UnusedLocalVariableAnalyser(), solution, GlobalMarker);

        findings.Should().NotContain(f => f.RuleId == "SNP0009" && f.Message.Contains("unreadTopLevel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_Unnecessary_Usings_In_A_Namespace_Less_Global_Usings_File_For_AnalyzeAsync()
    {
        // The MILKRUN shape: a file of global usings with no namespace declaration.
        var solution = GlobalUsingScopeSolution();

        var findings = await AnalyseAsync(new UnusedUsingDirectiveAnalyser(), solution, GlobalMarker);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task Suppress_Unnecessary_Usings_Above_A_File_Scoped_Namespace_By_That_Namespace_For_AnalyzeAsync()
    {
        // A file-scoped `namespace Excluded.Fake;` is a *sibling* of the using directives,
        // not an ancestor, so resolving the namespace by walking ancestors finds nothing.
        // Excluding the namespace the file declares must still suppress its own usings.
        var solution = FileScopedNamespaceSolution();

        var withoutExclusions = await new UnusedUsingDirectiveAnalyser().AnalyzeAsync(solution, CancellationToken.None);
        var withExclusions = await AnalyseAsync(new UnusedUsingDirectiveAnalyser(), solution, Exclusions);

        withoutExclusions.Should().ContainSingle(f => f.RuleId == "SNP0019");
        withExclusions.Should().BeEmpty();
    }

    [Fact]
    public async Task Not_Suppress_Usings_In_A_Namespaced_File_When_Only_The_Global_Marker_Is_Excluded_For_AnalyzeAsync()
    {
        // Guards the sibling lookup above: if file-level usings in a namespaced file were
        // classified as the global namespace, "<global>" would suppress using directives in
        // every namespaced file in the solution.
        var solution = FileScopedNamespaceSolution();

        var findings = await AnalyseAsync(new UnusedUsingDirectiveAnalyser(), solution, GlobalMarker);

        findings.Should().ContainSingle(f => f.RuleId == "SNP0019"
            && f.Message.Contains("System.Diagnostics", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resolve_Nested_Block_Namespaces_To_Their_Full_Name_For_ApplyAsync()
    {
        // `namespace B` inside `namespace A` declares A.B. Taking only the innermost name
        // would make an exclusion of A.B miss it, and an exclusion of B hit it.
        var solution = Adhoc("Nested", "Code.cs",
            """
            namespace Outer
            {
                namespace Inner
                {
                    public sealed class Holder
                    {
                        private int nestedUnusedField;
                    }
                }
            }
            """,
            OutputKind.DynamicallyLinkedLibrary);

        var excludedByFullName = await AnalyseAsync(new UnusedPrivateMemberAnalyser(), solution, AnalysisExclusions.Create(["Outer.Inner"]));
        var excludedByInnerName = await AnalyseAsync(new UnusedPrivateMemberAnalyser(), solution, AnalysisExclusions.Create(["Inner"]));

        excludedByFullName.Should().NotContain(f => f.Message.Contains("nestedUnusedField", StringComparison.Ordinal));
        excludedByInnerName.Should().Contain(f => f.Message.Contains("nestedUnusedField", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Never_Exclude_A_Finding_Outside_The_Solution_Source_For_ApplyAsync()
    {
        // Package and project findings land in project and lock files, which have no
        // namespace. They must not read as global-namespace code, or <global> would hide
        // every package finding in the solution.
        var solution = GlobalNamespaceSolution("public sealed class Anything { }");
        var packageFinding = new SnipperFinding(
            "SNP0011", "Unreferenced Package", "Package 'X' is never used.", CertaintyTier.High,
            FindingCategory.UnreferencedPackage, @"C:\repo\GlobalProbe\GlobalProbe.csproj", 12, 5);

        var result = await NamespaceExclusionFilter.ApplyAsync([packageFinding], solution, GlobalMarker, CancellationToken.None);

        result.Visible.Should().ContainSingle();
        result.Excluded.Should().BeEmpty();
    }

    [Fact]
    public async Task Credit_A_Hidden_Finding_To_The_Nearest_Covering_Exclusion_For_ApplyAsync()
    {
        var findings = await new UnusedNonPrivateMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        var result = await NamespaceExclusionFilter.ApplyAsync(
            findings, fixture.Solution, AnalysisExclusions.Create(["Excluded", "Excluded.Fake"]), CancellationToken.None);

        result.Excluded.Should().NotBeEmpty();
        result.Excluded.Should().OnlyContain(e => e.Exclusion == "Excluded.Fake");
        result.Visible.Count.Should().Be(findings.Count - result.Excluded.Count);
    }

    [Fact]
    public async Task Return_The_Findings_Untouched_When_Nothing_Is_Excluded_For_ApplyAsync()
    {
        var findings = await new UnusedNonPrivateMemberAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        var result = await NamespaceExclusionFilter.ApplyAsync(findings, fixture.Solution, AnalysisExclusions.None, CancellationToken.None);

        result.Visible.Should().BeSameAs(findings);
        result.Excluded.Should().BeEmpty();
    }

    /// <summary>
    /// Runs an analyser, then namespace exclusion over what it found — the order the CLI
    /// applies them in.
    /// </summary>
    private static async Task<IReadOnlyList<SnipperFinding>> AnalyseAsync(
        IWorkspaceAnalyser analyser,
        Solution solution,
        AnalysisExclusions exclusions)
    {
        var findings = await analyser.AnalyzeAsync(solution, CancellationToken.None);
        var result = await NamespaceExclusionFilter.ApplyAsync(findings, solution, exclusions, CancellationToken.None);

        return result.Visible;
    }

    private static Solution GlobalNamespaceSolution(string source)
    {
        return Adhoc("GlobalProbe", "Code.cs", source, OutputKind.DynamicallyLinkedLibrary);
    }

    private static Solution TopLevelStatementsSolution(string source)
    {
        return Adhoc("TopLevelProbe", "Program.cs", source, OutputKind.ConsoleApplication);
    }

    private static Solution FileScopedNamespaceSolution()
    {
        return Adhoc("ScopedProbe", "Code.cs",
            """
            using System.Diagnostics;

            namespace Excluded.Fake;

            public static class ScopedProbe
            {
            }
            """,
            OutputKind.DynamicallyLinkedLibrary);
    }

    /// <summary>
    /// Two files: a namespace-less <c>GlobalUsings.cs</c> declaring System.Text (used only
    /// from <c>Consumer.cs</c>) and System.Diagnostics (used nowhere), so the second is a
    /// genuine CS8019 to assert suppression against.
    /// </summary>
    private static Solution GlobalUsingScopeSolution()
    {
        return Adhoc("Scope", "GlobalUsings.cs",
            """
            global using System.Text;
            global using System.Diagnostics;
            """,
            OutputKind.DynamicallyLinkedLibrary,
            ("Consumer.cs",
            """
            namespace Scope.Support;

            public static class Consumer
            {
                public static int Len() => new StringBuilder().Length;
            }
            """));
    }

    private static Solution Adhoc(
        string projectName,
        string fileName,
        string source,
        OutputKind outputKind,
        params (string Name, string Source)[] extraFiles)
    {
        using var workspace = new AdhocWorkspace();
        var directory = $@"C:\repo\{projectName}";
        var projectId = ProjectId.CreateNewId(projectName);

        var builder = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(), projectName, projectName, LanguageNames.CSharp,
                filePath: $@"{directory}\{projectName}.csproj",
                compilationOptions: new CSharpCompilationOptions(outputKind)))
            .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

        builder = builder.AddDocument(
            DocumentId.CreateNewId(projectId), fileName, SourceText.From(source), filePath: $@"{directory}\{fileName}");

        foreach (var (name, extra) in extraFiles)
        {
            builder = builder.AddDocument(
                DocumentId.CreateNewId(projectId), name, SourceText.From(extra), filePath: $@"{directory}\{name}");
        }

        return builder;
    }
}
