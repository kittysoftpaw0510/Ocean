using System.Security.Cryptography;
using System.Text;

namespace Ocean.Core.Crypto;

/// <summary>
/// The "shared secret" feature. Nodes only see and serve nodes that know the same secret.
/// The secret never travels on the wire: beacons carry a short tag and every request carries a proof
/// bound to both the client's and the server's node ids (so it cannot be replayed against another node).
/// </summary>
public static class Realm
{
    public static string Normalize(string? secret) => (secret ?? "").Trim();

    public static string Tag(string? secret)
    {
        var s = Normalize(secret);
        if (s.Length == 0) return "";
        return Hex(Hmac(s, "ocean:realm:v1"))[..16];
    }

    public static string Proof(string? secret, string clientId, string serverId)
    {
        var s = Normalize(secret);
        if (s.Length == 0) return "";
        return Hex(Hmac(s, $"ocean:proof:v1:{clientId}:{serverId}"));
    }

    public static bool ProofMatches(string? secret, string clientId, string serverId, string? provided)
    {
        var expected = Proof(secret, clientId, serverId);
        var given = provided ?? "";
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given));
    }

    private static byte[] Hmac(string key, string message)
        => HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(message));

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
}

/// <summary>PBKDF2 password hashing for share / item passwords.</summary>
public static class Passwords
{
    private const int Iterations = 120_000;

    public static (string hash, string salt) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, string? hash, string? salt)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt)) return false;
        try
        {
            var computed = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt), Iterations, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(computed, Convert.FromBase64String(hash));
        }
        catch { return false; }
    }

    public static string RandomToken(int bytes = 24)
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string ShortId(int bytes = 8) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
}
