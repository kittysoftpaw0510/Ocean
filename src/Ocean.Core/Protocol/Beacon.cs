using System.Text;
using Ocean.Core.Crypto;

namespace Ocean.Core.Protocol;

/// <summary>UDP discovery datagram (JSON). Signed with the node's key so identities cannot be spoofed.</summary>
public sealed class Beacon
{
    public int V { get; set; } = OceanDefaults.ProtocolVersion;
    /// <summary>"hello", "reply" (a direct answer, never answered itself) or "bye".</summary>
    public string T { get; set; } = "hello";
    public string Id { get; set; } = "";
    /// <summary>Base64 SubjectPublicKeyInfo of the node key. SHA-256 of it is the node id.</summary>
    public string Pk { get; set; } = "";
    public string Name { get; set; } = "";
    public string User { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    /// <summary>Realm tag derived from the shared secret (empty when no secret is set).</summary>
    public string Realm { get; set; } = "";
    public long Cat { get; set; }
    public int Shares { get; set; }
    public long Ts { get; set; }
    /// <summary>
    /// Reverse-connection requests: ids of nodes this node cannot connect to directly (usually their firewall blocks
    /// incoming connections). A listed node answers by connecting out to us and serving our requests over that link.
    /// </summary>
    public List<string>? Want { get; set; }
    public string Sig { get; set; } = "";

    // ---- quiet presence (Ocean after 1.3.8). Older nodes ignore these fields and check Sig over the fields above. ----

    /// <summary>Seconds until this node's next heartbeat: 4 while older nodes are around, 300 otherwise. 0: an older Ocean.</summary>
    public int Hb { get; set; }
    /// <summary>Fingerprint of the nodes it hears directly (its bridge list): others ask for the list only when it changes.</summary>
    public string? Nrev { get; set; }
    /// <summary>A question (startup, refresh, "are you there?"): every node that hears it answers.</summary>
    public bool? Q { get; set; }
    /// <summary>Signature over everything, the fields above included (<see cref="Sig"/> covers what older nodes check).</summary>
    public string? Xs { get; set; }

    public bool HasExtension => Hb > 0 || Nrev is not null || Q == true;

    public byte[] SignedPayload()
    {
        var core = string.Join('\n', V, T, Id, Pk, Name, User, Host, Port, Realm, Cat, Shares, Ts);
        // Only appended when present, so plain beacons stay byte-for-byte compatible with older nodes.
        if (Want is { Count: > 0 }) core += "\nwant:" + string.Join(',', Want);
        return Encoding.UTF8.GetBytes(core);
    }

    private byte[] ExtendedPayload()
        => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(SignedPayload()) + $"\next:{Hb},{Nrev},{(Q == true ? 1 : 0)}");

    public void Sign(NodeIdentity identity)
    {
        Id = identity.NodeId;
        Pk = identity.PublicKeyBase64;
        Sig = Convert.ToBase64String(identity.Sign(SignedPayload()));
        Xs = HasExtension ? Convert.ToBase64String(identity.Sign(ExtendedPayload())) : null;
    }

    /// <summary>
    /// Checks the signatures. The newer fields count only when their own signature holds; otherwise they are dropped
    /// and the beacon is taken as an older node's (a forged heartbeat interval can't keep a node listed).
    /// </summary>
    public bool Verify()
    {
        try
        {
            var spki = Convert.FromBase64String(Pk);
            if (!string.Equals(NodeIdentity.ComputeNodeId(spki), Id, StringComparison.Ordinal)) return false;
            if (!NodeIdentity.Verify(spki, SignedPayload(), Convert.FromBase64String(Sig))) return false;
            if (HasExtension && (Xs is null || !NodeIdentity.Verify(spki, ExtendedPayload(), Convert.FromBase64String(Xs))))
            {
                Hb = 0; Nrev = null; Q = null;
            }
            return true;
        }
        catch { return false; }
    }
}
