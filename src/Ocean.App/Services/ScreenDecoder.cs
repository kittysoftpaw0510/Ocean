using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Ocean.App.Services;

/// <summary>
/// Shows a shared screen: ffmpeg reads the stream the service puts together (<c>/api/screen/view/stream</c>),
/// decodes it and hands raw pictures over a pipe; the newest one is drawn into <see cref="Bitmap"/>. Pictures are
/// shown as they come, never queued, so the delay stays as small as it can be; when the UI is busy, pictures in
/// between are skipped. If the decoder stops (the stream broke), it starts again at the next key picture.
/// </summary>
public sealed class ScreenDecoder : IDisposable
{
    private readonly string _url;
    private readonly int _width, _height, _fps;
    private readonly Dispatcher _ui;
    private readonly object _lock = new();
    private byte[] _latest;
    private bool _fresh, _renderQueued, _disposed;
    private Process? _process;

    public WriteableBitmap Bitmap { get; }
    public long Frames { get; private set; }
    public DateTime LastFrameAt { get; private set; }
    /// <summary>What ffmpeg said the last time it ended without a picture (for the log and for checks).</summary>
    public string? LastError { get; private set; }
    /// <summary>A picture was drawn (UI thread).</summary>
    public event Action? FrameShown;
    /// <summary>For measurements: every decoded picture, on the reader thread, before it is drawn.</summary>
    public Action<byte[], int, int>? OnRawFrame { get; set; }

    /// <summary>Create on the UI thread.</summary>
    public ScreenDecoder(ApiClient api, int width, int height, int fps)
    {
        _fps = Math.Clamp(fps, 1, 60);
        _url = $"{api.BaseUrl}/api/screen/view/stream?k={api.Token}";
        _width = width; _height = height;
        _ui = Dispatcher.CurrentDispatcher;
        _latest = new byte[width * height * 4];
        Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
    }

    public void Start() => new Thread(Run) { IsBackground = true, Name = "ocean-screen-decode" }.Start();

    private void Run()
    {
        var frame = new byte[_width * _height * 4];
        var failures = 0;
        while (!_disposed)
        {
            if (ScreenTools.Ffmpeg is not { } ffmpeg) return;
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[]
            {
                "-hide_banner", "-loglevel", "error",
                // nothing held back: no pictures kept for reordering, and no analysing of the stream before the first
                // picture. (Not "-fflags nobuffer": with it ffmpeg shows nothing until the second key picture, 4 s later.)
                "-flags", "low_delay", "-probesize", "32", "-analyzeduration", "0",
                "-threads", "2", "-thread_type", "slice",
                "-f", "h264", "-framerate", _fps.ToString(), "-i", _url,
                "-vf", $"scale={_width}:{_height}:flags=fast_bilinear", "-pix_fmt", "bgra", "-f", "rawvideo", "-fps_mode", "passthrough", "pipe:1",
            }) psi.ArgumentList.Add(a);
            var got = 0L;
            try
            {
                using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
                ScreenTools.TieToApp(p);
                lock (_lock) { if (_disposed) { try { p.Kill(true); } catch { } return; } _process = p; }
                var errors = new System.Text.StringBuilder();
                p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) lock (errors) { if (errors.Length < 600) errors.AppendLine(line); } };
                p.BeginErrorReadLine();
                var stream = p.StandardOutput.BaseStream;
                while (!_disposed)
                {
                    var read = 0;
                    while (read < frame.Length)
                    {
                        var n = stream.Read(frame, read, frame.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < frame.Length) break; // the stream ended
                    got++;
                    OnRawFrame?.Invoke(frame, _width, _height);
                    lock (_lock)
                    {
                        (frame, _latest) = (_latest, frame);
                        _fresh = true;
                        if (!_renderQueued) { _renderQueued = true; _ui.BeginInvoke(DispatcherPriority.Render, Render); }
                    }
                }
                try { if (!p.HasExited) p.Kill(true); } catch { }
                if (got == 0 && !_disposed)
                {
                    try { p.WaitForExit(500); } catch { }
                    string said;
                    lock (errors) said = string.Join(" ", errors.ToString().Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries));
                    said = said.Replace(_url, "(stream)");
                    if (said.Length > 0 && said != LastError) { LastError = said; AppLog.Warn("Screen", "the decoder showed no picture: " + said); }
                }
            }
            catch (Exception ex) { if (!_disposed) AppLog.Warn("Screen", "the decoder stopped", ex); }
            finally { lock (_lock) _process = null; }
            if (_disposed) return;
            // the stream ended or broke: again at the next key picture (the window is closed by its owner when the sharing is over)
            failures = got > 0 ? 0 : failures + 1;
            Thread.Sleep(failures > 5 ? 2000 : 400);
        }
    }

    private void Render()
    {
        if (_disposed) return;
        lock (_lock)
        {
            _renderQueued = false;
            if (!_fresh) return;
            _fresh = false;
            Bitmap.WritePixels(new Int32Rect(0, 0, _width, _height), _latest, _width * 4, 0);
        }
        Frames++;
        LastFrameAt = DateTime.UtcNow;
        FrameShown?.Invoke();
    }

    public void Dispose()
    {
        Process? p;
        lock (_lock) { _disposed = true; p = _process; _process = null; }
        try { if (p is { HasExited: false }) p.Kill(true); } catch { }
    }
}
