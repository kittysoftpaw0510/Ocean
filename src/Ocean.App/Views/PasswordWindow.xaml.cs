using System.Windows;
using System.Windows.Input;
using Ocean.App.Services;

namespace Ocean.App.Views;

public partial class PasswordWindow : Window
{
    public string? Result { get; private set; }

    public PasswordWindow(string title, string subtitle)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Loaded += (_, _) => Box.Focus();
    }

    public static string? Show(Window? owner, string title, string subtitle)
    {
        var w = new PasswordWindow(title, subtitle);
        if (owner is not null) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowDialog();
        return w.Result;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) { Result = Box.Password; DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { Result = null; DialogResult = false; }
    private void Box_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Ok_Click(sender, e); }
}
