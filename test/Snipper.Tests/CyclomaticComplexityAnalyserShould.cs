namespace Snipper.Tests;

using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

/// <summary>
/// SNP0033 — cyclomatic complexity.
///
/// The analyser is deliberately driven at <c>maxComplexity = 1</c> so every method
/// with a branch is reported and the exact count can be read back out of the
/// finding's message. That pins the counting table — including every deliberate
/// <em>non-count</em> — through the public API, so the numbers below cannot drift
/// away from what the rule actually emits.
/// </summary>
public sealed class CyclomaticComplexityAnalyserShould
{
    private const int FlagEverything = 1;

    /// <summary>
    /// Complexity per reported line number, at a threshold low enough to catch
    /// anything with a branch at all.
    /// </summary>
    private static async Task<Dictionary<int, int>> CountsAsync(string source, int maxComplexity = FlagEverything)
    {
        var findings = await AnalyzeAsync(source, maxComplexity);
        var result = new Dictionary<int, int>();

        foreach (var finding in findings)
        {
            var match = Regex.Match(finding.Message, @"complexity (\d+)");
            match.Success.Should().BeTrue($"the message must carry the count: '{finding.Message}'");
            result[finding.LineNumber] = int.Parse(match.Groups[1].Value);
        }

        return result;
    }

    private static async Task<List<SnipperFinding>> AnalyzeAsync(string source, int maxComplexity)
    {
        using var workspace = new AdhocWorkspace();
        var corlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var projectId = ProjectId.CreateNewId("Probe");

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Create(), "Probe", "Probe", LanguageNames.CSharp,
                filePath: @"C:\repo\Probe\Probe.csproj"))
            .AddMetadataReference(projectId, corlib)
            .AddDocument(
                DocumentId.CreateNewId(projectId), "Code.cs", SourceText.From(source),
                filePath: @"C:\repo\Probe\Code.cs");

        var findings = await new CyclomaticComplexityAnalyser(null, maxComplexity)
            .AnalyzeAsync(solution, CancellationToken.None);

        return [.. findings];
    }

    /// <summary>
    /// The 1-based line holding a unique substring. Keying on hand-counted line
    /// numbers was wrong repeatedly while these were written; locating the construct
    /// under test is both correct and readable.
    /// </summary>
    private static int LineOf(string source, string needle) =>
        Array.FindIndex(
            source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'),
            line => line.Contains(needle, StringComparison.Ordinal)) + 1;

    // ---- baseline -------------------------------------------------------

    [Fact]
    public async Task Branch_Free_Method_Scores_One_And_Is_Not_Reported()
    {
        const string source = "class C { void M() { var x = 1; Use(x); } }";

        var counts = await CountsAsync(source);

        counts.Should().BeEmpty("a method with no branches scores 1, which does not exceed the threshold");
    }

    [Fact]
    public async Task Each_Method_Is_Scored_On_Its_Own()
    {
        const string source = """
            class C
            {
                void A() { if (x) { } }
                void B() { if (x) { } else { } if (y) { } }
            }
            """;

        var counts = await CountsAsync(source);

        counts[LineOf(source, "void A")].Should().Be(2);
        counts[LineOf(source, "void B")].Should().Be(3, "two ifs; the else is not a third");
    }

    // ---- +1 rows -------------------------------------------------------

    [Fact]
    public async Task Loops_Each_Count_One()
    {
        const string source = """
            class C
            {
                void WhileLoop() { while (x) { } }
                void DoLoop() { do { } while (x); }
                void ForLoop() { for (var i = 0; i < 3; i++) { } }
                void ForEachLoop() { foreach (var i in items) { } }
            }
            """;

        var counts = await CountsAsync(source);

        counts[LineOf(source, "void WhileLoop")].Should().Be(2);
        counts[LineOf(source, "void DoLoop")].Should().Be(2);
        counts[LineOf(source, "void ForLoop")].Should().Be(2);
        counts[LineOf(source, "void ForEachLoop")].Should().Be(2);
    }

    [Fact]
    public async Task Case_Labels_Count_And_Default_Does_Not()
    {
        const string source = """
            class C
            {
                void M(int v)
                {
                    switch (v)
                    {
                        case 1: break;
                        case 2: break;
                        case 3: break;
                        default: break;
                    }
                }
            }
            """;

        var counts = await CountsAsync(source);

        counts[LineOf(source, "void M")].Should().Be(4, "baseline plus three case labels; default is the fallthrough");
    }

    [Fact]
    public async Task Catch_Clause_Counts_And_A_Filter_Counts_Again()
    {
        const string source = """
            class C
            {
                void Plain()
                {
                    try { } catch (Exception) { }
                }

                void Filtered(int v)
                {
                    try { } catch (Exception) when (v > 0) { }
                }
            }
            """;

        var counts = await CountsAsync(source);

        counts[LineOf(source, "void Plain")].Should().Be(2, "a catch is a branch");
        counts[LineOf(source, "void Filtered")].Should().Be(3, "a filtered catch is two branches");
    }

    [Fact]
    public async Task Short_Circuit_Operators_Each_Count()
    {
        const string source = """
            class C
            {
                void And(int a, int b) { if (a > 0 && b > 0) { } }
                void Or(int a, int b) { if (a > 0 || b > 0) { } }
                void Both(int a, int b, int c) { if (a > 0 && b > 0 || c > 0) { } }
            }
            """;

        var counts = await CountsAsync(source);

        counts[LineOf(source, "void And")].Should().Be(3);
        counts[LineOf(source, "void Or")].Should().Be(3);
        counts[LineOf(source, "void Both")].Should().Be(4, "an if plus two short-circuiting operators");
    }

    [Fact]
    public async Task Ternary_Counts()
    {
        const string source = "class C { int M(int a) => a > 0 ? 1 : 2; }";

        var counts = await CountsAsync(source);

        counts[LineOf(source, "int M")].Should().Be(2);
    }

    [Fact]
    public async Task Switch_Expression_Arms_Count_And_The_Discard_Arm_Does_Not()
    {
        const string source = """
            class C
            {
                int M(int v) => v switch
                {
                    1 => 10,
                    2 => 20,
                    3 => 30,
                    _ => 0,
                };
            }
            """;

        var counts = await CountsAsync(source);

        counts[LineOf(source, "int M")].Should().Be(4, "three arms; the discard arm is the fallthrough");
    }

    // ---- deliberate non-counts — the parity risk ------------------------

    [Fact]
    public async Task Bare_Else_Does_Not_Count()
    {
        const string source = "class C { void M(bool x) { if (x) { } else { } } }";

        var counts = await CountsAsync(source);

        counts[LineOf(source, "void M")].Should().Be(2, "the else is the if's negative branch, not a second one");
    }

    [Fact]
    public async Task Each_Else_If_Counts_As_Its_Own_If()
    {
        const string source = """
            class C
            {
                void M(int v)
                {
                    if (v == 1) { }
                    else if (v == 2) { }
                    else if (v == 3) { }
                    else { }
                }
            }
            """;

        var counts = await CountsAsync(source);

        counts[LineOf(source, "void M")].Should().Be(4, "three ifs; the trailing else adds nothing");
    }

    [Fact]
    public async Task Try_Finally_And_Throw_Do_Not_Count()
    {
        const string source = """
            class C
            {
                void M(int v)
                {
                    try
                    {
                        if (v == 0) { throw new Exception(); }
                    }
                    finally
                    {
                        Cleanup();
                    }
                }
            }
            """;

        var counts = await CountsAsync(source);

        counts[LineOf(source, "void M")].Should().Be(2, "only the if; try/finally/throw are control flow, not decisions");
    }

    [Fact]
    public async Task Null_Conditional_Coalescing_And_Is_Pattern_Do_Not_Count()
    {
        const string source = """
            class C
            {
                string? M(object? o)
                {
                    o?.ToString();
                    var a = o ?? "x";
                    var c = o is string ? o : null;
                    return a + c?.ToString();
                }
            }
            """;

        var counts = await CountsAsync(source);

        // Baseline 1 plus the one real ternary. `?.`, `??` and the `is` pattern are
        // all deliberately zero, which is what keeps this at 2 rather than 5.
        counts[LineOf(source, "string? M")].Should().Be(2, "only the ternary counts");
    }

    // ---- scope boundaries ----------------------------------------------

    [Fact]
    public async Task Lambda_Is_Scored_Separately_And_Not_Charged_To_Its_Parent()
    {
        const string source = """
            class C
            {
                void Parent()
                {
                    Run(() =>
                    {
                        if (a) { }
                        if (b) { }
                    });
                }
            }
            """;

        var counts = await CountsAsync(source);

        // The assertion that matters: the parent scores 1, so it is absent entirely.
        // Charging it for the lambda's two ifs would have shown up as a finding here.
        counts.Should().NotContainKey(LineOf(source, "void Parent"), "a parent must not be charged for a nested lambda");
        counts[LineOf(source, "Run(() =>")].Should().Be(3, "the lambda scores on its own: baseline plus two ifs");
    }

    [Fact]
    public async Task Local_Function_Is_Scored_Separately()
    {
        const string source = """
            class C
            {
                void Parent()
                {
                    void Inner() { if (a) { } }
                    Inner();
                }
            }
            """;

        var counts = await CountsAsync(source);

        counts.Should().NotContainKey(LineOf(source, "void Parent"), "the parent has no branches of its own");
        counts[LineOf(source, "void Inner")].Should().Be(2, "the local function scores on its own");
    }

    [Fact]
    public async Task Accessor_And_Expression_Bodied_Member_Are_Own_Scopes()
    {
        const string source = """
            class C
            {
                int Blocked
                {
                    get
                    {
                        if (a) { return 1; }
                        return 0;
                    }
                }

                int Expression => a > 0 ? 1 : 0;
            }
            """;

        var counts = await CountsAsync(source);

        counts[LineOf(source, "get")].Should().Be(2, "the accessor is the scope, not the property");
        counts[LineOf(source, "int Expression")].Should().Be(2, "an expression-bodied property scores its expression");
    }

    // ---- threshold and finding shape -----------------------------------

    [Fact]
    public async Task Threshold_Is_Respected()
    {
        const string source = "class C { void M(bool a) { if (a) { } if (a) { } if (a) { } } }";

        // Three ifs score 4, so a max of 3 reports it and a max of 4 does not.
        (await CountsAsync(source, maxComplexity: 4)).Should().BeEmpty();
        (await CountsAsync(source, maxComplexity: 3)).Should().ContainKey(LineOf(source, "void M"));
    }

    [Fact]
    public async Task Finding_Shape_Is_Advisory_In_The_Complexity_Category()
    {
        const string source = "class C { void M(bool a) { if (a) { } if (a) { } } }";

        var findings = await AnalyzeAsync(source, 2);

        var finding = findings.Should().ContainSingle(f => f.RuleId == "SNP0033").Subject;
        finding.Certainty.Should().Be(CertaintyTier.Advisory);
        finding.Category.Should().Be(FindingCategory.Complexity);
        finding.Title.Should().Be("High Cyclomatic Complexity");
        finding.Message.Should().Contain("complexity 3");
    }

    [Fact]
    public async Task Complexity_One_Method_Is_Not_Reported_Even_At_Threshold_One()
    {
        const string source = """
            class C
            {
                int Add(int a, int b) => a + b;

                void Loops()
                {
                    foreach (var x in list) { total += x; }
                    while (queue.Count > 0) { Dequeue(); }
                }
            }
            """;

        var counts = await CountsAsync(source);

        // A finding needs complexity > max, so at max = 1 a score of 1 is absent by
        // definition. Loops() scores 3 and is present.
        counts.Should().NotContainKey(LineOf(source, "int Add"));
        counts[LineOf(source, "void Loops")].Should().Be(3, "baseline plus foreach plus while");
    }

    [Fact]
    public async Task Non_Positive_Threshold_Falls_Back_To_The_Default()
    {
        const string source = "class C { void M(bool a) { if (a) { } } }";

        // maxComplexity = 1 would report M; the default of 15 must not.
        (await CountsAsync(source, maxComplexity: 1)).Should().ContainKey(LineOf(source, "void M"));

        var findings = await AnalyzeAsync(source, 0);
        findings.Should().BeEmpty("a non-positive threshold falls back to the default, not to zero");
    }
}