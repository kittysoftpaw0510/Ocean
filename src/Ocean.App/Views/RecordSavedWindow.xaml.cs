using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Ocean.App.Services;
using Ocean.Core;
using Ocean.Core.Protocol;

namespace Ocean.App.Views;

/// <summary>
/// "Recording saved", with what one usually does next: open it, put it in a share (others on the network see it at
/// once), trim it, show it in its folder, copy its path. Bottom right of the screen, so it also shows while Ocean's
/// window is closed to the tray; it goes by itself after 30 s unless the mouse is on it.
/// </summary>
public partial class RecordSavedWindow : Window
{
    private static RecordSavedWindow? _current;
    private readonly ApiClient _api;
    private readonly Action<string, string, string> _toast;
    private readonly Action<string, double> _trim;
    private string _path;
    private readonly DispatcherTimer _life = new() { Interval = TimeSpan.FromSeconds(30) };

    private RecordSavedWindow(ApiClient api, RecordingResult r, string heading, Action<string, string, string> toast, Action<string, double> trim)
    {
        _api = api; _toast = toast; _trim = trim; _path = r.Path!;
        InitializeComponent();
        Heading.Text = heading;
        FileName.Text = System.IO.Path.GetFileName(_path);
        FileName.ToolTip = _path;
        var length = r.Duration.TotalHours >= 1 ? r.Duration.ToString(@"h\:mm\:ss") : r.Duration.ToString(@"m\:ss");
        Meta.Text = $"{length}  ·  {Format.Bytes(r.Bytes)}  ·  {System.IO.Path.GetDirectoryName(_path)}";
        if (r.Reason is { } why || r.Error is not null)
        {
            Note.Text = r.Error is { } e ? char.ToUpper(e[0]) + e[1..] + "." : "It stopped by itself: " + r.Reason + ".";
            Note.Visibility = Visibility.Visible;
        }
        _duration = r.Duration.TotalSeconds;
        _life.Tick += (_, _) => { if (!IsMouseOver && !_menuOpen) Close(); };
        MouseEnter += (_, _) => _life.Stop();
        MouseLeave += (_, _) => { _life.Stop(); _life.Start(); };
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 10; Top = area.Bottom - ActualHeight - 10;
            _life.Start();
        };
        Closed += (_, _) => { _life.Stop(); if (ReferenceEquals(_current, this)) _current = null; };
        _ = CheckSharedAsync();
    }

    private readonly double _duration;
    private bool _menuOpen;

    public static void Show(ApiClient api, RecordingResult r, string heading, Action<string, string, string> toast, Action<string, double> trim)
    {
        _current?.Close();
        _current = new RecordSavedWindow(api, r, heading, toast, trim);
        _current.Show();
    }

    // ---- what to do with it ----

    private void Open_Click(object sender, RoutedEventArgs e) => Start(_path, null);
    private void Folder_Click(object sender, RoutedEventArgs e) => Start("explorer.exe", $"/select,\"{_path}\"");
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_path); CopyButton.Content = "Copied ✓"; }
        catch { CopyButton.Content = "Couldn't copy"; }
    }

    private void Trim_Click(object sender, RoutedEventArgs e) { _trim(_path, _duration); Close(); }

    private void Start(string file, string? args)
    {
        try { Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true }); }
        catch (Exception ex) { _toast("Can't open it", ex.Message, "warn"); }
    }

    /// <summary>Is it in a folder that is shared already? Then it is on the network as it is.</summary>
    private async Task CheckSharedAsync()
    {
        try
        {
            var shares = await _api.GetAsync<List<ShareDefinitionDto>>("/api/shares") ?? new();
            var folder = System.IO.Path.GetDirectoryName(_path)!.TrimEnd('\\') + "\\";
            if (shares.FirstOrDefault(s => s.Roots.Any(r => folder.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || r.Equals(_path, StringComparison.OrdinalIgnoreCase))) is { } inShare)
            {
                ShareText.Text = $"Shared in “{inShare.Name}”";
                ShareButton.IsEnabled = false;
                ShareButton.ToolTip = "It is saved in a shared folder: others on the network can watch it already";
            }
        }
        catch { } // the service is busy or gone: the button stays
    }

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        List<ShareDefinitionDto> shares;
        try { shares = await _api.GetAsync<List<ShareDefinitionDto>>("/api/shares") ?? new(); }
        catch (Exception ex) { _toast("Can't share it", ex.Message, "warn"); return; }
        var menu = new ContextMenu { PlacementTarget = ShareButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var s in shares.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var who = s.Mode switch { AccessMode.Public => "everyone", AccessMode.Password => "with the password", _ => "people you approve" };
            var item = new MenuItem { Header = $"{s.Name}   ({who})" };
            var share = s;
            item.Click += async (_, _) => await AddAsync(share);
            menu.Items.Add(item);
        }
        if (shares.Count > 0) menu.Items.Add(new Separator());
        var everyone = new MenuItem { Header = "New share “My recordings” (everyone can watch)" };
        everyone.Click += async (_, _) => await NewShareAsync(AccessMode.Public);
        var ask = new MenuItem { Header = "New share “My recordings” (people I approve)" };
        ask.Click += async (_, _) => await NewShareAsync(AccessMode.Ask);
        menu.Items.Add(everyone); menu.Items.Add(ask);
        _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        menu.IsOpen = true;
    }

    private async Task AddAsync(ShareDefinitionDto share)
    {
        try
        {
            await _api.PostAsync($"/api/shares/{share.Id}/add", new AddToShareDto { Paths = { _path } });
            Shared(share.Name);
        }
        catch (Exception ex) { _toast("Can't share it", ex.Message, "warn"); }
    }

    private async Task NewShareAsync(AccessMode mode)
    {
        try
        {
            // one "My recordings" share collects them; a second click adds to it
            var shares = await _api.GetAsync<List<ShareDefinitionDto>>("/api/shares") ?? new();
            if (shares.FirstOrDefault(s => s.Name.Equals("My recordings", StringComparison.CurrentCultureIgnoreCase)) is { } existing) { await AddAsync(existing); return; }
            await _api.PostAsync<ShareDefinitionDto>("/api/shares", new ShareUpsertDto { Name = "My recordings", Mode = mode, Roots = { _path } });
            Shared("My recordings");
        }
        catch (Exception ex) { _toast("Can't share it", ex.Message, "warn"); }
    }

    private void Shared(string name)
    {
        ShareText.Text = $"Shared in “{name}” ✓";
        ShareButton.IsEnabled = false;
        AppLog.Info("Record", $"added {System.IO.Path.GetFileName(_path)} to the share {name}");
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
