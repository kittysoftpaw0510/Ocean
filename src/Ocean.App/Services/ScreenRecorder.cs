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

    /// <summary>Videos\Ocean\Recordings in the Windows user's own Videos folder.</summary>
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Ocean", "Recordings");
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
/// One screen recording, made of one or more segments (a pause ends one, resuming starts the next). Each segment
/// captures what was picked and encodes it with its sound (<see cref="RecordingSegment"/>), as Matroska, which stays
/// playable even if the PC loses power or Ocean is ended. At the end the segments are cut at their exact ends and
/// joined (nothing is encoded again) into one MP4, index at the front. Recording stops by itself when the disk gets
/// full or a recorded window closes; quitting Ocean finishes the file first.
/// </summary>
public sealed class ScreenRecorder : IDisposable
{
    internal const long MinFreeBytes = 500L * 1024 * 1024;

    private readonly ScreenPick _pick;
    private readonly RecordingQuality.Spec _spec;
    private readonly bool _speaker, _mic;
    private readonly string _folder;
    private readonly string _base;            // the final path without its extension
    private readonly Stopwatch _clock = new(); // runs while recording (not while paused)
    private readonly object _lock = new();
    private readonly List<RecordingSegment> _segments = new();
    private RecordingSegment? _current;
    private bool _stopping, _done, _withSound;
    private string? _stopReason;
    private Timer? _watch;
    private bool _micMuted;
    private readonly List<string> _problems = new();
    private readonly ManualResetEventSlim _finished = new();

    public string FinalPath { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>The video encoder used (libx264, or the graphics card's for High).</summary>
    public string Encoder { get; }
    public ScreenPick Pick => _pick;
    /// <summary>Recorded time (pauses not counted).</summary>
    public TimeSpan Elapsed => _clock.Elapsed;
    public long Bytes { get { lock (_lock) return _segments.Sum(s => s.Bytes); } }
    public bool Paused { get; private set; }
    public bool HasSound => _withSound;
    /// <summary>The sound of the segment running now (null while paused or without sound).</summary>
    public RecordingAudio? Audio => _current?.Audio;
    public bool HasSpeaker { get; private set; }
    public bool HasMic { get; private set; }
    public IReadOnlyList<string> Problems { get { lock (_lock) return _problems.ToList(); } }
    /// <summary>The working file of the first segment (for checks).</summary>
    public string WorkingPath => _base + ".mkv";
    internal IReadOnlyList<RecordingSegment> Segments { get { lock (_lock) return _segments.ToList(); } }

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
        FinalPath = Unique(Path.Combine(_folder, $"Screen recording {stamp}.mp4"));
        _base = Path.Combine(Path.GetDirectoryName(FinalPath)!, Path.GetFileNameWithoutExtension(FinalPath));
        _speaker = settings.Speaker; _mic = settings.Mic;
        Encoder = RecordingEncoders.For(_spec);
    }

    private static string Unique(string path)
    {
        bool Taken(string p) => File.Exists(p) || File.Exists(Path.ChangeExtension(p, ".mkv"));
        if (!Taken(path)) return path;
        for (var i = 2; ; i++)
        {
            var p = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)} ({i}){Path.GetExtension(path)}");
            if (!Taken(p)) return p;
        }
    }

    /// <summary>Starts recording. Throws when it can't (no ffmpeg, the disk is full).</summary>
    public void Start()
    {
        if (ScreenTools.Ffmpeg is null) throw new InvalidOperationException("ffmpeg.exe is missing next to Ocean (install Ocean again).");
        CheckSpace();
        var audio = _speaker || _mic ? new RecordingAudio(_speaker, _mic) : null;
        // the file has a sound track if any sound could be opened now; later segments keep it (silent if a device is gone)
        _withSound = audio is { HasSpeaker: true } or { HasMic: true };
        HasSpeaker = audio?.HasSpeaker == true; HasMic = audio?.HasMic == true;
        if (audio is not null) lock (_lock) _problems.AddRange(audio.Problems);
        if (!_withSound) { audio?.Dispose(); audio = null; }
        StartSegment(audio);
        _clock.Start();
        _watch = new Timer(_ => Watch(), null, 2000, 2000);
        AppLog.Info("Record", $"recording {_pick.Kind} “{_pick.Title}” at {Width}x{Height}, up to {_spec.Fps} fps, {_spec.Name} ({Encoder}); sound: " +
                              (!_withSound ? "off" : $"speaker {(HasSpeaker ? "on" : "off")}, mic {(HasMic ? "on (" + audio!.MicName + ")" : "off")}") + $" → {FinalPath}");
    }

    private void CheckSpace()
    {
        if (FreeBytes(_folder) is { } free && free < MinFreeBytes) throw new InvalidOperationException($"There is too little free space on the disk of {_folder}.");
    }

    private void StartSegment(RecordingAudio? audio)
    {
        RecordingSegment seg;
        lock (_lock)
        {
            var n = _segments.Count;
            var path = n == 0 ? _base + ".mkv" : $"{_base}.part{n + 1}.mkv";
            seg = new RecordingSegment(_pick, _spec, Encoder, Width, Height, path, audio, _withSound);
            _segments.Add(seg);
            _current = seg;
        }
        if (seg.Audio is { } a) a.MicMuted = _micMuted;
        seg.EndedByItself += () => OnSegmentEnded(seg);
        try { seg.Start(); }
        catch { lock (_lock) { _segments.Remove(seg); _current = null; } throw; }
    }

    /// <summary>A segment's capture ended without Pause or Stop (the window closed, the capture failed): the recording ends.</summary>
    private void OnSegmentEnded(RecordingSegment seg)
    {
        if (!ReferenceEquals(_current, seg) || _stopping) return;
        var why = _pick.Kind == ScreenPick.Kinds.Window && !ScreenSources.Exists(_pick.Window) ? "the window was closed" : "the capture stopped" + (seg.Problem is { } p ? " (" + p + ")" : "");
        Stop(why, segmentAlreadyEnded: true);
    }

    public bool MicMuted
    {
        get => _micMuted;
        set { _micMuted = value; if (_current?.Audio is { } a) a.MicMuted = value; }
    }

    /// <summary>Pauses: the running segment ends here (its file is finished in the background); nothing is captured until Resume.</summary>
    public void Pause()
    {
        RecordingSegment? seg;
        lock (_lock) { if (_stopping || Paused) return; Paused = true; seg = _current; _current = null; }
        _clock.Stop();
        seg?.Stop();
        AppLog.Info("Record", $"paused after {_clock.Elapsed:hh\\:mm\\:ss}");
    }

    /// <summary>Goes on recording: a new segment starts (picture and sound as before). Throws when it can't.</summary>
    public void Resume()
    {
        lock (_lock) { if (_stopping || !Paused) return; }
        CheckSpace();
        if (_pick.Kind == ScreenPick.Kinds.Window && !ScreenSources.Exists(_pick.Window)) throw new InvalidOperationException("The recorded window was closed.");
        RecordingAudio? audio = null;
        if (_withSound)
        {
            audio = new RecordingAudio(_speaker && HasSpeaker, _mic && HasMic);
            foreach (var p in audio.Problems) AppLog.Warn("Record", "resuming: " + p);
        }
        StartSegment(audio);
        lock (_lock) Paused = false;
        _clock.Start();
        AppLog.Info("Record", $"resumed at {_clock.Elapsed:hh\\:mm\\:ss} (segment {_segments.Count})");
    }

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
    public void Stop(string? why = null) => Stop(why, segmentAlreadyEnded: false);

    private void Stop(string? why, bool segmentAlreadyEnded)
    {
        RecordingSegment? seg;
        lock (_lock) { if (_stopping) return; _stopping = true; _stopReason = why; seg = _current; _current = null; }
        _watch?.Dispose();
        _clock.Stop();
        AppLog.Info("Record", $"stopping after {_clock.Elapsed:hh\\:mm\\:ss}{(why is null ? "" : " (" + why + ")")}{(Paused ? " (paused)" : "")}");
        if (!segmentAlreadyEnded) seg?.Stop();
        _ = Task.Run(FinishAsync);
    }

    /// <summary>Waits until the file is finished (for quitting the app).</summary>
    public bool WaitFinished(TimeSpan timeout) => _finished.Wait(timeout);

    private async Task FinishAsync()
    {
        lock (_lock) { if (_done) return; _done = true; }
        var result = new RecordingResult { Duration = _clock.Elapsed, Reason = _stopReason, Problems = Problems.ToList() };
        List<RecordingSegment> segments;
        lock (_lock) segments = _segments.ToList();
        try
        {
            foreach (var s in segments) await s.Done;
            var good = segments.Where(s => s.HasFile).ToList();
            if (good.Count == 0)
            {
                foreach (var s in segments) s.DeleteFile();
                var said = segments.Select(s => s.Problem).FirstOrDefault(p => p is not null);
                result.Error = "nothing could be recorded" + (said is null ? "" : ": " + said);
                AppLog.Warn("Record", "no recording: " + said);
                return;
            }
            // into one MP4: nothing is encoded again; each segment ends where it was stopped or paused
            if (await JoinAsync(good, FinalPath))
            {
                foreach (var s in segments) s.DeleteFile();
                result.Path = FinalPath;
            }
            else
            {
                // the segments play on their own too: they stay
                result.Path = good[0].Path;
                if (good.Count > 1) result.Error = $"the {good.Count} parts couldn't be joined; they are kept next to each other";
            }
            result.Bytes = new FileInfo(result.Path).Length;
            AppLog.Info("Record", $"saved {result.Path} ({Ocean.Core.Format.Bytes(result.Bytes)}, {result.Duration:hh\\:mm\\:ss}, {good.Count} part{(good.Count == 1 ? "" : "s")}){(result.Reason is null ? "" : "; " + result.Reason)}");
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            if (segments.FirstOrDefault(s => s.HasFile) is { } first) result.Path = first.Path;
            AppLog.Warn("Record", "finishing the recording", ex);
        }
        finally
        {
            _finished.Set();
            Finished?.Invoke(result);
        }
    }

    /// <summary>
    /// The segments into one MP4. Each is first cut at its end (Stop or Pause) with its last picture lasting until
    /// then (only changed pictures are recorded: on a still screen the last picture would end a segment early), then
    /// they are put one after the other. One segment: in one step.
    /// </summary>
    private static async Task<bool> JoinAsync(List<RecordingSegment> segments, string to)
    {
        var temp = new List<string>();
        try
        {
            if (segments.Count == 1) return await RunAsync(CutArgs(segments[0], segments[0].Path, to, mp4: true), to);
            var list = new System.Text.StringBuilder();
            foreach (var s in segments)
            {
                var cut = Path.ChangeExtension(s.Path, ".cut.mkv");
                temp.Add(cut);
                if (!await RunAsync(CutArgs(s, s.Path, cut, mp4: false), cut)) return false;
                list.Append("file '").Append(cut.Replace("'", "'\\''")).Append("'\n");
            }
            var listPath = Path.ChangeExtension(segments[0].Path, ".parts.txt");
            temp.Add(listPath);
            await File.WriteAllTextAsync(listPath, list.ToString());
            return await RunAsync(new List<string> { "-hide_banner", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", listPath, "-map", "0", "-c", "copy", "-movflags", "+faststart", "-y", to }, to);
        }
        finally { foreach (var t in temp) { try { File.Delete(t); } catch { } } }
    }

    private static List<string> CutArgs(RecordingSegment s, string from, string to, bool mp4)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-i", from, "-map", "0", "-c", "copy" };
        if (s.Length is > 0.3 and var length)
        {
            // Matroska counts in milliseconds; by decode time, which is right with B-pictures too (measured)
            var endMs = ((long)Math.Round(length * 1000)).ToString(CultureInfo.InvariantCulture);
            args.AddRange(new[] { "-bsf:v", $@"setts=duration='if(eq(NEXT_DTS\,NOPTS)\,max(DURATION\,{endMs}-DTS)\,DURATION)'" });
            args.AddRange(new[] { "-t", length.ToString("0.000", CultureInfo.InvariantCulture) });
        }
        if (mp4) args.AddRange(new[] { "-movflags", "+faststart" });
        else args.AddRange(new[] { "-f", "matroska" });
        args.AddRange(new[] { "-y", to });
        return args;
    }

    private static async Task<bool> RunAsync(List<string> args, string to)
    {
        try
        {
            var psi = new ProcessStartInfo(ScreenTools.Ffmpeg!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            ScreenTools.TieToApp(p);
            var err = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await p.WaitForExitAsync(cts.Token);
            if (p.ExitCode == 0 && File.Exists(to) && new FileInfo(to).Length > 0) return true;
            AppLog.Warn("Record", "making the file failed: " + (await err).Trim());
        }
        catch (Exception ex) { AppLog.Warn("Record", "making the file failed", ex); }
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
/// One stretch of a recording, from Start or Resume to Pause or Stop. Two ffmpeg processes: the capture (it waits
/// for the screen to change, also before it can stop, so it keeps nothing back) writes timestamped raw pictures
/// straight into the encoder's input; the encoder writes the Matroska file with the sound. Stopping ends the
/// capture at once; the encoder sees the end of its input, writes what it still holds and closes the file.
/// </summary>
internal sealed class RecordingSegment
{
    private readonly ScreenPick _pick;
    private readonly RecordingQuality.Spec _spec;
    private readonly string _encoderName;
    private readonly int _width, _height;
    private readonly bool _withSound;
    private NamedPipeServerStream? _pipe;
    private Process? _capture, _encoder;
    private readonly System.Text.StringBuilder _capErrors = new(), _encErrors = new();
    private readonly TaskCompletionSource<long> _firstPicture = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _stopHns;
    private volatile bool _stopping;
    private static readonly bool Debug = Environment.GetEnvironmentVariable("OCEAN_REC_DEBUG") == "1";

    public string Path { get; }
    public RecordingAudio? Audio { get; }
    public long Bytes { get; private set; }
    /// <summary>Finished: the encoder ended and its file is complete (or there is none).</summary>
    public Task Done => _done.Task;
    public bool HasFile => File.Exists(Path) && new FileInfo(Path).Length >= 1024;
    /// <summary>Seconds from the first picture to Stop/Pause (null: it ended by itself, or no picture came).</summary>
    public double? Length => _stopHns > 0 && _firstPicture.Task.IsCompletedSuccessfully ? (_stopHns - _firstPicture.Task.Result) / 1e7 : null;
    public string? Problem { get { var c = _capErrors.ToString().Trim(); var e = _encErrors.ToString().Trim(); var t = c.Length > 0 ? c : e; return t.Length == 0 ? null : t.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Trim(); } }
    /// <summary>The capture ended without Stop.</summary>
    public event Action? EndedByItself;

    public RecordingSegment(ScreenPick pick, RecordingQuality.Spec spec, string encoder, int width, int height, string path, RecordingAudio? audio, bool withSound)
    {
        _pick = pick; _spec = spec; _encoderName = encoder; _width = width; _height = height; Path = path; Audio = audio; _withSound = withSound;
    }

    private string PixelFormat => _encoderName == "libx264" ? "yuv420p" : "nv12";
    /// <summary>
    /// Screen colours to video colours, on the processor (this ffmpeg can't do it on the graphics card here). At 60
    /// pictures a second the fast way: measured 12 instead of 14 ms of processor time per full-HD picture.
    /// </summary>
    private string Convert => (_spec.Fps >= 60 ? "scale=flags=fast_bilinear," : "") + "format=" + PixelFormat;

    public void Start()
    {
        var ffmpeg = ScreenTools.Ffmpeg!;
        var inv = CultureInfo.InvariantCulture;
        var src = new List<string>
        {
            _pick.Kind == ScreenPick.Kinds.Window ? "hwnd=" + ((ulong)_pick.Window.ToInt64()).ToString(inv) : "hmonitor=" + ((ulong)_pick.Monitor.ToInt64()).ToString(inv),
            "max_framerate=" + _spec.Fps, "capture_cursor=1", "display_border=0",
            "width=" + _width, "height=" + _height, "resize_mode=scale_aspect", "scale_mode=bicubic",
        };
        if (_pick.Kind == ScreenPick.Kinds.Region)
            src.AddRange(new[] { "crop_left=" + _pick.CropLeft, "crop_top=" + _pick.CropTop, "crop_right=" + _pick.CropRight, "crop_bottom=" + _pick.CropBottom });
        // info level for the first picture's report (only "Parsed_showinfo" lines and real problems are looked at):
        // the moment it is captured is time 0 for the sound and for where the segment ends
        var capArgs = new List<string>
        {
            "-hide_banner", "-loglevel", "info", "-nostats", "-nostdin", "-f", "lavfi", "-i", "gfxcapture=" + string.Join(':', src),
            "-filter_complex", "[0:v]hwdownload,format=bgra,split=2[v][m];[m]select='eq(n\\,0)',showinfo,nullsink;[v]" + Convert + "[out]",
            "-map", "[out]", "-c:v", "rawvideo", "-f", "nut", "pipe:1",
        };

        string? pipeName = null;
        if (_withSound)
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
        encArgs.AddRange(RecordingEncoders.Arguments(_encoderName, _spec));
        encArgs.AddRange(new[] { "-force_key_frames", "expr:gte(t,n_forced*10)", "-fps_mode", "vfr" });
        if (pipeName is not null) encArgs.AddRange(new[] { "-c:a", "aac", "-b:a", _spec.AudioKbps + "k" });
        // A still screen gives no pictures for a while; the sound must not wait for them (by default ffmpeg holds
        // sound back up to 10 s to interleave it with pictures, and the sound input stalls meanwhile). The sound
        // comes a quarter of a second behind the pictures (RecordingAudio's margin): 1 s leaves room for that.
        encArgs.AddRange(new[] { "-max_interleave_delta", "1000000", "-f", "matroska", "-y", Path });

        // the encoder first (it waits for its inputs), then the capture
        var encPsi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in encArgs) encPsi.ArgumentList.Add(a);
        var enc = Process.Start(encPsi) ?? throw new InvalidOperationException("ffmpeg did not start");
        ScreenTools.TieToApp(enc);
        _encoder = enc;
        enc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 } line) return;
            if (Debug) AppLog.Info("Record", "encoder: " + line);
            lock (_encErrors) { if (_encErrors.Length < 1500) _encErrors.AppendLine(line); }
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
            // the sound ends with it in any case
            Audio?.Stop();
            try { _pipe?.Dispose(); } catch { }
            if (File.Exists(Path)) Bytes = new FileInfo(Path).Length;
            _done.TrySetResult();
        };

        // the capture writes straight into the encoder's input (no copying through this app); its error output,
        // which reports the first picture, comes here
        var capErr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        var encIn = (FileStream)enc.StandardInput.BaseStream;
        Process cap;
        try { cap = ChildProcess.Start(ffmpeg, capArgs, encIn.SafeFileHandle, capErr.ClientSafePipeHandle); }
        catch { try { enc.Kill(true); } catch { } throw; }
        capErr.DisposeLocalCopyOfClientHandle();
        // this app's end of the encoder's input closes now: the input ends when the capture ends
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
            if (!_stopping) { EndSound(); EndedByItself?.Invoke(); }
        });

        if (_pipe is { } pipe)
        {
            // the sound starts once the encoder opens its end of the pipe; its time 0 is the first picture. A segment
            // whose sound devices are all gone still gets its (silent) sound track.
            var audio = Audio ?? new RecordingAudio(false, false);
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
            if (Audio is null) _silence = audio;
        }
    }

    private RecordingAudio? _silence;

    /// <summary>ffmpeg at info level also describes its inputs and outputs: only real problems are kept.</summary>
    private static bool IsProblem(string line)
        => line.Contains("rror", StringComparison.Ordinal) || line.Contains("ailed", StringComparison.Ordinal) || line.Contains("nvalid", StringComparison.Ordinal)
           || line.Contains("annot", StringComparison.Ordinal) || line.Contains("not found", StringComparison.Ordinal) || line.Contains("No such", StringComparison.Ordinal);

    /// <summary>Ends this segment at this moment; the file is finished in the background (<see cref="Done"/>).</summary>
    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        _stopHns = RecordingAudio.NowHns();
        // the capture holds nothing back: it simply ends (its pictures so far are with the encoder)
        try { if (_capture is { HasExited: false } c) c.Kill(true); } catch { }
        var enc = _encoder;
        _ = Task.Run(async () =>
        {
            EndSound();
            // it normally finishes within a second; it must not hang the recording forever
            await Task.Delay(TimeSpan.FromSeconds(30));
            try { if (enc is { HasExited: false }) { AppLog.Warn("Record", "the encoder didn't finish in 30 s: ended"); enc.Kill(true); } } catch { }
        });
    }

    /// <summary>The sound up to this moment (it runs a quarter of a second behind), then its end: with the end of both inputs the encoder finishes by itself.</summary>
    private void EndSound()
    {
        (Audio ?? _silence)?.Stop();
        try { _pipe?.Flush(); _pipe?.Dispose(); } catch { }
    }

    public void DeleteFile() { try { File.Delete(Path); } catch { } }
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
