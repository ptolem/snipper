namespace CoreLib;

using System.Text.Json;

// Consumes System.Text.Json so SNP0003 stays silent for the package reference while
// SNP0013 flags it as framework-provided. Called from App/Worker.cs to stay alive.
public static class JsonRoundTrip
{
    public static string Echo(string value)
    {
        // Fully qualified, not `using System.Text.Json` alone: FrameworkPatterns.cs declares a
        // stand-in `CoreLib.JsonSerializer`, and a type in the enclosing namespace beats one
        // brought in by a using - so the unqualified name bound to the stand-in (CS0117 here).
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        return System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? string.Empty;
    }
}
