using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastDLX.Services;
using Avalonia.Controls;

namespace FastDLX.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty] private string fastDlUrl = string.Empty;
    [ObservableProperty] private string gameDirectory = string.Empty;
    [ObservableProperty] private string status = "Idle";

    private readonly FastDlService _service = new();

    [RelayCommand]
    public async Task StartUpdateAsync()
    {
        if (string.IsNullOrWhiteSpace(FastDlUrl) || string.IsNullOrWhiteSpace(GameDirectory))
        {
            Status = "Please fill both fields.";
            return;
        }

        Status = "Starting...";
        var progress = new Progress<string>(msg => Status = msg);

        await _service.SyncAsync(FastDlUrl, GameDirectory, progress);

        Status = "Completed!";
    }

    [RelayCommand]
    public async Task BrowseAsync()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Source Game Directory"
        };

        // Open dialog (must pass Window)
        var mainWindow = App.Current.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

        if (mainWindow == null) return;

        string? result = await dlg.ShowAsync(mainWindow);

        if (!string.IsNullOrWhiteSpace(result))
        {
            GameDirectory = result;
        }
    }
}
