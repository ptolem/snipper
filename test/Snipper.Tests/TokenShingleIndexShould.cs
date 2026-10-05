namespace Snipper.Tests;

using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snipper.Analysis;
using Xunit;

/// <summary>
/// SNP0031 - token-shingle index unit tests. These drive the engine directly
/// from parsed syntax trees, without a workspace, so tokenizer and shingling
/// behaviour is pinned independently of the analyser.
/// </summary>
public sealed class TokenShingleIndexShould
{
    private static SyntaxNode Root(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return tree.GetRoot();
    }

    private static IReadOnlyList<string> NormalizedTokens(SyntaxNode root)
    {
        return TokenShingleIndex.Tokenize(root);
    }

    [Fact]
    public void Abstract_Identifiers_So_Renamed_Copies_Match()
    {
        var first = Root("class C { void M(int alpha) { var beta = alpha + 1; Use(beta); } }");
        var second = Root("class D { void N(int gamma) { var delta = gamma + 1; Use(delta); } }");

        NormalizedTokens(first).Should().Equal(NormalizedTokens(second));
    }

    [Fact]
    public void Abstract_String_Character_And_Numeric_Literals()
    {
        var source = """
            class C
            {
                void M()
                {
                    var a = "alpha";
                    var b = 'b';
                    var c = 123;
                    var d = 4.5;
                }
            }
            """;

        var tokens = NormalizedTokens(Root(source));

        tokens.Should().Contain("STR").And.Contain("CHR").And.Contain("NUM");
        tokens.Should().NotContain("alpha").And.NotContain("123");
    }

    [Fact]
    public void Preserve_Keyword_And_Punctuation_Text()
    {
        var tokens = NormalizedTokens(Root("class C { void M() { if (x == y) { return; } } }"));

        tokens.Should().Contain("if").And.Contain("class").And.Contain("void");
        tokens.Should().Contain("==").And.Contain(";");
    }

    [Fact]
    public void Exclude_Using_Directive_Tokens()
    {
        // Identifiers normalize to ID, so the check is stream equality: a file
        // with three using directives must produce exactly the same tokens as
        // one without them. This is what keeps near-identical GlobalUsings.cs
        // files across 82 projects from colliding (measured: 3,521 buckets at
        // W=60 before the exclusion).
        var withUsings = NormalizedTokens(Root("""
            using System;
            using System.Collections.Generic;
            using System.Text;
            class C { void M() { var list = 1; } }
            """));
        var withoutUsings = NormalizedTokens(Root("class C { void M() { var list = 1; } }"));

        withUsings.Should().Equal(withoutUsings);
    }

    [Fact]
    public void Exclude_Extern_Alias_Directive_Tokens()
    {
        var withAlias = NormalizedTokens(Root("""
            extern alias Legacy;
            class C { void M() { var v = 1; } }
            """));
        var withoutAlias = NormalizedTokens(Root("class C { void M() { var v = 1; } }"));

        withAlias.Should().Equal(withoutAlias);
        withAlias.Should().NotContain("extern");
    }

    [Fact]
    public void Ignore_Comments_And_Trivia()
    {
        var withComments = Root("""
            class C
            {
                // alpha beta gamma
                void M() { var x = 1; /* delta epsilon */ }
            }
            """);

        var withoutComments = Root("class C { void M() { var x = 1; } }");

        NormalizedTokens(withComments).Should().Equal(NormalizedTokens(withoutComments));
    }

    [Fact]
    public void Preserve_Namespace_Declaration_Tokens()
    {
        // Namespace bodies are the signal, so the declaration itself stays in
        // the stream (the keyword and braces); only using/extern subtrees are
        // cut. The namespace name is an identifier, so it normalizes to ID.
        var tokens = NormalizedTokens(Root("namespace N { class C { void M() { var x = 1; } } }"));

        tokens.Should().Contain("namespace").And.Contain("ID");
        tokens.Should().HaveCountGreaterThan(NormalizedTokens(Root("class C { void M() { var x = 1; } }")).Count);
    }

    [Fact]
    public void Record_Line_Numbers_For_Location_Reporting()
    {
        var root = Root("""
            class C
            {
                void M()
                {
                    var x = 1;
                }
            }
            """);

        var lines = TokenShingleIndex.TokenLines(root);

        lines.Should().HaveCountGreaterThan(0);
        lines.Select(l => l.Line).Distinct().Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public void Emit_No_Windows_For_Streams_Shorter_Than_The_Window()
    {
        var index = TokenShingleIndex.Build([("a.cs", Root("class C { int M() { return 1; } }"))], windowTokens: 60);

        index.Windows.Should().BeEmpty();
    }

    [Fact]
    public void Emit_Windows_For_Long_Streams()
    {
        var body = string.Join(" ", Enumerable.Range(0, 80).Select(i => $"var v{i} = {i};"));
        var root = Root($"class C {{ void M() {{ {body} }} }}");

        var index = TokenShingleIndex.Build([("a.cs", root)], windowTokens: 60);

        index.Windows.Should().NotBeEmpty();
    }

    [Fact]
    public void Dedupe_Locations_By_Path()
    {
        // Same document opened twice (linked file, multi-TFM): locations are
        // keyed by path, so one copy contributes one location (decision 5).
        var body = string.Join(" ", Enumerable.Range(0, 80).Select(i => $"var v{i} = {i};"));
        var root = Root($"class C {{ void M() {{ {body} }} }}");

        var index = TokenShingleIndex.Build([("a.cs", root), ("a.cs", root)], windowTokens: 60);

        var duplicated = index.Windows
            .SelectMany(w => w.Value)
            .Where(l => l.Path == "a.cs" && l.TokenIndex == 0)
            .ToList();

        duplicated.Should().HaveCount(1);
    }

    [Fact]
    public void Report_Distinct_Paths_For_The_Same_Window_Hash()
    {
        var body = string.Join(" ", Enumerable.Range(0, 80).Select(i => $"var v{i} = {i};"));
        var first = Root($"class C {{ void M() {{ {body} }} }}");
        var second = Root($"class D {{ void N() {{ {body} }} }}");

        var index = TokenShingleIndex.Build([("a.cs", first), ("b.cs", second)], windowTokens: 60);

        index.Windows.Values
            .Should().Contain(w => w.Count == 2 && w.Select(x => x.Path).Distinct().Count() == 2);
    }

    /// <summary>
    /// Regression: the window hash used to fold <c>string.GetHashCode()</c> into its FNV
    /// accumulator while its own documentation described plain FNV-1a. .NET randomizes
    /// string hashing <em>per process</em>, so the same window landed in a different
    /// bucket on every run: candidate pairs were generated in a different order, and match
    /// collapsing then chose different maximal fragments. SNP0031/SNP0032 output visibly
    /// moved between runs on identical input.
    /// <para>
    /// A same-process test cannot catch that on its own, because every call inside one
    /// process agrees. These assertions pin values that only a process-independent hash can
    /// produce, so the suite fails in a fresh process if the randomization returns.
    /// </para>
    /// </summary>
    [Fact]
    public void Bucket_Hashes_Are_Process_Independent()
    {
        var root = Root("class C { void M() { var alpha = 1; var beta = 2; } }");

        var index = TokenShingleIndex.Build([("a.cs", root)], windowTokens: 4);

        index.Windows.Keys.Should().BeEquivalentTo(
        [
            -1531693745, -1388522359, -1171468643, -1062740332, -1018468549,
            -602846674, -377643611, -111502071, 368403820, 896792330,
            1525551678, 1658470832, 1686949882, 1836633539, 1967195235,
        ]);
    }

    /// <summary>
    /// The same guarantee stated as a property rather than as constants: the engine's
    /// buckets must agree with an independent FNV-1a over the token <em>text</em>. Shares
    /// no code with the engine on purpose, so re-seeding the accumulator from
    /// <c>GetHashCode</c> cannot make this pass.
    /// </summary>
    [Fact]
    public void Bucket_Hashes_Match_An_Independent_Fnv_Over_Token_Text()
    {
        const int windowTokens = 4;
        var root = Root("class C { void M() { var alpha = 1; var beta = 2; } }");
        var tokens = TokenShingleIndex.Tokenize(root);

        var index = TokenShingleIndex.Build([("a.cs", root)], windowTokens);

        var expected = new HashSet<int>();
        for (var start = 0; start + windowTokens <= tokens.Count; start++)
        {
            expected.Add(ReferenceHash(tokens, start, windowTokens));
        }

        index.Windows.Keys.Should().BeEquivalentTo(expected);
    }

    /// <summary>
    /// A per-token terminator keeps window boundaries significant: without it, the token
    /// sequences ("ab", "c") and ("a", "bc") fold to the same value and pull unrelated text
    /// into one bucket, which costs time in the extension pass for no analytical gain.
    /// </summary>
    [Fact]
    public void Token_Boundaries_Are_Significant_To_The_Hash()
    {
        static int Reference(IReadOnlyList<string> tokens, int start, int length)
        {
            var hash = 2166136261u;
            for (var i = start; i < start + length; i++)
            {
                foreach (var character in tokens[i])
                {
                    hash ^= character;
                    hash *= 16777619u;
                }

                hash ^= 0x1Fu;
                hash *= 16777619u;
            }

            return unchecked((int)hash);
        }

        var joined = Reference(["ab", "c"], 0, 2);
        var split = Reference(["a", "bc"], 0, 2);

        split.Should().NotBe(joined);
    }

    // ---------- window verification ----------

    /// <summary>
    /// The smallest window the index will consider - 60 tokens, matching
    /// <c>DuplicateFragmentAnalyser.WindowTokens</c>. Shape only; <see cref="Extend"/>
    /// is driven on token text directly so no fixture has to reproduce a hash.
    /// </summary>
    private static List<string> Window(string lastToken = "tail")
    {
        var tokens = Enumerable.Repeat("ID", 59).ToList();
        tokens.Add(lastToken);
        return tokens;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(29)]
    [InlineData(58)]
    [InlineData(59)]
    public void Treat_A_Window_As_Unmatched_When_Any_Of_its_Tokens_Differs(int differingOffset)
    {
        // The defect this pins: Extend seeded its forward scan at WindowTokens, so the
        // 60-token window it was handed was never compared. A pair that merely shared
        // an FNV-1a/32 bucket therefore became a 60-token "clone" unverified - and
        // offsets [0,60) are the very run a real clone is made of.
        var left = Window();
        var right = Window();
        right[differingOffset] = "OTHER";

        var (_, _, length) = DuplicateFragmentAnalyser.Extend(left, 0, right, 0);

        length.Should().Be(0, "a bucket match is a hash collision until the window itself is proven");
    }

    [Fact]
    public void Extend_A_Run_From_A_Window_That_Is_Actually_Equal()
    {
        // The counterweight. Proving the window must not cost one real clone.
        var left = Window();
        var right = Window();

        var (leftStart, rightStart, length) = DuplicateFragmentAnalyser.Extend(left, 0, right, 0);

        length.Should().Be(60, "an equal window is the minimum reportable fragment");
        leftStart.Should().Be(0);
        rightStart.Should().Be(0);
    }

    [Fact]
    public void Treat_A_Real_Hash_Collision_As_No_Match()
    {
        // Proves the guard is load-bearing rather than theoretical: these two windows are
        // genuinely indistinguishable to the index, and differ in content.
        var prefix = Enumerable.Repeat("ID", 59).ToList();
        var byHash = new Dictionary<int, string>();

        List<string>? first = null;
        List<string>? second = null;
        for (var i = 0; i < 500_000 && second is null; i++)
        {
            var candidate = new List<string>(prefix) { "v" + i };
            var hash = TokenShingleIndex.Hash(candidate, 0, 60);
            if (byHash.TryGetValue(hash, out var prior))
            {
                first = [.. prefix, prior];
                second = candidate;
                break;
            }

            byHash[hash] = candidate[^1];
        }

        second.Should().NotBeNull("FNV-1a/32 collides well inside 500k 60-token windows");
        first.Should().NotEqual(second, "otherwise this test would prove nothing about collisions");
        TokenShingleIndex.Hash(first!, 0, 60).Should().Be(
            TokenShingleIndex.Hash(second!, 0, 60),
            "the whole point is that the index cannot tell these two apart");

        DuplicateFragmentAnalyser.Extend(first!, 0, second!, 0).Length.Should().Be(0);
    }

    [Fact]
    public void Treat_A_Window_That_Runs_Past_End_Of_Either_File_As_No_Match()
    {
        // Extend is reachable directly, so it must not trust its caller for bounds the
        // index happens to guarantee on the CLI path.
        DuplicateFragmentAnalyser.Extend(Window(), 0, Window(), 5).Length.Should().Be(0);
    }

    private static int ReferenceHash(IReadOnlyList<string> tokens, int start, int length)
    {
        var hash = 2166136261u;
        for (var index = start; index < start + length; index++)
        {
            foreach (var character in tokens[index])
            {
                hash ^= character;
                hash *= 16777619u;
            }

            hash ^= 0x1Fu;
            hash *= 16777619u;
        }

        return unchecked((int)hash);
    }
}