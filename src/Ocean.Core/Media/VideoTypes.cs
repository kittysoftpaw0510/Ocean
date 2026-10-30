namespace Ocean.Core.Media;

public static class VideoTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".mkv"] = "video/x-matroska",
        [".webm"] = "video/webm",
        [".avi"] = "video/x-msvideo",
        [".divx"] = "video/x-msvideo",
        [".wmv"] = "video/x-ms-wmv",
        [".asf"] = "video/x-ms-asf",
        [".mpg"] = "video/mpeg",
        [".mpeg"] = "video/mpeg",
        [".mpe"] = "video/mpeg",
        [".m2v"] = "video/mpeg",
        [".vob"] = "video/mpeg",
        [".ts"] = "video/mp2t",
        [".m2ts"] = "video/mp2t",
        [".mts"] = "video/mp2t",
        [".3gp"] = "video/3gpp",
        [".3g2"] = "video/3gpp2",
        [".flv"] = "video/x-flv",
        [".f4v"] = "video/mp4",
        [".ogv"] = "video/ogg",
        [".rm"] = "application/vnd.rn-realmedia",
        [".rmvb"] = "application/vnd.rn-realmedia-vbr",
    };

    public static IReadOnlyCollection<string> Extensions => Map.Keys;

    public static bool IsVideo(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length > 0 && Map.ContainsKey(ext);
    }

    public static string ContentType(string path)
        => Map.TryGetValue(Path.GetExtension(path), out var ct) ? ct : "application/octet-stream";

    public static string FilterString()
        => "Video files|" + string.Join(";", Map.Keys.Select(k => "*" + k)) + "|All files|*.*";
}
