using System.Threading;
using System.Threading.Tasks;

namespace DetPS2.Core.Metadata;

/// <summary>
/// Online (or remote) box-art fetcher. Implementations must never block the UI thread;
/// return <c>null</c> on miss / network failure (caller decides whether to placeholder).
/// Multiple scrapers may be chained by <see cref="LibraryMetadataService"/> — each is tried
/// in configured order until one returns non-null bytes for the requested kind.
/// </summary>
public interface IBoxArtScraper
{
    /// <summary>Stable provider id (see individual scraper ProviderId constants).</summary>
    string ProviderName { get; }

    /// <summary>True when this provider can supply the given art kind at all (before trying).</summary>
    bool SupportsKind(BoxArtKind kind);

    /// <summary>
    /// Fetch raw image bytes for <paramref name="serial"/> (normalized or raw).
    /// <paramref name="titleHint"/> is the best-known display title — required by
    /// title-indexed sources (e.g. libretro-thumbnails); serial-indexed sources ignore it.
    /// Returns null if unavailable — never throws for expected miss/404.
    /// </summary>
    Task<byte[]?> FetchAsync(
        string serial,
        string? titleHint,
        BoxArtKind kind,
        CancellationToken cancellationToken = default);
}
