namespace CoreLib;

// SNP0030: events that are never raised — subscribers sit idle, and an event
// nothing raises is dead surface. Exercised from App/Worker.cs.

public delegate void SimpleEventHandler(string payload);

internal class EventScenarios
{
    // Positive — subscribed (Worker), never raised.
    public event SimpleEventHandler? NeverRaised;

    // Positive — zero references at all.
    public event SimpleEventHandler? NeverUsed;

    // Negative — raised with ?.Invoke.
    public event SimpleEventHandler? Raised;

    // Negative — raised with the direct invocation form.
    public event SimpleEventHandler? RaisedDirectly;

    public void Fire()
    {
        Raised?.Invoke("x");
        if (RaisedDirectly is not null)
        {
            RaisedDirectly("y");
        }
    }
}

internal static class EventConsumer
{
    public static void Subscribe(EventScenarios scenarios)
    {
        scenarios.NeverRaised += HandleNeverRaised;
        scenarios.Raised += HandleRaised;
        scenarios.RaisedDirectly += HandleRaisedDirectly;
    }

    private static void HandleNeverRaised(string payload) => _ = payload;

    private static void HandleRaised(string payload) => _ = payload;

    private static void HandleRaisedDirectly(string payload) => _ = payload;
}
