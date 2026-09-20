namespace CoreLib;

using System.Reflection;

// SNP0005/0006 reflection-discovery fixtures (1.6.2): types instantiated via
// assembly scan + Activator have no C# reference, so the reference graph cannot
// see them. A type deriving from a base that appears in an IsSubclassOf call —
// or as the receiver of IsAssignableFrom — counts as usage evidence for the
// TYPE finding. Members still stand on their own: the framework only
// instantiates the type, so a never-read member stays flagged.

internal abstract class FwEndpointGroup
{
    public abstract void MapFw();
}

internal class FwScannedGroupBeta : FwEndpointGroup
{
    public override void MapFw()
    {
    }
}

internal sealed class FwScannedGroupAlpha : FwEndpointGroup
{
    public override void MapFw()
    {
    }

    // Never read even by the framework (the GroupDescription analog) — flagged.
    public string FwNeverReadSetting => "x";
}

// Transitive case: the scan discovers subclasses of subclasses too.
internal sealed class FwScannedGroupGamma : FwScannedGroupBeta
{
    public override void MapFw()
    {
    }
}

// IsAssignableFrom shape: typeof(T).IsAssignableFrom(candidate).
internal abstract class FwMessagePlugin
{
    public abstract void HandleFw();
}

internal sealed class FwScannedPlugin : FwMessagePlugin
{
    public override void HandleFw()
    {
    }
}

// Control: under no scan base — the type finding must survive.
internal sealed class FwUnscannedGroup
{
    public void MapFw()
    {
    }
}

internal static class FwReflectionDiscoverySeeds
{
    // Deliberately never called (the FwEvidenceSeeds precedent): the syntax pass
    // scans all documents, so these methods being flagged proves evidence does
    // not blanket-suppress.
    internal static void RegisterFwEndpoints()
    {
        foreach (var type in Assembly.GetExecutingAssembly().GetExportedTypes().Where(t => t.IsSubclassOf(typeof(FwEndpointGroup))))
        {
            _ = (FwEndpointGroup?)Activator.CreateInstance(type);
        }
    }

    internal static void RegisterFwPlugins()
    {
        foreach (var type in typeof(FwMessagePlugin).Assembly.GetTypes().Where(t => typeof(FwMessagePlugin).IsAssignableFrom(t) && !t.IsAbstract))
        {
            _ = Activator.CreateInstance(type);
        }
    }
}
