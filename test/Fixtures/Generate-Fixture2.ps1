#Requires -Version 7.0
<#
.SYNOPSIS
Generates the fixture2 regression solution used for end-to-end Snipper smoke tests.

.DESCRIPTION
Materialises a small solution (App -> Core + Orphan, plus a detached-on-disk project)
that deliberately produces a stable set of findings across every Snipper rule.
The fixture replaces the ad-hoc %TEMP% copy that silently rotted in September 2026 —
regenerate anywhere, diff counts, catch regressions.

Usage:
    pwsh test/Fixtures/Generate-Fixture2.ps1 [-OutputPath <dir>]
    snipper <dir>/Fixture.slnx
    snipper <dir>/Fixture.slnx --config-analysis

Expected findings (pinned for Snipper 1.3.1, verified 2026-09-18):
    default run:           20 findings
    --config-analysis run: 22 findings (adds SNP0007 + SNP0008)
Per-rule expectation (default): SNP0001=1 SNP0002=1 SNP0003=1 SNP0004=1 SNP0005=2
    SNP0006=3 SNP0009=1 SNP0010=1 SNP0011=1 SNP0012=1 SNP0013=1 SNP0018=1 SNP0019=1 SNP0020=1
    SNP0021=1 SNP0022=1 SNP0025=1
NOTE: restore the fixture (dotnet restore) before analysing — package rules need obj/project.assets.json.
A count drift without a corresponding rule change is a regression signal — investigate,
then either fix the regression or update this header with the new verified counts.
#>
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'fixture2')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-FixtureFile([string]$RelativePath, [string]$Content) {
    $path = Join-Path $OutputPath $RelativePath
    $parent = Split-Path $path -Parent
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Set-Content -Path $path -Value $Content -NoNewline
}

Write-FixtureFile 'Fixture.slnx' @'
<Solution>
  <Folder Name="/src/">
    <Project Path="src/App/App.csproj" />
    <Project Path="src/Core/Core.csproj" />
    <Project Path="src/Orphan/Orphan.csproj" />
  </Folder>
</Solution>
'@

Write-FixtureFile 'src/App/App.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Core\Core.csproj" />
    <ProjectReference Include="..\Orphan\Orphan.csproj" />
    <!-- SNP0003: declared, never used -->
    <PackageReference Include="Humanizer.Core" Version="2.14.1" />
    <!-- SNP0012: direct Serilog edge is transitively supplied by the sink -->
    <PackageReference Include="Serilog" Version="4.0.0" />
    <PackageReference Include="Serilog.Sinks.Console" Version="6.0.0" />
  </ItemGroup>
</Project>
'@

Write-FixtureFile 'src/App/appsettings.json' @'
{
  "App": { "BaseUrl": "https://example.com" },
  "LegacySection": { "OldKey": "x" },
  "ConnectionStrings": { "Db": "Server=." }
}
'@

Write-FixtureFile 'src/App/Program.cs' @'
namespace App;

internal static class Program
{
    private static int Main() => Worker.Run();
}
'@

Write-FixtureFile 'src/App/Worker.cs' @'
using System.Xml.Linq;

namespace App;

// Stand-ins for the framework contracts Snipper recognizes by name (options binding).
public interface IOptions<T>
{
    T Value { get; }
}

public static class ServiceCollection
{
    public static void AddOptions<TOptions>() { }
}

public static class Worker
{
    public static int Run()
    {
        ServiceCollection.AddOptions<AppOptions>();

        // Touches every options property so the POCO members stay referenced.
        _ = new AppOptions { BaseUrl = "https://example.com", RetiredSetting = "legacy" };

        var greeter = new Core.Greeter();
        _ = greeter.Greet("world");
        _ = Core.JsonRoundTrip.Echo("ping");

        Serilog.Log.Logger = new Serilog.LoggerConfiguration().WriteTo.Console().CreateLogger();

        var unusedLocal = 42;
        _ = WithUnusedParam(1, 2);
        _retryBudget = 7;
        _ = GreetWithFallback("hi", 2);
        _ = EchoExplicit<int>(5);
        return MultiplyUsed(2);
    }

    // SNP0021: written in Run, never read.
    private static int _retryBudget;

    // SNP0022: the literal 2 matches the parameter default.
    private static string GreetWithFallback(string text, int times = 2) => text + times;

    // SNP0025: inference infers <int> without the explicit list.
    private static T EchoExplicit<T>(T value) => value;

    private static int MultiplyUsed(int used) => used * 2;

    private static int MultiplyUnused(int a, int b) => a * b;

    private static int WithUnusedParam(int used, int unused) => used;

    public static int UnreachableAfterReturn(int value)
    {
        return value;
        var dead = value * 3;
    }
}
'@

Write-FixtureFile 'src/App/AppOptions.cs' @'
namespace App;

public sealed class AppOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string RetiredSetting { get; set; } = string.Empty;
}
'@

Write-FixtureFile 'src/Core/Core.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <!-- SNP0013: used in JsonRoundTrip but ships in the net10.0 shared framework -->
    <PackageReference Include="System.Text.Json" Version="8.0.5" />
  </ItemGroup>
</Project>
'@

Write-FixtureFile 'src/Core/Greeter.cs' @'
namespace Core;

public sealed class Greeter
{
    public string Greet(string name) => $"Hello, {name}!";

    // var prefix = "Hello";
    // if (prefix.Length > 0)
    // {
    //     Console.WriteLine(prefix);
    // }

    [Obsolete("Use Greet instead.")]
    public string OldGreet(string name) => Greet(name);
}

internal sealed class UnusedInternalType
{
}

public sealed class PublicApi
{
    public int UnusedPublicMethod() => 1;
}
'@

Write-FixtureFile 'src/Core/JsonRoundTrip.cs' @'
namespace Core;

// Keeps System.Text.Json used (SNP0003 stays silent) so SNP0013 flags the inbox reference.
public static class JsonRoundTrip
{
    public static string Echo(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
'@

Write-FixtureFile 'src/Orphan/Orphan.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@

Write-FixtureFile 'src/Orphan/OrphanType.cs' @'
namespace Orphan;

// Referenced by App.csproj but never used → SNP0004 on the reference; SNP0005 on this type.
internal sealed class OrphanType
{
}
'@

# Detached on disk, NOT in Fixture.slnx → SNP0011 (detached sweep).
Write-FixtureFile 'src/Detached/Detached.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
'@

Write-FixtureFile 'src/Detached/DetachedType.cs' @'
namespace Detached;

internal sealed class DetachedType
{
}
'@

Write-Host "fixture2 generated at $OutputPath"
Write-Host "Verify: dotnet restore `"$OutputPath/Fixture.slnx`", then snipper `"$OutputPath/Fixture.slnx`" (expect 20) and with --config-analysis (expect 22)."
