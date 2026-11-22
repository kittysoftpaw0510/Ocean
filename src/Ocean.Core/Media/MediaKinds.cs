namespace Ocean.Core.Media;

/// <summary>
/// The kinds of file a share can hold: videos (always), music, pictures, PDFs, and every other file ("file":
/// documents, archives, programs...). Ocean plays or shows the first four itself; the others can only be
/// downloaded. A share chooses which kinds of the files in its folders it shares (<c>Kinds</c>; older shares and
/// the default: videos only); a file added on its own is shared whatever its kind. Leftovers of other programs
/// (half downloads, temporary and lock files, Windows' own folder files) are never seen.
/// </summary>
public static class MediaKinds
{
    public const string Video = "video", Audio = "audio", Image = "image", Pdf = "pdf", File = "file";
    public static readonly string[] All = { Video, Audio, Image, Pdf, File };

    private static readonly HashSet<string> JunkExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".temp", ".part", ".partial", ".crdownload", ".download", ".opdownload", ".oceanpart", ".!ut", ".bc!", ".lnk", ".lock",
    };
    private static readonly HashSet<string> JunkNames = new(StringComparer.OrdinalIgnoreCase) { "thumbs.db", "desktop.ini", ".ds_store", "ehthumbs.db" };

    /// <summary>Something another program left lying around, not a file anyone means to share.</summary>
    public static bool IsJunk(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length == 0 || name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith("._", StringComparison.Ordinal)
               || JunkNames.Contains(name) || JunkExtensions.Contains(Path.GetExtension(name));
    }

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

    /// <summary>video, audio, image, pdf, or file (anything else); null: not a file Ocean shares (a leftover).</summary>
    public static string? KindOf(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length > 0 && Map.TryGetValue(ext, out var k)) return k.Kind;
        return IsJunk(path) ? null : File;
    }

    /// <summary>A file Ocean shares (anything but a leftover).</summary>
    public static bool IsKnown(string path) => KindOf(path) is not null;

    /// <summary>Named as a video, a song, a picture or a PDF: what Ocean plays or shows itself.</summary>
    public static bool IsMedia(string path) => Map.ContainsKey(Path.GetExtension(path));

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".xml", ".ini", ".cfg", ".conf", ".srt", ".vtt",
        ".yaml", ".yml", ".toml", ".nfo", ".cs", ".js", ".ts", ".jsx", ".tsx", ".py", ".java", ".c", ".cpp", ".h", ".hpp", ".go",
        ".rs", ".php", ".rb", ".swift", ".kt", ".html", ".htm", ".css", ".sql", ".sh", ".ps1", ".bat", ".cmd", ".gitignore",
    };

    /// <summary>
    /// Any other file that is plain text (txt, md, log, csv, json, code...): Ocean shows it in its text viewer. On the
    /// network it stays a "file" (an older Ocean downloads it); the app decides from its name.
    /// </summary>
    public static bool IsText(string name) => TextExtensions.Contains(Path.GetExtension(name));

    /// <summary>Ocean plays or shows this kind itself; the rest ("file") can only be downloaded.</summary>
    public static bool Opens(string? kind) => kind != File;

    /// <summary>The extension as people read it: "DOCX", "ZIP"; "FILE" when there is none (or it is too long to be one).</summary>
    public static string ExtensionLabel(string name)
    {
        var ext = Path.GetExtension(name).TrimStart('.');
        return ext.Length is > 0 and <= 5 ? ext.ToUpperInvariant() : "FILE";
    }

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
        return "All files|*.*|Videos, music, pictures and PDFs|" + F(Map.Keys) + "|Videos|" + F(Extensions(Video)) + "|Music|" + F(Extensions(Audio))
               + "|Pictures|" + F(Extensions(Image)) + "|PDFs|*.pdf";
    }

    /// <summary>What people call it: "video", "song", "picture", "PDF", "file".</summary>
    public static string Noun(string? kind) => kind switch { Audio => "song", Image => "picture", Pdf => "PDF", File => "file", _ => "video" };

    /// <summary>
    /// What a caller's Ocean can take, from what it says it knows (the X-Ocean-Kinds header): 0 = videos only (it
    /// says nothing: an Ocean from before), 1 = also music, pictures and PDFs, 2 = every file.
    /// </summary>
    public static int Level(string? said)
        => said is null ? 0 : said.Contains(File, StringComparison.Ordinal) ? 2 : said.Contains(Pdf, StringComparison.Ordinal) ? 1 : 0;

    /// <summary>Whether an Ocean of that level is told about a file of this kind (it would try to play what it doesn't know).</summary>
    public static bool Fits(string kind, int level) => level >= 2 || (level == 1 ? kind != File : kind == Video);
}
