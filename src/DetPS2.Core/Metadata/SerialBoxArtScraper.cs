using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DetPS2.Core.Metadata;

/// <summary>
/// Best-effort free/public box-art fetch by disc serial (no API key) against
/// <c>xlenore/ps2-covers</c> (github.com/xlenore/ps2-covers — community-maintained PS2 cover
/// collection, public raw HTTPS, no auth). Returns null on miss/failure.
/// Provider id: <c>SerialHttp</c>.
/// <para>
/// Verified live against the repo (2026-08-02): <c>covers/default/</c> (flat) is <c>.jpg</c>;
/// <c>covers/3d/</c> is <c>.png</c> — a prior version of this scraper requested <c>.jpg</c> for
/// the 3D path too, which 404'd every time (the 3D fallback silently never worked).
/// </para>
/// </summary>
public sealed class SerialBoxArtScraper : IBoxArtScraper, IDisposable
{
    public const string ProviderId = "SerialHttp";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public string ProviderName => ProviderId;

    public bool SupportsKind(BoxArtKind kind) => true; // flat + 3D both hosted

    public SerialBoxArtScraper(HttpClient? httpClient = null)
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
        if (string.IsNullOrWhiteSpace(serial))
            return null;

        string norm = MediaVerify.NormalizeSerial(serial);
        // Common community layouts use SLUS-21087 (hyphen, no dot) or SLUS_210.87
        string hyphenNoDot = norm.Replace('_', '-').Replace(".", "");
        string underscoreDot = norm;

        string[] candidates = kind == BoxArtKind.ThreeD
            ? new[]
            {
                $"https://raw.githubusercontent.com/xlenore/ps2-covers/main/covers/3d/{hyphenNoDot}.png",
                $"https://raw.githubusercontent.com/xlenore/ps2-covers/main/covers/3d/{underscoreDot}.png",
            }
            : new[]
            {
                $"https://raw.githubusercontent.com/xlenore/ps2-covers/main/covers/default/{hyphenNoDot}.jpg",
                $"https://raw.githubusercontent.com/xlenore/ps2-covers/main/covers/default/{underscoreDot}.jpg",
            };

        foreach (string url in candidates)
        {
            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    continue;
                byte[] bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (bytes.Length < 32)
                    continue;
                // crude JPEG/PNG magic check
                if (bytes[0] == 0xFF && bytes[1] == 0xD8)
                    return bytes;
                if (bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50)
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

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }
}

/// <summary>
/// No-op scraper — always returns null (local cache only). Provider id: <c>LocalOnly</c>.
/// </summary>
public sealed class NullBoxArtScraper : IBoxArtScraper
{
    public const string ProviderId = "LocalOnly";
    public string ProviderName => ProviderId;
    public bool SupportsKind(BoxArtKind kind) => false;

    public Task<byte[]?> FetchAsync(
        string serial, string? titleHint, BoxArtKind kind, CancellationToken cancellationToken = default) =>
        Task.FromResult<byte[]?>(null);
}
