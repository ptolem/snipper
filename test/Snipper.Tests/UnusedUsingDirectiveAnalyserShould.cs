namespace Snipper.Tests;

using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

[Collection("SampleSolution")]
public sealed class UnusedUsingDirectiveAnalyserShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Flag_Unused_Using_Directives_As_Guaranteed_For_AnalyzeAsync()
    {
        var findings = await new UnusedUsingDirectiveAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0019"
            && f.Certainty == CertaintyTier.Guaranteed
            && f.FilePath.EndsWith("UnusedUsings.cs", StringComparison.Ordinal)
            && f.LineNumber == 2);
        findings.Should().Contain(f =>
            f.RuleId == "SNP0019"
            && f.Certainty == CertaintyTier.Guaranteed
            && f.FilePath.EndsWith("UnusedUsings.cs", StringComparison.Ordinal)
            && f.LineNumber == 3);
    }

    [Fact]
    public async Task Flag_Using_That_Duplicates_A_Global_Using_For_AnalyzeAsync()
    {
        var findings = await new UnusedUsingDirectiveAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0019"
            && f.FilePath.EndsWith("UnusedUsings.cs", StringComparison.Ordinal)
            && f.LineNumber == 4);
    }

    [Fact]
    public async Task Report_One_Finding_When_A_Using_Duplicates_A_Global_Using_For_AnalyzeAsync()
    {
        // Roslyn describes ONE directive with TWO diagnostics here: CS8019 ("unnecessary")
        // and CS8933 ("duplicates a global using"), both on the same UsingDirectiveSyntax.
        // Surfacing both doubled every such directive (62 of 250 on MILKRUN) and, since
        // the two messages differ, BaselineService.ComputeFingerprint gave them different
        // hashes - so a consumer acting on one saw the other resurface as new.
        var findings = await new UnusedUsingDirectiveAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        var flagged = findings
            .Where(f => f.RuleId == "SNP0019"
                && f.FilePath.EndsWith("UnusedUsings.cs", StringComparison.Ordinal)
                && f.LineNumber == 4)
            .ToList();

        flagged.Should().ContainSingle("one directive is one finding, however many diagnostics describe it");
        flagged[0].Message.Should().Contain("duplicates a global using directive",
            "CS8933 is the specific verdict, so it is the one that must survive");
    }

    [Fact]
    public async Task Flag_Unused_Global_Using_For_AnalyzeAsync()
    {
        var findings = await new UnusedUsingDirectiveAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0019"
            && f.Certainty == CertaintyTier.Guaranteed
            && f.FilePath.EndsWith("GlobalUsings.cs", StringComparison.Ordinal)
            && f.LineNumber == 3);
    }

    [Fact]
    public async Task Flag_Duplicate_Using_With_Distinct_Message_For_AnalyzeAsync()
    {
        // The compiler flags only the second occurrence — the message must make
        // clear it is a duplicate, so consumers keep exactly one copy (FP-4).
        var findings = await new UnusedUsingDirectiveAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().Contain(f =>
            f.RuleId == "SNP0019"
            && f.FilePath.EndsWith("GlobalUsings.cs", StringComparison.Ordinal)
            && f.LineNumber == 7
            && f.Message.Contains("duplicates another using directive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Used_Using_Directives_For_AnalyzeAsync()
    {
        var findings = await new UnusedUsingDirectiveAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f =>
            f.RuleId == "SNP0019"
            && f.FilePath.EndsWith("UnusedUsings.cs", StringComparison.Ordinal)
            && f.LineNumber == 1);
    }

    [Fact]
    public async Task Not_Flag_Files_Without_Unused_Usings_For_AnalyzeAsync()
    {
        var findings = await new UnusedUsingDirectiveAnalyser().AnalyzeAsync(fixture.Solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0019" && f.FilePath.EndsWith("DeadCode.cs", StringComparison.Ordinal));
        findings.Should().NotContain(f => f.RuleId == "SNP0019" && f.FilePath.EndsWith("Worker.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Not_Flag_Global_Using_Consumed_Only_By_Another_File_For_AnalyzeAsync()
    {
        // The SampleApp App/GlobalUsings.cs fixture said CS8019 "fires on the global
        // using exactly as it does for ordinary ones", but it only ever proved the
        // unused case - nothing in App consumed System.Text anywhere. That left the
        // cross-file case untested, which is the one that matters: a global using is
        // project-wide, so the directive's own file is not where its usage can appear.
        //
        // CS8019 is confirmed compilation-wide (the analyser calls
        // compilation.GetDiagnostics()), so System.Text - used only from Consumer.cs -
        // must not be reported. Pinned here so a future change to how SNP0019 harvests
        // diagnostics cannot silently start flagging needed global usings.
        var solution = GlobalUsingScopeSolution();

        var findings = await new UnusedUsingDirectiveAnalyser().AnalyzeAsync(solution, CancellationToken.None);

        findings.Should().NotContain(f => f.RuleId == "SNP0019" && f.Message.Contains("System.Text", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flag_Global_Using_Never_Consumed_Anywhere_In_The_Project_For_AnalyzeAsync()
    {
        // The other half of the same pair: a global using no file in the project
        // consumes IS unnecessary, and must still be reported. Without this the test
        // above would also pass if SNP0019 stopped reporting global usings altogether.
        var solution = GlobalUsingScopeSolution();

        var findings = await new UnusedUsingDirectiveAnalyser().AnalyzeAsync(solution, CancellationToken.None);

        findings.Should().ContainSingle(f => f.RuleId == "SNP0019"
            && f.Certainty == CertaintyTier.Guaranteed
            && f.Message.Contains("System.Diagnostics", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two-file project: GlobalUsings.cs declares System.Text (consumed only from
    /// Consumer.cs) and System.Diagnostics (consumed nowhere). Both namespaces resolve
    /// from corlib, so no package reference is needed to build the fixture.
    /// </summary>
    private static Solution GlobalUsingScopeSolution()
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("Scope");

        return workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(), "Scope", "Scope", LanguageNames.CSharp,
                filePath: @"C:\repo\Scope\Scope.csproj",
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)))
            .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddDocument(DocumentId.CreateNewId(projectId), "GlobalUsings.cs", SourceText.From(
                """
                global using System.Text;
                global using System.Diagnostics;
                """),
                filePath: @"C:\repo\Scope\GlobalUsings.cs")
            .AddDocument(DocumentId.CreateNewId(projectId), "Consumer.cs", SourceText.From(
                """
                namespace Scope.Support;

                public static class Consumer
                {
                    public static int Len() => new StringBuilder().Length;
                }
                """),
                filePath: @"C:\repo\Scope\Consumer.cs");
    }
}
