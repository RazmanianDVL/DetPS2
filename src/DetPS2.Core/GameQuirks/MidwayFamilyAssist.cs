using System;

namespace DetPS2.Core;

/// <summary>
/// Minimal Midway MK-family boot assist for titles that need SN/PADMAN version gates
/// without Shaolin Monks' CRI/WAD plant machinery.
///
/// Targets:
/// <list type="bullet">
/// <item>MK: Deadly Alliance (SLUS_204.23) — PADMAN GetModVer major 4 + IOPRP ASCII GetVersion</item>
/// <item>MK: Deception (SLUS_208.81) — IOPRP ASCII GetVersion (and XPADMAN-class pad gate)</item>
/// <item>MK: Armageddon (SLUS_215.50 standard / SLUS_215.43 Premium Edition) — same SN-family gates</item>
/// </list>
///
/// Does <b>not</b> run <see cref="MidwayBootAssist"/> SM plants (no CRI, no logo spine,
/// no ADX thrash escapes). Flips <see cref="RealSifRpc"/> version policy flags and applies
/// a shared Midway heap-tree cycle break (see <see cref="TryBreakHeapTreeCycle"/>).
/// </summary>
public sealed class MidwayFamilyAssist : IGameQuirkModule
{
    private readonly string _serial;
    private readonly string _displayName;

    // Midway custom heap: block lookup walk via node+0x24 / +0x28. After incomplete
    // MWo3 overlay free (GAMER.OVL stub → no GAMEFD.ovl body), free can leave a
    // right-child cycle so the walk never exits.
    // Prefer breaking the cycle in RDRAM (repair) over planting a permanent code stub.
    // PC bands differ by title build; +0x24/+0x28 layout is SHARED across DA/Dec/Arm.
    // Dec SLUS_208.81 / DA family: 0x3BA948..0x3BA98C, ret0 @ 0x3BA900
    // Arm Premium SLUS_215.43 (live 2026-07-30): 0x42940C..0x42944C, ret0 @ 0x429450
    private static readonly (uint Lo, uint Hi, uint Ret0)[] HeapWalkBands =
    {
        (0x003BA948, 0x003BA98C, 0x003BA900), // DA / Deception
        (0x0042940C, 0x0042944C, 0x00429450), // Armageddon PE (SLUS_215.43)
    };

    // DA (SLUS_204.23) wait-for-ready: while (*s0 != 4) { spin; Delay(50); poll MSL }.
    // Live: after MSL fno=0xDADA, boot opens gameart.ssf (MKDA.PAK artps2 member) and
    // parks here. When archive stream/host was never mounted, s0 stays 0 and the wait
    // is unbounded (primary DA wall @0x2F5580). Shared shape with Dec asset waits.
    private const uint WaitReadyPcLo = 0x002F5564;
    private const uint WaitReadyPcHi = 0x002F55AC;
    private const uint WaitReadyEpilogue = 0x002F55B0; // restore s0/ra; jr ra
    // MSL EE response ring (DA live @0x587E60): +0 capacity, +4 count. count==0 ⇒ poll
    // short-circuits and async file completions never land.
    private const uint MslRingDa = 0x00587E60;
    // Scratch status word used when wait is entered with s0==null (no job object).
    private const uint WaitReadyScratch = 0x0007FF00;

    private uint _walkLastV1;
    private int _walkSameV1Hits;
    private int _walkBandHits;
    private int _cycleBreaks;
    private int _walkForcedExits;
    private int _waitReadyHits;
    private int _waitReadyEscapes;
    private int _mslRingSeeds;

    public MidwayFamilyAssist(string serial, string displayName)
    {
        _serial = serial ?? throw new ArgumentNullException(nameof(serial));
        _displayName = displayName ?? serial;
    }

    public string Serial => _serial;
    public string DisplayName => _displayName;

    public void Reset()
    {
        _walkLastV1 = 0;
        _walkSameV1Hits = 0;
        _walkBandHits = 0;
        _cycleBreaks = 0;
        _walkForcedExits = 0;
        _waitReadyHits = 0;
        _waitReadyEscapes = 0;
        _mslRingSeeds = 0;
    }

    public void OnDiscMounted(Ps2System sys) => ApplyVersionPolicy(sys);

    public void OnHostPresent(Ps2System sys) => _ = sys;

    public void Step(Ps2System sys)
    {
        // Re-assert after IOP reboot / RealRpc internal resets that clear open pad state
        // but leave flags; cheap idempotent set in case a future path recreates RealRpc.
        ApplyVersionPolicy(sys);
        TrySeedMslRing(sys);
        // Wait-ready force (*s0=4 / null-s0 plant) false-completes gameart.ssf and the
        // title Exit(0)s. Leave the honest hang at 0x2F55xx until MSL stream host + SEC
        // open actually deliver status==4. (TryEscapeWaitReady kept for future opt-in.)
        // TryEscapeWaitReady(sys);
        TryBreakHeapTreeCycle(sys);
    }

    private static void ApplyVersionPolicy(Ps2System sys)
    {
        var rpc = sys.Hle?.Sony?.RealRpc;
        if (rpc == null) return;
        rpc.PadModVerMajor4 = true;
        rpc.PreferIopRpGetVersion = true;
        // IOPRP300 digits would otherwise arm Play! FILEIO-2200 (SotC path). Midway EE is SN
        // ProDG FILEIO — keep classic open/read/lseek reply shapes so GAMER.OVL full-read and
        // later MKDA.PAK member opens complete.
        rpc.PreferSnFileIo = true;
    }

    /// <summary>
    /// If the DA MSL response ring looks initialized (capacity 0x28) but count is still 0
    /// after MSL bind/init, seed count=1 so EE poll helpers do not hard-skip. Does not
    /// invent full async payloads — only unblocks the empty-ring short-circuit.
    /// Safe: only when cap==0x28, count==0, and ring base is a valid RDRAM pointer.
    /// </summary>
    private void TrySeedMslRing(Ps2System sys)
    {
        if (_mslRingSeeds != 0) return;
        uint cap = sys.Memory.Read32(MslRingDa);
        uint count = sys.Memory.Read32(MslRingDa + 4);
        if (cap != 0x28 || count != 0) return;
        // Only seed once PAD/MSL boot has progressed (ring buffer base non-null).
        uint basePtr = sys.Memory.Read32(MslRingDa + 8);
        if (basePtr < 0x00100000 || basePtr >= 0x02000000) return;
        sys.Memory.Write32(MslRingDa + 4, 1);
        _mslRingSeeds = 1;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine($"[MKFAM] MSL ring seed count=1 base=0x{basePtr:X8} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// Escape DA wait-for-ready at 0x2F5564..0x2F55AC when status is sticky non-4.
    /// Live: gameart.ssf SEC load never reaches status==4 without a mounted archive stream
    /// (MSL host). Prefer writing *s0=4 when s0 is a valid RDRAM status object.
    /// When s0 is null (job never created — 0x5320E4 stays 0), plant a scratch status=4,
    /// point s0 at it, and also publish it at the DA status slot 0x5320E4 so the natural
    /// loop exit is taken (do <b>not</b> jump the epilogue — that trips Exit(0)).
    ///
    /// NOT called from <see cref="Step"/> — force-ready false-completes SEC and Exit(0)s.
    /// Kept for future opt-in once stream host is ground-truthed.
    /// </summary>
    private void TryEscapeWaitReady(Ps2System sys)
    {
        uint pc = (uint)sys.EE.PC;
        if (pc < WaitReadyPcLo || pc > WaitReadyPcHi)
        {
            _waitReadyHits = 0;
            return;
        }

        _waitReadyHits++;
        if (_waitReadyHits < 64) return;

        uint s0 = (uint)sys.EE.GetGpr(16).Lo;
        bool trace = Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1";
        ulong cyc = sys.MasterCycles;

        if (s0 >= 0x00100000 && s0 < 0x02000000)
        {
            uint st = sys.Memory.Read32(s0);
            if (st != 4)
            {
                sys.Memory.Write32(s0, 4);
                _waitReadyEscapes++;
                _waitReadyHits = 0;
                if (trace && _waitReadyEscapes <= 16)
                    Console.Error.WriteLine(
                        $"[MKFAM] wait-ready force *0x{s0:X8}=4 pc=0x{pc:X8} n={_waitReadyEscapes} cyc={cyc}");
            }
            return;
        }

        // s0 null/garbage: publish scratch status and retarget s0; let the *s0==4 check pass.
        if (_waitReadyHits < 96) return;
        sys.Memory.Write32(WaitReadyScratch, 4);
        // DA global slot loaded at 0x19BFE8 (lw s0, 0x5320E4).
        sys.Memory.Write32(0x005320E4, WaitReadyScratch);
        sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = WaitReadyScratch });
        _waitReadyEscapes++;
        _waitReadyHits = 0;
        if (trace && _waitReadyEscapes <= 16)
            Console.Error.WriteLine(
                $"[MKFAM] wait-ready null-s0 plant scratch=0x{WaitReadyScratch:X8}=4 " +
                $"slot=0x5320E4 n={_waitReadyEscapes} cyc={cyc}");
    }

    /// <summary>
    /// Detect Midway heap range-tree walk stuck on a +0x28 cycle (post-OVL free corruption)
    /// and repair by nulling one right-child link, or force a null lookup return.
    /// Shared across DA/Deception/Armageddon — same +0x24/+0x28 tree layout; PC bands vary.
    /// </summary>
    private void TryBreakHeapTreeCycle(Ps2System sys)
    {
        uint pc = (uint)sys.EE.PC;
        uint ret0 = 0;
        bool inBand = false;
        foreach (var (lo, hi, bandRet0) in HeapWalkBands)
        {
            if (pc < lo || pc > hi) continue;
            inBand = true;
            ret0 = bandRet0;
            break;
        }
        if (!inBand)
        {
            _walkBandHits = 0;
            _walkSameV1Hits = 0;
            return;
        }

        _walkBandHits++;
        uint v1 = (uint)sys.EE.GetGpr(3).Lo; // current node
        if (v1 != 0 && v1 == _walkLastV1)
            _walkSameV1Hits++;
        else
        {
            _walkLastV1 = v1;
            _walkSameV1Hits = 0;
        }

        // Fast path: same node re-entered many times inside the band ⇒ likely cycle.
        // Also fire if we have been in-band for a long time with varying nodes (full cycle).
        bool stickyNode = _walkSameV1Hits >= 8;
        bool longBand = _walkBandHits >= 64;
        if (!stickyNode && !longBand)
            return;

        bool trace = Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1";
        ulong cyc = sys.MasterCycles;

        // Attempt structural repair: from current v1, walk +0x28 up to 16 hops; if a node
        // repeats, null the link that closed the cycle.
        if (v1 >= 0x00100000 && v1 < 0x02000000)
        {
            if (BreakRightChildCycle(sys, v1, out uint cutAt, out uint cutTo))
            {
                _cycleBreaks++;
                _walkBandHits = 0;
                _walkSameV1Hits = 0;
                if (trace && _cycleBreaks <= 16)
                    Console.Error.WriteLine(
                        $"[MKFAM] heap-tree cycle break node=0x{cutAt:X8} +0x28 was 0x{cutTo:X8} " +
                        $"pc=0x{pc:X8} n={_cycleBreaks} cyc={cyc}");
                return;
            }
        }

        // Fallback: force null return from lookup (band ret0 epilogue / exit sets v0=0).
        // Used when the cycle walk cannot be resolved (garbage pointers).
        if (_walkBandHits >= 128 || _cycleBreaks >= 4)
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // v0
            sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 0 }); // s0
            sys.EE.PC = ret0;
            _walkForcedExits++;
            _walkBandHits = 0;
            _walkSameV1Hits = 0;
            if (trace && _walkForcedExits <= 16)
                Console.Error.WriteLine(
                    $"[MKFAM] heap-walk force-ret0 pc=0x{pc:X8} v1=0x{v1:X8} ret0=0x{ret0:X8} " +
                    $"n={_walkForcedExits} cyc={cyc}");
        }
    }

    /// <summary>
    /// Walk node+0x28 chain from <paramref name="start"/>; if a cycle is found, null the
    /// right-child pointer that would re-enter a seen node. Returns true if a link was cut.
    /// </summary>
    private static bool BreakRightChildCycle(Ps2System sys, uint start, out uint cutAt, out uint cutTo)
    {
        cutAt = 0;
        cutTo = 0;
        // Tortoise/hare + explicit set for the cut site.
        Span<uint> seen = stackalloc uint[24];
        int n = 0;
        uint cur = start;
        for (int hop = 0; hop < 24; hop++)
        {
            if (cur < 0x00100000 || cur >= 0x02000000)
                return false;
            for (int i = 0; i < n; i++)
            {
                if (seen[i] != cur) continue;
                // Cycle: predecessor is seen[n-1] (or start if n==0 — use cur itself).
                uint pred = n > 0 ? seen[n - 1] : cur;
                uint next = sys.Memory.Read32(pred + 0x28);
                // Prefer nulling pred→cur if that is the back-edge; else null cur's right.
                if (next == cur || next == start)
                {
                    cutAt = pred;
                    cutTo = next;
                    sys.Memory.Write32(pred + 0x28, 0);
                    return true;
                }
                cutAt = cur;
                cutTo = sys.Memory.Read32(cur + 0x28);
                sys.Memory.Write32(cur + 0x28, 0);
                return true;
            }
            if (n < seen.Length)
                seen[n++] = cur;
            uint r = sys.Memory.Read32(cur + 0x28);
            if (r == 0)
            {
                // Try left child chain as well (walker uses +0x24 when range matches).
                uint l = sys.Memory.Read32(cur + 0x24);
                if (l == 0) return false;
                // Detect left-cycle similarly on a short walk.
                return BreakLeftChildCycle(sys, cur, out cutAt, out cutTo);
            }
            // Direct back-edge to start or any previous.
            for (int i = 0; i < n; i++)
            {
                if (r != seen[i] && r != start) continue;
                cutAt = cur;
                cutTo = r;
                sys.Memory.Write32(cur + 0x28, 0);
                return true;
            }
            cur = r;
        }
        // Long chain without null — cut current right as last resort.
        if (cur >= 0x00100000 && cur < 0x02000000)
        {
            uint r = sys.Memory.Read32(cur + 0x28);
            if (r != 0)
            {
                cutAt = cur;
                cutTo = r;
                sys.Memory.Write32(cur + 0x28, 0);
                return true;
            }
        }
        return false;
    }

    private static bool BreakLeftChildCycle(Ps2System sys, uint start, out uint cutAt, out uint cutTo)
    {
        cutAt = 0;
        cutTo = 0;
        uint cur = start;
        Span<uint> seen = stackalloc uint[16];
        int n = 0;
        for (int hop = 0; hop < 16; hop++)
        {
            if (cur < 0x00100000 || cur >= 0x02000000) return false;
            for (int i = 0; i < n; i++)
            {
                if (seen[i] != cur) continue;
                uint pred = n > 0 ? seen[n - 1] : cur;
                cutAt = pred;
                cutTo = sys.Memory.Read32(pred + 0x24);
                sys.Memory.Write32(pred + 0x24, 0);
                return true;
            }
            if (n < seen.Length) seen[n++] = cur;
            uint l = sys.Memory.Read32(cur + 0x24);
            if (l == 0) return false;
            for (int i = 0; i < n; i++)
            {
                if (l != seen[i]) continue;
                cutAt = cur;
                cutTo = l;
                sys.Memory.Write32(cur + 0x24, 0);
                return true;
            }
            cur = l;
        }
        return false;
    }
}
