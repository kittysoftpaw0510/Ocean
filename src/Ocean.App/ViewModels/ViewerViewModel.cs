using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ocean.App.Services;
using Ocean.Core.Media;
using Windows.Data.Pdf;

namespace Ocean.App.ViewModels;

/// <summary>A picture, a PDF or a text to show: from another PC (its stream link) or from this one (its path).</summary>
public sealed record ViewerEntry(string Name, string Source, bool Local, string Subtitle, string Kind, Func<BitmapImage?>? Preview = null, Action? Download = null);

/// <summary>
/// The built-in viewer for pictures, PDFs and texts (videos and music go to the player). Pictures: fit to the window, zoom,
/// ←/→ through the pictures of the same folder (the next and previous ones are fetched ahead). PDFs: Windows' own PDF
/// engine draws the pages as they come into view (see ViewerView). Over the main window, like the player.
/// </summary>
public sealed partial class ViewerViewModel : ObservableObject
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(10) };

    private List<ViewerEntry> _entries = new();
    private readonly Dictionary<string, Task<BitmapSource>> _ahead = new(StringComparer.Ordinal);
    private CancellationTokenSource _cts = new();

    /// <summary>The viewer's own kind for a text file (on the network it is a "file").</summary>
    public const string TextKind = "text";
    /// <summary>A text larger than this is not shown (it is downloaded).</summary>
    public const long MaxText = 8 * 1024 * 1024;

    [ObservableProperty] private bool _isOpen;
    /// <summary>image, pdf or text.</summary>
    [ObservableProperty] private string _kind = MediaKinds.Image;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    // a picture
    [ObservableProperty] private int _index;
    [ObservableProperty] private BitmapSource? _image;
    /// <summary>The small preview, shown at once while the picture itself comes.</summary>
    [ObservableProperty] private BitmapImage? _preview;
    // a PDF
    [ObservableProperty] private PdfDocument? _pdf;
    [ObservableProperty] private string _pageText = "";
    /// <summary>The PDF's file on this PC (another PC's: a copy fetched into the temp folder).</summary>
    public string? PdfPath { get; private set; }
    // a text
    [ObservableProperty] private string? _textContent;
    [ObservableProperty] private bool _wrap = true;
    // finding words (a PDF, a text): the view does the finding, this is what the bar shows
    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty] private string _searchQuery = "";
    /// <summary>"3 of 17", "No matches", "Reading page 4 of 30…".</summary>
    [ObservableProperty] private string _searchStatus = "";

    public bool IsImage => Kind == MediaKinds.Image;
    public bool IsPdf => Kind == MediaKinds.Pdf;
    public bool IsText => Kind == TextKind;
    /// <summary>Words can be found in it (Ctrl+F).</summary>
    public bool CanSearch => IsPdf || IsText;
    public bool HasMany => IsImage && _entries.Count > 1;
    public string Position => HasMany ? $"{Index + 1} / {_entries.Count}" : "";
    public bool CanDownload => Current?.Download is not null;
    private ViewerEntry? Current => Index >= 0 && Index < _entries.Count ? _entries[Index] : null;

    partial void OnKindChanged(string value)
    {
        OnPropertyChanged(nameof(IsImage)); OnPropertyChanged(nameof(IsPdf)); OnPropertyChanged(nameof(IsText));
        OnPropertyChanged(nameof(CanSearch)); OnPropertyChanged(nameof(HasMany));
    }
    [RelayCommand] private void ToggleWrap() => Wrap = !Wrap;
    [RelayCommand] private void OpenSearch() { if (CanSearch) IsSearchOpen = true; }
    [RelayCommand] private void CloseSearch() { IsSearchOpen = false; SearchQuery = ""; SearchStatus = ""; }
    partial void OnIndexChanged(int value) { OnPropertyChanged(nameof(Position)); OnPropertyChanged(nameof(CanDownload)); }

    /// <summary>Pictures: <paramref name="entries"/> are the pictures of the folder (in its order), the one at <paramref name="index"/> first.</summary>
    public void OpenImages(IReadOnlyList<ViewerEntry> entries, int index)
    {
        Reset();
        _entries = entries.ToList();
        Kind = MediaKinds.Image;
        Index = Math.Clamp(index, 0, Math.Max(0, _entries.Count - 1));
        OnPropertyChanged(nameof(HasMany)); OnPropertyChanged(nameof(Position));
        IsOpen = true;
        _ = ShowAsync();
    }

    public void OpenPdf(ViewerEntry entry)
    {
        Reset();
        _entries = new List<ViewerEntry> { entry };
        Kind = MediaKinds.Pdf;
        Index = 0;
        Title = entry.Name; Subtitle = entry.Subtitle;
        OnPropertyChanged(nameof(CanDownload));
        IsOpen = true;
        _ = LoadPdfAsync(entry, _cts.Token);
    }

    /// <summary>A text file (txt, md, log, csv, code...): read whole, shown in a monospaced font, words found with Ctrl+F.</summary>
    public void OpenText(ViewerEntry entry)
    {
        Reset();
        _entries = new List<ViewerEntry> { entry };
        Kind = TextKind;
        Index = 0;
        Title = entry.Name; Subtitle = entry.Subtitle;
        OnPropertyChanged(nameof(CanDownload));
        IsOpen = true;
        _ = LoadTextAsync(entry, _cts.Token);
    }

    private void Reset()
    {
        _cts.Cancel(); _cts = new CancellationTokenSource();
        _ahead.Clear();
        Image = null; Preview = null; Pdf = null; PdfPath = null; TextContent = null; Error = null; PageText = "";
        IsSearchOpen = false; SearchQuery = ""; SearchStatus = "";
    }

    [RelayCommand]
    private void Close() { Reset(); _entries = new(); IsOpen = false; }

    [RelayCommand] private void Next() { if (IsImage && Index + 1 < _entries.Count) { Index++; _ = ShowAsync(); } }
    [RelayCommand] private void Previous() { if (IsImage && Index > 0) { Index--; _ = ShowAsync(); } }
    [RelayCommand] private void Download() => Current?.Download?.Invoke();

    // ------------------------------------------------------------------ pictures

    private async Task ShowAsync()
    {
        if (Current is not { } e) return;
        var ct = _cts.Token;
        Title = e.Name; Subtitle = e.Subtitle; Error = null;
        Preview = e.Preview?.Invoke();
        Image = null;
        IsLoading = true;
        try
        {
            var img = await Fetch(e);
            if (ct.IsCancellationRequested || !ReferenceEquals(Current, e)) return;
            Image = img;
            // the neighbours, ahead of the next ←/→
            foreach (var i in new[] { Index + 1, Index - 1 }) if (i >= 0 && i < _entries.Count) _ = Fetch(_entries[i]);
            foreach (var key in _ahead.Keys.ToList())
                if (!_entries.Skip(Math.Max(0, Index - 2)).Take(5).Any(x => x.Source == key)) _ahead.Remove(key);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (!ReferenceEquals(Current, e)) return;
            _ahead.Remove(e.Source);
            Error = CantShow(e, ex);
            AppLog.Info("Viewer", $"picture {e.Name}: {ex.Message}");
        }
        finally { if (ReferenceEquals(Current, e)) IsLoading = false; }
    }

    private Task<BitmapSource> Fetch(ViewerEntry e)
    {
        if (_ahead.TryGetValue(e.Source, out var t)) return t;
        t = Task.Run(async () =>
        {
            var bytes = e.Local ? await File.ReadAllBytesAsync(e.Source) : await Http.GetByteArrayAsync(e.Source);
            return Decode(bytes);
        });
        _ahead[e.Source] = t;
        return t;
    }

    /// <summary>Decoded off the UI thread, frozen (a huge photo is drawn at most 8192 pixels wide: more no screen shows).</summary>
    private static BitmapSource Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        if (frame.PixelWidth <= 8192) { frame.Freeze(); return frame; }
        ms.Position = 0;
        var img = new BitmapImage();
        img.BeginInit(); img.StreamSource = ms; img.CacheOption = BitmapCacheOption.OnLoad; img.DecodePixelWidth = 8192; img.EndInit();
        img.Freeze();
        return img;
    }

    private static string CantShow(ViewerEntry e, Exception ex)
    {
        var ext = Path.GetExtension(e.Name).ToLowerInvariant();
        if (ex is NotSupportedException || ex is FileFormatException || ex.HResult == unchecked((int)0x88982F50))
            return ext is ".heic" or ".heif" ? "Windows can't show HEIC pictures without the free “HEIF Image Extensions” from the Microsoft Store."
                 : ext == ".webp" ? "Windows can't show WebP pictures without the free “WebP Image Extensions” from the Microsoft Store."
                 : ext == ".avif" ? "Windows can't show AVIF pictures without the free “AV1 Video Extension” from the Microsoft Store."
                 : "This picture can't be shown here (its format isn't one Windows can read).";
        return ex is HttpRequestException ? "The picture couldn't be fetched: " + ex.Message : "The picture couldn't be shown: " + ex.Message;
    }

    // ------------------------------------------------------------------ PDFs

    private async Task LoadPdfAsync(ViewerEntry e, CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            var path = e.Local ? e.Source : await FetchToFile(e, ct);
            if (ct.IsCancellationRequested) return;
            var doc = await PdfFiles.OpenAsync(path);
            if (ct.IsCancellationRequested) return;
            PdfPath = path;
            Pdf = doc;
            Subtitle = e.Subtitle + (doc.PageCount == 1 ? "  ·  1 page" : $"  ·  {doc.PageCount} pages");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Error = ex.HResult == unchecked((int)0x8007052B) || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? "This PDF is protected by a password. Download it and open it in a PDF reader."
                : ex is HttpRequestException ? "The PDF couldn't be fetched: " + ex.Message
                : "This PDF can't be shown: it isn't one Windows can read (it may be damaged).";
            AppLog.Info("Viewer", $"pdf {e.Name}: {ex.Message}");
        }
        finally { if (!ct.IsCancellationRequested) IsLoading = false; }
    }

    // ------------------------------------------------------------------ texts

    private async Task LoadTextAsync(ViewerEntry e, CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            byte[] bytes;
            if (e.Local)
            {
                if (new FileInfo(e.Source).Length > MaxText) { Error = TooBig(e); return; }
                bytes = await File.ReadAllBytesAsync(e.Source, ct);
            }
            else
            {
                using var resp = await Http.GetAsync(e.Source, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                if (resp.Content.Headers.ContentLength > MaxText) { Error = TooBig(e); return; }
                bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > MaxText) { Error = TooBig(e); return; }
            }
            var text = await Task.Run(() => TextFiles.Decode(bytes), ct);
            if (ct.IsCancellationRequested) return;
            if (text is null) { Error = "This file isn't text that can be shown here (it holds other data)."; return; }
            TextContent = text;
            var lines = text.Count(c => c == '\n') + (text.Length > 0 && !text.EndsWith('\n') ? 1 : 0);
            Subtitle = e.Subtitle + (lines == 1 ? "  ·  1 line" : $"  ·  {lines:N0} lines");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Error = ex is HttpRequestException ? "The file couldn't be fetched: " + ex.Message : "The file couldn't be shown: " + ex.Message;
            AppLog.Info("Viewer", $"text {e.Name}: {ex.Message}");
        }
        finally { if (!ct.IsCancellationRequested) IsLoading = false; }
    }

    private static string TooBig(ViewerEntry e) => $"“{e.Name}” is too large to show here (more than {MaxText / 1024 / 1024} MB of text). Download it and open it in an editor.";

    /// <summary>Another PC's PDF, fetched whole into a file of its own (Windows' PDF engine reads files).</summary>
    private static async Task<string> FetchToFile(ViewerEntry e, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Ocean", "viewer");
        Directory.CreateDirectory(dir);
        // (what an earlier viewing left behind goes)
        foreach (var old in Directory.EnumerateFiles(dir)) { try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > TimeSpan.FromHours(1)) File.Delete(old); } catch { } }
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N")[..12] + ".pdf");
        using var resp = await Http.GetAsync(e.Source, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using (var file = File.Create(path))
            await resp.Content.CopyToAsync(file, ct);
        return path;
    }
}
