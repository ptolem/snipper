namespace CoreLib;

// SNP0001 × framework evidence: private members carrying serialization
// attributes are framework-invoked (STJ [JsonInclude] reads and writes
// non-public members). The stand-in attribute keeps the fixture self-contained;
// matching is by simple name. Fw-prefixed names for substring assertions.

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Method)]
internal sealed class JsonIncludeAttribute : Attribute;

internal class FwPrivateAnnotatedContract
{
    // Framework-invoked despite being private — must NOT be flagged.
    [JsonInclude]
    private string? FwPrivateIncludedValue { get; set; }

    // No attribute and no references — MUST be flagged (negative control).
    private string? FwPrivateUnreferencedValue { get; set; }
}
