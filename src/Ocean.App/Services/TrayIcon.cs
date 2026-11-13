using System.Drawing;
using System.Windows.Forms;

namespace Ocean.App.Services;

/// <summary>System tray presence: menu, balloon notifications, "new videos" badge.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _pause;
    private static readonly int TraySize = Math.Max(16, SystemInformation.SmallIconSize.Width);
    private readonly Icon _plain = IconFactory.CreateIcon(TraySize);
    private readonly Icon _badged = IconFactory.CreateIcon(TraySize, badge: true);
    private readonly Icon _recording = IconFactory.CreateIcon(TraySize, recording: true);
    private readonly Icon _recordingPaused = IconFactory.CreateIcon(TraySize, recording: true, paused: true);
    private readonly ToolStripMenuItem _record, _recordPause, _recordStop, _recordAgain;
    private bool _badge;
    private bool _isRecording, _isPaused;
    private Action? _balloonAction;

    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action? ExitAndStopRequested;
    public event Action<bool>? PauseToggled;
    /// <summary>"Record my screen…": the picker opens (Ocean's window stays as it is).</summary>
    public event Action? RecordRequested;
    public event Action? RecordPauseRequested;
    public event Action? RecordStopRequested;
    public event Action? RecordAgainRequested;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open Ocean", null, (_, _) => OpenRequested?.Invoke()) { Font = new Font(Control.DefaultFont, FontStyle.Bold) });
        menu.Items.Add(new ToolStripMenuItem("Settings", null, (_, _) => SettingsRequested?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        // recording, straight from here: no need to open Ocean's window
        _record = new ToolStripMenuItem("Record my screen…", null, (_, _) => RecordRequested?.Invoke());
        _recordStop = new ToolStripMenuItem("Stop recording", null, (_, _) => RecordStopRequested?.Invoke()) { Font = new Font(Control.DefaultFont, FontStyle.Bold), Visible = false };
        _recordPause = new ToolStripMenuItem("Pause recording", null, (_, _) => RecordPauseRequested?.Invoke()) { Visible = false };
        _recordAgain = new ToolStripMenuItem("Record the same again", null, (_, _) => RecordAgainRequested?.Invoke()) { Visible = false };
        menu.Items.Add(_record); menu.Items.Add(_recordAgain); menu.Items.Add(_recordStop); menu.Items.Add(_recordPause);
        menu.Items.Add(new ToolStripSeparator());
        _pause =new ToolStripMenuItem("Pause sharing", null, (_, _) => PauseToggled?.Invoke(!_pause!.Checked)) { CheckOnClick = false };
        menu.Items.Add(_pause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit (keep sharing in background)", null, (_, _) => ExitRequested?.Invoke()));
        menu.Items.Add(new ToolStripMenuItem("Exit and stop sharing", null, (_, _) => ExitAndStopRequested?.Invoke()));

        _icon = new NotifyIcon { Icon = _plain, Text = "Ocean", Visible = true, ContextMenuStrip = menu };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenRequested?.Invoke(); };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) =>
        {
            var a = _balloonAction;
            _balloonAction = null;
            if (a is not null) a(); else OpenRequested?.Invoke();
        };
    }

    public void SetText(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void SetBadge(bool on)
    {
        if (_badge == on) return;
        _badge = on;
        ApplyIcon();
    }

    /// <summary>A recording runs: the menu offers Stop and Pause / Go on (with the time), the icon carries a red dot (amber while paused).</summary>
    /// <param name="again">"Record the same again: …" (null: nothing to repeat).</param>
    public void SetRecording(bool recording, bool paused, string elapsed, string? again = null)
    {
        _record.Visible = !recording;
        _recordAgain.Visible = !recording && again is not null;
        if (again is not null) _recordAgain.Text = again.Length > 60 ? again[..60] + "…" : again;
        _recordStop.Visible = recording; _recordPause.Visible = recording;
        _recordStop.Text = recording ? $"Stop recording  ({elapsed})" : "Stop recording";
        _recordPause.Text = paused ? "Go on recording" : "Pause recording";
        if (_isRecording == recording && _isPaused == paused) return;
        _isRecording = recording; _isPaused = paused;
        ApplyIcon();
    }

    private void ApplyIcon() => _icon.Icon = _isRecording ? (_isPaused ? _recordingPaused : _recording) : _badge ? _badged : _plain;

    public void SetPaused(bool paused) => _pause.Checked = paused;

    public void Notify(string title, string text, Action? onClick = null, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _balloonAction = onClick;
        _icon.ShowBalloonTip(5000, title, string.IsNullOrEmpty(text) ? " " : text, icon);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _plain.Dispose();
        _badged.Dispose();
        _recording.Dispose();
        _recordingPaused.Dispose();
    }
}
