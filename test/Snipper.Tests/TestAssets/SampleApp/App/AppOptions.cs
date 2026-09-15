namespace App;

public sealed class AppOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; }
    public string RetiredSetting { get; set; } = string.Empty;
}
