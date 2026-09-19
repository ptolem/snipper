namespace CoreLib;

// SNP0026 upcast variant: an explicit implicit-reference upcast is redundant,
// but flagging is sound only where stripping it cannot re-bind anything —
// whole-argument positions pass an overload-rebind gate; fixed-target contexts
// (explicitly-typed declaration/assignment, matching return target) are safe by
// shape; var declarations, overload-rebinding arguments, and casts nested in
// larger expressions are never flagged. Exercised from App/Worker.cs.

public class UpcastBase
{
}

public sealed class UpcastDerived : UpcastBase
{
}

public static class UpcastScenarios
{
    public static int Exercise()
    {
        // Positive 1 — explicitly typed declaration.
        UpcastBase viaDeclaration = (UpcastBase)new UpcastDerived();
        _ = viaDeclaration;

        // Positive 2 — assignment to an explicitly typed local.
        UpcastBase viaAssignment = new UpcastDerived();
        viaAssignment = (UpcastBase)new UpcastDerived();
        _ = viaAssignment;

        // Positive 3 — whole argument against a single overload: rebind gate passes.
        ConsumeUpcastBase((UpcastBase)new UpcastDerived());

        // Negative — var: stripping would flip the inferred type.
        var viaVar = (UpcastBase)new UpcastDerived();
        _ = viaVar;

        // Negative — overload rebind: stripping selects the UpcastDerived overload.
        ConsumeOverloaded((UpcastBase)new UpcastDerived());

        // Negative — cast nested inside a larger expression.
        var nestedComparison = ((UpcastBase)new UpcastDerived()) == viaVar;
        _ = nestedComparison;

        return 0;
    }

    // Positive 4 — expression-bodied return with an exact type match.
    public static UpcastBase ReturnUpcast() => (UpcastBase)new UpcastDerived();

    private static void ConsumeUpcastBase(UpcastBase value) => _ = value;

    private static void ConsumeOverloaded(UpcastBase value) => _ = value;

    private static void ConsumeOverloaded(UpcastDerived value) => _ = value;
}
