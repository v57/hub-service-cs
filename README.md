<h1>
  <img alt="Hub Service logo" src="./icon.png" width="70" valign="middle">
  &nbsp;hub-service-cs
</h1>

C# port of [hub-service](https://github.com/v57/hub-service) — connect to a
[hub](https://github.com/v57/hub-lite) (default `ws://127.0.0.1:1997`), host
your own post/stream API endpoints, register apps, and call API through the hub.

hub-server auth is supported: every (re)connect sends `auth` + `v` headers signed
by the ed25519 keychain (`auth` key file in the cwd, or `$HUBKEY`), bound to
the hub's key once it is pinned (`v: 1`). hub-lite ignores the headers. The
wire contract matches the TS reference (`hub/profile/update`, `hub/service/update`,
AppInterface + element DSL). Built on [channel-cs](https://github.com/v57/channel-cs).

## Install

Add a project reference (channel-cs lives as a sibling checkout):

```xml
<ItemGroup>
  <ProjectReference Include="../../hub-service-cs/src/HubServiceCs/HubServiceCs.csproj" />
</ItemGroup>
```

## Usage

Start a hub first (`new Hub()` from hub-lite, listens on 1997), then:

```csharp
using HubServiceCs;

var service = new Service(new ServiceOptions
{
    Name = "Echo",
    Icon = new Dictionary<string, object?> { ["symbol"] = "speaker.wave.2", ["text"] = "E" },
});

service
    .Post("echo", body => body)
    .Post("echo/upper", body => body?.ToString()?.ToUpperInvariant())
    .App(new App
    {
        Header = new AppHeader { Type = "app", Name = "Echo", Path = "echo/ui" },
        Body =
        [
            "Hello from C#",
            App.TextField("echo", "Text to echo",
                new AppAction { Path = "echo/upper", Body = "value", Output = "value" }),
            App.Cell("$Echo", "value"),
        ],
        Data = new Dictionary<string, object?> { ["hello"] = "world" },
    })
    .Start();

await Task.Delay(Timeout.Infinite);
```

Call it through the hub (any language):

```csharp
var response = await service.Send("echo/upper", "hello"); // "HELLO"
await foreach (var value in service.Values("echo/ui")) { /* ... */ }
```

## API

- `new Service(ServiceOptions?)` — `Address` (port or `ws://` URL; default
  `1997` or `$HUB`), `Name`, `Icon`, `Sign`, `HubKey`
- Hub-bound auth: the first connect uses `v: 0`, asks `hub/info` for the hub
  key, pins it in `hubs.json` beside the key file and reconnects with `v: 1`
  (`HubKey` supplies it up front, `ForgetHubKey()` clears the pin,
  `LastError` reports a refused connect). `Keychain.Sign()` /
  `SignHub(key)` make the tokens
- Device mode (on by default): every connect also sends a `device` header, a
  7-day certificate for the service key signed by the device key
  (`~/.hub/device.key`, `$HUB_DEVICE_KEY` or `DeviceKeyPath`, created 0600;
  `DeviceKey = KeyFile` for a key kept elsewhere), renewed at connect with
  less than a day left. `Instance` names the service on its device (default:
  profile name, else the working directory's name, else `service`); `DeviceName`,
  `Platform`, `Parent` override the suggestions; `Device = false` turns it off
- `Post(path, Func<object?, object?> handler, ApiSettings? settings = null)` — fluent
- `Post(path, Func<object?, Task<object?>> handler, ApiSettings? settings = null)` — async variant
- `Post(path, Func<object?, Request, object?> handler, ...)` (and the async
  variant) — the handler also gets the `Request`: `Caller` (who is calling,
  from the hub's `context.hub.caller`: `Key`, `DeviceId`, `DeviceName`,
  `Groups`, `Owner`, and `Via`/`Claimed` through a merged hub; null on hubs
  that don't send it, and unless the request's `context.hub.hubKey` is the
  pinned hub key), `HubKey` and the whole `Context`
- `Stream(path, Func<object?, IAsyncEnumerable<object?>> handler, ApiSettings? settings = null)` — fluent
  (also with a `(body, request)` handler)
- Hub Network shares (TS `publishShare` parity): `Share(name, protocol, host,
  port, policy?)` declares one before `Start` (republished on every connect);
  `PublishShare(...)`, `UpdateShare(name, policy?, port?)` and
  `RemoveShare(name)` change the set at runtime. The local hub-net agent
  attests each share over its Unix socket (`$HUB_NET_AGENT_SOCKET`, else
  `/var/run/hub-net/agent.sock` on macOS, `/run/hub-net/agent.sock` on Linux;
  `Network.AgentSocket` overrides it), then the set goes to the hub with
  `network/shares/replace`. `Network.Publications` lists the results
- `App(App app)` — append AppHeader + register stream at `header.Path` yielding
  the AppInterface (all 5 keys: `header`, `body`, `top`, `bottom`, `data`)
- `Send(path, body?, context?)` — `Task<object?>` client call through the hub
  (throws `InvalidOperationException` before `Start`)
- `Values(path, body?, context?)` — `IAsyncEnumerable<object?>` stream call
  through the hub (throws `InvalidOperationException` before `Start`)
- `Start()` — connect; re-announces `hub/profile/update` + `hub/service/update`
  on every reconnect (the client auto-reconnects); `Stop()` disconnects

Handlers receive the decoded JSON body as plain C# objects (`string`, `long`/`double`,
`bool`, `null`, `Dictionary<string, object?>` for objects, `List<object?>` for
arrays). `ApiSettings` carries `Permissions` (group auto-filled from the first
path segment, TS parity) and `Limit` — wire-parity fields that hub-lite ignores.
App elements: `App.Text`, `App.Progress`, `App.TextField`, `App.Button`,
`App.Slider`, `App.List`, `App.Picker`, `App.Cell`, `App.Files`,
`App.FileOperation`, `App.HStack`, `App.VStack`, `App.ZStack`, `App.Spacer`
(plain strings in the body are shorthand text).

## Test

```bash
./scripts/test                         # unit tests
dotnet run --project src/HubServiceCs.Example   # example service (needs a hub on 1997)
# cross-language check (TS):
bun ../hub-service/tests/verify.ts echo echo/upper '"hello"' '"HELLO"'
bun ../hub-service/tests/appcheck.ts echo/ui Echo
```
