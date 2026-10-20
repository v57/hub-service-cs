using System.Text;
using HubServiceCs;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

[Collection("Keychain")]
public class KeychainTest
{
    // TS-generated test key (hub-pro owner key; also in
    // documentation/hub-pro-support-contract.md). auth = base64 of the
    // 48-byte PKCS8 private + 44-byte SPKI public; pub = the SPKI alone.
    private const string TsTestAuth =
        "MC4CAQAwBQYDK2VwBCIEINguo0cc0FACJ0oRV3V/+ZqIyj04ON5wdMJGH4a/do8CMCowBQYDK2VwAyEAHZSDpmbLRjLrOyNMugsStFqK5EYcNluUekE4di+FalQ=";
    private const string TsTestPub =
        "MCowBQYDK2VwAyEAHZSDpmbLRjLrOyNMugsStFqK5EYcNluUekE4di+FalQ=";

    [Fact]
    public void LoadsTsGeneratedAuthFileAndReproducesPublicKey()
    {
        var path = TempKeyPath();
        try
        {
            File.WriteAllText(path, TsTestAuth);
            Keychain.SetKeyPath(path);
            Assert.Equal(TsTestPub, Keychain.PublicKey());
        }
        finally
        {
            Keychain.SetKeyPath(null);
            File.Delete(path);
        }
    }

    [Fact]
    public void SignProducesTokenShapeWithVerifiableSignature()
    {
        var path = TempKeyPath();
        try
        {
            File.WriteAllText(path, TsTestAuth);
            Keychain.SetKeyPath(path);

            var token = Keychain.Sign();
            var parts = token.Split('.');
            Assert.Equal(4, parts.Length);
            Assert.Equal("key", parts[0]);
            Assert.Equal(TsTestPub, parts[1]);

            // Expiry is a DECIMAL epoch-seconds string of now + 60 (protocol.md;
            // hub-pro rejects it once expired or if over 5 minutes ahead).
            Assert.True(long.TryParse(parts[3], out var expiry));
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Assert.InRange(expiry, now + 59, now + 61);

            // The signature must verify against the public key embedded in the
            // token over the raw expiry STRING (hub-pro's verify() path).
            var publicKey = (Ed25519PublicKeyParameters)PublicKeyFactory.CreateKey(
                Convert.FromBase64String(parts[1]));
            var data = Encoding.ASCII.GetBytes(parts[3]);
            var signature = Convert.FromBase64String(parts[2]);
            Assert.True(Verify(publicKey, data, signature));
        }
        finally
        {
            Keychain.SetKeyPath(null);
            File.Delete(path);
        }
    }

    [Fact]
    public void GeneratesAuthFileWhenMissing()
    {
        var path = TempKeyPath();
        try
        {
            Keychain.SetKeyPath(path);
            Assert.False(File.Exists(path));

            var token = Keychain.Sign();
            Assert.True(File.Exists(path));

            // TS key layout: 92 bytes (48 PKCS8 + 44 SPKI) -> 124 base64 chars.
            var bytes = Convert.FromBase64String(File.ReadAllText(path).Trim());
            Assert.Equal(92, bytes.Length);
            Assert.Equal(124, File.ReadAllText(path).Trim().Length);

            var parts = token.Split('.');
            Assert.Equal(4, parts.Length);

            // Reload (force cache reset) reproduces the same key and the
            // public key embedded in the token is the file's SPKI.
            Keychain.SetKeyPath(path);
            Assert.Equal(parts[1], Keychain.PublicKey());
            Assert.Equal(Convert.ToBase64String(bytes[48..]), Keychain.PublicKey());

            var publicKey = (Ed25519PublicKeyParameters)PublicKeyFactory.CreateKey(
                Convert.FromBase64String(parts[1]));
            var data = Encoding.ASCII.GetBytes(parts[3]);
            Assert.True(Verify(publicKey, data, Convert.FromBase64String(parts[2])));
        }
        finally
        {
            Keychain.SetKeyPath(null);
            File.Delete(path);
        }
    }

    [Fact]
    public void EachSignCallProducesFreshToken()
    {
        var path = TempKeyPath();
        try
        {
            Keychain.SetKeyPath(path);
            // Same-second calls yield identical tokens (TS parity: expiry is
            // second-granular). A different expiry must produce a different
            // token whose expiry embeds the requested delta.
            var first = Keychain.Sign();
            var second = Keychain.Sign(expires: 12);
            Assert.NotEqual(first, second);
            var delta = long.Parse(second.Split('.')[3]) - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Assert.InRange(delta, 11, 12);
            // Both tokens carry the same identity (public key).
            Assert.Equal(first.Split('.')[1], second.Split('.')[1]);
        }
        finally
        {
            Keychain.SetKeyPath(null);
            File.Delete(path);
        }
    }

    [Fact]
    public void InvalidKeyFileThrows()
    {
        var path = TempKeyPath();
        try
        {
            File.WriteAllText(path, Convert.ToBase64String([1, 2, 3]));
            Keychain.SetKeyPath(path);
            Assert.Throws<InvalidOperationException>(() => Keychain.Sign());
        }
        finally
        {
            Keychain.SetKeyPath(null);
            File.Delete(path);
        }
    }

    // Mirrors hub-pro's verification: ed25519 over the expiry string against
    // the SPKI public key carried by the token.
    private static bool Verify(Ed25519PublicKeyParameters publicKey, byte[] data, byte[] signature)
    {
        var verifier = new Ed25519Signer();
        verifier.Init(false, publicKey);
        verifier.BlockUpdate(data, 0, data.Length);
        return verifier.VerifySignature(signature);
    }

    private static string TempKeyPath() =>
        Path.Combine(Path.GetTempPath(), $"hubkey-test-{Guid.NewGuid():N}");
}
