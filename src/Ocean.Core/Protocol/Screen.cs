namespace Ocean.Core.Protocol;

// Screen sharing (see Ocean.Service.Screen). A node shares one thing at a time (a monitor, a window or a part of a
// monitor): with everyone (in its catalog, like a multicast) or only with the PCs it invited. The app captures and
// encodes (the service cannot see the desktop); the service passes the H.264 stream on. The computer's sound can
// go with it (Opus, see ScreenSoundFrames); the sharer switches it on and off while sharing.

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
    /// <summary>My offer ("may I show you my screen?") that was accepted: that PC is let in and told where to watch.</summary>
    public string? OfferAskId { get; set; }
    /// <summary>What the computer plays goes with the picture (switched on and off while sharing: <see cref="ScreenSoundDto"/>).</summary>
    public bool Sound { get; set; }
}

/// <summary>App → service: the computer's sound on or off while sharing.</summary>
public sealed class ScreenSoundDto
{
    public bool On { get; set; }
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
    /// <summary>The computer's sound is shared too.</summary>
    public bool Sound { get; set; }
    /// <summary>Set on the event when the session has just ended: why.</summary>
    public string? Ended { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Node → node: "show me your screen" (kind show), "may I show you my screen?" (kind offer: nothing is shared yet;
/// the sender picks what to show once the answer is yes) or "look at my screen" (kind watch: an invitation into a
/// session that runs already).
/// </summary>
public sealed class ScreenAskDto
{
    public const string Show = "show", Watch = "watch", Offer = "offer";
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
    /// <summary>show (I asked to see theirs) or offer (I offered to show mine).</summary>
    public string Kind { get; set; } = ScreenAskDto.Show;
    public string PeerId { get; set; } = "";
    public string PeerName { get; set; } = "";
    /// <summary>waiting, allowed, declined, timeout, away, busy, muted, old (its Ocean has no screen sharing), unreachable.</summary>
    public string State { get; set; } = "waiting";
    public string? SessionId { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Node → node, after an accepted offer: the sender now shares, and this is the session to watch (null: the sender
/// didn't share after all; <see cref="Reason"/> says why: cancelled, timeout).
/// </summary>
public sealed class ScreenReadyDto
{
    public string AskId { get; set; } = "";
    public string? SessionId { get; set; }
    public string? Reason { get; set; }
    /// <summary>Filled in for the app: who.</summary>
    public string PeerId { get; set; } = "";
    public string PeerName { get; set; } = "";
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
    /// <summary>The host shares its sound right now (older hosts: never).</summary>
    public bool Sound { get; set; }
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
    public bool Sound { get; set; }
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
    /// <summary>The host shares its sound right now: the app plays <c>/api/screen/view/sound</c>.</summary>
    public bool Sound { get; set; }
}

/// <summary>
/// The sound of a shared screen as it goes between app and service (the local API, both ways) and between services
/// (the direct fallback): frames one after another, each a header of 16 bytes (Opus packet length u16, frame number
/// u32, host time µs i64, age u16, little-endian) and the Opus packet. A frame is 20 ms of 48 kHz stereo; its host
/// time is when its first sample was played on the sharing PC (that PC's performance counter), so the viewer can
/// place it; its age (10 µs units) is how much later the sharing PC sent it, so the viewer can tell the time on the
/// way (which it learns) from the time it took to make the frame (which only the sharing PC knows).
/// </summary>
public static class ScreenSoundFrames
{
    public const int Rate = 48000, Channels = 2, Samples = 960, Ms = 20;
    public const int HeaderSize = 16;
    /// <summary>The largest Opus packet passed on (a 20 ms frame is about 240 bytes at 96 kbps).</summary>
    public const int MaxData = 1275;

    public readonly record struct Frame(uint Seq, long HostUs, ushort Age, byte[] Data)
    {
        /// <summary>When the sharing PC sent it (its clock, µs).</summary>
        public long SentUs => HostUs + Age * 10L;
    }

    public static byte[] Pack(in Frame f)
    {
        var b = new byte[HeaderSize + f.Data.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)f.Data.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), f.Seq);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(6), f.HostUs);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), f.Age);
        f.Data.CopyTo(b.AsSpan(HeaderSize));
        return b;
    }

    /// <summary>The next frame; null at the end of the stream. Throws on a stream that is not frames.</summary>
    public static async Task<Frame?> ReadAsync(Stream s, byte[] header, CancellationToken ct)
    {
        if (!await Fill(s, header.AsMemory(0, HeaderSize), ct)) return null;
        var len = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header);
        if (len > MaxData) throw new InvalidDataException("not a sound frame");
        var seq = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2));
        var us = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(6));
        var age = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
        var data = new byte[len];
        if (len > 0 && !await Fill(s, data, ct)) return null;
        return new Frame(seq, us, age, data);
    }

    private static async Task<bool> Fill(Stream s, Memory<byte> into, CancellationToken ct)
    {
        var got = 0;
        while (got < into.Length)
        {
            var n = await s.ReadAsync(into[got..], ct);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }
}
