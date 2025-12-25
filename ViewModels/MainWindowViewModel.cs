using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastDLX.Services;
using FastDLX.Models;
using Avalonia.Controls;

namespace FastDLX.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty] private string fastDlUrl = "https://fastdl.nide.gg/css_ze/";
    [ObservableProperty] private double progress;
    [ObservableProperty] private string gameDirectory = string.Empty;
    [ObservableProperty] private string status = "Idle";
    [ObservableProperty] private string warning = string.Empty;
    [ObservableProperty] private ObservableCollection<FastDlServer> savedServers = new();
    [ObservableProperty] private bool isServerListVisible = false;
    [ObservableProperty] private bool downloadMaps = false;

    public bool HasWarning => !string.IsNullOrWhiteSpace(Warning);

    private readonly FastDlService _service = new();
    private readonly string _configPath;
    private readonly string _serversPath;

    public MainWindowViewModel()
    {
        string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastDLX");
        Directory.CreateDirectory(appDataDir);
        _configPath = Path.Combine(appDataDir, "config.json");
        _serversPath = Path.Combine(appDataDir, "servers.json");

        LoadServers();
        LoadConfig();
        DetectCsSourceDirectory();
    }

    partial void OnGameDirectoryChanged(string value)
    {
        ValidateDownloadDirectory(value);
    }

    partial void OnWarningChanged(string value)
    {
        OnPropertyChanged(nameof(HasWarning));
    }

    private void ValidateDownloadDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Warning = string.Empty;
            return;
        }

        // Check if path ends with \download or \download\
        string normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        
        if (!normalizedPath.EndsWith($"{Path.DirectorySeparatorChar}download", StringComparison.OrdinalIgnoreCase))
        {
            Warning = "⚠️ Warning: Selected directory is not a 'download' folder. FastDL files should be synced to the game's download directory (e.g., cstrike\\download).";
        }
        else
        {
            Warning = string.Empty;
        }
    }

    private void DetectCsSourceDirectory()
    {
        // Only auto-detect if game directory is empty
        if (!string.IsNullOrWhiteSpace(GameDirectory))
            return;

        Status = "Searching for Counter-Strike: Source...";

        // Strategy 1: Check Windows Registry for Steam installation path
        string? steamPathFromRegistry = GetSteamPathFromRegistry();
        if (!string.IsNullOrEmpty(steamPathFromRegistry))
        {
            if (TryFindCssInSteamPath(steamPathFromRegistry, out string? cssPath))
            {
                GameDirectory = cssPath;
                Status = "Auto-detected Counter-Strike: Source download directory";
                return;
            }
        }

        // Strategy 2: Common Steam installation paths
        string[] commonPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            @"D:\Steam",
            @"E:\Steam",
            @"C:\Steam"
        };

        foreach (string steamPath in commonPaths)
        {
            if (TryFindCssInSteamPath(steamPath, out string? cssPath))
            {
                GameDirectory = cssPath;
                Status = "Auto-detected Counter-Strike: Source download directory";
                return;
            }
        }

        // Strategy 3: Search C:\ drive recursively (but limit depth)
        string[] drivesToSearch = { "C:\\", "D:\\", "E:\\" };
        
        foreach (string drive in drivesToSearch)
        {
            if (!Directory.Exists(drive))
                continue;

            // Search for Steam folders up to 3 levels deep
            var steamFolders = FindSteamFolders(drive, maxDepth: 3);
            
            foreach (string steamPath in steamFolders)
            {
                if (TryFindCssInSteamPath(steamPath, out string? cssPath))
                {
                    GameDirectory = cssPath;
                    Status = "Auto-detected Counter-Strike: Source download directory";
                    return;
                }
            }
        }

        Status = "Could not auto-detect CS:S directory. Please select manually.";
    }

    private string? GetSteamPathFromRegistry()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Try 64-bit registry first
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Wow6432Node\Valve\Steam");
                if (key != null)
                {
                    string? installPath = key.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                    {
                        return installPath;
                    }
                }

                // Try 32-bit registry
                using var key32 = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
                if (key32 != null)
                {
                    string? installPath = key32.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                    {
                        return installPath;
                    }
                }
            }
        }
        catch
        {
            // Ignore registry errors
        }

        return null;
    }

    private System.Collections.Generic.List<string> FindSteamFolders(string rootPath, int maxDepth, int currentDepth = 0)
    {
        var steamFolders = new System.Collections.Generic.List<string>();

        if (currentDepth > maxDepth)
            return steamFolders;

        try
        {
            // Check if current directory is a Steam folder
            if (Path.GetFileName(rootPath).Equals("Steam", StringComparison.OrdinalIgnoreCase))
            {
                string steamAppsPath = Path.Combine(rootPath, "steamapps");
                if (Directory.Exists(steamAppsPath))
                {
                    steamFolders.Add(rootPath);
                }
            }

            // Search subdirectories
            foreach (string dir in Directory.GetDirectories(rootPath))
            {
                // Skip system/hidden folders and common large folders
                string dirName = Path.GetFileName(dir).ToLower();
                if (dirName == "windows" || dirName == "program files" || 
                    dirName == "$recycle.bin" || dirName == "system volume information" ||
                    dirName.StartsWith(".") || dirName.StartsWith("$"))
                {
                    continue;
                }

                steamFolders.AddRange(FindSteamFolders(dir, maxDepth, currentDepth + 1));
            }
        }
        catch
        {
            // Ignore access denied and other errors
        }

        return steamFolders;
    }

    private bool TryFindCssInSteamPath(string steamPath, out string? cssDownloadPath)
    {
        cssDownloadPath = null;

        if (!Directory.Exists(steamPath))
            return false;

        // Check main steamapps location
        string cssPath = Path.Combine(steamPath, "steamapps", "common", "Counter-Strike Source", "cstrike", "download");
        
        if (Directory.Exists(cssPath))
        {
            cssDownloadPath = cssPath;
            return true;
        }

        // Check Steam library folders
        string libraryFoldersPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraryFoldersPath))
        {
            var libraryPaths = ParseSteamLibraryFolders(libraryFoldersPath);
            
            foreach (string libraryPath in libraryPaths)
            {
                string libCssPath = Path.Combine(libraryPath, "steamapps", "common", "Counter-Strike Source", "cstrike", "download");
                
                if (Directory.Exists(libCssPath))
                {
                    cssDownloadPath = libCssPath;
                    return true;
                }
            }
        }

        return false;
    }

    private System.Collections.Generic.List<string> ParseSteamLibraryFolders(string vdfPath)
    {
        var libraries = new System.Collections.Generic.List<string>();
        
        try
        {
            string content = File.ReadAllText(vdfPath);
            
            // Parse both old and new VDF format
            // Old format: "1"  "D:\\SteamLibrary"
            // New format: "path"  "D:\\SteamLibrary"
            
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                
                // Look for lines with "path" key
                if (trimmed.Contains("\"path\""))
                {
                    // Extract the value after "path"
                    int pathStart = trimmed.IndexOf("\"path\"");
                    int valueStart = trimmed.IndexOf('"', pathStart + 6);
                    int valueEnd = trimmed.IndexOf('"', valueStart + 1);
                    
                    if (valueStart > 0 && valueEnd > valueStart)
                    {
                        string path = trimmed.Substring(valueStart + 1, valueEnd - valueStart - 1);
                        // Unescape backslashes
                        path = path.Replace("\\\\", "\\");
                        
                        if (Directory.Exists(path))
                        {
                            libraries.Add(path);
                        }
                    }
                }
                // Also check for numeric keys (old format)
                else if (trimmed.StartsWith("\"") && char.IsDigit(trimmed[1]))
                {
                    // Format: "1"  "D:\\SteamLibrary"
                    int firstQuoteEnd = trimmed.IndexOf('"', 1);
                    int secondQuoteStart = trimmed.IndexOf('"', firstQuoteEnd + 1);
                    int secondQuoteEnd = trimmed.IndexOf('"', secondQuoteStart + 1);
                    
                    if (secondQuoteStart > 0 && secondQuoteEnd > secondQuoteStart)
                    {
                        string path = trimmed.Substring(secondQuoteStart + 1, secondQuoteEnd - secondQuoteStart - 1);
                        // Unescape backslashes
                        path = path.Replace("\\\\", "\\");
                        
                        if (Directory.Exists(path) && !libraries.Contains(path))
                        {
                            libraries.Add(path);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Status = $"Failed to parse Steam libraries: {ex.Message}";
        }
        
        return libraries;
    }

    private void LoadServers()
    {
        try
        {
            // Load default servers
            var defaultServers = new System.Collections.Generic.List<FastDlServer>
            {
                new FastDlServer { Name = "NiDE.GG CS:S Zombie Escape", Url = "https://fastdl.nide.gg/css_ze/", IsDefault = true },
                new FastDlServer { Name = "NiDE.GG CS:S Zombie Revival", Url = "https://fastdl.nide.gg/css_zr/", IsDefault = true }
            };

            if (File.Exists(_serversPath))
            {
                string json = File.ReadAllText(_serversPath);
                var saved = JsonSerializer.Deserialize<System.Collections.Generic.List<FastDlServer>>(json);
                
                if (saved != null)
                {
                    // Merge default servers with user-added ones
                    foreach (var server in saved)
                    {
                        if (!server.IsDefault)
                        {
                            defaultServers.Add(server);
                        }
                    }
                }
            }

            SavedServers = new ObservableCollection<FastDlServer>(defaultServers);
        }
        catch (Exception ex)
        {
            Status = $"Failed to load servers: {ex.Message}";
            // Still add default servers on error
            SavedServers = new ObservableCollection<FastDlServer>
            {
                new FastDlServer { Name = "NiDE.gg CS:S Zombie Escape", Url = "https://fastdl.nide.gg/css_ze/", IsDefault = true },
                new FastDlServer { Name = "NiDE.gg CS:S Zombie Revival", Url = "https://fastdl.nide.gg/css_zr/", IsDefault = true }
            };
        }
    }

    private void SaveServers()
    {
        try
        {
            // Only save user-added servers (not defaults)
            var userServers = SavedServers.Where(s => !s.IsDefault).ToList();
            string json = JsonSerializer.Serialize(userServers, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_serversPath, json);
        }
        catch (Exception ex)
        {
            Status = $"Failed to save servers: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ToggleServerList()
    {
        IsServerListVisible = !IsServerListVisible;
    }

    [RelayCommand]
    public void SelectServer(FastDlServer server)
    {
        if (server != null)
        {
            FastDlUrl = server.Url;
            IsServerListVisible = false;
            SaveConfig();
            Status = $"Selected: {server.Name}";
        }
    }

    [RelayCommand]
    public void AddCustomServer()
    {
        // This will be called from UI with a dialog
        var newServer = new FastDlServer 
        { 
            Name = "Custom Server", 
            Url = FastDlUrl, 
            IsDefault = false 
        };

        if (!string.IsNullOrWhiteSpace(FastDlUrl) && 
            !SavedServers.Any(s => s.Url.Equals(FastDlUrl, StringComparison.OrdinalIgnoreCase)))
        {
            SavedServers.Add(newServer);
            SaveServers();
            Status = "Custom server added to list";
        }
        else
        {
            Status = "Server already exists or URL is empty";
        }
    }

    [RelayCommand]
    public void RemoveServer(FastDlServer server)
    {
        if (server != null && !server.IsDefault)
        {
            SavedServers.Remove(server);
            SaveServers();
            Status = $"Removed: {server.Name}";
        }
        else if (server?.IsDefault == true)
        {
            Status = "Cannot remove default servers";
        }
    }

    [RelayCommand]
    public async Task RenameServerAsync(FastDlServer server)
    {
        if (server == null || server.IsDefault)
        {
            Status = "Cannot rename default servers";
            return;
        }

        // Open a simple input dialog
        var mainWindow = App.Current.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

        if (mainWindow == null) return;

        var dialog = new Window
        {
            Title = "Rename Server",
            Width = 400,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var textBox = new TextBox
        {
            Text = server.Name,
            Margin = new Avalonia.Thickness(20, 20, 20, 10),
            Watermark = "Enter new server name"
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Avalonia.Thickness(20, 10, 20, 20),
            Spacing = 10
        };

        var okButton = new Button
        {
            Content = "OK",
            Width = 80,
            IsDefault = true
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            Width = 80,
            IsCancel = true
        };

        okButton.Click += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(textBox.Text))
            {
                server.Name = textBox.Text;
                SaveServers();
                Status = $"Renamed to: {server.Name}";
                // Trigger UI update
                var index = SavedServers.IndexOf(server);
                if (index >= 0)
                {
                    SavedServers.RemoveAt(index);
                    SavedServers.Insert(index, server);
                }
            }
            dialog.Close();
        };

        cancelButton.Click += (s, e) => dialog.Close();

        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);

        var panel = new StackPanel();
        panel.Children.Add(textBox);
        panel.Children.Add(buttonPanel);

        dialog.Content = panel;

        await dialog.ShowDialog(mainWindow);
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                string json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<Config>(json);
                
                if (config != null)
                {
                    FastDlUrl = config.FastDlUrl ?? string.Empty;
                    GameDirectory = config.GameDirectory ?? string.Empty;
                    Status = "Loaded previous configuration";
                }
            }
        }
        catch (Exception ex)
        {
            Status = $"Failed to load config: {ex.Message}";
        }
    }

    private void SaveConfig()
    {
        try
        {
            var config = new Config
            {
                FastDlUrl = FastDlUrl,
                GameDirectory = GameDirectory
            };

            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            Status = $"Failed to save config: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task StartUpdateAsync()
    {
        if (string.IsNullOrWhiteSpace(FastDlUrl) || string.IsNullOrWhiteSpace(GameDirectory))
        {
            Status = "Please fill both fields.";
            return;
        }

        // Save config before starting
        SaveConfig();

        Status = "Starting...";
        var progress = new Progress<DownloadProgress>(p => 
        {
            Status = p.Message;
            Progress = p.Percentage;
        });

        // Pass DownloadMaps directly to service - let FastDlService handle the logic
        await _service.SyncAsync(FastDlUrl, GameDirectory, progress, retryCount: 3, skipMaps: !DownloadMaps);
    }

    [RelayCommand]
    public async Task BrowseAsync()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Game Download Directory (e.g., cstrike\\download)"
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
            SaveConfig();
        }
    }

    private class Config
    {
        public string? FastDlUrl { get; set; }
        public string? GameDirectory { get; set; }
    }

    public class FastDlServer
    {
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public bool IsDefault { get; set; }
    }
}