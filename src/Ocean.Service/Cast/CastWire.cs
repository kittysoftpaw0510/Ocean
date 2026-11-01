using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Ocean.Service.Cast;

/// <summary>
/// The multicast packets. Every packet: "OCST", the session id (16), a type (1) and a sequence number (4), then a
/// payload. Data and control payloads are sealed with AES-GCM under the session key (nonce = type + sequence), so
/// only PCs that joined can read them and nobody can inject packets. Every <see cref="Group"/> data packets a parity
/// packet follows (the XOR of their sealed payloads), which recovers one lost packet per group; what that can't
/// recover, the viewer fetches directly from the host.
/// </summary>
public static class CastWire
{
    public static readonly IPAddress GroupAddress = IPAddress.Parse("239.255.77.1");
    public const int Port = 38473;
    public const int DataChunk = 1200;
    public const int Group = 8;
    public const byte TypeData = 1, TypeParity = 2, TypeControl = 3;
    private const int HeaderSize = 4 + 16 + 1 + 4;
    private static ReadOnlySpan<byte> Magic => "OCST"u8;

    public static byte[] Build(Guid session, byte type, uint seq, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[HeaderSize + payload.Length];
        Magic.CopyTo(packet);
        session.TryWriteBytes(packet.AsSpan(4, 16));
        packet[20] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(21, 4), seq);
        payload.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    public static bool TryParse(ReadOnlySpan<byte> packet, out Guid session, out byte type, out uint seq, out ReadOnlySpan<byte> payload)
    {
        session = default; type = 0; seq = 0; payload = default;
        if (packet.Length < HeaderSize || !packet[..4].SequenceEqual(Magic)) return false;
        session = new Guid(packet.Slice(4, 16));
        type = packet[20];
        seq = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(21, 4));
        payload = packet[HeaderSize..];
        return true;
    }

    /// <summary>Ciphertext followed by the 16-byte tag.</summary>
    public static byte[] Seal(AesGcm aes, byte type, uint seq, ReadOnlySpan<byte> plain)
    {
        var output = new byte[plain.Length + 16];
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, type, seq);
        aes.Encrypt(nonce, plain, output.AsSpan(0, plain.Length), output.AsSpan(plain.Length, 16));
        return output;
    }

    public static bool Open(AesGcm aes, byte type, uint seq, ReadOnlySpan<byte> sealedBytes, Span<byte> plain, out int length)
    {
        length = sealedBytes.Length - 16;
        if (length < 0 || length > plain.Length) return false;
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, type, seq);
        try { aes.Decrypt(nonce, sealedBytes[..length], sealedBytes[length..], plain[..length]); return true; }
        catch (CryptographicException) { return false; }
    }

    private static void Nonce(Span<byte> nonce, byte type, uint seq)
    {
        nonce.Clear();
        nonce[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(nonce[1..], seq);
    }

    /// <summary>Data payload (plain): offset (8) + length (2) + bytes.</summary>
    public static byte[] DataPlain(long offset, ReadOnlySpan<byte> bytes)
    {
        var plain = new byte[10 + bytes.Length];
        BinaryPrimitives.WriteInt64LittleEndian(plain, offset);
        BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(8), (ushort)bytes.Length);
        bytes.CopyTo(plain.AsSpan(10));
        return plain;
    }

    public static bool ReadData(ReadOnlySpan<byte> plain, out long offset, out ReadOnlySpan<byte> bytes)
    {
        offset = 0; bytes = default;
        if (plain.Length < 10) return false;
        offset = BinaryPrimitives.ReadInt64LittleEndian(plain);
        var len = BinaryPrimitives.ReadUInt16LittleEndian(plain[8..]);
        if (10 + len > plain.Length) return false;
        bytes = plain.Slice(10, len);
        return true;
    }

    /// <summary>Control payload (plain): position (8) + playing (1) + host time ms (8) + viewers (4) + ended (1).</summary>
    public static byte[] ControlPlain(double position, bool playing, long hostTimeMs, int viewers, bool ended)
    {
        var plain = new byte[22];
        BinaryPrimitives.WriteDoubleLittleEndian(plain, position);
        plain[8] = playing ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(plain.AsSpan(9), hostTimeMs);
        BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(17), viewers);
        plain[21] = ended ? (byte)1 : (byte)0;
        return plain;
    }

    public static bool ReadControl(ReadOnlySpan<byte> plain, out double position, out bool playing, out long hostTimeMs, out int viewers, out bool ended)
    {
        position = 0; playing = false; hostTimeMs = 0; viewers = 0; ended = false;
        if (plain.Length < 22) return false;
        position = BinaryPrimitives.ReadDoubleLittleEndian(plain);
        playing = plain[8] == 1;
        hostTimeMs = BinaryPrimitives.ReadInt64LittleEndian(plain[9..]);
        viewers = BinaryPrimitives.ReadInt32LittleEndian(plain[17..]);
        ended = plain[21] == 1;
        return true;
    }

    /// <summary>
    /// Parity payload: count (1), then per data packet its sequence (4) and sealed length (2), then the XOR of the
    /// sealed payloads. The sequences are listed because control packets take numbers in between.
    /// </summary>
    public static byte[] Parity(IReadOnlyList<(uint Seq, byte[] Sealed)> group)
    {
        var max = group.Max(p => p.Sealed.Length);
        var head = 1 + 6 * group.Count;
        var payload = new byte[head + max];
        payload[0] = (byte)group.Count;
        for (var i = 0; i < group.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1 + 6 * i), group[i].Seq);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(5 + 6 * i), (ushort)group[i].Sealed.Length);
            var x = payload.AsSpan(head);
            for (var j = 0; j < group[i].Sealed.Length; j++) x[j] ^= group[i].Sealed[j];
        }
        return payload;
    }

    private static bool ParityEntry(ReadOnlySpan<byte> parity, int i, out uint seq, out int len)
    {
        seq = BinaryPrimitives.ReadUInt32LittleEndian(parity[(1 + 6 * i)..]);
        len = BinaryPrimitives.ReadUInt16LittleEndian(parity[(5 + 6 * i)..]);
        return true;
    }

    /// <summary>How many of a parity group's data packets never arrived.</summary>
    public static int Missing(ReadOnlySpan<byte> parity, Func<uint, bool> have)
    {
        if (parity.Length < 1) return 0;
        int count = parity[0], missing = 0;
        if (parity.Length < 1 + 6 * count) return 0;
        for (var i = 0; i < count; i++) { ParityEntry(parity, i, out var seq, out _); if (!have(seq)) missing++; }
        return missing;
    }

    /// <summary>Rebuilds the one missing sealed payload of a group; null when none or more than one is missing.</summary>
    public static (uint Seq, byte[] Sealed)? Recover(ReadOnlySpan<byte> parity, Func<uint, byte[]?> have)
    {
        if (parity.Length < 1) return null;
        int count = parity[0];
        var head = 1 + 6 * count;
        if (parity.Length < head) return null;
        var x = parity[head..].ToArray();
        uint? missing = null; var missingLen = 0;
        for (var i = 0; i < count; i++)
        {
            ParityEntry(parity, i, out var seq, out var len);
            var p = have(seq);
            if (p is null) { if (missing is not null) return null; missing = seq; missingLen = len; continue; }
            for (var j = 0; j < p.Length && j < x.Length; j++) x[j] ^= p[j];
        }
        if (missing is null || missingLen > x.Length) return null;
        return (missing.Value, x[..missingLen]);
    }
}
