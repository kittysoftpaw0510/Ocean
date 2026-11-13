using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace Ocean.App.Services;

/// <summary>
/// The sound of a screen recording: what the speakers play (WASAPI loopback of the default output) and the
/// microphone (the default input), mixed into one 48 kHz stereo stream for the encoder.
///
/// Sync is by time stamps, not by arrival: Windows stamps every block of sound with the moment it was captured
/// (QPC), and every block is put on the recording's timeline at exactly that moment, where 0 is the first picture
/// (<see cref="Start"/>). The mix is handed to the encoder a quarter of a second behind real time, so blocks that
/// come late (Bluetooth, a busy PC) still land in their place. A source that is silent delivers nothing or
/// "silent" blocks; the timeline stays silent there. Small jitter in the stamps is smoothed; a real gap (the device
/// was busy) stays a gap; a device that disappears (headphones unplugged, another default device) is opened again.
/// </summary>
public sealed class RecordingAudio : IDisposable
{
    public const int Rate = 48000, Channels = 2;
    /// <summary>How far behind real time the mix is handed over (room for late blocks).</summary>
    private const int MarginMs = 250;
    /// <summary>The timeline, in frames: a ring of 4 s.</summary>
    private const int RingFrames = Rate * 4;
    /// <summary>
    /// The first picture is reported a little after it was captured (copied from the graphics card, logged, read
    /// by this app): about this much, measured. Taken off, so the sound's 0 is the picture's 0.
    /// </summary>
    private const double FirstPictureReportMs = 12;

    private readonly object _ring = new();
    private readonly float[] _mix = new float[RingFrames * Channels];
    private long _emitted;               // frames handed to the encoder
    private long _t0 = long.MinValue;    // the first picture, in 100 ns QPC units
    private readonly List<Source> _sources = new();
    private Thread? _pump;
    private volatile bool _stop;
    private long _stopAt = long.MaxValue; // the timeline frame the recording ends at
    private long _halfPictureHns;

    public volatile bool MicMuted;
    public bool HasSpeaker => _sources.Any(s => s.Loopback);
    public bool HasMic => _sources.Any(s => !s.Loopback);
    public string? MicName => _sources.FirstOrDefault(s => !s.Loopback)?.DeviceName;
    public List<string> Problems { get; } = new();
    public float Level { get; private set; }
    public float MicLevel { get; private set; }
    public long FramesWritten => Interlocked.Read(ref _emitted);
    public long SpeakerBytes => _sources.Where(s => s.Loopback).Sum(s => s.Frames);
    public long MicBytes => _sources.Where(s => !s.Loopback).Sum(s => s.Frames);
    public DateTime? StartedAt { get; private set; }
    /// <summary>Blocks that came later than the margin and were left out (for the log).</summary>
    public long LateFrames => _sources.Sum(s => s.LateFrames);
    public string ClockText => string.Join("; ", _sources.Select(s => $"{(s.Loopback ? "speaker" : "mic")} {s.Skew * 1000.0 / Rate:+0.0;-0.0} ms off, {s.Jumps} jumps"));

    public static long NowHns() => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));

    public RecordingAudio(bool speaker, bool mic)
    {
        if (speaker) _sources.Add(new Source(this, loopback: true));
        if (mic)
        {
            if (!MicrophoneExists()) Problems.Add("no microphone was found");
            else _sources.Add(new Source(this, loopback: false));
        }
        foreach (var s in _sources.ToList())
        {
            if (s.Open(out var why)) continue;
            Problems.Add(why);
            _sources.Remove(s);
        }
    }

    /// <summary>
    /// Starts capturing now (so the devices are running when the first picture comes) and writes the mix to
    /// <paramref name="output"/> from the first picture on, until <see cref="Stop"/>.
    /// </summary>
    public void Start(Stream output, Task<long> firstPictureHns, int fps)
    {
        foreach (var s in _sources) s.Start();
        // A change on the screen shows in the next captured picture: on average half a picture's time after it
        // happened. The sound is put that much later, so what is seen and heard meet in the middle.
        _halfPictureHns = 10_000_000L / Math.Max(1, fps) / 2;
        _pump = new Thread(() => Pump(output, firstPictureHns)) { IsBackground = true, Name = "ocean-rec-audio", Priority = ThreadPriority.AboveNormal };
        _pump.Start();
    }

    private void Pump(Stream output, Task<long> firstPicture)
    {
        try
        {
            if (!firstPicture.Wait(TimeSpan.FromSeconds(15)) || _stop) return;
            lock (_ring) _t0 = firstPicture.Result - (long)(FirstPictureReportMs * 10_000) - _halfPictureHns;
            StartedAt = DateTime.UtcNow;
            var bytes = new byte[Rate / 10 * Channels * 4];
            while (true)
            {
                Thread.Sleep(10);
                var stopping = _stop;
                long upTo;
                lock (_ring) upTo = Math.Min(FrameAt(NowHns()) - Rate * MarginMs / 1000, _stopAt);
                while (_emitted < upTo)
                {
                    var n = (int)Math.Min(upTo - _emitted, bytes.Length / 4 / Channels);
                    float peak = 0;
                    lock (_ring)
                    {
                        var at = (int)(_emitted % RingFrames) * Channels;
                        for (var i = 0; i < n * Channels; i++)
                        {
                            var idx = (at + i) % _mix.Length;
                            var v = _mix[idx];
                            _mix[idx] = 0;
                            // two loud sources would clip: a soft limit instead of a crackle
                            if (v > 0.95f) v = 0.95f + (v - 0.95f) / (1 + (v - 0.95f) * 20);
                            else if (v < -0.95f) v = -0.95f + (v + 0.95f) / (1 - (v + 0.95f) * 20);
                            v = Math.Clamp(v, -1f, 1f);
                            var abs = Math.Abs(v); if (abs > peak) peak = abs;
                            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4, 4), v);
                        }
                        Interlocked.Add(ref _emitted, n);
                    }
                    Level = peak;
                    output.Write(bytes, 0, n * Channels * 4);
                }
                output.Flush();
                if (stopping && (_emitted >= _stopAt || _stopAt == long.MaxValue)) break;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { if (!_stop) AppLog.Warn("Record", "the encoder stopped taking sound: " + ex.Message); }
        catch (Exception ex) { AppLog.Warn("Record", "sound mixing stopped", ex); }
    }

    /// <summary>The timeline frame of a moment (100 ns QPC units). Under the ring lock.</summary>
    private long FrameAt(long hns) => (long)Math.Round((hns - _t0) * (Rate / 10_000_000.0));

    /// <summary>Adds a block of 48 kHz stereo sound at a timeline frame. Late blocks (already handed over) lose their late part.</summary>
    private void Place(long frame, float[] samples, int frames, bool mic, Source src)
    {
        lock (_ring)
        {
            var skip = 0;
            if (frame < _emitted) { skip = (int)Math.Min(frames, _emitted - frame); src.LateFrames += skip; }
            var end = Math.Min(frame + frames, _emitted + RingFrames - Rate); // never over what is not handed over yet
            float peak = 0;
            for (var f = frame + skip; f < end; f++)
            {
                var at = (int)(f % RingFrames) * Channels;
                var i = (int)(f - frame) * Channels;
                var l = samples[i]; var r = samples[i + 1];
                if (mic) { var a = Math.Max(Math.Abs(l), Math.Abs(r)); if (a > peak) peak = a; if (MicMuted) continue; }
                _mix[at] += l; _mix[at + 1] += r;
            }
            if (mic) MicLevel = MicMuted ? 0 : peak;
        }
    }

    /// <summary>Stops at this moment: what was captured up to now is still handed over (a quarter of a second later).</summary>
    public void Stop()
    {
        if (_stop) return;
        lock (_ring) { if (_t0 != long.MinValue) _stopAt = Math.Max(_emitted, FrameAt(NowHns())); }
        _stop = true;
        // the sources keep capturing until the last blocks are in
        _pump?.Join(MarginMs + 1500);
        foreach (var s in _sources) s.Dispose();
    }

    public void Dispose() => Stop();

    public static bool MicrophoneExists()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            return devices.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications) || devices.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ one device

    private sealed class Source : IDisposable
    {
        private readonly RecordingAudio _owner;
        public readonly bool Loopback;
        public string? DeviceName;
        public long Frames, LateFrames;
        /// <summary>The device's clock against the system clock (frames) at the last block, and how often they were put together again.</summary>
        public long Skew; public int Jumps;
        private MMDevice? _device;
        private AudioClient? _client;
        private AudioCaptureClient? _capture;
        private WaveFormat _format = WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels);
        private bool _float;
        private WdlResampler? _resampler;
        private Thread? _thread;
        private volatile bool _stop;
        private long _next = long.MinValue;   // where the next block goes on the timeline (frames)
        private float[] _stereo = new float[4096];
        private float[] _resampled = new float[4096];

        public Source(RecordingAudio owner, bool loopback) { _owner = owner; Loopback = loopback; }

        private string What => Loopback ? "the speaker sound" : "the microphone";

        public bool Open(out string why)
        {
            why = "";
            try
            {
                using var devices = new MMDeviceEnumerator();
                _device = Loopback ? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console)
                    : devices.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications) ? devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                    : devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                DeviceName = _device.FriendlyName;
                _client = _device.AudioClient;
                _format = _client.MixFormat;
                _float = _format.Encoding == WaveFormatEncoding.IeeeFloat
                         || (_format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71") /* KSDATAFORMAT_SUBTYPE_IEEE_FLOAT */);
                if (!_float && _format.BitsPerSample is not (16 or 24 or 32)) { why = $"{What} has a format that can't be recorded ({_format})"; Close(); return false; }
                // a 200 ms buffer: enough for a busy moment; blocks are read every 5 ms
                _client.Initialize(AudioClientShareMode.Shared, Loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None, 2_000_000, 0, _format, Guid.Empty);
                _capture = _client.AudioCaptureClient;
                _resampler = null;
                if (_format.SampleRate != Rate)
                {
                    _resampler = new WdlResampler();
                    _resampler.SetMode(true, 2, false);
                    _resampler.SetFilterParms();
                    _resampler.SetFeedMode(true);
                    _resampler.SetRates(_format.SampleRate, Rate);
                }
                return true;
            }
            catch (Exception ex)
            {
                why = $"{What} can't be recorded ({(ex.Message.Length > 80 ? ex.Message[..80] + "…" : ex.Message)})";
                AppLog.Warn("Record", why, ex);
                Close();
                return false;
            }
        }

        public void Start()
        {
            _client?.Start();
            _thread = new Thread(Run) { IsBackground = true, Name = Loopback ? "ocean-rec-speaker" : "ocean-rec-mic", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        private void Run()
        {
            var failedAt = DateTime.MinValue;
            while (!_stop)
            {
                try
                {
                    if (_capture is null)
                    {
                        // the device went away: the (new) default one, once a second
                        if (DateTime.UtcNow - failedAt < TimeSpan.FromSeconds(1)) { Thread.Sleep(50); continue; }
                        failedAt = DateTime.UtcNow;
                        if (!Open(out _)) continue;
                        _client!.Start();
                        _next = long.MinValue;
                        AppLog.Info("Record", $"{What} goes on from {DeviceName}");
                        continue;
                    }
                    Drain();
                    Thread.Sleep(5);
                }
                catch (Exception ex) when (!_stop)
                {
                    AppLog.Warn("Record", $"{What} stopped ({ex.Message}); trying the default device again");
                    Close();
                    failedAt = DateTime.UtcNow;
                }
            }
            try { _client?.Stop(); } catch { }
        }

        private void Drain()
        {
            var cap = _capture!;
            while (cap.GetNextPacketSize() > 0)
            {
                var ptr = cap.GetBuffer(out var frames, out var flags, out _, out var qpc);
                try
                {
                    if (frames <= 0) continue;
                    ToStereo(ptr, frames, (flags & AudioClientBufferFlags.Silent) != 0);
                    var outFrames = frames;
                    var data = _stereo;
                    if (_resampler is { } rs)
                    {
                        var need = rs.ResamplePrepare(frames, Channels, out var inBuf, out var inOff);
                        Array.Copy(_stereo, 0, inBuf, inOff, need * Channels);
                        var max = frames * Rate / _format.SampleRate + 16;
                        if (_resampled.Length < max * Channels) _resampled = new float[max * Channels * 2];
                        outFrames = rs.ResampleOut(_resampled, 0, need, max, Channels);
                        data = _resampled;
                    }
                    Frames += frames;
                    // where this block belongs: its capture stamp; continuous with the previous block unless the
                    // stamps say there is a real gap (or an overlap) of more than 15 ms
                    long at;
                    lock (_owner._ring)
                    {
                        if (_owner._t0 == long.MinValue) { _next = long.MinValue; continue; }
                        at = _owner.FrameAt(qpc);
                    }
                    if (_next != long.MinValue)
                    {
                        var skew = at - _next;
                        if (Math.Abs(skew) <= Rate * 15 / 1000) { at = _next; Skew = skew; }
                        else Jumps++;
                    }
                    _owner.Place(at, data, outFrames, !Loopback, this);
                    _next = at + outFrames;
                }
                finally { cap.ReleaseBuffer(frames); }
            }
        }

        /// <summary>The block as interleaved stereo float, whatever the device's format.</summary>
        private unsafe void ToStereo(IntPtr ptr, int frames, bool silent)
        {
            if (_stereo.Length < frames * Channels) _stereo = new float[frames * Channels * 2];
            if (silent) { Array.Clear(_stereo, 0, frames * Channels); return; }
            int ch = _format.Channels, bytes = _format.BitsPerSample / 8, align = _format.BlockAlign;
            var p = (byte*)ptr;
            for (var f = 0; f < frames; f++)
            {
                var b = p + f * align;
                var l = Sample(b, bytes);
                var r = ch > 1 ? Sample(b + bytes, bytes) : l;
                _stereo[f * 2] = l; _stereo[f * 2 + 1] = r;
            }
        }

        private unsafe float Sample(byte* s, int bytes) => _float ? *(float*)s : bytes switch
        {
            2 => *(short*)s / 32768f,
            3 => ((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) / 2147483648f,
            _ => *(int*)s / 2147483648f,
        };

        private void Close()
        {
            try { _capture?.Dispose(); } catch { }
            try { _client?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
            _capture = null; _client = null; _device = null;
        }

        public void Dispose()
        {
            _stop = true;
            _thread?.Join(500);
            Close();
        }
    }
}
