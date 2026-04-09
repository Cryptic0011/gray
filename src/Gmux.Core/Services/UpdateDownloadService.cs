using Gmux.Core.Models;

namespace Gmux.Core.Services;

public class UpdateDownloadService : IUpdateDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly string _downloadDirectory;

    public UpdateDownloadService(HttpClient? httpClient = null, string? downloadDirectory = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _downloadDirectory = downloadDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "gray",
            "updates");
    }

    public async Task<string> DownloadAsync(
        UpdateCheckResult result,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.MsiAssetUrl))
            throw new UpdateDownloadException("No MSI asset URL in update result");
        if (string.IsNullOrWhiteSpace(result.LatestVersion))
            throw new UpdateDownloadException("No version in update result");

        Directory.CreateDirectory(_downloadDirectory);
        var safeVersion = result.LatestVersion.TrimStart('v', 'V');
        var targetPath = Path.Combine(_downloadDirectory, $"gray-{safeVersion}.msi");
        var tempPath = targetPath + ".partial";

        try
        {
            using var response = await _httpClient.GetAsync(
                result.MsiAssetUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new UpdateDownloadException(
                    $"Download failed: {(int)response.StatusCode} {response.ReasonPhrase} ({result.MsiAssetUrl})");

            var total = response.Content.Headers.ContentLength ?? result.MsiAssetSizeBytes ?? 0L;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var destination = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                useAsync: true))
            {
                var buffer = new byte[64 * 1024];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                    read += n;
                    if (total > 0)
                        progress.Report(Math.Min(1.0, (double)read / total));
                }
            }

            if (File.Exists(targetPath))
                File.Delete(targetPath);
            File.Move(tempPath, targetPath);
            progress.Report(1.0);
            return targetPath;
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (UpdateDownloadException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            throw new UpdateDownloadException($"Download failed: {ex.Message}", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }
}
