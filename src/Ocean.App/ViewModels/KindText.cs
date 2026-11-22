using System.IO;
using System.Windows;
using System.Windows.Media;
using Ocean.Core;
using Ocean.Core.Media;
using Ocean.Core.Protocol;

namespace Ocean.App.ViewModels;

/// <summary>How a shared file of each kind is shown: its icon, badge, the verb that opens it, its particulars.</summary>
public static class KindText
{
    public static string Of(ItemDto dto) => dto.Kind ?? MediaKinds.Video;

    /// <summary>The icon while there is no preview (Segoe MDL2).</summary>
    public static string Glyph(string kind) => kind switch
    {
        MediaKinds.Audio => "", // music note
        MediaKinds.Image => "", // photo
        MediaKinds.Pdf => "",   // document
        MediaKinds.File => "\uE7C3",  // a page (see FileGlyph: by its type)
        _ => "",                // video
    };

    /// <summary>The small badge on a preview ("MUSIC", "PICTURE", "PDF"); none for a video.</summary>
    public static string? Badge(string kind) => kind switch
    {
        MediaKinds.Audio => "MUSIC",
        MediaKinds.Image => "PICTURE",
        MediaKinds.Pdf => "PDF",
        _ => null,
    };

    /// <summary>What opening it does: any other file can only be downloaded.</summary>
    public static string Verb(string kind) => kind switch { MediaKinds.Image => "View", MediaKinds.Pdf => "Open", MediaKinds.File => "Download", _ => "Play" };

    /// <summary>Ocean plays or shows it itself (a video, a song, a picture, a PDF).</summary>
    public static bool Opens(string kind) => MediaKinds.Opens(kind);

    /// <summary>Where a kind stands when sorted by type: videos, music, pictures, PDFs, the rest.</summary>
    public static int Order(string kind) { var i = Array.IndexOf(MediaKinds.All, kind); return i < 0 ? MediaKinds.All.Length : i; }

    public static string Plural(string kind) => kind switch
    {
        MediaKinds.Audio => "music", MediaKinds.Image => "pictures", MediaKinds.Pdf => "PDFs", MediaKinds.File => "other files", _ => "videos",
    };

    // ---- any other file: no preview, so its type is drawn from its name (an icon and a colour per family, and the extension) ----
    private static readonly Dictionary<string, (string Glyph, Brush Brush)> Types = BuildTypes();
    private static readonly (string Glyph, Brush Brush) Plain = ("\uE7C3", Frozen("#9FB0C8"));

    private static Brush Frozen(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

    private static Dictionary<string, (string, Brush)> BuildTypes()
    {
        var map = new Dictionary<string, (string, Brush)>(StringComparer.OrdinalIgnoreCase);
        void Add(string glyph, string hex, string exts) { var look = (glyph, Frozen(hex)); foreach (var e in exts.Split(' ')) map["." + e] = look; }
        Add("\uE8A5", "#5B9BF0", "doc docx docm dot dotx odt rtf pages wps");                       // writing
        Add("\uE9F9", "#4CC38A", "xls xlsx xlsm xlsb csv tsv ods numbers");                          // tables
        Add("\uE8A5", "#F0883E", "ppt pptx pps ppsx odp key");                                      // slides
        Add("\uE8A5", "#9FB0C8", "txt md log nfo ini cfg conf srt vtt sub ass ssa");                // plain text
        Add("\uF012", "#E5B84B", "zip rar 7z tar gz tgz bz2 xz zst cab iso img dmg");               // archives, disk images
        Add("\uE943", "#B18CF2", "cs js ts jsx tsx py java c cpp h hpp go rs php rb swift kt html htm css json xml yaml yml sql sh ps1 bat cmd"); // code, data
        Add("\uE7B8", "#F2777A", "exe msi msix appx apk dll jar deb rpm");                           // programs
        Add("\uE8D2", "#4FD1C5", "ttf otf woff woff2");                                             // fonts
        Add("\uE82D", "#4FD1C5", "epub mobi azw azw3 djvu fb2 cbz cbr");                             // books
        Add("\uE790", "#F28CC8", "psd ai svg eps fig sketch xd blend dwg dxf indd raw cr2 nef arw"); // design, raw pictures
        return map;
    }

    private static (string Glyph, Brush Brush) TypeOf(string name) => Types.TryGetValue(Path.GetExtension(name), out var t) ? t : Plain;
    public static string FileGlyph(string name) => TypeOf(name).Glyph;
    public static Brush FileBrush(string name) => TypeOf(name).Brush;
    /// <summary>The colour of a video's, a song's... placeholder icon while it has no preview.</summary>
    public static Brush Dim => Application.Current?.TryFindResource("TextDim") as Brush ?? Brushes.Gray;

    /// <summary>Picture: its pixels ("4032 × 3024"); video: its resolution ("1080p"); else nothing.</summary>
    public static string Resolution(ItemDto dto) => Of(dto) switch
    {
        MediaKinds.Image => dto.Width is { } w && dto.Height is { } h ? $"{w} × {h}" : "",
        MediaKinds.Video => Format.Resolution(dto.Width, dto.Height),
        _ => "",
    };

    /// <summary>Video and music: the length; a PDF: its pages; a picture: nothing.</summary>
    public static string Length(ItemDto dto) => Of(dto) switch
    {
        MediaKinds.Pdf => dto.Pages is { } p ? (p == 1 ? "1 page" : $"{p} pages") : "",
        MediaKinds.Image => "",
        _ => Format.Duration(dto.DurationMs),
    };

    /// <summary>"3 videos", "12 files" (several kinds), "1 picture".</summary>
    public static string Count(IReadOnlyCollection<ItemDto> items)
    {
        var kinds = items.Select(Of).Distinct().ToList();
        var n = items.Count;
        if (kinds.Count > 1) return n == 1 ? "1 file" : $"{n} files";
        var noun = kinds.Count == 0 ? "video" : MediaKinds.Noun(kinds[0]);
        var plural = noun == "PDF" ? "PDFs" : noun + "s";
        return n == 1 ? $"1 {noun}" : $"{n} {plural}";
    }
}
