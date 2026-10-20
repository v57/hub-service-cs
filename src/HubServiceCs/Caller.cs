namespace HubServiceCs;

// Caller is who is calling, as the hub reports it in `context.hub.caller`
// (Docs/Devices/protocol.md section 4). The hub replaces any `context.hub` a
// client sent, and services accept requests only from the hub connection, so
// handlers can trust it.
public sealed class Caller
{
    // Key is the caller's service SPKI; null for anonymous callers.
    public string? Key { get; init; }

    // DeviceId is the caller's device id; null unless its connection is
    // device-attached.
    public string? DeviceId { get; init; }

    // DeviceName is the caller's device name; null without a device.
    public string? DeviceName { get; init; }

    // Groups is every group the key or its device belongs to.
    public IReadOnlyList<string> Groups { get; init; } = [];

    // Owner is true for members of the owners group.
    public bool Owner { get; init; }

    // Via is the merged hub's SPKI when the request came through a merged hub.
    public string? Via { get; init; }

    // Claimed is the merged hub's own (unverified) caller, through a merged hub.
    public Caller? Claimed { get; init; }

    // Raw is the `caller` object as received.
    public IReadOnlyDictionary<string, object?> Raw { get; init; } = new Dictionary<string, object?>();

    public bool HasDevice => DeviceId is not null;

    public bool ViaMergedHub => Via is not null;

    // FromContext returns the caller in a decoded request context, or null when
    // the hub sent none (hub-lite, older hub-pro).
    public static Caller? FromContext(object? context)
    {
        return From(Hub(context)?.GetValueOrDefault("caller"));
    }

    // From parses a decoded `caller` object, or returns null if it isn't one.
    public static Caller? From(object? value)
    {
        if (value is not Dictionary<string, object?> map)
        {
            return null;
        }

        var device = map.GetValueOrDefault("device") as Dictionary<string, object?>;
        return new Caller
        {
            Key = map.GetValueOrDefault("key") as string,
            DeviceId = device?.GetValueOrDefault("id") as string,
            DeviceName = device?.GetValueOrDefault("name") as string,
            Groups = (map.GetValueOrDefault("groups") as List<object?>)?.OfType<string>().ToList() ?? [],
            Owner = map.GetValueOrDefault("owner") is true,
            Via = map.GetValueOrDefault("via") as string,
            Claimed = From(map.GetValueOrDefault("claimed")),
            Raw = map,
        };
    }

    // HubKey returns the hub's SPKI from a decoded request context
    // (`context.hub.hubKey`), or null.
    public static string? HubKey(object? context)
    {
        return Hub(context)?.GetValueOrDefault("hubKey") as string;
    }

    private static Dictionary<string, object?>? Hub(object? context)
    {
        return (context as Dictionary<string, object?>)?.GetValueOrDefault("hub") as Dictionary<string, object?>;
    }
}

// Request is handed to post and stream handlers registered with a
// (body, request) handler.
public sealed class Request
{
    // Path is the API path called.
    public required string Path { get; init; }

    // Body is the decoded request body.
    public object? Body { get; init; }

    // Caller is who is calling (`context.hub.caller`); null when the hub sent
    // none or when its `context.hub.hubKey` isn't the hub key this service
    // pinned.
    public Caller? Caller { get; init; }

    // HubKey is the forwarding hub's SPKI (`context.hub.hubKey`); null when the
    // hub sent none.
    public string? HubKey { get; init; }

    // Context is the whole decoded request context (selection keys such as
    // `service` and `device` included).
    public IReadOnlyDictionary<string, object?> Context { get; init; } = new Dictionary<string, object?>();

    // Of builds the request for a raw context. The caller is trusted only
    // when the context names pinnedHubKey as its hub (TS parity), so a
    // service without a pinned key sees no caller.
    internal static Request Of(string path, object? body, object? contextRaw, string? pinnedHubKey)
    {
        var context = JsonBody.Decode(contextRaw) as Dictionary<string, object?> ?? [];
        var hubKey = Caller.HubKey(context);
        return new Request
        {
            Path = path,
            Body = body,
            Caller = pinnedHubKey is not null && pinnedHubKey == hubKey ? Caller.FromContext(context) : null,
            HubKey = hubKey,
            Context = context,
        };
    }
}
