namespace CoreLib.Shadowing
{
    // SNP0028 two-sided pins (1.6.2 addendum — the milkrun "FP-6" shape): sibling
    // types in nested namespaces of the usage file's own namespace.
    //  - CoreLib imports CoreLib.Shadowing.Algolia via global using, so a BARE
    //    FwShadowHandler binds to the Algolia type: the Algolia qualification IS
    //    redundant and must be flagged.
    //  - The Topsort sibling is not imported: bare FwShadowHandler would rebind
    //    to the Algolia type, so the Topsort qualification is load-bearing and
    //    must NOT be flagged (the speculation gate's rebind check refuses).
    internal static class FwShadowUsage
    {
        // Flagged: bare FwShadowHandler binds identically via the global using.
        internal static Type ForAlgolia() => typeof(Algolia.FwShadowHandler);

        // Not flagged: stripping would rebind to the Algolia sibling.
        internal static Type ForTopsort() => typeof(CoreLib.Shadowing.Topsort.FwShadowHandler);

        // Keeps the global using compiler-live (a genuine bare binding).
        internal static Type ForAlgoliaBare() => typeof(FwShadowHandler);
    }
}

namespace CoreLib.Shadowing.Algolia
{
    internal sealed class FwShadowHandler;
}

namespace CoreLib.Shadowing.Topsort
{
    internal sealed class FwShadowHandler;
}
