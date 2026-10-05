namespace CoreLib;

// App exercises several internal fixtures in this project, and `internal` does not cross
// an assembly boundary. The obvious fix - declaring App a friend assembly of CoreLib -
// is wrong for this fixture: HierarchyDeadCodeAnalyser demotes every finding on a type
// that has friend assemblies from Moderate to Advisory, on the reasoning that external
// code could then inherit from it. That silently rewrote the expected certainty of the
// SNP0023 and SNP0005 scenarios across the whole project (HasFriendAssemblies,
// HierarchyDeadCodeAnalyser). So the cross-assembly reach goes through the public entry
// points below and CoreLib keeps no friend assemblies.

/// <summary>
/// Same shape as App's stand-in. <c>DiRegistrationScanner</c> matches the method name, so
/// the registration is found wherever it lives - and finding it here is more honest than
/// finding it in a consumer, because the container being described is this assembly's.
/// </summary>
public static class ServiceCollectionShim
{
    public static void AddSingleton<TService>() { }
}

public static class InternalFixtureBridge
{
    /// <summary>
    /// SNP0024: a reference from outside the declaring type, so
    /// <c>CanBePrivateScenarios.PublicAndExternallyUsed</c> must not be tightened.
    /// </summary>
    public static int ConsumeCanBePrivate() => CanBePrivateConsumer.Consume().Length;

    public static void ExerciseCanBePrivate() => new CanBePrivateScenarios().Exercise();

    /// <summary>
    /// SNP0006 demotion: registering <c>InternalRegisteredService</c> must drop its member
    /// to Advisory where the unregistered <c>InternalHelper.UnusedInternalMethod</c> stays
    /// Moderate.
    /// </summary>
    public static void RegisterInternalService() =>
        ServiceCollectionShim.AddSingleton<InternalRegisteredService>();

    /// <summary>
    /// SNP0023: <c>UsedFamilyMethod</c> is overridden but called through both the root and
    /// the derived type, so neither is dead. The call has to originate outside the family to
    /// count as an external caller - which is why this lives here and not in Worker.cs.
    /// </summary>
    public static string UseFamilyRoot() => new FamilyRoot().UsedFamilyMethod();

    public static string UseFamilyDerived() => new FamilyDerived().UsedFamilyMethod();

    /// <summary>
    /// Subscription and raise from a consumer, so the event members have real references.
    /// </summary>
    public static void ExerciseEvents()
    {
        var eventScenarios = new EventScenarios();
        EventConsumer.Subscribe(eventScenarios);
        eventScenarios.Fire();
    }
}
