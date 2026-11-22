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

    public static string Verb(string kind) => kind switch { MediaKinds.Image => "View", MediaKinds.Pdf => "Open", _ => "Play" };

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
