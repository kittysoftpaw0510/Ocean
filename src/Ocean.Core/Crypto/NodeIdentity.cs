using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ocean.Core.Crypto;

/// <summary>
/// A node's long-lived identity: a P-256 key pair wrapped in a self-signed certificate.
/// The node id is the SHA-256 of the public key, so TLS peers are authenticated by key pinning
/// (no CA needed) and discovery beacons are signed with the same key.
/// </summary>
public sealed class NodeIdentity : IDisposable
{
    private const string PfxPassword = "ocean-node";

    public X509Certificate2 Certificate { get; }
    public string NodeId { get; }
    public string PublicKeyBase64 { get; }
    private readonly ECDsa _privateKey;

    private NodeIdentity(X509Certificate2 cert)
    {
        Certificate = cert;
        _privateKey = cert.GetECDsaPrivateKey() ?? throw new InvalidOperationException("Certificate has no ECDSA private key");
        var spki = cert.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo();
        PublicKeyBase64 = Convert.ToBase64String(spki);
        NodeId = ComputeNodeId(spki);
    }

    public static NodeIdentity LoadOrCreate(string pfxPath)
    {
        if (File.Exists(pfxPath))
        {
            try
            {
                var cert = new X509Certificate2(pfxPath, PfxPassword,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
                if (cert.HasPrivateKey && cert.NotAfter > DateTime.UtcNow.AddDays(30))
                    return new NodeIdentity(cert);
            }
            catch { /* fall through and regenerate */ }
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=Ocean Node, O=Ocean", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new Oid("1.3.6.1.5.5.7.3.1"), // server auth
            new Oid("1.3.6.1.5.5.7.3.2"), // client auth
        }, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("ocean.local");
        req.CertificateExtensions.Add(san.Build());
        var now = DateTimeOffset.UtcNow;
        using var fresh = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
        var pfx = fresh.Export(X509ContentType.Pfx, PfxPassword);
        Directory.CreateDirectory(Path.GetDirectoryName(pfxPath)!);
        File.WriteAllBytes(pfxPath, pfx);
        var loaded = new X509Certificate2(pfx, PfxPassword,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        return new NodeIdentity(loaded);
    }

    public static string ComputeNodeId(byte[] spki) => Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();

    public static string? ComputeNodeId(X509Certificate2 cert)
    {
        try
        {
            using var ec = cert.GetECDsaPublicKey();
            if (ec is null) return null;
            return ComputeNodeId(ec.ExportSubjectPublicKeyInfo());
        }
        catch { return null; }
    }

    public byte[] Sign(byte[] data) => _privateKey.SignData(data, HashAlgorithmName.SHA256);

    public static bool Verify(byte[] spki, byte[] data, byte[] signature)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(spki, out _);
            return ec.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }

    public static string ShortId(string nodeId) => nodeId.Length >= 12 ? nodeId[..12] : nodeId;

    public void Dispose()
    {
        _privateKey.Dispose();
        Certificate.Dispose();
    }
}
