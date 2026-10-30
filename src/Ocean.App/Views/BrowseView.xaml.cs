using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Ocean.App.ViewModels;

namespace Ocean.App.Views;

public partial class BrowseView : UserControl
{
    public BrowseView() { InitializeComponent(); }

    private BrowseViewModel? Vm => DataContext as BrowseViewModel;

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || Vm is null) return;
        switch ((e.OriginalSource as FrameworkElement)?.DataContext)
        {
            case ItemVm item: Vm.PlayCommand.Execute(item); e.Handled = true; break;
            case FolderVm folder: Vm.OpenFolderCommand.Execute(folder); e.Handled = true; break;
        }
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => Vm?.OnSelectionChanged();

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        if (e.Key == Key.Enter && List.SelectedItem is ItemVm item) { Vm.PlayCommand.Execute(item); e.Handled = true; }
        else if (e.Key == Key.Enter && List.SelectedItem is FolderVm folder) { Vm.OpenFolderCommand.Execute(folder); e.Handled = true; }
        else if (e.Key == Key.Back && Vm.FolderMode) { Vm.UpCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { List.SelectAll(); e.Handled = true; }
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var menu = SortButton.ContextMenu;
        if (menu is null) return;
        menu.PlacementTarget = SortButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Chips_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv) { sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta); e.Handled = true; }
    }

    private void ChipDownload_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is ShareChipVm chip) Vm?.DownloadShareCommand.Execute(chip);
    }

    private void ChipUnlock_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is ShareChipVm chip) Vm?.UnlockCommand.Execute(chip);
    }
}
