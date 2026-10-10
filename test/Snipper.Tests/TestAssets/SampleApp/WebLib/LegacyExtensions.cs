namespace WebLib.LegacyExtensions;

// ---------------------------------------------------------------------------
// F3c: an extension invocation binds to the METHOD, not the class. A holder can
// be in constant use while its own name appears nowhere in source.
// ---------------------------------------------------------------------------

[Obsolete("Use ModernStringExtensions")]
public static class LegacyStringExtensions
{
    public static string Shout(this string value) => value.ToUpperInvariant();
}

/// <summary>Negative control: no member is ever called, so the holder is dead.</summary>
[Obsolete("Nothing calls any of these")]
public static class DeadLegacyStringExtensions
{
    public static string Whisper(this string value) => value.ToLowerInvariant();
}

public static class LegacyStringUsage
{
    public static string Call() => "hello".Shout();
}