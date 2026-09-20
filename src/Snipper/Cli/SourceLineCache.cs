namespace Snipper.Cli;

/// <summary>
/// Per-file memoized source-line lookup for report line snippets (FP-5, 1.6.1):
/// each finding carries the trimmed text of the line it points at, so consumers
/// can mechanically verify a finding still matches before applying it. Files are
/// read once per report; missing/unreadable files and out-of-range lines degrade
/// to null — never to a failed run.
/// </summary>
internal sealed class SourceLineCache
{
    private readonly Dictionary<string, string[]?> _linesByPath = new(StringComparer.OrdinalIgnoreCase);

    public string? GetLine(string? path, int lineNumber)
    {
        if (string.IsNullOrEmpty(path) || lineNumber <= 0)
        {
            return null;
        }

        if (!_linesByPath.TryGetValue(path, out var lines))
        {
            lines = TryReadAllLines(path);
            _linesByPath[path] = lines;
        }

        if (lines is null || lineNumber > lines.Length)
        {
            return null;
        }

        var line = lines[lineNumber - 1].Trim();
        return line.Length == 0 ? null : line;
    }

    private static string[]? TryReadAllLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
