using System.Text.Json;
using System.Text.Json.Serialization;
using HubServiceCs;

public class ServiceTest
{
    // Mirrors ChannelCs.JsonCodec options (Web defaults, camelCase, ignore nulls).
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string Json(object value) => JsonSerializer.Serialize(value, Options);

    [Fact]
    public void ServicesListIncludesRegisteredPaths()
    {
        var service = new Service();
        service.Post("echo", body => body);
        service.Stream("echo/values", body => Empty());
        service.App(new App { Header = new AppHeader { Type = "app", Name = "Echo", Path = "echo/ui" }, Body = ["hi"] });

        var update = service.UpdatePayload();
        var services = Assert.IsType<List<object?>>(update["services"]);
        Assert.Equal(3, services.Count);
        var apps = Assert.IsType<List<object?>>(update["apps"]);
        var app = Assert.IsType<Dictionary<string, object?>>(Assert.Single(apps));
        Assert.Equal("app", app["type"]);
        Assert.Equal("Echo", app["name"]);
        Assert.Equal("echo/ui", app["path"]);
    }

    [Fact]
    public void SettingsApplyToUpdatePayload()
    {
        var service = new Service();
        service.Post("echo", body => body, new ApiSettings
        {
            Permissions = new ServicePermissions { Name = "Echo", Allows = true },
            Limit = 5,
        });

        var update = service.UpdatePayload();
        var entry = Assert.IsType<Dictionary<string, object?>>(
            Assert.Single(Assert.IsType<List<object?>>(update["services"])));
        Assert.Equal(5, entry["limit"]);
        var permissions = Assert.IsType<Dictionary<string, object?>>(entry["permissions"]);
        Assert.Equal("echo", permissions["group"]);
        Assert.Equal("Echo", permissions["name"]);
        Assert.Equal(true, permissions["allows"]);
    }

    [Fact]
    public void ProfileOnlyWhenSet()
    {
        var plain = new Service();
        Assert.Null(plain.ProfilePayload());

        var named = new Service(new ServiceOptions { Name = "Echo" });
        var profile = named.ProfilePayload();
        Assert.NotNull(profile);
        Assert.Equal("Echo", profile!["name"]);

        var icon = new Service(new ServiceOptions
        {
            Name = "Echo",
            Icon = new Dictionary<string, object?> { ["symbol"] = "speaker.wave.2", ["text"] = "E" },
        });
        var withIcon = icon.ProfilePayload();
        Assert.Equal("""{"name":"Echo","icon":{"symbol":"speaker.wave.2","text":"E"}}""", Json(withIcon!));
    }

    [Fact]
    public void UpdateJsonMatchesWireContract()
    {
        var service = new Service();
        service.Post("echo", body => body, new ApiSettings
        {
            Permissions = new ServicePermissions { Name = "Echo", Allows = true },
            Limit = 5,
        });
        service.App(new App { Header = new AppHeader { Type = "app", Name = "Echo", Path = "echo/ui" } });

        var json = Json(service.UpdatePayload());
        Assert.Equal(
            """{"services":[{"path":"echo","permissions":{"group":"echo","name":"Echo","allows":true},"limit":5},{"path":"echo/ui"}],"apps":[{"type":"app","name":"Echo","path":"echo/ui"}]}""",
            json);
    }

    [Fact]
    public async Task SendBeforeStartThrows()
    {
        var service = new Service();
        Assert.Throws<InvalidOperationException>(() => service.Values("echo", "x"));
        Assert.Throws<InvalidOperationException>(() => service.Values("echo"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Send("echo", "x"));
    }

    [Fact]
    public void AppInterfaceHasAllFiveKeys()
    {
        var header = new AppHeader { Type = "app", Name = "Echo", Path = "echo/ui" };
        var body = new object?[] { "Hello from C#", App.Text("hi") };
        var iface = Service.Interface(header, body, null, null, new Dictionary<string, object?>());

        Assert.Equal(5, iface.Count);
        Assert.Equal(body, iface["body"]);
        Assert.Null(iface["top"]);
        Assert.Null(iface["bottom"]);
        var headerDict = Assert.IsType<Dictionary<string, object?>>(iface["header"]);
        Assert.Equal("app", headerDict["type"]);
        Assert.Equal("Echo", headerDict["name"]);
        Assert.Equal("echo/ui", headerDict["path"]);
        Assert.Empty(Assert.IsType<Dictionary<string, object?>>(iface["data"]));

        // All 5 keys must survive serialization (WhenWritingNull must not drop
        // the null top/bottom values).
        var json = Json(iface);
        Assert.Contains("\"top\":null", json);
        Assert.Contains("\"bottom\":null", json);
    }

    private static async IAsyncEnumerable<object?> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }
}
