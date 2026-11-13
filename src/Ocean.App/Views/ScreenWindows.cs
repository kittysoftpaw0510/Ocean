using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ocean.App.Services;
using Ocean.App.ViewModels;

namespace Ocean.App.Views;

/// <summary>
/// Screen sharing's windows: the picker, the questions (bottom right, one above the other), the viewer and the
/// "you are sharing" bar. None of them needs Ocean's main window, which may be closed to the tray.
/// </summary>
public sealed class ScreenUi : IScreenUi
{
    private readonly ScreenViewModel _vm;
    private readonly Dictionary<string, ScreenPromptWindow> _prompts = new(StringComparer.Ordinal);
    private ScreenViewWindow? _viewer;
    private ScreenBarWindow? _bar;
    private RecordBarWindow? _recordBar;

    public ScreenUi(ScreenViewModel vm) { _vm = vm; }

    public Task<ScreenPick?> PickAsync(string heading, string detail)
    {
        var w = new ScreenPickerWindow(heading, detail);
        var main = Application.Current.MainWindow;
        if (main is { IsVisible: true, WindowState: not WindowState.Minimized }) { w.Owner = main; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        return Task.FromResult(w.ShowDialog() == true ? w.Result : null);
    }

    public void ShowPrompt(ScreenPromptVm prompt)
    {
        if (_prompts.ContainsKey(prompt.Dto.AskId)) return;
        var w = new ScreenPromptWindow(prompt, _vm);
        _prompts[prompt.Dto.AskId] = w;
        w.Closed += (_, _) => { _prompts.Remove(prompt.Dto.AskId); Stack(); };
        w.Show();
        Stack();
        try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
    }

    public void ClosePrompt(string askId) { if (_prompts.TryGetValue(askId, out var w)) w.Close(); }

    /// <summary>Bottom right of the main screen's work area, the oldest lowest.</summary>
    private void Stack()
    {
        var area = SystemParameters.WorkArea;
        var bottom = area.Bottom - 16;
        foreach (var w in _prompts.Values)
        {
            w.UpdateLayout();
            var h = w.ActualHeight > 0 ? w.ActualHeight : 170;
            w.Left = area.Right - w.Width - 16;
            w.Top = bottom - h;
            bottom -= h + 10;
        }
    }

    public void ShowViewer(ScreenViewVm view)
    {
        if (_viewer is not null && ReferenceEquals(_viewer.View, view))
        {
            if (_viewer.WindowState == WindowState.Minimized) _viewer.WindowState = WindowState.Normal;
            _viewer.Activate();
            return;
        }
        CloseViewer();
        _viewer = new ScreenViewWindow(view, _vm);
        var mine = _viewer;
        _viewer.Closed += (_, _) => { if (ReferenceEquals(_viewer, mine)) _viewer = null; };
        _viewer.Show();
        _viewer.Activate();
    }

    public void CloseViewer() { var w = _viewer; _viewer = null; w?.Close(); }

    public void ShowBar()
    {
        if (_bar is not null) return;
        _bar = new ScreenBarWindow(_vm);
        _bar.Show();
    }

    public void HideBar() { var b = _bar; _bar = null; b?.CloseForGood(); }

    public Task<ScreenPick?> PickToRecordAsync(RecordViewModel record)
    {
        var w = new ScreenPickerWindow("Record your screen", "Pick what to record: a whole screen, one window or a part of a screen.", record);
        var main = Application.Current.MainWindow;
        if (main is { IsVisible: true, WindowState: not WindowState.Minimized }) { w.Owner = main; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        return Task.FromResult(w.ShowDialog() == true ? w.Result : null);
    }

    public void ShowRecordBar(RecordViewModel record)
    {
        if (_recordBar is not null) return;
        _recordBar = new RecordBarWindow(record, below: _bar);
        _recordBar.Show();
        // the boundary of what is recorded
        _boundary?.Dispose();
        if (record.Target is { } target)
        {
            try { _boundary = new RecordBoundary(target, record); }
            catch (Exception ex) { AppLog.Warn("Record", "the recording boundary can't be shown", ex); }
        }
    }

    public void HideRecordBar()
    {
        var b = _recordBar; _recordBar = null; b?.CloseForGood();
        _boundary?.Dispose(); _boundary = null;
    }

    private RecordBoundary? _boundary;

    public Task<bool> CountdownAsync(ScreenPick target)
    {
        // the area: a screen, a part of one, a window (when it can't be seen, the main screen)
        var mon = ScreenSources.Monitors();
        var m = mon.FirstOrDefault(x => x.Handle == target.Monitor) ?? mon.FirstOrDefault();
        Int32Rect area = target.Kind switch
        {
            ScreenPick.Kinds.Window when ScreenSources.IsOnScreen(target.Window) && ScreenSources.WindowBounds(target.Window) is { } w => w,
            ScreenPick.Kinds.Region when m is not null => new Int32Rect(m.Bounds.X + target.CropLeft, m.Bounds.Y + target.CropTop, target.SourceWidth, target.SourceHeight),
            _ => m?.Bounds ?? new Int32Rect(0, 0, 800, 600),
        };
        return RecordCountdownWindow.RunAsync(area);
    }

    public bool Confirm(string title, string message, string okLabel, bool danger, bool hideFromCapture)
    {
        var w = new ConfirmWindow(title, message, okLabel, "Cancel", danger) { Topmost = true };
        var main = Application.Current.MainWindow;
        if (main is { IsVisible: true, WindowState: not WindowState.Minimized } && !hideFromCapture) w.Owner = main;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (hideFromCapture) w.SourceInitialized += (_, _) => ScreenSources.HideFromCapture(w);
        return w.ShowDialog() == true;
    }

    public void ShowSaved(RecordingResult result, string heading)
    {
        var record = (Application.Current.MainWindow?.DataContext as MainViewModel)?.Record;
        RecordSavedWindow.Show(_vm.Api, result, heading,
            (title, text, kind) => (Application.Current.MainWindow?.DataContext as MainViewModel)?.Toast(title, text, kind),
            (path, duration) => record?.Trim(path, duration));
    }

    public void OpenTrim(string path, double duration, Action<RecordingTrim.Result, double> done)
    {
        var w = new TrimWindow(path, duration, done);
        w.Show();
        w.Activate();
    }
}

/// <summary>
/// "Recording": a small bar on top of everything and on every desktop while a recording runs: time, size, the
/// sound, mute the microphone, Stop. Kept out of every capture (it is never in the recording). Can be dragged
/// aside; not closed: only Stop removes it.
/// </summary>
public partial class RecordBarWindow : Window
{
    private readonly AllDesktops _allDesktops;
    private bool _closing;

    public RecordBarWindow(RecordViewModel vm, Window? below)
    {
        InitializeComponent();
        DataContext = vm;
        SourceInitialized += (_, _) => ScreenSources.HideFromCapture(this);
        _allDesktops = new AllDesktops(this);
        _allDesktops.Set(true);
        Loaded += (_, _) =>
        {
            // top middle of the main screen; under the sharing bar when both run
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = below is { IsVisible: true } ? below.Top + below.ActualHeight : area.Top + 8;
        };
        SizeChanged += (_, e) => { if (e.WidthChanged && IsLoaded && e.PreviousSize.Width > 0) Left -= (e.NewSize.Width - e.PreviousSize.Width) / 2; };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } } };
    }

    public void CloseForGood() { _closing = true; _allDesktops.Dispose(); Close(); }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closing) e.Cancel = true; // Alt+F4 must not hide that the screen is being recorded
        base.OnClosing(e);
    }
}

/// <summary>A question from another PC, in a small window of its own (also seen when Ocean is in the tray).</summary>
public partial class ScreenPromptWindow : Window
{
    private readonly ScreenPromptVm _prompt;
    private readonly ScreenViewModel _vm;
    private readonly AllDesktops _allDesktops;

    public ScreenPromptWindow(ScreenPromptVm prompt, ScreenViewModel vm)
    {
        _prompt = prompt; _vm = vm;
        InitializeComponent();
        DataContext = prompt;
        _allDesktops = new AllDesktops(this);
        _allDesktops.Set(true); // seen on whatever desktop the user is
        Closed += (_, _) => _allDesktops.Dispose();
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } } };
    }

    private void Allow_Click(object sender, RoutedEventArgs e) => _ = _vm.AnswerAsync(_prompt, allow: true);
    private void Decline_Click(object sender, RoutedEventArgs e) => _ = _vm.AnswerAsync(_prompt, allow: false);
    private void Mute_Click(object sender, RoutedEventArgs e) => _ = _vm.AnswerAsync(_prompt, allow: false, mute: true);
}

/// <summary>
/// "You are sharing your screen": a small bar on top of everything, on every desktop, as long as a session runs,
/// with who watches and Stop. It is kept out of the capture, so viewers don't see it. It can be dragged aside but
/// not closed: only stopping the sharing removes it.
/// </summary>
public partial class ScreenBarWindow : Window
{
    private readonly AllDesktops _allDesktops;
    private bool _closing;

    public ScreenBarWindow(ScreenViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        SourceInitialized += (_, _) => ScreenSources.HideFromCapture(this);
        _allDesktops = new AllDesktops(this);
        _allDesktops.Set(true);
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2; Top = area.Top + 8;
        };
        SizeChanged += (_, e) => { if (e.WidthChanged && IsLoaded) Left -= (e.NewSize.Width - e.PreviousSize.Width) / 2; }; // grows around its middle
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } } };
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _ = ((ScreenViewModel)DataContext).StopAsync();

    public void CloseForGood() { _closing = true; _allDesktops.Dispose(); Close(); }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closing) e.Cancel = true; // Alt+F4 must not hide the fact that the screen is shared
        base.OnClosing(e);
    }
}

/// <summary>
/// A shared screen: the picture as large as the window allows, a status line, full screen on F11 or a double-click.
/// Closing the window stops watching. When the host stops, the last picture stays with a note until the user closes.
/// </summary>
public partial class ScreenViewWindow : Window
{
    private readonly ScreenViewModel _vm;
    private readonly ScreenDecoder _decoder;
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(1) };
    private WindowState _stateBefore;
    private bool _full;

    public ScreenViewVm View { get; }

    public ScreenViewWindow(ScreenViewVm view, ScreenViewModel vm)
    {
        View = view; _vm = vm;
        InitializeComponent();
        DataContext = view;
        Title = view.WindowTitle;
        try { Icon = IconFactory.WindowIcon(); } catch { }
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        // as large as the picture, within most of the screen
        var area = SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow ?? this);
        double w = view.Width / dpi.DpiScaleX, h = view.Height / dpi.DpiScaleY;
        var scale = Math.Min(1.0, Math.Min(area.Width * 0.85 / w, (area.Height * 0.85 - 60) / h));
        Width = Math.Max(480, w * scale + 16); Height = Math.Max(320, h * scale + 76);

        _decoder = new ScreenDecoder(vm.Api, view.Width, view.Height, view.Fps);
        Picture.Source = _decoder.Bitmap;
        _decoder.FrameShown += () => { if (!view.HasPicture) view.HasPicture = true; };
        _decoder.Start();
        view.PropertyChanged += OnViewChanged;
        _watch.Tick += (_, _) =>
        {
            // pictures stopped although the sharing goes on (a still screen keeps sending): say so instead of a frozen picture
            Stalled.Visibility = !view.Ended && view.HasPicture && DateTime.UtcNow - _decoder.LastFrameAt > TimeSpan.FromSeconds(4) ? Visibility.Visible : Visibility.Collapsed;
        };
        _watch.Start();
        KeyDown += OnKey;
        Picture.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) ToggleFull(); };
    }

    private void OnViewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScreenViewVm.Ended) && View.Ended) { _decoder.Dispose(); if (_full) ToggleFull(); }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 || e.Key == Key.F) { ToggleFull(); e.Handled = true; }
        else if (e.Key == Key.Escape && _full) { ToggleFull(); e.Handled = true; }
    }

    private void Full_Click(object sender, RoutedEventArgs e) => ToggleFull();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleFull()
    {
        if (!_full)
        {
            _stateBefore = WindowState;
            WindowStyle = WindowStyle.None;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; // so it covers the taskbar too
            WindowState = WindowState.Maximized;
            StatusBar.Visibility = Visibility.Collapsed;
            _full = true;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBefore;
            StatusBar.Visibility = Visibility.Visible;
            _full = false;
            DarkTitleBar.Apply(this);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _watch.Stop();
        View.PropertyChanged -= OnViewChanged;
        _decoder.Dispose();
        _ = _vm.LeaveAsync(View);
        base.OnClosed(e);
    }
}
