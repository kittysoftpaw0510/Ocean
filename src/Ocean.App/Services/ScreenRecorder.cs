using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;

namespace Ocean.App.Services;

/// <summary>How recordings are made, remembered per Windows user (%LocalAppData%\Ocean\recording.json).</summary>
public sealed class RecordingSettings
{
    public string Quality { get; set; } = RecordingQuality.Balanced;
    public bool Speaker { get; set; } = true;
    public bool Mic { get; set; } = true;
    public string? Folder { get; set; }

    /// <summary>The Windows user's own Videos folder.</summary>
    public static string DefaultFolder => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    [System.Text.Json.Serialization.JsonIgnore]
    public string FolderOrDefault => string.IsNullOrWhiteSpace(Folder) ? DefaultFolder : Folder!;

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ocean", "recording.json");

    public static RecordingSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<RecordingSettings>(File.ReadAllText(FilePath)) is { } s)
            {
                if (!RecordingQuality.All.Contains(s.Quality)) s.Quality = RecordingQuality.Balanced;
                return s;
            }
        }
        catch (Exception ex) { AppLog.Warn("Record", "recording.json can't be read", ex); }
        return new RecordingSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AppLog.Warn("Record", "recording.json can't be written", ex); }
    }
}

/// <summary>
/// The three qualities. Every one records only the pictures that changed (a still screen costs almost nothing),
/// as H.264 + AAC in MP4, which every PC and Ocean's own player can play. Measured on real screen recordings:
/// Balanced files are about a sixth of what Windows' own screen recorder makes, at practically the same sharpness.
/// </summary>
public static class RecordingQuality
{
    public const string Small = "small", Balanced = "balanced", High = "high";
    public static readonly string[] All = { Small, Balanced, High };

    public sealed record Spec(string Key, string Name, string Hint, int MaxWidth, int MaxHeight, int Fps, int Crf, int MaxKbps, int AudioKbps);

    public static Spec Of(string key) => key switch
    {
        Small => new(Small, "Small file", "Up to 720p, 15 pictures a second: the smallest files; small text gets soft", 1280, 720, 15, 30, 2500, 64),
        High => new(High, "High", "Up to 1440p, 60 pictures a second: for games and fast motion; files several times larger", 2560, 1440, 60, 22, 16000, 160),
        _ => new(Balanced, "Balanced", "Up to 1080p, 30 pictures a second: sharp text, small files (recommended)", 1920, 1080, 30, 27, 8000, 96),
    };

    /// <summary>The size recorded: the source scaled down to fit (never up), even numbers.</summary>
    public static (int Width, int Height) Fit(Spec spec, int width, int height)
    {
        var scale = Math.Min(1.0, Math.Min((double)spec.MaxWidth / Math.Max(1, width), (double)spec.MaxHeight / Math.Max(1, height)));
        return (Math.Max(64, (int)Math.Round(width * scale) & ~1), Math.Max(64, (int)Math.Round(height * scale) & ~1));
    }
}

/// <summary>
/// One screen recording: ffmpeg captures what was picked (Windows.Graphics.Capture) and encodes it; the sound
/// (<see cref="RecordingAudio"/>) comes in over a named pipe. It is written as Matroska while it runs, which stays
/// playable even if the PC loses power or Ocean is ended, and turned into MP4 (moov at the front, quick to open
/// and to stream) when it stops. Recording stops by itself when the disk gets full or a recorded window closes.
/// </summary>
public sealed class ScreenRecorder : IDisposable
{
    private const long MinFreeBytes = 500L * 1024 * 1024;

    private readonly ScreenPick _pick;
    private readonly RecordingQuality.Spec _spec;
    private readonly RecordingAudio? _audio;
    private readonly string _folder;
    private NamedPipeServerStream? _pipe;
    private readonly Stopwatch _clock = new();
    private readonly object _lock = new();
    private bool _stopping, _done;
    private string? _stopReason;
    private Timer? _watch;

    public string WorkingPath { get; }
    /// <summary>The video encoder used (libx264, or the graphics card's for High).</summary>
    public string Encoder { get; private set; } = "libx264";
    private string PixelFormat => Encoder == "libx264" ? "yuv420p" : "nv12";
    /// <summary>
    /// Screen colours to video colours, on the processor (this ffmpeg can't do it on the graphics card here). At 60
    /// pictures a second the fast way: measured 12 instead of 14 ms of processor time per full-HD picture.
    /// </summary>
    private string Convert => (_spec.Fps >= 60 ? "scale=flags=fast_bilinear," : "") + "format=" + PixelFormat;
    public string FinalPath { get; private set; }
    public int Width { get; }
    public int Height { get; }
    public TimeSpan Elapsed => _clock.Elapsed;
    public long Bytes { get; private set; }
    public bool HasSound => _audio is { HasSpeaker: true } or { HasMic: true };
    public RecordingAudio? Audio => _audio;
    public IReadOnlyList<string> Problems => _audio?.Problems ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>The recording is over and its file is ready (or failed: Error). Raised on a worker thread.</summary>
    public event Action<RecordingResult>? Finished;

    public ScreenRecorder(ScreenPick pick, RecordingSettings settings)
    {
        _pick = pick;
        _spec = RecordingQuality.Of(settings.Quality);
        (Width, Height) = RecordingQuality.Fit(_spec, pick.SourceWidth, pick.SourceHeight);
        _folder = settings.FolderOrDefault;
        Directory.CreateDirectory(_folder);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        var name = $"Screen recording {stamp}";
        FinalPath = Unique(Path.Combine(_folder, name + ".mp4"));
        WorkingPath = Path.ChangeExtension(FinalPath, ".mkv");
        if (settings.Speaker || settings.Mic) _audio = new RecordingAudio(settings.Speaker, settings.Mic);
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path) && !File.Exists(Path.ChangeExtension(path, ".mkv"))) return path;
        for (var i = 2; ; i++)
        {
            var p = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)} ({i}){Path.GetExtension(path)}");
            if (!File.Exists(p) && !File.Exists(Path.ChangeExtension(p, ".mkv"))) return p;
        }
    }

    /// <summary>Throws when it can't start (no ffmpeg, the disk is full).</summary>
    public void Start()
    {
        if (ScreenTools.Ffmpeg is not { } ffmpeg) throw new InvalidOperationException("ffmpeg.exe is missing next to Ocean (install Ocean again).");
        if (FreeBytes(_folder) is { } free && free < MinFreeBytes) throw new InvalidOperationException($"There is too little free space on the disk of {_folder}.");
        var inv = CultureInfo.InvariantCulture;
        Encoder = RecordingEncoders.For(_spec);

        // Two processes. The capture waits for the screen to change before it can do anything, also before it can
        // stop: on a still window, ffmpeg asked to finish would wait forever, and its encoder would keep the last
        // pictures. So one process only captures (it keeps nothing back) and hands timestamped raw pictures to the
        // other, which encodes and writes the file. Stopping ends the capture at once; the encoder sees the end of
        // its input, writes everything it still holds and closes the file properly.
        var src = new List<string>
        {
            _pick.Kind == ScreenPick.Kinds.Window ? "hwnd=" + ((ulong)_pick.Window.ToInt64()).ToString(inv) : "hmonitor=" + ((ulong)_pick.Monitor.ToInt64()).ToString(inv),
            "max_framerate=" + _spec.Fps, "capture_cursor=1", "display_border=0",
            "width=" + Width, "height=" + Height, "resize_mode=scale_aspect", "scale_mode=bicubic",
        };
        if (_pick.Kind == ScreenPick.Kinds.Region)
            src.AddRange(new[] { "crop_left=" + _pick.CropLeft, "crop_top=" + _pick.CropTop, "crop_right=" + _pick.CropRight, "crop_bottom=" + _pick.CropBottom });
        // info level for the first picture's report (only "Parsed_showinfo" lines and real problems are looked at):
        // the moment it is captured is time 0 for the sound and for where the file ends
        var capArgs = new List<string>
        {
            "-hide_banner", "-loglevel", "info", "-nostats", "-nostdin", "-f", "lavfi", "-i", "gfxcapture=" + string.Join(':', src),
            "-filter_complex", "[0:v]hwdownload,format=bgra,split=2[v][m];[m]select='eq(n\\,0)',showinfo,nullsink;[v]" + Convert + "[out]",
            "-map", "[out]", "-c:v", "rawvideo", "-f", "nut", "pipe:1",
        };

        string? pipeName = null;
        if (_audio is { } && HasSound)
        {
            pipeName = "ocean-rec-" + Guid.NewGuid().ToString("N");
            _pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
        }
        var encArgs = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-thread_queue_size", "64", "-f", "nut", "-i", "pipe:0" };
        if (pipeName is not null)
            encArgs.AddRange(new[] { "-thread_queue_size", "1024", "-f", "f32le", "-ar", RecordingAudio.Rate.ToString(inv), "-ac", RecordingAudio.Channels.ToString(inv), "-i", @"\\.\pipe\" + pipeName });
        encArgs.AddRange(new[] { "-map", "0:v" });
        if (pipeName is not null) encArgs.AddRange(new[] { "-map", "1:a" });
        // only changed pictures, a key picture at least every 10 s, quality-driven with a ceiling for busy moments
        encArgs.AddRange(RecordingEncoders.Arguments(Encoder, _spec));
        encArgs.AddRange(new[] { "-force_key_frames", "expr:gte(t,n_forced*10)", "-fps_mode", "vfr" });
        if (pipeName is not null) encArgs.AddRange(new[] { "-c:a", "aac", "-b:a", _spec.AudioKbps + "k" });
        // A still screen gives no pictures for a while; the sound must not wait for them (by default ffmpeg holds
        // sound back up to 10 s to interleave it with pictures, and the sound input stalls meanwhile). The sound
        // comes a quarter of a second behind the pictures (RecordingAudio's margin): 1 s leaves room for that.
        encArgs.AddRange(new[] { "-max_interleave_delta", "1000000", "-f", "matroska", "-y", WorkingPath });

        // the encoder first (it waits for its inputs), then the capture
        var enc = StartFfmpeg(ffmpeg, encArgs, stdin: true);
        _encoder = enc;
        var encErrors = new System.Text.StringBuilder();
        enc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 } line) return;
            if (Debug) AppLog.Info("Record", "encoder: " + line);
            lock (encErrors) { if (encErrors.Length < 1500) encErrors.AppendLine(line); }
        };
        enc.BeginErrorReadLine();
        enc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { } line) return;
            if (line.StartsWith("total_size=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(11), out var size) && size > 0) Bytes = size;
        };
        enc.BeginOutputReadLine();
        enc.EnableRaisingEvents = true;
        enc.Exited += (_, _) =>
        {
            string said; lock (encErrors) said = encErrors.ToString().Trim();
            string capSaid; lock (_capErrors) capSaid = _capErrors.ToString().Trim();
            _ = Task.Run(() => FinishAsync(capSaid.Length > 0 ? capSaid : said));
        };

        // the capture writes straight into the encoder's input (no copying through this app); its error output,
        // which reports the first picture, comes here
        var capErr = new System.IO.Pipes.AnonymousPipeServerStream(PipeDirection.In, System.IO.HandleInheritability.None);
        var encIn = (FileStream)enc.StandardInput.BaseStream;
        var cap = ChildProcess.Start(ffmpeg, capArgs, encIn.SafeFileHandle, capErr.ClientSafePipeHandle);
        capErr.DisposeLocalCopyOfClientHandle();
        // this app's end of the encoder's input closes now: the input ends when the capture ends (Stop, the window
        // closed, an error)
        try { enc.StandardInput.Close(); } catch { }
        _capture = cap;
        _ = Task.Run(() =>
        {
            try
            {
                using var reader = new StreamReader(capErr);
                while (reader.ReadLine() is { } line)
                {
                    if (line.Length == 0) continue;
                    if (Debug) AppLog.Info("Record", "capture: " + line);
                    if (line.Contains("Parsed_showinfo", StringComparison.Ordinal)) { if (line.Contains(" n:", StringComparison.Ordinal)) _firstPicture.TrySetResult(RecordingAudio.NowHns()); continue; }
                    if (!IsProblem(line)) continue;
                    lock (_capErrors) { if (_capErrors.Length < 1500) _capErrors.AppendLine(line); }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            // the capture ended without Stop: the sound ends with it
            if (!_stopping) Stop(null, byItself: true);
        });

        if (_pipe is { } pipe && _audio is { } audio)
        {
            // the sound starts once the encoder opens its end of the pipe; its time 0 is the first picture
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await pipe.WaitForConnectionAsync(cts.Token);
                    audio.Start(pipe, _firstPicture.Task, _spec.Fps);
                }
                catch (Exception ex) { AppLog.Warn("Record", "the sound didn't reach the encoder", ex); }
            });
        }
        _clock.Start();
        _watch = new Timer(_ => Watch(), null, 2000, 2000);
        AppLog.Info("Record", $"recording {_pick.Kind} “{_pick.Title}” at {Width}x{Height}, up to {_spec.Fps} fps, {_spec.Name} ({Encoder}); sound: " +
                              (_audio is null ? "off" : $"speaker {(_audio.HasSpeaker ? "on" : "off")}, mic {(_audio.HasMic ? "on (" + _audio.MicName + ")" : "off")}") + $" → {FinalPath}");
    }

    private Process? _capture, _encoder;
    /// <summary>The capture ended without Stop (the window closed, the capture failed).</summary>
    private bool _endedByItself;
    private readonly System.Text.StringBuilder _capErrors = new();

    private static Process StartFfmpeg(string ffmpeg, IEnumerable<string> args, bool stdin)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = stdin, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        ScreenTools.TieToApp(p);
        return p;
    }

    /// <summary>When the first picture was captured (100 ns QPC units): the sound's time 0.</summary>
    private readonly TaskCompletionSource<long> _firstPicture = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly bool Debug = Environment.GetEnvironmentVariable("OCEAN_REC_DEBUG") == "1";
    /// <summary>When Stop was pressed (100 ns QPC units).</summary>
    private long _stopHns;

    /// <summary>ffmpeg at info level also describes its inputs and outputs: only real problems are kept for the user and the log.</summary>
    private static bool IsProblem(string line)
        => line.Contains("rror", StringComparison.Ordinal) || line.Contains("ailed", StringComparison.Ordinal) || line.Contains("nvalid", StringComparison.Ordinal)
           || line.Contains("annot", StringComparison.Ordinal) || line.Contains("not found", StringComparison.Ordinal) || line.Contains("No such", StringComparison.Ordinal);

    private static long? FreeBytes(string folder)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace; } catch { return null; }
    }

    private void Watch()
    {
        if (_stopping) return;
        if (FreeBytes(_folder) is { } free && free < MinFreeBytes) Stop("the disk is almost full");
        else if (_pick.Kind == ScreenPick.Kinds.Window && !ScreenSources.Exists(_pick.Window)) Stop("the window was closed");
    }

    /// <summary>Ends the recording; the file is finished in the background (<see cref="Finished"/>).</summary>
    public void Stop(string? why = null) => Stop(why, byItself: false);

    private void Stop(string? why, bool byItself)
    {
        Process? cap, enc;
        lock (_lock)
        {
            if (_stopping) return;
            _stopping = true; _stopReason = why; cap = _capture; enc = _encoder; _endedByItself = byItself;
            // stopped by the user (or the disk, a closed window): the file ends at this moment
            if (!byItself) _stopHns = RecordingAudio.NowHns();
        }
        _watch?.Dispose();
        _clock.Stop();
        AppLog.Info("Record", $"stopping after {_clock.Elapsed:hh\\:mm\\:ss}{(why is null ? (byItself ? " (the capture ended)" : "") : " (" + why + ")")}");
        // the capture holds nothing back: it simply ends (its pictures so far are with the encoder)
        try { if (cap is { HasExited: false }) cap.Kill(true); } catch { }
        _ = Task.Run(async () =>
        {
            // the sound up to this moment (it runs a quarter of a second behind), then its end: the encoder then has
            // the end of both inputs and finishes the file by itself
            _audio?.Stop();
            try { _pipe?.Flush(); _pipe?.Dispose(); } catch { }
            // it normally finishes within a second; it must not hang the recording forever
            await Task.Delay(TimeSpan.FromSeconds(30));
            try { if (enc is { HasExited: false }) { AppLog.Warn("Record", "the encoder didn't finish in 30 s: ended"); enc.Kill(true); } } catch { }
        });
    }

    /// <summary>Waits until the file is finished (for quitting the app).</summary>
    public bool WaitFinished(TimeSpan timeout) => _finished.Wait(timeout);
    private readonly ManualResetEventSlim _finished = new();

    private async Task FinishAsync(string ffmpegSaid)
    {
        lock (_lock) { if (_done) return; _done = true; }
        var stoppedByItself = _endedByItself || !_stopping;
        _clock.Stop();
        _watch?.Dispose();
        _audio?.Stop();
        try { _pipe?.Dispose(); } catch { }
        var result = new RecordingResult { Duration = _clock.Elapsed, Reason = _stopReason, Problems = Problems.ToList() };
        try
        {
            if (!File.Exists(WorkingPath) || new FileInfo(WorkingPath).Length < 1024)
            {
                try { File.Delete(WorkingPath); } catch { }
                result.Error = "nothing could be recorded" + (ffmpegSaid.Length > 0 ? ": " + LastLine(ffmpegSaid) : "");
                AppLog.Warn("Record", "no recording: " + ffmpegSaid.Replace('\n', ' '));
                return;
            }
            if (stoppedByItself && result.Reason is null)
                result.Reason = _pick.Kind == ScreenPick.Kinds.Window && !ScreenSources.Exists(_pick.Window) ? "the window was closed" : "the capture stopped (" + LastLine(ffmpegSaid) + ")";
            // into MP4: nothing is encoded again, it only takes a moment. It ends where Stop was pressed (the
            // encoder went on for a moment while the last of the sound came in).
            double? length = _stopHns > 0 && _firstPicture.Task.IsCompletedSuccessfully ? (_stopHns - _firstPicture.Task.Result) / 1e7 : null;
            if (await RemuxAsync(WorkingPath, FinalPath, length))
            {
                try { File.Delete(WorkingPath); } catch { }
                result.Path = FinalPath;
            }
            else result.Path = WorkingPath; // the Matroska file plays too
            result.Bytes = new FileInfo(result.Path).Length;
            AppLog.Info("Record", $"saved {result.Path} ({Ocean.Core.Format.Bytes(result.Bytes)}, {result.Duration:hh\\:mm\\:ss}){(result.Reason is null ? "" : "; " + result.Reason)}");
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            if (File.Exists(WorkingPath)) result.Path = WorkingPath;
            AppLog.Warn("Record", "finishing the recording", ex);
        }
        finally
        {
            _finished.Set();
            Finished?.Invoke(result);
        }
    }

    private static string LastLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";

    private static async Task<bool> RemuxAsync(string from, string to, double? length)
    {
        try
        {
            var psi = new ProcessStartInfo(ScreenTools.Ffmpeg!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-i", from, "-map", "0", "-c", "copy" };
            if (length is > 0.5)
            {
                // Only changed pictures are recorded: on a still screen the last picture would end the video early
                // (without sound, the file would be shorter than the recording). The last picture lasts until Stop.
                // (Matroska counts in milliseconds; by decode time, which is right with B-pictures too: measured.)
                var endMs = ((long)Math.Round(length.Value * 1000)).ToString(CultureInfo.InvariantCulture);
                args.AddRange(new[] { "-bsf:v", $@"setts=duration='if(eq(NEXT_DTS\,NOPTS)\,max(DURATION\,{endMs}-DTS)\,DURATION)'" });
                args.AddRange(new[] { "-t", length.Value.ToString("0.000", CultureInfo.InvariantCulture) });
            }
            args.AddRange(new[] { "-movflags", "+faststart", "-y", to });
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            ScreenTools.TieToApp(p);
            var err = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await p.WaitForExitAsync(cts.Token);
            if (p.ExitCode == 0 && File.Exists(to) && new FileInfo(to).Length > 0) return true;
            AppLog.Warn("Record", "making the MP4 failed: " + (await err).Trim());
        }
        catch (Exception ex) { AppLog.Warn("Record", "making the MP4 failed", ex); }
        try { File.Delete(to); } catch { }
        return false;
    }

    public void Dispose()
    {
        Stop();
        _finished.Wait(TimeSpan.FromSeconds(25));
    }
}

/// <summary>
/// Which encoder makes a recording. Small file and Balanced: the software encoder (x264), which makes the smallest
/// files at a moderate processor load (a third to two thirds of one core at 1080p30 while things move, almost
/// nothing on a still screen). High (60 pictures a second, up to 1440p) would take one and a half cores in
/// software, too much next to a game: the graphics card's encoder (NVIDIA, Intel, AMD) when one works here, checked
/// once per run of the app with exactly these settings on a few test pictures; files are somewhat larger.
/// </summary>
public static class RecordingEncoders
{
    private static readonly string[] Hardware = { "h264_nvenc", "h264_qsv", "h264_amf" };
    private static readonly Lazy<string?> BestHardware = new(FindHardware, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string For(RecordingQuality.Spec spec) => spec.Key == RecordingQuality.High ? BestHardware.Value ?? "libx264" : "libx264";

    /// <summary>Finds a working hardware encoder ahead of time (so starting a High recording doesn't wait for it).</summary>
    public static void Warm() => _ = Task.Run(() => BestHardware.Value);

    public static IEnumerable<string> Arguments(string encoder, RecordingQuality.Spec spec)
    {
        var q = spec.Crf.ToString(CultureInfo.InvariantCulture);
        var max = spec.MaxKbps + "k"; var buf = (spec.MaxKbps * 2) + "k";
        return encoder switch
        {
            "h264_nvenc" => new[] { "-c:v", encoder, "-preset", "p5", "-rc", "vbr", "-cq", q, "-b:v", "0", "-maxrate", max, "-bufsize", buf, "-bf", "0" },
            "h264_qsv" => new[] { "-c:v", encoder, "-preset", "medium", "-global_quality", q, "-look_ahead", "0", "-maxrate", max, "-bf", "0" },
            "h264_amf" => new[] { "-c:v", encoder, "-quality", "balanced", "-rc", "qvbr", "-qvbr_quality_level", q, "-maxrate", max, "-bufsize", buf, "-bf", "0" },
            _ => new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", q, "-maxrate", max, "-bufsize", buf },
        };
    }

    private static string? FindHardware()
    {
        if (ScreenTools.Ffmpeg is not { } ffmpeg) return null;
        var spec = RecordingQuality.Of(RecordingQuality.High);
        foreach (var enc in Hardware)
        {
            try
            {
                var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=60:duration=0.5", "-vf", "format=nv12" }
                    .Concat(Arguments(enc, spec)).Concat(new[] { "-f", "null", "-" })) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                ScreenTools.TieToApp(p);
                var err = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch { } continue; }
                if (p.ExitCode == 0) { AppLog.Info("Record", $"High quality records with {enc} (the graphics card)"); return enc; }
                AppLog.Info("Record", $"{enc} isn't usable here: {err.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim()}");
            }
            catch (Exception ex) { AppLog.Info("Record", $"{enc} isn't usable here: {ex.Message}"); }
        }
        AppLog.Info("Record", "no graphics card encoder: High quality records in software");
        return null;
    }
}

public sealed class RecordingResult
{
    /// <summary>The finished file (null when nothing was recorded).</summary>
    public string? Path { get; set; }
    public long Bytes { get; set; }
    public TimeSpan Duration { get; set; }
    /// <summary>Why it stopped by itself (the window closed, the disk is full), null when the user stopped it.</summary>
    public string? Reason { get; set; }
    public string? Error { get; set; }
    public List<string> Problems { get; set; } = new();
}
