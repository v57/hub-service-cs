using HubServiceCs;

[Collection("Keychain")]
public sealed class DeviceTest : IDisposable
{
    private readonly string _tmp = TestKeys.TempDirectory();

    public DeviceTest()
    {
        Keychain.SetKeyPath(Path.Combine(_tmp, "auth"));
    }

    public void Dispose()
    {
        Keychain.SetKeyPath(null);
        Directory.Delete(_tmp, recursive: true);
    }

    private string DeviceKeyPath => Path.Combine(_tmp, "dev", "device.key");

    private Service NewService(
        string? name = null,
        string? instance = null,
        string? deviceName = "box",
        string? platform = null,
        string? parent = null,
        bool device = true,
        KeyFile? deviceKey = null)
    {
        return new Service(new ServiceOptions
        {
            Address = 4100,
            Name = name,
            Instance = instance,
            DeviceName = deviceName,
            Platform = platform,
            Parent = parent,
            Device = device,
            DeviceKey = deviceKey,
            DeviceKeyPath = DeviceKeyPath,
        });
    }

    [Fact]
    public void CreatesDeviceKeyWithPrivateModes()
    {
        var path = Path.Combine(_tmp, "hub", "device.key");
        var key = Device.LoadOrCreateKey(path);
        Assert.Equal(124, File.ReadAllText(path).Length);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        Assert.Equal(key.PublicKey, Device.LoadOrCreateKey(path).PublicKey);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void ConnectHeadersCarryASignedCertificate()
    {
        var service = NewService(name: "Echo", deviceName: "Mac Studio", platform: "macos");
        var headers = service.AuthHeaders();
        var header = headers["device"];
        var parts = header.Split('.');
        Assert.Equal(2, parts.Length);
        var payload = Device.DecodeCertificate(header);
        var deviceKey = service.DeviceKey!.PublicKey;
        Assert.True(TestKeys.Verifies(deviceKey, parts[0], parts[1]));
        Assert.Equal(1L, (long)payload["v"]!);
        Assert.Equal(deviceKey, payload["device"]);
        Assert.Equal(Keychain.PublicKey(), payload["service"]);
        Assert.Equal(headers["auth"].Split('.')[1], payload["service"]);
        Assert.Equal("Echo", payload["instance"]);
        Assert.Equal("Mac Studio", payload["deviceName"]);
        Assert.Equal("macos", payload["platform"]);
        Assert.False(payload.ContainsKey("parent"));
        var iat = (long)payload["iat"]!;
        var exp = (long)payload["exp"]!;
        Assert.Equal(Device.CertificateLifetime, exp - iat);
        Assert.InRange(iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2);
    }

    [Fact]
    public void CertificateIsReusedUntilRenewal()
    {
        var service = NewService();
        Assert.Same(service.AuthHeaders()["device"], service.AuthHeaders()["device"]);
    }

    [Fact]
    public void InstanceDefaults()
    {
        Assert.Equal("s3-local/Backup Disk", NewService(name: "Backup", instance: "s3-local/Backup Disk").Instance);
        Assert.Equal("Backup", NewService(name: "Backup").Instance);
        var byPath = NewService();
        Assert.Equal("service", byPath.Instance);
        byPath.Post("s3/read", body => body);
        byPath.Post("other/x", body => body);
        Assert.Equal("s3", byPath.Instance);
    }

    [Fact]
    public void ParentIsDeclared()
    {
        var service = NewService(deviceName: "builder", platform: "linux", parent: "Mac Studio");
        Assert.Equal("Mac Studio", Device.DecodeCertificate(service.AuthHeaders()["device"])["parent"]);
    }

    [Fact]
    public void DeviceModeCanBeTurnedOff()
    {
        Assert.False(NewService(device: false).AuthHeaders().ContainsKey("device"));
        Assert.False(File.Exists(DeviceKeyPath));
    }

    [Fact]
    public void CallerProvidedKeyIsUsed()
    {
        var key = KeyFile.Generate();
        var header = NewService(deviceKey: key).AuthHeaders()["device"];
        Assert.Equal(key.PublicKey, Device.DecodeCertificate(header)["device"]);
        Assert.False(File.Exists(DeviceKeyPath));
    }

    [Fact]
    public void UnreadableDeviceKeyTurnsDeviceModeOff()
    {
        var path = Path.Combine(_tmp, "bad.key");
        File.WriteAllText(path, "garbage");
        var headers = new Service(new ServiceOptions { DeviceKeyPath = path }).AuthHeaders();
        Assert.False(headers.ContainsKey("device"));
        Assert.True(headers.ContainsKey("auth"));
    }
}
