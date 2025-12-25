using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SharpCompress.Compressors.BZip2;
using FastDLX.Models;

namespace FastDLX.Services;

public class FastDlService
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _logDir = Path.Combine(AppContext.BaseDirectory, "Logs");
    private readonly string _logFile;
    private readonly string _tempExtension = ".fdltemp"; // temporary file extension
    private int _totalFiles;
    private int _completedFiles;

    public FastDlService()
    {
        Directory.CreateDirectory(_logDir);
        _logFile = Path.Combine(_logDir, $"FastDLX_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");
        
        // Create the log file immediately to test if it works
        try
        {
            File.WriteAllText(_logFile, $"Log created at {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            // If this fails, we have a real problem - maybe show in UI?
            Console.WriteLine($"Failed to create log file: {ex.Message}");
        }
    }

    public async Task SyncAsync(string baseUrl, string targetDir, IProgress<DownloadProgress>? progress = null, int retryCount = 3, bool skipMaps = false)
    {
        try
        {
            // Validate URL format
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) || 
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                string err = "Invalid URL format. Please provide a valid HTTP/HTTPS URL.";
                progress?.Report(new DownloadProgress { Message = err, Percentage = 0 });
                Log(err);
                return;
            }

            if (!baseUrl.EndsWith('/')) baseUrl += '/';
            Directory.CreateDirectory(targetDir);

            Log($"Starting sync: {baseUrl} -> {targetDir} (Skip Maps: {skipMaps})");
            _totalFiles = 0;
            _completedFiles = 0;
            
            // Quick scan to count files - shows progress so user knows it's working
            progress?.Report(new DownloadProgress { Message = "Scanning server files...", Percentage = 0 });
            await CountFiles(baseUrl, retryCount, skipMaps, progress);
            
            if (_totalFiles > 0)
            {
                progress?.Report(new DownloadProgress { Message = $"Found {_totalFiles} files. Starting download...", Percentage = 0 });
                Log($"Scan complete: {_totalFiles} files found");
            }
            else
            {
                progress?.Report(new DownloadProgress { Message = "Scanning complete. Processing files...", Percentage = 0 });
            }

            bool success = await SyncDirectory(baseUrl, targetDir, progress, retryCount, skipMaps);

            if (success)
            {
                progress?.Report(new DownloadProgress { Message = "Sync completed!", Percentage = 100 });
                Log("Sync completed successfully.");
            }
            else
            {
                progress?.Report(new DownloadProgress { Message = "Sync failed - could not access server.", Percentage = 0 });
                Log("Sync failed - server not accessible.");
            }
        }
        catch (Exception ex)
        {
            string err = $"Sync failed: {ex.Message}";
            progress?.Report(new DownloadProgress { Message = err, Percentage = 0 });
            Log(err);
        }
    }

    private async Task<bool> SyncDirectory(string url, string localDir, IProgress<DownloadProgress>? progress, int retryCount, bool skipMaps)
    {
        string html;
        try
        {
            var response = await _client.GetAsync(url);
            
            if (!response.IsSuccessStatusCode)
            {
                string err = $"Failed to access URL: {url} (Status: {response.StatusCode})";
                progress?.Report(new DownloadProgress { Message = err, Percentage = GetProgressPercentage() });
                Log(err);
                return false;
            }
            
            html = await response.Content.ReadAsStringAsync();
            
            // Check if we got actual HTML content
            if (string.IsNullOrWhiteSpace(html))
            {
                string err = $"No content returned from URL: {url}";
                progress?.Report(new DownloadProgress { Message = err, Percentage = GetProgressPercentage() });
                Log(err);
                return false;
            }
        }
        catch (HttpRequestException ex)
        {
            string err = $"Network error accessing {url}: {ex.Message}";
            progress?.Report(new DownloadProgress { Message = err, Percentage = GetProgressPercentage() });
            Log(err);
            return false;
        }
        catch (Exception ex)
        {
            string err = $"Failed to read URL: {url} ({ex.Message})";
            progress?.Report(new DownloadProgress { Message = err, Percentage = GetProgressPercentage() });
            Log(err);
            return false;
        }

        var matches = Regex.Matches(html, "<a href=\"([^\"]+?)\">", RegexOptions.IgnoreCase);
        
        // Validate that we found at least some links (otherwise might not be a directory listing)
        if (matches.Count == 0)
        {
            string warn = $"No files or folders found at {url} - may not be a valid FastDL directory";
            progress?.Report(new DownloadProgress { Message = warn, Percentage = GetProgressPercentage() });
            Log(warn);
            return true; // Not necessarily an error, could be empty directory
        }

        Directory.CreateDirectory(localDir);
        bool allSucceeded = true;

        foreach (Match match in matches)
        {
            string name = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(name) || name == "../") continue;

            string remotePath = url + name;

            if (name.EndsWith("/"))
            {
                // Handle folder
                string folderName = name.TrimEnd('/');
                
                // Skip maps folder if skipMaps is true
                if (skipMaps && folderName.Equals("maps", StringComparison.OrdinalIgnoreCase))
                {
                    string skipMsg = $"⏭️ Skipping maps folder (Download Maps is disabled)";
                    Log($"Skipping maps folder as requested");
                    progress?.Report(new DownloadProgress { Message = skipMsg, Percentage = GetProgressPercentage() });
                    continue;
                }
                
                string localPath = Path.Combine(localDir, folderName);
                bool folderSuccess = await SyncDirectory(remotePath, localPath, progress, retryCount, skipMaps);
                if (!folderSuccess) allSucceeded = false;
            }
            else
            {
                // Handle file
                string safeName = SanitizeFileName(name);
                
                // Skip map files if skipMaps is true (check if we're in maps directory or file is a .bsp/.bz2 map)
                if (skipMaps && IsMapFile(safeName, localDir))
                {
                    string skipMsg = $"⏭️ Skipping map file: {safeName}";
                    Log($"Skipping map file: {safeName}");
                    _completedFiles++;
                    progress?.Report(new DownloadProgress { Message = skipMsg, Percentage = GetProgressPercentage() });
                    continue;
                }
                
                string localPath = Path.Combine(localDir, safeName);
                
                // Special handling for .bz2 files - check if decompressed .bsp exists
                if (safeName.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase))
                {
                    string bspName = safeName.Substring(0, safeName.Length - 4); // Remove .bz2
                    string bspPath = Path.Combine(localDir, bspName);
                    string bspTempPath = bspPath + _tempExtension;
                    string bz2Path = bspPath + ".bz2";
                    
                    // Clean up any incomplete decompression from previous run
                    if (File.Exists(bspTempPath))
                    {
                        Log($"Found incomplete decompression for {bspName}, cleaning up temp file...");
                        File.Delete(bspTempPath);
                    }
                    
                    // Check if we have a complete BZ2 file ready to decompress (from interrupted decompression)
                    if (File.Exists(bz2Path) && !File.Exists(bspPath))
                    {
                        Log($"Found complete BZ2 file for {bspName}, attempting decompression...");
                        progress?.Report(new DownloadProgress { Message = $"Resuming decompression of {bspName}...", Percentage = GetProgressPercentage() });
                        
                        try
                        {
                            await DecompressBz2File(bz2Path, bspTempPath);
                            File.Move(bspTempPath, bspPath);
                            File.Delete(bz2Path);
                            Log($"Successfully decompressed existing {safeName}");
                        }
                        catch (Exception ex)
                        {
                            Log($"Failed to decompress existing BZ2: {ex.Message}, will re-download");
                            if (File.Exists(bspTempPath)) File.Delete(bspTempPath);
                            if (File.Exists(bz2Path)) File.Delete(bz2Path);
                        }
                    }
                    
                    // Only download if the decompressed BSP doesn't exist
                    if (!File.Exists(bspPath))
                    {
                        await DownloadAndDecompressBz2(remotePath, bspPath, safeName, progress, retryCount);
                    }
                    else
                    {
                        string skipMsg = $"⏭️ Skipping {safeName} (decompressed file already exists)";
                        Log($"Skipping {safeName} - decompressed file {bspName} already exists");
                        _completedFiles++;
                        progress?.Report(new DownloadProgress { Message = skipMsg, Percentage = GetProgressPercentage() });
                    }
                }
                else
                {
                    // Regular file (not .bz2)
                    if (!File.Exists(localPath))
                    {
                        await DownloadFileWithResume(remotePath, localPath, safeName, progress, retryCount);
                    }
                    else
                    {
                        string skipMsg = $"⏭️ Skipping {safeName} (already exists)";
                        Log($"Skipping {safeName} - file already exists");
                        _completedFiles++;
                        progress?.Report(new DownloadProgress { Message = skipMsg, Percentage = GetProgressPercentage() });
                    }
                }
            }
        }
        
        return allSucceeded;
    }

    private bool IsMapFile(string fileName, string currentDir)
    {
        // Check if we're in a maps directory
        if (currentDir.Contains($"{Path.DirectorySeparatorChar}maps{Path.DirectorySeparatorChar}") ||
            currentDir.EndsWith($"{Path.DirectorySeparatorChar}maps", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        
        // Check if file is a .bsp file
        if (fileName.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        
        return false;
    }

    private async Task DownloadAndDecompressBz2(string url, string finalBspPath, string displayName, IProgress<DownloadProgress>? progress, int retryCount)
    {
        string tempBz2Path = finalBspPath + ".bz2" + _tempExtension;
        string bz2Path = finalBspPath + ".bz2";
        long startByte = 0;

        // Check if there's a partial download
        if (File.Exists(tempBz2Path))
        {
            startByte = new FileInfo(tempBz2Path).Length;
            Log($"Resuming download of {displayName} from byte {startByte}");
            progress?.Report(new DownloadProgress { Message = $"Resuming {displayName} from {FormatBytes(startByte)}...", Percentage = GetProgressPercentage() });
        }
        else
        {
            progress?.Report(new DownloadProgress { Message = $"Downloading {displayName}...", Percentage = GetProgressPercentage() });
            Log($"Downloading {url} -> {finalBspPath} (will decompress)");
        }

        bool downloaded = false;
        for (int attempt = 1; attempt <= retryCount && !downloaded; attempt++)
        {
            try
            {
                // Check if server supports range requests
                long? totalSize = await GetContentLength(url);
                
                if (totalSize.HasValue && startByte > 0 && startByte < totalSize.Value)
                {
                    // Resume download using range request
                    await DownloadRange(url, tempBz2Path, startByte, totalSize.Value, displayName, progress);
                }
                else
                {
                    // Fresh download or server doesn't support ranges
                    if (File.Exists(tempBz2Path))
                    {
                        File.Delete(tempBz2Path);
                    }
                    await DownloadFull(url, tempBz2Path, displayName, progress);
                }

                // Download complete, now decompress
                progress?.Report(new DownloadProgress { Message = $"Decompressing {displayName}...", Percentage = GetProgressPercentage() });
                Log($"Decompressing {displayName} to {Path.GetFileName(finalBspPath)}");
                
                // Move temp to bz2 path first
                if (File.Exists(bz2Path))
                {
                    File.Delete(bz2Path);
                }
                File.Move(tempBz2Path, bz2Path);
                
                // Decompress bz2 to temp bsp first (for safety)
                string tempBspPath = finalBspPath + _tempExtension;
                if (File.Exists(tempBspPath))
                {
                    File.Delete(tempBspPath);
                }
                
                await DecompressBz2File(bz2Path, tempBspPath);
                
                // Decompression successful, move temp to final location
                if (File.Exists(finalBspPath))
                {
                    File.Delete(finalBspPath);
                }
                File.Move(tempBspPath, finalBspPath);
                
                // Only delete the compressed file AFTER successful decompression and move
                // This way if app crashes during decompression, we still have the .bz2 to retry
                File.Delete(bz2Path);
                
                Log($"Successfully decompressed {displayName} to {Path.GetFileName(finalBspPath)}");
                _completedFiles++;
                progress?.Report(new DownloadProgress { Message = $"Completed {displayName}", Percentage = GetProgressPercentage() });
                downloaded = true;
            }
            catch (Exception ex)
            {
                if (attempt == retryCount)
                {
                    string err = $"Failed: {displayName} after {retryCount} attempts ({ex.Message})";
                    progress?.Report(new DownloadProgress { Message = err, Percentage = GetProgressPercentage() });
                    Log(err);
                    
                    // Keep the temp file for future resume
                    if (File.Exists(tempBz2Path))
                    {
                        Log($"Partial file saved at {tempBz2Path} for future resume");
                    }
                }
                else
                {
                    Log($"Download/decompress attempt {attempt} failed for {displayName}, retrying...");
                    await Task.Delay(1000 * attempt); // exponential backoff
                    
                    // Update startByte for next attempt
                    if (File.Exists(tempBz2Path))
                    {
                        startByte = new FileInfo(tempBz2Path).Length;
                    }
                }
            }
        }
    }

    private async Task DecompressBz2File(string bz2Path, string outputPath)
    {
        using var inputStream = new FileStream(bz2Path, FileMode.Open, FileAccess.Read);
        using var bz2Stream = new BZip2Stream(inputStream, SharpCompress.Compressors.CompressionMode.Decompress, false);
        using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        
        await bz2Stream.CopyToAsync(outputStream);
    }

    private async Task DownloadFileWithResume(string url, string localPath, string displayName, IProgress<DownloadProgress>? progress, int retryCount)
    {
        string tempPath = localPath + _tempExtension;
        long startByte = 0;

        // Check if there's a partial download
        if (File.Exists(tempPath))
        {
            startByte = new FileInfo(tempPath).Length;
            Log($"Found partial download of {displayName}, size: {FormatBytes(startByte)}");
        }

        bool downloaded = false;
        for (int attempt = 1; attempt <= retryCount && !downloaded; attempt++)
        {
            try
            {
                // Check if server supports range requests and get total size
                long? totalSize = await GetContentLength(url);
                
                if (!totalSize.HasValue)
                {
                    Log($"Server did not return content length for {displayName}, downloading without resume support");
                }
                else if (startByte > 0)
                {
                    if (startByte >= totalSize.Value)
                    {
                        // File is already complete or larger than expected
                        Log($"Temp file size ({FormatBytes(startByte)}) >= total size ({FormatBytes(totalSize.Value)}), treating as complete");
                        if (File.Exists(localPath))
                        {
                            File.Delete(localPath);
                        }
                        File.Move(tempPath, localPath);
                        Log($"Downloaded {displayName}");
                        downloaded = true;
                        continue;
                    }
                    else
                    {
                        Log($"Resuming download of {displayName} from byte {startByte} / {totalSize.Value}");
                        progress?.Report(new DownloadProgress { Message = $"Resuming {displayName} from {FormatBytes(startByte)}...", Percentage = GetProgressPercentage() });
                    }
                }
                else
                {
                    progress?.Report(new DownloadProgress { Message = $"Downloading {displayName}...", Percentage = GetProgressPercentage() });
                    Log($"Downloading {url} -> {localPath}");
                }
                
                if (totalSize.HasValue && startByte > 0 && startByte < totalSize.Value)
                {
                    // Resume download using range request
                    await DownloadRange(url, tempPath, startByte, totalSize.Value, displayName, progress);
                }
                else
                {
                    // Fresh download or server doesn't support ranges
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                    await DownloadFull(url, tempPath, displayName, progress);
                }

                // Move temp file to final location
                if (File.Exists(localPath))
                {
                    File.Delete(localPath);
                }
                File.Move(tempPath, localPath);
                
                Log($"Downloaded {displayName}");
                _completedFiles++;
                downloaded = true;
            }
            catch (Exception ex)
            {
                if (attempt == retryCount)
                {
                    string err = $"Failed: {displayName} after {retryCount} attempts ({ex.Message})";
                    progress?.Report(new DownloadProgress { Message = err, Percentage = GetProgressPercentage() });
                    Log(err);
                    
                    // Keep the temp file for future resume
                    Log($"Partial file saved at {tempPath} for future resume");
                }
                else
                {
                    Log($"Download attempt {attempt} failed for {displayName}: {ex.Message}, retrying...");
                    await Task.Delay(1000 * attempt); // exponential backoff
                    
                    // Update startByte for next attempt
                    if (File.Exists(tempPath))
                    {
                        startByte = new FileInfo(tempPath).Length;
                    }
                }
            }
        }
    }

    private async Task<long?> GetContentLength(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _client.SendAsync(request);
            return response.Content.Headers.ContentLength;
        }
        catch
        {
            return null;
        }
    }

    private async Task DownloadRange(string url, string tempPath, long startByte, long totalSize, string displayName, IProgress<DownloadProgress>? progress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(startByte, null);

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        
        // Check if server actually supports range requests
        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            Log($"Server does not support range requests for {displayName} (got {response.StatusCode}), starting fresh download");
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
            await DownloadFull(url, tempPath, displayName, progress);
            return;
        }
        
        response.EnsureSuccessStatusCode();

        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(tempPath, FileMode.Append, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        long totalRead = startByte;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead);
            totalRead += bytesRead;

            // Update progress occasionally
            if (totalRead % (1024 * 1024) < 8192) // Every ~1MB
            {
                int filePercentage = (int)((totalRead * 100) / totalSize);
                progress?.Report(new DownloadProgress 
                { 
                    Message = $"Downloading {displayName}... {filePercentage}% ({FormatBytes(totalRead)}/{FormatBytes(totalSize)})", 
                    Percentage = GetProgressPercentage() 
                });
            }
        }
    }

    private async Task DownloadFull(string url, string tempPath, string displayName, IProgress<DownloadProgress>? progress)
    {
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long? totalSize = response.Content.Headers.ContentLength;
        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead);
            totalRead += bytesRead;

            // Update progress occasionally
            if (totalSize.HasValue && totalRead % (1024 * 1024) < 8192) // Every ~1MB
            {
                int filePercentage = (int)((totalRead * 100) / totalSize.Value);
                progress?.Report(new DownloadProgress 
                { 
                    Message = $"Downloading {displayName}... {filePercentage}% ({FormatBytes(totalRead)}/{FormatBytes(totalSize.Value)})", 
                    Percentage = GetProgressPercentage() 
                });
            }
        }
    }

    private string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    private string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }

    private double GetProgressPercentage()
    {
        if (_totalFiles == 0)
        {
            // If we haven't counted yet or no files, show incremental progress
            return Math.Min(95, _completedFiles * 5);
        }
        
        // Show accurate percentage based on files completed
        return Math.Min(100, (_completedFiles * 100.0) / _totalFiles);
    }

    private async Task CountFiles(string url, int retryCount, bool skipMaps, IProgress<DownloadProgress>? progress = null, int depth = 0)
    {
        try
        {
            var response = await _client.GetAsync(url);
            if (!response.IsSuccessStatusCode) return;
            
            string html = await response.Content.ReadAsStringAsync();
            var matches = Regex.Matches(html, "<a href=\"([^\"]+?)\">", RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                string name = match.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(name) || name == "../") continue;

                if (name.EndsWith("/"))
                {
                    string folderName = name.TrimEnd('/');
                    if (skipMaps && folderName.Equals("maps", StringComparison.OrdinalIgnoreCase))
                        continue;
                    
                    // Show which folder we're scanning (only for first few levels)
                    if (depth < 3 && progress != null)
                    {
                        progress.Report(new DownloadProgress 
                        { 
                            Message = $"Scanning {folderName}/... ({_totalFiles} files so far)", 
                            Percentage = 0 
                        });
                    }
                    
                    await CountFiles(url + name, retryCount, skipMaps, progress, depth + 1);
                }
                else
                {
                    string safeName = SanitizeFileName(name);
                    if (skipMaps && IsMapFile(safeName, url))
                        continue;
                    
                    _totalFiles++;
                    
                    // Update progress every 10 files so user sees activity
                    if (_totalFiles % 10 == 0 && progress != null)
                    {
                        progress.Report(new DownloadProgress 
                        { 
                            Message = $"Scanning... found {_totalFiles} files", 
                            Percentage = 0 
                        });
                    }
                }
            }
        }
        catch
        {
            // Ignore errors during counting
        }
    }

    private void Log(string message)
    {
        try
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            File.AppendAllText(_logFile, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            // Log to console if file logging fails
            Console.WriteLine($"Logging failed: {ex.Message}");
        }
    }
}