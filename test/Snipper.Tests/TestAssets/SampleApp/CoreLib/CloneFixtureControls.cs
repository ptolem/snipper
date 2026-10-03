namespace CloneFixtures.Controls;

// SNP0031 control - too short to report. Deliberately identical in shape to
// ShortHelperBelow.Add, but far below the 60-token window: guards that the
// threshold is a floor, not a formality.
public static class ShortHelper
{
    public static int One()
    {
        return 1;
    }

    public static int Add(int left, int right)
    {
        return left + right;
    }
}

// SNP0031 control - long enough to clear the token window in principle, but
// sharing no 60-token contiguous run with anything else in the fixture set.
// Uses a 6k+-1 wheel sieve: no StringBuilder, no nested index loops, no switch
// on strings, so it cannot alias the Render/Compose/Describe families.
public static class DivergentBody
{
    private static readonly int[] Wheel =
    [
        7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59, 61, 67,
        71, 73, 79, 83, 89, 97, 101, 103, 107, 109, 113, 127,
    ];

    public static bool IsPrime(int candidate)
    {
        if (candidate < 2)
        {
            return false;
        }

        if (candidate < 7)
        {
            return candidate is 2 or 3 or 5;
        }

        if (candidate % 2 == 0 || candidate % 3 == 0 || candidate % 5 == 0)
        {
            return false;
        }

        var limit = (int)System.Math.Sqrt(candidate);
        var found = System.Array.BinarySearch(Wheel, limit);
        var reachable = found < 0 ? ~found - 1 : found;

        for (var index = 0; index <= reachable; index++)
        {
            var divisor = Wheel[index];
            if (candidate % divisor == 0)
            {
                return false;
            }

            var quotient = candidate / divisor;
            if (quotient == divisor || quotient < divisor)
            {
                break;
            }
        }

        return true;
    }

    public static int CountPrimesBelow(int bound)
    {
        var total = 0;
        for (var candidate = 0; candidate < bound; candidate++)
        {
            if (IsPrime(candidate))
            {
                total++;
            }
        }

        return total;
    }
}

// SNP0031 control - an intra-file duplicate. TwiceInOneFile.A and
// TwiceInOneFile.B are identical >=60-token fragments in the SAME source file.
// Same-path locations are deduped by design (decision 5), so this must NOT be
// reported.
//
// The bucket-histogram body is deliberately unlike every other fixture body:
// dictionary TryGetValue/out-var accumulation, key sort, and string.Join. If it
// shared a 60-token run with another file it would acquire a cross-file partner
// and no longer exercise intra-file suppression.
public sealed class TwiceInOneFile
{
    public string A(string label, int width, int height)
    {
        var counts = new System.Collections.Generic.Dictionary<int, int>();
        var seed = width * 31 + height;
        for (var step = 0; step < seed; step++)
        {
            var bucket = (step * 7) % 13;
            if (counts.TryGetValue(bucket, out var existing))
            {
                counts[bucket] = existing + 1;
            }
            else
            {
                counts[bucket] = 1;
            }
        }

        var keys = new System.Collections.Generic.List<int>(counts.Keys);
        keys.Sort();
        var parts = new System.Collections.Generic.List<string>();
        foreach (var key in keys)
        {
            parts.Add(key.ToString(System.Globalization.CultureInfo.InvariantCulture));
            parts.Add("=");
            parts.Add(counts[key].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var joined = string.Join(",", parts);
        return label + joined;
    }

    public string B(string label, int width, int height)
    {
        var counts = new System.Collections.Generic.Dictionary<int, int>();
        var seed = width * 31 + height;
        for (var step = 0; step < seed; step++)
        {
            var bucket = (step * 7) % 13;
            if (counts.TryGetValue(bucket, out var existing))
            {
                counts[bucket] = existing + 1;
            }
            else
            {
                counts[bucket] = 1;
            }
        }

        var keys = new System.Collections.Generic.List<int>(counts.Keys);
        keys.Sort();
        var parts = new System.Collections.Generic.List<string>();
        foreach (var key in keys)
        {
            parts.Add(key.ToString(System.Globalization.CultureInfo.InvariantCulture));
            parts.Add("=");
            parts.Add(counts[key].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var joined = string.Join(",", parts);
        return label + joined;
    }
}