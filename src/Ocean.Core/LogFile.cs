using System.Text;
using System.Text.RegularExpressions;

namespace Ocean.Core;

/// <summary>
/// One of Ocean's daily log files (<c>&lt;prefix&gt;-YYYYMMDD.log</c>), kept small however long the PC runs and
/// whatever goes wrong:
/// <list type="bullet">
/// <item>the same line over and over (the service is down, a VPN keeps changing the network) is written at most once a
/// minute, followed later by how many times it was repeated;</item>
/// <item>a day's file stops at <see cref="DayCap"/> (errors still get a little more room);</item>
/// <item>files older than <see cref="KeepDays"/> days go, and the folder as a whole stays under <see cref="FolderCap"/>.</item>
/// </list>
/// Thread-safe; logging never throws.
/// </summary>
public sealed class LogFile
{
    public const long DayCap = 8L << 20;
    public const int KeepDays = 10;
    public const long FolderCap = 100L << 20;
    private const long ErrorRoom = 2L << 20;
    private const int MaxTracked = 1000;
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(1);
    private static readonly Regex Numbers = new(@"\d+", RegexOptions.Compiled);

    private readonly string _prefix;
    private readonly object _lock = new();
    private readonly Dictionary<string, Repeat> _recent = new();
    private DateTime _day;
    private string _path = "";
    private long _size;
    private bool _full;
    private DateTime _nextSummary;

    private sealed class Repeat
    {
        public DateTime Last;
        public int Skipped;
        public string Sample = "";
    }

    public LogFile(string dir, string prefix)
    {
        Directory = dir;
        _prefix = prefix;
    }

    public string Directory { get; }

    /// <summary>Appends "HH:mm:ss.fff LVL Area: message" plus <paramref name="extra"/> (exception details).</summary>
    public void Write(string level, string area, string message, string? extra = null)
    {
        var now = DateTime.Now;
        lock (_lock)
        {
            try
            {
                if (now.Date != _day) OpenDay(now);
                var sb = new StringBuilder();
                SummarizeRepeats(now, sb, all: false);

                // Lines that differ only in numbers (times, ports, counts) count as the same line.
                var key = level + " " + area + ": " + Numbers.Replace(message, "#");
                if (_recent.TryGetValue(key, out var r) && now - r.Last < RepeatWindow)
                {
                    r.Skipped++;
                    Flush(sb, level);
                    return;
                }
                if (r is null)
                {
                    if (_recent.Count >= MaxTracked) { SummarizeRepeats(now, sb, all: true); _recent.Clear(); }
                    _recent[key] = r = new Repeat();
                }
                else if (r.Skipped > 0) Summary(sb, now, r);
                r.Last = now; r.Skipped = 0; r.Sample = $"{area}: {Shorten(message)}";

                sb.Append(now.ToString("HH:mm:ss.fff")).Append(' ').Append(level).Append(' ')
                  .Append(area).Append(": ").Append(message).Append(extra).Append("\r\n");
                Flush(sb, level);
            }
            catch { /* logging must never break Ocean */ }
        }
    }

    private void Flush(StringBuilder sb, string level)
    {
        if (sb.Length == 0) return;
        var important = level is "ERR" or "CRT";
        var bytes = Encoding.UTF8.GetByteCount(sb.ToString());
        if (_full && !(important && _size + bytes <= DayCap + ErrorRoom)) return;
        if (!_full && _size + bytes > DayCap)
        {
            _full = true;
            sb.Insert(0, $"{DateTime.Now:HH:mm:ss.fff} WRN Log: today's log reached {DayCap >> 20} MB; until midnight only errors are written\r\n");
            if (!important) sb.Length = sb.ToString().IndexOf('\n') + 1; // keep just that notice
            bytes = Encoding.UTF8.GetByteCount(sb.ToString());
        }
        File.AppendAllText(_path, sb.ToString());
        _size += bytes;
    }

    /// <summary>"... repeated N more times" for lines that went quiet (all: every line still counting).</summary>
    private void SummarizeRepeats(DateTime now, StringBuilder sb, bool all)
    {
        if (!all && now < _nextSummary) return;
        _nextSummary = now.AddSeconds(15);
        foreach (var r in _recent.Values)
            if (r.Skipped > 0 && (all || now - r.Last >= RepeatWindow))
            {
                Summary(sb, now, r);
                r.Skipped = 0;
            }
    }

    private static void Summary(StringBuilder sb, DateTime now, Repeat r)
        => sb.Append(now.ToString("HH:mm:ss.fff")).Append(" INF Log: the line \"").Append(r.Sample)
             .Append("\" repeated ").Append(r.Skipped).Append(r.Skipped == 1 ? " more time" : " more times")
             .Append(" (written once a minute at most)\r\n");

    private static string Shorten(string s)
    {
        var line = s.Split('\n')[0].TrimEnd('\r');
        return line.Length <= 160 ? line : line[..160] + "…";
    }

    private void OpenDay(DateTime now)
    {
        _day = now.Date;
        System.IO.Directory.CreateDirectory(Directory);
        _path = Path.Combine(Directory, $"{_prefix}-{now:yyyyMMdd}.log");
        try { _size = File.Exists(_path) ? new FileInfo(_path).Length : 0; } catch { _size = 0; }
        _full = _size >= DayCap;
        Prune();
    }

    /// <summary>Deletes old logs, then the oldest ones while the folder is over its cap (runs once a day).</summary>
    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(Directory).GetFiles("*.log").OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (string.Equals(f.FullName, Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase)) continue;
                if (f.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-KeepDays) && total <= FolderCap) break;
                try { f.Delete(); total -= f.Length; } catch { /* the other process's file, or in use */ }
            }
        }
        catch { }
    }
}
