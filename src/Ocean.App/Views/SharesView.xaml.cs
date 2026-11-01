using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Ocean.App.ViewModels;

namespace Ocean.App.Views;

public partial class SharesView : UserControl
{
    private const string DragFormat = "Ocean.ItemIds";
    private Point _dragStart;
    private LocalItemVm? _pressedItem;
    private bool _deferredSingleSelect;

    public SharesView()
    {
        InitializeComponent();
        ItemsList.PreviewMouseLeftButtonUp += ItemsList_PreviewMouseLeftButtonUp;
    }

    private SharesViewModel? Vm => DataContext as SharesViewModel;

    // ------------------------------------------------------------------ row menus

    private void Policy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: LocalItemVm item, Tag: string mode } && Vm is not null)
            Vm.SetItemPolicyCommand.Execute(new object[] { item, mode });
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: LocalItemVm item }) Vm?.RevealItemCommand.Execute(item);
    }

    private void Tags_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LocalItemVm item }) Vm?.EditTagsCommand.Execute(item);
    }

    private void MoveTo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LocalItemVm item }) Vm?.MoveToCommand.Execute(item);
    }

    private void FolderOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LocalFolderVm f }) Vm?.EnterFolderCommand.Execute(f);
    }

    private void FolderRename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LocalFolderVm f }) Vm?.RenameFolderCommand.Execute(f);
    }

    private void FolderDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LocalFolderVm f }) Vm?.DeleteFolderCommand.Execute(f);
    }

    private void RowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        var row = FindAncestor<Border>(b, requireMenu: true);
        if (row?.ContextMenu is null) return;
        row.ContextMenu.PlacementTarget = b;
        row.ContextMenu.Placement = PlacementMode.Bottom;
        row.ContextMenu.IsOpen = true;
    }

    // ------------------------------------------------------------------ adding videos from disk

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (AddButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = AddButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static string[]? DroppedFiles(DragEventArgs e)
        => e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    /// <summary>Files from Explorer dropped on an entry of the left list are added to that share.</summary>
    private void ShareItem_DragOver(object sender, DragEventArgs e)
    {
        if (DroppedFiles(e) is null) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void ShareItem_Drop(object sender, DragEventArgs e)
    {
        if (DroppedFiles(e) is not { } files || sender is not FrameworkElement { DataContext: MyShareVm share } || Vm is null) return;
        e.Handled = true; // otherwise the window would create a new share
        await Vm.AddPathsAsync(share, files);
    }

    /// <summary>Files dropped on the open share go into the folder being viewed.</summary>
    private void RightPane_DragOver(object sender, DragEventArgs e)
    {
        if (DroppedFiles(e) is null || Vm?.Selected is null) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void RightPane_Drop(object sender, DragEventArgs e)
    {
        if (DroppedFiles(e) is not { } files || Vm?.Selected is not { } share) return;
        e.Handled = true;
        await Vm.AddPathsAsync(share, files);
    }

    // ------------------------------------------------------------------ list: open, keys, selection

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => Vm?.OnSelectionChanged();

    private void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is LocalFolderVm f && Vm is not null)
        {
            Vm.EnterFolderCommand.Execute(f);
            e.Handled = true;
        }
        else if ((e.OriginalSource as FrameworkElement)?.DataContext is LocalItemVm item && Vm is not null)
        {
            Vm.PlayCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: LocalItemVm item }) Vm?.PlayCommand.Execute(item);
    }

    private void StartCast_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: LocalItemVm item }) Vm?.StartCastCommand.Execute(item);
    }

    private void StopCast_Click(object sender, RoutedEventArgs e) => Vm?.StopCastCommand.Execute(null);

    private void ItemsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        if (e.Key == Key.Enter && ItemsList.SelectedItem is LocalFolderVm f) { Vm.EnterFolderCommand.Execute(f); e.Handled = true; }
        else if (e.Key == Key.Enter && ItemsList.SelectedItem is LocalItemVm it) { Vm.PlayCommand.Execute(it); e.Handled = true; }
        else if (e.Key == Key.Back && Vm.FolderMode) { Vm.UpCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.F2 && ItemsList.SelectedItem is LocalFolderVm rf) { Vm.RenameFolderCommand.Execute(rf); e.Handled = true; }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { ItemsList.SelectAll(); e.Handled = true; }
    }

    // ------------------------------------------------------------------ drag videos onto folders

    private void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _pressedItem = (e.OriginalSource as FrameworkElement)?.DataContext as LocalItemVm;
        _deferredSingleSelect = false;
        // Pressing an already-selected video must not collapse a multi-selection before a drag can start.
        if (_pressedItem is { IsSelected: true } && e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None
            && FindAncestor<ButtonBase>((DependencyObject)e.OriginalSource) is null)
        {
            _deferredSingleSelect = true;
            e.Handled = true;
        }
    }

    private void ItemsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferredSingleSelect && _pressedItem is not null)
        {
            // It was a click, not a drag: behave like a normal click (select just this one).
            ItemsList.SelectedItems.Clear();
            ItemsList.SelectedItem = _pressedItem;
        }
        _deferredSingleSelect = false;
    }

    private void ItemsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressedItem is null || Vm is null) return;
        var d = e.GetPosition(null) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var items = ItemsList.SelectedItems.OfType<LocalItemVm>().ToList();
        if (!items.Contains(_pressedItem)) items = new() { _pressedItem };
        var ids = items.Select(i => i.Id).ToArray();
        _deferredSingleSelect = false;
        _pressedItem = null;
        DragDrop.DoDragDrop(ItemsList, new DataObject(DragFormat, ids), DragDropEffects.Move);
    }

    private static string[]? DraggedIds(DragEventArgs e) => e.Data.GetDataPresent(DragFormat) ? e.Data.GetData(DragFormat) as string[] : null;

    private void Folder_DragOver(object sender, DragEventArgs e)
    {
        if (DraggedIds(e) is null) return; // e.g. files from Explorer: let the window create a share
        e.Effects = DragDropEffects.Move;
        if (sender is FrameworkElement { DataContext: LocalFolderVm f }) f.IsDropTarget = true;
        e.Handled = true;
    }

    private void Folder_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LocalFolderVm f }) f.IsDropTarget = false;
    }

    private async void Folder_Drop(object sender, DragEventArgs e)
    {
        if (DraggedIds(e) is not { } ids || sender is not FrameworkElement { DataContext: LocalFolderVm f } || Vm is null) return;
        e.Handled = true;
        f.IsDropTarget = false;
        await Vm.MoveItemsAsync(ids.ToList(), f.Path);
    }

    /// <summary>Dropping on a breadcrumb segment moves the videos up to that folder.</summary>
    private async void Crumb_Drop(object sender, DragEventArgs e)
    {
        if (DraggedIds(e) is not { } ids || sender is not FrameworkElement { Tag: string path } || Vm is null) return;
        e.Handled = true;
        if (string.Equals(path, Vm.CurrentFolder, StringComparison.OrdinalIgnoreCase)) return; // already here
        await Vm.MoveItemsAsync(ids.ToList(), path);
    }

    private static T? FindAncestor<T>(DependencyObject? d, bool requireMenu = false) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t && (!requireMenu || t is not Border bd || bd.ContextMenu is not null)) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
