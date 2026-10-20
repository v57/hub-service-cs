using System.Text.Json;
using HubServiceCs;

// The device layer test vectors (Docs/Devices/vectors.json, copied to
// testdata/device-vectors.json). Ed25519 is deterministic, so the exact
// strings must match.
public class VectorsTest
{
    private static readonly JsonElement Vectors =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "device-vectors.json")))
            .RootElement;

    private static JsonElement At(params string[] path)
    {
        var value = Vectors;
        foreach (var key in path)
        {
            value = value.GetProperty(key);
        }

        return value;
    }

    private static string Text(params string[] path) => At(path).GetString()!;

    private static KeyFile Key(string name) => KeyFile.Parse(Text("keys", name, "keyFile"));

    [Fact]
    public void KeyFilesGiveThePublicKeys()
    {
        foreach (var name in new[] { "hub", "service", "device" })
        {
            Assert.Equal(Text("keys", name, "publicKey"), Key(name).PublicKey);
        }
    }

    [Fact]
    public void DeviceIdAndSafetyCode()
    {
        var publicKey = Text("keys", "device", "publicKey");
        Assert.Equal(Text("keys", "device", "deviceId"), Device.Id(publicKey));
        Assert.Equal(Text("keys", "device", "safetyCode"), Device.SafetyCode(publicKey));
    }

    [Fact]
    public void AuthTokensV0AndV1()
    {
        var expiry = At("auth", "expiry").GetInt64();
        var service = Key("service");
        var hubKey = Text("keys", "hub", "publicKey");

        Assert.Equal(Text("auth", "v0", "signature"), service.Sign(Text("auth", "v0", "signedData")));
        Assert.Equal(Text("auth", "v0", "header"), Keychain.Token(service, null, expiry));

        Assert.Equal($"hub1:{hubKey}:{expiry}", Text("auth", "v1", "signedData"));
        Assert.Equal(Text("auth", "v1", "signature"), service.Sign(Text("auth", "v1", "signedData")));
        Assert.Equal(Text("auth", "v1", "header"), Keychain.Token(service, hubKey, expiry));
    }

    [Fact]
    public void Certificate()
    {
        CheckCertificate("certificate", "s3-local/Backup Disk", "Mac Studio", "macos", null);
    }

    [Fact]
    public void ChildCertificateWithParent()
    {
        CheckCertificate("childCertificate", "builder", "builder", "linux", "Mac Studio");
    }

    private static void CheckCertificate(string vector, string instance, string deviceName, string platform, string? parent)
    {
        var device = Key("device");
        var payload = Device.CertificatePayload(
            device.PublicKey, Key("service").PublicKey, instance, deviceName, platform, parent, 1792500000L, 1793104800L);
        Assert.Equal(Text(vector, "json"), Device.PayloadJson(payload));
        var header = Device.Certificate(device, payload);
        Assert.Equal(Text(vector, "header"), header);
        Assert.Equal($"{Text(vector, "payload")}.{Text(vector, "signature")}", header);
        Assert.Equal(Text(vector, "signature"), device.Sign(Text(vector, "payload")));
        Assert.Equal(parent, Device.DecodeCertificate(header).GetValueOrDefault("parent"));
    }

    [Fact]
    public void CallerContext()
    {
        var context = JsonBody.Decode(At("caller", "context"));
        var caller = Caller.FromContext(context)!;
        Assert.Equal(Text("keys", "service", "publicKey"), caller.Key);
        Assert.Equal(Text("keys", "device", "deviceId"), caller.DeviceId);
        Assert.Equal("Mac Studio", caller.DeviceName);
        Assert.Equal(["owners"], caller.Groups);
        Assert.True(caller.Owner);
        Assert.Equal(Text("keys", "hub", "publicKey"), Caller.HubKey(context));
    }
}
