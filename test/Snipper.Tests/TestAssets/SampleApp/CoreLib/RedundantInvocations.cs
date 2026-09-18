namespace CoreLib;

using System.Runtime.CompilerServices;

// SNP0022 scenarios: invocation arguments that repeat the parameter's default
// value. Kept alive through ExerciseRedundancies, called from App/Worker.cs.
// Parameter names are deliberately distinctive — FluentAssertions assertions
// are substring-based across the whole fixture.
public static class RedundantInvocationScenarios
{
    public static int ExerciseRedundancies()
    {
        // Positives: the trailing literal matches the parameter default.
        _ = GreetWithFallback("world", 2);
        _ = LogWithContext("boot", null);
        _ = DispatchWithMode(DispatchMode.Fast);
        _ = BothDefaults(1, 2, 3);

        // Negatives.
        _ = VolumeFor(4);
        _ = GreetByName("world", namedTimes: 2);
        _ = GreetOrShort("world", 2);
        _ = GreetOrShort("world");
        _ = SumAll(1, 2, 3);
        _ = TraceWithCaller("start", "");
        return 0;
    }

    private static string GreetWithFallback(string name, int times = 2) => name + times;

    private static string LogWithContext(string message, string? context = null) => message + context;

    private static string DispatchWithMode(DispatchMode mode = DispatchMode.Fast) => mode.ToString();

    private static int BothDefaults(int first, int second = 2, int third = 3) => first + second + third;

    private static int VolumeFor(int level = 5) => level;

    private static string GreetByName(string name, int namedTimes = 2) => name + namedTimes;

    private static string GreetOrShort(string name, int rebindTimes = 2) => name + rebindTimes;

    private static string GreetOrShort(string name) => name;

    private static int SumAll(params int[] values) => values.Length;

    // Removing the literal looks safe but the caller-info default is injected by
    // the caller — semantics change, so SNP0022 must never fire here.
    private static string TraceWithCaller(string message, [CallerMemberName] string caller = "") => message + caller;
}

public enum DispatchMode
{
    Slow,
    Fast,
}
