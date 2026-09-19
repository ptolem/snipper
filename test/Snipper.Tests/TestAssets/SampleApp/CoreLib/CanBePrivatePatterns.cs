namespace CoreLib;

// SNP0024 can-be-private: a member referenced only from inside its own
// containing type (and not on exported API surface) can drop to private.
// Exercised from App/Worker.cs.

internal class CanBePrivateScenarios
{
    // Positive — public, but only Exercise calls it.
    public string PublicButSelfUsed() => "self";

    // Positive — internal, only Exercise calls it.
    internal string InternalButSelfUsed() => "internal";

    // Positive — internal static, only Exercise calls it.
    internal static string InternalStaticSelfUsed() => "static";

    // Positive — read and written only inside this type.
    public int PublicSelfUsedProperty { get; set; }

    // Negative — virtual: accessibility is part of the extensibility contract.
    public virtual string PublicVirtualSelfUsed() => "virtual";

    // Negative — called from CanBePrivateConsumer (outside this type).
    public string PublicAndExternallyUsed() => "external";

    // Negative — never called at all: belongs to SNP0005/0006, not tightening.
    public string PublicButUnused() => "unused";

    public void Exercise()
    {
        _ = PublicButSelfUsed();
        _ = InternalButSelfUsed();
        _ = InternalStaticSelfUsed();
        PublicSelfUsedProperty++;
        _ = PublicSelfUsedProperty;
        _ = PublicVirtualSelfUsed();
    }
}

internal static class CanBePrivateConsumer
{
    public static string Consume() => new CanBePrivateScenarios().PublicAndExternallyUsed();
}

// Serialization evidence: a DTO-registered type's members are framework-invoked.
// WireValue is read only inside the type, yet must not be tightened.
[JsonSerializable(typeof(CanBePrivateDto))]
internal static class CanBePrivateDtoContext;

internal class CanBePrivateDto
{
    public string? WireValue { get; set; }

    public string Touch() => WireValue ?? string.Empty;
}
