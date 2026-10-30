using System.Windows;
using System.Windows.Input;
using Ocean.App.Services;

namespace Ocean.App.Views;

/// <summary>Asks for a short text such as a folder name.</summary>
public partial class PromptWindow : Window
{
    public string? Result { get; private set; }

    public PromptWindow(string title, string message, string initial, string okLabel)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        Box.Text = initial;
        OkButton.Content = okLabel;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Loaded += (_, _) => { Box.Focus(); Box.SelectAll(); };
    }

    public static string? Show(Window? owner, string title, string message, string initial, string okLabel)
    {
        var w = new PromptWindow(title, message, initial, okLabel);
        if (owner is not null) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowDialog();
        return w.Result;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var text = Box.Text.Trim();
        if (text.Length == 0) { Box.Focus(); return; }
        Result = text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Box_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Ok_Click(sender, e); }
}
