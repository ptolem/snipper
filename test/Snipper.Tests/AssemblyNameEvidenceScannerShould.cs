namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Xunit;

public sealed class AssemblyNameEvidenceScannerShould : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), "snipper-name-evidence", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Find_Names_Spelled_In_Configuration_Json_For_FindSpelledNamesInJsonFiles()
    {
        WriteJson("appsettings.json", """{ "plugins": [ "EvidencePlugin" ] }""");

        var found = AssemblyNameEvidenceScanner.FindSpelledNamesInJsonFiles(_rootDirectory, ["EvidencePlugin", "AbsentPlugin"]);

        found.Should().Contain("EvidencePlugin");
        found.Should().NotContain("AbsentPlugin");
    }

    [Fact]
    public void Ignore_Snipper_Report_Json_For_FindSpelledNamesInJsonFiles()
    {
        // A stale report spells real candidate names inside its findings; it is
        // self-reference, not external consumption (twice-confirmed on the
        // monorepo: 1.4.4 and 1.5.1 each lost real SNP0023 findings this way).
        WriteJson(
            "output_1_4_0_milkrun.json",
            """[{"ruleId":"SNP0023","certainty":"Advisory","message":"Class 'EvidencePlugin' declares virtual members","filePath":"src/EvidencePlugin.cs","lineNumber":4}]""");

        var found = AssemblyNameEvidenceScanner.FindSpelledNamesInJsonFiles(_rootDirectory, ["EvidencePlugin"]);

        found.Should().NotContain("EvidencePlugin");
    }

    [Fact]
    public void Still_Scan_Other_Json_When_A_Report_Is_Present_For_FindSpelledNamesInJsonFiles()
    {
        WriteJson(
            "output_wave4_milkrun.json",
            """[{"ruleId":"SNP0023","message":"Class 'EvidencePlugin' declares virtual members"}]""");
        WriteJson("manifest.json", """{ "modules": [ "EvidencePlugin" ] }""");

        var found = AssemblyNameEvidenceScanner.FindSpelledNamesInJsonFiles(_rootDirectory, ["EvidencePlugin"]);

        found.Should().Contain("EvidencePlugin");
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    private void WriteJson(string fileName, string content)
    {
        Directory.CreateDirectory(_rootDirectory);
        File.WriteAllText(Path.Combine(_rootDirectory, fileName), content);
    }
}
