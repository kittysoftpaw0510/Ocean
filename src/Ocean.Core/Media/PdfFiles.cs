using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Ocean.Core.Media;

/// <summary>
/// PDFs through Windows' own PDF engine (Windows.Data.Pdf, the one Windows apps use): the page count, a first-page
/// preview and pages drawn at any size for the viewer. No component of our own; works on every Windows 10 and 11.
/// </summary>
public static class PdfFiles
{
    /// <summary>
    /// The document, read from the file as a stream (Windows' own path API refuses forward slashes and short 8.3
    /// names, which a temp folder may have). The stream stays open while the document is in use (the viewer draws
    /// pages later): it goes with the document. Others may read the file meanwhile.
    /// </summary>
    public static async Task<PdfDocument> OpenAsync(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
        try { return await PdfDocument.LoadFromStreamAsync(fs.AsRandomAccessStream()); }
        catch { fs.Dispose(); throw; }
    }

    /// <summary>Opened for a moment (the file is let go at once).</summary>
    private static async Task<T> WithAsync<T>(string path, Func<PdfDocument, Task<T>> use)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
        var doc = await PdfDocument.LoadFromStreamAsync(fs.AsRandomAccessStream());
        return await use(doc);
    }

    /// <summary>How many pages (null: not readable, e.g. protected by a password).</summary>
    public static async Task<int?> PagesAsync(string path)
    {
        try { return await WithAsync(path, d => Task.FromResult<int?>((int)d.PageCount)); }
        catch { return null; }
    }

    /// <summary>The first page as a JPEG fitting <paramref name="width"/> × <paramref name="height"/> (on white); null when it can't be read.</summary>
    public static async Task<byte[]?> ThumbnailAsync(string path, int width = 320, int height = 180)
    {
        try
        {
            return await WithAsync<byte[]?>(path, async doc =>
            {
                if (doc.PageCount == 0) return null;
                using var page = doc.GetPage(0);
                var scale = Math.Min(width / Math.Max(1, page.Size.Width), height / Math.Max(1, page.Size.Height));
                return await RenderAsync(page, (uint)Math.Max(1, page.Size.Width * scale), (uint)Math.Max(1, page.Size.Height * scale), jpeg: true);
            });
        }
        catch { return null; }
    }

    /// <summary>A page drawn <paramref name="pixelWidth"/> wide (its height in proportion): PNG.</summary>
    public static Task<byte[]> RenderAsync(PdfPage page, uint pixelWidth)
        => RenderAsync(page, pixelWidth, (uint)Math.Max(1, page.Size.Height * pixelWidth / Math.Max(1, page.Size.Width)), jpeg: false);

    /// <summary>A page drawn exactly <paramref name="w"/> × <paramref name="h"/> pixels: PNG.</summary>
    public static Task<byte[]> RenderAsync(PdfPage page, uint w, uint h) => RenderAsync(page, w, h, jpeg: false);

    private static async Task<byte[]> RenderAsync(PdfPage page, uint w, uint h, bool jpeg)
    {
        using var stream = new InMemoryRandomAccessStream();
        var options = new PdfPageRenderOptions { DestinationWidth = w, DestinationHeight = h, BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255) };
        if (jpeg) options.BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId;
        await page.RenderToStreamAsync(stream, options);
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
