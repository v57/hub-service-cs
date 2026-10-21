using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace HubServiceCs;

// NetworkShare is a local target published under a stable name with an
// access policy (`{mode: hub|private|disabled}`, `{mode: group, group}` or
// `{mode: keys, keys}`).
public sealed record NetworkShare(
    string Name,
    string Protocol,
    string Host,
    int Port,
    IReadOnlyDictionary<string, object?>? Policy = null)
{
    // ToWire is the share as the hub expects it (without attestation).
    internal Dictionary<string, object?> ToWire()
    {
        var map = new Dictionary<string, object?>
        {
            ["name"] = Name,
            ["protocol"] = Protocol,
            ["target"] = new Dictionary<string, object?> { ["host"] = Host, ["port"] = Port },
        };
        if (Policy is not null)
        {
            map["policy"] = Policy;
        }

        return map;
    }
}

// NetworkAgentException is a local-agent failure. Code is agent-unavailable,
// agent-timeout, agent-rejected, invalid-agent-response, attestation-mismatch
// or attestation-expired.
public sealed class NetworkAgentException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// NetworkPublisher publishes Hub Network shares, ported from the TS SDK
// (hub-service/network.ts). A share makes a local TCP/UDP port reachable
// through Hub Network: for each share the local hub-net agent attests that
// this service owns the port (POST /challenge, sign the nonce with the
// service key, POST /attest over the agent's Unix socket), then the complete
// set goes to the hub with `network/shares/replace`, which answers one
// publication per share in request order. The declared set is republished
// after every reconnect.
public sealed class NetworkPublisher
{
    public const int WireVersion = 1;
    private const int MaxShareNameLength = 255;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SortedDictionary<string, NetworkShare> _shares = new(StringComparer.Ordinal);
    private Dictionary<string, Dictionary<string, object?>> _publications = [];

    // AgentSocket is the hub-net agent socket: $HUB_NET_AGENT_SOCKET, else the
    // platform's.
    public string AgentSocket { get; set; } = DefaultAgentSocket();

    public TimeSpan AgentTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public static string DefaultAgentSocket()
    {
        var env = Environment.GetEnvironmentVariable("HUB_NET_AGENT_SOCKET");
        if (!string.IsNullOrEmpty(env))
        {
            return env;
        }

        return OperatingSystem.IsLinux() ? "/run/hub-net/agent.sock" : "/var/run/hub-net/agent.sock";
    }

    public bool HasShares
    {
        get
        {
            lock (_shares)
            {
                return _shares.Count > 0;
            }
        }
    }

    // DeclaredShares lists the declared shares, sorted by name.
    public IReadOnlyList<NetworkShare> DeclaredShares
    {
        get
        {
            lock (_shares)
            {
                return [.. _shares.Values];
            }
        }
    }

    // Publications lists the current publications, in share-name order.
    public IReadOnlyList<Dictionary<string, object?>> Publications
    {
        get
        {
            lock (_shares)
            {
                return [.. _publications.Values];
            }
        }
    }

    // Publication returns the publication of shareName, or null.
    public Dictionary<string, object?>? Publication(string shareName)
    {
        lock (_shares)
        {
            return _publications.GetValueOrDefault(shareName);
        }
    }

    // Declare declares a share, published on the next connect and after every
    // reconnect.
    public void Declare(NetworkShare share)
    {
        Validate(share);
        lock (_shares)
        {
            _shares[share.Name] = share;
        }
    }

    // PublishAsync publishes share with the declared set; rolls the
    // declaration back on failure.
    public async Task<IReadOnlyList<Dictionary<string, object?>>> PublishAsync(
        NetworkShare share, KeyFile serviceKey, Func<object, Task<object?>> send)
    {
        Validate(share);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            NetworkShare? previous;
            lock (_shares)
            {
                _shares.TryGetValue(share.Name, out previous);
                _shares[share.Name] = share;
            }

            try
            {
                return await SynchronizeAsync(serviceKey, send).ConfigureAwait(false);
            }
            catch
            {
                lock (_shares)
                {
                    if (previous is not null)
                    {
                        _shares[share.Name] = previous;
                    }
                    else
                    {
                        _shares.Remove(share.Name);
                    }
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // RemoveAsync removes shareName and republishes the rest; restores it on
    // failure.
    public async Task<IReadOnlyList<Dictionary<string, object?>>> RemoveAsync(
        string shareName, KeyFile serviceKey, Func<object, Task<object?>> send)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            NetworkShare? removed;
            lock (_shares)
            {
                if (!_shares.Remove(shareName, out removed))
                {
                    throw new ArgumentException($"Share '{shareName}' not found");
                }
            }

            try
            {
                return await SynchronizeAsync(serviceKey, send).ConfigureAwait(false);
            }
            catch
            {
                lock (_shares)
                {
                    _shares[shareName] = removed;
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // RepublishAllAsync publishes the whole declared set again (after a reconnect).
    public async Task<IReadOnlyList<Dictionary<string, object?>>> RepublishAllAsync(
        KeyFile serviceKey, Func<object, Task<object?>> send)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await SynchronizeAsync(serviceKey, send).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // RequestAttestationAsync asks the local agent to attest share: /challenge
    // returns a nonce, which the service key signs for /attest.
    public async Task<Dictionary<string, object?>> RequestAttestationAsync(NetworkShare share, KeyFile serviceKey)
    {
        Validate(share);
        var body = new Dictionary<string, object?>
        {
            ["serviceKey"] = serviceKey.PublicKey,
            ["shareName"] = share.Name,
            ["protocol"] = share.Protocol,
            ["target"] = share.ToWire()["target"],
        };
        var challenge = await AgentPostAsync("/challenge", body, "challenge").ConfigureAwait(false);
        if (challenge.GetValueOrDefault("nonce") is not string { Length: > 0 } nonce)
        {
            throw new NetworkAgentException("invalid-agent-response", "Hub Network agent returned an invalid challenge");
        }

        body["signature"] = serviceKey.Sign(nonce);
        var attestation = await AgentPostAsync("/attest", body, "attestation").ConfigureAwait(false);
        ValidateAttestation(attestation, share, serviceKey.PublicKey);
        return attestation;
    }

    // Validate checks a share like the TS SDK: name, protocol, target and policy.
    public static void Validate(NetworkShare share)
    {
        RequireText(share.Name, "share name", MaxShareNameLength);
        if (share.Protocol is not ("tcp" or "udp"))
        {
            throw new ArgumentException("Invalid Hub Network protocol");
        }

        RequireText(share.Host, "target host");
        if (share.Port is < 1 or > 65_535)
        {
            throw new ArgumentException("Invalid Hub Network target port");
        }

        if (share.Policy is null)
        {
            return;
        }

        switch (share.Policy.GetValueOrDefault("mode") as string)
        {
            case "hub" or "private" or "disabled":
                return;
            case "group":
                RequireText(share.Policy.GetValueOrDefault("group"), "policy group");
                return;
            case "keys":
                if (share.Policy.GetValueOrDefault("keys") is not System.Collections.IEnumerable keys
                    || share.Policy["keys"] is string
                    || !keys.Cast<object?>().Any())
                {
                    throw new ArgumentException("Invalid Hub Network policy keys");
                }

                foreach (var key in keys)
                {
                    RequireText(key, "policy key");
                }

                return;
            default:
                throw new ArgumentException("Invalid Hub Network policy mode");
        }
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>> SynchronizeAsync(
        KeyFile serviceKey, Func<object, Task<object?>> send)
    {
        var declared = DeclaredShares;
        var wire = new List<object?>();
        foreach (var share in declared)
        {
            var entry = share.ToWire();
            entry["version"] = WireVersion;
            entry["attestation"] = await RequestAttestationAsync(share, serviceKey).ConfigureAwait(false);
            wire.Add(entry);
        }

        var response = await send(new Dictionary<string, object?> { ["shares"] = wire }).ConfigureAwait(false);
        if (response is not Dictionary<string, object?> map || map.GetValueOrDefault("shares") is not List<object?> list)
        {
            throw new InvalidOperationException("Hub returned an invalid shares/replace response");
        }

        if (list.Count != declared.Count)
        {
            throw new InvalidOperationException($"Hub returned {list.Count} publications for {declared.Count} shares");
        }

        // Protocol v1 publications carry no share name: the hub keeps request order.
        var correlated = new Dictionary<string, Dictionary<string, object?>>();
        var resourceIds = new HashSet<string>();
        for (var index = 0; index < declared.Count; index++)
        {
            var publication = ValidatePublication(list[index], declared[index], index);
            if (!resourceIds.Add((string)publication["resourceId"]!))
            {
                throw new InvalidOperationException($"Hub returned duplicate resourceId at share index {index}");
            }

            correlated[declared[index].Name] = publication;
        }

        lock (_shares)
        {
            _publications = correlated;
        }

        return [.. correlated.Values];
    }

    private static Dictionary<string, object?> ValidatePublication(object? value, NetworkShare share, int index)
    {
        if (value is not Dictionary<string, object?> map)
        {
            throw new InvalidOperationException($"Invalid Hub publication at share index {index}");
        }

        if (map.GetValueOrDefault("version") is not (long)WireVersion)
        {
            throw new InvalidOperationException($"Invalid Hub publication version at share index {index}");
        }

        if (map.GetValueOrDefault("resourceId") is not string id || !id.StartsWith("hnr1_", StringComparison.Ordinal) || id.Length == 5)
        {
            throw new InvalidOperationException($"Invalid Hub publication resourceId at share index {index}");
        }

        foreach (var field in new[] { "authority", "providerId", "overlayAddress" })
        {
            if (map.GetValueOrDefault(field) is not string text || string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException($"Invalid Hub publication {field} at share index {index}");
            }
        }

        if (map.GetValueOrDefault("protocol") as string != share.Protocol)
        {
            throw new InvalidOperationException($"Hub publication protocol mismatch at share index {index}");
        }

        if (map.GetValueOrDefault("port") is not long port || port is < 1 or > 65_535)
        {
            throw new InvalidOperationException($"Invalid Hub publication port at share index {index}");
        }

        return map;
    }

    private static void ValidateAttestation(Dictionary<string, object?> attestation, NetworkShare share, string serviceKey)
    {
        var target = attestation.GetValueOrDefault("target") as Dictionary<string, object?> ?? [];
        if (attestation.GetValueOrDefault("version") is not (long)WireVersion
            || attestation.GetValueOrDefault("serviceKey") as string != serviceKey
            || attestation.GetValueOrDefault("shareName") as string != share.Name
            || attestation.GetValueOrDefault("protocol") as string != share.Protocol
            || target.GetValueOrDefault("host") as string != share.Host
            || target.GetValueOrDefault("port") is not long port
            || port != share.Port)
        {
            throw new NetworkAgentException("attestation-mismatch", "Local-agent attestation does not match the share");
        }

        if (!DateTimeOffset.TryParse(attestation.GetValueOrDefault("issuedAt") as string, out var issuedAt)
            || !DateTimeOffset.TryParse(attestation.GetValueOrDefault("expiresAt") as string, out var expiresAt))
        {
            throw new NetworkAgentException("attestation-expired", "Local-agent attestation is expired");
        }

        var now = DateTimeOffset.UtcNow;
        if (expiresAt <= issuedAt || issuedAt > now.AddSeconds(30) || expiresAt <= now)
        {
            throw new NetworkAgentException("attestation-expired", "Local-agent attestation is expired");
        }
    }

    private static void RequireText(object? value, string field, int maxLength = int.MaxValue)
    {
        if (value is not string text
            || string.IsNullOrWhiteSpace(text)
            || text.Length > maxLength
            || text.Any(c => c < 0x20 || c == 0x7f))
        {
            throw new ArgumentException($"Invalid Hub Network {field}");
        }
    }

    // AgentPostAsync POSTs JSON to the agent over its Unix socket and decodes
    // the JSON answer.
    private async Task<Dictionary<string, object?>> AgentPostAsync(string path, object body, string operation)
    {
        var socketPath = AgentSocket;
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var client = new HttpClient(handler) { Timeout = AgentTimeout };
        using var content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync($"http://localhost{path}", content).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            throw new NetworkAgentException("agent-timeout", $"Hub Network agent timed out after {AgentTimeout.TotalMilliseconds} ms");
        }
        catch (Exception)
        {
            throw new NetworkAgentException("agent-unavailable", $"Hub Network agent is unavailable at {socketPath}");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = text.Trim();
                throw new NetworkAgentException(
                    "agent-rejected",
                    $"Hub Network agent rejected {operation} ({(int)response.StatusCode}){(detail.Length > 0 ? ": " + detail : "")}");
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                return JsonBody.Decode(document.RootElement.Clone()) as Dictionary<string, object?>
                    ?? throw new JsonException("not an object");
            }
            catch (JsonException)
            {
                throw new NetworkAgentException("invalid-agent-response", "Hub Network agent returned invalid JSON");
            }
        }
    }
}
