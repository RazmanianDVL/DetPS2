using System;

namespace DetPS2.Core;

/// <summary>
/// Vexx (USA) SLUS_203.83 — IOPRP252 version plant + null-path basename unstick.
///
/// <para>
/// <b>Primary blocker (2026-07-30):</b> after IOP reboot
/// <c>rom0:UDNL cdrom0:\SYSTEM\IOPRP252.IMG;1</c> and LOADFILE GetVersion (fno=0xFF),
/// the EE LOADFILE client memcmp's a version cell that still holds the unfilled
/// ASCII placeholder <c>"...."</c> at <c>0x003D18B8</c> / <c>0x003D1938</c>. Real
/// hardware fills those when UDNL applies IOPRP252.IMG; HLE has no UDNL image apply.
/// Compare target is rodata <c>"2520"</c> at <c>0x003A9FCC</c> / <c>0x003ABBEC</c>.
/// Gate fails → client returns <c>0xFFFEFFFC</c> (live in a1/a2 at path-scan entry) →
/// no <c>LF_F_MOD_LOAD</c> RPC → IRX list never loads.
/// Same class as <see cref="BloodOmen2SnAssist"/> <c>"2340"</c>,
/// <see cref="Burnout3Assist"/> <c>"2800"</c>, <see cref="GodOfWarAssist"/> <c>"3000"</c>.
/// </para>
///
/// <para>
/// <b>Secondary hang:</b> basename/find-last-slash at <c>0x00146170</c> (and twin
/// <c>0x00146230</c>) is called with a0=NULL when the failed gate leaves struct+0x2C
/// path empty. With s0=0 and strlen=0 the reverse scan does <c>a1--</c> forever because
/// <c>sltu(a1,0)</c> is never true — burns tens of millions of cycles (PcProfiler).
/// A small RDRAM stub null-checks a0 and returns v0=0 so callers take their existing
/// <c>beq v0,zero</c> alt path.
/// </para>
/// </summary>
public sealed class VexxAssist : IGameQuirkModule
{
    public string Serial => "SLUS_203.83";
    public string DisplayName => "Vexx (USA)";

    /// <summary>Unfilled IOPRP version placeholders in EE .data ("....").</summary>
    public const uint IopVersionCellA = 0x003D18B8;
    public const uint IopVersionCellB = 0x003D1938;

    /// <summary>find-last-slash / basename helper entry points.</summary>
    public const uint PathBasenameA = 0x00146170;
    public const uint PathBasenameB = 0x00146230;

    /// <summary>Low RDRAM stubs (below typical ELF PT_LOAD @ 0x100000).</summary>
    public const uint StubA = 0x00090000;
    public const uint StubB = 0x00090040;

    private bool _pathPatched;
    private int _versionReplants;
    private int _nullPathEscapes;

    public void Reset()
    {
        _pathPatched = false;
        _versionReplants = 0;
        _nullPathEscapes = 0;
    }

    public void OnDiscMounted(Ps2System sys)
    {
        Reset();
        PlantIopRpVersion(sys);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine("[VEXX] OnDiscMounted: IOPRP252 version plant ready");
    }

    public void OnHostPresent(Ps2System sys) => _ = sys;

    public void Step(Ps2System sys)
    {
        // Re-plant after SifIopReset / game zero of version cells.
        if (!VersionCellsOk(sys))
        {
            PlantIopRpVersion(sys);
            _versionReplants++;
        }

        // ELF PT_LOAD can overwrite .text after OnDiscMounted — re-apply path stubs once
        // the basename entry is back to a real addiu sp (not our j Stub).
        if (!_pathPatched || !PathStubActive(sys, PathBasenameA))
        {
            PatchNullPathBasename(sys);
            _pathPatched = true;
        }

        // Defense: if still inside reverse-scan body with s0==0, snap return v0=0.
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFu);
        if ((pc is >= 0x0014619C and <= 0x001461BC) || (pc is >= 0x0014625C and <= 0x0014627C))
        {
            if (sys.EE.GetGpr(16).Lo == 0)
            {
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
                sys.EE.PC = sys.EE.GetGpr(31).Lo;
                _nullPathEscapes++;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
                    Console.Error.WriteLine(
                        $"[VEXX] null-path scan escape #{_nullPathEscapes} cyc={sys.Scheduler.MasterCycles}");
            }
        }
    }

    /// <summary>Plant IOPRP 2.5.2 version tag the LOADFILE client compares after GetVersion.</summary>
    public static void PlantIopRpVersion(Ps2System sys)
    {
        WriteCString4(sys, IopVersionCellA, "2520");
        WriteCString4(sys, IopVersionCellB, "2520");
    }

    private static bool VersionCellsOk(Ps2System sys) =>
        ReadCString4(sys, IopVersionCellA) == "2520"
        || ReadCString4(sys, IopVersionCellB) == "2520";

    private static bool PathStubActive(Ps2System sys, uint entry)
    {
        uint op = sys.Memory.Read32(entry);
        // j encoding: top 6 bits = 000010
        return (op >> 26) == 2;
    }

    /// <summary>
    /// Hijack basename entries with a low-RDRAM stub:
    ///   if (a0 == 0) return 0;
    ///   else run original prologue words then continue at entry+8.
    /// </summary>
    public static void PatchNullPathBasename(Ps2System sys)
    {
        PlantOne(sys, PathBasenameA, StubA);
        PlantOne(sys, PathBasenameB, StubB);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine("[VEXX] null-path basename stubs at 0x90000/0x90040");
    }

    private static void PlantOne(Ps2System sys, uint entry, uint stub)
    {
        uint w0 = sys.Memory.Read32(entry);
        uint w1 = sys.Memory.Read32(entry + 4);
        // Already our jump?
        if ((w0 >> 26) == 2)
            return;

        uint cont = (entry + 8) >> 2;
        // stub:
        //   beq a0, zero, null     ; +5 words → stub+0x18
        //   nop
        //   <original w0>
        //   <original w1>
        //   j entry+8
        //   nop
        // null:
        //   jr ra
        //   move v0, zero
        sys.Memory.Write32(stub + 0x00, 0x10800005u); // beq a0, zero, +5
        sys.Memory.Write32(stub + 0x04, 0x00000000u); // nop
        sys.Memory.Write32(stub + 0x08, w0);
        sys.Memory.Write32(stub + 0x0C, w1);
        sys.Memory.Write32(stub + 0x10, 0x08000000u | (cont & 0x03FFFFFF)); // j entry+8
        sys.Memory.Write32(stub + 0x14, 0x00000000u); // nop
        sys.Memory.Write32(stub + 0x18, 0x03E00008u); // jr ra
        sys.Memory.Write32(stub + 0x1C, 0x0000102Du); // daddu v0, zero, zero

        uint stubJ = stub >> 2;
        sys.Memory.Write32(entry + 0x00, 0x08000000u | (stubJ & 0x03FFFFFF)); // j stub
        sys.Memory.Write32(entry + 0x04, 0x00000000u); // nop
    }

    private static string ReadCString4(Ps2System sys, uint addr)
    {
        var chars = new char[4];
        for (int i = 0; i < 4; i++)
        {
            byte b = sys.Memory.Read8(addr + (uint)i);
            if (b == 0) return new string(chars, 0, i);
            chars[i] = (char)b;
        }
        return new string(chars);
    }

    private static void WriteCString4(Ps2System sys, uint addr, string s)
    {
        for (int i = 0; i < 4; i++)
            sys.Memory.Write8(addr + (uint)i, i < s.Length ? (byte)s[i] : (byte)0);
    }
}
