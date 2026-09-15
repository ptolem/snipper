namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Cli;
using Xunit;

public sealed class CliRunnerShould
{
    [Fact]
    public async Task Return_One_When_No_Target_Path_Is_Supplied_For_RunAsync()
    {
        var exitCode = await CliRunner.RunAsync([]);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task Return_One_When_Target_File_Does_Not_Exist_For_RunAsync()
    {
        var exitCode = await CliRunner.RunAsync([Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.sln")]);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task Return_One_When_Format_Value_Is_Invalid_For_RunAsync()
    {
        var target = Path.Combine(AppContext.BaseDirectory, "TestAssets", "SampleApp", "CoreLib", "CoreLib.csproj");

        var exitCode = await CliRunner.RunAsync([target, "--format", "yaml"]);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task Return_One_When_Certainty_Tier_Value_Is_Invalid_For_RunAsync()
    {
        var target = Path.Combine(AppContext.BaseDirectory, "TestAssets", "SampleApp", "CoreLib", "CoreLib.csproj");

        var exitCode = await CliRunner.RunAsync([target, "--certainty-tier", "cosmic"]);

        exitCode.Should().Be(1);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public async Task Return_Zero_When_Version_Is_Requested_For_RunAsync(string flag)
    {
        var exitCode = await CliRunner.RunAsync([flag]);

        exitCode.Should().Be(0);
    }

    [Fact]
    public void Return_The_Package_Version_For_GetToolVersion()
    {
        CliRunner.GetToolVersion().Should().MatchRegex(@"^\d+\.\d+\.\d+$");
    }

    [Fact]
    public async Task Return_One_When_Exclude_Namespaces_Value_Is_Missing_For_RunAsync()
    {
        var target = Path.Combine(AppContext.BaseDirectory, "TestAssets", "SampleApp", "CoreLib", "CoreLib.csproj");

        var exitCode = await CliRunner.RunAsync([target, "--exclude-namespaces"]);

        exitCode.Should().Be(1);
    }
}
