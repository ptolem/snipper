namespace CoreLib;

// SNP0027: an override family whose members reference only each other — the
// chain keeps itself alive but has no external caller. Exercised from App/Worker.cs.

internal class FamilyRoot
{
    // The dead family: root + override + a base. call — and nothing else.
    public virtual string DeadFamilyMethod() => "root";

    // The used family: overridden AND called from Worker through the base.
    public virtual string UsedFamilyMethod() => "used-root";

    // Not virtual — never a candidate.
    public string PlainMethod() => "plain";
}

internal sealed class FamilyDerived : FamilyRoot
{
    public override string DeadFamilyMethod() => base.DeadFamilyMethod() + "-derived";

    public override string UsedFamilyMethod() => "used-derived";
}
