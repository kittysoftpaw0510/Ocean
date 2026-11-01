namespace Ocean.Core.Protocol;

/// <summary>
/// Multicast: one node (the host) sends a video once onto the LAN and drives playback; anyone with access to the video
/// can join and watches in step with the host. Carried in the host's catalog (<see cref="CatalogDto.Cast"/>) so every
/// node sees who is multicasting what.
/// </summary>
public sealed class CastInfoDto
{
    public string SessionId { get; set; } = "";
    /// <summary>The video (its original id; the stream may be its lighter copy).</summary>
    public string ItemId { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string ShareName { get; set; } = "";
    public bool Light { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public int Viewers { get; set; }
}

/// <summary>Where the host is right now.</summary>
public sealed class CastStateDto
{
    public string SessionId { get; set; } = "";
    public bool Active { get; set; }
    public double Position { get; set; }
    public bool Playing { get; set; }
    /// <summary>The host's clock (Unix ms) when Position was taken; viewers extrapolate from it.</summary>
    public long HostTimeMs { get; set; }
    public int Viewers { get; set; }
    public bool Ended { get; set; }
}

/// <summary>Answer to joining: what to listen for and the session key (only ever sent over the encrypted channel).</summary>
public sealed class CastJoinDto
{
    public string SessionId { get; set; } = "";
    /// <summary>The stream's item id (with "@light" when the host sends its lighter copy).</summary>
    public string ItemId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ShareId { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public long? DurationMs { get; set; }
    public string Group { get; set; } = "";
    public int Port { get; set; }
    /// <summary>Base64 AES key for the session's packets.</summary>
    public string Key { get; set; } = "";
    public CastStateDto State { get; set; } = new();
}

public sealed class CastBeatDto
{
    public long BytesMulticast { get; set; }
    public int Lost { get; set; }
}

// ---- local API (service ↔ app) ----

public sealed class CastStartDto
{
    public string ItemId { get; set; } = "";
    public bool PreferLight { get; set; } = true;
}

public sealed class CastHostStateDto
{
    public double Position { get; set; }
    public bool Playing { get; set; }
}

/// <summary>My own multicast (host side), for the app.</summary>
public sealed class MyCastDto
{
    public bool Active { get; set; }
    public string? SessionId { get; set; }
    public string? ItemId { get; set; }
    public string ItemName { get; set; } = "";
    public string ShareName { get; set; } = "";
    public bool Light { get; set; }
    public int Viewers { get; set; }
    /// <summary>How many of them receive the multicast packets (the others fall back to a direct stream).</summary>
    public int ViewersMulticast { get; set; }
    public double Position { get; set; }
    public bool Playing { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public string? Error { get; set; }
}

/// <summary>The multicast I'm watching (viewer side), for the app.</summary>
public sealed class CastViewDto
{
    public bool Active { get; set; }
    public string PeerId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string HostName { get; set; } = "";
    public double Position { get; set; }
    public bool Playing { get; set; }
    public long HostTimeMs { get; set; }
    /// <summary>This PC's clock (Unix ms) when the host's position was received: the player extrapolates from it.</summary>
    public long ReceivedAtMs { get; set; }
    public int Viewers { get; set; }
    public bool Ended { get; set; }
    public long BytesMulticast { get; set; }
    public int Lost { get; set; }
    public int Recovered { get; set; }
    /// <summary>Multicast packets arrive here (otherwise the video comes directly from the host).</summary>
    public bool MulticastWorking { get; set; }
}

/// <summary>Whether one more multicast may start on this network (at most 30 % of the PCs at once).</summary>
public sealed class CastLimitDto
{
    public bool Allowed { get; set; }
    public int Active { get; set; }
    public int Nodes { get; set; }
    public int MaxAllowed { get; set; }
    public string? Reason { get; set; }
}
