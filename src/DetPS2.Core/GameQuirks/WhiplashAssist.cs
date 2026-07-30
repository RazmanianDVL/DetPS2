using System;

namespace DetPS2.Core;

/// <summary>
/// Whiplash (SLUS_206.84) — UsingCD / IOPRP255 retail boot assist.
///
/// <para>
/// Retail ELF still carries the SN ProView dual path: when the <c>UsingCD</c> config key
/// is unset (not in <c>WHIPLASH/GAME.INI</c>), init stores 0 at the media-mode byte and
/// builds <c>rom0:UDNL host0:~/bin/IOPRP255.IMG</c> plus empty host FILEIO opens. Live
/// (2026-07-30): that path reboots IOP, binds a custom SN RPC <c>0x00534E03</c>, then
/// <c>Exit(0)</c> at ~6.0M with px=0 / cdvd=0. Disc layout is
/// <c>WHIPLASH/BIN/IOPRP255.IMG</c> + IRX; retail path is <c>cdrom0:</c> +
/// <c>/whiplash/bin/</c>.
/// </para>
///
/// <para>
/// Force the CD branch (media-mode byte = 1) so prefix/path helpers pick
/// <c>cdrom0:</c> / <c>/whiplash/bin/</c>, and plant IOPRP version cells <c>"2550"</c>
/// (same UDNL version-handoff class as BO2/B3/GoW). Prefer a real UsingCD config default
/// when the keyword table is HLE'd end-to-end.
/// </para>
/// </summary>
public sealed class WhiplashAssist : IGameQuirkModule
{
    public string Serial => "SLUS_206.84";
    public string DisplayName => "Whiplash (USA)";

    /// <summary>Unfilled IOPRP version placeholders in EE .data ("....").</summary>
    public const uint IopVersionCellA = 0x00421718;
    public const uint IopVersionCellB = 0x00421720;

    /// <summary>
    /// <c>sb s1, 5(s4)</c> site that stores the UsingCD detection result.
    /// Force <c>s1=1</c> in the delay-slot of the preceding branch so the store always writes 1.
    /// </summary>
    public const uint UsingCdStore = 0x00215380;

    /// <summary>Path-prefix select: <c>beq v0, zero, host0:~/</c> → nop (always take cdrom0:).</summary>
    public const uint UsingCdBranchPrefix = 0x00215458;

    /// <summary>Subdir helper: <c>beql v0, zero, "bin/"</c> → nop (always "/whiplash/bin/").</summary>
    public const uint UsingCdBranchSubdir = 0x0021568C;

    /// <summary>IRX load prefix: <c>beq v0, zero, host0:~/</c> → nop.</summary>
    public const uint UsingCdBranchIrx = 0x0021588C;

    /// <summary>Skip disk-type when UsingCD=0: <c>beq v1, zero, skip</c> → nop.</summary>
    public const uint UsingCdBranchDiskType = 0x00215614;

    private bool _patchesApplied;
    private bool _versionPlanted;
    private int _argRewrites;

    public void Reset()
    {
        _patchesApplied = false;
        _versionPlanted = false;
        _argRewrites = 0;
    }

    public void OnDiscMounted(Ps2System sys)
    {
        Reset();
        PlantIopRpVersion(sys);
    }

    public void OnHostPresent(Ps2System sys) => _ = sys;

    /// <summary>
    /// Plant IOPRP 2.5.5 version tag. Real hardware fills these when UDNL applies
    /// IOPRP255.IMG; HLE has no UDNL image apply.
    /// </summary>
    public static void PlantIopRpVersion(Ps2System sys)
    {
        WriteVersionIfPlaceholder(sys, IopVersionCellA);
        WriteVersionIfPlaceholder(sys, IopVersionCellB);
    }

    private static void WriteVersionIfPlaceholder(Ps2System sys, uint addr)
    {
        uint w = sys.Memory.Read32(addr);
        if (w == 0x2E2E2E2Eu || w == 0) // "...." or zero
        {
            sys.Memory.Write8(addr + 0, (byte)'2');
            sys.Memory.Write8(addr + 1, (byte)'5');
            sys.Memory.Write8(addr + 2, (byte)'5');
            sys.Memory.Write8(addr + 3, (byte)'0');
        }
    }

    /// <summary>
    /// EE .text patches that force the retail CD path. Applied after PT_LOAD is resident.
    /// </summary>
    public static void ApplyUsingCdPatches(Ps2System sys)
    {
        // 0x21537C: beq v0, zero, 0x2153BC  (skip sb when refcount zero — keep)
        // 0x215380: sb s1, 5(s4)  — force s1=1 immediately before store by rewriting store to
        // use a constant: replace with  addiu s1, zero, 1 ; then need a second instr for sb.
        // Instead: patch the three branch sites that *read* the byte, plus rewrite the store's
        // source by planting  addiu s1, zero, 1  over the dead delay-slot path.
        //
        // At 0x215374 the fallthrough already does addiu s1, zero, 1 when the "cdrom" keyword
        // probe succeeds. When both probes fail s1 stays 0. Overwrite the store instruction's
        // preceding nop-equivalent by changing the store itself to:
        //   We patch UsingCdStore (sb s1, 5(s4) = 0xA2910005) → keep as store but ensure s1=1:
        //   write  addiu s1, zero, 1  at 0x21537C delay... can't without shifting.
        // Practical: patch all consumer branches to take the CD arm, and rewrite sb to
        //   ori s1, zero, 1 ; which is wrong size.
        // Two-instruction plant at store site via overwriting the beq's delay slot + store:
        //   0x21537C was: beq v0, zero, 0x2153BC
        //   0x215380 was: sb s1, 5(s4)
        // Change to:
        //   0x21537C: addiu s1, zero, 1
        //   0x215380: sb s1, 5(s4)
        // so we always store 1; refcount cleanup still runs either way (harmless extra path).
        sys.Memory.Write32(0x0021537C, 0x24110001u); // addiu s1, zero, 1
        // 0x215380 sb s1, 5(s4) left intact

        // Consumer branches → always CD
        sys.Memory.Write32(UsingCdBranchPrefix, 0x00000000u);   // nop (was beq → host prefix)
        sys.Memory.Write32(UsingCdBranchSubdir, 0x00000000u);   // nop (was beql → "bin/")
        sys.Memory.Write32(UsingCdBranchIrx, 0x00000000u);      // nop (was beq → host IRX)
        sys.Memory.Write32(UsingCdBranchDiskType, 0x00000000u); // nop (was beq → skip disk type)
    }

    public void Step(Ps2System sys)
    {
        ulong c = sys.Scheduler.MasterCycles;

        // ELF PT_LOAD lands at c≈0; wait for code to be resident (store site non-zero).
        if (!_patchesApplied && c >= 1_000)
        {
            uint probe = sys.Memory.Read32(UsingCdStore);
            if (probe != 0)
            {
                ApplyUsingCdPatches(sys);
                PlantIopRpVersion(sys);
                _patchesApplied = true;
                _versionPlanted = true;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" ||
                    Environment.GetEnvironmentVariable("DETPS2_TRACE_WHIP") == "1")
                    Console.Error.WriteLine($"[WHIP] UsingCD patches + IOPRP2550 plant cyc={c}");
            }
        }

        if (_versionPlanted)
            PlantIopRpVersion(sys);

        // If reboot arg still carries host0 (race before patches / external path build), rewrite.
        string arg = sys.Sif.LastIopRebootArg ?? "";
        if (arg.Contains("host0", StringComparison.OrdinalIgnoreCase) &&
            arg.Contains("IOPRP255", StringComparison.OrdinalIgnoreCase) &&
            _argRewrites < 4)
        {
            // Prefer the real disc path under WHIPLASH/BIN.
            const string retail = "rom0:UDNL cdrom0:\\WHIPLASH\\BIN\\IOPRP255.IMG;1";
            // Only the live buffer we saw at 0x46D718 during host path; scan for the string.
            RewriteRebootArgBuffers(sys, retail);
            _argRewrites++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" ||
                Environment.GetEnvironmentVariable("DETPS2_TRACE_WHIP") == "1")
                Console.Error.WriteLine($"[WHIP] reboot arg host→cdrom rewrite #{_argRewrites} cyc={c}");
        }
    }

    private static void RewriteRebootArgBuffers(Ps2System sys, string retail)
    {
        // Known live buffer from host-path run; also scan a small BSS window for host0:~/bin/IOPRP.
        TryRewriteCString(sys, 0x0046D718, retail);
        // Stack copy seen at 0x01FEF700 during host build — only rewrite if still host.
        TryRewriteCString(sys, 0x01FEF700, retail);
    }

    private static void TryRewriteCString(Ps2System sys, uint addr, string replacement)
    {
        var sb = new System.Text.StringBuilder(48);
        for (int i = 0; i < 64; i++)
        {
            byte b = sys.Memory.Read8(addr + (uint)i);
            if (b == 0) break;
            if (b < 0x20 || b >= 0x7F) return;
            sb.Append((char)b);
        }
        string cur = sb.ToString();
        if (!cur.Contains("host0", StringComparison.OrdinalIgnoreCase)) return;
        if (!cur.Contains("IOPRP", StringComparison.OrdinalIgnoreCase)) return;
        for (int i = 0; i < replacement.Length; i++)
            sys.Memory.Write8(addr + (uint)i, (byte)replacement[i]);
        sys.Memory.Write8(addr + (uint)replacement.Length, 0);
    }
}
