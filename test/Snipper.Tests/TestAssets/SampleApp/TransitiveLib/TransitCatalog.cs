namespace TransitiveLib;

// SNP0003/SNP0004 transitive-flow scenario: this assembly never receives a
// direct reference from App — it arrives through FacadeLib alone.
public static class TransitCatalog
{
    public static string Describe() => "transit";
}
