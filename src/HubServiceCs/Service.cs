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

    // Sign is the hub-pro auth token factory (default: the keychain, which
    // loads or generates the `auth` key file in the cwd and signs v1 tokens
    // once the hub key is pinned). The fresh token is attached as the `auth`
    // header on EVERY connect/reconnect; hub-lite ignores it. A custom
    // factory always connects with v=0.
    public Func<string>? Sign { get; init; }

    // HubKey is the hub's SPKI if known up front (invitation or config).
    // Null = learn and pin it via `hub/info`.
    public string? HubKey { get; init; }

    // Device turns device mode on (default) or off: every connect sends a
    // `device` header, a certificate for the service key signed by the device
    // key (Docs/Devices/protocol.md section 3).
    public bool Device { get; init; } = true;

    // DeviceKey is a caller-provided device key (e.g. kept in an app's own
    // keychain). Null = the device key file.
    public KeyFile? DeviceKey { get; init; }

    // DeviceKeyPath is the device key file. Null = HubServiceCs.Device.DefaultKeyPath().
    public string? DeviceKeyPath { get; init; }

    // Instance names this service on its device (`s3-local/Backup Disk`).
    // Default: the profile name, else the working directory's name, else
    // `service`.
    public string? Instance { get; init; }

    // DeviceName is the suggested device name. Null = the computer name.
    public string? DeviceName { get; init; }

    // Platform is the certificate platform. Null = this OS.
    public string? Platform { get; init; }

    // Parent is the declared parent device name (containers and VMs).
    public string? Parent { get; init; }
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
// Port of hub-service (TS reference) with hub-pro auth: every (re)connect
// attaches a fresh signed `auth` token (ed25519 keychain, `auth` file in the
// cwd) plus `v`; hub-lite ignores both headers.
//
// Hub-bound auth (Docs/Devices/protocol.md section 2): with no pinned key for
// the address, the service connects with `v: 0`, calls `hub/info`, pins the
// returned key in `hubs.json` beside the key file and reconnects at once with
// `v: 1` tokens signed over `hub1:<hub key>:<expiry>`. A hub that doesn't
// answer `hub/info` (hub-lite) keeps getting v0. A refused v1 connect is
// reported (LastError), never silently downgraded; call ForgetHubKey when the
// hub key really changed.
//
// Device mode (protocol.md section 3, on by default): every connect also
// carries a `device` header, a certificate for the service key signed by the
// device key. It is created at connect when missing or with less than 24
// hours left (7-day lifetime). A device key that can't be read or created
// turns device mode off for this service.
public sealed class Service
{
    private readonly object _gate = new();
    private readonly object _deviceGate = new();
    private readonly Channel<object?> _channel = new();
    private readonly List<string> _paths = [];
    private readonly Dictionary<string, ApiSettings> _settings = [];
    private readonly List<AppHeader> _apps = [];
    private readonly object _address;
    private readonly Profile? _profile;
    private readonly Func<string>? _sign;
    private readonly HubPins? _pins;
    private volatile string? _hubKey;
    private volatile string? _lastError;
    private ClientSender<object?>? _client;
    private ChannelSender<object?>? _sender;
    private bool _stopped;
    private readonly ServiceOptions _options;
    private volatile bool _deviceMode;
    private KeyFile? _deviceKey;
    private string? _certificate;
    private long _certificateExpiry;
    private string? _deviceName;

    // Creates a Service. Options are optional.
    public Service(ServiceOptions? options = null)
    {
        options ??= new ServiceOptions();
        _options = options;
        // A custom token factory hides the service key the certificate must name.
        _deviceMode = options.Device && options.Sign is null;
        var hub = Environment.GetEnvironmentVariable("HUB");
        _address = options.Address
            ?? (string.IsNullOrEmpty(hub)
                ? 1997
                : int.TryParse(hub, out var hubPort) ? hubPort : hub);
        _sign = options.Sign;
        _pins = options.Sign is null ? HubPins.Beside(Keychain.KeyPath()) : null;
        _hubKey = options.HubKey ?? _pins?.Get(NormalizeAddress(_address));
        if (!string.IsNullOrEmpty(options.Name) || options.Icon is not null)
        {
            _profile = new Profile { Name = options.Name, Icon = options.Icon };
        }
    }

    // HubKey is the hub key v1 tokens are bound to, or null while connecting
    // with v0.
    public string? HubKey => _hubKey;

    // LastError is the last connect error (e.g. a refused v1 connect), or null.
    public string? LastError => _lastError;

    // AddressKey is the address as `hubs.json` keys it (NormalizeAddress).
    public string AddressKey => NormalizeAddress(_address);

    // Instance is the instance name the certificate carries: the option, else
    // the profile name, else the working directory's name, else `service`.
    public string Instance
    {
        get
        {
            if (!string.IsNullOrEmpty(_options.Instance))
            {
                return _options.Instance;
            }

            if (!string.IsNullOrEmpty(_profile?.Name))
            {
                return _profile.Name;
            }

            var directory = Path.GetFileName(Directory.GetCurrentDirectory().TrimEnd(Path.DirectorySeparatorChar));
            return string.IsNullOrEmpty(directory) ? "service" : directory;
        }
    }

    // DeviceKey is the device key in use, or null before the first connect or
    // with device mode off.
    public KeyFile? DeviceKey
    {
        get
        {
            lock (_deviceGate)
            {
                return _deviceKey;
            }
        }
    }

    // DeviceHeader is the `device` header for the next connect, or null with
    // device mode off. Reuses the current certificate while it has more than
    // 24 hours left.
    internal string? DeviceHeader()
    {
        if (!_deviceMode)
        {
            return null;
        }

        lock (_deviceGate)
        {
            try
            {
                _deviceKey ??= _options.DeviceKey
                    ?? HubServiceCs.Device.LoadOrCreateKey(_options.DeviceKeyPath ?? HubServiceCs.Device.DefaultKeyPath());
                var now = Keychain.Now();
                if (_certificate is null || _certificateExpiry - now < HubServiceCs.Device.RenewBefore)
                {
                    _deviceName ??= _options.DeviceName ?? HubServiceCs.Device.DefaultName();
                    var exp = now + HubServiceCs.Device.CertificateLifetime;
                    _certificate = HubServiceCs.Device.Certificate(_deviceKey, HubServiceCs.Device.CertificatePayload(
                        _deviceKey.PublicKey,
                        Keychain.PublicKey(),
                        Instance,
                        _deviceName,
                        _options.Platform ?? HubServiceCs.Device.DefaultPlatform(),
                        _options.Parent,
                        now,
                        exp));
                    _certificateExpiry = exp;
                }

                return _certificate;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"hub-service: device mode off: {error.Message}");
                _deviceMode = false;
                return null;
            }
        }
    }

    // ForgetHubKey clears the pinned hub key for this address, so the next
    // connect uses v0 and pins whatever key `hub/info` returns. Use it when a
    // hub's key changed on purpose.
    public void ForgetHubKey()
    {
        _hubKey = null;
        _pins?.Remove(AddressKey);
    }

    // NormalizeAddress normalizes a hub address like channel TS does
    // (protocol.md section 2): a bare port becomes `ws://localhost:<port>`; a
    // URL keeps its form with the scheme and host lowercased and no trailing
    // slash.
    public static string NormalizeAddress(object address)
    {
        if (address is int port)
        {
            return $"ws://localhost:{port}";
        }

        var text = address.ToString()!.Trim();
        if (text.Length > 0 && text.All(char.IsAsciiDigit))
        {
            return $"ws://localhost:{text}";
        }

        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            var authorityEnd = text.IndexOf('/', schemeEnd + 3);
            if (authorityEnd < 0)
            {
                authorityEnd = text.Length;
            }

            text = text[..authorityEnd].ToLowerInvariant() + text[authorityEnd..];
        }

        return text.TrimEnd('/');
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

    // Post registers a request/response API endpoint whose handler also
    // receives the Request: the caller identity the hub forwards
    // (Request.Caller, only when the request's hub key is the pinned one) and
    // the whole request context.
    public Service Post(string path, Func<object?, Request, object?> handler, ApiSettings? settings = null)
    {
        AddPath(path, settings);
        _channel.Post(path, request =>
        {
            var body = JsonBody.Decode(request.BodyRaw);
            return handler(body, Request.Of(request.Path, body, request.ContextRaw, _hubKey));
        });
        return this;
    }

    // Post registers an async request/response API endpoint whose handler
    // also receives the Request.
    public Service Post(string path, Func<object?, Request, Task<object?>> handler, ApiSettings? settings = null)
    {
        AddPath(path, settings);
        _channel.Post(path, request =>
        {
            var body = JsonBody.Decode(request.BodyRaw);
            return handler(body, Request.Of(request.Path, body, request.ContextRaw, _hubKey));
        });
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

    // Stream registers a streaming API endpoint whose handler also receives
    // the Request with the caller identity.
    public Service Stream(string path, Func<object?, Request, IAsyncEnumerable<object?>> handler, ApiSettings? settings = null)
    {
        AddPath(path, settings);
        _channel.Stream(path, request =>
        {
            var body = JsonBody.Decode(request.BodyRaw);
            return handler(body, Request.Of(request.Path, body, request.ContextRaw, _hubKey));
        });
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

    // Network publishes Hub Network shares: agent socket settings and the
    // current publications.
    public NetworkPublisher Network { get; } = new();

    // Share declares a Hub Network share before Start: the local host:port is
    // published under name on connect and after every reconnect. policy is
    // {mode: hub|private|disabled}, {mode: group, group} or {mode: keys,
    // keys}; null means private.
    public Service Share(string name, string protocol, string host, int port, IReadOnlyDictionary<string, object?>? policy = null)
    {
        Network.Declare(new NetworkShare(name, protocol, host, port, policy ?? PrivatePolicy));
        return this;
    }

    // PublishShare publishes a Hub Network share now (TS publishShare): the
    // local hub-net agent attests the port, then the declared set goes to the
    // hub. Returns every current publication. Requires Start.
    public Task<IReadOnlyList<Dictionary<string, object?>>> PublishShare(
        string name, string protocol, string host, int port, IReadOnlyDictionary<string, object?>? policy = null)
    {
        return Network.PublishAsync(new NetworkShare(name, protocol, host, port, policy ?? PrivatePolicy), ShareKey(), ReplaceShares);
    }

    // UpdateShare changes a published share's policy and/or port (null keeps
    // the current one) and republishes.
    public Task<IReadOnlyList<Dictionary<string, object?>>> UpdateShare(
        string name, IReadOnlyDictionary<string, object?>? policy = null, int? port = null)
    {
        var current = Network.DeclaredShares.FirstOrDefault(share => share.Name == name)
            ?? throw new ArgumentException($"Share '{name}' not found");
        return Network.PublishAsync(
            current with { Policy = policy ?? current.Policy, Port = port ?? current.Port }, ShareKey(), ReplaceShares);
    }

    // RemoveShare removes a share and republishes the rest.
    public Task<IReadOnlyList<Dictionary<string, object?>>> RemoveShare(string name)
    {
        return Network.RemoveAsync(name, ShareKey(), ReplaceShares);
    }

    private static readonly IReadOnlyDictionary<string, object?> PrivatePolicy =
        new Dictionary<string, object?> { ["mode"] = "private" };

    private KeyFile ShareKey()
    {
        return _sign is null
            ? Keychain.Key()
            : throw new InvalidOperationException("Share publication needs the keychain service key, not a custom sign");
    }

    private Task<object?> ReplaceShares(object body)
    {
        return Send("network/shares/replace", body);
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
            if (_client is null && !_stopped)
            {
                Connect();
            }
        }

        return this;
    }

    // Stop closes the connection and stops reconnecting.
    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _client?.Stop();
            _client = null;
        }
    }

    // Connect dials the hub; the caller holds _gate.
    private void Connect()
    {
        ClientSender<object?>? client = null;
        var options = new ClientOptions<object?>
        {
            // Fresh token per (re)connect: the headers provider runs on every
            // connection attempt (before the upgrade), so the hub sees a fresh
            // signed identity each time. The provider is synchronous — no
            // deadlock risk with the read loop.
            Headers = () => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(AuthHeaders()),
            OnConnect = s =>
            {
                // The client awaits this before reading replies, so calls run
                // in the background.
                _ = OnConnectedAsync(client, s);
                return Task.CompletedTask;
            },
        };
        client = _address switch
        {
            int port => _channel.Connect(port, options),
            string address => _channel.Connect(address, options),
            _ => throw new InvalidOperationException($"Invalid address: {_address}"),
        };
        client.WebSocket.OnProtocolError = ReportConnectError;
        _client = client;
        _sender = client.Sender;
    }

    // AuthHeaders builds the hub-pro headers for one connect: a fresh token
    // and the protocol version, `1` with a token bound to the pinned hub key,
    // else `0`.
    internal Dictionary<string, string> AuthHeaders()
    {
        var headers = new Dictionary<string, string>();
        var hubKey = _hubKey;
        if (_sign is not null)
        {
            headers["auth"] = _sign();
            headers["v"] = "0";
        }
        else if (hubKey is not null)
        {
            headers["auth"] = Keychain.SignHub(hubKey);
            headers["v"] = "1";
        }
        else
        {
            headers["auth"] = Keychain.Sign();
            headers["v"] = "0";
        }

        var device = DeviceHeader();
        if (device is not null)
        {
            headers["device"] = device;
        }

        return headers;
    }

    private async Task OnConnectedAsync(ClientSender<object?>? client, ChannelSender<object?> sender)
    {
        _lastError = null;
        if (await LearnHubKeyAsync(sender).ConfigureAwait(false))
        {
            // Pinned the hub key: reconnect at once with a v1 token.
            lock (_gate)
            {
                if (client is not null && ReferenceEquals(_client, client) && !_stopped)
                {
                    client.Stop();
                    Connect();
                }
            }

            return;
        }

        await SendUpdatesAsync(sender).ConfigureAwait(false);
        if (Network.HasShares && _sign is null)
        {
            try
            {
                await Network.RepublishAllAsync(Keychain.Key(), async body =>
                    JsonBody.Decode(await sender.SendAsync<object?>("network/shares/replace", body).ConfigureAwait(false)))
                    .ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"hub-service: failed to republish network shares: {error.Message}");
            }
        }
    }

    // LearnHubKeyAsync asks `hub/info` for the hub key on a v0 connection with
    // no pinned key and pins it. Returns true when a key was newly pinned.
    private async Task<bool> LearnHubKeyAsync(ChannelSender<object?> sender)
    {
        if (_pins is null || _hubKey is not null)
        {
            return false;
        }

        string? key;
        try
        {
            var info = JsonBody.Decode(await sender.SendAsync<object?>("hub/info").ConfigureAwait(false));
            key = (info as Dictionary<string, object?>)?.GetValueOrDefault("key") as string;
        }
        catch
        {
            return false; // hub-lite or an older hub-pro: stay on v0.
        }

        if (key is null)
        {
            return false;
        }

        _hubKey = key;
        try
        {
            _pins.Put(AddressKey, key);
        }
        catch
        {
            // Unwritable pin file: keep the key for this process only.
        }

        return true;
    }

    private void ReportConnectError(string message)
    {
        if (_hubKey is not null && _sign is null && message != _lastError)
        {
            Console.Error.WriteLine(
                $"hub-service: the hub at {AddressKey} refused the v1 connect for hub key {_hubKey} ({message}). "
                + "If it says invalid signature, the hub key changed or a different server answers; "
                + "call ForgetHubKey() if the change is expected.");
        }

        _lastError = message;
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
        }
        catch
        {
            // A hub without the profile endpoint still gets the services (TS parity).
        }

        try
        {
            await sender.SendAsync<object?>("hub/service/update", UpdatePayload()).ConfigureAwait(false);
        }
        catch
        {
            // Fire-and-forget: the hub may drop us mid-update; the client's
            // reconnect loop re-sends on the next connection.
        }
    }
}
