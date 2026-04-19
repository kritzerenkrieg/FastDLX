# FastDLX

A standalone FastDL client for Source games that provides fast and reliable content downloads from game server FastDL repositories.

![License](https://img.shields.io/badge/license-MIT-blue.svg)
![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)
![Platform](https://img.shields.io/badge/platform-Windows-lightgrey.svg)

## Install

Download the latest release from the [Releases page](https://github.com/kritzerenkrieg/FastDLX/releases), or download the application directly: [FastDLX.exe](https://github.com/kritzerenkrieg/FastDLX/releases/download/v1.1/FastDLX.exe)


## Overview

![Preview](https://raw.githubusercontent.com/kritzerenkrieg/FastDLX/refs/heads/master/Assets/preview.png)

FastDLX is an independent desktop application that allows you to download game content (maps, materials, models, sounds, etc.) directly from FastDL servers without needing to connect to the game server first. This is particularly useful for:

- Pre-downloading content before joining a server
- Resuming interrupted downloads
- Managing content from multiple FastDL servers
- Downloading only what you need (optional map downloads)

## Features

✨ **Smart Download Management**
- Resume interrupted downloads automatically
- Download only missing or outdated files
- Optional map file downloads (saves bandwidth)

🎮 **Game Integration**
- Automatic Source game directory detection
- Proper file structure preservation
- Compatible with standard FastDL server formats

🌐 **Server Management**
- Built-in preset servers
- Add and manage custom FastDL servers
- Rename and organize server list
- Quick server selection

📊 **Progress Tracking**
- Real-time download progress with percentage
- File counting during scan phase
- Detailed status messages
- Logging for troubleshooting

## TODO
- Only tested on CS:S, need to know if it works well in TF2, GMOD, etc.
- L4D2 has different download method, not sure if i'd work on it.
- Waiting for new suggestions, language support can wait.

### Requirements (For building/development)

- Windows 10 or later (x64)
- .NET 8.0 Runtime (included in portable version)
- Internet connection

### Map Downloads

Maps are typically very large (10-100+ MB each). If you're on a limited connection or just want to download materials/sounds/models, uncheck the "Download Maps" option before starting.

## Building from Source

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10 or later
- Visual Studio 2022 or JetBrains Rider (optional)

### Build Instructions

#### Portable Single-File Executable

```powershell
# Clone the repository
git clone https://github.com/yourusername/FastDLX.git
cd FastDLX

# Run the build script
.\build-release.ps1
```

The executable will be in `bin\Release\net8.0\win-x64\publish\FastDLX.exe`

### Manual Build

```powershell
dotnet clean -c Release
dotnet restore
dotnet publish -c Release -r win-x64 --self-contained true ^
  /p:PublishSingleFile=true ^
  /p:IncludeNativeLibrariesForSelfExtract=true ^
  /p:EnableCompressionInSingleFile=true
```

## Project Structure

```
FastDLX/
├── Models/           # Data models (FileEntry, DownloadProgress)
├── Services/         # Core services (FastDL download logic)
├── ViewModels/       # MVVM view models
├── Views/            # Avalonia UI views
├── Assets/           # Application assets
├── .github/          # GitHub Actions workflows
├── build-release.ps1 # Portable build script
├── build-msi.ps1     # MSI installer build script
└── README.md         # This file
```

## Development Stack

- **Framework**: .NET 8.0
- **UI**: [Avalonia UI](https://avaloniaui.net/) (Cross-platform XAML-based UI)
- **MVVM**: [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
- **Compression**: [SharpCompress](https://github.com/adamhathcock/sharpcompress)
- **CI/CD**: GitHub Actions

## Troubleshooting

### Can't find game directory
- Manually browse to your CS:Source installation
- The path should end with `cstrike\download`
- Typical location: `C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Source\cstrike\download`

### Downloads are slow
- Limited by the FastDL server's bandwidth
- Bad internet
- Use VPN

### "Scanning files..." takes too long
- The first scan counts all files on the server
- This is normal and happens once per session
- Update maps only if you use it frequently

### Check the logs
Logs are stored in the `Logs\` folder next to the executable:
```
bin\Debug\net8.0\win-x64\Logs\FastDLX_YYYY-MM-DD_HH-MM-SS.txt
```

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit your changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Acknowledgments

- Built with [Avalonia UI](https://avaloniaui.net/)
- Thanks to Chujoy for map only update suggestion (v1.1)
- Thanks to the Counter-Strike: Source community

---

**Note**: This is an independent client and is not affiliated with Valve Corporation.
