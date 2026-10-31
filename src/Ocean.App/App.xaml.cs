using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Ocean.App.Services;
using Ocean.App.ViewModels;
using Ocean.App.Views;
using Ocean.Core;

namespace Ocean.App;

public partial class App : Application
{
    // One Ocean app per device (see SingleInstance). The "show" event is per session: a second launch in the
    // same session brings the running window forward.
    private static readonly string ShowEventName = @"Local\Ocean.App.ShowWindow" + SingleInstance.Scope;

    private SingleInstance? _instance;
    private EventWaitHandle? _showEvent;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.Start(e.Args);
        AppLog.Info("App", "networking: " + NetworkCompat.Describe());
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Warn("App", "background task failed (ignored)", args.Exception.InnerException ?? args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("App", "app crashed", args.ExceptionObject as Exception);

        _instance = new SingleInstance("Ocean.App");
        if (!_instance.IsFirst)
        {
            AppLog.Info("App", _instance.OwnedByOtherUser
                ? "another user already has Ocean open on this PC; exiting"
                : "already running in this session; bringing that window forward and exiting");
            var shown = false;
            if (!_instance.OwnedByOtherUser)
            {
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); shown = true; } catch { }
            }
            if (!shown)
            {
                // Running in another user's session on this PC; windows cannot cross sessions.
                MessageBox.Show(
                    "Ocean is already open for another user on this PC.\n\n" +
                    "Only one copy of Ocean runs per device. Sharing continues in the background either way; " +
                    "ask that user to close Ocean (tray icon › Exit) if you want to use it here.",
                    "Ocean is already running", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _ = Task.Run(() =>
        {
            while (_showEvent.WaitOne()) Dispatcher.InvokeAsync(ShowWindow);
        });

        var minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        try
        {
            var (api, status, spawned) = await ServiceConnector.ConnectAsync(null, CancellationToken.None);
            AppLog.Info("App", $"connected to the service: Ocean {status.Version}, {(status.RunningAsService ? "Windows service" : spawned ? "background process started by this app" : "background process")}, " +
                               $"node {status.NodeName} ({status.UserName})");
            _vm = new MainViewModel(api);
            _window = new MainWindow(_vm);
            _vm.Dialogs = _window;
            _tray = new TrayIcon();
            WireTray();
            await _vm.InitializeAsync(status);
            UpdateTray();
            _vm.Peers.CollectionChanged += (_, _) => UpdateTray();
            _vm.PropertyChanged += (_, args) => { if (args.PropertyName is nameof(MainViewModel.TotalNew) or nameof(MainViewModel.SharingPaused) or nameof(MainViewModel.StatusText)) UpdateTray(); };
            if (!minimized) ShowWindow();
            else _tray.Notify("Ocean is running", "Sharing in the background. Click the tray icon to open.");
            // --page shares|downloads|settings opens a specific page (also used by shortcuts).
            var pageIndex = Array.FindIndex(e.Args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
            if (pageIndex >= 0 && pageIndex + 1 < e.Args.Length) OpenPage(e.Args[pageIndex + 1]);
        }
        catch (Exception ex)
        {
            AppLog.Error("App", "could not start", ex);
            MessageBox.Show(ex.Message, "Ocean could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void WireTray()
    {
        if (_tray is null || _vm is null) return;
        _tray.OpenRequested += () => Dispatcher.InvokeAsync(ShowWindow);
        _tray.SettingsRequested += () => Dispatcher.InvokeAsync(() => { ShowWindow(); OpenPage("settings"); });
        _tray.ExitRequested += () => Dispatcher.InvokeAsync(() => ExitApp(stopService: false));
        _tray.ExitAndStopRequested += () => Dispatcher.InvokeAsync(() => ExitApp(stopService: true));
        _tray.PauseToggled += _ => Dispatcher.InvokeAsync(() => _vm.TogglePauseCommand.Execute(null));
        _vm.TrayNotify += (title, text, click) => Dispatcher.InvokeAsync(() =>
        {
            if (_window is { IsVisible: true, IsActive: true }) return; // the in-app toast is enough
            _tray.Notify(title, text, () => { ShowWindow(); click?.Invoke(); });
        });
        _vm.AccessRequested += req => Dispatcher.InvokeAsync(() => AccessRequestWindow.Show(_vm, req, _window));
    }

    private void OpenPage(string page)
    {
        if (_vm is null) return;
        switch (page.ToLowerInvariant())
        {
            case "shares": _vm.ShowShares(); break;
            case "downloads": _vm.ShowDownloads(); break;
            case "settings": _vm.ShowSettings(); break;
            default: _vm.ShowHome(); break;
        }
    }

    private void UpdateTray()
    {
        if (_tray is null || _vm is null) return;
        _tray.SetBadge(_vm.TotalNew > 0);
        _tray.SetPaused(_vm.SharingPaused);
        _tray.SetText("Ocean: " + _vm.StatusText + (_vm.TotalNew > 0 ? $"  ·  {_vm.TotalNew} new" : ""));
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true; _window.Topmost = false;
    }

    public async void ExitApp(bool stopService)
    {
        if (_exiting) return;
        _exiting = true;
        AppLog.Info("App", stopService ? "exit requested (and stop sharing)" : "exit requested");
        try
        {
            if (stopService && _vm is not null)
            {
                // The installed Windows service keeps running for other users and at boot; "stop sharing"
                // therefore pauses sharing (resume from the tray or the sidebar). A private background
                // process is simply shut down.
                if (_vm.Status?.RunningAsService == true)
                    try { await _vm.Api.PostAsync("/api/sharing/pause?paused=true"); } catch { }
                else
                    try { await _vm.Api.PostAsync("/api/shutdown"); } catch { }
            }
        }
        finally
        {
            _vm?.Shutdown();
            _window?.ForceClose();
            _tray?.Dispose();
            Shutdown();
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        AppLog.Error("App", "unhandled error in the window (kept running)", e.Exception);
        _vm?.Toast("Something went wrong", e.Exception.Message, "error");
        Debug.WriteLine(e.Exception);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("App", $"exit (code {e.ApplicationExitCode})");
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
