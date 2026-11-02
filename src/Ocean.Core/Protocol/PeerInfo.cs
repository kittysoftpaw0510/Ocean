namespace Ocean.Core.Protocol;

/// <summary>Everything this PC knows about one node, for the "Node info" window (GET /api/peers/{id}/info).</summary>
public sealed class PeerInfoDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string User { get; set; } = "";
    /// <summary>The PC's Windows name.</summary>
    public string Host { get; set; } = "";

    public bool Online { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public DateTimeOffset? OfflineSince { get; set; }

    /// <summary>Its Ocean version: from its own requests to this PC, or from its video list. Null = not known yet.</summary>
    public string? AppVersion { get; set; }
    public DateTimeOffset? AppVersionSeenAt { get; set; }
    /// <summary>This PC's version, to compare with.</summary>
    public string MyVersion { get; set; } = "";
    /// <summary>"Windows 11 Pro 24H2 (build 26100)"; only Ocean versions after 1.3.3 report it.</summary>
    public string? Os { get; set; }

    /// <summary>direct | bridge (through another PC) | reverse (it connects to us: its firewall blocks incoming)</summary>
    public string Connection { get; set; } = "direct";
    /// <summary>The bridge in use, when reached through another PC.</summary>
    public string? Via { get; set; }
    /// <summary>Every PC that can bridge to it right now.</summary>
    public List<string> Bridges { get; set; } = new();
    /// <summary>The address Ocean uses (the last one that worked); null when only reached through a bridge.</summary>
    public string? Address { get; set; }
    public int Port { get; set; }
    /// <summary>Every address it was heard on lately.</summary>
    public List<PeerAddressDto> Addresses { get; set; } = new();

    public bool CatalogLoaded { get; set; }
    /// <summary>When its video list (as this PC has it) was made.</summary>
    public DateTimeOffset? CatalogAt { get; set; }
    public int ShareCount { get; set; }
    public int ItemCount { get; set; }
    public long TotalBytes { get; set; }

    /// <summary>The video it is multicasting right now.</summary>
    public string? CastItem { get; set; }
}

public sealed class PeerAddressDto
{
    public string Address { get; set; } = "";
    public DateTimeOffset LastHeard { get; set; }
    /// <summary>The one Ocean uses.</summary>
    public bool Preferred { get; set; }
    /// <summary>On this PC's local network (Ocean only ever talks to those).</summary>
    public bool Local { get; set; }
}
