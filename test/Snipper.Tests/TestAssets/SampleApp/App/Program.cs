namespace App;

// Process entry point: the CLR roots all usage here, so the containing type
// must never be flagged however unreferenced it looks statically.
internal static class Program
{
    private static int Main() => Worker.Run();
}
