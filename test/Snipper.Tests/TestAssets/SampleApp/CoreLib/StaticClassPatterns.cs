namespace CoreLib;

// Extension invocations ("hello".Shout()) bind to the method symbol — this class's
// name never appears at any call site even though every method is in use.
public static class TextExtensions
{
    public static string Shout(this string value)
    {
        return value.ToUpperInvariant();
    }
}

// Imported via "using static" and called unqualified (Triple(2)).
public static class ViaUsingStatic
{
    public static int Triple(int value)
    {
        return value * 3;
    }
}
