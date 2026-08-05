using ChannelCs;
using ChannelCs.Client;

namespace HubServiceCs;

// ServiceOptions configures a Service.
public sealed class ServiceOptions
{
    // Address is a port (int) or a ws:// URL (string). Default: 1997 or $HUB.
    public object? Address { get; init; }

    // Name is the profile name shown in the hub.
    public string? Name { get; init; }

    // Icon is the profile icon: a string or a {symbol, text} dictionary.
    public object? Icon { get; init; }
}

// Profile is sent to the hub as hub/profile/update on connect.
public sealed class Profile
{
    public string? Name { get; init; }

    public object? Icon { get; init; }
}

// AppHeader identifies an app in the hub.
public sealed class AppHeader
{
    public string Type { get; init; } = "app";

    public string Name { get; init; } = "";

    public string Path { get; init; } = "";
}

// ServicePermissions is a pro-only wire-parity field; hub-lite ignores it.
public sealed class ServicePermissions
{
    public string? Group { get; set; }

    public string Name { get; set; } = "";

    public bool? Allows { get; set; }
}

// ApiSettings configures a registered API path (pro-only fields, wire parity).
public sealed class ApiSettings
{
    public ServicePermissions? Permissions { get; init; }

    public int? Limit { get; init; }
}

// Service registers API endpoints on a hub and serves calls forwarded by it.
// Lite port of hub-service (TS reference): no auth, no keychain, no crypto.
public sealed class Service
{
    private readonly object _gate = new();
    private readonly Channel<object?> _channel = new();
    private readonly List<string> _paths = [];
    private readonly Dictionary<string, ApiSettings> _settings = [];
    private readonly List<AppHeader> _apps = [];
    private readonly object _address;
    private readonly Profile? _profile;
    private ChannelSender<object?>? _sender;

    // Creates a Service. Options are optional.
    public Service(ServiceOptions? options = null)
    {
        options ??= new ServiceOptions();
        var hub = Environment.GetEnvironmentVariable("HUB");
        _address = options.Address ?? (string.IsNullOrEmpty(hub) ? 1997 : hub);
        if (!string.IsNullOrEmpty(options.Name) || options.Icon is not null)
        {
            _profile = new Profile { Name = options.Name, Icon = options.Icon };
        }
    }

    // Post registers a request/response API endpoint.
    public Service Post(string path, Func<object?, object?> handler, ApiSettings? settings = null)
    {
        AddPath(path, settings);
        _channel.Post(path, request => handler(JsonBody.Decode(request.BodyRaw)));
        return this;
    }

    // Post registers an async request/response API endpoint.
    public Service Post(string path, Func<object?, Task<object?>> handler, ApiSettings? settings = null)
    {
        AddPath(path, settings);
        _channel.Post(path, request => handler(JsonBody.Decode(request.BodyRaw)));
        return this;
    }

    // Stream registers a streaming API endpoint. The handler receives the body
    // and returns the values to emit (TS/Go parity).
    public Service Stream(string path, Func<object?, IAsyncEnumerable<object?>> handler, ApiSettings? settings = null)
    {
        AddPath(path, settings);
        _channel.Stream(path, request => handler(JsonBody.Decode(request.BodyRaw)));
        return this;
    }

    // App registers an app: its header goes to the hub apps list and its path
    // serves the AppInterface as a stream (all 5 keys: header, body, top,
    // bottom, data).
    public Service App(App app)
    {
        lock (_gate)
        {
            _apps.Add(app.Header);
        }

        AddPath(app.Header.Path, null);
        var header = app.Header;
        var body = app.Body;
        var top = app.Top;
        var bottom = app.Bottom;
        var data = app.Data;
        _channel.Stream(app.Header.Path, _ => YieldInterface(header, body, top, bottom, data));
        return this;
    }

    // Send calls an API through the hub. Throws InvalidOperationException if
    // the service is not started.
    public async Task<object?> Send(string path, object? body = null, object? context = null)
    {
        var sender = SenderOrError();
        var response = await sender.SendAsync<object?>(path, body, context).ConfigureAwait(false);
        return JsonBody.Decode(response);
    }

    // Values opens a stream call through the hub. Throws InvalidOperationException
    // if the service is not started.
    public IAsyncEnumerable<object?> Values(string path, object? body = null, object? context = null)
    {
        var sender = SenderOrError();
        return DecodeStream(sender.Values<object?>(path, body, context));
    }

    // Start connects to the hub. On every (re)connect it sends
    // hub/profile/update (if a profile is set) then hub/service/update with the
    // current services + apps.
    public Service Start()
    {
        lock (_gate)
        {
            if (_sender is not null)
            {
                return this;
            }

            var options = new ClientOptions<object?>
            {
                OnConnect = s =>
                {
                    _ = SendUpdatesAsync(s);
                    return Task.CompletedTask;
                },
            };
            var sender = _address switch
            {
                int port => _channel.Connect(port, options),
                string address => _channel.Connect(address, options),
                _ => throw new InvalidOperationException($"Invalid address: {_address}"),
            };
            _sender = sender.Sender;
        }

        return this;
    }

    // UpdatePayload builds the current hub/service/update payload.
    internal Dictionary<string, object?> UpdatePayload()
    {
        lock (_gate)
        {
            var services = new List<object?>();
            foreach (var path in _paths.OrderBy(p => p, StringComparer.Ordinal))
            {
                var entry = new Dictionary<string, object?> { ["path"] = path };
                if (_settings.TryGetValue(path, out var settings))
                {
                    if (settings.Permissions is not null)
                    {
                        var permissions = new Dictionary<string, object?>
                        {
                            ["group"] = string.IsNullOrEmpty(settings.Permissions.Group)
                                ? path.Split('/')[0]
                                : settings.Permissions.Group,
                            ["name"] = settings.Permissions.Name,
                        };
                        if (settings.Permissions.Allows is bool allows)
                        {
                            permissions["allows"] = allows;
                        }

                        entry["permissions"] = permissions;
                    }

                    if (settings.Limit is int limit)
                    {
                        entry["limit"] = limit;
                    }
                }

                services.Add(entry);
            }

            var apps = _apps.Select(app => (object?)new Dictionary<string, object?>
            {
                ["type"] = app.Type,
                ["name"] = app.Name,
                ["path"] = app.Path,
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["services"] = services,
                ["apps"] = apps,
            };
        }
    }

    // ProfilePayload builds the hub/profile/update payload, or null when no
    // profile (name/icon) is set.
    internal Dictionary<string, object?>? ProfilePayload()
    {
        if (_profile is null)
        {
            return null;
        }

        var payload = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(_profile.Name))
        {
            payload["name"] = _profile.Name;
        }

        if (_profile.Icon is not null)
        {
            payload["icon"] = _profile.Icon;
        }

        return payload;
    }

    // Interface builds the AppInterface payload (all 5 keys always present).
    internal static Dictionary<string, object?> Interface(
        AppHeader header,
        IReadOnlyList<object?> body,
        object? top,
        object? bottom,
        IReadOnlyDictionary<string, object?> data)
    {
        return new Dictionary<string, object?>
        {
            ["header"] = new Dictionary<string, object?>
            {
                ["type"] = header.Type,
                ["name"] = header.Name,
                ["path"] = header.Path,
            },
            ["body"] = body,
            ["top"] = top,
            ["bottom"] = bottom,
            ["data"] = data,
        };
    }

    private static async IAsyncEnumerable<object?> YieldInterface(
        AppHeader header,
        IReadOnlyList<object?> body,
        object? top,
        object? bottom,
        IReadOnlyDictionary<string, object?> data)
    {
        yield return Interface(header, body, top, bottom, data);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<object?> DecodeStream(IAsyncEnumerable<object?> source)
    {
        await foreach (var item in source.ConfigureAwait(false))
        {
            yield return JsonBody.Decode(item);
        }
    }

    private ChannelSender<object?> SenderOrError()
    {
        lock (_gate)
        {
            return _sender ?? throw new InvalidOperationException("Service not started");
        }
    }

    private void AddPath(string path, ApiSettings? settings)
    {
        lock (_gate)
        {
            _paths.Add(path);
            if (settings is not null)
            {
                if (settings.Permissions is not null && string.IsNullOrEmpty(settings.Permissions.Group))
                {
                    settings.Permissions.Group = path.Split('/')[0];
                }

                _settings[path] = settings;
            }
        }
    }

    private async Task SendUpdatesAsync(ChannelSender<object?> sender)
    {
        try
        {
            var profile = ProfilePayload();
            if (profile is not null)
            {
                await sender.SendAsync<object?>("hub/profile/update", profile).ConfigureAwait(false);
            }

            await sender.SendAsync<object?>("hub/service/update", UpdatePayload()).ConfigureAwait(false);
        }
        catch
        {
            // Fire-and-forget: the hub may drop us mid-update; the client's
            // reconnect loop re-sends on the next connection.
        }
    }
}
