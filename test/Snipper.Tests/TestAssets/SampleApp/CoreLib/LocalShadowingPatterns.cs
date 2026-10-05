namespace CoreLib;

/// <summary>
/// Shadowing patterns for the document-confined reference test (SNP0009/SNP0010).
/// The textual name always appears in these methods, so every case below takes the
/// slow path: the analyser must bind the identifier and confirm it resolves to the
/// candidate symbol, not merely that the name is spelled somewhere in the file.
/// </summary>
public sealed class LocalShadowingPatterns
{
    /// <summary>
    /// Inner <c>shadowed</c> is read; the outer one is not. Only a local function's parameter
    /// may shadow an enclosing local (a block, loop or catch is CS0136), so the analyser must bind.
    /// </summary>
    public int OuterShadowedIsUnused(int value)
    {
        var shadowed = value;
        int Inner(int shadowed) => shadowed;
        return Inner(value * 2);
    }

    /// <summary>Unread local whose name is spelled only inside a string literal.</summary>
    public int NameOnlyInStringLiteral(int value)
    {
        var literal = value;
        _ = "literal";
        return 0;
    }

    /// <summary>
    /// Unread local whose name appears in a usage position that binds to an
    /// unrelated type's member — textual presence alone would wrongly call it read.
    /// </summary>
    public int NameSpelledOnForeignType(int value)
    {
        var member = new ForeignHolder();
        _ = new ForeignHolder().member;
        return value;
    }

    private sealed class ForeignHolder
    {
        public int member { get; set; }
    }

    /// <summary>Deconstruction where only the first element is read.</summary>
    public int PartialDeconstruction(int value)
    {
        var (read, unread) = (value, value + 1);
        return read;
    }

    private int UnusedParameters(int usedParam, int unusedParam) => usedParam;

    private int CapturedParameterIsRead(int captured)
    {
        Func<int> read = () => captured * 2;
        return read();
    }
}
