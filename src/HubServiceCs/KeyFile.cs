using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace HubServiceCs;

// KeyFile is an ed25519 key in the hub key file format
// (Docs/Devices/protocol.md section 1): base64 of the 48-byte PKCS8 DER
// private key followed by the 44-byte SPKI DER public key (92 bytes).
// Service keys (`auth`) and the device key (`~/.hub/device.key`) both use it.
public sealed class KeyFile
{
    // TS keychain layout: PKCS8 DER private key (48 bytes for ed25519)
    // followed by SPKI DER public key (44 bytes for ed25519).
    public const int PrivateDerLength = 48;
    public const int PublicDerLength = 44;

    // Fixed DER prefixes for ed25519 (see hub-service-dart keychain note:
    // 302e020100300506032b657004220420 / 302a300506032b6570032100).
    private static readonly byte[] Pkcs8Prefix = Convert.FromHexString("302e020100300506032b657004220420");
    private static readonly byte[] SpkiPrefix = Convert.FromHexString("302a300506032b6570032100");

    private readonly byte[] _data;
    private readonly Ed25519PrivateKeyParameters _privateKey;

    private KeyFile(byte[] data)
    {
        if (data.Length != PrivateDerLength + PublicDerLength)
        {
            throw new ArgumentException($"expected {PrivateDerLength + PublicDerLength} bytes, got {data.Length}");
        }

        _data = data;
        _privateKey = (Ed25519PrivateKeyParameters)PrivateKeyFactory.CreateKey(data[..PrivateDerLength]);
    }

    // Text is the key file text: base64 of the 92 bytes.
    public string Text => Convert.ToBase64String(_data);

    // PublicKey is base64 of the 44-byte SPKI DER public key.
    public string PublicKey => Convert.ToBase64String(PublicDer);

    // PublicDer is the 44-byte SPKI DER public key.
    public byte[] PublicDer => _data[PrivateDerLength..];

    // Parse reads key file text (base64, surrounding whitespace ignored).
    public static KeyFile Parse(string text)
    {
        return new KeyFile(Convert.FromBase64String(text.Trim()));
    }

    // Generate makes a fresh random key.
    public static KeyFile Generate()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var privateKey = (Ed25519PrivateKeyParameters)pair.Private;
        var publicKey = (Ed25519PublicKeyParameters)pair.Public;
        // BC GetEncoded() returns the raw 32-byte keys; re-wrap in the fixed
        // DER containers (PKCS8 private / SPKI public) for TS key-layout parity.
        return new KeyFile(Pkcs8Prefix.Concat(privateKey.GetEncoded())
            .Concat(SpkiPrefix)
            .Concat(publicKey.GetEncoded())
            .ToArray());
    }

    // Load reads the key file at path.
    public static KeyFile Load(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Invalid key file '{path}': {error.Message}", error);
        }
    }

    // LoadOrCreate reads the key file at path, creating it if absent. With
    // secure, the parent directory is created 0700 and the file written 0600
    // to a temp file renamed into place (device key rules, protocol.md
    // section 3).
    public static KeyFile LoadOrCreate(string path, bool secure)
    {
        if (File.Exists(path))
        {
            return Load(path);
        }

        var key = Generate();
        if (secure)
        {
            WriteAtomic(path, key.Text, secure: true);
        }
        else
        {
            File.WriteAllText(path, key.Text);
        }

        return key;
    }

    // Sign returns the base64 ed25519 signature over the UTF-8 bytes of text.
    public string Sign(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var signer = new Ed25519Signer();
        signer.Init(true, _privateKey);
        signer.BlockUpdate(data, 0, data.Length);
        return Convert.ToBase64String(signer.GenerateSignature());
    }

    // WriteAtomic writes text to a temp file beside path and renames it into place.
    internal static void WriteAtomic(string path, string text, bool secure)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!Directory.Exists(directory))
        {
            if (secure && !OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            else
            {
                Directory.CreateDirectory(directory);
            }
        }

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (secure && !OperatingSystem.IsWindows())
            {
                using var stream = new FileStream(temp, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
            else
            {
                File.WriteAllText(temp, text);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
