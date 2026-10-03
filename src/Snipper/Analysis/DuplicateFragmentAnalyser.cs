namespace Snipper.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snipper.Models;

/// <summary>
/// SNP0031 - duplicate code fragments. Reports code copied across files (and
/// across projects) as Type-1 exact and Type-2 rename-only clones, detected
/// from syntax alone: no semantic model is bound, so this pass adds no binding
/// time to the run.
///
/// Method: each file becomes a normalized token stream, fixed-width windows are
/// hashed into a shared bucket table, colliding windows are extended into
/// maximal runs, and runs that overlap form clone sets. One Advisory finding is
/// emitted per occurrence.
///
/// Threshold rationale (measured, see docs/plan_1_6_3.md): W=60 clears the
/// generated/codegen shapes at every threshold tested, and lowering it adds
/// noise rather than signal - Program.cs still collided on 1,248 buckets with
/// its using lists stripped.
/// </summary>
public sealed class DuplicateFragmentAnalyser(AnalysisExclusions? exclusions = null) : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = ["SNP0031"];

    private const int WindowTokens = 60;
    private const int MinimumFragmentLines = 4;
    private const string GlobalNamespaceMarker = "<global>";

    private readonly AnalysisExclusions _exclusions = exclusions ?? AnalysisExclusions.None;

    /// <summary>One maximal duplicated run inside a single file.</summary>
    private sealed record Fragment(
        string Path,
        int TokenIndex,
        int TokenCount,
        int StartLine,
        int StartCharacter,
        int EndLine);

    /// <summary>
    /// A proven duplication between two files. The pair is path-ordered so the
    /// unordered file pair has a single identity.
    /// </summary>
    private sealed record CloneMatch(Fragment Left, Fragment Right, int Length);

    private sealed record SourceFile(
        string Path,
        CompilationUnitSyntax Unit,
        IReadOnlyList<string> Tokens,
        IReadOnlyList<(int Line, int Character)> Lines);

    public async Task<IReadOnlyList<SnipperFinding>> AnalyzeAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(solution);

        var files = await CollectFiles(solution, cancellationToken).ConfigureAwait(false);

        if (files.Count < 2)
        {
            return [];
        }

        progress?.Invoke($"DuplicateFragmentAnalyser: shingling {files.Count} files");
        var index = TokenShingleIndex.Build(
            files.Select(f => (f.Path, Root: (SyntaxNode)f.Unit)),
            WindowTokens,
            cancellationToken);

        progress?.Invoke("DuplicateFragmentAnalyser: extending matches");
        var (fragments, matches) = FindFragments(index.Windows, files, cancellationToken);

        progress?.Invoke($"DuplicateFragmentAnalyser: grouping {fragments.Count} fragments");
        var findings = BuildFindings(fragments, matches, files);

        // Deterministic ordering is a hard requirement (plan guiding principle
        // 5): clone sets are discovered through hash buckets, so without this
        // the report order would depend on scheduling.
        return findings
            .OrderBy(f => f.FilePath, StringComparer.Ordinal)
            .ThenBy(f => f.LineNumber)
            .ThenBy(f => f.CharacterOffset)
            .ThenBy(f => f.Message, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<List<SourceFile>> CollectFiles(Solution solution, CancellationToken cancellationToken)
    {
        var analysisRoots = ExclusionEngine.GetAnalysisRootDirectories(solution);
        var byPath = new Dictionary<string, SourceFile>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!document.SupportsSyntaxTree || document.FilePath is not { Length: > 0 } path)
                {
                    continue;
                }

                // Path-keyed collection: a linked file or a multi-TFM document
                // contributes one copy of its content (decision 5).
                if (byPath.ContainsKey(path))
                {
                    continue;
                }

                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                if (root is null || ExclusionEngine.ShouldSkipDocument(path, root, analysisRoots))
                {
                    continue;
                }

                var unit = (CompilationUnitSyntax)root;
                byPath[path] = new SourceFile(
                    path,
                    unit,
                    TokenShingleIndex.Tokenize(unit),
                    TokenShingleIndex.TokenLines(unit));
            }
        }

        return byPath.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Extend every multi-location window bucket into maximal token runs, then
    /// keep one maximal fragment per (path, tokenIndex) so a long clone is
    /// reported once rather than once per window that straddles it.
    /// </summary>
    private static (List<Fragment> Fragments, List<CloneMatch> Matches) FindFragments(
        IReadOnlyDictionary<int, List<TokenShingleIndex.Location>> windows,
        IReadOnlyList<SourceFile> files,
        CancellationToken cancellationToken)
    {
        var byPath = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var matchIndex = new Dictionary<(string, string, int, int), CloneMatch>();

        foreach (var bucket in windows.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (bucket.Count < 2)
            {
                continue;
            }

            for (var i = 0; i < bucket.Count; i++)
            {
                for (var j = i + 1; j < bucket.Count; j++)
                {
                    var left = bucket[i];
                    var right = bucket[j];

                    if (string.Equals(left.Path, right.Path, StringComparison.Ordinal))
                    {
                        // One copy per path by design (decision 5).
                        continue;
                    }

                    if (!byPath.TryGetValue(left.Path, out var leftFile)
                        || !byPath.TryGetValue(right.Path, out var rightFile))
                    {
                        continue;
                    }

                    var (leftStart, rightStart, length) = Extend(
                        leftFile.Tokens, left.TokenIndex, rightFile.Tokens, right.TokenIndex);
                    if (length < WindowTokens)
                    {
                        continue;
                    }

                    Record(matchIndex, CreateMatch(
                        CreateFragment(leftFile, leftStart, length),
                        CreateFragment(rightFile, rightStart, length)));
                }
            }
        }

        return KeepMaximalMatches(matchIndex.Values.ToList());
    }

    /// <summary>
    /// Collapse matches down to their maximal ones, returning both the surviving
    /// fragments and the match edges that produced them.
    ///
    /// Extension walks backwards one token at a time, so a single 200-token
    /// clone between two files arrives as ~140 nested matches that differ only
    /// by start offset; without this collapse one clone is reported ~140 times.
    ///
    /// Runs as one ordered sweep per file pair rather than comparing every match
    /// against every other: within a pair the maximal span is found by taking
    /// the longest match first and keeping only matches whose right-hand end
    /// passes the furthest end seen so far. Scoping to a file pair means an
    /// unrelated clone between other files is never absorbed. The sweep is what
    /// keeps this linear; an all-pairs subsumption test is quadratic and does
    /// not finish on a 3,000-file solution.
    /// </summary>
    private static (List<Fragment> Fragments, List<CloneMatch> Matches) KeepMaximalMatches(List<CloneMatch> matches)
    {
        var keptMatches = new List<CloneMatch>();

        foreach (var pair in matches.GroupBy(m => (m.Left.Path, m.Right.Path)))
        {
            var furthestRightEnd = int.MinValue;

            foreach (var match in pair.OrderByDescending(m => m.Length).ThenBy(m => m.Left.TokenIndex))
            {
                var rightEnd = match.Right.TokenIndex + match.Length;
                if (rightEnd <= furthestRightEnd)
                {
                    continue;
                }

                furthestRightEnd = rightEnd;
                keptMatches.Add(match);
            }
        }

        var keptFragments = new List<Fragment>();
        foreach (var match in keptMatches)
        {
            keptFragments.Add(match.Left);
            keptFragments.Add(match.Right);
        }

        var qualifying = keptFragments
            .Where(f => (f.EndLine - f.StartLine + 1) >= MinimumFragmentLines)
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ThenBy(f => f.TokenIndex)
            .ToList();

        // Edges whose fragments did not both qualify would strand a fragment in
        // no set, so drop them too.
        var qualifyingSet = qualifying.ToHashSet();
        var qualifyingMatches = keptMatches
            .Where(m => qualifyingSet.Contains(m.Left) && qualifyingSet.Contains(m.Right))
            .ToList();

        return (qualifying, qualifyingMatches);
    }

    private static CloneMatch CreateMatch(Fragment left, Fragment right)
    {
        // Order the pair by path so the unordered pair has one identity.
        return string.CompareOrdinal(left.Path, right.Path) <= 0
            ? new CloneMatch(left, right, left.TokenCount)
            : new CloneMatch(right, left, right.TokenCount);
    }

    /// <summary>
    /// Extend the matching run around a window start: forward from the window,
    /// then backward over the region the window already covered. The two sides
    /// back up by different amounts, so each start offset is returned
    /// separately - sharing one index would misplace every fragment whose two
    /// sides are not aligned.
    /// </summary>
    private static (int LeftStart, int RightStart, int Length) Extend(
        IReadOnlyList<string> left,
        int leftStart,
        IReadOnlyList<string> right,
        int rightStart)
    {
        var forward = WindowTokens;
        while (leftStart + forward < left.Count
            && rightStart + forward < right.Count
            && string.Equals(left[leftStart + forward], right[rightStart + forward], StringComparison.Ordinal))
        {
            forward++;
        }

        var leftBackward = 0;
        while (leftStart - leftBackward - 1 >= 0
            && rightStart - leftBackward - 1 >= 0
            && leftBackward + 1 < WindowTokens
            && string.Equals(left[leftStart - leftBackward - 1], right[rightStart - leftBackward - 1], StringComparison.Ordinal))
        {
            leftBackward++;
        }

        // The right side may extend further back than the left; find its own
        // limit rather than assuming symmetry.
        var rightBackward = leftBackward;
        while (rightStart - rightBackward - 1 >= 0
            && leftStart - rightBackward - 1 >= 0
            && rightBackward + 1 < WindowTokens
            && string.Equals(left[leftStart - rightBackward - 1], right[rightStart - rightBackward - 1], StringComparison.Ordinal))
        {
            rightBackward++;
        }

        return (leftStart - leftBackward, rightStart - rightBackward, forward + Math.Max(leftBackward, rightBackward));
    }

    private static Fragment CreateFragment(SourceFile file, int start, int length)
    {
        var first = file.Lines[start];
        var lastIndex = Math.Min(start + length - 1, file.Lines.Count - 1);
        var last = file.Lines[lastIndex];

        return new Fragment(file.Path, start, length, first.Line, first.Character, last.Line);
    }

    private static void Record(Dictionary<(string, string, int, int), CloneMatch> matches, CloneMatch match)
    {
        // Hash-deduplicated: a linear scan here is quadratic in the number of
        // matches, which on a 3,000-file solution means tens of millions of
        // string comparisons per bucket.
        var key = (match.Left.Path, match.Right.Path, match.Left.TokenIndex, match.Right.TokenIndex);
        if (!matches.ContainsKey(key))
        {
            matches[key] = match;
        }
    }

    /// <summary>
    /// Group fragments into clone sets using the matches actually proven during
    /// extension, transitively closed by union-find. Grouping on the proven
    /// edges - rather than on equal token offsets - is what lets two copies of
    /// the same code that sit at different offsets in differently sized files
    /// still form one set.
    /// </summary>
    private IReadOnlyList<SnipperFinding> BuildFindings(
        List<Fragment> fragments,
        List<CloneMatch> matches,
        List<SourceFile> files)
    {
        if (fragments.Count == 0)
        {
            return [];
        }

        var byPath = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var parent = new int[fragments.Count];
        for (var index = 0; index < parent.Length; index++)
        {
            parent[index] = index;
        }

        var indexByFragment = new Dictionary<Fragment, int>();
        for (var index = 0; index < fragments.Count; index++)
        {
            indexByFragment[fragments[index]] = index;
        }

        foreach (var match in matches)
        {
            if (indexByFragment.TryGetValue(match.Left, out var leftIndex)
                && indexByFragment.TryGetValue(match.Right, out var rightIndex))
            {
                Union(parent, leftIndex, rightIndex);
            }
        }

        var sets = fragments
            .Select((fragment, index) => (fragment, Root: Find(parent, index)))
            .GroupBy(entry => entry.Root)
            .Select(group => group.Select(e => e.fragment)
                .Distinct()
                .OrderBy(f => f.Path, StringComparer.Ordinal)
                .ThenBy(f => f.TokenIndex)
                .ToList())
            .Where(members => members.Select(m => m.Path).Distinct(StringComparer.Ordinal).Count() >= 2)
            .ToList();

        var findings = new List<SnipperFinding>();

        foreach (var members in sets)
        {
            // Structural guard: a clone set confined to one directory (and
            // therefore one project in a normal layout) is the signature of a
            // locally repeated skeleton - every workspace analyser in a codebase
            // shares one project/document loop. Measured on Snipper.slnx, this
            // removes 518 of 1160 findings, all of them same-directory pairs
            // between sibling analysers, without touching any cross-project set.
            if (!SpansDistinctLocations(members))
            {
                continue;
            }

            for (var index = 0; index < members.Count; index++)
            {
                var member = members[index];
                if (!byPath.TryGetValue(member.Path, out var file))
                {
                    continue;
                }

                if (IsExcluded(file.Unit, member, _exclusions))
                {
                    continue;
                }

                var other = members[(index + 1) % members.Count];
                var lines = member.EndLine - member.StartLine + 1;

                findings.Add(new SnipperFinding(
                    RuleId: "SNP0031",
                    Title: "Duplicate Code Fragment",
                    Message: $"{member.TokenCount}-token fragment ({lines} lines) duplicated {members.Count} time(s); first other occurrence at {other.Path}:{other.StartLine}.",
                    Certainty: CertaintyTier.Advisory,
                    Category: FindingCategory.DuplicateFragment,
                    FilePath: member.Path,
                    LineNumber: member.StartLine,
                    CharacterOffset: member.StartCharacter,
                    Symbol: null));
            }
        }

        return findings;
    }

    /// <summary>
    /// True when a clone set reaches beyond one containing directory. Two files
    /// in the same folder are usually siblings of one design; a copy that
    /// crosses a directory (and in a normal layout, a project) is the case the
    /// rule exists to surface.
    /// </summary>
    private static bool SpansDistinctLocations(IReadOnlyList<Fragment> members)
    {
        string? directory = null;

        foreach (var member in members)
        {
            var current = Path.GetDirectoryName(member.Path);
            if (directory is null)
            {
                directory = current;
                continue;
            }

            if (!string.Equals(directory, current, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A duplicate fragment carries no symbol, so namespace exclusion resolves
    /// syntactically through the enclosing namespace declaration, falling back
    /// to the global-namespace marker for file-scope code. Binding a semantic
    /// model here would defeat this rule's syntax-only design, so the resolution
    /// is deliberately syntactic rather than via
    /// <see cref="ExclusionEngine.IsNamespaceExcluded(SyntaxNode, SemanticModel, AnalysisExclusions, CancellationToken)"/>.
    /// </summary>
    private static bool IsExcluded(CompilationUnitSyntax unit, Fragment fragment, AnalysisExclusions exclusions)
    {
        if (exclusions.Namespaces.Count == 0)
        {
            return false;
        }

        var tokens = unit.DescendantTokens().ToList();
        if (tokens.Count == 0)
        {
            return false;
        }

        var ordinal = Math.Min(fragment.TokenIndex, tokens.Count - 1);
        var name = tokens[ordinal].Parent?
            .FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>()?.Name.ToString();

        if (string.IsNullOrEmpty(name))
        {
            // File-scope code: no enclosing namespace declaration.
            return exclusions.Namespaces.Contains(GlobalNamespaceMarker);
        }

        var candidate = name;
        while (candidate.Length > 0)
        {
            if (exclusions.Namespaces.Contains(candidate))
            {
                return true;
            }

            var lastDot = candidate.LastIndexOf('.');
            if (lastDot < 0)
            {
                break;
            }

            candidate = candidate[..lastDot];
        }

        return false;
    }

    private static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
        {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }

        return index;
    }

    private static void Union(int[] parent, int left, int right)
    {
        var leftRoot = Find(parent, left);
        var rightRoot = Find(parent, right);
        if (leftRoot != rightRoot)
        {
            parent[rightRoot] = leftRoot;
        }
    }
}