namespace CoreLib;

// Partner copies for App/SameCountAnchor.cs. Two distinct sets, each paired with
// one anchor method, so the anchor file carries two findings that report identical
// counts and differ only in which partner they name — see the F6 note on
// DuplicateFragmentAnalyser and the fingerprint that carries no position.
public static class SameCountPartnerOne
{
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
}

public static class SameCountPartnerTwo
{
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
