namespace CoreLib;

using System.Text.Json;

// Consumes System.Text.Json so SNP0003 stays silent for the package reference while
// SNP0013 flags it as framework-provided. Called from App/Worker.cs to stay alive.
public static class JsonRoundTrip
{
    public static string Echo(string value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<string>(json) ?? string.Empty;
    }
}
