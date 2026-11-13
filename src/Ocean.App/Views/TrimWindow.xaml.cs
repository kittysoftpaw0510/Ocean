using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Ocean.App.Services;

namespace Ocean.App.Views;

/// <summary>
/// Trim a recording: watch it, set where the copy starts and ends (buttons, or I and O), save. The original stays.
/// Space plays and pauses; the arrow keys step a second (with Shift a tenth).
/// </summary>
public partial class TrimWindow : Window
{
    private readonly string _path;
    private readonly Action<RecordingTrim.Result, double> _done;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private double _length, _start, _end;
    private bool _playing, _dragging, _fromPlayer, _saving;
    private CancellationTokenSource? _cts;

    public TrimWindow(string path, double duration, Action<RecordingTrim.Result, double> done)
    {
        _path = path; _done = done; _length = duration; _end = duration;
        InitializeComponent();
        Title = "Trim  ·  " + System.IO.Path.GetFileName(path);
        try { Icon = IconFactory.WindowIcon(); } catch { }
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _tick.Tick += (_, _) => FromPlayer();
        Player.Source = new Uri(path);
        Player.Play(); Player.Pause(); // opens it and shows the first picture
        Timeline.Maximum = Math.Max(0.1, duration);
        Refresh();
        Closed += (_, _) => { _tick.Stop(); _cts?.Cancel(); try { Player.Close(); } catch { } };
    }

    private void Player_Opened(object sender, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan)
        {
            var d = Player.NaturalDuration.TimeSpan.TotalSeconds;
            if (Math.Abs(d - _length) > 0.5 || _end > d) { if (_end >= _length - 0.01 || _end > d) _end = d; _length = d; }
            Timeline.Maximum = Math.Max(0.1, _length);
        }
        Refresh();
    }

    private void Player_Ended(object sender, RoutedEventArgs e) { Pause(); Player.Position = TimeSpan.FromSeconds(_start); FromPlayer(); }

    private void Player_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        Problem.Text = "This file can't be shown here (" + e.ErrorException.Message + "). It can still be trimmed: set the start and the end with the timeline.";
        Problem.Visibility = Visibility.Visible;
    }

    private void FromPlayer()
    {
        if (_dragging) return;
        _fromPlayer = true;
        Timeline.Value = Player.Position.TotalSeconds;
        _fromPlayer = false;
        // playing the kept part: it stops at its end
        if (_playing && Player.Position.TotalSeconds >= _end) { Pause(); Player.Position = TimeSpan.FromSeconds(_end); }
        Refresh();
    }

    private void Timeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_fromPlayer) return;
        Player.Position = TimeSpan.FromSeconds(e.NewValue);
        Refresh();
    }

    private void Timeline_Down(object sender, MouseButtonEventArgs e) => _dragging = true;
    private void Timeline_Up(object sender, MouseButtonEventArgs e) { _dragging = false; Player.Position = TimeSpan.FromSeconds(Timeline.Value); }

    private void Play_Click(object sender, RoutedEventArgs e) { if (_playing) Pause(); else Play(); }

    private void Play()
    {
        if (Player.Position.TotalSeconds >= _end - 0.05 || Player.Position.TotalSeconds < _start) Player.Position = TimeSpan.FromSeconds(_start);
        Player.Play(); _playing = true; _tick.Start(); PlayButton.Content = "";
    }

    private void Pause() { Player.Pause(); _playing = false; _tick.Stop(); PlayButton.Content = ""; }

    private void SetStart_Click(object sender, RoutedEventArgs e) => SetStart();
    private void SetEnd_Click(object sender, RoutedEventArgs e) => SetEnd();

    private void SetStart()
    {
        _start = Math.Min(Timeline.Value, Math.Max(0, _end - 0.5));
        Refresh();
    }

    private void SetEnd()
    {
        _end = Math.Max(Timeline.Value, Math.Min(_length, _start + 0.5));
        Refresh();
    }

    private static string T(double s) => TimeSpan.FromSeconds(Math.Max(0, s)) is var t && t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"m\:ss\.f");

    private void Refresh()
    {
        TimeText.Text = $"{T(Timeline.Value)} / {T(_length)}";
        var kept = _end - _start;
        RangeText.Text = _start <= 0.05 && _end >= _length - 0.05 ? "Set where the copy starts and ends"
            : $"{T(_start)}  →  {T(_end)}   ({T(kept)} kept, {T(_length - kept)} cut off)";
        // the lit band
        var w = Band.ActualWidth;
        if (w > 0 && _length > 0)
        {
            double a = w * _start / _length, b = w * _end / _length;
            Canvas.SetLeft(Kept, a);
            Kept.Width = Math.Max(2, b - a);
            Canvas.SetLeft(StartMark, a - 2);
            Canvas.SetLeft(EndMark, b - 2);
        }
        Hint.Text = _start <= 0.05
            ? "A new file is saved next to it; the original stays as it is. Cutting only the end is instant and keeps the quality exactly."
            : "A new file is saved next to it; the original stays as it is. With a new start the copy is encoded again (a moment per minute of video).";
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) { base.OnRenderSizeChanged(sizeInfo); Refresh(); }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_saving) return;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 1;
        switch (e.Key)
        {
            case Key.Space: if (_playing) Pause(); else Play(); break;
            case Key.I: SetStart(); break;
            case Key.O: SetEnd(); break;
            case Key.Left: Timeline.Value = Math.Max(0, Timeline.Value - step); break;
            case Key.Right: Timeline.Value = Math.Min(_length, Timeline.Value + step); break;
            case Key.Escape: Close(); break;
            default: base.OnPreviewKeyDown(e); return;
        }
        e.Handled = true;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        if (_start <= 0.05 && _end >= _length - 0.05) { Hint.Text = "Nothing is cut off yet: set the start or the end first."; return; }
        Pause();
        _saving = true;
        SaveButton.IsEnabled = false; SaveButton.Content = "Saving…";
        Progress.Visibility = Visibility.Visible; Progress.Value = 0;
        _cts = new CancellationTokenSource();
        var r = await RecordingTrim.TrimAsync(_path, _start, _end, _length, p => Dispatcher.BeginInvoke(() => Progress.Value = p), _cts.Token);
        _saving = false;
        if (r.Ok) { _done(r, _end - _start); Close(); return; }
        if (r.Error is null) { Close(); return; } // cancelled
        SaveButton.IsEnabled = true; SaveButton.Content = "Save trimmed copy";
        Progress.Visibility = Visibility.Collapsed;
        Hint.Text = "The copy couldn't be saved: " + r.Error;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
