# How to Create a Release

This guide explains how to create and publish releases using GitHub Actions.

## Automatic Release (Recommended)

1. **Commit and push your changes**:
   ```bash
   git add .
   git commit -m "Prepare for release v1.0.0"
   git push origin main
   ```

2. **Create and push a version tag**:
   ```bash
   git tag v1.0.0
   git push origin v1.0.0
   ```

3. **GitHub Actions will automatically**:
   - Build the single-file executable
   - Build the MSI installer
   - Sign the MSI with a self-signed certificate
   - Create a GitHub Release
   - Upload all artifacts (portable ZIP, MSI installer, certificate, instructions)

4. **Check the release**:
   - Go to `https://github.com/YOUR_USERNAME/FastDLX/releases`
   - The release should be published automatically

## Manual Release

You can also trigger the build manually:

1. Go to your repository on GitHub
2. Click on "Actions" tab
3. Select "Build and Release" workflow
4. Click "Run workflow"
5. Enter a version number (e.g., `1.0.0`)
6. Click "Run workflow"

## Version Numbering

Follow semantic versioning (MAJOR.MINOR.PATCH):
- **MAJOR**: Breaking changes
- **MINOR**: New features (backwards compatible)
- **PATCH**: Bug fixes

Examples:
- `v1.0.0` - Initial release
- `v1.1.0` - Added new feature
- `v1.1.1` - Bug fix

## What Gets Built

Each release includes:

1. **Portable Version** (`FastDLX-v*.*.* -portable.zip`)
   - Single EXE file
   - No installation required
   - ~25-30 MB

2. **MSI Installer** (`FastDLX-v*.*.*-installer.msi`)
   - Windows installer
   - Creates Start Menu shortcut
   - Self-signed certificate included
   - ~25-30 MB

3. **Certificate** (`FastDLX-Certificate.pfx`)
   - Self-signed code signing certificate
   - Password: `FastDLX2025`
   - Required for MSI installation

4. **Installation Instructions** (`INSTALL.txt`)
   - How to use portable version
   - How to install certificate and MSI

## Testing Before Release

Every push to main/master/develop branches automatically runs build tests without creating a release. Check the "Build Test" workflow in the Actions tab to ensure your code builds successfully.

## Troubleshooting

### Build fails
- Check the Actions tab for error logs
- Ensure all dependencies are properly referenced in the .csproj file
- Make sure the version tag format is correct (v1.0.0, not 1.0.0)

### Release not created
- Ensure you pushed the tag: `git push origin v1.0.0`
- Check that the tag starts with 'v'
- Verify GitHub Actions are enabled in repository settings

### MSI build fails
- WiX Toolset is automatically installed in the workflow
- Check for any file path issues in build-msi.ps1

## Local Testing

Before pushing a tag, you can test builds locally:

```powershell
# Test portable build
.\build-release.ps1

# Test MSI build (requires WiX)
.\build-msi.ps1 -Version "1.0.0"
```

## Cleanup Old Releases

To delete a release and its tag:

```bash
# Delete the GitHub release (via web interface or API)

# Delete local tag
git tag -d v1.0.0

# Delete remote tag
git push origin :refs/tags/v1.0.0
```
