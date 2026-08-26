using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace CobbleMusicUpdater;

internal static class TrustedKeyRing
{
    public const string CurrentKeyId = "cobble-music-release-1";

    // The corresponding private signing seed is stored outside this repository
    // and never travels to a friend''s computer or an AI session.
    private const string BootstrapPublicKey = "u8M5PqOCBO0SYZn8ctnqxLrVaGNW29vfFftliVFEkQg=";

    private static readonly IReadOnlyDictionary<string, byte[]> Keys =
        new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [CurrentKeyId] = Convert.FromBase64String(BootstrapPublicKey)
        };

    public static bool TryGet(string keyId, out byte[] publicKey) =>
        Keys.TryGetValue(keyId, out publicKey!);
}

internal static class ManifestSecurity
{
    private const int PrivateSeedSize = 32;
    private const int PublicKeySize = 32;
    private const int SignatureSize = 64;

    public static bool Verify(byte[] manifestBytes, DetachedSignature detachedSignature)
    {
        if (!string.Equals(detachedSignature.Algorithm, "Ed25519", StringComparison.Ordinal)
            || !TrustedKeyRing.TryGet(detachedSignature.KeyId, out byte[] publicKey))
        {
            return false;
        }

        try
        {
            byte[] signature = Convert.FromBase64String(detachedSignature.Signature);
            if (signature.Length != SignatureSize || publicKey.Length != PublicKeySize)
            {
                return false;
            }

            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(manifestBytes, 0, manifestBytes.Length);
            return verifier.VerifySignature(signature);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static byte[] Sign(byte[] manifestBytes, byte[] privateSeed)
    {
        if (privateSeed.Length != PrivateSeedSize)
        {
            throw new ArgumentException("An Ed25519 private seed must be exactly 32 bytes.", nameof(privateSeed));
        }

        byte[] seedCopy = privateSeed.ToArray();
        try
        {
            var signer = new Ed25519Signer();
            signer.Init(true, new Ed25519PrivateKeyParameters(seedCopy, 0));
            signer.BlockUpdate(manifestBytes, 0, manifestBytes.Length);
            byte[] signature = signer.GenerateSignature();
            if (signature.Length != SignatureSize)
            {
                throw new CryptographicException("Unexpected Ed25519 signature size.");
            }
            return signature;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seedCopy);
        }
    }

    public static (byte[] PrivateSeed, byte[] PublicKey) GenerateKeyPair()
    {
        var privateKey = new Ed25519PrivateKeyParameters(new SecureRandom(new CryptoApiRandomGenerator()));
        return (privateKey.GetEncoded(), privateKey.GeneratePublicKey().GetEncoded());
    }
}
