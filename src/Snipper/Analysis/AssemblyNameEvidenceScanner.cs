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
/// Batched by design: one solution-wide scan answers for every candidate name — a
/// per-name scan would multiply the sweep (and the JSON file reads) by the candidate count.
/// </summary>
internal static class AssemblyNameEvidenceScanner
{
    private static readonly FrozenSet<string> PrunedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", "node_modules",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the subset of <paramref name="names"/> spelled inside any string literal
    /// in any document — including generated and external documents, which are legitimate
    /// evidence. Names shorter than 3 characters always count as spelled (too short to be
    /// distinctive). An assembly's OWN generated identity files (obj/&lt;AssemblyName&gt;.*.cs:
    /// AssemblyInfo, GlobalUsings, …) are excluded for that name only — they spell the name
    /// by definition and are self-reference, not external consumption.
    /// </summary>
    public static HashSet<string> FindSpelledNames(Solution solution, IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(names);

        var (found, sought) = SplitCandidates(names);
        if (sought.Count == 0)
        {
            return found;
        }

        var documents = solution.Projects
            .SelectMany(static p => p.Documents)
            .Where(static d => d.SupportsSyntaxTree);

        var sync = new object();
        var foundSought = 0;
        var allFound = 0;
        Parallel.ForEach(
            documents,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            (document, loopState) =>
            {
                if (Volatile.Read(ref allFound) == 1)
                {
                    loopState.Stop();
                    return;
                }

                var namesToCheck = NamesExcludingOwnIdentityDocument(document.FilePath, sought);
                if (namesToCheck.Count == 0)
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
                    if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
                    {
                        continue;
                    }

                    var text = literal.Token.ValueText;
                    foreach (var name in namesToCheck)
                    {
                        if (!text.Contains(name, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        lock (sync)
                        {
                            if (!found.Add(name))
                            {
                                continue;
                            }

                            foundSought++;
                            if (foundSought == sought.Count)
                            {
                                Interlocked.Exchange(ref allFound, 1);
                                loopState.Stop();
                            }
                        }

                        break;
                    }
                }
            });

        return found;
    }

    /// <summary>
    /// Returns the subset of <paramref name="names"/> spelled in any *.json file under the
    /// root directory (appsettings module lists, plugin manifests, deployment descriptors).
    /// Each file is read once regardless of candidate count.
    /// </summary>
    public static HashSet<string> FindSpelledNamesInJsonFiles(string rootDirectory, IReadOnlyCollection<string> names)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(names);

        var (found, sought) = SplitCandidates(names);
        if (sought.Count > 0)
        {
            ScanDirectory(rootDirectory, sought, found);
        }

        return found;
    }

    /// <summary>
    /// Names shorter than 3 characters always count as spelled (too short to be
    /// distinctive); the remainder become the sought set. Input is de-duplicated.
    /// </summary>
    private static (HashSet<string> Found, List<string> Sought) SplitCandidates(IReadOnlyCollection<string> names)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sought = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
            {
                continue;
            }

            if (name.Length < 3)
            {
                found.Add(name);
            }
            else
            {
                sought.Add(name);
            }
        }

        return (found, sought);
    }

    private static IReadOnlyList<string> NamesExcludingOwnIdentityDocument(string? filePath, List<string> sought)
    {
        if (filePath is null
            || !filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return sought;
        }

        var fileName = Path.GetFileName(filePath);
        List<string>? filtered = null;
        foreach (var name in sought)
        {
            // This document is the assembly's own generated identity file for `name`
            // ("<AssemblyName>.AssemblyInfo.cs" et al.) — not evidence for that name.
            if (fileName.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            filtered ??= [];
            filtered.Add(name);
        }

        return filtered ?? sought;
    }

    /// <summary>
    /// A snipper JSON report carries <c>"ruleId"</c> fields near the top of the
    /// file; ordinary configuration JSON (appsettings, manifests) does not.
    /// </summary>
    private static bool LooksLikeSnipperReport(string content)
    {
        const string marker = "\"ruleId\"";
        var probeLength = Math.Min(content.Length, 4096);
        return content.IndexOf(marker, 0, probeLength, StringComparison.Ordinal) >= 0;
    }

    private static void ScanDirectory(string directory, List<string> sought, HashSet<string> found)
    {
        IReadOnlyList<string> entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (found.Count == sought.Count)
            {
                return;
            }

            if (Directory.Exists(entry))
            {
                if (!PrunedDirectoryNames.Contains(Path.GetFileName(entry)))
                {
                    ScanDirectory(entry, sought, found);
                }

                continue;
            }

            if (!entry.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable file carries no evidence either way — keep scanning.
                continue;
            }

            // Snipper's own JSON reports spell candidate names in their findings.
            // A stale report in the analysis root must not feed name evidence —
            // twice-confirmed self-suppression (the 1.4.4 and 1.5.1 monorepo runs
            // each lost real SNP0023 findings to a previous report file). Reports
            // are identified by content, not name: output filenames are user-chosen.
            if (LooksLikeSnipperReport(content))
            {
                continue;
            }

            foreach (var name in sought)
            {
                if (content.Contains(name, StringComparison.Ordinal))
                {
                    found.Add(name);
                }
            }
        }
    }
}
