namespace CloneFixtures.Mirrored;

// SNP0031 control - the mirror of CoreLib/FileScopeClone.cs. PopCount and
// Spread are exact copies; ClassifyLabel is deliberately different so the
// shared span resolves to the two copied helpers only. This file also carries
// a byte-identical using block to its sibling, which is what the
// using-directive exclusion must neutralize.
internal static class FileScopeHelpersMirror
{
    internal static int PopCount(ulong value)
    {
        var working = value;
        working = working - ((working >> 1) & 0x5555555555555555UL);
        working = (working & 0x3333333333333333UL) + ((working >> 2) & 0x3333333333333333UL);
        working = (working + (working >> 4)) & 0x0f0f0f0f0f0f0f0fUL;
        var folded = (working * 0x0101010101010101UL) >> 56;
        return (int)folded;
    }

    internal static int Spread(int seed, int lanes)
    {
        var table = new int[lanes];
        var cursor = seed & 0x7fffffff;
        for (var pass = 0; pass < lanes; pass++)
        {
            cursor = (cursor * 1103515245 + 12345) & 0x7fffffff;
            table[pass] = cursor >> 16;
        }

        for (var index = lanes - 1; index > 0; index--)
        {
            var swap = PopCount((ulong)(seed + index)) % (index + 1);
            var held = table[index];
            table[index] = table[swap];
            table[swap] = held;
        }

        var total = 0;
        foreach (var value in table)
        {
            total += value ^ (value >> 3);
        }

        return total & 0x7fffffff;
    }

    internal static string ClassifyLabel(int input)
    {
        if (input < 0)
        {
            return "negative";
        }

        if (input == 0)
        {
            return "zero";
        }

        var magnitude = input * input;
        return magnitude > 1000 ? "large" : "small";
    }
}