namespace HubServiceCs;

// ed25519 keychain: load-or-generate the `auth` key file and sign hub-server
// auth tokens. Wire parity with hub-service/keychain.ts and
// Docs/Devices/protocol.md section 2:
//
// - The key file (`auth` in the working directory, or $HUBKEY) is the base64
//   encoding of the 48-byte PKCS8 DER ed25519 private key followed by the
//   44-byte SPKI DER public key (92 bytes total, 124 base64 chars).
// - A signed token has the shape `key.<pub>.<sig>.<expiry>` where:
//     pub     = base64 of the 44-byte SPKI public key (bytes 48..92)
//     expiry  = DECIMAL epoch-seconds string of now + 60
//     sig     = base64 ed25519 signature over `<expiry>` (header `v: 0`) or
//               over `hub1:<hub SPKI>:<expiry>` (header `v: 1`, bound to one
//               hub). hub-server allows 30 s of clock skew and at most 300 s
//               ahead.
// - hub-server verifies the signature against the embedded public key and treats
//   the key as the connection identity (owner/host/group permissions);
//   hub-lite ignores the auth header entirely.
public static class Keychain
{
    // DefaultExpires is the token lifetime in seconds (protocol.md: now + 60).
    public const int DefaultExpires = 60;

    private static readonly object Gate = new();
    private static string? _keyPath;
    private static KeyFile? _key;

    // Overrides the key file location (default: `auth` in the cwd, or $HUBKEY).
    public static void SetKeyPath(string? path)
    {
        lock (Gate)
        {
            _keyPath = path;
            _key = null;
        }
    }

    // KeyPath is the key file location in use. Hub key pins (`hubs.json`)
    // live beside it.
    public static string KeyPath()
    {
        lock (Gate)
        {
            if (!string.IsNullOrEmpty(_keyPath))
            {
                return _keyPath;
            }
        }

        var env = Environment.GetEnvironmentVariable("HUBKEY");
        return string.IsNullOrEmpty(env) ? "auth" : env;
    }

    // Key is the service key, loaded or generated on first use.
    public static KeyFile Key()
    {
        lock (Gate)
        {
            return _key ??= KeyFile.LoadOrCreate(KeyPath(), secure: false);
        }
    }

    // The service's public key: base64 of the 44-byte SPKI DER public key.
    // This is the connection identity hub-server associates permissions with.
    public static string PublicKey()
    {
        return Key().PublicKey;
    }

    // A fresh v0 auth token: `key.<pub>.<sig>.<expiry>`. Call once per
    // connect/reconnect (the hub treats each token as a fresh identity).
    public static string Sign(int expires = DefaultExpires)
    {
        return Token(Key(), null, Now() + expires);
    }

    // A fresh v1 auth token bound to hubKey (the hub's SPKI).
    public static string SignHub(string hubKey, int expires = DefaultExpires)
    {
        return Token(Key(), hubKey, Now() + expires);
    }

    // Token builds an auth token with key expiring at expiry: v1 (over
    // `hub1:<hubKey>:<expiry>`) when hubKey is set, else v0 (over `<expiry>`).
    public static string Token(KeyFile key, string? hubKey, long expiry)
    {
        var data = expiry.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var signed = hubKey is null ? data : $"hub1:{hubKey}:{data}";
        return $"key.{key.PublicKey}.{key.Sign(signed)}.{data}";
    }

    internal static long Now()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}
