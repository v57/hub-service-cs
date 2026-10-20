using System.Text;
using HubServiceCs;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

// Every test class that touches the static Keychain shares this collection,
// so xUnit doesn't run them in parallel.
[CollectionDefinition("Keychain")]
public class KeychainCollection;

public static class TestKeys
{
    public const string HubKey = "MCowBQYDK2VwAyEAiojj3XQJ8ZX9UtstPLpdcspnCb8dlBIb83SIAbQPb1w=";

    public static bool Verifies(string publicKey, string data, string signature)
    {
        var key = (Ed25519PublicKeyParameters)PublicKeyFactory.CreateKey(Convert.FromBase64String(publicKey));
        var verifier = new Ed25519Signer();
        verifier.Init(false, key);
        var bytes = Encoding.UTF8.GetBytes(data);
        verifier.BlockUpdate(bytes, 0, bytes.Length);
        return verifier.VerifySignature(Convert.FromBase64String(signature));
    }

    public static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "hubservice-cs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

[Collection("Keychain")]
public sealed class HubAuthTest : IDisposable
{
    private readonly string _tmp = TestKeys.TempDirectory();

    public HubAuthTest()
    {
        Keychain.SetKeyPath(Path.Combine(_tmp, "auth"));
    }

    public void Dispose()
    {
        Keychain.SetKeyPath(null);
        Directory.Delete(_tmp, recursive: true);
    }

    [Fact]
    public void SignHubSignsHubBoundString()
    {
        var parts = Keychain.SignHub(TestKeys.HubKey).Split('.');
        Assert.Equal(4, parts.Length);
        Assert.True(TestKeys.Verifies(parts[1], $"hub1:{TestKeys.HubKey}:{parts[3]}", parts[2]));
        Assert.False(TestKeys.Verifies(parts[1], parts[3], parts[2]));
    }

    [Fact]
    public void ConnectsWithV0UntilAHubKeyIsKnown()
    {
        var service = new Service(new ServiceOptions { Address = 4100 });
        Assert.Null(service.HubKey);
        Assert.Equal("0", service.AuthHeaders()["v"]);
    }

    [Fact]
    public void HubKeyOptionSignsV1()
    {
        var headers = new Service(new ServiceOptions { Address = 4100, HubKey = TestKeys.HubKey }).AuthHeaders();
        Assert.Equal("1", headers["v"]);
        var parts = headers["auth"].Split('.');
        Assert.True(TestKeys.Verifies(parts[1], $"hub1:{TestKeys.HubKey}:{parts[3]}", parts[2]));
    }

    [Fact]
    public void PinnedKeyBesideKeyFileIsUsedAndForgotten()
    {
        var pinsPath = Path.Combine(_tmp, "hubs.json");
        new HubPins(pinsPath).Put("ws://localhost:4100", TestKeys.HubKey);
        Assert.Equal($"{{\"ws://localhost:4100\":\"{TestKeys.HubKey}\"}}", File.ReadAllText(pinsPath));

        var service = new Service(new ServiceOptions { Address = 4100 });
        Assert.Equal(TestKeys.HubKey, service.HubKey);
        Assert.Equal("1", service.AuthHeaders()["v"]);
        Assert.Null(new Service(new ServiceOptions { Address = "ws://other:1997" }).HubKey);

        service.ForgetHubKey();
        Assert.Null(service.HubKey);
        Assert.Equal("0", service.AuthHeaders()["v"]);
        Assert.Equal("{}", File.ReadAllText(pinsPath));
    }

    [Fact]
    public void AddressesNormalizeLikeChannelTs()
    {
        Assert.Equal("ws://localhost:1997", Service.NormalizeAddress(1997));
        Assert.Equal("ws://localhost:1997", Service.NormalizeAddress("1997"));
        Assert.Equal("wss://hub.example.com/path", Service.NormalizeAddress("WSS://Hub.Example.COM/path/"));
        Assert.Equal("ws://127.0.0.1:1997", Service.NormalizeAddress("ws://127.0.0.1:1997/"));
    }

    [Fact]
    public void CustomSignStaysV0()
    {
        var headers = new Service(new ServiceOptions { HubKey = TestKeys.HubKey, Sign = () => "key.custom" }).AuthHeaders();
        Assert.Equal("key.custom", headers["auth"]);
        Assert.Equal("0", headers["v"]);
    }

    [Fact]
    public void CorruptPinFileCountsAsEmpty()
    {
        var pinsPath = Path.Combine(_tmp, "hubs.json");
        File.WriteAllText(pinsPath, "not json");
        var pins = new HubPins(pinsPath);
        Assert.Null(pins.Get("ws://localhost:1997"));
        pins.Put("ws://localhost:1997", TestKeys.HubKey);
        Assert.Equal(TestKeys.HubKey, pins.Get("ws://localhost:1997"));
    }
}
