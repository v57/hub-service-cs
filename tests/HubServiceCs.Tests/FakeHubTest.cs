using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ChannelCs;
using ChannelCs.Server;
using HubServiceCs;

// End to end against a fake hub: hub/info pinning, the v1 reconnect, the
// device header and caller identity.
[Collection("Keychain")]
public sealed class FakeHubTest : IDisposable
{
    private readonly string _tmp = TestKeys.TempDirectory();

    public void Dispose()
    {
        Keychain.SetKeyPath(null);
        Directory.Delete(_tmp, recursive: true);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task Await(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task PinsHubKeyReconnectsWithV1AndSeesTheCaller()
    {
        var connects = new ConcurrentQueue<IReadOnlyDictionary<string, string?>>();
        var updates = new ConcurrentQueue<IReadOnlyDictionary<string, string?>>();
        ChannelSender<IReadOnlyDictionary<string, string?>>? latest = null;
        var hub = new Channel<IReadOnlyDictionary<string, string?>>()
            .Post("hub/info", _ => new Dictionary<string, object?>
            {
                ["key"] = TestKeys.HubKey, ["protocol"] = 1, ["pro"] = true, ["mode"] = "default-allow",
            })
            .Post("hub/service/update", request =>
            {
                updates.Enqueue(request.State);
                return null;
            });
        var port = FreePort();
        using var server = hub.Listen(port, new ListenOptions<IReadOnlyDictionary<string, string?>>
        {
            StateFactory = headers =>
            {
                var copy = new Dictionary<string, string?>(headers, StringComparer.OrdinalIgnoreCase);
                connects.Enqueue(copy);
                return ValueTask.FromResult<IReadOnlyDictionary<string, string?>>(copy);
            },
            OnConnect = body => latest = body.Sender,
        });

        Keychain.SetKeyPath(Path.Combine(_tmp, "auth"));
        var address = $"ws://127.0.0.1:{port}";
        var service = new Service(new ServiceOptions
        {
            Address = address,
            Name = "Echo",
            DeviceName = "Mac Studio",
            DeviceKeyPath = Path.Combine(_tmp, "device.key"),
        });
        service.Post("echo/caller", (_, request) => new Dictionary<string, object?>
        {
            ["key"] = request.Caller!.Key,
            ["device"] = request.Caller.DeviceName,
            ["owner"] = request.Caller.Owner,
            ["hubKey"] = request.HubKey,
            ["service"] = request.Context.GetValueOrDefault("service"),
        });
        service.Start();
        try
        {
            // First connect v0 → hub/info → pin → reconnect with v1, then the update.
            await Await(() => !updates.IsEmpty);
            Assert.True(connects.Count >= 2, $"expected a v0 connect then a v1 connect, got {connects.Count}");
            Assert.Equal("0", connects.First()["v"]);
            var v1 = updates.Last();
            Assert.Equal("1", v1["v"]);
            var auth = v1["auth"]!.Split('.');
            Assert.True(TestKeys.Verifies(auth[1], $"hub1:{TestKeys.HubKey}:{auth[3]}", auth[2]));
            Assert.Equal($"{{\"{address}\":\"{TestKeys.HubKey}\"}}", File.ReadAllText(Path.Combine(_tmp, "hubs.json")));
            Assert.Equal(TestKeys.HubKey, service.HubKey);

            var payload = Device.DecodeCertificate(v1["device"]!);
            Assert.Equal(auth[1], payload["service"]);
            Assert.Equal("Echo", payload["instance"]);

            var context = new Dictionary<string, object?>
            {
                ["service"] = "s1",
                ["hub"] = new Dictionary<string, object?>
                {
                    ["caller"] = new Dictionary<string, object?>
                    {
                        ["key"] = auth[1],
                        ["device"] = new Dictionary<string, object?>
                        {
                            ["id"] = Device.Id((string)payload["device"]!), ["name"] = "Mac Studio",
                        },
                        ["groups"] = new[] { "owners" },
                        ["owner"] = true,
                    },
                    ["hubKey"] = TestKeys.HubKey,
                },
            };
            await Await(() => latest is not null);
            var result = (Dictionary<string, object?>)JsonBody.Decode(
                await latest!.SendAsync<object?>("echo/caller", null, context))!;
            Assert.Equal(auth[1], result["key"]);
            Assert.Equal("Mac Studio", result["device"]);
            Assert.Equal(true, result["owner"]);
            Assert.Equal(TestKeys.HubKey, result["hubKey"]);
            Assert.Equal("s1", result["service"]);
        }
        finally
        {
            service.Stop();
        }
    }
}
