namespace Snipper.Tests;

using FluentAssertions;
using Microsoft.CodeAnalysis;
using System.Globalization;
using Xunit;

/// <summary>
/// The shared SampleApp fixture must compile.
///
/// Every analyser test in this suite runs against this solution, and Snipper reports
/// findings rather than requiring a clean build - so a broken fixture degrades silently.
/// Semantic models stop binding, framework-evidence resolution goes partial, and
/// assertions written against the intended shape quietly end up testing something else.
/// CoreLib declared <c>JsonIncludeAttribute</c> in two files (CS0101) for long enough
/// that no test noticed, which is the failure mode this test exists to close.
///
/// This is the only place in the suite that asserts on the fixture *building* rather
/// than on what Snipper reports, and it is deliberately the broadest assertion here.
/// </summary>
[Collection("SampleSolution")]
public sealed class FixtureBuildShould(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Produce_No_Compilation_Errors_For_AnalyzeAsync()
    {
        var errors = new List<string>();

        foreach (var project in fixture.Solution.Projects.Where(static p => p.SupportsCompilation))
        {
            var compilation = await project.GetCompilationAsync(CancellationToken.None);
            if (compilation is null)
            {
                errors.Add($"{project.Name}: no compilation could be created");
                continue;
            }

            foreach (var diagnostic in compilation.GetDiagnostics(CancellationToken.None)
                         .Where(static d => d.Severity == DiagnosticSeverity.Error))
            {
                var span = diagnostic.Location.GetLineSpan();
                errors.Add(
                    $"{project.Name}: {diagnostic.Id} "
                    + $"{Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1} "
                    + $"{diagnostic.GetMessage(CultureInfo.InvariantCulture)}");
            }
        }

        errors.Should().BeEmpty("every analyser test in this suite reads this fixture");
    }
}
