using System.Threading;
using System.Threading.Tasks;

namespace DetPS2.Core.Metadata;

/// <summary>
/// Online (or remote) box-art fetcher. Implementations must never block the UI thread;
/// return <c>null</c> on miss / network failure (caller decides whether to placeholder).
/// </summary>
public interface IBoxArtScraper
{
    /// <summary>Stable provider id matching <see cref="EmulatorConfig.ScraperProvider"/>.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Fetch raw image bytes for <paramref name="serial"/> (normalized or raw).
    /// Returns null if unavailable — never throws for expected miss/404.
    /// </summary>
    Task<byte[]?> FetchAsync(string serial, CancellationToken cancellationToken = default);
}
