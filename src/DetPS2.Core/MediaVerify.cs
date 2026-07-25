using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DetPS2.Core;

/// <summary>
/// Identify / sanity-check PS2 disc images via SYSTEM.CNF serial + quick hash.
/// Optional online serial lookup (best-effort; offline heuristics always work).
/// </summary>
public static class MediaVerify
{
    private static readonly Regex SerialRx = new(
        @"\b((?:SLUS|SLES|SCES|SCUS|SLPS|SLPM|SCPS|SCAJ|SLAJ|SLKA|SCKA|PBPX|PAPX)[_-]?\d{3}\.?\d{2})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed class Report
    {
        public string Path { get; set; } = "";
        public long SizeBytes { get; set; }
        public string? VolumeId { get; set; }
        public string? Serial { get; set; }
        public string? Boot2 { get; set; }
        public string QuickSha256 { get; set; } = "";
        public bool LooksLikePs2 { get; set; }
        public bool OnlineChecked { get; set; }
        public bool OnlineMatch { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// Quick hash: SHA-256 over first 1 MiB + last 64 KiB + size (does not hash whole 5 GB disc).
    /// </summary>
    public static string ComputeQuickSha256(string path)
    {
        path = FileDiscImage.NormalizePath(path);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.SequentialScan);
        long len = fs.Length;
        using var sha = SHA256.Create();
        byte[] head = new byte[(int)Math.Min(1024 * 1024, len)];
        int n = fs.Read(head, 0, head.Length);
        sha.TransformBlock(head, 0, n, null, 0);
        if (len > head.Length)
        {
            long tail = Math.Min(64 * 1024, len);
            fs.Seek(len - tail, SeekOrigin.Begin);
            byte[] tbuf = new byte[tail];
            int tn = fs.Read(tbuf, 0, tbuf.Length);
            sha.TransformBlock(tbuf, 0, tn, null, 0);
        }
        byte[] sizeBytes = BitConverter.GetBytes(len);
        sha.TransformFinalBlock(sizeBytes, 0, sizeBytes.Length);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    public static Report Identify(string path)
    {
        path = FileDiscImage.NormalizePath(path);
        var report = new Report { Path = path };
        if (!File.Exists(path))
        {
            report.Message = "File not found";
            return report;
        }

        var fi = new FileInfo(path);
        report.SizeBytes = fi.Length;

        try
        {
            using var disc = new FileDiscImage(path);
            var vol = Iso9660.Open(disc);
            if (vol == null)
            {
                report.QuickSha256 = ComputeQuickSha256(path);
                report.Message = "Not a readable ISO9660 image";
                return report;
            }

            string? serial = null;
            string? boot2 = null;
            string? cnfText = null;
            byte[]? cnf = Iso9660.ReadFile(vol, "SYSTEM.CNF");
            if (cnf != null)
            {
                cnfText = Encoding.ASCII.GetString(cnf);
                var cnfP = SystemCnf.Parse(cnfText);
                boot2 = cnfP.BootFileName;
                var m = SerialRx.Match(cnfText);
                if (m.Success) serial = NormalizeSerial(m.Groups[1].Value);
                if (serial == null && boot2 != null)
                {
                    m = SerialRx.Match(boot2);
                    if (m.Success) serial = NormalizeSerial(m.Groups[1].Value);
                }
            }

            bool looks = cnf != null && boot2 != null &&
                         (serial != null || (cnfText != null && textLooksPs2Boot(cnfText)));

            string qh;
            try { qh = ComputeQuickSha256(path); }
            catch { qh = ""; }

            report.VolumeId = vol.VolumeId;
            report.Serial = serial;
            report.Boot2 = boot2;
            report.QuickSha256 = qh;
            report.LooksLikePs2 = looks;
            report.Message = looks
                ? $"PS2 media likely (serial={serial ?? "n/a"}, boot={boot2}, size={fi.Length / (1024 * 1024)}MB)"
                : "ISO readable but SYSTEM.CNF/BOOT2 does not look like PS2";
            return report;
        }
        catch (Exception ex)
        {
            report.Message = ex.Message;
            return report;
        }
    }

    private static bool textLooksPs2Boot(string cnf) =>
        cnf.Contains("BOOT2", StringComparison.OrdinalIgnoreCase) ||
        cnf.Contains("cdrom0", StringComparison.OrdinalIgnoreCase);

    /// <summary>Extract + normalize a disc serial (e.g. "SLUS_210.87") from SYSTEM.CNF text
    /// and/or the resolved BOOT2 filename, without re-reading the disc. Used by DiscBoot to
    /// key GameQuirkRegistry lookups the same way Identify() keys its own report.</summary>
    public static string? ExtractSerial(string? cnfText, string? boot2)
    {
        if (!string.IsNullOrEmpty(cnfText))
        {
            var m = SerialRx.Match(cnfText);
            if (m.Success) return NormalizeSerial(m.Groups[1].Value);
        }
        if (!string.IsNullOrEmpty(boot2))
        {
            var m = SerialRx.Match(boot2);
            if (m.Success) return NormalizeSerial(m.Groups[1].Value);
        }
        return null;
    }

    public static string NormalizeSerial(string s)
    {
        s = s.ToUpperInvariant().Replace("-", "_").Replace(" ", "");
        // SLUS_123.45 style
        if (s.Length >= 11 && s[4] != '_')
            s = s.Insert(4, "_");
        return s;
    }

    /// <summary>
    /// Best-effort online check: fetch a public serial list cache (or use AppData cache).
    /// Does not require full redump DAT (whole-image MD5 of 5 GB is too slow for UI).
    /// </summary>
    public static async Task<Report> IdentifyWithOnlineAsync(string path, bool allowNetwork = true)
    {
        var local = Identify(path);
        if (!allowNetwork || string.IsNullOrEmpty(local.Serial))
            return local;

        try
        {
            string cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DetPS2", "media-db");
            Directory.CreateDirectory(cacheDir);
            string cacheFile = Path.Combine(cacheDir, "ps2-serials.txt");

            if (!File.Exists(cacheFile) || File.GetLastWriteTimeUtc(cacheFile) < DateTime.UtcNow.AddDays(-30))
            {
                // Community serial list (plain text, one serial per line) — best effort URL
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("DetPS2Sharp/3.1 (media-verify)");
                // Fallback: write built-in seeds if download fails
                try
                {
                    // No guaranteed free redump API; seed cache with format note + optional fetch
                    string url = "https://raw.githubusercontent.com/nicoboss/redump-serials/master/ps2.txt";
                    string body = await http.GetStringAsync(url);
                    if (body.Length > 32)
                        await File.WriteAllTextAsync(cacheFile, body);
                }
                catch
                {
                    if (!File.Exists(cacheFile))
                        await File.WriteAllTextAsync(cacheFile, "# offline seed — serial pattern check only\n");
                }
            }

            bool match = false;
            if (File.Exists(cacheFile) && local.Serial != null)
            {
                foreach (string line in File.ReadLines(cacheFile))
                {
                    if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
                    if (line.Contains(local.Serial, StringComparison.OrdinalIgnoreCase) ||
                        NormalizeSerial(line.Trim()) == local.Serial)
                    {
                        match = true;
                        break;
                    }
                }
            }

            local.OnlineChecked = true;
            local.OnlineMatch = match;
            local.Message += match
                ? " | serial found in online/cache list"
                : " | serial not in cache (still may be valid PS2)";
            return local;
        }
        catch (Exception ex)
        {
            local.OnlineChecked = true;
            local.Message += " | online: " + ex.Message;
            return local;
        }
    }
}
