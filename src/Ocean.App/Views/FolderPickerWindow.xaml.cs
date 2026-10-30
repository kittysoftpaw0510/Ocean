using System.Windows;
using System.Windows.Input;
using Ocean.App.Services;
using Ocean.Core.Naming;

namespace Ocean.App.Views;

/// <summary>Picks a destination folder inside a share (tree shown as an indented list).</summary>
public partial class FolderPickerWindow : Window
{
    public sealed record Entry(string Path, string Name, int Depth, bool IsCurrent)
    {
        public Thickness Indent => new(Depth * 18, 0, 0, 0);
        public string Glyph => Depth == 0 ? "" : "";
    }

    private readonly string _shareName;
    private readonly string _current;
    private readonly List<string> _folders;
    public string? Result { get; private set; }

    public FolderPickerWindow(string title, string shareName, IReadOnlyList<string> folders, string current)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        _shareName = shareName;
        _current = current;
        _folders = folders.ToList();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Refresh(select: current);
    }

    private void Refresh(string select)
    {
        var entries = new List<Entry> { new("", _shareName, 0, _current.Length == 0) };
        entries.AddRange(_folders.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => new Entry(f, FolderPath.Name(f), f.Count(c => c == '/') + 1, string.Equals(f, _current, StringComparison.OrdinalIgnoreCase))));
        List.ItemsSource = entries;
        List.SelectedItem = entries.FirstOrDefault(e => string.Equals(e.Path, select, StringComparison.OrdinalIgnoreCase)) ?? entries[0];
        List.ScrollIntoView(List.SelectedItem);
    }

    public static string? Show(Window? owner, string title, string shareName, IReadOnlyList<string> folders, string current)
    {
        var w = new FolderPickerWindow(title, shareName, folders, current);
        if (owner is not null) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowDialog();
        return w.Result;
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var parent = (List.SelectedItem as Entry)?.Path ?? "";
        var name = PromptWindow.Show(this, "New folder", parent.Length == 0 ? $"Create a folder in “{_shareName}”" : $"Create a folder in “{FolderPath.Name(parent)}”", "", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        var path = FolderPath.Combine(parent, name.Replace('/', '-').Replace('\\', '-'));
        if (path.Length == 0) return;
        _folders.Add(path); // created for real when the move happens
        Refresh(select: path);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not Entry entry) return;
        Result = entry.Path;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Entry) Ok_Click(sender, e);
    }
}
