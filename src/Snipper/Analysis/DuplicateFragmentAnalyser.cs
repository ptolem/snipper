namespace Snipper.Analysis;

using System.Diagnostics;
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
/// Threshold rationale (measured, see docs/history/1_6_3_plan.md): W=60 clears the
/// generated/codegen shapes at every threshold tested, and lowering it adds
/// noise rather than signal - Program.cs still collided on 1,248 buckets with
/// its using lists stripped.
/// </summary>
public sealed class DuplicateFragmentAnalyser(
    AnalysisExclusions? exclusions = null,
    CloneDriftDetector? cloneDrift = null) : IWorkspaceAnalyser
{
    public IReadOnlyCollection<string> RuleIds { get; } = cloneDrift is null ? ["SNP0031"] : ["SNP0031", "SNP0032"];

    private const int WindowTokens = 60;
    private const int MinimumFragmentLines = 4;

    /// <summary>
    /// Hard ceiling on how many locations one shingle window may compare.
    ///
    /// This is the termination guarantee. Same-path pairs are skipped, so a repeated window
    /// contributes one pairing per cross-path combination, and the pair loop is quadratic in
    /// bucket size; capping the locations caps the work. Set well above any plausible real
    /// clone set, because a window occurring hundreds of times is boilerplate rather than a
    /// clone - the same shape as the Program.cs using-list collisions documented on
    /// <see cref="WindowTokens"/>. Windows reduced by this cap are reported through
    /// <see cref="Trace.TraceWarning(string, object?[])"/>, never silently.
    /// </summary>
    private const int MaxLocationsPerBucket = 512;

    private readonly AnalysisExclusions _exclusions = exclusions ?? AnalysisExclusions.None;

    /// <summary>
    /// Optional 4C drift pass. Null disables SNP0032 entirely, so the default cost and output of
    /// SNP0031 are unchanged.
    /// </summary>
    private readonly CloneDriftDetector? _cloneDrift = cloneDrift;

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
        var (fragments, matches, droppedWindows) = FindFragments(index.Windows, files, cancellationToken);

        if (droppedWindows > 0)
        {
            // Reported, not swallowed. Trace alone is not enough: the CLI installs no listener,
            // so a window dropped by the cap has to reach the console to count as "not silent".
            progress?.Invoke(
                $"DuplicateFragmentAnalyser: {droppedWindows} shingle window(s) exceeded the "
                + $"{MaxLocationsPerBucket}-location cap and were compared on a deterministic subset");
        }

        progress?.Invoke($"DuplicateFragmentAnalyser: grouping {fragments.Count} fragments");
        var (duplicateFindings, cloneSets) = BuildFindings(fragments, matches, files);
        var findings = duplicateFindings.ToList();

        // 4C runs over the sets this pass already proved, so enabling drift costs git lookups and
        // no second shingling. Degrades to nothing outside a git repository.
        if (_cloneDrift is { } detector && cloneSets.Count > 0)
        {
            progress?.Invoke($"DuplicateFragmentAnalyser: checking {cloneSets.Count} clone set(s) for one-sided fixes");
            findings.AddRange(detector.Detect(cloneSets));
        }

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
    private static (List<Fragment> Fragments, List<CloneMatch> Matches, int DroppedWindowCount) FindFragments(
        IReadOnlyDictionary<int, List<TokenShingleIndex.Location>> windows,
        IReadOnlyList<SourceFile> files,
        CancellationToken cancellationToken)
    {
        var byPath = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var matchIndex = new Dictionary<(string, string, int, int), CloneMatch>();
        var droppedWindowCount = 0;

        foreach (var bucket in windows.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (bucket.Count < 2)
            {
                continue;
            }

// Same-path pairs are skipped below, so a bucket holding one window 500 times in a
            // single file pairs 500 locations against every location in every other file, and
            // each surviving pair calls Extend, which walks tokens in both directions. The pass
            // is therefore O(K^2 * L) in the raw bucket size K: generated DTO/entity layers put
            // thousands of locations in a single bucket, and on a measured 182-file solution this
            // loop did not terminate in 20 minutes while every other analyser finished inside
            // 5 seconds.
            //
            // The fix is a cap, not a rewrite. Collapsing each bucket to one location per path
            // was tried first and is faster still, but it can lose clones: when one repeated
            // window seeds two distinct clone regions between the same pair of files, only the
            // earliest offset is kept and the second region is never offered to Extend. That
            // variant is not adopted. Buckets at or below the cap are compared exactly as
            // before, so the cap only changes behaviour where the old code did not finish.
            IReadOnlyList<TokenShingleIndex.Location> candidates;
            if (bucket.Count <= MaxLocationsPerBucket)
            {
                candidates = bucket;
            }
            else
            {
                // Deterministic order, so two runs on the same tree compare the same locations.
                droppedWindowCount++;
                candidates =
                [
                    .. bucket
                        .OrderBy(static location => location.Path, StringComparer.Ordinal)
                        .ThenBy(static location => location.TokenIndex)
                        .Take(MaxLocationsPerBucket)
                ];
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                for (var j = i + 1; j < candidates.Count; j++)
                {
                    // The only exit from this loop for a long pass. On the CLI path the token is
                    // CancellationToken.None and this is a no-op, but FindFragments is reachable
                    // with a real token and the inner loop has no other yield point.
                    cancellationToken.ThrowIfCancellationRequested();

                    var left = candidates[i];
                    var right = candidates[j];

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

        if (droppedWindowCount > 0)
        {
            Trace.TraceWarning(
                "DuplicateFragmentAnalyser: {0} shingle window(s) exceeded the {1}-location cap and were "
                + "compared on a deterministic subset. A window shared by more locations than this is "
                + "boilerplate rather than a clone; raise "
                + "DuplicateFragmentAnalyser.MaxLocationsPerBucket if a real clone is being missed.",
                droppedWindowCount,
                MaxLocationsPerBucket);
        }

        var (keptFragments, keptMatches) = KeepMaximalMatches(matchIndex.Values.ToList());
        return (keptFragments, keptMatches, droppedWindowCount);
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
    ///
    /// The window is verified first, and that verification is load-bearing rather
    /// than defensive. A bucket only says two windows hashed alike, and the key is
    /// FNV-1a/32: at a million windows on a real codebase the birthday bound puts
    /// collisions in the hundreds. The window is the very run a clone is made of,
    /// so it has to be proven before it can seed anything. Previously the forward
    /// scan simply began at <see cref="WindowTokens"/>, leaving offsets [0,60)
    /// uncompared - so a collision produced a 60-token fragment between unrelated
    /// files, and the caller's <c>length &lt; WindowTokens</c> check could never
    /// fire because the returned length was always at least the window size.
    /// </summary>
    internal static (int LeftStart, int RightStart, int Length) Extend(
        IReadOnlyList<string> left,
        int leftStart,
        IReadOnlyList<string> right,
        int rightStart)
    {
        // The index guarantees this on the CLI path, but Extend is reachable directly
        // and must not read past either end to satisfy a caller it does not own.
        if (leftStart < 0
            || rightStart < 0
            || leftStart + WindowTokens > left.Count
            || rightStart + WindowTokens > right.Count)
        {
            return (leftStart, rightStart, 0);
        }

        for (var offset = 0; offset < WindowTokens; offset++)
        {
            if (!string.Equals(left[leftStart + offset], right[rightStart + offset], StringComparison.Ordinal))
            {
                return (leftStart, rightStart, 0);
            }
        }

        // Verified, so the window is earned rather than assumed.
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
    /// <summary>
    /// SNP0031 findings, plus the clone sets that produced them.
    ///
    /// The sets are returned rather than recomputed so 4C can run drift detection over exactly
    /// the sets this rule reported — one shingling pass, two rules — and so the two can never
    /// disagree about what counts as a clone.
    /// </summary>
    private (IReadOnlyList<SnipperFinding> Findings, IReadOnlyList<CloneSet> Sets) BuildFindings(
        List<Fragment> fragments,
        List<CloneMatch> matches,
        List<SourceFile> files)
    {
        if (fragments.Count == 0)
        {
            return ([], []);
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
        var cloneSets = new List<CloneSet>();

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

            var reported = new List<CloneSetMember>();

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

                reported.Add(new CloneSetMember(
                    member.Path, member.StartLine, member.StartCharacter, member.EndLine));

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

            // A single surviving copy is not a set 4C can reason about: there is no sibling to
            // have drifted from.
            if (reported.Count >= 2)
            {
                cloneSets.Add(new CloneSet(reported));
            }
        }

        return (findings, cloneSets);
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

        // File-scope fragments report no name, which Covers resolves against the shared
        // <global> sentinel — the same marker every other namespace-aware rule uses.
        return exclusions.Covers(name);
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
