using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Ocean.App.ViewModels;

namespace Ocean.App.Views;

/// <summary>The notes of one video (see <see cref="NotesViewModel"/>); reads them while shown.</summary>
public partial class NotesPanel : UserControl
{
    /// <summary>The panel's own ✕ (beside the player; a window has its own).</summary>
    public event Action? CloseRequested;

    public NotesPanel()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible) Vm?.Opened(); else Vm?.Closed(); };
        DataContextChanged += (_, e) =>
        {
            (e.OldValue as NotesViewModel)?.Closed();
            if (IsVisible) (e.NewValue as NotesViewModel)?.Opened();
        };
    }

    private NotesViewModel? Vm => DataContext as NotesViewModel;

    public bool ShowClose
    {
        get => CloseButton.Visibility == Visibility.Visible;
        set => CloseButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Puts the cursor in the box for a new note.</summary>
    public void FocusDraft() => DraftBox.Focus();

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void NoteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Vote(sender, "up");
    private void Down_Click(object sender, RoutedEventArgs e) => Vote(sender, "down");

    private void Vote(object sender, string dir)
    {
        if (sender is FrameworkElement { DataContext: NoteVm note }) Vm?.VoteCommand.Execute(new object[] { note, dir });
    }
}
