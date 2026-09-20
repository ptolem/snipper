namespace CoreLib;

// SNP0020 location fixtures (1.6.2): two commented blocks sharing one anchor
// token must report their own start lines, not the anchor's (1.6.1 pinned both
// blocks of Milkrun.External.ProductDataExporter/LocalHostedService.cs to the
// enclosing method brace).
public static class CommentedOutLocationsProbe
{
    public static int Live()
    {
        var value = 1;

        // var total = value + 1;
        // if (total > 0)
        // {
        //     return total;
        // }

        // var other = value + 2;
        // while (other > 0)
        // {
        //     other--;
        // }

        return value;
    }
}
