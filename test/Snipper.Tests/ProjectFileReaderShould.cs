namespace Snipper.Tests;

using System.Xml.Linq;
using FluentAssertions;
using Snipper.Analysis;
using Xunit;

/// <summary>
/// Pins the per-path memo in <see cref="ProjectFileReader"/>.
///
/// Five call sites read the same csproj files and each used to build its own
/// <see cref="XDocument"/> with <c>LoadOptions.SetLineInfo</c>. The memo removes that
/// duplicate parse, which means it can now serve a STALE answer if invalidation is wrong.
///
/// That is not hypothetical: mutating the invalidation check so the cache never expires
/// left all 34 project-file-dependent tests green. Nothing else in the suite writes a
/// csproj between two reads, so the last-write guard was completely uncovered. These tests
/// close that hole by rewriting the file between reads.
/// </summary>
public sealed class ProjectFileReaderShould : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"snipper-pfr-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string WriteProject(string name, string assemblyName)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>{assemblyName}</AssemblyName>
              </PropertyGroup>
            </Project>
            """);
        return path;
    }

    [Fact]
    public void Parse_A_Project_File_For_Read()
    {
        var path = WriteProject("A.csproj", "Alpha");

        var info = ProjectFileReader.Read(path);

        info.Should().NotBeNull();
        info!.AssemblyName.Should().Be("Alpha");
    }

    [Fact]
    public void Reuse_The_Memo_For_A_Repeat_Read_For_Read()
    {
        var path = WriteProject("B.csproj", "Bravo");

        var first = ProjectFileReader.Read(path);
        var second = ProjectFileReader.Read(path);

        // Same instance back: the memo is actually serving the second read.
        second.Should().BeSameAs(first);
    }

    [Fact]
    public void Re_Read_After_The_File_Changes_For_Read()
    {
        var path = WriteProject("C.csproj", "Charlie");

        var before = ProjectFileReader.Read(path);
        before!.AssemblyName.Should().Be("Charlie");

        // Rewrite the same path. Last-write granularity on some filesystems is coarse, so
        // push the timestamp forward explicitly rather than relying on wall-clock timing.
        Thread.Sleep(1100);
        File.WriteAllText(path, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>Delta</AssemblyName>
              </PropertyGroup>
            </Project>
            """);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        var after = ProjectFileReader.Read(path);

        after.Should().NotBeNull();
        after!.AssemblyName.Should().Be("Delta", "a changed csproj must invalidate the memo");
    }

    [Fact]
    public void Cache_A_Parse_Failure_Without_Poisoning_Later_Reads_For_Read()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "Broken.csproj");
        File.WriteAllText(path, "<Project><PropertyGroup>");   // unterminated

        var failed = ProjectFileReader.Read(path);
        failed.Should().BeNull("malformed XML must report null, not throw");

        // Repair the file: the failure must not be cached past the change.
        Thread.Sleep(1100);
        File.WriteAllText(path, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>Echo</AssemblyName>
              </PropertyGroup>
            </Project>
            """);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        var repaired = ProjectFileReader.Read(path);

        repaired.Should().NotBeNull();
        repaired!.AssemblyName.Should().Be("Echo");
    }
}