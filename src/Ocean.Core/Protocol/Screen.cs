namespace Ocean.Core.Protocol;

// Screen sharing (see Ocean.Service.Screen). A node shares one thing at a time (a monitor, a window or a part of a
// monitor): with everyone (in its catalog, like a multicast) or only with the PCs it invited. The app captures and
// encodes (the service cannot see the desktop); the service passes the H.264 stream on.

/// <summary>A screen a node shares with everyone, in its catalog (<see cref="CatalogDto.Screen"/>). Older nodes ignore it.</summary>
public sealed class ScreenInfoDto
{
    public string SessionId { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public int Viewers { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>App → service: start sharing what the user picked.</summary>
public sealed class ScreenStartDto
{
    /// <summary>What is shared, for people ("Monitor 1", a window's title, "Part of monitor 2").</summary>
    public string Title { get; set; } = "";
    /// <summary>The picture's size as it is sent.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; } = 15;
    /// <summary>Everyone on the network may watch (else only <see cref="Invite"/>).</summary>
    public bool Everyone { get; set; }
    /// <summary>Node ids asked to watch.</summary>
    public List<string> Invite { get; set; } = new();
    /// <summary>The request ("show me your screen") this answers, if any: its sender is let in and told.</summary>
    public string? AskId { get; set; }
}

public sealed class ScreenInviteDto
{
    public List<string> PeerIds { get; set; } = new();
    /// <summary>A "show me your screen" request allowed into the running session.</summary>
    public string? AskId { get; set; }
}

public sealed class ScreenPersonDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>asked (no answer yet), accepted, watching, declined, unreachable, left.</summary>
    public string State { get; set; } = "";
}

/// <summary>My screen sharing (host side), for my app.</summary>
public sealed class MyScreenDto
{
    public bool Active { get; set; }
    public string? SessionId { get; set; }
    public string Title { get; set; } = "";
    public bool Everyone { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }
    public int Viewers { get; set; }
    public List<ScreenPersonDto> People { get; set; } = new();
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>Somebody watches (or is about to): the app captures and encodes only then.</summary>
    public bool Wanted { get; set; }
    /// <summary>Set on the event when the session has just ended: why.</summary>
    public string? Ended { get; set; }
    public string? Error { get; set; }
}

/// <summary>Node → node: "show me your screen" (kind show) or "look at my screen" (kind watch).</summary>
public sealed class ScreenAskDto
{
    public const string Show = "show", Watch = "watch";
    public string AskId { get; set; } = "";
    public string Kind { get; set; } = Show;
    /// <summary>Kind watch: the session to join, and what it shows.</summary>
    public string? SessionId { get; set; }
    public string? Title { get; set; }
}

public sealed class ScreenAnswerDto
{
    public string AskId { get; set; } = "";
    public bool Allow { get; set; }
    /// <summary>Kind show, allowed: the session the asker may join now.</summary>
    public string? SessionId { get; set; }
    /// <summary>declined, timeout, away (nobody at that PC's Ocean), busy, muted.</summary>
    public string? Reason { get; set; }
}

/// <summary>A request waiting for this PC's user (the app shows it).</summary>
public sealed class ScreenPromptDto
{
    public string AskId { get; set; } = "";
    public string Kind { get; set; } = ScreenAskDto.Show;
    public string PeerId { get; set; } = "";
    public string PeerName { get; set; } = "";
    public string? SessionId { get; set; }
    public string? Title { get; set; }
    public DateTimeOffset At { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>Set on the event when the request is over without the user (taken back, timed out).</summary>
    public string? Gone { get; set; }
}

/// <summary>Local: the user's answer to a prompt.</summary>
public sealed class ScreenPromptAnswerDto
{
    public bool Allow { get; set; }
    /// <summary>Don't show this PC's requests any more (until taken back in the settings file).</summary>
    public bool Mute { get; set; }
}

/// <summary>My own request to another PC (asker side), for my app.</summary>
public sealed class ScreenRequestDto
{
    public string AskId { get; set; } = "";
    public string PeerId { get; set; } = "";
    public string PeerName { get; set; } = "";
    /// <summary>waiting, allowed, declined, timeout, away, busy, muted, old (its Ocean has no screen sharing), unreachable.</summary>
    public string State { get; set; } = "waiting";
    public string? SessionId { get; set; }
    public string? Message { get; set; }
}

public sealed class ScreenJoinRequestDto
{
    /// <summary>The UDP port this PC listens on for packets sent straight to it (0: none).</summary>
    public int UdpPort { get; set; }
}

public sealed class ScreenJoinDto
{
    public string SessionId { get; set; } = "";
    public string Title { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }
    public string Key { get; set; } = "";
    /// <summary>The multicast group the packets go to (a share with everyone), null when they are sent straight to each viewer.</summary>
    public string? Group { get; set; }
    public int Port { get; set; }
    /// <summary>The host sends packets straight to the port this PC named.</summary>
    public bool Unicast { get; set; }
}

public sealed class ScreenBeatDto
{
    public long BytesUdp { get; set; }
    public long BytesDirect { get; set; }
    public int Lost { get; set; }
    public int Recovered { get; set; }
    /// <summary>Right now the picture comes over the direct connection (no usable UDP).</summary>
    public bool Direct { get; set; }
}

public sealed class ScreenStateDto
{
    public string SessionId { get; set; } = "";
    public bool Ended { get; set; }
    public int Viewers { get; set; }
}

/// <summary>The screen I'm watching (viewer side), for my app.</summary>
public sealed class ScreenViewDto
{
    public bool Active { get; set; }
    public string PeerId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Title { get; set; } = "";
    public string HostName { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }
    public int Viewers { get; set; }
    public bool Ended { get; set; }
    /// <summary>udp (packets arrive), direct (over the connection to the host), connecting.</summary>
    public string Mode { get; set; } = "connecting";
    public long BytesUdp { get; set; }
    public long BytesDirect { get; set; }
    public int Lost { get; set; }
    public int Recovered { get; set; }
}
