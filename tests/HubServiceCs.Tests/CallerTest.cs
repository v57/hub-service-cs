using HubServiceCs;

public class CallerTest
{
    [Fact]
    public void NoHubContextMeansNoCaller()
    {
        Assert.Null(Caller.FromContext(null));
        Assert.Null(Caller.FromContext(new Dictionary<string, object?> { ["service"] = "x" }));
        var request = Request.Of("echo", "hi", null);
        Assert.Null(request.Caller);
        Assert.Null(request.HubKey);
        Assert.Empty(request.Context);
    }

    [Fact]
    public void AnonymousCallerHasNoKeyOrDevice()
    {
        var caller = Caller.FromContext(new Dictionary<string, object?>
        {
            ["hub"] = new Dictionary<string, object?>
            {
                ["caller"] = new Dictionary<string, object?> { ["groups"] = new List<object?>(), ["owner"] = false },
            },
        })!;
        Assert.Null(caller.Key);
        Assert.False(caller.HasDevice);
        Assert.False(caller.Owner);
        Assert.Empty(caller.Groups);
    }

    [Fact]
    public void MergedHubCallerKeepsTheUnverifiedClaim()
    {
        var caller = Caller.FromContext(new Dictionary<string, object?>
        {
            ["hub"] = new Dictionary<string, object?>
            {
                ["caller"] = new Dictionary<string, object?>
                {
                    ["key"] = "merging",
                    ["groups"] = new List<object?> { "g1", 5L },
                    ["owner"] = false,
                    ["via"] = "merged-hub",
                    ["claimed"] = new Dictionary<string, object?>
                    {
                        ["key"] = "remote",
                        ["device"] = new Dictionary<string, object?> { ["id"] = "d1", ["name"] = "Phone" },
                        ["owner"] = true,
                    },
                },
            },
        })!;
        Assert.Equal("merging", caller.Key);
        Assert.Equal(["g1"], caller.Groups);
        Assert.True(caller.ViaMergedHub);
        Assert.Equal("remote", caller.Claimed!.Key);
        Assert.Equal("Phone", caller.Claimed.DeviceName);
        Assert.True(caller.Claimed.Owner);
    }
}
