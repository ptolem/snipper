namespace Snipper.Analysis;

using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using Snipper.Models;

/// <summary>Certainty of a clone-drift finding.</summary>
internal enum CloneDriftTier : byte
{
    /// <summary>A one-sided change that reads as a defensive fix.</summary>
    High = 1,

    /// <summary>A one-sided change that does not read as a fix: drift worth looking at.</summary>
    Advisory = 2,
}

/// <summary>
/// The 4C decision, isolated from git and from analysis so it can be tested directly.
///
/// Two facts carry a finding, and both must hold:
///
/// - **HEAD proves the copies are meant to be in sync.** SNP0031 proved token-identity over the
///   region, so divergence is a departure from a demonstrated invariant rather than a guess.
/// - **The patch proves they were out of sync at commit C.** C changed one copy's region and no
///   sibling. That is what makes it *temporal* rather than merely "these two files differ".
///
/// <c>competitive-analysis.md</c> §6.1 additionally requires that "the other copies are
/// byte-identical to the pre-change text". That clause was written for a present-tense framing
/// which cannot exist — a copy that received a fix stops being a clone and leaves the set — so
/// HEAD-identity stands in for it, and is the stronger of the two: it is a proof over the whole
/// region rather than a single commit's parent.
/// </summary>
internal static class CloneDriftClassifier
{
    /// <summary>
    /// Tier for a one-sided change, or null when the change was not drift at all.
    /// </summary>
    /// <param name="oneSided">Whether the commit changed this copy and no sibling.</param>
    /// <param name="fixShaped">Whether the added lines read as a defensive fix.</param>
    /// <param name="renameOnly">
    /// Whether the change only renamed identifiers. A rename is drift worth reporting but not a
    /// defensive fix, so it is capped at <see cref="CloneDriftTier.Advisory"/> however fix-shaped
    /// its text looks.
    /// </param>
    public static CloneDriftTier? Classify(bool oneSided, bool fixShaped, bool renameOnly = false)
    {
        if (!oneSided)
        {
            return null;
        }

        return fixShaped && !renameOnly ? CloneDriftTier.High : CloneDriftTier.Advisory;
    }

    /// <summary>
    /// Whether a hunk falls inside a clone region, tolerating one line of adjacency.
    ///
    /// The canonical drift shape is a guard inserted immediately above a copied block, and git
    /// reports that as touching the line *before* the region. Requiring strict containment would
    /// miss precisely the case the rule exists for.
    /// </summary>
    public static bool HunkTouchesRegion(PatchHunk hunk, int regionStart, int regionEnd)
    {
        ArgumentNullException.ThrowIfNull(hunk);

        var hunkStart = hunk.NewCount == 0 ? hunk.NewStart : hunk.NewStart;
        var hunkEnd = hunkStart + Math.Max(hunk.NewCount, 1) - 1;

        return hunkEnd >= regionStart - 1 && hunkStart <= regionEnd + 1;
    }

    /// <summary>
    /// The line a finding should point at: where the hunk actually changed something, clamped into
    /// the clone region.
    /// <para>
    /// Anchoring at <c>regionStart</c> was wrong. A region starts wherever the duplicated run
    /// happens to begin, which for a maximal run is frequently a stray <c>;</c> or a lone <c>{</c>
    /// on line 1, so the location pointed nowhere. The hunk's <c>NewStart</c> is in HEAD
    /// coordinates because the detector only ever considers the most recent commit touching a copy
    /// (see the class note), so it is the same coordinate system as the region. Clamping covers the
    /// adjacency that <see cref="HunkTouchesRegion"/> deliberately tolerates.
    /// </para>
    /// </summary>
    public static int AnchorLine(PatchHunk hunk, int regionStart, int regionEnd)
    {
        ArgumentNullException.ThrowIfNull(hunk);
        ArgumentOutOfRangeException.ThrowIfLessThan(regionEnd, regionStart);

        // Anchor on a line the change actually touched.
        //
        // NewStart alone was wrong: git points it at the hunk's first line, which it fills with
        // unchanged context, so a finding could land on a blank line or a lone brace. Arithmetic on
        // added-line indexes is also wrong - PatchHunk keeps no context lines, so an index cannot
        // be mapped back to a file line. PatchHunk records the post-image line of every line the
        // hunk changed, so pick the first one that lands inside the region.
        //
        // Preferring an in-region line matters: HunkTouchesRegion deliberately tolerates a hunk that
        // merely abuts the region, and clamping such a hunk's line forward to regionStart put the
        // finding on whatever happened to be the region's first line - which on the gate-5 target was
        // an opening brace the commit never touched.
        var changed = hunk.ChangedNewLines;
        if (changed is { Length: > 0 })
        {
            foreach (var line in changed)
            {
                if (line >= regionStart && line <= regionEnd)
                {
                    return line;
                }
            }

            // The hunk overlaps or abuts the region but none of its changes land inside it, so no
            // line of the region was actually modified. Anchor on the changed line nearest the
            // region: it sits just outside the region but is a real edit, which is more useful than
            // a line inside the region that the commit never touched.
            var nearest = changed[0];
            var nearestDistance = Math.Abs(nearest - regionStart);

            foreach (var line in changed)
            {
                var distance = Math.Abs(line - regionStart);
                if (distance >= nearestDistance)
                {
                    continue;
                }

                nearest = line;
                nearestDistance = distance;
            }

            return nearest;
        }

        // No changed line recorded, so fall back to the hunk's own start, clamped into the region.
        return Math.Clamp(hunk.NewStart, regionStart, regionEnd);
    }

    /// <summary>
    /// Whether a patch line carries code rather than being blank or bare punctuation. Letters,
    /// digits and underscore count; braces, semicolons, dots, commas and quotes do not.
    /// </summary>
    internal static bool CarriesCode(string line)
    {
        foreach (var character in line)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sibling references as repository-relative <c>path:line</c>, ordinally ordered.
    /// <para>
    /// Not a bare filename. The finding's own <c>filePath</c> is absolute, so a bare sibling name was
    /// the only unqualified reference in the message - and in a monorepo it is not unique: the
    /// gate-5 target holds two <c>GetStoresQuery.cs</c>, so <c>GetStoresQuery.cs:23</c> named two
    /// different files. A High finding has to be self-locating.
    /// </para>
    /// </summary>
    public static string DescribeSiblings(IEnumerable<(string RelativePath, int StartLine)> siblings)
    {
        ArgumentNullException.ThrowIfNull(siblings);

        return string.Join(
            ", ",
            siblings
                .Select(sibling => $"{sibling.RelativePath}:{sibling.StartLine}")
                .OrderBy(text => text, StringComparer.Ordinal));
    }

    /// <summary>
    /// Which sibling was brought in line later, and by which commit.
    /// <para>
    /// Each entry names its own sibling. Reporting bare SHAs made two siblings fixed by one commit
    /// read as the same fact twice - "92a2b30 on 2025-05-14; 92a2b30 on 2025-05-14" - which was
    /// indistinguishable from a bug attributing a single commit to two files.
    /// </para>
    /// </summary>
    public static string DescribeCatchUps(IEnumerable<(string Path, string Sha, DateTimeOffset Date)> catchUps)
    {
        ArgumentNullException.ThrowIfNull(catchUps);

        return string.Join(
            "; ",
            catchUps
                .OrderBy(entry => entry.Path, StringComparer.Ordinal)
                .Select(entry => $"{entry.Path} was fixed by {entry.Sha[..7]} on {entry.Date:yyyy-MM-dd}"));
    }
}

/// <summary>
/// Whether a hunk's change is purely a rename - identifiers changed, structure did not.
/// <para>
/// A rename in a clone set is real drift and stays reportable, but it is not a <em>defensive fix</em>.
/// Calling it one inflated a cosmetic <c>_lastOrderId</c> rename to <c>High</c> during gate 5
/// validation, and a High tier that fires on renames stops being read as a High tier.
/// </para>
/// <para>
/// Compared as skeletons: the added and removed text with every identifier character removed.
/// A rename leaves the punctuation, keywords-as-characters and spacing intact, so the skeletons
/// match; adding a null guard does not. This is a heuristic on patch text rather than on a syntax
/// tree, and deliberately so - it errs toward calling a change structural, which costs a High
/// finding on an exotic rename but never invents one.
/// </para>
/// </summary>
internal static class RenameDetector
{
    /// <summary>
    /// Whether the change only renamed things. Requires text on both sides: a pure addition or a
    /// pure deletion cannot be a rename, and an empty skeleton means there was nothing structural
    /// to compare, so neither is treated as one.
    /// </summary>
    public static bool IsRenameOnly(IReadOnlyList<string> addedLines, IReadOnlyList<string> removedLines)
    {
        ArgumentNullException.ThrowIfNull(addedLines);
        ArgumentNullException.ThrowIfNull(removedLines);

        if (addedLines.Count == 0 || removedLines.Count == 0)
        {
            return false;
        }

        var before = Skeleton(removedLines);
        var after = Skeleton(addedLines);

        return before.Length > 0
            && string.Equals(before, after, StringComparison.Ordinal);
    }

    /// <summary>Everything that is not an identifier character, whitespace-collapsed.</summary>
    private static string Skeleton(IEnumerable<string> lines)
    {
        var builder = new StringBuilder();

        foreach (var line in lines)
        {
            foreach (var character in line)
            {
                if (!char.IsLetterOrDigit(character) && character != '_')
                {
                    builder.Append(character);
                }
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }
}

/// <summary>
/// Recognises a defensive fix in a change's added lines, per <c>competitive-analysis.md</c> 6.1's
/// null/guard/try/catch/exception/bounds list.
///
/// Deliberately conservative. A single marker promotes a change to fix-shaped; a looser rule would
/// manufacture High findings, and a drift rule that cries wolf is a drift rule that gets switched
/// off. Kept separate from <see cref="GitHistory"/> because this is a judgement about code, not
/// about a patch.
/// </summary>
internal static class DefensiveFixMarkers
{
    /// <summary>
    /// Shape markers: null-defence as *structure* rather than as a word.
    /// <para>
    /// The 1.7.1 sweep matched bare <c>null</c>, <c>Length</c> and <c>Count</c>, and every one of
    /// those reached <c>High</c> on the reference monorepo as something that is not a guard:
    /// <c>string? url = null;</c> (an initialiser), a property declared <c>public int Count</c>, a
    /// FluentAssertions <c>actualLineItems.Count.Should().Be(1)</c>, and a metric whose name is
    /// <c>"…-requests.count"</c>. A guard needs a comparison or a fallback, so the set is shapes.
    /// <c>!= null</c> and <c>== null</c> are in it because they are the commonest null guards in C#
    /// and dropping them - as the 1.7.1 proposal did - demotes a real one-sided fix.
    /// </para>
    /// </summary>
    private static readonly FrozenSet<string> ShapeMarkers = new[]
    {
        "== null", "!= null", "is null", "is not null", "?.", "??",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The <c>ThrowIf*</c> family, which needs no comparison to be a guard.</summary>
    private static readonly FrozenSet<string> GuardThrows = new[]
    {
        "ThrowIfNull", "ThrowIfEmpty", "ThrowIfNegative", "ThrowIfZero", "ThrowIfNegativeOrZero",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Bounds names, counted only when a comparison operator follows them.
    /// <para>
    /// <c>list.Count &gt; 0</c> is a bounds guard and has always been reported as one. What the
    /// 1.7.1 sweep added by accident was every other line containing <c>Count</c> or <c>Length</c>:
    /// a property declared <c>public int Count</c>, a FluentAssertions
    /// <c>actualLineItems.Count.Should().Be(1)</c>, and a metric named <c>"…-requests.count"</c>.
    /// Requiring the name to be followed by a comparison separates the two without weakening the
    /// rule that was always meant to be there - the bounds check is in
    /// <c>competitive-analysis.md</c> 6.1's marker list.
    /// </para>
    /// </summary>
    private static readonly FrozenSet<string> BoundsNames = new[]
    {
        "Count", "Length",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Comparison operators, which are what turn a bounds name into a bounds <em>check</em>. A
    /// bare identifier is a property or an argument, not a guard.
    /// </summary>
    private static readonly string[] ComparisonOperators =
    [
        "==", "!=", ">=", "<=", ">", "<",
    ];

    /// <summary>
    /// Null-checking helpers, matched in invocation form only. <c>public static bool
    /// IsNullOrEmpty&lt;T&gt;(…)</c> declares such a method and matched its own name, which put a
    /// High on a commit whose entire diff was that one signature; requiring the open paren
    /// immediately after the name excludes the declaration, because the type parameters sit
    /// between. The remaining ambiguity - a *call* to such a helper that guards nothing - is
    /// settled by <see cref="HasConsumingGuardCall"/>.
    /// </summary>
    private static readonly FrozenSet<string> GuardCalls = new[]
    {
        "IsNullOrEmpty", "IsNullOrWhiteSpace",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Statement keywords, matched only at statement position. Substring matching let <c>try</c>
    /// fire on <c>Country</c> and on a 1.5 KB JSON fixture's test name; anchoring to the start of
    /// the line keeps <c>throw new …</c> and drops <c>trying</c>.
    /// </summary>
    private static readonly FrozenSet<string> StatementKeywords = new[]
    {
        "try", "catch", "finally", "throw",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Tokens that put what follows them in a consuming position - a condition, a return, a
    /// propagation. A guard call outside one of these is incidental to the line.
    /// </summary>
    private static readonly string[] ConsumingTokens =
    [
        "&&", "||", "=>", "==", "!=", "?", ":", "!", "return",
    ];

    /// <summary>
    /// Tokens that establish the <em>whole line</em> as a consumed expression, so a guard call
    /// anywhere after one of them counts however much syntax sits between.
    /// <para>
    /// Needed because the commonest shape is <c>=&gt; !string.IsNullOrWhiteSpace(x) ? …</c>: the
    /// negation and the member access both land between the arrow and the call, so a
    /// trailing-token test alone misses the guard and demotes a real one-sided fix to Advisory.
    /// </para>
    /// </summary>
    private static readonly string[] LeadingConsumingTokens =
    [
        "=>", "return",
    ];

    /// <summary>
    /// Below this length, added and removed text overlap by accident rather than by relocation.
    /// <c>}</c>, <c>);</c> and <c>{</c> are substrings of nearly any hunk, and treating them as
    /// evidence would make every hunk look relocated.
    /// </summary>
    private const int MinimumRelocatableLength = 12;

    /// <summary>
    /// Whether the added lines read as a defensive fix. A pure deletion carries no added text and
    /// is never fix-shaped - calling that a fix would be backwards.
    /// <para>
    /// Three things are checked per line, and the order matters. Comments and string/char literals
    /// come off first, because a marker inside prose or a JSON fixture is not code: the 1.7.1 sweep
    /// put a High on <c>/// … (null = tag omitted)</c> and another on a serialised order payload.
    /// Then the line is compared against the removed side, so a marker that was <em>already there</em>
    /// cannot be this commit's contribution. Only then is shape matched.
    /// </para>
    /// <para>
    /// The removed-side test is what catches relocation, and it is deliberately per-line rather than
    /// per-hunk. Git reports a re-indent or a relocated block as removed-then-added, so
    /// <c>if (checkpoint != null) return checkpoint;</c> split across two lines reads as a new
    /// guard. A whole-hunk line-set comparison misses that, because the one removed line is not
    /// among the two added ones; testing whether the added text was already present catches it, and
    /// still lets a hunk that reformats <em>and</em> genuinely fixes report the real fix.
    /// </para>
    /// </summary>
    public static bool IsFixShaped(IReadOnlyList<string> addedLines, IReadOnlyList<string> removedLines)
    {
        ArgumentNullException.ThrowIfNull(addedLines);
        ArgumentNullException.ThrowIfNull(removedLines);

        // Carried across lines so a verbatim string opened on one line cannot leak its body into
        // the next as if it were code.
        var insideString = false;

        foreach (var line in addedLines)
        {
            var code = StripCommentsAndLiterals(line, ref insideString);

            // Only lines that actually carry code are considered. A hunk that adds nothing but
            // whitespace, braces or semicolons changed no behaviour, so calling it a defensive fix
            // was both wrong and unanchored: 4 High findings landed on a blank line because the hunk
            // behind them had no code to point at. That is drift, and is reported as drift.
            if (!CloneDriftClassifier.CarriesCode(code) || PreExisted(code, removedLines))
            {
                continue;
            }

            if (IsFixShape(code))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether any marker shape appears in already-stripped code.</summary>
    private static bool IsFixShape(string code)
    {
        // Case-insensitive throughout, which is the contract this method had before F5 and is why
        // `IF (VALUE IS NULL)` still counts. Symbol markers are unaffected by casing.
        foreach (var shape in ShapeMarkers)
        {
            if (code.Contains(shape, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var guard in GuardThrows)
        {
            if (code.Contains(guard + "(", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var name in BoundsNames)
        {
            if (HasBoundsComparison(code, name))
            {
                return true;
            }
        }

        foreach (var call in GuardCalls)
        {
            if (HasConsumingGuardCall(code, call))
            {
                return true;
            }
        }

        foreach (var keyword in StatementKeywords)
        {
            if (StartsStatement(code, keyword))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a bounds name appears on identifier boundaries with a comparison operator after it.
    /// </summary>
    private static bool HasBoundsComparison(string code, string name)
    {
        var index = code.IndexOf(name, StringComparison.Ordinal);

        while (index >= 0)
        {
            var before = index == 0 ? '\0' : code[index - 1];
            var afterIndex = index + name.Length;
            var after = afterIndex >= code.Length ? '\0' : code[afterIndex];

            if (!IsIdentifierCharacter(before) && !IsIdentifierCharacter(after))
            {
                var rest = code[afterIndex..].TrimStart();

                foreach (var comparison in ComparisonOperators)
                {
                    if (rest.StartsWith(comparison, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            index = code.IndexOf(name, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    /// Whether this marker text was already on the removed side of the hunk.
    /// <para>
    /// A one-sided *fix* adds something the sibling does not have. When the added text is already
    /// contained in - or already contains - a removed line, the commit rearranged existing code and
    /// introduced nothing, which is the case that must not reach <c>High</c>. This errs toward
    /// demoting real drift to <c>Advisory</c>, which loses a tier rather than inventing one; F5 is
    /// a precision fix and that is the direction it should fail.
    /// </para>
    /// </summary>
    private static bool PreExisted(string code, IReadOnlyList<string> removedLines)
    {
        if (removedLines.Count == 0)
        {
            // A pure insertion has no pre-image, so nothing can have pre-existed.
            return false;
        }

        var added = code.Trim();

        if (added.Length < MinimumRelocatableLength)
        {
            return false;
        }

        foreach (var removed in removedLines)
        {
            var before = removed.Trim();

            if (before.Length < MinimumRelocatableLength)
            {
                continue;
            }

            if (before.Contains(added, StringComparison.Ordinal)
                || added.Contains(before, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a null-checking helper is called somewhere that consumes its result - a condition, a
    /// return, a propagation - rather than merely appearing in the line.
    /// </summary>
    private static bool HasConsumingGuardCall(string code, string name)
    {
        var needle = name + "(";
        var index = code.IndexOf(needle, StringComparison.Ordinal);

        while (index >= 0)
        {
            var before = code[..index].TrimEnd();

            if (before.Length == 0 || IsConsuming(before))
            {
                return true;
            }

            index = code.IndexOf(needle, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsConsuming(string before)
    {
        foreach (var token in ConsumingTokens)
        {
            if (before.EndsWith(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // `if (values.IsNullOrEmpty())` puts the guard inside a condition, so the text before the
        // call ends in the callee rather than in a token.
        if (ContainsWord(before, "if"))
        {
            return true;
        }

        // `=> !string.IsNullOrWhiteSpace(x) ? …` puts it in a returned expression with a negation
        // and a member access in between, so the leading form is what has to catch it. Kept to
        // the three tokens that make the *line* a consumed expression: a bare `?` or `:` would
        // also match a nullable type declared earlier on the same line.
        foreach (var token in LeadingConsumingTokens)
        {
            if (before.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the trimmed line begins with a statement keyword.</summary>
    private static bool StartsStatement(string code, string keyword)
    {
        var trimmed = code.TrimStart();

        if (!trimmed.StartsWith(keyword, StringComparison.Ordinal))
        {
            return false;
        }

        if (trimmed.Length == keyword.Length)
        {
            return true;
        }

        var next = trimmed[keyword.Length];

        return !char.IsLetterOrDigit(next) && next != '_';
    }

    /// <summary>
    /// Removes comments and string/char literals, leaving the code they decorate.
    /// <para>
    /// Text-based rather than syntax-based on purpose: the input is a patch's added lines, which
    /// have no syntax tree and no context lines. Verbatim strings carry their state across lines so
    /// a multi-line JSON fixture stays opaque; raw (<c>"""</c>) literals are not tracked and would
    /// need the same treatment, which is noted rather than claimed.
    /// </para>
    /// </summary>
    private static string StripCommentsAndLiterals(string line, ref bool insideString)
    {
        var builder = new StringBuilder(line.Length);
        var index = 0;

        if (insideString)
        {
            index = ConsumeLiteral(line, 0, ref insideString);

            if (index == 0)
            {
                return string.Empty;
            }
        }

        while (index < line.Length)
        {
            var current = line[index];
            var next = index + 1 < line.Length ? line[index + 1] : '\0';

            if (current == '/' && next == '/')
            {
                break;
            }

            if (current == '/' && next == '*')
            {
                var close = line.IndexOf("*/", index + 2, StringComparison.Ordinal);

                if (close < 0)
                {
                    // Unterminated on this line: everything after it is comment.
                    break;
                }

                index = close + 2;
                continue;
            }

            if (current == '"' || current == '\'')
            {
                index = ConsumeLiteral(line, index, ref insideString);
                continue;
            }

            builder.Append(current);
            index++;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Skips past a string or char literal starting at <paramref name="start"/>, returning the
    /// index just after it. Sets <paramref name="insideString"/> when a verbatim literal continues
    /// onto the next line.
    /// </summary>
    private static int ConsumeLiteral(string line, int start, ref bool insideString)
    {
        var quote = line[start];
        var verbatim = IsVerbatimStart(line, start);
        var index = start + 1;

        while (index < line.Length)
        {
            var current = line[index];

            if (verbatim && current == '"')
            {
                if (index + 1 < line.Length && line[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }

                return index + 1;
            }

            if (!verbatim)
            {
                if (current == '\\')
                {
                    index += 2;
                    continue;
                }

                if (current == quote)
                {
                    return index + 1;
                }
            }

            index++;
        }

        // Only a verbatim literal can survive a line break; anything else is malformed.
        insideString = verbatim;
        return line.Length;
    }

    private static bool IsVerbatimStart(string line, int quoteIndex)
    {
        if (quoteIndex == 0)
        {
            return false;
        }

        if (line[quoteIndex - 1] == '@')
        {
            return true;
        }

        return quoteIndex >= 2 && line[quoteIndex - 1] == '$' && line[quoteIndex - 2] == '@';
    }

    /// <summary>Case-insensitive substring match that will not start or end mid-identifier.</summary>
    /// <remarks>
    /// Case-insensitivity is kept because it was already part of this method's contract and costs
    /// little now that matching is anchored: the false positives that motivated the change came
    /// from matching *inside* a longer identifier, not from casing.
    /// </remarks>
    private static bool ContainsWord(string line, string word)
    {
        var index = line.IndexOf(word, StringComparison.OrdinalIgnoreCase);

        while (index >= 0)
        {
            var before = index == 0 ? '\0' : line[index - 1];
            var afterIndex = index + word.Length;
            var after = afterIndex >= line.Length ? '\0' : line[afterIndex];

            if (!IsIdentifierCharacter(before) && !IsIdentifierCharacter(after))
            {
                return true;
            }

            index = line.IndexOf(word, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

private static bool IsIdentifierCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';
}

/// <summary>
/// Detects the temporal one-sided fix across a solution's clone sets (Wave 4 / 4C).
///
/// Scope is deliberately narrow and is stated rather than implied: **the most recent commit
/// touching each copy.** For such a commit the post-image is HEAD, so the hunk's coordinates and
/// the clone region's coordinates are one system and no reconciliation is needed. Reconciling a
/// patch's pre-image against a HEAD-anchored region across arbitrary history is where this design
/// would otherwise go wrong, and a plausible-looking bug there would produce confident nonsense.
/// The cost is recall — a one-sided fix followed by unrelated later edits to the same file is
/// missed — and widening it is a follow-up, not a redesign.
/// </summary>
public sealed class CloneDriftDetector
{
    private readonly string _repositoryRoot;

    public CloneDriftDetector(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        _repositoryRoot = repositoryRoot;
    }

    /// <summary>Drift findings for the given clone sets, deterministically ordered.</summary>
    internal IReadOnlyList<SnipperFinding> Detect(IReadOnlyList<CloneSet> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);

        if (sets.Count == 0)
        {
            return [];
        }

        var memberPaths = sets
            .SelectMany(set => set.Members)
            .Select(member => ToRepositoryRelative(member.Path))
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // One call answers "most recent commit per copy" for every member of every set.
        var mostRecent = GitHistory.TryGetMostRecentCommits(_repositoryRoot, memberPaths);
        if (mostRecent is null || mostRecent.Count == 0)
        {
            return [];
        }

        var wantedShas = mostRecent.Values
            .Select(commit => commit.Sha)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var patches = GitHistory.TryGetPatches(_repositoryRoot, wantedShas)
            .ToDictionary(patch => patch.Sha, StringComparer.Ordinal);

        var findings = new List<SnipperFinding>();

        foreach (var set in sets)
        {
            Evaluate(set, mostRecent, patches, findings);
        }

        // Overlapping clone sets are normal — SNP0031 reports a maximal run and its sub-runs, so the
        // same two files can appear in several sets. Deduplicating on (commit, copy) stops one
        // inconsistency being reported two or three times over, which is how a finding loses the
        // reader's trust. The first occurrence in the deterministic set order wins.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduplicated = new List<SnipperFinding>();

        foreach (var finding in findings
            .OrderBy(f => f.FilePath, StringComparer.Ordinal)
            .ThenBy(f => f.LineNumber)
            .ThenBy(f => f.Message, StringComparer.Ordinal))
        {
            if (seen.Add($"{CommitOf(finding)}|{finding.FilePath}"))
            {
                deduplicated.Add(finding);
            }
        }

        return deduplicated;
    }

    /// <summary>
    /// The commit a finding names, taken from its message. Findings are keyed by commit rather
    /// than by line because two overlapping sets can report the same commit at different lines.
    /// </summary>
    private static string CommitOf(SnipperFinding finding)
    {
        const string marker = "commit ";
        var start = finding.Message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return finding.Message;
        }

        start += marker.Length;
        var length = finding.Message.IndexOf(' ', start);
        return length < 0 ? finding.Message[start..] : finding.Message[start..length];
    }

    private void Evaluate(
        CloneSet set,
        IReadOnlyDictionary<string, MostRecentCommit> mostRecent,
        IReadOnlyDictionary<string, CommitPatch> patches,
        List<SnipperFinding> findings)
    {
        foreach (var member in set.Members)
        {
            var relative = ToRepositoryRelative(member.Path);
            if (!mostRecent.TryGetValue(relative, out var commit)
                || !patches.TryGetValue(commit.Sha, out var patch)
                || !patch.HunksByPath.TryGetValue(relative, out var hunks))
            {
                continue;
            }

            var siblingPaths = set.Members
                .Where(other => !string.Equals(other.Path, member.Path, StringComparison.Ordinal))
                .Select(other => ToRepositoryRelative(other.Path))
                // Distinct because a set can hold two regions of one file, and two members can
                // reduce to the same repository-relative path. Listing a sibling twice made a
                // single commit look like it had fixed two files.
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // One-sidedness is judged from the history index, which already knows every path the
            // commit touched; no sibling patch needs to be fetched to decide it.
            var oneSided = siblingPaths.All(path => !commit.Paths.Contains(path));

            foreach (var hunk in hunks)
            {
                // A hunk that created this copy is not a one-sided *fix*: nothing existed
                // to fall out of sync, so there was no inconsistency for the commit to
                // resolve. On a real repository this is the majority of what the rule sees -
                // the merge that introduced a clone set was being reported as drift within
                // it, and the "later (… on …) reached …" clause could name a catch-up that
                // in fact predated the change it was meant to resolve.
                //
                // Keyed on git's "new file mode" header, not on OldCount == 0: a guard
                // inserted above a cloned block is also "@@ -N,0 +N,K @@", and that is the
                // canonical shape this rule exists to report.
                if (hunk.IsFileCreation)
                {
                    continue;
                }

                if (!CloneDriftClassifier.HunkTouchesRegion(hunk, member.StartLine, member.EndLine))
                {
                    continue;
                }

                var renameOnly = RenameDetector.IsRenameOnly(hunk.AddedLines, hunk.RemovedLines);
                var fixShaped = !renameOnly && DefensiveFixMarkers.IsFixShaped(hunk.AddedLines, hunk.RemovedLines);
                var tier = CloneDriftClassifier.Classify(oneSided, fixShaped, renameOnly);
                if (tier is null)
                {
                    continue;
                }

                // A commit that brought this copy *into line* with a sibling fixed earlier is the
                // resolution of an inconsistency, not a new one. Reporting both links of a chain
                // would double every finding and bury the one that matters. Creation commits are
                // excluded: a commit that adds a file has not "fixed" a clone.
                if (SiblingWasFixedEarlier(set, member, commit, mostRecent, patches))
                {
                    continue;
                }

                var siblings = CloneDriftClassifier.DescribeSiblings(
                    set.Members
                        .Where(other => !string.Equals(other.Path, member.Path, StringComparison.Ordinal))
                        .Select(other => (RelativePath: ToRepositoryRelative(other.Path), other.StartLine)));

// Whether the sibling was eventually brought in line, and which commit did it.
                // This dates the window in which the copies were out of sync - the actionable half
                // of the finding - or shows that no equivalent change ever arrived.
                var catchUps = siblingPaths
                    .Select(path => (Path: path, Later: mostRecent.TryGetValue(path, out var later) ? later : null))
                    .Where(entry => entry.Later is not null && entry.Later.Order < commit.Order)
                    .Select(entry => (entry.Path, entry.Later!.Sha, entry.Later.Date))
                    .ToList();

                var outcome = catchUps.Count > 0
                    ? "an equivalent change reached the sibling later ("
                        + CloneDriftClassifier.DescribeCatchUps(catchUps)
                        + ")"
                    : "no equivalent change to any sibling appears in later history";

                var message = new StringBuilder()
                    .Append(tier == CloneDriftTier.High
                        ? "One-sided defensive fix"
                        : renameOnly
                            ? "One-sided rename"
                            : "One-sided change")
                    .Append(" in a clone set of ")
                    .Append(set.Members.Count)
                    .Append(" copies: commit ")
                    .Append(commit.Sha[..7])
                    .Append(" (\"")
                    .Append(commit.Message)
                    .Append("\", ")
                    .Append(commit.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    .Append(") changed this copy without touching ")
                    .Append(siblings)
                    .Append("; ")
                    .Append(outcome)
                    .Append('.')
                    .ToString();

                // Anchor at the line the hunk actually changed rather than at the start of the
                // clone region; see CloneDriftClassifier.AnchorLine for why region start was wrong.
                var anchorLine = CloneDriftClassifier.AnchorLine(hunk, member.StartLine, member.EndLine);

                findings.Add(new SnipperFinding(
                    RuleId: "SNP0032",
                    Title: "Clone Set Fix Drift",
                    Message: message,
                    Certainty: tier == CloneDriftTier.High
                        ? CertaintyTier.High
                        : CertaintyTier.Advisory,
                    Category: FindingCategory.CloneDrift,
                    FilePath: member.Path,
                    LineNumber: anchorLine,
                    CharacterOffset: member.StartCharacter,
                    Symbol: null));
            }
        }
    }

    /// <summary>
    /// Whether a sibling copy of this set was itself changed — at a region it shares with this
    /// member — by an *older* commit. That makes the current commit a catch-up rather than drift.
    ///
    /// Only hunks that modify existing lines count. The commit that created a file reports
    /// <c>@@ -0,0 +1,N @@</c>, whose pre-image is empty; treating that as "the sibling was fixed
    /// earlier" would suppress every finding in any repository whose clone set pre-dates the fix.
    /// </summary>
    private bool SiblingWasFixedEarlier(
        CloneSet set,
        CloneSetMember member,
        MostRecentCommit commit,
        IReadOnlyDictionary<string, MostRecentCommit> mostRecent,
        IReadOnlyDictionary<string, CommitPatch> patches)
    {
        foreach (var sibling in set.Members)
        {
            if (string.Equals(sibling.Path, member.Path, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = ToRepositoryRelative(sibling.Path);

            // Only a sibling that was fixed *before* this commit makes this commit a catch-up.
            // Order is newest-first, so "before" means a larger order. A sibling fixed after this
            // commit is the other half of the drift and must not suppress it.
            if (!mostRecent.TryGetValue(relative, out var siblingCommit)
                || siblingCommit.Order <= commit.Order
                || !patches.TryGetValue(siblingCommit.Sha, out var patch)
                || !patch.HunksByPath.TryGetValue(relative, out var hunks))
            {
                continue;
            }

            foreach (var hunk in hunks)
            {
                // Deliberately left as OldCount > 0 rather than switched to IsFileCreation.
                // The stated intent is "only hunks that modify existing lines count", and a
                // pure insertion modifies no line - so IsFileCreation would wrongly admit
                // insertions and suppress drift the sibling really did resolve.
                if (hunk.OldCount > 0
                    && CloneDriftClassifier.HunkTouchesRegion(hunk, sibling.StartLine, sibling.EndLine))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private string ToRepositoryRelative(string path) =>
        Path.GetRelativePath(_repositoryRoot, path).Replace('\\', '/');
}

/// <summary>One copy of a clone set, as 4C needs it.</summary>
internal sealed record CloneSetMember(string Path, int StartLine, int StartCharacter, int EndLine);

/// <summary>A group of copies proven token-identical by SNP0031.</summary>
internal sealed record CloneSet(IReadOnlyList<CloneSetMember> Members);
