using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace EPrescription.Api;

/// <summary>ECDSA P-256 / SHA-256 using the built-in .NET ECDsa class only (no custom crypto).</summary>
public class CryptoService
{
    readonly byte[] _master;
    const DSASignatureFormat Fmt = DSASignatureFormat.IeeeP1363FixedFieldConcatenation; // r||s, 64 bytes

    public const string QrPrefix = "MSQ1.";
    static readonly byte[] QrAad = Encoding.ASCII.GetBytes("MSQ1");
    readonly byte[] _qrKey;

    public CryptoService(IConfiguration cfg)
    {
        var s = cfg["KeyMaster"];
        if (string.IsNullOrWhiteSpace(s) || s.Length < 32)
            throw new InvalidOperationException("KeyMaster must be set (>=32 chars) via environment variable KeyMaster.");
        
        _master = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        _qrKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, _master, 32,
                        info: Encoding.UTF8.GetBytes("medsign/qr-envelope/v1"));
    }

    public (string PublicKeyB64, string PublicKeyPem, string EncryptedPrivateKey) GenerateKeyPair()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var priv = ec.ExportPkcs8PrivateKey();
        try { return (Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()), ec.ExportSubjectPublicKeyInfoPem(), Encrypt(priv)); }
        finally { CryptographicOperations.ZeroMemory(priv); }
    }

    public string Sign(string payload, string encryptedPrivateKey)
    {
        var priv = Decrypt(encryptedPrivateKey);
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportPkcs8PrivateKey(priv, out _);
            return Convert.ToBase64String(ec.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, Fmt));
        }
        finally { CryptographicOperations.ZeroMemory(priv); }
    }

    public bool Verify(string payload, string signatureB64, string publicKeyB64)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyB64), out _);
            return ec.VerifyData(Encoding.UTF8.GetBytes(payload), Convert.FromBase64String(signatureB64), HashAlgorithmName.SHA256, Fmt);
        }
        catch { return false; }
    }

    string Encrypt(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16]; var cipher = new byte[plain.Length];
        using var aes = new AesGcm(_master, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    byte[] Decrypt(string blob)
    {
        var all = Convert.FromBase64String(blob);
        var plain = new byte[all.Length - 28];
        using var aes = new AesGcm(_master, 16);
        aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain);
        return plain;
    }

    /// <summary>Encrypts the QR envelope (AES-256-GCM). Output: MSQ1.base64url(nonce|tag|ciphertext)</summary>
    public string SealQr(string envelopeJson)
    {
        var plain = Encoding.UTF8.GetBytes(envelopeJson);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16]; var cipher = new byte[plain.Length];
        using var aes = new AesGcm(_qrKey, 16);
        aes.Encrypt(nonce, plain, cipher, tag, QrAad);
        var blob = new byte[28 + cipher.Length];
        nonce.CopyTo(blob, 0); tag.CopyTo(blob, 12); cipher.CopyTo(blob, 28);
        return QrPrefix + Base64UrlEncoder.Encode(blob);
    }

    /// <summary>Returns the envelope JSON, or null if the data is not authentic (tampered / forged / wrong key).</summary>
    public string? OpenQr(string qr)
    {
        try
        {
            var all = Base64UrlEncoder.DecodeBytes(qr[QrPrefix.Length..]);
            if (all.Length < 29) return null;
            var plain = new byte[all.Length - 28];
            using var aes = new AesGcm(_qrKey, 16);
            aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain, QrAad);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }
}
