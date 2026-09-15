namespace Snipper.Analysis;

using System.Collections.Frozen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Evidence scanner for plugin-style assembly loading: an assembly name spelled in a
/// string literal (<c>Assembly.Load("PluginLib")</c>, Scrutor scanning, module
/// registries) or in configuration JSON means the project may be consumed by name
/// rather than by reference. Evidence suppresses orphan findings — it never creates
/// them. Syntax-only work: safe to run in parallel (no semantic binding).
/// </summary>
internal static class AssemblyNameEvidenceScanner
{
    private static readonly FrozenSet<string> PrunedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", "node_modules",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the assembly name appears inside any string literal in any document —
    /// including generated and external documents, which are legitimate evidence.
    /// Names shorter than 3 characters always return true (too short to be distinctive).
    /// The assembly's OWN generated identity files (obj/&lt;AssemblyName&gt;.*.cs:
    /// AssemblyInfo, GlobalUsings, …) are excluded — they spell the name by definition
    /// and are self-reference, not external consumption.
    /// </summary>
    public static bool IsAssemblyNameSpelled(Solution solution, string assemblyName)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);

        if (assemblyName.Length < 3)
        {
            return true;
        }

        var documents = solution.Projects
            .SelectMany(static p => p.Documents)
            .Where(static d => d.SupportsSyntaxTree);

        var found = 0;
        Parallel.ForEach(
            documents,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            (document, loopState) =>
            {
                if (Volatile.Read(ref found) == 1)
                {
                    loopState.Stop();
                    return;
                }

                if (IsOwnIdentityDocument(document.FilePath, assemblyName))
                {
                    return;
                }

                var root = document.GetSyntaxTreeAsync().GetAwaiter().GetResult()?.GetRoot();
                if (root is null)
                {
                    return;
                }

                foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
                {
                    if (literal.IsKind(SyntaxKind.StringLiteralExpression)
                        && literal.Token.ValueText.Contains(assemblyName, StringComparison.Ordinal))
                    {
                        Interlocked.Exchange(ref found, 1);
                        loopState.Stop();
                        return;
                    }
                }
            });

        return found == 1;
    }

    private static bool IsOwnIdentityDocument(string? filePath, string assemblyName)
    {
        return filePath is not null
            && filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(filePath).StartsWith(assemblyName + ".", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the assembly name appears in any *.json file under the root directory
    /// (appsettings module lists, plugin manifests, deployment descriptors).
    /// </summary>
    public static bool IsAssemblyNameSpelledInJsonFiles(string rootDirectory, string assemblyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);

        return assemblyName.Length >= 3 && ScanDirectory(rootDirectory, assemblyName);
    }

    private static bool ScanDirectory(string directory, string assemblyName)
    {
        IReadOnlyList<string> entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        foreach (var entry in entries)
        {
            if (Directory.Exists(entry))
            {
                if (!PrunedDirectoryNames.Contains(Path.GetFileName(entry)) && ScanDirectory(entry, assemblyName))
                {
                    return true;
                }

                continue;
            }

            if (!entry.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (File.ReadAllText(entry).Contains(assemblyName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable file carries no evidence either way — keep scanning.
            }
        }

        return false;
    }
}
