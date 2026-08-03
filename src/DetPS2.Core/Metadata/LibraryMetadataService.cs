using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DetPS2.Core.Metadata;

/// <summary>
/// Resolves disc serials via <see cref="MediaVerify"/> and optionally scrapes box art
/// (flat + optional 3D case render) into <see cref="LocalBoxArtCache"/>. All work is async /
/// fire-and-forget friendly — never call network paths on the UI thread without awaiting
/// off-thread.
/// <para>
/// Multiple scrapers may be configured (<see cref="EmulatorConfig.ScraperProvider"/> plus the
/// additive <c>Use*</c> toggles) — <see cref="ResolveScrapers"/> returns them in priority
/// order and each art kind is tried against each scraper in turn until one hits.
/// </para>
/// </summary>
public sealed class LibraryMetadataService : IDisposable
{
    private readonly EmulatorConfig _config;
    private readonly LocalBoxArtCache _cache;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GameMetadata> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IBoxArtScraper> _ownedScrapers = new();
    private bool _disposed;

    public LibraryMetadataService(EmulatorConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        string? root = string.IsNullOrWhiteSpace(config.MetadataCacheDir)
            ? null
            : config.MetadataCacheDir;
        _cache = new LocalBoxArtCache(root);
    }

    public LocalBoxArtCache Cache => _cache;

    public event Action<GameMetadata>? MetadataUpdated;

    /// <summary>Configured scrapers in try-order. Lazily created once, reused for the service lifetime.</summary>
    public IReadOnlyList<IBoxArtScraper> ResolveScrapers()
    {
        if (_ownedScrapers.Count > 0)
            return _ownedScrapers;

        string provider = string.IsNullOrWhiteSpace(_config.ScraperProvider)
            ? NullBoxArtScraper.ProviderId
            : _config.ScraperProvider.Trim();

        if (string.Equals(provider, SerialBoxArtScraper.ProviderId, StringComparison.OrdinalIgnoreCase))
            _ownedScrapers.Add(new SerialBoxArtScraper());

        if (_config.UseLibretroThumbnails)
            _ownedScrapers.Add(new LibretroThumbnailsScraper());

        if (_config.UseScreenScraper && !string.IsNullOrWhiteSpace(_config.ScreenScraperUser))
            _ownedScrapers.Add(new ScreenScraperBoxArtScraper(_config));

        return _ownedScrapers;
    }

    /// <summary>
    /// Ensure <see cref="GameSettings.Serial"/> is populated (sync disc read) and enqueue
    /// optional box-art scrape. Returns immediately after serial extract; scrape continues async.
    /// </summary>
    public async Task<GameMetadata> EnsureSerialAndEnqueueAsync(
        string path,
        GameSettings? game = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path required.", nameof(path));

        string? serial = game?.Serial;
        if (string.IsNullOrWhiteSpace(serial))
        {
            // Disc identify can be slow on UNC — offload
            MediaVerify.Report report = await Task.Run(() => MediaVerify.Identify(path), cancellationToken)
                .ConfigureAwait(false);
            serial = report.Serial;
            if (game != null && !string.IsNullOrEmpty(serial))
                game.Serial = serial;
            if (game != null && !string.IsNullOrEmpty(report.VolumeId) &&
                string.IsNullOrEmpty(game.TitleOverride))
            {
                // soft hint only — do not overwrite DisplayName here
            }
        }

        if (string.IsNullOrWhiteSpace(serial))
        {
            var empty = new GameMetadata
            {
                Serial = "",
                Title = game?.TitleOverride ?? game?.DisplayName,
                Provider = "none"
            };
            return empty;
        }

        serial = MediaVerify.NormalizeSerial(serial);
        if (game != null)
            game.Serial = serial;

        string? cachedFlat = _cache.TryGet(serial, BoxArtKind.Flat);
        string? cached3D = _cache.TryGet(serial, BoxArtKind.ThreeD);
        var meta = new GameMetadata
        {
            Serial = serial,
            Title = game?.TitleOverride ?? game?.DisplayName,
            BoxArtPath = cachedFlat ?? game?.BoxArtPath,
            BoxArt3DPath = cached3D ?? game?.BoxArt3DPath,
            Provider = cachedFlat != null ? "cache" : null
        };

        if (cachedFlat != null && game != null)
            game.BoxArtPath = cachedFlat;
        if (cached3D != null && game != null)
            game.BoxArt3DPath = cached3D;

        _memory[serial] = meta;

        bool needFlat = _config.ScrapeBoxArt && cachedFlat == null;
        bool need3D = _config.ScrapeBoxArt && _config.Scrape3DBoxArt && cached3D == null;
        if (needFlat || need3D)
            _ = EnqueueScrapeAsync(serial, game, writePlaceholder: false, cancellationToken);

        return meta;
    }

    /// <summary>
    /// Fire-and-forget friendly enqueue. Deduplicates in-flight serials.
    /// </summary>
    public Task EnqueueScrapeAsync(
        string serial,
        GameSettings? game = null,
        bool writePlaceholder = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serial))
            return Task.CompletedTask;
        serial = MediaVerify.NormalizeSerial(serial);

        if (!_inFlight.TryAdd(serial, 0))
            return Task.CompletedTask;

        return ScrapeCoreAsync(serial, game, writePlaceholder, cancellationToken);
    }

    private async Task ScrapeCoreAsync(
        string serial,
        GameSettings? game,
        bool writePlaceholder,
        CancellationToken cancellationToken)
    {
        try
        {
            string? titleHint = game?.TitleOverride ?? game?.DisplayName;
            string? flatPath = _cache.TryGet(serial, BoxArtKind.Flat);
            string? threeDPath = _cache.TryGet(serial, BoxArtKind.ThreeD);
            string? flatProvider = flatPath != null ? "cache" : null;

            if (_config.ScrapeBoxArt)
            {
                if (flatPath == null)
                    (flatPath, flatProvider) = await TryFetchAndSaveAsync(
                        serial, titleHint, BoxArtKind.Flat, cancellationToken).ConfigureAwait(false);

                if (_config.Scrape3DBoxArt && threeDPath == null)
                    (threeDPath, _) = await TryFetchAndSaveAsync(
                        serial, titleHint, BoxArtKind.ThreeD, cancellationToken).ConfigureAwait(false);
            }

            if (flatPath == null && writePlaceholder)
            {
                flatPath = _cache.Save(serial, LocalBoxArtCache.MinimalPlaceholderJpeg(), BoxArtKind.Flat);
                flatProvider = "placeholder";
            }

            Publish(serial, flatPath, threeDPath, game, flatProvider);
        }
        finally
        {
            _inFlight.TryRemove(serial, out _);
        }
    }

    /// <summary>Try every configured scraper (that supports this kind) in order; first hit wins.</summary>
    private async Task<(string? path, string? provider)> TryFetchAndSaveAsync(
        string serial, string? titleHint, BoxArtKind kind, CancellationToken cancellationToken)
    {
        foreach (var scraper in ResolveScrapers())
        {
            if (!scraper.SupportsKind(kind))
                continue;

            byte[]? bytes;
            try
            {
                bytes = await scraper.FetchAsync(serial, titleHint, kind, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                bytes = null;
            }

            if (bytes != null && bytes.Length > 0)
            {
                string path = _cache.Save(serial, bytes, kind);
                return (path, scraper.ProviderName);
            }
        }

        return (null, null);
    }

    private void Publish(string serial, string? boxPath, string? box3DPath, GameSettings? game, string? provider)
    {
        if (game != null)
        {
            if (boxPath != null) game.BoxArtPath = boxPath;
            if (box3DPath != null) game.BoxArt3DPath = box3DPath;
        }

        var meta = new GameMetadata
        {
            Serial = serial,
            Title = game?.TitleOverride ?? game?.DisplayName,
            BoxArtPath = boxPath,
            BoxArt3DPath = box3DPath,
            LastFetchedUtc = DateTimeOffset.UtcNow,
            Provider = provider
        };
        _memory[serial] = meta;
        try { MetadataUpdated?.Invoke(meta); }
        catch { /* host handlers must not break scrape */ }
    }

    public bool TryGetCached(string serial, out GameMetadata? meta)
    {
        meta = null;
        if (string.IsNullOrWhiteSpace(serial)) return false;
        serial = MediaVerify.NormalizeSerial(serial);
        if (_memory.TryGetValue(serial, out var m))
        {
            meta = m;
            return true;
        }
        string? path = _cache.TryGet(serial, BoxArtKind.Flat);
        string? path3D = _cache.TryGet(serial, BoxArtKind.ThreeD);
        if (path == null && path3D == null) return false;
        meta = new GameMetadata { Serial = serial, BoxArtPath = path, BoxArt3DPath = path3D, Provider = "cache" };
        _memory[serial] = meta;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var scraper in _ownedScrapers)
            if (scraper is IDisposable d)
                d.Dispose();
        _ownedScrapers.Clear();
    }
}
