using System.Windows;
using System.Windows.Input;
using Ocean.App.Services;

namespace Ocean.App.Views;

/// <summary>
/// Asks for a password: to unlock someone else's video ("Unlock", typed once), or to set one on my own video
/// (<c>confirm</c>: "Set password", typed twice and never empty).
/// </summary>
public partial class PasswordWindow : Window
{
    private readonly bool _confirm;
    public string? Result { get; private set; }

    public PasswordWindow(string title, string subtitle, string okLabel = "Unlock", bool confirm = false)
    {
        InitializeComponent();
        _confirm = confirm;
        Title = title;
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        OkButton.Content = okLabel;
        if (confirm) ConfirmPanel.Visibility = Visibility.Visible;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Loaded += (_, _) => Box.Focus();
    }

    public static string? Show(Window? owner, string title, string subtitle, string okLabel = "Unlock", bool confirm = false)
    {
        var w = new PasswordWindow(title, subtitle, okLabel, confirm);
        if (owner is not null) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowDialog();
        return w.Result;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_confirm)
        {
            if (Box.Password.Length == 0) { ShowError("Enter a password."); Box.Focus(); return; }
            if (Box.Password != ConfirmBox.Password)
            {
                ShowError("The two passwords don't match. Type them again.");
                ConfirmBox.Clear();
                ConfirmBox.Focus();
                return;
            }
        }
        Result = Box.Password;
        DialogResult = true;
    }

    private void ShowError(string text) { ErrorText.Text = text; ErrorText.Visibility = Visibility.Visible; }

    private void Cancel_Click(object sender, RoutedEventArgs e) { Result = null; DialogResult = false; }

    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_confirm && ConfirmBox.Password.Length == 0) ConfirmBox.Focus(); // Enter moves on to the second box first
        else Ok_Click(sender, e);
    }

    private void ConfirmBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Ok_Click(sender, e);
    }
}
