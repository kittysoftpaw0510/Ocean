using System.Text;
using Ocean.Core.Crypto;

namespace Ocean.Core.Protocol;

/// <summary>UDP discovery datagram (JSON). Signed with the node's key so identities cannot be spoofed.</summary>
public sealed class Beacon
{
    public int V { get; set; } = OceanDefaults.ProtocolVersion;
    /// <summary>"hello" or "bye".</summary>
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

    public byte[] SignedPayload()
    {
        var core = string.Join('\n', V, T, Id, Pk, Name, User, Host, Port, Realm, Cat, Shares, Ts);
        // Only appended when present, so plain beacons stay byte-for-byte compatible with older nodes.
        if (Want is { Count: > 0 }) core += "\nwant:" + string.Join(',', Want);
        return Encoding.UTF8.GetBytes(core);
    }

    public void Sign(NodeIdentity identity)
    {
        Id = identity.NodeId;
        Pk = identity.PublicKeyBase64;
        Sig = Convert.ToBase64String(identity.Sign(SignedPayload()));
    }

    public bool Verify()
    {
        try
        {
            var spki = Convert.FromBase64String(Pk);
            if (!string.Equals(NodeIdentity.ComputeNodeId(spki), Id, StringComparison.Ordinal)) return false;
            return NodeIdentity.Verify(spki, SignedPayload(), Convert.FromBase64String(Sig));
        }
        catch { return false; }
    }
}
