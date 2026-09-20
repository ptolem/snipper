namespace CoreLib;

// SNP0026 scenarios: casts the type system proves redundant (identity
// conversions only). Exercised from App/Worker.cs. Target-type names are
// deliberately distinctive — FluentAssertions assertions are substring-based
// across the whole fixture.

public class CastBase
{
}

public sealed class CastDerived : CastBase
{
}

public readonly struct CastWrapper
{
    public static implicit operator long(CastWrapper wrapper) => 42L;
}

public static class RedundantCastScenarios
{
    public static int Exercise()
    {
        // Positives: the operand is already of the target type (identity).
        string alreadyString = "x";
        var sameString = (string)alreadyString;
        var sameStringViaParen = (string)(alreadyString);
        var sameGeneric = EchoCast<string>(alreadyString);
        object alreadyObject = new object();
        ConsumeObject((object)alreadyObject);

        // Negatives: every non-identity shape carries semantics.
        var upcast = (CastBase)new CastDerived();
        CastBase castUp = upcast;
        var down = (CastDerived)castUp;
        var numeric = (int)3.7;
        object boxed = 5;
        var unboxed = (int)boxed;
        var userDefined = (long)new CastWrapper();

        // Negatives: collection expressions have no natural type — the cast IS
        // the target type (stripping makes the expression untargetable, CS9176).
        // Structural, never cosmetic; the parenthesized form included.
        var spreadA = new List<string> { "a" };
        var spreadB = new List<string> { "b" };
        var combinedCount = ((List<string>)[.. spreadA, .. spreadB]).Count;
        List<string> typedTarget = (List<string>)[.. spreadA, .. spreadB];

        _ = down;
        return sameString.Length + sameStringViaParen.Length + sameGeneric.Length + numeric + unboxed + (int)userDefined + combinedCount + typedTarget.Count;
    }

    // (T)value with value already of type T — identity through generics.
    private static T EchoCast<T>(T value) => (T)value;

    private static void ConsumeObject(object value) => _ = value;
}
