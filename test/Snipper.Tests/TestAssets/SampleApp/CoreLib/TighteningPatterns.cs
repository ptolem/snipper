namespace CoreLib;

// SNP0024 scenarios: tightening invitations (Advisory). Everything is exercised
// from App/Worker.cs via TighteningExercise.Run. Member names are deliberately
// distinctive — FluentAssertions assertions are substring-based across the
// whole fixture, and every field is read at least once so SNP0001/SNP0021
// stay silent (their findings, not ours).

public sealed class TighteningScenarios
{
    private readonly int _seed;

    // Written only in the ctor and read afterwards → can be readonly → SNP0024.
    private int _ctorOnly;

    // Written in a method as well → readonly rejected.
    private int _methodWritten;

    // Passed by ref → readonly rejected.
    private int _refPassed;

    // Volatile can never be readonly (CS0678).
    private volatile int _volatileField;

    public TighteningScenarios(int seed)
    {
        _seed = seed;
        _ctorOnly = seed;
        _methodWritten = seed;
        _refPassed = seed;
    }

    // Params-only, touches no instance state → can be static → SNP0024.
    public int ParamsOnly(int a, int b) => a + b;

    // Touches instance state → not flagged.
    public int TouchesState(int a) => a + _seed;

    // Carries an attribute → fixed-signature hooks are never flagged.
    [LifecycleHook]
    public int AttributedHook(int x) => x;

    public int Exercise(int seed)
    {
        _methodWritten = seed;
        BumpRef(ref _refPassed);
        _volatileField = seed;
        return _ctorOnly + _methodWritten + _refPassed + _volatileField
            + ParamsOnly(1, 2) + TouchesState(seed) + AttributedHook(seed);
    }

    private static void BumpRef(ref int value) => value += 1;
}

// A primary-constructor parameter used inside an instance method is NOT a
// field symbol, so GetSymbolInfo on the bare name surfaces an IParameterSymbol
// (or nothing) and the instance-state bail-out finds nothing. The method uses
// captured state — it cannot be static. `PrimaryCtorCapture` must not be
// flagged; `PrimaryCtorUnrelated` is the negative control: same class, a
// primary constructor exists, but this method touches none of its parameters.
public sealed class PrimaryCtorCapture(int seed, string name)
{
    private readonly int _field = seed;

    // Reads `name`, a primary-constructor parameter → captures instance state.
    public int LogName() => name.Length;

    // Reads nothing from the primary constructor → genuinely stateless.
    public int ComputeValue(int a, int b) => a * b + 1;

    public int ReadField() => _field + LogName() + ComputeValue(2, 3);
}

// Stand-in for a lifecycle/callback attribute: any attribute on a method
// excludes it from can-be-static (serialization callbacks, framework hooks).
public sealed class LifecycleHookAttribute : Attribute
{
}

// Interface implementations dispatch through the contract → never flagged.
internal interface ITighteningContract
{
    int ContractValue(int x);
}

internal sealed class TighteningContractImpl : ITighteningContract
{
    public int ContractValue(int x) => x * 2;
}

// Virtual members are extension points → never can-be-static candidates. The
// derived override keeps SNP0023 silent for both class and member.
internal class TighteningVirtualBase
{
    public virtual int StatelessVirtual(int x) => x;
}

internal sealed class TighteningVirtualDerived : TighteningVirtualBase
{
    public override int StatelessVirtual(int x) => x + 1;
}

// Internal, unsealed, no derived types, used → can be sealed → SNP0024.
internal class SealableInternal
{
    private readonly int _value = 7;

    public int ReadValue() => _value;
}

// Has a derived class → sealed rejected.
internal class UnsealedWithDerived
{
    public int BasePing() => 1;
}

internal sealed class DerivedFromUnsealed : UnsealedWithDerived
{
}

// Abstract classes cannot be sealed → not candidates.
internal abstract class AbstractTighteningBase
{
    public abstract int AbstractValue();
}

internal sealed class ConcreteTighteningLeaf : AbstractTighteningBase
{
    public override int AbstractValue() => 1;
}

// Already sealed → not a candidate.
internal sealed class AlreadySealedLeaf
{
    public int LeafValue() => 2;
}

public static class TighteningExercise
{
    public static int Run(int seed)
    {
        var scenarios = new TighteningScenarios(seed);
        var total = scenarios.Exercise(seed);
        total += new TighteningContractImpl().ContractValue(seed);
        total += new TighteningVirtualDerived().StatelessVirtual(seed);
        total += new SealableInternal().ReadValue();
        total += new DerivedFromUnsealed().BasePing();
        total += new ConcreteTighteningLeaf().AbstractValue();
        total += new AlreadySealedLeaf().LeafValue();
        total += new PrimaryCtorCapture(1, "n").ReadField();
        return total;
    }
}
