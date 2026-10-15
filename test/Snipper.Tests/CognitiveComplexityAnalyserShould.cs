namespace Snipper.Tests;

using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Snipper.Analysis;
using Snipper.Models;
using Xunit;

/// <summary>
/// SNP0034 — cognitive complexity.
///
/// The first tests are <em>spec parity</em>: the two worked examples from the
/// SonarSource whitepaper, transcribed into C#, with the totals the whitepaper
/// prints. Those are the anchor. Every other test exists to pin a rule the
/// examples do not cover on their own, or a place where this implementation
/// deliberately differs from the spec — because an unstated difference from the
/// reference is how a "parity" claim quietly stops being true.
/// <para>
/// Scoring tests go through <see cref="CognitiveComplexityAnalyser.Score"/> rather than
/// through findings, because cognitive complexity starts at 0: a method scoring 1
/// cannot breach any threshold, so a findings-driven test could never pin the rows
/// where the interesting disagreement with SNP0033 lives.
/// </para>
/// </summary>
public sealed class CognitiveComplexityAnalyserShould
{
    /// <summary>
    /// Cognitive complexity per method, keyed by the 1-based line of its declaration.
    /// </summary>
    private static Dictionary<int, int> Scores(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var scores = new Dictionary<int, int>();

        foreach (var node in root.DescendantNodes())
        {
            var body = ComplexityFunctionBody.TryGetBody(node, includeLambdas: false);
            if (body is null)
            {
                continue;
            }

            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            scores[line] = CognitiveComplexityAnalyser.Score(body);
        }

        return scores;
    }

    /// <summary>
    /// Runs the analyser end to end at an explicit threshold, for the tests that are
    /// about findings rather than about the counting table.
    /// </summary>
    private static async Task<List<SnipperFinding>> AnalyzeAsync(string source, int? maxComplexity)
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

        var findings = await new CognitiveComplexityAnalyser(maxComplexity)
            .AnalyzeAsync(solution, CancellationToken.None);

        return [.. findings];
    }

    /// <summary>
    /// Line numbers of the methods the analyser actually reports, at an explicit threshold.
    /// </summary>
    private static async Task<Dictionary<int, int>> ReportedLinesAsync(string source, int maxComplexity)
    {
        var findings = await AnalyzeAsync(source, maxComplexity);
        var reported = new Dictionary<int, int>();

        foreach (var finding in findings)
        {
            var match = Regex.Match(finding.Message, @"complexity (\d+)");
            match.Success.Should().BeTrue($"the message must carry the count: '{finding.Message}'");
            reported[finding.LineNumber] = int.Parse(match.Groups[1].Value);
        }

        return reported;
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

    // ---- spec parity: the whitepaper's two worked examples ---------------

    /// <summary>
    /// The whitepaper's <c>myMethod</c>, which the spec prints as 9.
    /// <para>
    /// try +1, if +1 (nesting 0), for +2 (nesting 1), while +3 (nesting 2),
    /// catch +1 (nesting 0), if +2 (nesting 1).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Whitepaper_MyMethod_Scores_Nine()
    {
        const string source = """
            class C
            {
                void MyMethod()
                {
                    try
                    {
                        if (condition1)
                        {
                            for (var i = 0; i < 10; i++)
                            {
                                while (condition2) { }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        if (condition2) { }
                    }
                }
            }
            """;

        var scores = Scores(source);

        scores[LineOf(source, "void MyMethod")].Should().Be(9, "the total the SonarSource whitepaper prints for myMethod");
    }

    /// <summary>
    /// The whitepaper's <c>myMethod2</c>, which the spec prints as 2: the lambda
    /// itself takes no increment, but the <c>if</c> inside it sits at nesting level 1
    /// and so costs 2.
    /// </summary>
    [Fact]
    public async Task Whitepaper_MyMethod2_Scores_Two()
    {
        const string source = """
            class C
            {
                void MyMethod2()
                {
                    Run(() =>
                    {
                        if (condition1) { }
                    });
                }
            }
            """;

        var scores = Scores(source);

        scores[LineOf(source, "void MyMethod2")].Should().Be(
            2,
            "the whitepaper's myMethod2: the lambda adds no increment, but it does raise the nesting level to 1, so the if costs 2");
    }

    // ---- baseline --------------------------------------------------------

    [Fact]
    public async Task Branch_Free_Method_Scores_Zero()
    {
        const string source = "class C { void M() { var x = 1; Use(x); } }";

        var scores = Scores(source);

        scores[LineOf(source, "void M")].Should().Be(
            0,
            "cognitive starts at 0, not cyclomatic's 1 — a method with no flow breaks is not complex, and scoring it 0 is what keeps the threshold meaningful");
    }

    // ---- nesting is the whole point --------------------------------------

    /// <summary>
    /// The claim SNP0034 exists to make, stated as the difference between the two
    /// rules on identical source. Flat branches score the same on both; nested ones
    /// do not.
    /// </summary>
    [Fact]
    public async Task Flat_Branches_Score_Like_Cyclomatic_But_Nested_Ones_Do_Not()
    {
        const string source = """
            class C
            {
                void Flat(bool a, bool b, bool c)
                {
                    if (a) { }
                    if (b) { }
                    if (c) { }
                }

                void Nested(bool a, bool b, bool c)
                {
                    if (a)
                    {
                        if (b)
                        {
                            if (c) { }
                        }
                    }
                }
            }
            """;

        var scores = Scores(source);

        scores[LineOf(source, "void Flat")].Should().Be(3, "three flat ifs: +1 each");
        scores[LineOf(source, "void Nested")].Should().Be(
            6,
            "three nested ifs: +1, +2, +3 — the shape cyclomatic cannot express");
    }

    [Fact]
    public async Task Deeply_Nested_Loops_Accumulate_Nesting_Penalty()
    {
        const string source = """
            class C
            {
                void M()
                {
                    while (a)
                    {
                        for (var i = 0; i < 3; i++)
                        {
                            foreach (var x in y)
                            {
                                if (b) { }
                            }
                        }
                    }
                }
            }
            """;

        var scores = Scores(source);

        scores[LineOf(source, "void M")].Should().Be(
            10,
            "while +1, for +2, foreach +3, if +4 — the mdlr reference gives this shape 10 for the same reason");
    }

    // ---- structural increments -------------------------------------------

    [Fact]
    public async Task Loops_Ternary_And_Switch_Each_Count_One()
    {
        const string source = """
            class C
            {
                void While_(bool a) { while (a) { } }
                void Do_(bool a) { do { } while (a); }
                void For_(int n) { for (var i = 0; i < n; i++) { } }
                void ForEach_(int[] xs) { foreach (var x in xs) { } }
                int Ternary(bool a) => a ? 1 : 0;
                void Switch_(int v) { switch (v) { case 1: break; default: break; } }
            }
            """;

        var scores = Scores(source);

        scores[LineOf(source, "void While_")].Should().Be(1);
        scores[LineOf(source, "void Do_")].Should().Be(1);
        scores[LineOf(source, "void For_")].Should().Be(1);
        scores[LineOf(source, "void ForEach_")].Should().Be(1);
        scores[LineOf(source, "int Ternary")].Should().Be(1);
        scores[LineOf(source, "void Switch_")].Should().Be(1);
    }

    /// <summary>
    /// Rule 1 of the metric: shorthand structures are not charged per-statement.
    /// A five-arm switch costs 1, where SNP0033 would charge 5.
    /// </summary>
    [Fact]
    public async Task Switch_Costs_One_Regardless_Of_Arm_Count()
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
                        case 4: break;
                        case 5: break;
                        default: break;
                    }
                }
            }
            """;

        var scores = Scores(source);

        scores[LineOf(source, "void M")].Should().Be(
            1,
            "five arms still cost one increment — this is the 'ignore structures that shorthand multiple statements' rule");
    }

    [Fact]
    public async Task Catch_Costs_One_And_A_Filter_Is_Not_A_Second_Catch()
    {
        const string source = """
            class C
            {
                void Plain() { try { } catch (Exception) { } }
                void Filtered(int v) { try { } catch (Exception) when (v > 0) { } }
            }
            """;

        var scores = Scores(source);

        scores[LineOf(source, "void Plain")].Should().Be(1);
        scores[LineOf(source, "void Filtered")].Should().Be(
            1,
            "deliberate divergence: SonarSource charges the filter separately, but a filtered catch reads as one branch");
    }

    [Fact]
    public async Task Bare_Else_Costs_One_And_ElseIf_Is_Not_An_Else_Plus_An_If()
    {
        const string source = """
            class C
            {
                void Bare(bool a) { if (a) { } else { } }
                void Chain(int v)
                {
                    if (v == 1) { }
                    else if (v == 2) { }
                    else if (v == 3) { }
                }
            }
            """;

        var scores = Scores(source);

        scores[LineOf(source, "void Bare")].Should().Be(
            2,
            "this is where SNP0034 and SNP0033 disagree by design: cyclomatic gives a bare else 0, cognitive gives it 1");
        scores[LineOf(source, "void Chain")].Should().Be(
            3,
            "three ifs; an else-if is one if, not an else wrapping an if");
    }

    // ---- boolean operator sequences --------------------------------------

    [Fact]
    public async Task A_Chain_Of_Like_Operators_Costs_One_Not_One_Each()
    {
        const string source = """
            class C
            {
                bool And(bool a, bool b, bool c, bool d) => a && b && c && d;
                bool Or(bool a, bool b, bool c, bool d) => a || b || c || d;
            }
            """;

        var scores = Scores(source);

        scores[LineOf(source, "bool And")].Should().Be(
            1,
            "four short-circuiting operators are one sequence — this is the metric's discount against cyclomatic");
        scores[LineOf(source, "bool Or")].Should().Be(1);
    }

    /// <summary>
    /// Mixed operator kinds each open a new sequence, which is the whole point of the
    /// rule: <c>a &amp;&amp; b &amp;&amp; c || d || e</c> parses as
    /// <c>(a &amp;&amp; b &amp;&amp; c) || (d || e)</c>, so a pre-order walk meets
    /// <c>||</c>, <c>||</c>, <c>&amp;&amp;</c>, <c>&amp;&amp;</c> — two runs, not one
    /// and not four.
    /// <para>
    /// The 1.7.x research note recorded this example as costing 3. It costs 2: the
    /// "+3" in the whitepaper's figure annotates the enclosing <c>if</c> line, which is
    /// +1 for the <c>if</c> plus these 2. This test is the first place the note and the
    /// implementation disagree, so it is the place to look if that reading is wrong.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Mixed_Operators_Cost_One_Each_Time_They_Change()
    {
        const string source = """
            class C
            {
                bool Chain(bool a, bool b, bool c, bool d, bool e) => a && b && c || d || e;
                bool InsideIf(bool a, bool b, bool c, bool d, bool e)
                {
                    if (a && b && c || d || e) { return true; }
                    return false;
                }
            }
            """;

        var scores = Scores(source);

        scores[LineOf(source, "bool Chain")].Should().Be(
            2,
            "two runs of like operators — the && group and the || group. Note the || is met first in a pre-order walk, so it opens the first sequence");
        scores[LineOf(source, "bool InsideIf")].Should().Be(
            3,
            "the same chain at 2, plus 1 for the if: this is the total the whitepaper's figure annotates");
    }

    [Fact]
    public async Task Boolean_Sequence_Inside_A_Condition_Is_Still_One_Sequence()
    {
        const string source = """
            class C
            {
                void M(bool a, bool b, bool c)
                {
                    if (a && b && c) { }
                }
            }
            """;

        var scores = Scores(source);

        scores[LineOf(source, "void M")].Should().Be(
            2,
            "the if is +1 and its condition is one operator sequence, which is charged flat — nesting does not apply to conditions");
    }

    // ---- scope boundaries -------------------------------------------------

    [Fact]
    public async Task Lambda_Is_Scored_Inside_Its_Parent_Not_As_Its_Own_Method()
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

        var scores = Scores(source);

        // The assertion that matters: the lambda's body belongs to Parent at nesting 1,
        // and the lambda is not scored as a method in its own right. Reporting both would
        // put one construct in the report twice under two numbers that are not the same
        // metric — a single entry here is the proof it does not.
        scores.Should().ContainSingle(
            "the lambda is part of Parent's score, not a second scoring unit");
        scores[LineOf(source, "void Parent")].Should().Be(4, "two ifs at nesting 1: +2 each");
    }

    [Fact]
    public async Task Local_Function_Is_Scored_Inside_Its_Parent()
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

        var scores = Scores(source);

        // A local function *is* its own body and *is* scored on its own — it is a named
        // method, not an anonymous one. What differs is that its branches also count
        // towards Parent at nesting 1, so both numbers are real and neither is double
        // counting: Parent's is the nesting-inclusive total, Inner's is the standalone one.
        scores.Should().HaveCount(2);
        scores[LineOf(source, "void Parent")].Should().Be(2, "one if inside Inner, which sits at nesting 1");
        scores[LineOf(source, "void Inner")].Should().Be(1, "scored standalone, at nesting 0");
    }

    [Fact]
    public async Task Nested_Lambdas_Accumulate_Nesting_Level()
    {
        const string source = """
            class C
            {
                void M()
                {
                    Run(() => Run(() =>
                    {
                        if (a) { }
                    }));
                }
            }
            """;

        var scores = Scores(source);

        scores[LineOf(source, "void M")].Should().Be(3, "two lambdas raise the nesting level to 2, so the if costs 3");
    }

    // ---- deliberate divergences from the spec -----------------------------

    /// <summary>
    /// Recursion cycles and preprocessor conditionals are documented non-counts in
    /// the class summary. Pinning them here is what stops the divergence from
    /// being an accident that a future edit quietly fixes or breaks.
    /// </summary>
    [Fact]
    public async Task Recursion_And_Preprocessor_Conditionals_Are_Not_Counted()
    {
        const string source = """
            class C
            {
                int Factorial(int n)
                {
                    if (n <= 1) { return 1; }
                    return n * Factorial(n - 1);
                }
            }
            """;

        var scores = Scores(source);

        scores[LineOf(source, "int Factorial")].Should().Be(
            1,
            "the recursive call is not a +1: counting it needs a solution-wide call graph, which clang-tidy also declines");
    }

    [Fact]
    public async Task Try_Finally_Throw_And_Plain_Return_Do_Not_Count()
    {
        const string source = """
            class C
            {
                int M(int v)
                {
                    try
                    {
                        if (v == 0) { throw new Exception(); }
                        return 1;
                    }
                    finally
                    {
                        Cleanup();
                    }
                }
            }
            """;

        var scores = Scores(source);

        scores[LineOf(source, "int M")].Should().Be(1, "only the if; try/finally/throw/return are control flow, not flow breaks");
    }

    // ---- threshold and finding shape --------------------------------------

    [Fact]
    public async Task Threshold_Is_Respected()
    {
        const string source = "class C { void M(bool a) { if (a) { } if (a) { } if (a) { } } }";

        (await ReportedLinesAsync(source, 3)).Should().BeEmpty();
        (await ReportedLinesAsync(source, 2)).Should().ContainKey(LineOf(source, "void M"));
    }

    [Fact]
    public async Task Finding_Shape_Is_Advisory_In_The_Complexity_Category()
    {
        const string source = "class C { void M(bool a) { if (a) { } if (a) { } } }";

        var findings = await AnalyzeAsync(source, 1);

        var finding = findings.Should().ContainSingle(f => f.RuleId == "SNP0034").Subject;
        finding.Certainty.Should().Be(CertaintyTier.Advisory);
        finding.Category.Should().Be(FindingCategory.Complexity);
        finding.Title.Should().Be("High Cognitive Complexity");
        finding.Message.Should().Contain("complexity 2");
        finding.Message.Should().Contain("SNP0033",
            "the message must tell the reader that this metric disagrees with SNP0033 by design");
    }

    [Fact]
    public async Task Non_Positive_Threshold_Falls_Back_To_The_Default()
    {
        const string source = """
            class C
            {
                void Nested(bool a, bool b, bool c)
                {
                    if (a) { if (b) { if (c) { } } }
                }
            }
            """;

        // Scores 6, so an explicit threshold of 1 reports it and the default of 15 does not.
        (await ReportedLinesAsync(source, 1)).Should().ContainKey(LineOf(source, "void Nested"));

        var findings = await AnalyzeAsync(source, -5);
        findings.Should().BeEmpty("a non-positive threshold falls back to the default, not to zero");
    }
}