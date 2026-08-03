using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DetPS2.Core.Metadata;

/// <summary>
/// Box-art fetch against screenscraper.fr's <c>jeuInfos.php</c> API (systemeid 58 = Sony
/// PlayStation 2) — the only source wired here with genuine <c>box-3D</c> media (rendered
/// case art), alongside <c>box-2D</c> flat covers. See docs/METADATA_SCRAPE.md for the full
/// ToS summary.
/// <para>
/// Auth: requires the end user's own free ScreenScraper account (<c>ssid</c>/<c>sspassword</c>
/// — register at screenscraper.fr, no cost). An optional developer id/password pair
/// (<c>devid</c>/<c>devpassword</c>) raises the personal rate limit but is not required —
/// per this project's "no API keys in-tree" rule, neither pair is ever embedded; both are
/// read from <see cref="EmulatorConfig"/>, which the user fills in under Options → Metadata.
/// Inactive (returns null immediately) when no ssid is configured.
/// </para>
/// <para>
/// Matching: ScreenScraper is primarily hash/filename-based; disc serials aren't a first-class
/// lookup key on this API, so this queries by the best-known display title (<c>romnom</c>) —
/// their backend does its own fuzzy title matching. Best-effort, like the other scrapers here.
/// </para>
/// Provider id: <c>ScreenScraper</c>.
/// </summary>
public sealed class ScreenScraperBoxArtScraper : IBoxArtScraper, IDisposable
{
    public const string ProviderId = "ScreenScraper";
    private const string BaseUrl = "https://www.screenscraper.fr/api2/jeuInfos.php";
    private const int SystemePs2 = 58;

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly string _devId;
    private readonly string _devPassword;
    private readonly string _user;
    private readonly string _password;

    public string ProviderName => ProviderId;

    public bool SupportsKind(BoxArtKind kind) => true;

    public ScreenScraperBoxArtScraper(EmulatorConfig config, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _devId = config.ScreenScraperDevId ?? "";
        _devPassword = config.ScreenScraperDevPassword ?? "";
        _user = config.ScreenScraperUser ?? "";
        _password = config.ScreenScraperPassword ?? "";

        if (httpClient != null)
        {
            _http = httpClient;
            _ownsClient = false;
        }
        else
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("DetPS2Sharp/3.1 (box-art)");
            _ownsClient = true;
        }
    }

    /// <inheritdoc />
    public async Task<byte[]?> FetchAsync(
        string serial, string? titleHint, BoxArtKind kind, CancellationToken cancellationToken = default)
    {
        // Inactive without the user's own account — never guess/embed credentials.
        if (string.IsNullOrWhiteSpace(_user))
            return null;

        string romNom = BuildRomNom(titleHint, serial);
        if (romNom.Length == 0)
            return null;

        string requestUrl = BuildRequestUrl(romNom);

        try
        {
            using var resp = await _http.GetAsync(requestUrl, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            string json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string? mediaUrl = ExtractMediaUrl(json, kind);
            if (string.IsNullOrEmpty(mediaUrl))
                return null;

            using var imgResp = await _http.GetAsync(mediaUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!imgResp.IsSuccessStatusCode)
                return null;
            byte[] bytes = await imgResp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return bytes.Length >= 32 ? bytes : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private string BuildRequestUrl(string romNom)
    {
        var qs = new StringBuilder(BaseUrl).Append('?');
        if (!string.IsNullOrEmpty(_devId))
            qs.Append("devid=").Append(Uri.EscapeDataString(_devId)).Append('&');
        if (!string.IsNullOrEmpty(_devPassword))
            qs.Append("devpassword=").Append(Uri.EscapeDataString(_devPassword)).Append('&');
        qs.Append("softname=").Append(Uri.EscapeDataString("DetPS2Sharp")).Append('&');
        qs.Append("ssid=").Append(Uri.EscapeDataString(_user)).Append('&');
        qs.Append("sspassword=").Append(Uri.EscapeDataString(_password)).Append('&');
        qs.Append("systemeid=").Append(SystemePs2).Append('&');
        qs.Append("output=json&");
        qs.Append("romnom=").Append(Uri.EscapeDataString(romNom));
        return qs.ToString();
    }

    private static string BuildRomNom(string? titleHint, string serial)
    {
        if (!string.IsNullOrWhiteSpace(titleHint))
            return titleHint.Trim() + ".iso";
        return string.IsNullOrWhiteSpace(serial) ? "" : serial.Trim();
    }

    /// <summary>
    /// Response shape: <c>{"response":{"jeu":{"medias":[{"type":"box-2D","region":"us","url":"..."}]}}}</c>
    /// (verified against public ScreenScraper client source; see METADATA_SCRAPE.md). Prefers
    /// US/World region when multiple regional variants exist for the wanted media type.
    /// </summary>
    private static string? ExtractMediaUrl(string json, BoxArtKind kind)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var response)) return null;
            if (!response.TryGetProperty("jeu", out var jeu)) return null;
            if (!jeu.TryGetProperty("medias", out var medias) || medias.ValueKind != JsonValueKind.Array) return null;

            string wantType = kind == BoxArtKind.ThreeD ? "box-3D" : "box-2D";
            string? fallbackUrl = null;
            foreach (var media in medias.EnumerateArray())
            {
                if (!media.TryGetProperty("type", out var typeProp)) continue;
                if (!string.Equals(typeProp.GetString(), wantType, StringComparison.OrdinalIgnoreCase)) continue;
                if (!media.TryGetProperty("url", out var urlProp)) continue;
                string? url = urlProp.GetString();
                if (string.IsNullOrEmpty(url)) continue;

                string region = media.TryGetProperty("region", out var regionProp) ? regionProp.GetString() ?? "" : "";
                if (string.Equals(region, "us", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(region, "wor", StringComparison.OrdinalIgnoreCase))
                    return url;
                fallbackUrl ??= url;
            }
            return fallbackUrl;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }
}
