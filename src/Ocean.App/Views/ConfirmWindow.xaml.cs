using System.Windows;
using Ocean.App.Services;

namespace Ocean.App.Views;

public partial class ConfirmWindow : Window
{
    private int _choice = 1;

    public ConfirmWindow(string title, string message, string okLabel, string? cancelLabel, bool danger, string? altLabel = null, bool altDanger = false)
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
        if (altLabel is not null)
        {
            AltButton.Content = altLabel;
            AltButton.Visibility = Visibility.Visible;
            if (altDanger) AltButton.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
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

    /// <summary>Two ways forward and Cancel: 1 = ok, 2 = alt, 0 = cancelled (Cancel, Esc or the close box).</summary>
    public static int ShowChoice(Window? owner, string title, string message, string okLabel, string altLabel, bool altDanger = false)
    {
        var w = new ConfirmWindow(title, message, okLabel, "Cancel", false, altLabel, altDanger);
        if (owner is not null) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return w.ShowDialog() == true ? w._choice : 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) { _choice = 1; DialogResult = true; }
    private void Alt_Click(object sender, RoutedEventArgs e) { _choice = 2; DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
