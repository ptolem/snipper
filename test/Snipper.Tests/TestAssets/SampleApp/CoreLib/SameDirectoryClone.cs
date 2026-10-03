namespace CloneFixtures.Adjacent;

// SNP0031 control - a same-directory clone. RootlessSibling and its peer in
// this same file are an identical >=60-token fragment, but both live in one
// directory, so the cross-directory structural guard suppresses the set.
//
// This is the shape that dominated Snipper's own dogfood run: sibling
// analysers that share one project/document loop are duplication by design,
// not the cross-project copy-paste the rule is meant to surface.
public sealed class RootlessSibling
{
    public int Tally(IReadOnlyList<int> samples)
    {
        var buckets = new System.Collections.Generic.Dictionary<int, int>();
        foreach (var sample in samples)
        {
            var key = sample % 17;
            if (buckets.TryGetValue(key, out var seen))
            {
                buckets[key] = seen + 1;
            }
            else
            {
                buckets[key] = 1;
            }
        }

        var running = 0;
        var output = new System.Text.StringBuilder();
        foreach (var pair in buckets)
        {
            running += pair.Value;
            output.Append(pair.Key);
            output.Append(':');
            output.Append(pair.Value);
            output.Append(';');
        }

        return running + output.Length;
    }
}

// A verbatim copy of Tally above, in the SAME file and the SAME directory.
// Together they form a >=60-token same-directory clone set that the
// cross-directory guard must suppress.
public sealed class AdjacentPeer
{
    public int Count(IReadOnlyList<int> samples)
    {
        var buckets = new System.Collections.Generic.Dictionary<int, int>();
        foreach (var sample in samples)
        {
            var key = sample % 17;
            if (buckets.TryGetValue(key, out var seen))
            {
                buckets[key] = seen + 1;
            }
            else
            {
                buckets[key] = 1;
            }
        }

        var running = 0;
        var output = new System.Text.StringBuilder();
        foreach (var pair in buckets)
        {
            running += pair.Value;
            output.Append(pair.Key);
            output.Append(':');
            output.Append(pair.Value);
            output.Append(';');
        }

        return running + output.Length;
    }
}
