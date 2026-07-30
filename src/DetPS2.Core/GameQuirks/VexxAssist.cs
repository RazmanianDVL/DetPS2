using System;

namespace DetPS2.Core;

/// <summary>
/// Vexx (USA) SLUS_203.83 — IOPRP252 version plant + null-path basename unstick +
/// CRT malloc jump-table plant + path-normalize thrash escape.
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
///
/// <para>
/// <b>Tertiary wall (2026-07-30 secondary fleet):</b> post-pad / SearchFile bind, the
/// path-normalize helper at <c>0x00372A80</c> calls string alloc which falls back through
/// CRT trampolines <c>0x001CEBA0</c> → jump table <c>0x003BCD00</c>. Live dump: that table
/// is still all-zero (heap never wired), so <c>jalr v0</c> hits the low-page JRGUARD and
/// falls through; string alloc returns a near-null body pointer (live <c>sp+0x38=0xF</c>).
/// Path munge then loops with length word at <c>-8(0xF)</c> ≈ <c>0x40000433</c> forever
/// (PC stuck <c>0x00372ADx</c>, no FILEIO). Plant a bump allocator into the CRT table and
/// escape the munge loop when the path base is still low/garbage.
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

    /// <summary>CRT malloc/free/realloc jump table (live all-zero after pad stack).</summary>
    public const uint CrtMallocSlot = 0x003BCD00;
    public const uint CrtFreeSlot = 0x003BCD04;
    public const uint CrtReallocSlot = 0x003BCD08;

    /// <summary>Bump-allocator stubs + cursor (low RDRAM, below ELF).</summary>
    public const uint MallocStub = 0x00090100;
    public const uint FreeStub = 0x00090140;
    public const uint ReallocStub = 0x00090160;
    public const uint BumpCursorCell = 0x00090180;
    public const uint BumpArenaBase = 0x01800000;
    public const uint BumpArenaEnd = 0x01C00000;

    /// <summary>Path-normalize helper that thrash-loops on garbage path base.</summary>
    public const uint PathNormalizeEntry = 0x00372A80;
    public const uint PathNormalizeLoop = 0x00372ABC;
    public const uint PathNormalizeAfterLoop = 0x00372B04;
    public const uint EmptyStringSentinel = 0x003C4C58; // *0x003C4C60 points here; "" 

    private bool _pathPatched;
    private bool _mallocPlanted;
    private int _versionReplants;
    private int _nullPathEscapes;
    private int _pathNormEscapes;
    private int _mallocReplants;

    public void Reset()
    {
        _pathPatched = false;
        _mallocPlanted = false;
        _versionReplants = 0;
        _nullPathEscapes = 0;
        _pathNormEscapes = 0;
        _mallocReplants = 0;
    }

    public void OnDiscMounted(Ps2System sys)
    {
        Reset();
        if (sys.Hle?.Sony?.RealRpc != null)
            sys.Hle.Sony.RealRpc.PreferIopRpGetVersion = true;
        PlantIopRpVersion(sys);
        PlantCrtMallocTable(sys);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine("[VEXX] OnDiscMounted: IOPRP252 + CRT malloc plant ready");
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

        // CRT table can be zeroed by BSS wipe / late init — re-plant when empty.
        if (!_mallocPlanted || sys.Memory.Read32(CrtMallocSlot) == 0)
        {
            PlantCrtMallocTable(sys);
            _mallocPlanted = true;
            _mallocReplants++;
        }

        // ELF PT_LOAD can overwrite .text after OnDiscMounted — re-apply path stubs once
        // the basename entry is back to a real addiu sp (not our j Stub).
        if (!_pathPatched || !PathStubActive(sys, PathBasenameA))
        {
            PatchNullPathBasename(sys);
            _pathPatched = true;
        }

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFu);

        // Defense: if still inside reverse-scan body with s0==0, snap return v0=0.
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

        // Path-normalize thrash: sp+0x38 holds path-body pointer; live garbage 0xF →
        // length at -8(path) is open-bus huge → infinite '/'→'\' scan.
        if (pc is >= PathNormalizeLoop and <= PathNormalizeAfterLoop)
        {
            uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFu);
            if (sp >= 0x1000 && sp + 0x40 < SystemMemory.RDRAM_SIZE)
            {
                uint pathPtr = sys.Memory.Read32(sp + 0x38);
                if (pathPtr < 0x10000u)
                {
                    sys.Memory.Write32(sp + 0x38, EmptyStringSentinel);
                    // Empty sentinel length word at -8 is 0 (rodata zeros) → loop exits.
                    sys.EE.SetGpr(7, new EmotionEngine.Gpr128 { Lo = EmptyStringSentinel }); // a3
                    sys.EE.SetGpr(6, new EmotionEngine.Gpr128 { Lo = 0 }); // a2 index
                    sys.EE.PC = PathNormalizeAfterLoop;
                    _pathNormEscapes++;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
                        Console.Error.WriteLine(
                            $"[VEXX] path-normalize escape #{_pathNormEscapes} wasPtr=0x{pathPtr:X} cyc={sys.Scheduler.MasterCycles}");
                }
            }
        }
    }

    /// <summary>Plant IOPRP 2.5.2 version tag the LOADFILE client compares after GetVersion.</summary>
    public static void PlantIopRpVersion(Ps2System sys)
    {
        WriteCString4(sys, IopVersionCellA, "2520");
        WriteCString4(sys, IopVersionCellB, "2520");
    }

    /// <summary>
    /// Install a simple bump malloc/free/realloc into the CRT jump table at 0x003BCD00.
    /// Real hardware fills this during C runtime heap init; under HLE the table stays zero
    /// so string alloc (path normalize → first FILEIO) returns garbage near-null pointers.
    /// </summary>
    public static void PlantCrtMallocTable(Ps2System sys)
    {
        // malloc(a0=size):
        //   t0 = &cursor
        //   v0 = *cursor
        //   t1 = (size + 15) & ~15
        //   t2 = v0 + t1
        //   if (t2 >= end) return 0
        //   *cursor = t2; return v0
        uint cur = BumpCursorCell;
        uint stub = MallocStub;
        uint end = BumpArenaEnd;

        // Initialize cursor once (do not rewind a live arena).
        uint existing = sys.Memory.Read32(cur);
        if (existing < BumpArenaBase || existing >= BumpArenaEnd)
            sys.Memory.Write32(cur, BumpArenaBase);

        uint[] mallocOps =
        {
            0x3C080000u | (cur >> 16),            // 00 lui t0, hi(cur)
            0x35080000u | (cur & 0xFFFF),         // 04 ori t0, t0, lo(cur)
            0x8D020000u,                          // 08 lw  v0, 0(t0)
            0x2489000Fu,                          // 0C addiu t1, a0, 15
            0x00094902u,                          // 10 srl t1, t1, 4
            0x00094900u,                          // 14 sll t1, t1, 4   ; align16
            0x00495021u,                          // 18 addu t2, v0, t1
            0x3C0B0000u | (end >> 16),            // 1C lui t3, hi(end)
            0x356B0000u | (end & 0xFFFF),         // 20 ori t3, t3, lo(end)
            0x014B602Bu,                          // 24 sltu t4, t2, t3
            0x11800003u,                          // 28 beq t4, zero, +3 → fail @0x38
            0x00000000u,                          // 2C nop
            0xAD0A0000u,                          // 30 sw t2, 0(t0)
            0x03E00008u,                          // 34 jr ra
            0x00000000u,                          // 38 nop (delay of jr) — ALSO fail target
            0x03E00008u,                          // 3C jr ra (fail)
            0x0000102Du,                          // 40 daddu v0, zero, zero
        };
        // Fix fail branch: beq at 0x28 with delay 0x2C; taken target = PC+4+4*imm = 0x2C+4*3 = 0x38
        // At 0x38 we need jr ra / move v0,0 — but 0x38 is currently the success jr's delay nop.
        // Re-layout success path to jr at 0x30 with delay nop at 0x34, fail at 0x38/0x3C.
        mallocOps = new uint[]
        {
            0x3C080000u | (cur >> 16),            // 00 lui t0, hi(cur)
            0x35080000u | (cur & 0xFFFF),         // 04 ori t0, t0, lo(cur)
            0x8D020000u,                          // 08 lw  v0, 0(t0)
            0x2489000Fu,                          // 0C addiu t1, a0, 15
            0x00094902u,                          // 10 srl t1, t1, 4
            0x00094900u,                          // 14 sll t1, t1, 4
            0x00495021u,                          // 18 addu t2, v0, t1
            0x3C0B0000u | (end >> 16),            // 1C lui t3, hi(end)
            0x356B0000u | (end & 0xFFFF),         // 20 ori t3, t3, lo(end)
            0x014B602Bu,                          // 24 sltu t4, t2, t3
            0x11800004u,                          // 28 beq t4, zero, +4 → 0x3C fail
            0x00000000u,                          // 2C nop
            0xAD0A0000u,                          // 30 sw t2, 0(t0)
            0x03E00008u,                          // 34 jr ra
            0x00000000u,                          // 38 nop
            0x03E00008u,                          // 3C jr ra (fail)
            0x0000102Du,                          // 40 daddu v0, zero, zero
        };
        for (int i = 0; i < mallocOps.Length; i++)
            sys.Memory.Write32(stub + (uint)(i * 4), mallocOps[i]);

        // free: return immediately
        sys.Memory.Write32(FreeStub + 0, 0x03E00008u); // jr ra
        sys.Memory.Write32(FreeStub + 4, 0x00000000u); // nop

        // realloc(old, size): ignore old, malloc(size) with a1→a0
        sys.Memory.Write32(ReallocStub + 0, 0x00A0202Du); // daddu a0, a1, zero
        sys.Memory.Write32(ReallocStub + 4, 0x08000000u | ((MallocStub >> 2) & 0x03FFFFFF));
        sys.Memory.Write32(ReallocStub + 8, 0x00000000u);

        sys.Memory.Write32(CrtMallocSlot, MallocStub);
        sys.Memory.Write32(CrtFreeSlot, FreeStub);
        sys.Memory.Write32(CrtReallocSlot, ReallocStub);

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine(
                $"[VEXX] CRT malloc table → bump 0x{BumpArenaBase:X}-0x{BumpArenaEnd:X} stub=0x{MallocStub:X}");
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
