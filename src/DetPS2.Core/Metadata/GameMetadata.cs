using System;

namespace DetPS2.Core.Metadata;

/// <summary>
/// Cached / resolved metadata for a PS2 title keyed primarily by disc serial.
/// </summary>
public sealed class GameMetadata
{
    /// <summary>Normalized serial, e.g. <c>SLUS_210.87</c>.</summary>
    public string Serial { get; set; } = "";

    /// <summary>Display title if known (from override, scraper, or SYSTEM.CNF heuristics).</summary>
    public string? Title { get; set; }

    /// <summary>Absolute path to local box art image (JPEG/PNG), if present.</summary>
    public string? BoxArtPath { get; set; }

    /// <summary>When online metadata was last attempted (UTC).</summary>
    public DateTimeOffset? LastFetchedUtc { get; set; }

    /// <summary>Scraper / provider that produced this entry (e.g. LocalOnly, SerialHttp).</summary>
    public string? Provider { get; set; }

    public static GameMetadata ForSerial(string serial) =>
        new() { Serial = MediaVerify.NormalizeSerial(serial) };
}
