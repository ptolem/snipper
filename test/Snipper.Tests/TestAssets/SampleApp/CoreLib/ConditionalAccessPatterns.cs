namespace CoreLib;

// Regression scenarios for the conditional-access speculation crash
// (SNP0022/SNP0025): invocations under ?. chains must never reach Roslyn's
// speculative binder — it throws NullReferenceException on MemberBindingExpression.
// Exercised from App/Worker.cs. Member names are deliberately distinctive.

public sealed class ConditionalHelper
{
    private readonly int _salt = 11;

    public T EchoViaConditional<T>(T value) => value;

    public int Probe() => _salt;
}

public sealed class ConditionalReceiver
{
    public ConditionalHelper Helper { get; } = new ConditionalHelper();

    public int GreetViaConditional(string text, int times = 2) => text.Length + times;
}

public static class ConditionalAccessScenarios
{
    public static int Exercise(ConditionalReceiver? receiver)
    {
        // SNP0022 crash shape: a trailing default-matching literal argument on an
        // invocation whose own expression is the ?. member binding.
        var greeted = receiver?.GreetViaConditional("hi", 2);

        // SNP0025 crash shape: a generic invocation whose member-access chain
        // bottoms out in a member binding (.Helper).
        var echoed = receiver?.Helper.EchoViaConditional<int>(5);

        // Argument-side shape: the conditional access sits inside the argument
        // subtree of an otherwise plain generic invocation.
        var plain = EchoPlain<int>(receiver?.Helper.Probe() ?? 0);

        return (greeted ?? 0) + (echoed ?? 0) + plain;
    }

    private static T EchoPlain<T>(T value) => value;
}
