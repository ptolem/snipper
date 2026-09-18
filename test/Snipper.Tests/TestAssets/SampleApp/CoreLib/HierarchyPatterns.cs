namespace CoreLib;

// SNP0023 scenarios: hierarchy dead code. Instantiated and exercised from
// App/Worker.cs (via HierarchyScenarios.Exercise) so usage gates see real,
// reachable references. Member names are deliberately distinctive —
// FluentAssertions assertions are substring-based across the whole fixture.

// Internal, declares virtuals, never inherited, but used via direct
// instantiation → class-level finding (Moderate); its virtual members are
// suppressed by the root-cause dedup.
internal class NeverInheritedBase
{
    public virtual int VirtualHookOne() => 1;

    public virtual int VirtualHookTwo() => 2;
}

// Inherited, so no class-level finding. SpeculativeHook is a virtual chain
// root nothing overrides → member-level finding (Moderate).
internal class UsedBase
{
    public virtual int SpeculativeHook() => 1;

    public virtual int FulfilledHook() => 2;
}

internal sealed class UsedDerived : UsedBase
{
    public override int FulfilledHook() => 3;
}

// Abstract classes are SNP0005/0006 territory — never SNP0023 candidates.
internal abstract class AbstractChainRoot
{
    public virtual int AbstractRootHook() => 0;
}

internal sealed class ConcreteChainLeaf : AbstractChainRoot
{
}

// Public exported surface: external consumers can inherit → Advisory demotion.
public class ExportedBase
{
    public virtual int ExportedHook() => 1;
}

// Internal, never inherited, used — but its name is spelled in a string
// literal (plugin-loading evidence) → finding suppressed entirely.
internal class PluginLoadedBase
{
    public virtual int PluginHook() => 1;
}

// Zero references: plain dead code (SNP0005), not hierarchy dead code.
internal class UnreferencedVirtualBase
{
    public virtual int OrphanHook() => 1;
}

// Interface implementations are contract dispatch — never member candidates,
// even when declared virtual.
internal interface IHierarchyContract
{
    int ContractMethod();
}

internal class ContractImpl : IHierarchyContract
{
    public virtual int ContractMethod() => 1;
}

internal sealed class ContractImplDerived : ContractImpl
{
}

public static class HierarchyScenarios
{
    public static int Exercise()
    {
        var total = new NeverInheritedBase().VirtualHookOne() + new NeverInheritedBase().VirtualHookTwo();
        var derived = new UsedDerived();
        total += derived.SpeculativeHook() + derived.FulfilledHook();
        total += new ConcreteChainLeaf().AbstractRootHook();
        total += new ExportedBase().ExportedHook();
        total += new PluginLoadedBase().PluginHook();
        total += new ContractImplDerived().ContractMethod();

        // Plugin host loads this type by name at runtime — string-name evidence
        // that suppresses the SNP0023 finding for PluginLoadedBase.
        _ = "PluginLoadedBase";

        return total;
    }
}
