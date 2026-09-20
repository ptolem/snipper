namespace CoreLib;

public interface IRegistryClient
{
}

public sealed class RegistryClient : IRegistryClient
{
}

public interface IShapeFilter<T>
{
}

public sealed class ShapeMessage
{
}

public sealed class ShapeMessageFilter : IShapeFilter<ShapeMessage>
{
}

// SNP0025 scenarios: explicit method type arguments that type inference would
// infer identically. Kept alive through ExerciseTypeArgs, called from
// App/Worker.cs. Method names are deliberately distinctive — FluentAssertions
// assertions are substring-based across the whole fixture.
public static class RedundantTypeArgScenarios
{
    public static int ExerciseTypeArgs()
    {
        // Positives: inference infers the same type arguments.
        _ = EchoExplicit<int>(5);
        _ = RedundantTypeArgScenarios.EchoViaClass<string>("s");

        // Positives — the milkrun FP-review canonical shapes, verified TRUE
        // positives 1.6.1 against the real Microsoft DI overload set in an
        // isolated repro: output type inference from a block-bodied lambda's
        // return expression, and lower-bound inference through a concrete
        // class's unique matching interface. Both strip cleanly; the gate
        // must keep flagging them.
        RegisterViaFactory<IRegistryClient>(sp =>
        {
            IRegistryClient client = new RegistryClient();
            return client;
        });
        _ = CreateViaUniqueInterface<ShapeMessage>(new object(), new ShapeMessageFilter());

        // Negatives.
        _ = EchoAmbiguous<int>(5);
        _ = EchoAmbiguous(7);
        _ = EchoNoArgs<int>();
        _ = EchoAnnotated<string?>("s");
        return IdentityUninferrable<long>(5);
    }

    private static void RegisterViaFactory<TService>(Func<IServiceProvider, TService> factory)
    {
        _ = factory;
    }

    private static T CreateViaUniqueInterface<T>(object options, IShapeFilter<T> filter)
    {
        _ = options;
        _ = filter;
        return default!;
    }

    private static T EchoExplicit<T>(T value) => value;

    private static T EchoViaClass<T>(T value) => value;

    // Stripping <int> here rebinds to the non-generic overload — a semantic
    // change the speculation gate must catch.
    private static T EchoAmbiguous<T>(T value) => value;

    private static int EchoAmbiguous(int value) => value;

    // No arguments: inference has nothing to work with, so the explicit list
    // is load-bearing.
    private static T EchoNoArgs<T>() => default!;

    // The annotation carries nullability meaning; never strip it.
    private static T EchoAnnotated<T>(T value) => value;

    // T appears nowhere in the parameter list — inference cannot produce it.
    private static long IdentityUninferrable<T>(object value) => value.GetHashCode();
}
