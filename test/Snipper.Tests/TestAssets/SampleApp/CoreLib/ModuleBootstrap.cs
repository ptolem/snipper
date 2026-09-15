namespace CoreLib;

using System.Runtime.CompilerServices;

// The CLR invokes [ModuleInitializer] methods at module load — zero source
// references by design. Neither the type nor the method may be flagged.
internal static class ModuleBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
    }
}
