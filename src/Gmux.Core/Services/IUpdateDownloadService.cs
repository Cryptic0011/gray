using Gmux.Core.Models;

namespace Gmux.Core.Services;

public interface IUpdateDownloadService
{
    Task<string> DownloadAsync(
        UpdateCheckResult result,
        IProgress<double> progress,
        CancellationToken cancellationToken);
}
