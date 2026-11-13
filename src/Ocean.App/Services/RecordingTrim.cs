using System.Diagnostics;
using System.Globalization;

namespace Ocean.App.Services;

/// <summary>
/// Cuts a recording to a start and an end. Only cutting the end: instant and lossless (the pictures are copied as
/// they are, it ends at the exact moment). A new start: the pictures there depend on earlier ones (a key picture
/// only every 10 s), so that copy is encoded again, with the recording's own settings (x264, quality 23: a little
/// above Balanced, so a second generation stays as sharp); it reports its progress.
/// </summary>
public static class RecordingTrim
{
    public sealed record Result(bool Ok, string? Path, string? Error);

    /// <param name="progress">0..1 while it runs (encoding only; cutting the end is too quick to need it).</param>
    public static async Task<Result> TrimAsync(string source, double start, double end, double duration, Action<double>? progress, CancellationToken ct)
    {
        if (ScreenTools.Ffmpeg is not { } ffmpeg) return new(false, null, "ffmpeg.exe is missing next to Ocean.");
        if (end - start < 0.5) return new(false, null, "That is less than half a second.");
        var inv = CultureInfo.InvariantCulture;
        var target = Unique(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, System.IO.Path.GetFileNameWithoutExtension(source) + " (trimmed).mp4"));
        var onlyEnd = start < 0.05;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1" };
        if (!onlyEnd) args.AddRange(new[] { "-ss", start.ToString("0.000", inv) });
        args.AddRange(new[] { "-i", source, "-t", (end - start).ToString("0.000", inv), "-map", "0" });
        if (onlyEnd)
        {
            args.AddRange(new[] { "-c", "copy" });
            // Cut in decode order, not by time: a B-picture comes after the later picture it is built on, so a cut by
            // time could keep it and drop what it needs. Everything from the first picture past the end on goes.
            if (await FirstPastAsync(ffmpeg, source, end, ct) is { } n)
                args.AddRange(new[] { "-bsf:v", $@"noise=drop=gte(n\,{n.ToString(inv)})" });
        }
        else args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-force_key_frames", "expr:gte(t,n_forced*10)", "-fps_mode", "vfr", "-c:a", "aac", "-b:a", "128k" });
        args.AddRange(new[] { "-movflags", "+faststart", "-y", target });

        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            ScreenTools.TieToApp(p);
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            var err = p.StandardError.ReadToEndAsync(ct);
            var length = end - start;
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12), out var us) && length > 0)
                    progress?.Invoke(Math.Clamp(us / 1e6 / length, 0, 1));
            await p.WaitForExitAsync(ct);
            if (p.ExitCode == 0 && File.Exists(target) && new FileInfo(target).Length > 0) { progress?.Invoke(1); return new(true, target, null); }
            var said = (await err).Trim();
            AppLog.Warn("Record", "trimming failed: " + said);
            try { File.Delete(target); } catch { }
            return new(false, null, said.Length == 0 ? "ffmpeg didn't finish." : said.Split('\n').Last().Trim());
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(target); } catch { }
            return new(false, null, null);
        }
    }

    /// <summary>The number (in decode order) of the first picture shown after <paramref name="end"/> seconds; null: none.</summary>
    private static async Task<long?> FirstPastAsync(string ffmpeg, string source, double end, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-i", source, "-map", "0:v:0", "-c", "copy", "-f", "framemd5", "-" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            ScreenTools.TieToApp(p);
            _ = p.StandardError.ReadToEndAsync(ct);
            double tb = 0; long n = 0; long? found = null;
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith("#tb 0:", StringComparison.Ordinal))
                {
                    var f = line[6..].Trim().Split('/');
                    if (f.Length == 2 && double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num) && double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) && den > 0) tb = num / den;
                    continue;
                }
                if (line.StartsWith('#') || line.Length == 0) continue;
                var cols = line.Split(',');
                if (found is null && cols.Length > 2 && tb > 0 && long.TryParse(cols[2].Trim(), out var pts) && pts * tb > end + 0.0005) found = n;
                n++;
            }
            await p.WaitForExitAsync(ct);
            return found;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { AppLog.Warn("Record", "reading the pictures for trimming", ex); return null; }
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        for (var i = 2; ; i++)
        {
            var p = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $"{System.IO.Path.GetFileNameWithoutExtension(path)} {i}{System.IO.Path.GetExtension(path)}");
            if (!File.Exists(p)) return p;
        }
    }
}
