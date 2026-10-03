namespace CloneFixtures.Mirrored;

// SNP0031 target - the mirrored half of the CoreLib/CloneFixtures.cs pair.
// Render is a verbatim copy of the seed (Type-1); Compose renames every
// identifier but keeps every statement shape (Type-2). Uses a Span<char>
// buffer rather than StringBuilder so it adds no SNP0028 redundancy findings to
// the shared SampleApp fixture counts.
public sealed class MirroredPair
{
    public string Render(string label, int width, int height)
    {
        Span<char> buffer = stackalloc char[width * height + 64];
        var cursor = 0;
        buffer[cursor++] = label[0];
        buffer[cursor++] = ':';
        buffer[cursor++] = (char)('0' + (width % 10));
        buffer[cursor++] = 'x';
        buffer[cursor++] = (char)('0' + (height % 10));
        buffer[cursor++] = '|';
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                buffer[cursor++] = (char)('a' + ((row + column) % 26));
            }

            buffer[cursor++] = '/';
        }

        if (cursor > 512)
        {
            return new string(buffer[..512]);
        }

        return new string(buffer[..cursor]).TrimEnd('/');
    }

    public string Compose(string caption, int columns, int rows)
    {
        Span<char> buffer = stackalloc char[columns * rows + 64];
        var cursor = 0;
        buffer[cursor++] = caption[0];
        buffer[cursor++] = ':';
        buffer[cursor++] = (char)('0' + (columns % 10));
        buffer[cursor++] = 'x';
        buffer[cursor++] = (char)('0' + (rows % 10));
        buffer[cursor++] = '|';
        for (var line = 0; line < rows; line++)
        {
            for (var position = 0; position < columns; position++)
            {
                buffer[cursor++] = (char)('a' + ((line + position) % 26));
            }

            buffer[cursor++] = '/';
        }

        if (cursor > 512)
        {
            return new string(buffer[..512]);
        }

        return new string(buffer[..cursor]).TrimEnd('/');
    }
}

// SNP0031 control - identical usings, different bodies. The using lists are
// byte-identical to the file above, which is exactly what the using-directive
// exclusion must neutralize: without it this file pair would collide on the
// shared using block alone.
public sealed class SameUsingsDifferentBody
{
    public int Classify(int input)
    {
        if (input < 0)
        {
            return -1;
        }

        if (input == 0)
        {
            return 0;
        }

        var magnitude = input * input;
        return magnitude > 1000 ? 1 : 2;
    }
}