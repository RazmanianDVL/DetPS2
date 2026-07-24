using System;
using System.IO;
using System.Text;
using DetPS2.Core;

namespace DetPS2.Desktop;

/// <summary>
/// Detailed session log under %TEMP%\DetPS2\session-*.log for hang diagnosis.
/// </summary>
public sealed class SessionLog : IDisposable
{
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private long _lineCount;

    public string? LogPath { get; private set; }
    public string TempDir { get; }

    public SessionLog()
    {
        TempDir = Path.Combine(Path.GetTempPath(), "DetPS2");
        Directory.CreateDirectory(TempDir);
        string name = $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log";
        LogPath = Path.Combine(TempDir, name);
        _writer = new StreamWriter(new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
        {
            AutoFlush = true
        };
        Write("=== DetPS2 session log ===");
        Write($"Started {DateTime.Now:O}");
        Write($"TempDir={TempDir}");
        Write($"Version={VersionInfo.Banner}");
    }

    public void Write(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (_lock)
        {
            try
            {
                _writer?.WriteLine(line);
                _lineCount++;
            }
            catch { /* ignore disk full */ }
        }
    }

    public void WriteDetail(string category, string message) =>
        Write($"[{category}] {message}");

    public void WriteSystemSnapshot(Ps2System? sys, string reason = "snapshot")
    {
        if (sys == null)
        {
            WriteDetail(reason, "system=null");
            return;
        }
        try
        {
            var sb = new StringBuilder();
            sb.Append($"PC=0x{sys.EE.PC:X8} cycles={sys.MasterCycles} ");
            sb.Append($"px={sys.Gs.PixelsWritten} gifP3={sys.Gif.Path3Transfers} ");
            sb.Append($"overlay={sys.Gs.HostOverlayActive} ");
            sb.Append($"assist={sys.MidwayAssist.Status} ");
            sb.Append($"fmv={sys.MidwayAssist.LogoFrame}/{sys.MidwayAssist.LogoFramesTotal} ");
            sb.Append($"fmvPresented={sys.MidwayAssist.FramesPresented} ");
            sb.Append($"cdvd={(sys.Cdvd.MountedPath ?? "none")} discLen={sys.Cdvd.ImageLength} ");
            sb.Append($"teleHits={sys.Telemetry.TotalHits} unique={sys.Telemetry.UniqueKeys}");
            WriteDetail(reason, sb.ToString());
            foreach (var (kind, key, count) in sys.Telemetry.TopBlockers(12))
                WriteDetail("blocker", $"{kind}:0x{key:X8} x{count}");
        }
        catch (Exception ex)
        {
            WriteDetail(reason, "snapshot failed: " + ex.Message);
        }
    }

    public void WriteException(string where, Exception ex)
    {
        WriteDetail("EXCEPTION", where + ": " + ex);
        if (ex.InnerException != null)
            WriteDetail("INNER", ex.InnerException.ToString());
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try
            {
                Write($"=== end session lines={_lineCount} ===");
                _writer?.Dispose();
            }
            catch { /* ignore */ }
            _writer = null;
        }
    }
}
