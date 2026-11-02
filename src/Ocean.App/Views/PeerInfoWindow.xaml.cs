using System.Windows;
using System.Windows.Threading;
using Ocean.App.Services;
using Ocean.App.ViewModels;

namespace Ocean.App.Views;

/// <summary>"Node info": everything this PC knows about one node (opened from the node list's right-click menu).</summary>
public partial class PeerInfoWindow : Window
{
    private readonly PeerInfoViewModel _vm;
    private readonly DispatcherTimer _copied = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public PeerInfoWindow(PeerInfoViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _copied.Tick += (_, _) => { _copied.Stop(); CopiedText.Visibility = Visibility.Collapsed; };
    }

    public static void Show(Window? owner, PeerInfoViewModel vm)
    {
        var w = new PeerInfoWindow(vm);
        if (owner is not null) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowDialog();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is InfoRow { Copy: { } text }) SetClipboard(text);
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e) => SetClipboard(_vm.AsText());

    private void SetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            CopiedText.Visibility = Visibility.Visible;
            _copied.Stop(); _copied.Start();
        }
        catch (Exception ex) { AppLog.Warn("NodeInfo", "copy to the clipboard", ex); } // another app holds the clipboard
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
