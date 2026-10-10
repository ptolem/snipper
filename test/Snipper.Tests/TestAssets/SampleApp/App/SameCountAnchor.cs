namespace App;

// SNP0031 identity fixture — the ANCHOR side. Holds two clone sets of identical
// shape and identical line count, each paired with the matching copy in
// CoreLib/SameCountCloneSets.cs, and separated by a filler so the two anchor on
// different lines.
//
// This is the milkrun shape at
// src/Metro60.CommerceTools.MyProfile/WebApplicationBuilderExtensions.cs:109, where
// one line anchored two sets whose messages were byte-identical. The baseline
// fingerprint is RuleId|path|message and carries no position, so those two findings
// shared one fingerprint and suppressing one suppressed the other.
//
// StringBuilder is avoided in favour of a stackalloc span because SNP0028 flags the
// qualified form and an unrelated rule counts those occurrences fixture-wide.
public static class SameCountAnchor
{
    private static string FillerForSeparation(int a, int b, int c)
    {
        var total = 0;
        total += a * 3 + b * 5 + c * 7;
        total += (a ^ b) * (b ^ c);
        total += (a + b + c) % 11;
        total -= (a - b) * (c + 1);
        total ^= a << 2;
        total |= b >> 1;
        return total.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string RenderFirst(int width, int height, int depth)
    {
        Span<char> buffer = stackalloc char[width * height + 64];
        var cursor = 0;
        buffer[cursor++] = (char)('0' + (width % 10));
        buffer[cursor++] = (char)('0' + (height % 10));
        buffer[cursor++] = (char)('0' + (depth % 10));
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                buffer[cursor++] = (char)('a' + ((row + column + depth) % 26));
            }

            buffer[cursor++] = '|';
        }

        return new string(buffer[..cursor]);
    }

    public static string RenderSecond(int width, int height, int depth)
    {
        Span<char> buffer = stackalloc char[width * height + 64];
        var cursor = 0;
        buffer[cursor++] = (char)('0' + (width % 10));
        buffer[cursor++] = (char)('0' + (height % 10));
        buffer[cursor++] = (char)('0' + (depth % 10));
        for (var row = height; row > 0; row--)
        {
            for (var column = width; column > 0; column--)
            {
                buffer[cursor++] = (char)('a' + ((row * column + depth) % 26));
            }

            buffer[cursor++] = '!';
        }

        return new string(buffer[..cursor]);
    }
}