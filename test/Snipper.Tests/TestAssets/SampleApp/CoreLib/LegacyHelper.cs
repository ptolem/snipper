namespace CoreLib;

// SNP0018 scenarios: obsolete members with and without references. Existing member
// rules skip obsolete symbols by design, so these produce no SNP0001/0005/0006 noise.
public sealed class LegacyHelper
{
    [Obsolete]
    private void UnusedOldMethod() { }

    [Obsolete("Use the replacement API instead.", error: true)]
    public void RemovedApi() { }

    [Obsolete]
    public void SunsetApi() { }

    [Obsolete]
    public void StillUsedApi() { }
}

// Obsolete interface implementation: invoked through the contract — never flagged.
// (Named without "Greet" so the unused-type SNP0006 finding cannot collide with the
// existing interface-dispatch test's message assertion.)
public sealed class LegacyContractImpl : IGreeter
{
    [Obsolete]
    public string Greet(string name)
    {
        return $"Legacy: {name}";
    }
}
