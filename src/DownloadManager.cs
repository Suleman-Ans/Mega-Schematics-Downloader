using System.IO;
namespace MegaSchematicsDownloader;

public readonly record struct DownloadProgress(int Done, int Total, long BytesDone, long BytesTotal, string CurrentFile);

public sealed class DownloadResult
{
    public int Total { get; set; }
    public int Downloaded { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public long Bytes { get; set; }
    public bool Cancelled { get; set; }
    public bool QuotaReached { get; set; }
    public bool DiskFull { get; set; }
    public List<string> Errors { get; } = new();

    public int NotAttempted => Math.Max(0, Total - Downloaded - Skipped - Failed);
}

/// <summary>
/// Sequential, resumable downloader: skips files already on disk, writes to a partial file first,
/// retries transient errors, and stops cleanly on cancel, MEGA quota or a full disk.
/// </summary>
public sealed class DownloadManager
{
    private const int MaxAttempts = 3;
    private readonly MegaService _mega;

    public DownloadManager(MegaService mega) => _mega = mega;

    public async Task<DownloadResult> RunAsync(
        IReadOnlyList<MegaItem> files, string root, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        var result = new DownloadResult { Total = files.Count };
        var rootFull = Path.GetFullPath(root);
        var rootPrefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;

        long bytesTotal = 0;
        foreach (var file in files) bytesTotal += file.Size;
        long bytesDone = 0;
        var done = 0;

        foreach (var file in files)
        {
            if (token.IsCancellationRequested)
            {
                result.Cancelled = true;
                break;
            }

            progress?.Report(new DownloadProgress(done, files.Count, bytesDone, bytesTotal, file.Name));

            try
            {
                var destination = Path.GetFullPath(Path.Combine(rootFull, file.RelativePath));
                if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The target path is outside the download folder.");

                if (File.Exists(destination))
                {
                    result.Skipped++;
                }
                else
                {
                    await DownloadWithRetryAsync(file, destination, token);
                    result.Downloaded++;
                    result.Bytes += file.Size;
                }
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                break;
            }
            catch (Exception ex) when (IsQuota(ex))
            {
                result.QuotaReached = true;
                break;
            }
            catch (Exception ex) when (IsDiskFull(ex))
            {
                result.DiskFull = true;
                break;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{file.DisplayPath}: {FirstLine(ex.Message)}");
            }

            done++;
            bytesDone += file.Size;
        }

        progress?.Report(new DownloadProgress(done, files.Count, bytesDone, bytesTotal, string.Empty));
        return result;
    }

    private async Task DownloadWithRetryAsync(MegaItem file, string destination, CancellationToken token)
    {
        var node = file.Node ?? throw new InvalidOperationException("The live MEGA listing has not loaded yet.");
        var temporary = destination + ".download-partial";
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(destination)) return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                TryDelete(temporary);
                await _mega.DownloadFileAsync(node, temporary);
                if (!File.Exists(temporary))
                    throw new IOException("MEGA did not produce the expected download file.");
                File.Move(temporary, destination);
                return;
            }
            catch (Exception ex)
            {
                TryDelete(temporary);
                if (ex is OperationCanceledException || attempt >= MaxAttempts || !IsRetryable(ex)) throw;
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), token);
            }
        }
    }

    private static bool IsRetryable(Exception ex) =>
        !(IsQuota(ex) || IsDiskFull(ex) ||
          ex is UnauthorizedAccessException or PathTooLongException or DirectoryNotFoundException
              or InvalidDataException or NotSupportedException or ArgumentException);

    private static bool IsQuota(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            // Local file system errors can mention a file name; they are never a MEGA quota error.
            if (current is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                continue;

            var text = current.Message ?? string.Empty;
            if (text.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("bandwidth", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("(509)", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool IsDiskFull(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            // Win32 ERROR_HANDLE_DISK_FULL (39) and ERROR_DISK_FULL (112).
            if (current is IOException)
            {
                var code = current.HResult & 0xFFFF;
                if (code == 39 || code == 112) return true;
            }
        }
        return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* The original error is the useful one. */ }
    }

    private static string FirstLine(string message)
    {
        var newline = message.IndexOfAny(new[] { '\r', '\n' });
        return newline >= 0 ? message.Substring(0, newline) : message;
    }
}
