using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SharpCompress.Compressors.BZip2;

namespace FastDLX.Services;

public class FastDlService
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _logDir = Path.Combine(AppContext.BaseDirectory, "Logs");
    private readonly string _logFile;
    private readonly string _tempExtension = ".fdltemp"; // temporary file extension

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

    public async Task SyncAsync(string baseUrl, string targetDir, IProgress<string>? progress = null, int retryCount = 3)
    {
        try
        {
            if (!baseUrl.EndsWith('/')) baseUrl += '/';
            Directory.CreateDirectory(targetDir);

            Log($"Starting sync: {baseUrl} -> {targetDir}");
            progress?.Report("Starting sync...");

            await SyncDirectory(baseUrl, targetDir, progress, retryCount);

            progress?.Report("Sync completed!");
            Log("Sync completed successfully.");
        }
        catch (Exception ex)
        {
            string err = $"Sync failed: {ex.Message}";
            progress?.Report(err);
            Log(err);
        }
    }

    private async Task SyncDirectory(string url, string localDir, IProgress<string>? progress, int retryCount)
    {
        string html;
        try
        {
            html = await _client.GetStringAsync(url);
        }
        catch (Exception ex)
        {
            string err = $"Failed to read URL: {url} ({ex.Message})";
            progress?.Report(err);
            Log(err);
            return;
        }

        var matches = Regex.Matches(html, "<a href=\"([^\"]+?)\">", RegexOptions.IgnoreCase);

        Directory.CreateDirectory(localDir);

        foreach (Match match in matches)
        {
            string name = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(name) || name == "../") continue;

            string remotePath = url + name;

            if (name.EndsWith("/"))
            {
                // Handle folder
                string folderName = name.TrimEnd('/');
                string localPath = Path.Combine(localDir, folderName);
                await SyncDirectory(remotePath, localPath, progress, retryCount);
            }
            else
            {
                // Handle file
                string safeName = SanitizeFileName(name);
                string localPath = Path.Combine(localDir, safeName);
                
                // Special handling for .bz2 files - check if decompressed .bsp exists
                if (safeName.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase))
                {
                    string bspName = safeName.Substring(0, safeName.Length - 4); // Remove .bz2
                    string bspPath = Path.Combine(localDir, bspName);
                    
                    // Only download if the decompressed BSP doesn't exist
                    if (!File.Exists(bspPath))
                    {
                        await DownloadAndDecompressBz2(remotePath, bspPath, safeName, progress, retryCount);
                    }
                    else
                    {
                        Log($"Skipping {safeName} - decompressed file {bspName} already exists");
                    }
                }
                else if (!File.Exists(localPath))
                {
                    await DownloadFileWithResume(remotePath, localPath, safeName, progress, retryCount);
                }
            }
        }
    }

    private async Task DownloadAndDecompressBz2(string url, string finalBspPath, string displayName, IProgress<string>? progress, int retryCount)
    {
        string tempBz2Path = finalBspPath + ".bz2" + _tempExtension;
        string bz2Path = finalBspPath + ".bz2";
        long startByte = 0;

        // Check if there's a partial download
        if (File.Exists(tempBz2Path))
        {
            startByte = new FileInfo(tempBz2Path).Length;
            Log($"Resuming download of {displayName} from byte {startByte}");
            progress?.Report($"Resuming {displayName} from {FormatBytes(startByte)}...");
        }
        else
        {
            progress?.Report($"Downloading {displayName}...");
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
                progress?.Report($"Decompressing {displayName}...");
                Log($"Decompressing {displayName} to {Path.GetFileName(finalBspPath)}");
                
                // Move temp to bz2 path first
                if (File.Exists(bz2Path))
                {
                    File.Delete(bz2Path);
                }
                File.Move(tempBz2Path, bz2Path);
                
                // Decompress bz2 to bsp
                await DecompressBz2File(bz2Path, finalBspPath);
                
                // Delete the compressed file after successful decompression
                File.Delete(bz2Path);
                
                Log($"Successfully decompressed {displayName} to {Path.GetFileName(finalBspPath)}");
                progress?.Report($"Completed {displayName}");
                downloaded = true;
            }
            catch (Exception ex)
            {
                if (attempt == retryCount)
                {
                    string err = $"Failed: {displayName} after {retryCount} attempts ({ex.Message})";
                    progress?.Report(err);
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

    private async Task DownloadFileWithResume(string url, string localPath, string displayName, IProgress<string>? progress, int retryCount)
    {
        string tempPath = localPath + _tempExtension;
        long startByte = 0;

        // Check if there's a partial download
        if (File.Exists(tempPath))
        {
            startByte = new FileInfo(tempPath).Length;
            Log($"Resuming download of {displayName} from byte {startByte}");
            progress?.Report($"Resuming {displayName} from {FormatBytes(startByte)}...");
        }
        else
        {
            progress?.Report($"Downloading {displayName}...");
            Log($"Downloading {url} -> {localPath}");
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
                downloaded = true;
            }
            catch (Exception ex)
            {
                if (attempt == retryCount)
                {
                    string err = $"Failed: {displayName} after {retryCount} attempts ({ex.Message})";
                    progress?.Report(err);
                    Log(err);
                    
                    // Keep the temp file for future resume
                    Log($"Partial file saved at {tempPath} for future resume");
                }
                else
                {
                    Log($"Download attempt {attempt} failed for {displayName}, retrying...");
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

    private async Task DownloadRange(string url, string tempPath, long startByte, long totalSize, string displayName, IProgress<string>? progress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(startByte, null);

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
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
                int percentage = (int)((totalRead * 100) / totalSize);
                progress?.Report($"Downloading {displayName}... {percentage}% ({FormatBytes(totalRead)}/{FormatBytes(totalSize)})");
            }
        }
    }

    private async Task DownloadFull(string url, string tempPath, string displayName, IProgress<string>? progress)
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
                int percentage = (int)((totalRead * 100) / totalSize.Value);
                progress?.Report($"Downloading {displayName}... {percentage}% ({FormatBytes(totalRead)}/{FormatBytes(totalSize.Value)})");
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