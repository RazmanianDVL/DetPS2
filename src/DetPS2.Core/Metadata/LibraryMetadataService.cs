using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DetPS2.Core.Metadata;

/// <summary>
/// Resolves disc serials via <see cref="MediaVerify"/> and optionally scrapes box art
/// into <see cref="LocalBoxArtCache"/>. All work is async / fire-and-forget friendly —
/// never call network paths on the UI thread without awaiting off-thread.
/// </summary>
public sealed class LibraryMetadataService : IDisposable
{
    private readonly EmulatorConfig _config;
    private readonly LocalBoxArtCache _cache;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GameMetadata> _memory = new(StringComparer.OrdinalIgnoreCase);
    private IBoxArtScraper? _scraper;
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

    public IBoxArtScraper ResolveScraper()
    {
        string provider = string.IsNullOrWhiteSpace(_config.ScraperProvider)
            ? NullBoxArtScraper.ProviderId
            : _config.ScraperProvider.Trim();

        if (string.Equals(provider, SerialBoxArtScraper.ProviderId, StringComparison.OrdinalIgnoreCase))
            return _scraper ??= new SerialBoxArtScraper();

        return NullBoxArtScraperHolder.Instance;
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

        string? cached = _cache.TryGet(serial);
        var meta = new GameMetadata
        {
            Serial = serial,
            Title = game?.TitleOverride ?? game?.DisplayName,
            BoxArtPath = cached ?? game?.BoxArtPath,
            Provider = cached != null ? "cache" : null
        };

        if (cached != null && game != null)
            game.BoxArtPath = cached;

        _memory[serial] = meta;

        if (_config.ScrapeBoxArt && cached == null)
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
            string? existing = _cache.TryGet(serial);
            if (existing != null)
            {
                Publish(serial, existing, game, "cache");
                return;
            }

            if (!_config.ScrapeBoxArt)
            {
                if (writePlaceholder)
                {
                    string path = _cache.Save(serial, LocalBoxArtCache.MinimalPlaceholderJpeg());
                    Publish(serial, path, game, "placeholder");
                }
                return;
            }

            IBoxArtScraper scraper = ResolveScraper();
            byte[]? bytes = null;
            try
            {
                bytes = await scraper.FetchAsync(serial, cancellationToken).ConfigureAwait(false);
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
                string path = _cache.Save(serial, bytes);
                Publish(serial, path, game, scraper.ProviderName);
                return;
            }

            if (writePlaceholder)
            {
                string path = _cache.Save(serial, LocalBoxArtCache.MinimalPlaceholderJpeg());
                Publish(serial, path, game, "placeholder");
            }
            else
            {
                // leave BoxArtPath empty — documented in METADATA_SCRAPE.md
                Publish(serial, null, game, scraper.ProviderName);
            }
        }
        finally
        {
            _inFlight.TryRemove(serial, out _);
        }
    }

    private void Publish(string serial, string? boxPath, GameSettings? game, string? provider)
    {
        if (game != null && boxPath != null)
            game.BoxArtPath = boxPath;

        var meta = new GameMetadata
        {
            Serial = serial,
            Title = game?.TitleOverride ?? game?.DisplayName,
            BoxArtPath = boxPath,
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
        string? path = _cache.TryGet(serial);
        if (path == null) return false;
        meta = new GameMetadata { Serial = serial, BoxArtPath = path, Provider = "cache" };
        _memory[serial] = meta;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_scraper is IDisposable d)
            d.Dispose();
        _scraper = null;
    }

    private static class NullBoxArtScraperHolder
    {
        public static readonly NullBoxArtScraper Instance = new();
    }
}
