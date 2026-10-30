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
    private bool _badge;
    private Action? _balloonAction;

    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action? ExitAndStopRequested;
    public event Action<bool>? PauseToggled;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open Ocean", null, (_, _) => OpenRequested?.Invoke()) { Font = new Font(Control.DefaultFont, FontStyle.Bold) });
        menu.Items.Add(new ToolStripMenuItem("Settings", null, (_, _) => SettingsRequested?.Invoke()));
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
        _icon.Icon = on ? _badged : _plain;
    }

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
    }
}
