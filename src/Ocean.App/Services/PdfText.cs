using System.Windows;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Ocean.App.Services;

/// <summary>
/// The words on a PDF's pages, for finding them (Ctrl+F in the viewer). Windows' PDF engine draws pages but doesn't
/// give their text, so each page is drawn large and read by Windows' own text recognition (OCR): it finds the words
/// of scanned pages too. A page's words come with where they are on it, as fractions of its size (0..1).
/// </summary>
public static class PdfText
{
    /// <summary>A word on a page: its text and where it is (fractions of the page's width and height).</summary>
    public sealed record Word(string Text, Rect At);

    /// <summary>A page's words in reading order, with the text they make ("word word word ...") and where each begins in it.</summary>
    public sealed class Page
    {
        public required IReadOnlyList<Word> Words { get; init; }
        public required string Text { get; init; }
        public required int[] Starts { get; init; }
    }

    private static OcrEngine? _engine;
    private static bool _tried;

    /// <summary>Windows' text recognition in this user's languages; null when Windows has none installed.</summary>
    public static OcrEngine? Engine
    {
        get
        {
            if (_tried) return _engine;
            _tried = true;
            try { _engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US")); }
            catch (Exception ex) { AppLog.Info("Viewer", "no text recognition: " + ex.Message); }
            return _engine;
        }
    }

    /// <summary>Reads one page (drawn about 2000 pixels on its long side: small print is still read well).</summary>
    public static async Task<Page> ReadAsync(PdfPage page)
    {
        var engine = Engine ?? throw new InvalidOperationException("no text recognition");
        double w = Math.Max(1, page.Size.Width), h = Math.Max(1, page.Size.Height);
        var scale = Math.Min(2200.0, OcrEngine.MaxImageDimension) / Math.Max(w, h);
        uint pw = (uint)Math.Max(1, Math.Round(w * scale)), ph = (uint)Math.Max(1, Math.Round(h * scale));
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
        {
            DestinationWidth = pw, DestinationHeight = ph, BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
        });
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        var words = new List<Word>();
        foreach (var line in result.Lines)
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                words.Add(new Word(word.Text, new Rect(r.X / pw, r.Y / ph, r.Width / pw, r.Height / ph)));
            }
        var starts = new int[words.Count];
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            starts[i] = sb.Length;
            sb.Append(words[i].Text);
        }
        return new Page { Words = words, Text = sb.ToString(), Starts = starts };
    }

    /// <summary>
    /// Where <paramref name="query"/> is on the page (case and repeated spaces ignored): for each place, the parts of
    /// the words it covers (a part of a word in proportion to its letters).
    /// </summary>
    public static List<List<Rect>> Find(Page page, string query)
    {
        var found = new List<List<Rect>>();
        var q = string.Join(' ', query.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (q.Length == 0 || page.Words.Count == 0) return found;
        for (var at = page.Text.IndexOf(q, StringComparison.CurrentCultureIgnoreCase); at >= 0;
             at = page.Text.IndexOf(q, at + q.Length, StringComparison.CurrentCultureIgnoreCase))
        {
            var end = at + q.Length;
            var rects = new List<Rect>();
            for (var i = WordAt(page, at); i < page.Words.Count && page.Starts[i] < end; i++)
            {
                var word = page.Words[i];
                int ws = page.Starts[i], we = ws + word.Text.Length;
                int from = Math.Max(at, ws) - ws, to = Math.Min(end, we) - ws;
                if (to <= from) continue;
                var r = word.At;
                var per = r.Width / Math.Max(1, word.Text.Length);
                rects.Add(new Rect(r.X + per * from, r.Y, per * (to - from), r.Height));
            }
            if (rects.Count > 0) found.Add(rects);
        }
        return found;
    }

    private static int WordAt(Page page, int index)
    {
        var i = Array.BinarySearch(page.Starts, index);
        return i >= 0 ? i : Math.Max(0, ~i - 1);
    }
}
