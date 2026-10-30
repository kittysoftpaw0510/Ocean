using System.Windows;
using System.Windows.Controls;
using Ocean.App.Services;
using Ocean.App.ViewModels;
using Ocean.Core.Media;
using Ocean.Core.Protocol;

namespace Ocean.App.Views;

public partial class ShareEditorWindow : Window
{
    private readonly ShareEditorViewModel _vm;
    private readonly IDialogs _dialogs;
    public ShareUpsertDto? Result { get; private set; }

    public ShareEditorWindow(ShareEditorViewModel vm, IDialogs dialogs)
    {
        _vm = vm; _dialogs = dialogs;
        InitializeComponent();
        DataContext = vm;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
        Drop += OnDrop;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
    }

    public static ShareUpsertDto? Show(Window? owner, ShareEditorViewModel vm, IDialogs dialogs)
    {
        var w = new ShareEditorWindow(vm, dialogs);
        if (owner is not null) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowDialog();
        return w.Result;
    }

    private void AddRoots(IEnumerable<string> paths)
    {
        var wasEmpty = _vm.Roots.Count == 0;
        foreach (var p in paths)
        {
            if (!(Directory.Exists(p) || (File.Exists(p) && VideoTypes.IsVideo(p)))) continue;
            if (_vm.Roots.Any(r => string.Equals(r, p, StringComparison.OrdinalIgnoreCase))) continue;
            _vm.Roots.Add(p);
        }
        if (wasEmpty && string.IsNullOrWhiteSpace(_vm.Name) && _vm.Roots.Count > 0) _vm.Name = _vm.SuggestName();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) AddRoots((string[])e.Data.GetData(DataFormats.FileDrop)!);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var f = _dialogs.PickFolder("Choose a folder to share");
        if (f is not null) AddRoots(new[] { f });
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var files = _dialogs.PickFiles("Choose videos to share");
        if (files is not null) AddRoots(files);
    }

    private void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is string root) _vm.Roots.Remove(root);
    }

    private void Password_Changed(object sender, RoutedEventArgs e) => _vm.Password = PasswordBox.Password;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dto = _vm.Build();
        if (dto is null) return;
        Result = dto;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
