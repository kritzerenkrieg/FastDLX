# FastDLX MSI Installer Build Script
# Requires: WiX Toolset v4 (dotnet tool install --global wix)
# Run: .\build-msi.ps1

param(
    [string]$Version = "1.0.0",
    [string]$CertPassword = "FastDLX2025"
)

Write-Host "=== FastDLX MSI Installer Build ===" -ForegroundColor Cyan
Write-Host "Version: $Version" -ForegroundColor Yellow

# Check if WiX is installed
Write-Host "`nChecking for WiX Toolset..." -ForegroundColor Yellow
$wixInstalled = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wixInstalled) {
    Write-Host "WiX Toolset not found. Installing..." -ForegroundColor Yellow
    dotnet tool install --global wix
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Failed to install WiX Toolset!" -ForegroundColor Red
        exit 1
    }
}

# Check if WiX UI extension is installed
Write-Host "Checking for WiX UI extension..." -ForegroundColor Yellow
wix extension list | Out-Null
wix extension add -g WixToolset.UI.wixext | Out-Null
Write-Host "WiX UI extension ready" -ForegroundColor Green

# Clean previous builds
Write-Host "`nCleaning previous builds..." -ForegroundColor Yellow
dotnet clean -c Release
if (Test-Path ".\installer") { Remove-Item ".\installer" -Recurse -Force }
New-Item -ItemType Directory -Path ".\installer" -Force | Out-Null

# Restore packages
Write-Host "`nRestoring packages..." -ForegroundColor Yellow
dotnet restore

# Publish application
Write-Host "`nPublishing application..." -ForegroundColor Yellow
dotnet publish -c Release -r win-x64 `
  --self-contained true `
  /p:PublishSingleFile=false `
  /p:PublishTrimmed=true `
  /p:DebugType=none `
  /p:DebugSymbols=false `
  -o ".\installer\publish"

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Create or use existing self-signed certificate
Write-Host "`nSetting up self-signed certificate..." -ForegroundColor Yellow
$certName = "FastDLX"
$certPath = ".\installer\FastDLX.pfx"

$existingCert = Get-ChildItem -Path Cert:\CurrentUser\My | Where-Object { $_.Subject -match "CN=$certName" } | Select-Object -First 1

if ($existingCert) {
    Write-Host "Using existing certificate: $($existingCert.Thumbprint)" -ForegroundColor Green
} else {
    Write-Host "Creating new self-signed certificate..." -ForegroundColor Yellow
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject "CN=$certName" `
        -KeyUsage DigitalSignature `
        -FriendlyName "$certName Code Signing" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
        -NotAfter (Get-Date).AddYears(5)
    
    $existingCert = $cert
    Write-Host "Certificate created: $($cert.Thumbprint)" -ForegroundColor Green
}

# Export certificate to PFX
$securePwd = ConvertTo-SecureString -String $CertPassword -Force -AsPlainText
Export-PfxCertificate -Cert $existingCert -FilePath $certPath -Password $securePwd | Out-Null
Write-Host "Certificate exported to: $certPath" -ForegroundColor Green

# Create WiX source file with all files from publish folder
Write-Host "`nCreating WiX installer definition..." -ForegroundColor Yellow

# Get all files from publish directory
$publishFiles = Get-ChildItem -Path ".\installer\publish" -File -Recurse
$fileComponents = ""
$fileIndex = 1

foreach ($file in $publishFiles) {
    $relativePath = $file.FullName.Replace((Resolve-Path ".\installer\publish").Path, "").TrimStart('\')
    $fileId = "File_$fileIndex"
    $componentId = "Component_$fileIndex"
    $componentGuid = [guid]::NewGuid().ToString().ToUpper()
    
    $fileComponents += @"
      <Component Id="$componentId" Guid="$componentGuid">
        <File Id="$fileId" Source=".\installer\publish\$relativePath" KeyPath="yes" />
      </Component>

"@
    $fileIndex++
}

$wixContent = @"
<?xml version="1.0" encoding="UTF-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs" xmlns:ui="http://wixtoolset.org/schemas/v4/wxs/ui">
  <Package Name="FastDLX" 
           Version="$Version" 
           Manufacturer="FastDLX Team" 
           UpgradeCode="12345678-1234-1234-1234-123456789012"
           Language="1033"
           Codepage="1252">
    
    <SummaryInformation Description="FastDLX - Fast Download Manager" />
    
    <MajorUpgrade DowngradeErrorMessage="A newer version of [ProductName] is already installed." />
    <MediaTemplate EmbedCab="yes" />

    <Feature Id="ProductFeature" Title="FastDLX" Level="1">
      <ComponentGroupRef Id="ProductComponents" />
      <ComponentRef Id="ApplicationShortcut" />
    </Feature>

    <Property Id="ARPPRODUCTICON" Value="icon.ico" />
    <Property Id="ARPHELPLINK" Value="https://github.com/yourusername/fastdlx" />
    
    <ui:WixUI Id="WixUI_Minimal" />
    <WixVariable Id="WixUILicenseRtf" Value=".\installer\License.rtf" />

  </Package>

  <Fragment>
    <StandardDirectory Id="ProgramFiles64Folder">
      <Directory Id="INSTALLFOLDER" Name="FastDLX" />
    </StandardDirectory>
    
    <StandardDirectory Id="ProgramMenuFolder">
      <Directory Id="ApplicationProgramsFolder" Name="FastDLX"/>
    </StandardDirectory>
  </Fragment>

  <Fragment>
    <ComponentGroup Id="ProductComponents" Directory="INSTALLFOLDER">
$fileComponents
    </ComponentGroup>

    <Component Id="ApplicationShortcut" Directory="ApplicationProgramsFolder" Guid="AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE">
      <Shortcut Id="ApplicationStartMenuShortcut"
                Name="FastDLX"
                Description="Fast Download Manager"
                Target="[INSTALLFOLDER]FastDLX.exe"
                WorkingDirectory="INSTALLFOLDER"/>
      <RemoveFolder Id="CleanUpShortCut" Directory="ApplicationProgramsFolder" On="uninstall"/>
      <RegistryValue Root="HKCU" Key="Software\FastDLX" Name="installed" Type="integer" Value="1" KeyPath="yes"/>
    </Component>
  </Fragment>
</Wix>
"@

$wixContent | Out-File -FilePath ".\installer\FastDLX.wxs" -Encoding UTF8
Write-Host "Generated WiX file with $($publishFiles.Count) files" -ForegroundColor Green

# Create MIT License RTF file
Write-Host "Creating license file..." -ForegroundColor Yellow
$licenseRtf = @"
{\rtf1\ansi\deff0
{\fonttbl{\f0 Arial;}}
{\colortbl;\red0\green0\blue0;}
\viewkind4\uc1\pard\lang1033\f0\fs20

{\b FastDLX - MIT License}\par
\par
Copyright (c) 2025 FastDLX Team\par
\par
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:\par
\par
The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.\par
\par
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.\par
}
"@

$licenseRtf | Out-File -FilePath ".\installer\License.rtf" -Encoding ASCII

# Build MSI with WiX
Write-Host "`nBuilding MSI installer..." -ForegroundColor Yellow
$msiOutput = ".\installer\FastDLX-$Version.msi"

wix build ".\installer\FastDLX.wxs" `
    -o $msiOutput `
    -ext WixToolset.UI.wixext

if ($LASTEXITCODE -ne 0) {
    Write-Host "MSI build failed!" -ForegroundColor Red
    exit 1
}

# Sign the MSI
Write-Host "`nSigning MSI installer..." -ForegroundColor Yellow
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe"

# Try to find signtool.exe
if (-not (Test-Path $signtool)) {
    $signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
}

if ($signtool) {
    & $signtool sign /f $certPath /p $CertPassword /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $msiOutput
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "MSI signed successfully!" -ForegroundColor Green
    } else {
        Write-Host "Warning: MSI signing failed, but installer was created." -ForegroundColor Yellow
    }
} else {
    Write-Host "Warning: signtool.exe not found. Install Windows SDK to sign the MSI." -ForegroundColor Yellow
    Write-Host "MSI created but not signed." -ForegroundColor Yellow
}

# Display results
Write-Host "`n=== Build Complete ===" -ForegroundColor Green
Write-Host "MSI Installer: $msiOutput" -ForegroundColor Cyan
if (Test-Path $msiOutput) {
    $size = (Get-Item $msiOutput).Length / 1MB
    Write-Host "File size: $([math]::Round($size, 2)) MB" -ForegroundColor Cyan
}
Write-Host "`nCertificate: $certPath (Password: $CertPassword)" -ForegroundColor Cyan
Write-Host "`nTo install the certificate (required for installation):" -ForegroundColor Yellow
Write-Host "  1. Double-click FastDLX.pfx" -ForegroundColor White
Write-Host "  2. Select 'Current User' and click Next" -ForegroundColor White
Write-Host "  3. Enter password: $CertPassword" -ForegroundColor White
Write-Host "  4. Place certificate in 'Trusted Root Certification Authorities'" -ForegroundColor White
Write-Host "`nNote: Self-signed certificates are for testing/personal use only." -ForegroundColor DarkGray
