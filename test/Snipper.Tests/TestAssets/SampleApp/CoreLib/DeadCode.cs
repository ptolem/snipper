namespace CoreLib;

public sealed class PublicApi
{
    private int _unusedPrivateField = 99;
    private readonly int _usedPrivateField = 7;

    public int UsedByApp(int value)
    {
        return value * 2 + _usedPrivateField;
    }

    public int UnusedPublicMethod(int value)
    {
        return value + 1;
    }

    public int UnreachableAfterReturn(int value)
    {
        return value;
        var dead = value * 3;
    }

    private int MultiplyUnused(int a, int b)
    {
        return a * b;
    }
}

internal sealed class InternalHelper
{
    public static int UnusedInternalMethod(int value)
    {
        return value - 1;
    }
}

internal sealed class UnusedInternalType
{
    public void Nothing() { }
}

internal sealed class InternalRegisteredService
{
    public int UnusedButRegistered() => 5;
}

// Stand-in for the xUnit [Fact] attribute — the exclusion engine matches by name,
// so the fixture needs no real test-framework package.
public sealed class FactAttribute : Attribute { }

public sealed class SampleTests
{
    [Fact]
    public void It_Works() { }

    public int UnusedTestHelper() => 3;
}

public interface IGreeter
{
    string Greet(string name);
}

public sealed class Greeter : IGreeter
{
    public string Greet(string name)
    {
        return $"Hello, {name}!";
    }
}

public sealed class LocalFunctionPatterns
{
    public int UsesBottomHelper(int value)
    {
        return BottomHelper(value);
        int BottomHelper(int x) => x + 1;
    }

    public int WithUncalledBottomHelper(int value)
    {
        return value;
        int UncalledBottomHelper(int x) => x * 2;
    }
}

public interface IGraphQueryContract<TResponse>
{
    string JsonTypeInfo => "default";
}

public sealed class MyCartQuery : IGraphQueryContract<MyCartResponse>
{
    string IGraphQueryContract<MyCartResponse>.JsonTypeInfo => "explicit";
}

public sealed class MyCartResponse { }

public sealed class LocalFunctionEdgeCases
{
    public int StaticLocalAfterReturn(int value)
    {
        return value;
        static int StaticHelper(int x) => x * 3;
    }

    public int StaticLocalAfterThrow(int value)
    {
        throw new InvalidOperationException();
        static int StaticHelper(int x) => x * 3;
    }

    public int StaticLocalInTryCatch(int value)
    {
        try
        {
            return value;
            static int TryHelper(int x) => x + 1;
        }
        catch (Exception)
        {
            return -1;
            static int CatchHelper(int x) => x - 1;
        }
    }

    public int LabeledStaticLocalAfterReturn(int value)
    {
        return value;
        AfterReturn: static int LabeledHelper(int x) => x * 3;
    }
}

public sealed class UnusedLocalAndParameterPatterns
{
    public int EntryPoint(int value)
    {
        var helperResult = LocalHelper(value, 10, 20);
        Action<object, int> handler = GroupReferenceHandler;
        handler(this, 1);
        return helperResult;
    }

    private int LocalHelper(int usedParam, int unusedParam, int anotherUnusedParam)
    {
        var usedLocal = usedParam * 2;
        var unusedLocal = 42;
        _ = int.TryParse("1", out var parsedOut);
        _ = int.TryParse("2", out var usedOut);
        var (usedPart, unusedPart) = (1, 2);
        using var stream = new System.IO.MemoryStream();
        var _ = 5;
        _ = int.TryParse("3", out _);
        return usedLocal + usedPart + usedOut;
    }

    private void GroupReferenceHandler(object sender, int count)
    {
    }
}
