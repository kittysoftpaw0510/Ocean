using System.Text;
using Ocean.Core.Crypto;

namespace Ocean.Core.Protocol;

/// <summary>
/// Requests carried through other nodes ("A reaches C via B" when A and C have no network path to each other).
/// <para>
/// A sends <c>/p/via/{C's id}/p/stream/…</c> to B, and B sends <c>/p/stream/…</c> on to C. There is never more than
/// one PC in between: B only offers PCs it reaches itself, and never passes on a request that was already passed on. The origin signs every such request with its node key: a relay can carry a request but never make
/// one up in someone else's name, so C knows for certain the request is A's and applies A's grants.
/// </para>
/// </summary>
public static class Via
{
    public const string Prefix = "/p/via/";
    public const string OriginHeader = "X-Ocean-Origin";
    public const string KeyHeader = "X-Ocean-Origin-Key";
    public const string TimeHeader = "X-Ocean-Origin-Ts";
    public const string SignatureHeader = "X-Ocean-Origin-Sig";
    /// <summary>How many relays a request has passed so far (a relay refuses one that already passed one).</summary>
    public const string HopsHeader = "X-Ocean-Hops";
    /// <summary>At most this many PCs in between (so no routing loops either).</summary>
    public const int MaxHops = 1;
    /// <summary>
    /// Set by a bridge on an answer that is its own failure, not the target's: "off" (bridging switched off there),
    /// "busy" (it passes on enough downloads already), "unreachable", "loop" or "failed". The origin then tries
    /// another bridge right away.
    /// </summary>
    public const string ErrorHeader = "X-Ocean-Via-Error";
    /// <summary>/p/neighbors: the caller's last known revision of the list; unchanged → 304 with only the load.</summary>
    public const string RevHeader = "X-Ocean-Neighbors-Rev";
    /// <summary>/p/neighbors: how many transfers the answering node is passing on right now.</summary>
    public const string LoadHeader = "X-Ocean-Load";
    /// <summary>Clock difference tolerated between the origin and the destination.</summary>
    private static readonly TimeSpan MaxSkew = TimeSpan.FromDays(1);

    /// <summary>Paths that may be relayed: the read-only and access parts of the peer API.</summary>
    public static bool IsRelayable(string path)
        => path.StartsWith("/p/catalog", StringComparison.Ordinal) || path.StartsWith("/p/stream/", StringComparison.Ordinal)
           || path.StartsWith("/p/thumb/", StringComparison.Ordinal) || path.StartsWith("/p/access", StringComparison.Ordinal)
           || path.StartsWith("/p/ping", StringComparison.Ordinal);

    /// <summary>Splits "/p/via/{target}/p/…" into the target node id and the path meant for it.</summary>
    public static bool TryParse(string rawTarget, out string targetId, out string inner)
    {
        targetId = inner = "";
        if (!rawTarget.StartsWith(Prefix, StringComparison.Ordinal) || rawTarget.Length < Prefix.Length + 64 + 3) return false;
        targetId = rawTarget.Substring(Prefix.Length, 64).ToLowerInvariant();
        inner = rawTarget[(Prefix.Length + 64)..];
        return targetId.All(Uri.IsHexDigit) && inner.StartsWith("/p/", StringComparison.Ordinal);
    }

    /// <summary>The origin's headers for a request to <paramref name="targetId"/>.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Sign(NodeIdentity me, string targetId, string method, string pathAndQuery)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = me.Sign(Payload(me.NodeId, targetId, method, pathAndQuery, ts));
        yield return new(OriginHeader, me.NodeId);
        yield return new(KeyHeader, me.PublicKeyBase64);
        yield return new(TimeHeader, ts.ToString());
        yield return new(SignatureHeader, Convert.ToBase64String(sig));
    }

    /// <summary>Checks the origin's signature for a request to <paramref name="targetId"/> of <paramref name="pathAndQuery"/>.</summary>
    public static bool Verify(Func<string, string> header, string targetId, string method, string pathAndQuery, out string originId, out string error)
    {
        originId = header(OriginHeader).Trim().ToLowerInvariant();
        error = "";
        try
        {
            var spki = Convert.FromBase64String(header(KeyHeader));
            if (originId.Length != 64 || NodeIdentity.ComputeNodeId(spki) != originId) { error = "origin key does not match its id"; return false; }
            if (!long.TryParse(header(TimeHeader), out var ts)) { error = "no time"; return false; }
            var skew = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(ts);
            if (skew.Duration() > MaxSkew) { error = $"the clocks of the two PCs differ by {skew.Duration().TotalHours:0} hours"; return false; }
            if (NodeIdentity.Verify(spki, Payload(originId, targetId, method, pathAndQuery, ts), Convert.FromBase64String(header(SignatureHeader)))) return true;
            error = "bad signature";
            return false;
        }
        catch (FormatException) { error = "malformed origin headers"; return false; }
    }

    private static byte[] Payload(string originId, string targetId, string method, string pathAndQuery, long ts)
        => Encoding.UTF8.GetBytes($"ocean:via:v1\n{originId}\n{targetId}\n{method.ToUpperInvariant()}\n{Uri.UnescapeDataString(pathAndQuery)}\n{ts}");
}

/// <summary>A node the answering node hears directly (it is never offered a node it reaches through another).</summary>
public sealed class NeighborDto
{
    public string Id { get; set; } = "";
    /// <summary>Public key (its SHA-256 is the id).</summary>
    public string Pk { get; set; } = "";
    public string Name { get; set; } = "";
    public string User { get; set; } = "";
    public string Host { get; set; } = "";
    public long Cat { get; set; }
    public int Shares { get; set; }
    /// <summary>PCs between the answering node and this one; always 0 (kept for later versions).</summary>
    public int Hops { get; set; }
}

public sealed class NeighborsDto
{
    public List<NeighborDto> Nodes { get; set; } = new();
    /// <summary>Changes when the list changes (sent back as <see cref="Via.RevHeader"/> to get a 304 next time).</summary>
    public string Rev { get; set; } = "";
    /// <summary>Transfers this node is passing on for others right now (the origin picks the least busy bridge).</summary>
    public int Load { get; set; }
    /// <summary>False when this node refuses to act as a bridge (Settings); the list is then empty.</summary>
    public bool Bridging { get; set; } = true;
}
