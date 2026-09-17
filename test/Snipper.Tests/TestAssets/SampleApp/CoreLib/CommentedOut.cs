namespace CoreLib;

// SNP0020 fixture: one commented-out code block (flagged), one prose block
// with a TODO marker and a URL (never flagged).
public static class CommentedOutProbe
{
    public static int Live() => 1;

    // var total = orders.Sum(o => o.Price);
    // if (total > 0)
    // {
    //     return total;
    // }

    // This method needs a rewrite for clarity.
    // TODO: split into smaller helpers
    // See https://example.com/docs for background.
}
