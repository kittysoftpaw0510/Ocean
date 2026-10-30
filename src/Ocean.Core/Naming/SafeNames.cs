using System.Text;

namespace Ocean.Core.Naming;

/// <summary>
/// Makes arbitrary (remote, possibly hostile or just weird) names safe to use on the local file system:
/// invalid characters, reserved device names, trailing dots/spaces, over-long segments and over-long paths.
/// </summary>
public static class SafeNames
{
    private static readonly HashSet<char> Invalid = new(Path.GetInvalidFileNameChars());
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON","PRN","AUX","NUL",
        "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9",
    };

    public const int MaxSegmentChars = 200;

    public static string FileName(string? name, string fallback = "video")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (Invalid.Contains(ch) || char.IsControl(ch)) sb.Append('_');
            else sb.Append(ch);
        }
        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length == 0) return fallback;

        var stem = Path.GetFileNameWithoutExtension(result);
        var ext = Path.GetExtension(result);
        if (Reserved.Contains(stem)) result = "_" + result;

        if (result.Length > MaxSegmentChars)
        {
            ext = ext.Length > 16 ? "" : ext;
            var keep = Math.Max(1, MaxSegmentChars - ext.Length);
            result = TruncateAtCharBoundary(stem, keep) + ext;
        }
        return result;
    }

    /// <summary>Converts a forward-slash relative path into a safe local relative path.</summary>
    public static string RelPathToLocal(string relPath)
    {
        var parts = relPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safe = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            if (p == "." || p == "..") continue;
            safe.Add(FileName(p));
        }
        if (safe.Count == 0) return "video";
        return string.Join('\\', safe);
    }

    /// <summary>Prefixes with \\?\ so Win32 APIs accept paths longer than MAX_PATH.</summary>
    public static string LongPath(string fullPath)
    {
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return fullPath;
        if (fullPath.Length < 240) return fullPath;
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + fullPath[2..];
        return @"\\?\" + fullPath;
    }

    /// <summary>Returns a path that does not exist yet by appending " (2)", " (3)"...</summary>
    public static string Unique(string fullPath)
    {
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath)) return fullPath;
        var dir = Path.GetDirectoryName(fullPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        var ext = Path.GetExtension(fullPath);
        for (var i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    private static string TruncateAtCharBoundary(string s, int max)
    {
        if (s.Length <= max) return s;
        if (max > 0 && char.IsHighSurrogate(s[max - 1])) max--;
        return s[..max];
    }

    /// <summary>Human-friendly display of a possibly very long name (middle ellipsis).</summary>
    public static string Ellipsize(string s, int max = 60)
    {
        if (s.Length <= max) return s;
        var head = max / 2 - 1;
        var tail = max - head - 1;
        return s[..head] + "…" + s[^tail..];
    }
}
