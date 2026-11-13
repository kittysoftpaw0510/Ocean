using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Ocean.Core.Protocol;

namespace Ocean.App.Services;

/// <summary>
/// What the PC plays, as 48 kHz stereo blocks, each stamped with the moment it was played (the performance
/// counter, µs; Windows stamps every block). Normally a loopback of the default output device (followed when it
/// changes). With <see cref="ExcludeOcean"/>: everything except Ocean's own sound (process loopback, Windows 10
/// 21H1 and later). That is used while Ocean plays a shared screen's sound, which must not be sent back: two PCs
/// sharing their sound with each other would echo forever. Where process loopback isn't there, it stays the device.
/// </summary>
public sealed class SoundCapture : IDisposable
{
    public const int Rate = ScreenSoundFrames.Rate, Channels = ScreenSoundFrames.Channels;
    public delegate void BlockHandler(long hostUs, float[] stereo, int frames);

    private readonly BlockHandler _onBlock;
    private Thread? _thread;
    private volatile bool _stop, _excludeOcean, _reopen;
    private static bool _noProcessLoopback;
    /// <summary>For checks: nothing is captured (the check hands its own sound to the sender).</summary>
    private static bool _testQuiet = false;

    private MMDevice? _device;
    private string? _deviceId;
    private AudioClient? _client;
    private AudioCaptureClient? _capture;
    private WaveFormat _format = WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels);
    private bool _float;
    private WdlResampler? _resampler;
    private float[] _stereo = new float[4096];
    private float[] _resampled = new float[4096];

    /// <summary>Why nothing can be captured (null while it works). Set on the capture thread.</summary>
    public string? Problem { get; private set; }
    /// <summary>"device" or "all but Ocean" (for the log and for checks).</summary>
    public string Mode { get; private set; } = "";
    public long Blocks { get; private set; }

    public bool ExcludeOcean
    {
        get => _excludeOcean;
        set { if (_excludeOcean == value) return; _excludeOcean = value; _reopen = true; }
    }

    public SoundCapture(BlockHandler onBlock) => _onBlock = onBlock;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "ocean-screen-sound", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        var failedAt = DateTime.MinValue;
        var checkedAt = DateTime.UtcNow;
        while (!_stop)
        {
            if (_testQuiet) { Thread.Sleep(20); continue; }
            try
            {
                if (_reopen) { _reopen = false; Close(); }
                if (_capture is null)
                {
                    if (DateTime.UtcNow - failedAt < TimeSpan.FromSeconds(1)) { Thread.Sleep(50); continue; }
                    failedAt = DateTime.UtcNow;
                    if (!Open()) continue;
                    _client!.Start();
                    AppLog.Info("Screen", $"sound: capturing {Mode}{(_device is null ? "" : " (" + _device.FriendlyName + ")")}");
                    continue;
                }
                Drain();
                // a new default output device (headphones plugged in): follow it
                if (_device is not null && DateTime.UtcNow - checkedAt > TimeSpan.FromSeconds(2))
                {
                    checkedAt = DateTime.UtcNow;
                    using var devices = new MMDeviceEnumerator();
                    if (devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID != _deviceId) { Close(); continue; }
                }
                Thread.Sleep(5);
            }
            catch (Exception ex) when (!_stop)
            {
                AppLog.Warn("Screen", $"sound capture stopped ({ex.Message}); opening it again");
                Close();
                failedAt = DateTime.UtcNow;
            }
        }
        try { _client?.Stop(); } catch { }
        Close();
    }

    private bool Open()
    {
        if (_excludeOcean && !_noProcessLoopback)
        {
            try
            {
                var client = ProcessLoopback.Activate(Environment.ProcessId, include: false);
                _format = WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels);
                _float = true;
                client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality, 2_000_000, 0, _format, Guid.Empty);
                _client = client;
                _capture = client.AudioCaptureClient;
                _resampler = null; _device = null; _deviceId = null;
                Mode = "all but Ocean";
                Problem = null;
                return true;
            }
            catch (Exception ex)
            {
                // older Windows: the whole output (an echo is possible only when two PCs share sound with each other)
                _noProcessLoopback = true;
                AppLog.Warn("Screen", "sound: process loopback isn't available here; capturing the whole output (" + ex.Message + ")");
                Close();
            }
        }
        try
        {
            using var devices = new MMDeviceEnumerator();
            _device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            _deviceId = _device.ID;
            _client = _device.AudioClient;
            _format = _client.MixFormat;
            _float = _format.Encoding == WaveFormatEncoding.IeeeFloat
                     || (_format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
            if (!_float && _format.BitsPerSample is not (16 or 24 or 32)) throw new NotSupportedException("the output has a format that can't be captured (" + _format + ")");
            _client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, 2_000_000, 0, _format, Guid.Empty);
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
            Mode = "device";
            Problem = null;
            return true;
        }
        catch (Exception ex)
        {
            var why = ex is COMException { HResult: unchecked((int)0x80070490) } ? "no speakers or headphones are connected" : ex.Message;
            if (Problem != why) AppLog.Warn("Screen", "sound can't be captured: " + why);
            Problem = why;
            Close();
            return false;
        }
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
                Blocks++;
                if (outFrames > 0) _onBlock((long)(qpc / 10), data, outFrames);
            }
            finally { cap.ReleaseBuffer(frames); }
        }
    }

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
        try { _client?.Stop(); } catch { }
        try { _capture?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        _capture = null; _client = null; _device = null;
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread is { } t && t != Thread.CurrentThread) t.Join(1000);
    }

    /// <summary>A capture of what processes play, with or without one process tree (AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK).</summary>
    private static class ProcessLoopback
    {
        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
        private static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr activationParams, ICompletionHandler handler, out IAsyncOperation op);

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICompletionHandler { void ActivateCompleted(IAsyncOperation op); }
        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAsyncOperation { void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface); }
        /// <summary>The handler is called on another thread: it says it may be (IAgileObject).</summary>
        [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAgileObject { }

        private sealed class Handler : ICompletionHandler, IAgileObject
        {
            public readonly TaskCompletionSource<object> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public void ActivateCompleted(IAsyncOperation op)
            {
                try
                {
                    op.GetActivateResult(out var hr, out var iface);
                    if (hr != 0) Done.TrySetException(Marshal.GetExceptionForHR(hr) ?? new COMException("activation failed", hr));
                    else Done.TrySetResult(iface);
                }
                catch (Exception ex) { Done.TrySetException(ex); }
            }
        }

        public static AudioClient Activate(int processId, bool include)
        {
            // AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = PROCESS_LOOPBACK, TargetProcessId, ProcessLoopbackMode }
            var ap = Marshal.AllocHGlobal(12);
            // PROPVARIANT { vt = VT_BLOB, blob = { cbSize, pBlobData } }
            var pv = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(ap, 0, 1); Marshal.WriteInt32(ap, 4, processId); Marshal.WriteInt32(ap, 8, include ? 0 : 1);
                for (var i = 0; i < 24; i += 4) Marshal.WriteInt32(pv, i, 0);
                Marshal.WriteInt16(pv, 0, 65); Marshal.WriteInt32(pv, 8, 12); Marshal.WriteIntPtr(pv, 16, ap);
                var h = new Handler();
                ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID, pv, h, out var op);
                if (!h.Done.Task.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("the process loopback didn't open");
                GC.KeepAlive(op);
                return h.Done.Task.Result is IAudioClient c ? new AudioClient(c) : throw new InvalidOperationException("no audio client");
            }
            catch (AggregateException ex) when (ex.InnerException is { } inner) { throw inner; }
            finally { Marshal.FreeHGlobal(pv); Marshal.FreeHGlobal(ap); }
        }
    }
}

/// <summary>
/// The sound of my shared screen: captured (<see cref="SoundCapture"/>), cut into 20 ms frames on the capture's own
/// time line, encoded (Opus, 96 kbps: clear music and speech at a twentieth of the picture's data) and posted to the
/// service as they are made. Each frame carries the moment its first sample was played here, so viewers play it
/// in step with the picture. Runs only while <see cref="Wanted"/> (the sound is on and somebody watches).
/// </summary>
public sealed class ScreenSoundSender : IDisposable
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private const int Samples = ScreenSoundFrames.Samples, Ch = ScreenSoundFrames.Channels, Rate = ScreenSoundFrames.Rate;

    private readonly string _url;
    private readonly object _lock = new();
    private bool _wanted, _disposed, _exclude;
    private SoundCapture? _capture;
    private CancellationTokenSource? _cts;
    private Channel<byte[]>? _out;
    // framing (capture thread)
    private IOpusEncoder? _opus;
    private readonly float[] _frame = new float[Samples * Ch];
    private readonly byte[] _packet = new byte[ScreenSoundFrames.MaxData];
    private int _fill;
    private long _runUs = long.MinValue, _runPos, _frameStartPos;
    private uint _seq;
    private string? _toldProblem;

    /// <summary>The sound can't be captured: why (shown to the user once). Raised on a worker thread.</summary>
    public event Action<string>? Problem;
    public long FramesSent { get; private set; }
    public string? Mode => _capture?.Mode;
    /// <summary>For checks: every frame as it is sent (host time µs, the PCM before encoding).</summary>
    public Action<long, float[]>? OnFrame { get; set; }

    public ScreenSoundSender(ApiClient api, string sessionId) => _url = $"{api.BaseUrl}/api/screen/feed/{sessionId}/sound?k={api.Token}";

    /// <summary>Leave Ocean's own sound out (it plays a shared screen's sound right now).</summary>
    public bool ExcludeOcean
    {
        get { lock (_lock) return _exclude; }
        set { lock (_lock) { _exclude = value; if (_capture is { } c) c.ExcludeOcean = value; } }
    }

    public bool Wanted
    {
        get { lock (_lock) return _wanted; }
        set
        {
            lock (_lock)
            {
                if (_disposed || _wanted == value) return;
                _wanted = value;
                if (value) StartLocked(); else StopLocked();
            }
        }
    }

    private void StartLocked()
    {
        _cts = new CancellationTokenSource();
        _out = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(100) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        _opus ??= CreateEncoder();
        _runUs = long.MinValue; _fill = 0;
        _capture = new SoundCapture(OnBlock) { ExcludeOcean = _exclude };
        _capture.Start();
        var ct = _cts.Token; var frames = _out;
        _ = Task.Run(() => PostLoopAsync(frames, ct));
        _ = Task.Run(() => WatchAsync(_capture, ct));
    }

    private void StopLocked()
    {
        _cts?.Cancel(); _cts?.Dispose(); _cts = null;
        _out?.Writer.TryComplete(); _out = null;
        _capture?.Dispose(); _capture = null;
    }

    private static IOpusEncoder CreateEncoder()
    {
        var e = OpusCodecFactory.CreateEncoder(Rate, Ch, OpusApplication.OPUS_APPLICATION_AUDIO);
        e.Bitrate = 96_000;
        e.Complexity = 7;
        e.UseVBR = true;
        e.UseConstrainedVBR = true; // no big peaks: every packet stays well under one Ethernet frame
        return e;
    }

    /// <summary>A block of sound: onto the time line (a jump of more than 15 ms starts a new run), out in 20 ms frames.</summary>
    private void OnBlock(long hostUs, float[] stereo, int frames)
    {
        if (_runUs != long.MinValue)
        {
            var expected = _runUs + _runPos * 1_000_000 / Rate;
            var skew = hostUs - expected;
            if (Math.Abs(skew) > 15_000) { FlushPartial(); _runUs = long.MinValue; }
            else if (Math.Abs(skew) > 1_000) _runUs += skew / 32; // the device's clock against the PC's: followed slowly, without a jump
        }
        if (_runUs == long.MinValue) { _runUs = hostUs; _runPos = 0; _fill = 0; }
        var i = 0;
        while (i < frames)
        {
            if (_fill == 0) _frameStartPos = _runPos;
            var n = Math.Min(frames - i, Samples - _fill);
            Array.Copy(stereo, i * Ch, _frame, _fill * Ch, n * Ch);
            _fill += n; _runPos += n; i += n;
            if (_fill == Samples) Emit();
        }
    }

    private void FlushPartial()
    {
        if (_fill == 0) return;
        Array.Clear(_frame, _fill * Ch, (Samples - _fill) * Ch);
        _fill = Samples;
        Emit();
    }

    private void Emit()
    {
        var us = _runUs + _frameStartPos * 1_000_000 / Rate;
        _fill = 0;
        if (_opus is not { } opus || _out is not { } output) return;
        int n;
        try { n = opus.Encode(_frame, Samples, _packet, _packet.Length); }
        catch (Exception ex) { AppLog.Warn("Screen", "sound: a frame couldn't be encoded: " + ex.Message); return; }
        if (n <= 0) return;
        OnFrame?.Invoke(us, _frame);
        var age = (ushort)Math.Clamp((ScreenSoundPlayer.NowUs() - us) / 10, 0, ushort.MaxValue);
        output.Writer.TryWrite(ScreenSoundFrames.Pack(new ScreenSoundFrames.Frame(_seq++, us, age, _packet.AsSpan(0, n).ToArray())));
        FramesSent++;
    }

    private async Task PostLoopAsync(Channel<byte[]> frames, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, _url) { Content = new FramesContent(frames.Reader, ct) };
                req.Headers.TransferEncodingChunked = true;
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return; // the session is over
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { AppLog.Warn("Screen", "sound: posting stopped: " + ex.Message); }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Tells the user once when the sound can't be captured (no output device), and once more when it can again.</summary>
    private async Task WatchAsync(SoundCapture capture, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1500, ct);
                var p = capture.Problem;
                if (p is not null && p != _toldProblem) { _toldProblem = p; Problem?.Invoke(p); }
                else if (p is null) _toldProblem = null;
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>A request body that is written as frames come (chunked), each sent at once.</summary>
    private sealed class FramesContent : HttpContent
    {
        private readonly ChannelReader<byte[]> _frames;
        private readonly CancellationToken _ct;
        public FramesContent(ChannelReader<byte[]> frames, CancellationToken ct) { _frames = frames; _ct = ct; }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            try
            {
                while (await _frames.WaitToReadAsync(_ct))
                {
                    while (_frames.TryRead(out var f)) await stream.WriteAsync(f, _ct);
                    await stream.FlushAsync(_ct);
                }
            }
            catch (OperationCanceledException) { }
        }

        protected override bool TryComputeLength(out long length) { length = -1; return false; }
    }

    public void Dispose()
    {
        lock (_lock) { _disposed = true; _wanted = false; StopLocked(); }
    }
}

/// <summary>
/// Plays a shared screen's sound, in step with its picture. Frames come as they arrive (<c>/api/screen/view/sound</c>)
/// and are played by their time stamps, not by arrival: every frame is heard <see cref="DelayMs"/> after the
/// sharing PC played it, which is about how long its picture takes to get here, so what is seen and heard meet.
/// The link between the sharing PC's clock and this one is learned from the frames themselves (the quickest
/// arrivals of the last seconds, counted from when each was sent), so it needs no clock setting and follows a drift.
/// Small differences (the two sound cards never run at exactly the same speed) are evened out by playing up to
/// 0.5 % faster or slower, which nobody hears; a large one (after a stall) is a clean jump. A frame that hasn't
/// come by the time it is due is concealed (Opus fills the gap from the sound before it), and silence after 60 ms.
/// Follows the default output device when it changes.
/// </summary>
public sealed class ScreenSoundPlayer : IDisposable
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private const int Rate = ScreenSoundFrames.Rate, Ch = ScreenSoundFrames.Channels, Samples = ScreenSoundFrames.Samples;
    private const long FrameUs = ScreenSoundFrames.Ms * 1000;
    /// <summary>Opus hands a sample back this much later than it went in (its look-ahead, 312 samples).</summary>
    private const long CodecDelayUs = 6_500;
    /// <summary>
    /// The quickest way from the sharing PC's app to this one (its service, the network, this service): a frame's
    /// quickest arrival counted from its sending is the clocks' difference plus this. Measured on one PC (where both
    /// clocks are the same): 0.6 to 0.9 ms; a cable or Wi-Fi adds well under a millisecond.
    /// </summary>
    private const long WayUs = 1_000;
    /// <summary>Settling: in the first seconds the clocks' link is still being learned; a difference is jumped, not played out slowly.</summary>
    private const long SettleUs = 4_000_000;
    private const int OutLatencyMs = 40;

    private readonly string _url;
    private readonly object _lock = new();
    private readonly SortedList<long, ScreenSoundFrames.Frame> _pending = new();
    private readonly long[] _minBuckets = new long[10];
    private readonly long[] _bucketAt = new long[10];
    private readonly IOpusDecoder _opus = OpusCodecFactory.CreateDecoder(Rate, Ch);
    private readonly float[] _decoded = new float[Samples * Ch];
    private CancellationTokenSource _cts = new();
    private Thread? _reader;
    private WasapiOut? _out;
    private string? _outDevice;
    private Timer? _deviceCheck;
    private bool _disposed;
    // play-out (under _lock)
    private long _offsetUs = long.MinValue;  // local µs − host µs (sent) for the quickest arrival
    private bool _running;
    private long _startedUs;
    private bool _lastDecoded;               // the last queued samples are a frame decoded in order (the next one joins on)
    private double _cursorUs;                // the host time of the next sample out
    private float[] _pcm = new float[Rate * Ch];
    private int _pcmCount;                   // samples (stereo frames) queued
    private double _pcmStartUs;              // host time of the first queued sample
    private long _lastSeq = -1;
    private int _concealed;
    private long _writtenFrames;

    /// <summary>From the sharing PC playing a sound to it being heard here (ms).</summary>
    public int DelayMs { get; }
    public bool Muted { get; set; }
    public long FramesIn { get; private set; }
    public long Concealed { get; private set; }
    public long Late { get; private set; }
    public long Jumps { get; private set; }
    /// <summary>The quickest arrival of the last seconds (local µs − host µs); for checks.</summary>
    public long OffsetUs { get { lock (_lock) return _offsetUs; } }
    public string? Problem { get; private set; }
    /// <summary>For checks: a block of samples handed to the device (host time of its first sample, local µs it is heard at, samples, count).</summary>
    public Action<double, long, float[], int>? OnPlayed { get; set; }

    /// <param name="fps">The picture's rate: its delay depends on it (a picture takes about two picture times through capture, encoder and decoder).</param>
    public ScreenSoundPlayer(ApiClient api, int fps)
    {
        _url = $"{api.BaseUrl}/api/screen/view/sound?k={api.Token}";
        DelayMs = PictureDelayMs(fps);
    }

    /// <summary>
    /// How long a shared picture takes from the screen to this screen: capturing (half a picture time on average,
    /// and the copy: about 8 ms), from the encoder to the decoded picture (measured between two services: 153 ms at
    /// 15 fps, 72 ms at 30 fps, i.e. 2.2 picture times and 5 ms) and drawing it (the next frame of the app and of
    /// the desktop: about 28 ms). 221 ms at 15 fps, 131 ms at 30 fps.
    /// </summary>
    public static int PictureDelayMs(int fps) => 41 + (int)Math.Round(2.7 * 1000.0 / Math.Clamp(fps, 1, 60));

    public static long NowUs() => (long)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));

    public void Start()
    {
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ocean-screen-sound-in" };
        _reader.Start();
        OpenOutput();
        _deviceCheck = new Timer(_ => CheckDevice(), null, 2000, 2000);
    }

    // ------------------------------------------------------------------ in

    private void ReadLoop()
    {
        var header = new byte[ScreenSoundFrames.HeaderSize];
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var resp = Http.Send(new HttpRequestMessage(HttpMethod.Get, _url), HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var stream = resp.Content.ReadAsStream(ct);
                    while (ScreenSoundFrames.ReadAsync(stream, header, ct).GetAwaiter().GetResult() is { } f) Arrived(f);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { if (!ct.IsCancellationRequested) AppLog.Info("Screen", "sound: the stream broke (" + ex.Message + "); again"); }
            if (ct.WaitHandle.WaitOne(500)) return;
        }
    }

    private void Arrived(in ScreenSoundFrames.Frame f)
    {
        var now = NowUs();
        lock (_lock)
        {
            FramesIn++;
            // the quickest arrival of the last 10 s (in 1 s buckets) is the link between the two clocks
            var d = now - f.SentUs;
            var bucket = now / 1_000_000;
            var i = (int)(bucket % _minBuckets.Length);
            if (_bucketAt[i] != bucket) { _bucketAt[i] = bucket; _minBuckets[i] = d; }
            else if (d < _minBuckets[i]) _minBuckets[i] = d;
            var min = long.MaxValue;
            for (var k = 0; k < _minBuckets.Length; k++) if (_bucketAt[k] > bucket - _minBuckets.Length && _minBuckets[k] < min) min = _minBuckets[k];
            _offsetUs = min;
            if (_running && f.HostUs - CodecDelayUs + FrameUs <= PcmEndUs()) { Late++; return; } // its time has passed
            _pending[f.HostUs] = f;
            while (_pending.Count > 200) _pending.RemoveAt(0);
        }
    }

    // ------------------------------------------------------------------ out

    private void OpenOutput()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            var dev = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _outDevice = dev.ID;
            var o = new WasapiOut(dev, AudioClientShareMode.Shared, true, OutLatencyMs);
            o.Init(new SampleToWaveProvider(new Source(this)));
            o.PlaybackStopped += (_, e) =>
            {
                if (_disposed) return;
                if (e.Exception is not null) AppLog.Info("Screen", "sound: the output stopped (" + e.Exception.Message + "); opening it again");
                _outDevice = null; // the check opens the (new) default device
            };
            lock (_lock) { _writtenFrames = 0; _running = false; }
            o.Play();
            _out = o;
            Problem = null;
        }
        catch (Exception ex)
        {
            Problem = ex is COMException { HResult: unchecked((int)0x80070490) } ? "no speakers or headphones are connected" : ex.Message;
            AppLog.Warn("Screen", "sound can't be played: " + Problem);
            _outDevice = null;
        }
    }

    private void CheckDevice()
    {
        if (_disposed) return;
        try
        {
            using var devices = new MMDeviceEnumerator();
            if (!devices.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return;
            var id = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            if (id == _outDevice && _out is { PlaybackState: PlaybackState.Playing }) return;
            var old = _out; _out = null;
            try { old?.Dispose(); } catch { }
            OpenOutput();
        }
        catch (Exception ex) { AppLog.Info("Screen", "sound: output check: " + ex.Message); }
    }

    /// <summary>The device asks for samples: the ones due when these will be heard.</summary>
    private sealed class Source : ISampleProvider
    {
        private readonly ScreenSoundPlayer _p;
        public Source(ScreenSoundPlayer p) => _p = p;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, Ch);
        public int Read(float[] buffer, int offset, int count) { _p.Fill(buffer, offset, count / Ch); return count; }
    }

    private void Fill(float[] buffer, int offset, int frames)
    {
        // when the first of these samples will be heard: now plus what the device holds already
        var now = NowUs();
        long queuedUs = 0;
        try
        {
            if (_out is { } o)
            {
                var played = o.GetPosition() / (double)o.OutputWaveFormat.AverageBytesPerSecond;
                queuedUs = Math.Max(0, (long)((Interlocked.Read(ref _writtenFrames) / (double)Rate - played) * 1_000_000));
            }
        }
        catch { }
        var heardAt = now + queuedUs;
        lock (_lock)
        {
            // the first few frames first: the clocks' link needs more than one arrival
            if (_offsetUs == long.MinValue || (!_running && FramesIn < 5)) { Array.Clear(buffer, offset, frames * Ch); Interlocked.Add(ref _writtenFrames, frames); return; }
            // the host time that should be heard then
            var due = heardAt - (_offsetUs - WayUs) - DelayMs * 1000L;
            if (!_running) { Restart(due); _running = true; _startedUs = now; }
            var error = _cursorUs - due; // > 0: ahead (heard too early): play slower
            if (Math.Abs(error) > (now - _startedUs < SettleUs ? 6_000 : 80_000)) { if (now - _startedUs >= SettleUs) Jumps++; Restart(due); error = 0; }
            var ratio = 1 - Math.Clamp(error * 5e-7, -0.005, 0.005);
            // the samples to read, and how they are spread over the frames asked for (linear: the change is tiny)
            var take = frames * ratio;
            Ensure(_cursorUs + (take + 2) * 1_000_000.0 / Rate);
            var exact = Math.Max(0, (_cursorUs - _pcmStartUs) * Rate / 1_000_000.0);
            var start = (int)exact;
            var step = take / frames;
            var pos = exact - start;
            for (var i = 0; i < frames; i++)
            {
                var j = (int)pos; var t = (float)(pos - j);
                var a = (int)Math.Min(start + j, _pcmCount - 1); var b = (int)Math.Min(start + j + 1, _pcmCount - 1);
                for (var c = 0; c < Ch; c++)
                    buffer[offset + i * Ch + c] = a < 0 ? 0 : _pcm[a * Ch + c] + (_pcm[b * Ch + c] - _pcm[a * Ch + c]) * t;
                pos += step;
            }
            OnPlayed?.Invoke(_cursorUs, heardAt, buffer, frames);
            if (Muted) Array.Clear(buffer, offset, frames * Ch); // (muted: on in step, silent)
            _cursorUs += take * 1_000_000.0 / Rate;
            Drop(start + (int)pos);
        }
        Interlocked.Add(ref _writtenFrames, frames);
    }

    /// <summary>Starts over at host time <paramref name="at"/>: nothing queued, the next frame due is decoded fresh.</summary>
    private void Restart(long at)
    {
        _cursorUs = at;
        _pcmCount = 0; _pcmStartUs = at;
        _concealed = 0; _lastDecoded = false;
        while (_pending.Count > 0 && _pending.Keys[0] + FrameUs - CodecDelayUs <= at) _pending.RemoveAt(0);
    }

    /// <summary>Decodes (or conceals, or fills with silence) until host time <paramref name="untilUs"/> is queued.</summary>
    private void Ensure(double untilUs)
    {
        while (PcmEndUs() < untilUs)
        {
            var end = PcmEndUs();
            // what is too late now goes
            while (_pending.Count > 0 && _pending.Keys[0] - CodecDelayUs + FrameUs <= end + 1_000) { _pending.RemoveAt(0); Late++; }
            if (_pending.Count > 0)
            {
                var f = _pending.Values[0];
                var at = f.HostUs - CodecDelayUs;
                var diff = at - end;
                if (_lastDecoded && f.Seq == _lastSeq + 1 && Math.Abs(diff) <= 15_000)
                {
                    // the next frame of an unbroken run: it joins on, sample for sample. A small difference of its
                    // time stamp (the sharing PC's sound card against its clock) moves the time line, not the
                    // samples: the speed control then evens it out unheard.
                    _pending.RemoveAt(0);
                    _pcmStartUs += diff; _cursorUs += diff;
                    Decode(f, 0);
                    continue;
                }
                if (diff <= 1_000)
                {
                    // after a break (the start, silence, a concealed frame): exactly where it belongs; a frame that
                    // began a little before now loses its start
                    _pending.RemoveAt(0);
                    Decode(f, (int)(Math.Max(0, -diff) * Rate / 1_000_000));
                    continue;
                }
                // a gap before it: frames lost on the way (numbers missing) are concealed; a pause of the sharing PC's sound is silence
                var lost = _lastSeq >= 0 && f.Seq > _lastSeq + 1;
                if (lost && _concealed < 3 && diff >= FrameUs - 1_000) { Conceal(); continue; }
                Silence(Math.Max(1, (int)(Math.Min(diff, untilUs - end) * Rate / 1_000_000)));
                continue;
            }
            // nothing here yet: conceal a little, then silence
            if (_lastDecoded || (_lastSeq >= 0 && _concealed > 0 && _concealed < 3)) { Conceal(); continue; }
            Silence(Math.Max(1, (int)((untilUs - end) * Rate / 1_000_000)));
        }
    }

    private double PcmEndUs() => _pcmStartUs + _pcmCount * 1_000_000.0 / Rate;

    /// <param name="skip">Samples at its start that are not played (their time has passed).</param>
    private void Decode(in ScreenSoundFrames.Frame f, int skip)
    {
        int n;
        try { n = _opus.Decode(f.Data, _decoded, Samples, false); }
        catch { n = 0; }
        _lastSeq = f.Seq;
        if (n <= 0) { Silence(Samples - skip); _lastDecoded = false; return; }
        skip = Math.Clamp(skip, 0, n);
        Grow(n - skip);
        Array.Copy(_decoded, skip * Ch, _pcm, _pcmCount * Ch, (n - skip) * Ch);
        _pcmCount += n - skip;
        _concealed = 0; _lastDecoded = true;
    }

    private void Conceal()
    {
        int n;
        try { n = _opus.Decode(ReadOnlySpan<byte>.Empty, _decoded, Samples, false); }
        catch { n = 0; }
        _lastDecoded = false;
        if (n <= 0) { Silence(Samples); return; }
        Append(_decoded, n);
        _concealed++; Concealed++;
    }

    private void Silence(int samples)
    {
        _lastDecoded = false;
        Grow(samples);
        Array.Clear(_pcm, _pcmCount * Ch, samples * Ch);
        _pcmCount += samples;
    }

    private void Append(float[] src, int samples)
    {
        Grow(samples);
        Array.Copy(src, 0, _pcm, _pcmCount * Ch, samples * Ch);
        _pcmCount += samples;
    }

    private void Grow(int more)
    {
        if ((_pcmCount + more) * Ch <= _pcm.Length) return;
        Array.Resize(ref _pcm, Math.Max(_pcm.Length * 2, (_pcmCount + more) * Ch));
    }

    /// <summary>Forgets the samples before index <paramref name="upTo"/> (played).</summary>
    private void Drop(long upTo)
    {
        var n = (int)Math.Clamp(upTo, 0, _pcmCount);
        if (n == 0) return;
        Array.Copy(_pcm, n * Ch, _pcm, 0, (_pcmCount - n) * Ch);
        _pcmCount -= n;
        _pcmStartUs += n * 1_000_000.0 / Rate;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _deviceCheck?.Dispose();
        try { _out?.Dispose(); } catch { }
        _out = null;
    }
}
