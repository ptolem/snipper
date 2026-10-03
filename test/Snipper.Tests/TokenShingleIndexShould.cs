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
}