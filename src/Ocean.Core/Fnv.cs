namespace Ocean.Core;

/// <summary>
/// 64-bit FNV-1a. Stable across processes and restarts (unlike string.GetHashCode), cheap, and good enough to
/// detect changes; never used for security.
/// </summary>
public static class Fnv
{
    public const ulong Seed = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Add(ulong h, string? s)
    {
        if (s is not null)
            foreach (var c in s)
            {
                h = (h ^ (byte)c) * Prime;
                h = (h ^ (byte)(c >> 8)) * Prime;
            }
        return (h ^ 0xFF) * Prime; // separator, so ("ab","c") != ("a","bc")
    }

    public static ulong Add(ulong h, long v)
    {
        for (var i = 0; i < 8; i++) { h = (h ^ (byte)v) * Prime; v >>= 8; }
        return h;
    }

    public static ulong Hash(string? s) => Add(Seed, s);
}
