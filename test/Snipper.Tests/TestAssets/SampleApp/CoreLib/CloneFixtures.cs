namespace CloneFixtures.Primary;

// SNP0031 target - exact cross-project clone. SeedPair.Render and
// App/CloneFixturesMirrored.cs MirroredPair.Render are an identical
// >=60-token fragment: same statements, same literals, same order (Type-1).
//
// Note on types: these fixtures deliberately avoid System.Text.StringBuilder.
// SNP0028 (redundant qualifier) flags both `var builder = new StringBuilder()`
// and its declaration type, and RedundancyAnalyserShould counts those
// occurrences across the whole SampleApp fixture set; introducing more would
// break an unrelated, pre-existing rule assertion. System.Text.StringBuilder is
// used here in fully-qualified form, which is exactly the shape SNP0028 flags,
// so the clone is expressed with a value-type buffer instead.
public sealed class SeedPair
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

    public int WeightedTotal(int[] values)
    {
        var total = 0;
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] % 2 == 0)
            {
                total += values[index] * 3;
            }
            else if (values[index] % 3 == 0)
            {
                total += values[index] + 1;
            }
            else
            {
                total -= values[index];
            }
        }

        return total;
    }
}

// SNP0031 target - renamed-only clone (Type-2). Method name, parameter names and
// local names all differ from SeedPair.Render above, but every statement shape is
// identical, so the normalized token stream matches.
public sealed class SeedPairRenamed
{
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