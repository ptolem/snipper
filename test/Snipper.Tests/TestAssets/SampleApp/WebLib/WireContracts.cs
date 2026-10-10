namespace WebLib.WireContracts;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

// ---------------------------------------------------------------------------
// F3a / F2: minimal-API typed results are wire contracts.
//
// The handler's declared return type IS the response shape the client receives.
// Nothing in C# references these properties, and [Obsolete] does not stop the
// JSON writer - a converter registration does not imply IgnoreReadOnlyProperties.
// ---------------------------------------------------------------------------

/// <summary>
/// Declared on the BASE of the returned type on purpose. A serializer writes
/// every public property along the whole inheritance chain, so a closure that
/// stops at the leaf reaches none of these - which is exactly how milkrun's
/// ListingProductBase, StoreEvent&lt;TArgs&gt; and CommonProps.ProductItemBase
/// went unreached before the base-type walk was added.
/// </summary>
public class RoutePayloadBase
{
    public string Sku { get; set; } = string.Empty;

    [Obsolete("Still shipped to old clients; delete only with a version bump")]
    public string? LegacyBaseField => "legacy";

    [Obsolete("Still shipped to old clients; delete only with a version bump")]
    public bool LegacyBaseFlag => true;
}

public class RoutePayload : RoutePayloadBase
{
    public int Quantity { get; set; }

    [Obsolete("Still shipped to old clients; delete only with a version bump")]
    public string? LegacyLeafField => "legacy";
}

public class RouteResponse
{
    public RoutePayload Payload { get; set; } = new();

    [Obsolete("Still shipped to old clients; delete only with a version bump")]
    public string? LegacyEnvelopeField => "legacy";
}

/// <summary>Bound from the query string by the model binder - also framework-written.</summary>
public class BoundQuery
{
    public string? Term { get; init; }

    [Obsolete("Still bound from the query string; delete only with a version bump")]
    public string? LegacyQueryField => "legacy";
}

/// <summary>
/// Negative control: no route, no serializer call, no binder. These obsolete
/// members really are dead, and the rule must keep saying so.
/// </summary>
public class OrphanPayload
{
    [Obsolete("Genuinely unreferenced")]
    public string? OrphanField => "orphan";

    public string Used { get; set; } = string.Empty;
}

public static class ApiRoutes
{
    public static void MapApi(IEndpointRouteBuilder app)
    {
        app.MapGet("/payload", GetPayload);
    }

    private static Task<Ok<RouteResponse>> GetPayload([AsParameters] BoundQuery query)
    {
        return Task.FromResult(TypedResults.Ok(new RouteResponse()));
    }
}

/// <summary>
/// Negative control for the semantic gate: an application-defined method with a
/// route-shaped name is not ASP.NET route mapping, so its handler's return type
/// must not open a serialization closure.
/// </summary>
public sealed class FakeRouter
{
    public void MapGet(string pattern, Func<string> handler)
    {
        _ = handler();
    }
}

public class FakeRouterResponse
{
    public string Referenced { get; set; } = string.Empty;

    /// <summary>Not obsolete, so SNP0006 can reach it - that is the point of it existing.</summary>
    public string? PlainUnreferenced => "plain";

    [Obsolete("Not a wire contract: FakeRouter.MapGet is not ASP.NET's")]
    public string? FakeLegacyField => "fake";
}

public static class FakeRouterUsage
{
    public static string Call()
    {
        new FakeRouter().MapGet("/x", () => new FakeRouterResponse().Referenced);
        return "called";
    }
}