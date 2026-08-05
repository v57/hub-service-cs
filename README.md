<h1>
  <img alt="Hub Service logo" src="./icon.png" width="70" valign="middle">
  &nbsp;hub-service-cs
</h1>

C# port of [hub-service](https://github.com/v57/hub-service) — connect to a
[hub](https://github.com/v57/hub-lite) (default `ws://127.0.0.1:1997`), host
your own post/stream API endpoints, register apps, and call API through the hub.

Lite port: no auth, no keychain, no crypto (hub-pro territory). The wire
contract matches the TS reference (`hub/profile/update`, `hub/service/update`,
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
  `1997` or `$HUB`), `Name`, `Icon`
- `Post(path, Func<object?, object?> handler, ApiSettings? settings = null)` — fluent
- `Post(path, Func<object?, Task<object?>> handler, ApiSettings? settings = null)` — async variant
- `Stream(path, Func<object?, IAsyncEnumerable<object?>> handler, ApiSettings? settings = null)` — fluent
- `App(App app)` — append AppHeader + register stream at `header.Path` yielding
  the AppInterface (all 5 keys: `header`, `body`, `top`, `bottom`, `data`)
- `Send(path, body?, context?)` — `Task<object?>` client call through the hub
  (throws `InvalidOperationException` before `Start`)
- `Values(path, body?, context?)` — `IAsyncEnumerable<object?>` stream call
  through the hub (throws `InvalidOperationException` before `Start`)
- `Start()` — connect; re-announces `hub/profile/update` + `hub/service/update`
  on every reconnect (the client auto-reconnects)

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
