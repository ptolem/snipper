namespace Snipper.Tests;

using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

internal static class TestBootstrap
{
    // MSBuildLocator MUST execute before any MSBuild/Roslyn workspace types load
    // into the test host process.
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }
}
