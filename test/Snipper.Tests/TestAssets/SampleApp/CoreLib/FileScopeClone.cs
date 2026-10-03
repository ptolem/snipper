// SNP0031 control - a file-scope (non-type) fragment, duplicated across two
// files. There is no enclosing type declaration here, so namespace exclusion
// must resolve through the enclosing namespace declaration rather than the
// type-ancestor path the member-level rules use.
//
// The duplicated span is the two static helpers below. Both bodies are
// distinctive (bit-scan popcount and a radix bucket pass) so this file shares
// no 60-token run with any other fixture except its own mirror.
internal static class FileScopeHelpers
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
}