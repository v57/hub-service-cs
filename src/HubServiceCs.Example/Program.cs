using HubServiceCs;

// Echo service: hosts post echo + echo/upper and an app interface at echo/ui,
// then connects to the hub (default ws://127.0.0.1:1997) and stays running.
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
            App.TextField("echo", "Text to echo", new Action { Path = "echo/upper", Body = "value", Output = "value" }),
            App.Cell("$Echo", "value"),
            App.Button("Upper", new Action { Path = "echo/upper", Body = "value", Output = "value" }),
        ],
        Data = new Dictionary<string, object?> { ["hello"] = "world" },
    })
    .Start();

Console.WriteLine("echo service started (hub-lite :1997)");
await Task.Delay(Timeout.Infinite);
