namespace CoreLib;

// Stand-ins for the serialization attributes that demote SNP0021 to Moderate —
// the analyser name-matches them, so the fixture needs no real serializer
// packages (same pattern as FactAttribute in DeadCode.cs).
public sealed class JsonIncludeAttribute : Attribute
{
}

public sealed class DataMemberAttribute : Attribute
{
}

public sealed class JsonPropertyAttribute : Attribute
{
}

// SNP0021 scenarios: private fields that are written but never read. Instantiated
// and exercised from App/Worker.cs so the writers are real, reachable references.
public sealed class WriteOnlyFieldScenarios
{
    // Written in the ctor and again in Exercise, never read → SNP0021 High.
    private int _retryBudget;

    // Readonly, written only in the ctor → SNP0021 High.
    private readonly int _lastSeed;

    // Only ever assigned through an out argument → SNP0021 High.
    private int _outOnly;

    // Written, then referenced only by nameof — which proves nothing about value
    // flow → SNP0021 High.
    private int _namedOnly;

    // Written-never-read but serialization-attributed → SNP0021 Moderate (demoted).
    [JsonInclude]
    private int _jsonInclude;

    [DataMember]
    private int _dataMember;

    [JsonProperty]
    private int _jsonProperty;

    // Compound assignment reads the old value → read+write → not flagged.
    private int _compound;

    // Increment reads the old value → read+write → not flagged.
    private int _counter;

    // Passed by ref → read+write → not flagged.
    private int _byRef;

    // Written and read → not flagged.
    private int _read;

    // Zero references → SNP0001 territory, never SNP0021.
    private int _neverTouched;

    public WriteOnlyFieldScenarios(int seed)
    {
        _retryBudget = seed;
        _lastSeed = seed;
    }

    // Private auto-property: its backing field is implicit and must never surface
    // as an SNP0021 candidate. Write-only here so no other rule reports it either.
    private int AutoProp { get; set; }

    public int Exercise(int seed)
    {
        _retryBudget = seed;
        FillOut(out _outOnly);
        _namedOnly = seed;
        _ = nameof(_namedOnly);
        _jsonInclude = seed;
        _dataMember = seed;
        _jsonProperty = seed;
        _compound += seed;
        _counter++;
        Bump(ref _byRef);
        _read = seed;
        AutoProp = seed;
        return _read;
    }

    private static void FillOut(out int value) => value = 42;

    private static void Bump(ref int value) => value += 1;
}
