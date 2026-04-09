using Gmux.Core.Models;

namespace Gmux.Core.Services;

public interface IUpdateCheckerService
{
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default);
}
