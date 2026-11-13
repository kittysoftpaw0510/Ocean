using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Ocean.App.Services;

/// <summary>Where ffmpeg is, and a way to make sure a helper process never outlives Ocean.</summary>
public static class ScreenTools
{
    /// <summary>ffmpeg.exe next to Ocean (shipped by the installer), in the repo's .tools folder (development), or null.</summary>
    public static string? Ffmpeg { get; } = Find();

    private static string? Find()
    {
        if (Environment.GetEnvironmentVariable("OCEAN_FFMPEG") is { Length: > 0 } set && File.Exists(set)) return set; // tests
        var beside = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(beside)) return beside;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var dev = Path.Combine(dir.FullName, ".tools", "ffmpeg", "ffmpeg.exe");
            if (File.Exists(dev)) return dev;
        }
        return null;
    }

    private static IntPtr _job;
    private static readonly object JobLock = new();

    /// <summary>
    /// Ties a helper process to Ocean's life: Windows ends it when Ocean ends, however that happens (also a crash
    /// or Task Manager). A capture must never go on without the app that shows it is running.
    /// </summary>
    public static void TieToApp(Process process)
    {
        try
        {
            lock (JobLock)
            {
                if (_job == IntPtr.Zero)
                {
                    _job = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                    var mem = Marshal.AllocHGlobal(size);
                    try { Marshal.StructureToPtr(info, mem, false); SetInformationJobObject(_job, 9, mem, (uint)size); }
                    finally { Marshal.FreeHGlobal(mem); }
                }
                AssignProcessToJobObject(_job, process.Handle);
            }
        }
        catch (Exception ex) { AppLog.Warn("Screen", "helper process not tied to the app", ex); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}

/// <summary>
/// Captures what the user picked and encodes it (H.264) with ffmpeg, which posts the stream straight to the
/// service. Runs only while <see cref="Wanted"/> (somebody watches): otherwise nothing is captured at all.
/// Encoders are tried in order: the graphics card's (NVIDIA, Intel, AMD), then the software one, which always
/// works. One that fails at once is not tried again this session. If the capture stops by itself (a hiccup of the
/// graphics driver, a display change), it is started again; a window that was closed ends the sharing.
/// </summary>
public sealed class ScreenEncoder : IDisposable
{
    private static readonly string[] Encoders = { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" };
    /// <summary>Encoders that failed on this PC (this run of the app).</summary>
    private static readonly HashSet<string> Failed = new();

    private readonly string _feedUrl;
    private readonly ScreenPick _pick;
    private readonly int _width, _height;
    private readonly object _lock = new();
    private Process? _process;
    private bool _wanted, _disposed;
    private int _generation;
    private int _quickFailures;

    /// <summary>The sharing can't go on: why (shown to the user). Raised on a worker thread.</summary>
    public event Action<string>? Stopped;
    public string? EncoderInUse { get; private set; }

    public ScreenEncoder(ApiClient api, string sessionId, ScreenPick pick)
    {
        _feedUrl = $"{api.BaseUrl}/api/screen/feed/{sessionId}?k={api.Token}";
        _pick = pick;
        (_width, _height) = pick.SendSize();
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
                _generation++;
                if (value) { var gen = _generation; _quickFailures = 0; _ = Task.Run(() => RunAsync(gen)); }
                else Kill();
            }
        }
    }

    private void Kill()
    {
        var p = _process; _process = null;
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.Dispose(); } catch { }
    }

    private async Task RunAsync(int generation)
    {
        while (true)
        {
            string encoder;
            lock (_lock)
            {
                if (_disposed || !_wanted || generation != _generation) return;
                encoder = Encoders.First(e => e == "libx264" || !Failed.Contains(e));
            }
            if (ScreenTools.Ffmpeg is not { } ffmpeg) { Stopped?.Invoke("ffmpeg.exe is missing next to Ocean (install Ocean again)."); return; }
            if (_pick.Kind == ScreenPick.Kinds.Window && !ScreenSources.Exists(_pick.Window)) { Stopped?.Invoke("the window was closed"); return; }

            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in Arguments(encoder)) psi.ArgumentList.Add(a);
            Process p;
            var started = Stopwatch.StartNew();
            var errors = new System.Text.StringBuilder();
            try
            {
                p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
                ScreenTools.TieToApp(p);
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line && errors.Length < 2000) lock (errors) errors.AppendLine(line); };
                p.BeginErrorReadLine();
            }
            catch (Exception ex) { Stopped?.Invoke("the capture could not start: " + ex.Message); return; }
            lock (_lock)
            {
                if (_disposed || !_wanted || generation != _generation) { try { p.Kill(true); } catch { } p.Dispose(); return; }
                _process = p;
                EncoderInUse = encoder;
            }
            AppLog.Info("Screen", $"capturing {_pick.Kind} at {_width}x{_height}, {_pick.Fps} fps, encoder {encoder}");
            try { await p.WaitForExitAsync(); } catch { }
            var ran = started.Elapsed;
            lock (_lock)
            {
                if (ReferenceEquals(_process, p)) _process = null;
                if (_disposed || !_wanted || generation != _generation) return; // stopped on purpose
            }
            string tail;
            lock (errors) tail = errors.ToString().Trim();
            if (tail.Length > 400) tail = tail[^400..];
            AppLog.Warn("Screen", $"capture with {encoder} stopped after {ran.TotalSeconds:0.0} s: {tail.Replace("\r", "").Replace('\n', ' ')}");
            if (ran < TimeSpan.FromSeconds(4))
            {
                // failed at once: this encoder doesn't work here (no such graphics card, an old driver) - the next one
                if (encoder != "libx264") { lock (_lock) Failed.Add(encoder); continue; }
                if (++_quickFailures >= 3) { Stopped?.Invoke("this PC can't capture that (" + (tail.Length > 0 ? tail.Split('\n')[^1].Trim() : "the capture failed") + ")"); return; }
            }
            else _quickFailures = 0;
            await Task.Delay(1000);
        }
    }

    private IEnumerable<string> Arguments(string encoder)
    {
        var inv = CultureInfo.InvariantCulture;
        var src = new List<string>
        {
            _pick.Kind == ScreenPick.Kinds.Window ? "hwnd=" + ((ulong)_pick.Window.ToInt64()).ToString(inv) : "hmonitor=" + ((ulong)_pick.Monitor.ToInt64()).ToString(inv),
            "max_framerate=" + _pick.Fps, "capture_cursor=1", "display_border=0",
            "width=" + _width, "height=" + _height, "resize_mode=scale_aspect", "scale_mode=bicubic",
        };
        if (_pick.Kind == ScreenPick.Kinds.Region)
            src.AddRange(new[] { "crop_left=" + _pick.CropLeft, "crop_top=" + _pick.CropTop, "crop_right=" + _pick.CropRight, "crop_bottom=" + _pick.CropBottom });

        // a full-HD desktop needs up to 3 Mbps at its busiest (30 fps: 5); smaller pictures in proportion; a still
        // screen takes a small part of that whatever the limit
        var pixels = (double)_width * _height / (1920 * 1080);
        var max = (int)Math.Max(600, (_pick.Smooth ? 5000 : 3000) * Math.Sqrt(pixels));
        var target = max * 6 / 10;
        var gop = (_pick.Fps * 4).ToString(inv);

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "gfxcapture=" + string.Join(':', src) };
        args.AddRange(new[] { "-vf", "hwdownload,format=bgra,format=" + (encoder == "libx264" ? "yuv420p" : "nv12"), "-an", "-c:v", encoder });
        switch (encoder)
        {
            case "h264_nvenc":
                args.AddRange(new[] { "-preset", "p4", "-tune", "ll", "-zerolatency", "1", "-rc", "vbr", "-cq", "27", "-b:v", "0", "-maxrate", max + "k", "-bufsize", max + "k" });
                break;
            case "h264_qsv":
                args.AddRange(new[] { "-preset", "veryfast", "-look_ahead", "0", "-async_depth", "1", "-b:v", target + "k", "-maxrate", max + "k" });
                break;
            case "h264_amf":
                args.AddRange(new[] { "-usage", "lowlatency", "-rc", "vbr_peak", "-b:v", target + "k", "-maxrate", max + "k" });
                break;
            default:
                args.AddRange(new[] { "-preset", "veryfast", "-tune", "zerolatency", "-crf", "25", "-maxrate", max + "k", "-bufsize", max + "k" });
                break;
        }
        // a key picture every 4 s (where a new viewer starts), each with its parameters; no pictures held back
        args.AddRange(new[] { "-g", gop, "-bf", "0", "-bsf:v", "dump_extra=freq=keyframe", "-f", "h264", "-flush_packets", "1", "-method", "POST", _feedUrl });
        return args;
    }

    public void Dispose()
    {
        lock (_lock) { _disposed = true; _wanted = false; _generation++; Kill(); }
    }
}
