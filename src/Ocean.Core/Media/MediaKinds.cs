namespace Ocean.Core.Media;

/// <summary>
/// The kinds of file a share can hold: videos (always), music, pictures and PDFs. A share chooses which kinds of
/// the files in its folders it shares (<c>Kinds</c>; older shares and the default: videos only); a file added on
/// its own is shared whatever its kind. Every other file is never seen.
/// </summary>
public static class MediaKinds
{
    public const string Video = "video", Audio = "audio", Image = "image", Pdf = "pdf";
    public static readonly string[] All = { Video, Audio, Image, Pdf };

    private static readonly Dictionary<string, (string Kind, string Type)> Map = Build();

    private static Dictionary<string, (string, string)> Build()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var ext in VideoTypes.Extensions) map[ext] = (Video, VideoTypes.ContentType("x" + ext));
        foreach (var (ext, type) in new[]
        {
            (".mp3", "audio/mpeg"), (".m4a", "audio/mp4"), (".aac", "audio/aac"), (".flac", "audio/flac"), (".wav", "audio/wav"),
            (".ogg", "audio/ogg"), (".oga", "audio/ogg"), (".opus", "audio/ogg"), (".wma", "audio/x-ms-wma"), (".aif", "audio/aiff"),
            (".aiff", "audio/aiff"), (".mka", "audio/x-matroska"),
        }) map[ext] = (Audio, type);
        foreach (var (ext, type) in new[]
        {
            (".jpg", "image/jpeg"), (".jpeg", "image/jpeg"), (".jfif", "image/jpeg"), (".png", "image/png"), (".gif", "image/gif"),
            (".bmp", "image/bmp"), (".webp", "image/webp"), (".heic", "image/heic"), (".heif", "image/heif"), (".avif", "image/avif"),
            (".tif", "image/tiff"), (".tiff", "image/tiff"),
        }) map[ext] = (Image, type);
        map[".pdf"] = (Pdf, "application/pdf");
        return map;
    }

    /// <summary>video, audio, image, pdf; null: not a file Ocean shares.</summary>
    public static string? KindOf(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length > 0 && Map.TryGetValue(ext, out var k) ? k.Kind : null;
    }

    public static bool IsKnown(string path) => KindOf(path) is not null;

    public static string ContentType(string path)
        => Map.TryGetValue(Path.GetExtension(path), out var k) ? k.Type : "application/octet-stream";

    /// <summary>A share's kinds, cleaned: known ones only, in order, videos always among them.</summary>
    public static List<string> Normalize(IEnumerable<string>? kinds)
    {
        var set = new HashSet<string>(kinds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase) { Video };
        return All.Where(set.Contains).ToList();
    }

    public static IEnumerable<string> Extensions(string kind) => Map.Where(kv => kv.Value.Kind == kind).Select(kv => kv.Key);

    /// <summary>For the file dialog: every kind, then each kind on its own.</summary>
    public static string FilterString()
    {
        string F(IEnumerable<string> exts) => string.Join(";", exts.Select(e => "*" + e));
        return "Videos, music, pictures and PDFs|" + F(Map.Keys) + "|Videos|" + F(Extensions(Video)) + "|Music|" + F(Extensions(Audio))
               + "|Pictures|" + F(Extensions(Image)) + "|PDFs|*.pdf|All files|*.*";
    }

    /// <summary>What people call it: "video", "song", "picture", "PDF".</summary>
    public static string Noun(string? kind) => kind switch { Audio => "song", Image => "picture", Pdf => "PDF", _ => "video" };
}
