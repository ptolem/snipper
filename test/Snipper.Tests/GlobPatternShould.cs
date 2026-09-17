namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Cli;
using Xunit;

public sealed class GlobPatternShould
{
    [Theory]
    [InlineData("**/Generated/**", @"C:\ws\x\Generated\y.cs", true)]
    [InlineData("**/Generated/**", "Generated/y.cs", true)]
    [InlineData("**/Generated/**", @"C:\ws\x\Other\y.cs", false)]
    [InlineData("src/*.cs", "src/a.cs", true)]
    [InlineData("src/*.cs", "src/sub/a.cs", false)]
    [InlineData("*.cs", "a.cs", true)]
    [InlineData("**/deadcode.cs", @"C:\ws\CoreLib\DeadCode.cs", true)]
    [InlineData("src/File?.cs", "src/File1.cs", true)]
    [InlineData("src/File?.cs", "src/File12.cs", false)]
    [InlineData("**/obj/**", @"C:\ws\App\obj\Debug\x.g.cs", true)]
    public void Match_Expected_Paths_For_IsMatch(string pattern, string path, bool expected)
    {
        GlobPattern.IsMatch(pattern, path).Should().Be(expected);
    }
}
