namespace Snipper.Tests;

using FluentAssertions;
using Snipper.Analysis;
using Xunit;

/// <summary>
/// Environment variables are process-global and these tests mutate them, so the
/// class is serialized against every other test in the assembly.
/// </summary>
[Collection(nameof(AnalysisParallelismShould))]
public sealed class AnalysisParallelismShould
{
    [Fact]
    public void Return_A_Maximum_Of_At_One_And_A_Cancellation_Token_For_CreateOptions()
    {
        var token = new CancellationTokenSource().Token;

        var options = AnalysisParallelism.CreateOptions(token);

        options.MaxDegreeOfParallelism.Should().BeGreaterThanOrEqualTo(1);
        options.CancellationToken.Should().Be(token);
    }

    [Fact]
    public void Honour_SNIPPER_MAX_DOP_For_CreateOptions()
    {
        // Arrange
        using var scope = new EnvironmentVariableScope("SNIPPER_MAX_DOP", "3");

        // Act
        var options = AnalysisParallelism.CreateOptions(CancellationToken.None);

        // Assert
        options.MaxDegreeOfParallelism.Should().Be(3);
    }

    [Fact]
    public void Return_A_Maximum_Of_One_For_CreateOptions_When_SNIPPER_MAX_DOP_Is_One()
    {
        // Arrange: the documented revert path for constrained agents.
        using var scope = new EnvironmentVariableScope("SNIPPER_MAX_DOP", "1");

        // Act
        var options = AnalysisParallelism.CreateOptions(CancellationToken.None);

        // Assert
        options.MaxDegreeOfParallelism.Should().Be(1);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-4")]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void Fall_Back_To_The_Default_Width_For_CreateOptions_When_SNIPPER_MAX_DOP_Is_Unusable(string configured)
    {
        // Arrange
        using var scope = new EnvironmentVariableScope("SNIPPER_MAX_DOP", configured);

        // Act
        var options = AnalysisParallelism.CreateOptions(CancellationToken.None);

        // Assert
        options.MaxDegreeOfParallelism.Should().Be(Environment.ProcessorCount);
    }

    /// <summary>
    /// The fan-out nests inside each analyser's own inner loops, so its width is a
    /// separate knob from the per-pass width. These tests pin the default relationship
    /// and the override, which together are the tuning surface for constrained agents.
    /// </summary>
    [Fact]
    public void Default_The_FanOut_Width_To_The_PerPass_Width_For_CreateFanOutOptions()
    {
        var options = AnalysisParallelism.CreateFanOutOptions(CancellationToken.None);

        options.MaxDegreeOfParallelism.Should().Be(Environment.ProcessorCount);
    }

    [Fact]
    public void Honour_SNIPPER_FANOUT_DOP_For_CreateFanOutOptions()
    {
        // Arrange
        using var scope = new EnvironmentVariableScope("SNIPPER_FANOUT_DOP", "2");

        // Act
        var options = AnalysisParallelism.CreateFanOutOptions(CancellationToken.None);

        // Assert
        options.MaxDegreeOfParallelism.Should().Be(2);
    }

    [Fact]
    public void Return_A_Maximum_Of_One_For_CreateFanOutOptions_When_SNIPPER_MAX_DOP_Is_One()
    {
        // Arrange: a fully sequential run must not be defeated by the fan-out default.
        using var scope = new EnvironmentVariableScope("SNIPPER_MAX_DOP", "1");

        // Act
        var options = AnalysisParallelism.CreateFanOutOptions(CancellationToken.None);

        // Assert
        options.MaxDegreeOfParallelism.Should().Be(1);
    }

    [Fact]
    public void Carry_The_Cancellation_Token_For_CreateFanOutOptions()
    {
        var token = new CancellationTokenSource().Token;

        var options = AnalysisParallelism.CreateFanOutOptions(token);

        options.CancellationToken.Should().Be(token);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _original);
        }
    }
}
