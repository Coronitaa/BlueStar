using Avalonia.Controls;
using Avalonia.Platform.Storage;
using BlueStar.Linux.ViewModels;

namespace BlueStar.Linux.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.Library.PickFolder = PickFolderAsync;
            vm.Requirements.Check();
            await vm.Library.LoadAsync();
            await SelfTest.OnWindowReadyAsync(this, vm);
        };
    }

    private async Task<string?> PickFolderAsync()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false, Title = L.AddFolder });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }
}
