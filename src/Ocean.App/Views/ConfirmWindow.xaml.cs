using System.Windows;
using Ocean.App.Services;

namespace Ocean.App.Views;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string message, string okLabel, string? cancelLabel, bool danger)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = okLabel;
        if (cancelLabel is null) CancelButton.Visibility = Visibility.Collapsed; else CancelButton.Content = cancelLabel;
        if (danger && cancelLabel is not null)
        {
            OkButton.Style = (Style)FindResource("Btn");
            OkButton.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
        }
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Loaded += (_, _) => OkButton.Focus();
    }

    public static bool Show(Window? owner, string title, string message, string okLabel = "OK", string? cancelLabel = "Cancel", bool danger = false)
    {
        var w = new ConfirmWindow(title, message, okLabel, cancelLabel, danger);
        if (owner is not null) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return w.ShowDialog() == true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
