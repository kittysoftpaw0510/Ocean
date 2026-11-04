using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Ocean.App.Services;
using Ocean.App.ViewModels;
using Ocean.Core.Media;
using Ocean.Core.Protocol;

namespace Ocean.App.Views;

public partial class MainWindow : Window, IDialogs
{
    private readonly MainViewModel _vm;
    private bool _forceClose;
    private WindowState _stateBeforeFullscreen;
    private WindowStyle _styleBeforeFullscreen;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        Icon = IconFactory.WindowIcon();
        Logo.Source = IconFactory.Artwork();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        vm.Player.PropertyChanged += Player_PropertyChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        Drop += OnDrop;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
    }

    public void ForceClose() { _vm.Player.IsPip = false; _forceClose = true; Close(); } // quitting: the small window goes too

    /// <summary>
    /// A right-click on a node shows its menu without opening the node: a ListBoxItem would otherwise select itself
    /// on the right button too, and selecting a node navigates to it. The menu still opens (on the button's release).
    /// </summary>
    private void PeerItem_RightButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose)
        {
            e.Cancel = true;
            _vm.Player.CloseForHide(); // a multicast I'm hosting goes on by itself (nothing to ask on the way to the tray)
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            VideoSearchBox.Focus(); VideoSearchBox.SelectAll(); e.Handled = true;
        }
        else if (e.Key == Key.Escape && !_vm.Player.ShowInWindow && VideoSearchBox.IsKeyboardFocused)
        {
            _vm.VideoQuery = ""; Keyboard.ClearFocus(); e.Handled = true;
        }
        else if (e.Key == Key.Escape && !_vm.Player.ShowInWindow && SearchBox.IsKeyboardFocused)
        {
            _vm.SearchText = ""; Keyboard.ClearFocus(); e.Handled = true;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        if (paths.Length == 0) return;
        if (_vm.Page is not SharesViewModel) _vm.ShowShares();
        await _vm.SharesVm.NewShareFromPathsAsync(paths);
    }

    private void Player_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerViewModel.IsFullscreen)) return;
        if (_vm.Player.IsFullscreen)
        {
            _stateBeforeFullscreen = WindowState; _styleBeforeFullscreen = WindowStyle;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal; WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _styleBeforeFullscreen; ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBeforeFullscreen == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        }
    }

    // ------------------------------------------------------------------ IDialogs

    private Window? Owner_ => IsVisible ? this : null;

    public Task<string?> AskPasswordAsync(string title, string subtitle)
        => Task.FromResult(PasswordWindow.Show(Owner_, title, subtitle));

    public void ShowPeerInfo(PeerInfoViewModel info) => PeerInfoWindow.Show(Owner_, info);

    public Task<string?> NewPasswordAsync(string title, string subtitle)
        => Task.FromResult(PasswordWindow.Show(Owner_, title, subtitle, "Set password", confirm: true));

    public Task<bool> ConfirmAsync(string title, string message, string okLabel = "OK", bool danger = false)
        => Task.FromResult(ConfirmWindow.Show(Owner_, title, message, okLabel, "Cancel", danger));

    public Task<int> ChooseAsync(string title, string message, string okLabel, string altLabel, bool altDanger = false)
        => Task.FromResult(ConfirmWindow.ShowChoice(Owner_, title, message, okLabel, altLabel, altDanger));

    public Task<ShareUpsertDto?> EditShareAsync(ShareEditorViewModel editor)
        => Task.FromResult(ShareEditorWindow.Show(Owner_, editor, this));

    public Task<string?> PromptAsync(string title, string message, string initial, string okLabel)
        => Task.FromResult(PromptWindow.Show(Owner_, title, message, initial, okLabel));

    public Task<string?> PickShareFolderAsync(string title, string shareName, IReadOnlyList<string> folders, string current)
        => Task.FromResult(FolderPickerWindow.Show(Owner_, title, shareName, folders, current));

    public string? PickFolder(string title)
    {
        var dlg = new OpenFolderDialog { Title = title, Multiselect = false };
        return dlg.ShowDialog(Owner_) == true ? dlg.FolderName : null;
    }

    public string[]? PickFiles(string title)
    {
        var dlg = new OpenFileDialog { Title = title, Multiselect = true, Filter = VideoTypes.FilterString(), CheckFileExists = true };
        return dlg.ShowDialog(Owner_) == true ? dlg.FileNames : null;
    }

    public void ShowError(string title, string message)
    {
        Services.AppLog.Warn("Dialog", $"{title}: {message}");
        ConfirmWindow.Show(Owner_, title, message, "OK", null, true);
    }
}
