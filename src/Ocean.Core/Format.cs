namespace Ocean.Core;

public static class Format
{
    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        var i = -1;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return v >= 100 ? $"{v:0} {units[i]}" : $"{v:0.#} {units[i]}";
    }

    public static string Duration(long? ms)
    {
        if (ms is null or <= 0) return "";
        var t = TimeSpan.FromMilliseconds(ms.Value);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Duration(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    public static string Speed(double bps) => bps <= 0 ? "" : Bytes((long)bps) + "/s";

    /// <summary>"in 9 min", "in 1.5 h", "now" (for something that starts later).</summary>
    public static string Until(DateTimeOffset t)
    {
        var left = t - DateTimeOffset.UtcNow;
        if (left.TotalSeconds < 30) return "any moment now";
        if (left.TotalMinutes < 60) return $"in {Math.Ceiling(left.TotalMinutes):0} min";
        if (left.TotalHours < 24) return $"in {left.TotalHours:0.#} h";
        return $"in {left.TotalDays:0} days";
    }

    /// <summary>"18:30", "tomorrow 18:30", "Fri 7 Nov 18:30" (local time).</summary>
    public static string ClockTime(DateTimeOffset t)
    {
        var local = t.ToLocalTime();
        var day = local.Date == DateTime.Today ? "" : local.Date == DateTime.Today.AddDays(1) ? "tomorrow " : local.ToString("ddd d MMM ");
        return day + local.ToString("HH:mm");
    }

    public static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} d ago";
        return t.LocalDateTime.ToString("yyyy-MM-dd");
    }

    public static string Resolution(int? w, int? h)
    {
        if (w is null || h is null) return "";
        return h switch
        {
            >= 2100 => "4K",
            >= 1400 => "1440p",
            >= 1000 => "1080p",
            >= 700 => "720p",
            >= 400 => "480p",
            _ => $"{w}×{h}",
        };
    }
}
