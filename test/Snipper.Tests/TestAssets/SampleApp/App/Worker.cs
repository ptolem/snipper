namespace App;

using CoreLib;
using static CoreLib.ViaUsingStatic;

// Minimal stand-ins for the framework contracts Snipper recognizes by name,
// so the fixture needs no real framework packages to exercise DI/options rules.
public interface IOptions<T>
{
    T Value { get; }
}

public static class ServiceCollection
{
    public static void AddSingleton<TService, TImplementation>() { }
    public static void AddSingleton<TService>() { }
    public static void AddOptions<TOptions>() { }
}

public static class Worker
{
    public static int Run()
    {
        ServiceCollection.AddSingleton<IGreeter, Greeter>();
        ServiceCollection.AddSingleton<InternalRegisteredService>();
        ServiceCollection.AddOptions<AppOptions>();
        ServiceCollection.AddOptions<Excluded.Fake.ExcludedOptions>();

        IGreeter greeter = new Greeter();
        _ = greeter.Greet("world");

        IOptions<AppOptions>? options = null;
        _ = options;

        // Options type from a metadata assembly (stand-in for options bound in a
        // referenced library/package): must still suppress SNP0007 for its keys.
        IOptions<System.Text.Json.JsonSerializerOptions>? jsonOptions = null;
        _ = jsonOptions;

        var api = new PublicApi();
        _ = "hello".Shout();
        _ = Triple(2);
        _ = JsonRoundTrip.Echo("ping");
        _ = new LegacyHelper().StillUsedApi();
        _ = new WriteOnlyFieldScenarios(21).Exercise(3);
        _ = RedundantInvocationScenarios.ExerciseRedundancies();
        _ = RedundantTypeArgScenarios.ExerciseTypeArgs();
        _ = HierarchyScenarios.Exercise();
        _ = TighteningExercise.Run(3);
        _ = RedundantCastScenarios.Exercise();
        _ = UpcastScenarios.Exercise();
        _ = UpcastScenarios.ReturnUpcast();
        _ = ConditionalAccessScenarios.Exercise(new ConditionalReceiver());

        // Plugin host loads this assembly by name at runtime — name evidence for SNP0011.
        _ = "PluginLib";

        // Both Serilog packages used: SNP0003 stays silent, SNP0012 flags the direct edge.
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().WriteTo.Console().CreateLogger();

        // Type from Microsoft.Extensions.Logging.Abstractions — pulled in transitively by
        // the direct Microsoft.Extensions.Logging.Console reference. Keeps that package's
        // subtree load-bearing even though its own assembly is never touched.
        Microsoft.Extensions.Logging.ILogger? logger = null;
        _ = logger;

        return api.UsedByApp(21);
    }
}
