namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Cli;
using Snipper.Models;
using Xunit;

public sealed class SnipperConfigLoaderShould : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "Snipper.Tests", Guid.NewGuid().ToString("N"));

    public SnipperConfigLoaderShould()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDirectory, recursive: true);
    }

    [Fact]
    public void Return_Empty_Config_When_No_File_Exists_For_Load()
    {
        var warnings = new List<string>();

        var config = SnipperConfigLoader.Load(Target("App.csproj"), out var configPath, warnings);

        config.Should().Be(SnipperConfig.Empty);
        configPath.Should().BeNull();
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Parse_Rules_And_Exclusions_From_Valid_Config_For_Load()
    {
        WriteConfig(Target("snipper.json"), """
            {
              "version": 1,
              "rules": { "SNP0010": "off", "snp0018": "Advisory" },
              "exclude": { "namespaces": ["Company.Generated"], "paths": ["**/Generated/**"] }
            }
            """);
        var warnings = new List<string>();

        var config = SnipperConfigLoader.Load(Target("App.csproj"), out var configPath, warnings);

        configPath.Should().Be(Target("snipper.json"));
        config.DisabledRules.Should().Contain("SNP0010");
        config.SeverityOverrides.Should().ContainKey("SNP0018").WhoseValue.Should().Be(CertaintyTier.Advisory);
        config.ExcludedNamespaces.Should().Contain("Company.Generated");
        config.ExcludedPathGlobs.Should().Contain("**/Generated/**");
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Discover_Config_In_A_Parent_Directory_For_Load()
    {
        WriteConfig(Target("snipper.json"), """{ "version": 1, "rules": { "SNP0007": "off" } }""");
        var nested = Path.Combine(_tempDirectory, "src", "App");
        Directory.CreateDirectory(nested);
        var warnings = new List<string>();

        var config = SnipperConfigLoader.Load(Path.Combine(nested, "App.csproj"), out var configPath, warnings);

        configPath.Should().Be(Target("snipper.json"));
        config.DisabledRules.Should().Contain("SNP0007");
    }

    [Fact]
    public void Return_Empty_With_Warning_When_Json_Is_Malformed_For_Load()
    {
        WriteConfig(Target("snipper.json"), "{ not json");
        var warnings = new List<string>();

        var config = SnipperConfigLoader.Load(Target("App.csproj"), out _, warnings);

        config.Should().Be(SnipperConfig.Empty);
        warnings.Should().ContainSingle(w => w.Contains("Could not parse", StringComparison.Ordinal));
    }

    [Fact]
    public void Return_Empty_With_Warning_When_Schema_Version_Is_Unsupported_For_Load()
    {
        WriteConfig(Target("snipper.json"), """{ "version": 99 }""");
        var warnings = new List<string>();

        var config = SnipperConfigLoader.Load(Target("App.csproj"), out _, warnings);

        config.Should().Be(SnipperConfig.Empty);
        warnings.Should().ContainSingle(w => w.Contains("unsupported schema version", StringComparison.Ordinal));
    }

    [Fact]
    public void Warn_And_Ignore_Unknown_Rule_Ids_And_Severities_For_Load()
    {
        WriteConfig(Target("snipper.json"), """
            { "version": 1, "rules": { "XXXX": "off", "SNP0001": "loud", "SNP0002": "high" } }
            """);
        var warnings = new List<string>();

        var config = SnipperConfigLoader.Load(Target("App.csproj"), out _, warnings);

        warnings.Should().HaveCount(2);
        config.DisabledRules.Should().BeEmpty();
        config.SeverityOverrides.Should().ContainSingle(pair => pair.Key == "SNP0002" && pair.Value == CertaintyTier.High);
    }

    private string Target(string fileName)
    {
        return Path.Combine(_tempDirectory, fileName);
    }

    private static void WriteConfig(string path, string content)
    {
        File.WriteAllText(path, content);
    }
}
