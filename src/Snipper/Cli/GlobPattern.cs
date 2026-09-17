namespace Snipper.Cli;

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Minimal glob matcher for finding-path exclusions. Supports `**` (any directories,
/// including none — `**\/X` also matches bare `X`), `*` (within one segment), and `?`
/// (one character). Paths are normalised to forward slashes; matching is
/// case-insensitive. Compiled patterns are cached for the process lifetime.
/// </summary>
internal static class GlobPattern
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    public static bool IsMatch(string pattern, string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        var regex = Cache.GetOrAdd(
            pattern,
            static p => new Regex(ToRegex(p), RegexOptions.IgnoreCase | RegexOptions.Compiled));

        return regex.IsMatch(Normalize(fullPath));
    }

    private static string Normalize(string path)
    {
        return path.Replace('\\', '/');
    }

    private static string ToRegex(string pattern)
    {
        var glob = Normalize(pattern);
        var builder = new StringBuilder("^");

        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                var isDouble = i + 1 < glob.Length && glob[i + 1] == '*';
                if (isDouble && i + 2 < glob.Length && glob[i + 2] == '/')
                {
                    builder.Append("(?:.*/)?");
                    i += 2;
                }
                else if (isDouble)
                {
                    builder.Append(".*");
                    i += 1;
                }
                else
                {
                    builder.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(c.ToString()));
            }
        }

        builder.Append('$');
        return builder.ToString();
    }
}
