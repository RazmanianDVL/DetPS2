using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DetPS2.Core.Metadata;

/// <summary>
/// Best-effort free/public box-art fetch by disc serial (no API key).
/// Tries community cover repos over raw HTTPS; returns null on miss/failure.
/// Provider id: <c>SerialHttp</c>.
/// </summary>
public sealed class SerialBoxArtScraper : IBoxArtScraper, IDisposable
{
    public const string ProviderId = "SerialHttp";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public string ProviderName => ProviderId;

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
    public async Task<byte[]?> FetchAsync(string serial, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serial))
            return null;

        string norm = MediaVerify.NormalizeSerial(serial);
        // Common community layouts use SLUS-21087 (hyphen, no dot) or SLUS_210.87
        string hyphenNoDot = norm.Replace('_', '-').Replace(".", "");
        string underscoreDot = norm;

        string[] candidates =
        {
            // xlenore/ps2-covers — public GitHub raw (best-effort; may 404)
            $"https://raw.githubusercontent.com/xlenore/ps2-covers/main/covers/default/{hyphenNoDot}.jpg",
            $"https://raw.githubusercontent.com/xlenore/ps2-covers/main/covers/default/{underscoreDot}.jpg",
            $"https://raw.githubusercontent.com/xlenore/ps2-covers/main/covers/3d/{hyphenNoDot}.jpg",
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

    public Task<byte[]?> FetchAsync(string serial, CancellationToken cancellationToken = default) =>
        Task.FromResult<byte[]?>(null);
}
