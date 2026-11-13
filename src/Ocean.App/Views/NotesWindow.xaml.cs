using System.Windows;
using Ocean.App.ViewModels;

namespace Ocean.App.Views;

/// <summary>The notes of one video, from its menu: one window per video (asking again brings it forward).</summary>
public partial class NotesWindow : Window
{
    private static readonly Dictionary<string, NotesWindow> Open = new();
    private readonly string _key;

    private NotesWindow(NotesViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        _key = vm.PeerId + "|" + vm.ItemId;
        Title = "Notes  ·  " + vm.ItemName;
        SourceInitialized += (_, _) => Services.DarkTitleBar.Apply(this);
        Closed += (_, _) => { Open.Remove(_key); vm.Closed(); };
    }

    public static void Show(Window? owner, NotesViewModel vm)
    {
        if (Open.TryGetValue(vm.PeerId + "|" + vm.ItemId, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var w = new NotesWindow(vm);
        if (owner is not null) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Open[w._key] = w;
        w.Show();
        w.Panel.FocusDraft();
    }
}
