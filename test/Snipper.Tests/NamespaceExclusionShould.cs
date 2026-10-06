namespace Snipper.Tests;

using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

    private static readonly AnalysisExclusions GlobalMarker =
        AnalysisExclusions.Create([AnalysisExclusions.GlobalNamespaceMarker]);

    [Fact]
    public async Task Suppress_Unused_Private_Members_Of_Types_In_The_Global_Namespace_For_AnalyzeAsync()
    {
        // A type declared with no namespace at all binds to the global namespace, so no real
        // namespace name can match it. This is the ExclusionEngine symbol path: the type's
        // ContainingNamespace is global and the exclusion used to be unreachable from here,
        // leaving the member flagged however many namespaces were excluded.
        var solution = GlobalNamespaceSolution(
            """
            public sealed class GlobalProbe
            {
                private int globalUnusedField;
            }
            """);

        var findings = await new UnusedPrivateMemberAnalyser(GlobalMarker).AnalyzeAsync(solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0001" && f.Message.Contains("globalUnusedField", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_Unread_Locals_In_Top_Level_Statements_For_AnalyzeAsync()
    {
        // Top-level statements have no enclosing type declaration, so the syntax path's
        // FirstAncestorOrSelf<TypeDeclarationSyntax> lookup returned null and the finding
        // escaped suppression.
        var solution = TopLevelStatementsSolution(
            """
            var unreadTopLevel = 1;
            Console.WriteLine("reachable");
            """);

        var findings = await new UnusedLocalVariableAnalyser(GlobalMarker).AnalyzeAsync(solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0009" && f.Message.Contains("unreadTopLevel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suppress_Unnecessary_Usings_In_A_Namespace_Less_Global_Usings_File_For_AnalyzeAsync()
    {
        // The MILKRUN shape: a file of global usings with no namespace declaration.
        var solution = GlobalUsingScopeSolution();

        var findings = await new UnusedUsingDirectiveAnalyser(GlobalMarker).AnalyzeAsync(solution, CancellationToken.None);

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
        var withExclusions = await new UnusedUsingDirectiveAnalyser(Exclusions).AnalyzeAsync(solution, CancellationToken.None);

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

        var findings = await new UnusedUsingDirectiveAnalyser(GlobalMarker).AnalyzeAsync(solution, CancellationToken.None);

        findings.Should().ContainSingle(f => f.RuleId == "SNP0019"
            && f.Message.Contains("System.Diagnostics", StringComparison.Ordinal));
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
