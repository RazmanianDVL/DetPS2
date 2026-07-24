using System;
using System.IO;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Local crash / diagnostic log (Phase 37). Never uploads; no game code dumps by default.
/// </summary>
public static class CrashLog
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DetPS2", "crash.log");

    public static void Write(string message, Exception? ex = null, Ps2System? system = null)
    {
        try
        {
            string? dir = Path.GetDirectoryName(DefaultPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine($"==== {DateTime.UtcNow:O} UTC ====");
            sb.AppendLine(message);
            if (system != null)
            {
                sb.AppendLine($"MasterCycles={system.MasterCycles} EE.PC=0x{system.EE.PC:X8}");
                sb.AppendLine($"HLE.Exit={system.Hle.ExitRequested} TelemetryHits={system.Telemetry.TotalHits}");
            }
            if (ex != null)
            {
                sb.AppendLine(ex.GetType().FullName);
                sb.AppendLine(ex.Message);
                sb.AppendLine(ex.StackTrace);
            }
            sb.AppendLine();
            File.AppendAllText(DefaultPath, sb.ToString());
        }
        catch
        {
            // never throw from logger
        }
    }
}
