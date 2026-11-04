using OptiscalerClient.Models;

namespace OptiscalerClient.Services;

/// <summary>
/// Common interface for all platform game scanners.
/// Implement this to add support for new platforms (e.g. Linux launchers).
/// </summary>
public interface IGameScanner
{
    Task<List<Game>> ScanAsync(CancellationToken cancellationToken = default);
}
