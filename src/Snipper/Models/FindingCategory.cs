namespace Snipper.Models;

public enum FindingCategory : byte
{
    UnreachableCode = 1,
    UnusedPrivateMember = 2,
    UnusedInternalMember = 3,
    UnusedPublicMember = 4,
    UnreferencedPackage = 5,
    UnreferencedProject = 6,
    UnusedConfigurationSetting = 7,
    UnusedLocalVariable = 8,
    UnusedParameter = 9,
    OrphanProject = 10,
    RedundantTransitivePackage = 11,
    FrameworkProvidedPackage = 12,
    ObsoleteUnreferencedMember = 13,
    UnusedUsingDirective = 14,
    CommentedOutCode = 15,
    WriteOnlyField = 16,
    RedundantArgument = 17,
    RedundantTypeArguments = 18,
    HierarchyDeadCode = 19,
    Tightening = 20,
    RedundantCast = 21
}
