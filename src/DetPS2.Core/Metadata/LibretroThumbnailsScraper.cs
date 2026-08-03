using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DetPS2.Core.Metadata;

/// <summary>
/// Best-effort free/public box-art fetch by display title (no API key) against
/// <c>libretro-thumbnails/Sony_-_PlayStation_2</c> (github.com/libretro-thumbnails —
/// RetroArch's public thumbnail set, No-Intro-style naming: <c>Title (Region).png</c>).
/// Title-indexed, not serial-indexed — lower hit rate than <see cref="SerialBoxArtScraper"/>
/// since it depends on the known display title closely matching No-Intro naming, but it's an
/// independent free source so it can succeed where the primary source has no entry.
/// Flat covers only — this set has no 3D box renders.
/// Provider id: <c>LibretroThumbnails</c>.
/// </summary>
public sealed class LibretroThumbnailsScraper : IBoxArtScraper, IDisposable
{
    public const string ProviderId = "LibretroThumbnails";

    private const string BaseUrl =
        "https://raw.githubusercontent.com/libretro-thumbnails/Sony_-_PlayStation_2/master/Named_Boxarts/";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public string ProviderName => ProviderId;

    public bool SupportsKind(BoxArtKind kind) => kind == BoxArtKind.Flat;

    public LibretroThumbnailsScraper(HttpClient? httpClient = null)
    {
        if (httpClient != null)
        {
            _http = httpClient;
            _ownsClient = false;
        }
        else
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("DetPS2Sharp/3.1 (box-art)");
            _ownsClient = true;
        }
    }

    /// <inheritdoc />
    public async Task<byte[]?> FetchAsync(
        string serial, string? titleHint, BoxArtKind kind, CancellationToken cancellationToken = default)
    {
        if (kind != BoxArtKind.Flat) return null;
        string baseTitle = CleanTitle(titleHint);
        if (baseTitle.Length == 0) return null;

        string? region = GuessRegion(serial);
        var names = new List<string>();
        if (region != null) names.Add($"{baseTitle} ({region})");
        names.Add($"{baseTitle} (USA)");
        names.Add($"{baseTitle} (Europe)");
        names.Add($"{baseTitle} (Japan)");
        names.Add($"{baseTitle} (World)");
        names.Add(baseTitle);

        foreach (string name in names)
        {
            string url = BaseUrl + EncodeForRawUrl(name) + ".png";
            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    continue;
                byte[] bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (bytes.Length < 32)
                    continue;
                if (bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50) // PNG magic
                    return bytes;
                if (bytes[0] == 0xFF && bytes[1] == 0xD8) // JPEG magic (just in case)
                    return bytes;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // try next candidate
            }
        }

        return null;
    }

    /// <summary>Strip an existing region/disc parenthetical and surrounding whitespace.</summary>
    private static string CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        int paren = title.IndexOf('(');
        string t = paren > 0 ? title[..paren] : title;
        return t.Trim();
    }

    private static string? GuessRegion(string serial)
    {
        if (string.IsNullOrEmpty(serial)) return null;
        string s = serial.ToUpperInvariant();
        if (s.StartsWith("SLUS") || s.StartsWith("SCUS")) return "USA";
        if (s.StartsWith("SLES") || s.StartsWith("SCES")) return "Europe";
        if (s.StartsWith("SLPS") || s.StartsWith("SCPS") || s.StartsWith("SLPM")) return "Japan";
        if (s.StartsWith("SLKA")) return "Korea";
        return null;
    }

    // GitHub raw content escapes spaces but leaves parentheses/apostrophes literal in path
    // segments (verified against live repo listings) — Uri.EscapeDataString would over-encode
    // parens and break the URL, so this only escapes what's actually needed.
    private static string EncodeForRawUrl(string name) => name.Replace(" ", "%20");

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }
}
