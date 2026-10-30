using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Ocean.App.ViewModels;

namespace Ocean.App.Views;

public partial class SettingsView : UserControl
{
    private bool _syncing;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SettingsViewModel old) old.PropertyChanged -= Vm_PropertyChanged;
        if (e.NewValue is SettingsViewModel vm)
        {
            vm.PropertyChanged += Vm_PropertyChanged;
            SyncFromVm(vm);
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.Secret) && sender is SettingsViewModel vm) SyncFromVm(vm);
    }

    private void SyncFromVm(SettingsViewModel vm)
    {
        if (_syncing || SecretBox.Password == vm.Secret) return;
        _syncing = true;
        try { SecretBox.Password = vm.Secret; } finally { _syncing = false; }
    }

    private void SecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || DataContext is not SettingsViewModel vm) return;
        _syncing = true;
        try { vm.Secret = SecretBox.Password; } finally { _syncing = false; }
    }

    private void ToggleSecret_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.ShowSecret = !vm.ShowSecret;
    }
}
