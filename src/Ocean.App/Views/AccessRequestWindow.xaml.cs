using System.Collections.Specialized;
using System.Windows;
using Ocean.App.Services;
using Ocean.App.ViewModels;

namespace Ocean.App.Views;

public partial class AccessRequestWindow : Window
{
    private static readonly Dictionary<string, AccessRequestWindow> Open = new();
    private readonly MainViewModel _main;
    private readonly PendingAccessVm _req;

    private AccessRequestWindow(MainViewModel main, PendingAccessVm req)
    {
        _main = main; _req = req;
        InitializeComponent();
        DataContext = req;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Icon = IconFactory.WindowIcon();
        main.Pending.CollectionChanged += Pending_Changed;
        Closed += (_, _) => { main.Pending.CollectionChanged -= Pending_Changed; Open.Remove(req.RequestId); };
    }

    private void Pending_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_main.Pending.Contains(_req)) Close(); // answered elsewhere (banner) or expired
    }

    public static void Show(MainViewModel main, PendingAccessVm req, Window? owner)
    {
        if (Open.TryGetValue(req.RequestId, out var existing)) { existing.Activate(); return; }
        var w = new AccessRequestWindow(main, req);
        Open[req.RequestId] = w;
        if (owner is { IsVisible: true }) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        w.Show();
        w.Activate();
    }

    private async void Deny_Click(object sender, RoutedEventArgs e) { await _main.DecideAsync(_req, "deny"); Close(); }
    private async void Once_Click(object sender, RoutedEventArgs e) { await _main.DecideAsync(_req, "once"); Close(); }
    private async void Always_Click(object sender, RoutedEventArgs e) { await _main.DecideAsync(_req, "always"); Close(); }
}
