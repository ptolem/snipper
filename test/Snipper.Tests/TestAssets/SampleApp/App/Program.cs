namespace App;

using WebLib.LegacyExtensions;
using WebLib.OrdinalEnums;
using WebLib.WireContracts;

// Process entry point: the CLR roots all usage here, so the containing type
// must never be flagged however unreferenced it looks statically.
internal static class Program
{
    private static int Main()
    {
        // The WebLib reference has to be load-bearing or SNP0012 reports it as an
        // unused project reference. These calls are also the consumer side of the
        // F2/F3 fixture: an obsolete extension holder is used, and both
        // ordinal-bound enums are touched from outside their declaring project.
        _ = LegacyStringUsage.Call();
        _ = ReflectedEnumUsage.CountReflected();
        _ = ConverterBoundCommandType.Alpha;
        _ = FakeRouterUsage.Call();
        _ = typeof(WebLib.WireContracts.ApiRoutes);

        return Worker.Run();
    }
}