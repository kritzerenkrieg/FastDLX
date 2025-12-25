# FastDLX Release Build Script
Write-Host "Building FastDLX Release..." -ForegroundColor Green

# Clean
Write-Host "Cleaning previous builds..." -ForegroundColor Yellow
dotnet clean -c Release

# Restore
Write-Host "Restoring packages..." -ForegroundColor Yellow
dotnet restore

# Publish
Write-Host "Publishing single-file executable..." -ForegroundColor Yellow
dotnet publish -c Release -r win-x64 `
  --self-contained true `
  /p:PublishSingleFile=true `
  /p:PublishTrimmed=true `
  /p:IncludeNativeLibrariesForSelfExtract=true `
  /p:EnableCompressionInSingleFile=true `
  /p:PublishReadyToRun=true `
  /p:DebugType=none `
  /p:DebugSymbols=false

if ($LASTEXITCODE -eq 0) {
    Write-Host "Build successful!" -ForegroundColor Green
    Write-Host "Output: bin\Release\net8.0\win-x64\publish\FastDLX.exe" -ForegroundColor Cyan
    
    # Get file size
    $exePath = "bin\Release\net8.0\win-x64\publish\FastDLX.exe"
    if (Test-Path $exePath) {
        $size = (Get-Item $exePath).Length / 1MB
        Write-Host "File size: $([math]::Round($size, 2)) MB" -ForegroundColor Cyan
    }
} else {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}