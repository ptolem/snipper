namespace CoreLib;

// SNP0029: empty parameterless constructors (the compiler synthesizes an
// identical one) and empty destructors (they only force finalization
// overhead). Exercised from App/Worker.cs.

public class EmptyCtorScenario
{
    // Positive — public, parameterless, empty; the synthesized ctor is identical.
    public EmptyCtorScenario()
    {
    }
}

public class EmptyDtorScenario
{
    public EmptyDtorScenario(int value) => _ = value;

    // Positive — empty finalizer.
    ~EmptyDtorScenario()
    {
    }
}

public class LoadBearingCtorScenario
{
    // Negative — private: it prevents external instantiation.
    private LoadBearingCtorScenario()
    {
    }

    public static LoadBearingCtorScenario Create() => new();
}

public class NonEmptyCtorScenario
{
    // Negative — has a body.
    public NonEmptyCtorScenario() => _ = 42;
}

public static class StaticCtorScenario
{
    // Negative — a static ctor (even empty) forces beforefieldinit semantics.
    static StaticCtorScenario()
    {
    }
}
