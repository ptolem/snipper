namespace CoreLib;

// Attribute-class fixtures (1.6.2):
//  - SNP0005/0006: applications omit the "Attribute" suffix, so the textual
//    usage index only ever sees the short spelling — an applied attribute class
//    must not be flagged.
//  - SNP0023: attribute classes are terminal by convention, so virtual members
//    on them are not speculative extension points.

[AttributeUsage(AttributeTargets.Method)]
internal sealed class FwMarkerAttribute : Attribute;

[AttributeUsage(AttributeTargets.Class)]
public sealed class FwAppliedPublicAttribute : Attribute;

// SNP0023 exclusion target: declares a virtual member and is never inherited,
// yet must not be flagged. The typeof reference keeps the usage gate satisfied
// independently of the attribute-application reference fix.
[AttributeUsage(AttributeTargets.Property)]
public class FwPrefixAttribute : Attribute
{
    public virtual string FwPrefix => "fw";
}

internal static class FwAttributeConsumer
{
    // The full-name reference (the milkrun GetCustomAttributes(typeof(...)) shape).
    internal static readonly Type FwPrefixTypeReference = typeof(FwPrefixAttribute);

    // Unused member — stays flagged; the attribute on it must not be.
    [FwMarker]
    public static string FwDecoratedLogic() => "x";

    // Unused member — stays flagged; carries the SNP0023-excluded attribute.
    [FwPrefix]
    public static string FwDecoratedSetting => "y";
}

// Unused type — stays flagged; the attribute on it must not be.
[FwAppliedPublic]
internal static class FwAttributeAnnotatedType;
