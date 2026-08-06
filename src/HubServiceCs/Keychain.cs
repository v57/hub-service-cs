using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace HubServiceCs;

// ed25519 keychain: load-or-generate the `auth` key file and sign hub-pro
// auth tokens. Wire parity with hub-service/keychain.ts:
//
// - The key file (`auth` in the working directory, or $HUBKEY) is the base64
//   encoding of the 48-byte PKCS8 DER ed25519 private key followed by the
//   44-byte SPKI DER public key (92 bytes total, 124 base64 chars).
// - A signed token has the shape `key.<pub>.<sig>.<expiry>` where:
//     pub     = base64 of the 44-byte SPKI public key (bytes 48..92)
//     sig     = base64 ed25519 signature over the expiry string
//     expiry  = DECIMAL epoch-seconds string of now + 10 (TS parity; hub-pro
//               reads it as base-36, so the decimal form never trips expiry)
// - hub-pro verifies the signature against the embedded public key and treats
//   the key as the connection identity (owner/host/group permissions);
//   hub-lite ignores the auth header entirely.
public static class Keychain
{
    // TS keychain layout: PKCS8 DER private key (48 bytes for ed25519)
    // followed by SPKI DER public key (44 bytes for ed25519).
    private const int PrivateDerLength = 48;
    private const int PublicDerLength = 44;

    // Fixed DER prefixes for ed25519 (see hub-service-dart keychain note:
    // 302e020100300506032b657004220420 / 302a300506032b6570032100).
    private static readonly byte[] Pkcs8Prefix = Convert.FromHexString("302e020100300506032b657004220420");
    private static readonly byte[] SpkiPrefix = Convert.FromHexString("302a300506032b6570032100");

    private static readonly object Gate = new();
    private static string? _keyPath;
    private static (byte[] PrivateDer, byte[] PublicDer)? _key;

    // Overrides the key file location (default: `auth` in the cwd, or $HUBKEY).
    public static void SetKeyPath(string? path)
    {
        lock (Gate)
        {
            _keyPath = path;
            _key = null;
        }
    }

    // The service's public key: base64 of the 44-byte SPKI DER public key.
    // This is the connection identity hub-pro associates permissions with.
    public static string PublicKey()
    {
        var key = LoadOrGenerate();
        return Convert.ToBase64String(key.PublicDer);
    }

    // A fresh auth token: `key.<pub>.<sig>.<expiry>`. Call once per
    // connect/reconnect (the hub treats each token as a fresh identity).
    public static string Sign(int expires = 10)
    {
        var key = LoadOrGenerate();
        var data = Encoding.ASCII.GetBytes($"{DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expires}");
        var privateKey = (Ed25519PrivateKeyParameters)PrivateKeyFactory.CreateKey(key.PrivateDer);
        var signer = new Ed25519Signer();
        signer.Init(true, privateKey);
        signer.BlockUpdate(data, 0, data.Length);
        var signature = signer.GenerateSignature();
        return $"key.{Convert.ToBase64String(key.PublicDer)}.{Convert.ToBase64String(signature)}.{Encoding.ASCII.GetString(data)}";
    }

    private static (byte[] PrivateDer, byte[] PublicDer) LoadOrGenerate()
    {
        lock (Gate)
        {
            if (_key is not null)
            {
                return _key.Value;
            }

            var path = _keyPath ?? Environment.GetEnvironmentVariable("HUBKEY");
            if (string.IsNullOrEmpty(path))
            {
                path = "auth";
            }

            byte[] bytes;
            if (File.Exists(path))
            {
                bytes = Convert.FromBase64String(File.ReadAllText(path).Trim());
            }
            else
            {
                bytes = Generate();
                File.WriteAllText(path, Convert.ToBase64String(bytes));
            }

            if (bytes.Length != PrivateDerLength + PublicDerLength)
            {
                throw new InvalidOperationException(
                    $"Invalid key file '{path}': expected {PrivateDerLength + PublicDerLength} bytes, got {bytes.Length}");
            }

            _key = (bytes[..PrivateDerLength], bytes[PrivateDerLength..]);
            return _key.Value;
        }
    }

    private static byte[] Generate()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var privateKey = (Ed25519PrivateKeyParameters)pair.Private;
        var publicKey = (Ed25519PublicKeyParameters)pair.Public;
        // BC GetEncoded() returns the raw 32-byte keys; re-wrap in the fixed
        // DER containers (PKCS8 private / SPKI public) for TS key-layout parity.
        return Pkcs8Prefix.Concat(privateKey.GetEncoded())
            .Concat(SpkiPrefix)
            .Concat(publicKey.GetEncoded())
            .ToArray();
    }
}
