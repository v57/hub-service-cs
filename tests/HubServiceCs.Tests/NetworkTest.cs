using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ChannelCs;
using ChannelCs.Server;
using HubServiceCs;

// Share publication against a fake hub-net agent (Unix socket) and a fake hub.
[Collection("Keychain")]
public sealed class NetworkTest : IDisposable
{
    // Unix socket paths are short (104 bytes on macOS), so stay out of the deep per-user temp dir.
    private readonly string _tmp = Directory.CreateDirectory($"/tmp/hsc{Guid.NewGuid():N}"[..20]).FullName;
    private readonly Socket _agent = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly ConcurrentQueue<Dictionary<string, object?>> _requests = new();

    // Lets a test tamper with the attestation the fake agent returns.
    private Action<Dictionary<string, object?>> _attestationEdit = _ => { };

    public NetworkTest()
    {
        _agent.Bind(new UnixDomainSocketEndPoint(AgentPath));
        _agent.Listen();
        _ = Task.Run(ServeAgentAsync);
        Keychain.SetKeyPath(Path.Combine(_tmp, "auth"));
    }

    private string AgentPath => Path.Combine(_tmp, "agent.sock");

    public void Dispose()
    {
        _agent.Dispose();
        Keychain.SetKeyPath(null);
        Directory.Delete(_tmp, recursive: true);
    }

    private async Task ServeAgentAsync()
    {
        while (true)
        {
            Socket connection;
            try
            {
                connection = await _agent.AcceptAsync();
            }
            catch
            {
                return;
            }

            using (connection)
            {
                var buffer = new byte[8192];
                var received = new StringBuilder();
                while (true)
                {
                    var count = await connection.ReceiveAsync(buffer);
                    received.Append(Encoding.UTF8.GetString(buffer, 0, count));
                    var text = received.ToString();
                    var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (end < 0)
                    {
                        continue;
                    }

                    var lengthLine = text[..end].Split("\r\n")
                        .FirstOrDefault(line => line.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase));
                    var length = lengthLine is null ? 0 : int.Parse(lengthLine.Split(':')[1].Trim());
                    if (Encoding.UTF8.GetByteCount(text[(end + 4)..]) >= length)
                    {
                        break;
                    }
                }

                var request = received.ToString();
                var path = request.Split(' ')[1];
                using var document = JsonDocument.Parse(request[(request.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]);
                var body = (Dictionary<string, object?>)JsonBody.Decode(document.RootElement.Clone())!;
                body["_path"] = path;
                _requests.Enqueue(body);
                string reply;
                if (path == "/challenge")
                {
                    reply = """{"type":"attest_response","nonce":"nonce-1"}""";
                }
                else
                {
                    var attestation = new Dictionary<string, object?>
                    {
                        ["version"] = 1, ["attestationId"] = "att-1", ["serviceKey"] = body["serviceKey"], ["key"] = "agent-key",
                        ["shareName"] = body["shareName"], ["protocol"] = body["protocol"], ["target"] = body["target"],
                        ["issuedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                        ["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
                        ["signature"] = "agent-signature",
                    };
                    _attestationEdit(attestation);
                    reply = JsonSerializer.Serialize(attestation);
                }

                var bytes = Encoding.UTF8.GetBytes(reply);
                await connection.SendAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
                await connection.SendAsync(bytes);
            }
        }
    }

    private NetworkPublisher Publisher() => new() { AgentSocket = AgentPath };

    private static Task<object?> Publications(object body)
    {
        var shares = (List<object?>)((Dictionary<string, object?>)body)["shares"]!;
        var list = shares.Select((share, i) => (object?)new Dictionary<string, object?>
        {
            ["version"] = 1L, ["resourceId"] = "hnr1_" + ((Dictionary<string, object?>)share!)["name"], ["authority"] = $"a{i}",
            ["providerId"] = "p", ["protocol"] = ((Dictionary<string, object?>)share)["protocol"],
            ["overlayAddress"] = "100.96.0.2", ["port"] = 40000L + i,
        }).ToList();
        return Task.FromResult<object?>(new Dictionary<string, object?> { ["shares"] = list });
    }

    [Fact]
    public async Task PublishesAttestedSharesInNameOrder()
    {
        var key = Keychain.Key();
        var sent = new List<object>();
        var network = Publisher();
        await network.PublishAsync(
            new NetworkShare("web", "tcp", "127.0.0.1", 8080, new Dictionary<string, object?> { ["mode"] = "hub" }),
            key, body => { sent.Add(body); return Publications(body); });
        var result = await network.PublishAsync(
            new NetworkShare("db", "tcp", "localhost", 5432), key, body => { sent.Add(body); return Publications(body); });

        // The agent's JSON decoder refuses unknown fields: exactly these go out.
        var requests = _requests.ToArray();
        Assert.Equal(["_path", "protocol", "serviceKey", "shareName", "target"], requests[0].Keys.Order());
        Assert.Equal(["_path", "protocol", "serviceKey", "shareName", "signature", "target"], requests[1].Keys.Order());
        Assert.Equal(key.PublicKey, requests[1]["serviceKey"]);
        Assert.True(TestKeys.Verifies(key.PublicKey, "nonce-1", (string)requests[1]["signature"]!));

        var wire = (List<object?>)((Dictionary<string, object?>)sent[1])["shares"]!;
        Assert.Equal(["db", "web"], wire.Select(share => ((Dictionary<string, object?>)share!)["name"]));
        var first = (Dictionary<string, object?>)wire[0]!;
        Assert.Equal(1, first["version"]);
        Assert.Equal("att-1", ((Dictionary<string, object?>)first["attestation"]!)["attestationId"]);
        Assert.Equal(2, result.Count);
        Assert.Equal("hnr1_web", network.Publication("web")!["resourceId"]);
    }

    [Fact]
    public async Task MismatchedAttestationIsRefusedAndRolledBack()
    {
        _attestationEdit = attestation => attestation["shareName"] = "other";
        var network = Publisher();
        var error = await Assert.ThrowsAsync<NetworkAgentException>(() =>
            network.PublishAsync(new NetworkShare("web", "tcp", "127.0.0.1", 8080), Keychain.Key(), Publications));
        Assert.Equal("attestation-mismatch", error.Code);
        Assert.Empty(network.DeclaredShares);
    }

    [Fact]
    public async Task ExpiredAttestationIsRefused()
    {
        _attestationEdit = attestation => attestation["expiresAt"] = DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O");
        var error = await Assert.ThrowsAsync<NetworkAgentException>(() =>
            Publisher().PublishAsync(new NetworkShare("web", "tcp", "127.0.0.1", 8080), Keychain.Key(), Publications));
        Assert.Equal("attestation-expired", error.Code);
    }

    [Fact]
    public async Task MissingAgentIsReported()
    {
        var network = new NetworkPublisher { AgentSocket = Path.Combine(_tmp, "none.sock") };
        var error = await Assert.ThrowsAsync<NetworkAgentException>(() =>
            network.PublishAsync(new NetworkShare("web", "tcp", "127.0.0.1", 8080), Keychain.Key(), Publications));
        Assert.Equal("agent-unavailable", error.Code);
    }

    [Fact]
    public async Task InvalidSharesAndPublicationsAreRefused()
    {
        Assert.Throws<ArgumentException>(() => NetworkPublisher.Validate(new NetworkShare("", "tcp", "h", 1)));
        Assert.Throws<ArgumentException>(() => NetworkPublisher.Validate(new NetworkShare("a", "sctp", "h", 1)));
        Assert.Throws<ArgumentException>(() => NetworkPublisher.Validate(new NetworkShare("a", "tcp", "h", 70000)));
        Assert.Throws<ArgumentException>(() => NetworkPublisher.Validate(
            new NetworkShare("a", "tcp", "h", 1, new Dictionary<string, object?> { ["mode"] = "keys" })));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Publisher().PublishAsync(
            new NetworkShare("web", "tcp", "127.0.0.1", 8080), Keychain.Key(),
            _ => Task.FromResult<object?>(new Dictionary<string, object?> { ["shares"] = new List<object?>() })));
        Assert.Contains("0 publications for 1 shares", error.Message);
    }

    [Fact]
    public async Task DeclaredSharesArePublishedOnConnect()
    {
        var replaced = new ConcurrentQueue<object?>();
        var hub = new Channel<object?>()
            .Post("hub/service/update", _ => null)
            .Post("network/shares/replace", request =>
            {
                var body = JsonBody.Decode(request.BodyRaw);
                replaced.Enqueue(body);
                return Publications(body!).Result;
            });
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var server = hub.Listen(port);

        var service = new Service(new ServiceOptions { Address = $"ws://127.0.0.1:{port}", Device = false });
        service.Network.AgentSocket = AgentPath;
        service.Share("web", "tcp", "127.0.0.1", 8080);
        service.Start();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (replaced.IsEmpty && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.Single(replaced);
            await Task.Delay(50);
            Assert.Equal("hnr1_web", service.Network.Publication("web")!["resourceId"]);
            Assert.Single(await service.UpdateShare("web", new Dictionary<string, object?> { ["mode"] = "hub" }, 9090));
            Assert.Equal(9090, service.Network.DeclaredShares[0].Port);
            Assert.Empty(await service.RemoveShare("web"));
        }
        finally
        {
            service.Stop();
        }
    }
}
