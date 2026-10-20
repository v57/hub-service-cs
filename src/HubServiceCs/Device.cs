using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HubServiceCs;

// Device identity (Docs/Devices/protocol.md sections 1 and 3).
//
// The device key is one ed25519 key per OS instance, in the key file format,
// at $HUB_DEVICE_KEY, /var/lib/hub/device.key for root, or ~/.hub/device.key
// (directory 0700, file 0600, created atomically). It signs a short
// certificate for each service key it runs, sent in the `device` connection
// header next to `auth`.
public static class Device
{
    // CertificateLifetime is the default certificate lifetime: 7 days.
    public const long CertificateLifetime = 7 * 24 * 3600;

    // RenewBefore: a certificate with less than this left is renewed at
    // connect (24 hours).
    public const long RenewBefore = 24 * 3600;

    // DefaultKeyPath is $HUB_DEVICE_KEY, root's system path, or ~/.hub/device.key.
    public static string DefaultKeyPath()
    {
        var env = Environment.GetEnvironmentVariable("HUB_DEVICE_KEY");
        if (!string.IsNullOrEmpty(env))
        {
            return env;
        }

        if (!OperatingSystem.IsWindows() && Environment.UserName == "root")
        {
            return "/var/lib/hub/device.key";
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hub", "device.key");
    }

    // LoadOrCreateKey loads the device key at path, creating it (0600,
    // atomic) if absent.
    public static KeyFile LoadOrCreateKey(string path)
    {
        return KeyFile.LoadOrCreate(path, secure: true);
    }

    // Id is the device id: the first 32 lowercase hex characters of SHA-256 of
    // the SPKI DER.
    public static string Id(string publicKey)
    {
        return Hex(publicKey)[..32];
    }

    // SafetyCode is the code shown while approving: "ddd ddd".
    public static string SafetyCode(string publicKey)
    {
        var n = long.Parse(Hex(publicKey)[..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture) % 1_000_000;
        var code = n.ToString("D6", CultureInfo.InvariantCulture);
        return $"{code[..3]} {code[3..]}";
    }

    // DefaultName is the computer name on macOS, else the host name without
    // `.local`.
    public static string? DefaultName()
    {
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("scutil", "--get ComputerName")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                var output = process!.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (process.ExitCode == 0 && output.Length > 0)
                {
                    return output;
                }
            }
            catch
            {
                // Fall back to the host name.
            }
        }

        var name = Environment.MachineName;
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? name[..^".local".Length] : name;
    }

    // DefaultPlatform is the certificate `platform` for this OS.
    public static string DefaultPlatform()
    {
        if (OperatingSystem.IsMacOS())
        {
            return "macos";
        }

        if (OperatingSystem.IsIOS())
        {
            return "ios";
        }

        if (OperatingSystem.IsAndroid())
        {
            return "android";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        return OperatingSystem.IsWindows() ? "windows" : "other";
    }

    // CertificatePayload builds the certificate payload, fields in protocol
    // order with optional ones omitted when null: v, device, service,
    // instance, deviceName, platform, parent, iat, exp.
    public static IReadOnlyList<KeyValuePair<string, object>> CertificatePayload(
        string devicePublicKey,
        string servicePublicKey,
        string instance,
        string? deviceName,
        string? platform,
        string? parent,
        long iat,
        long exp)
    {
        var payload = new List<KeyValuePair<string, object>>
        {
            new("v", 1),
            new("device", devicePublicKey),
            new("service", servicePublicKey),
            new("instance", instance),
        };
        if (deviceName is not null)
        {
            payload.Add(new("deviceName", deviceName));
        }

        if (platform is not null)
        {
            payload.Add(new("platform", platform));
        }

        if (parent is not null)
        {
            payload.Add(new("parent", parent));
        }

        payload.Add(new("iat", iat));
        payload.Add(new("exp", exp));
        return payload;
    }

    // PayloadJson encodes a payload as JSON.stringify does: no whitespace,
    // only quotes, backslashes and control characters escaped.
    public static string PayloadJson(IReadOnlyList<KeyValuePair<string, object>> payload)
    {
        var builder = new StringBuilder("{");
        for (var i = 0; i < payload.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            WriteString(builder, payload[i].Key);
            builder.Append(':');
            switch (payload[i].Value)
            {
                case string text:
                    WriteString(builder, text);
                    break;
                case IFormattable number:
                    builder.Append(number.ToString(null, CultureInfo.InvariantCulture));
                    break;
                default:
                    throw new ArgumentException($"unsupported certificate field {payload[i].Key}");
            }
        }

        return builder.Append('}').ToString();
    }

    // Certificate signs a certificate: `<base64url(JSON)>.<base64 signature>`,
    // the signature covering the base64url payload string.
    public static string Certificate(KeyFile deviceKey, IReadOnlyList<KeyValuePair<string, object>> payload)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(PayloadJson(payload)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{encoded}.{deviceKey.Sign(encoded)}";
    }

    // DecodeCertificate decodes the payload of a `device` header (no signature
    // check) into plain C# objects.
    public static Dictionary<string, object?> DecodeCertificate(string header)
    {
        var encoded = header.Split('.')[0].Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
        using var document = JsonDocument.Parse(Convert.FromBase64String(encoded));
        return (Dictionary<string, object?>)JsonBody.Decode(document.RootElement.Clone())!;
    }

    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    private static string Hex(string publicKey)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(publicKey)));
    }
}
