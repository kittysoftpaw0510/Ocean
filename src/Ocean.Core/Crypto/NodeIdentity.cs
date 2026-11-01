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

    /// <summary>
    /// Loads this PC's identity, or creates one on first start. The identity is who this PC is to every other PC
    /// (a new one makes it a new node in everyone's list), so it is never replaced lightly: a file that can't be read
    /// right now (antivirus or a backup tool holding it during an update, a key-store hiccup) is retried, the backup
    /// copy next to it is tried, and only a file that stays unreadable is set aside (kept, never overwritten) before a
    /// new identity is made. <paramref name="warn"/> receives what went wrong, for the log.
    /// </summary>
    public static NodeIdentity LoadOrCreate(string pfxPath, Action<string>? warn = null)
    {
        var backup = pfxPath + ".bak";
        foreach (var path in new[] { pfxPath, backup })
        {
            if (!File.Exists(path)) continue;
            Exception? last = null;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    var cert = new X509Certificate2(path, PfxPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
                    if (!cert.HasPrivateKey || cert.NotAfter <= DateTime.UtcNow.AddDays(30)) { last = new InvalidDataException("no private key, or expired"); break; }
                    var identity = new NodeIdentity(cert);
                    if (path == backup)
                    {
                        warn?.Invoke($"identity: {Path.GetFileName(pfxPath)} could not be read; restored it from its backup (node {ShortId(identity.NodeId)})");
                        SetAside(pfxPath, warn);
                        TryCopy(backup, pfxPath);
                    }
                    else if (!File.Exists(backup)) TryCopy(pfxPath, backup);
                    return identity;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
                {
                    last = ex;
                    Thread.Sleep(400 * attempt); // locked or busy for a moment: try again
                }
            }
            warn?.Invoke($"identity: {Path.GetFileName(path)} could not be read: {last?.Message}");
        }

        if (File.Exists(pfxPath) || File.Exists(backup))
        {
            SetAside(pfxPath, warn);
            SetAside(backup, warn);
            warn?.Invoke("identity: a new one is being created; other PCs will list this PC as a new node (the old entry disappears from their lists by itself)");
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
        try { File.WriteAllBytes(backup, pfx); } catch { /* the backup is a safety net only */ }
        var loaded = new X509Certificate2(pfx, PfxPassword,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        return new NodeIdentity(loaded);
    }

    /// <summary>Keeps an unreadable identity file under another name instead of overwriting it.</summary>
    private static void SetAside(string path, Action<string>? warn)
    {
        if (!File.Exists(path)) return;
        var aside = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
        try { File.Move(path, aside); warn?.Invoke($"identity: kept the unreadable file as {Path.GetFileName(aside)}"); }
        catch (Exception ex) { warn?.Invoke($"identity: could not set {Path.GetFileName(path)} aside: {ex.Message}"); }
    }

    private static void TryCopy(string from, string to)
    {
        try { File.Copy(from, to, overwrite: true); } catch { /* the backup is a safety net only */ }
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
