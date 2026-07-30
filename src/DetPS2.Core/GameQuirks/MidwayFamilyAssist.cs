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
/// <item>MK: Armageddon (SLUS_215.50) when present — same SN-family gates</item>
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

    // Midway custom heap: block lookup walk at 0x3BA948..0x3BA988 (Deception SLUS_208.81;
    // same allocator family on DA). Walks tree via node+0x24 / +0x28. After incomplete
    // MWo3 overlay free (GAMER.OVL stub → no GAMEFD.ovl body), free@0x3BA270 can leave a
    // right-child cycle (live: C64500→C422E0→C4CD70→C64500) so the walk never exits.
    // Prefer breaking the cycle in RDRAM (repair) over planting a permanent code stub.
    private const uint HeapWalkPcLo = 0x003BA948;
    private const uint HeapWalkPcHi = 0x003BA98C;
    private const uint HeapWalkExit = 0x003BA990;
    private const uint HeapWalkRet0 = 0x003BA900; // v0=0; restore; jr ra

    private uint _walkLastV1;
    private int _walkSameV1Hits;
    private int _walkBandHits;
    private int _cycleBreaks;
    private int _walkForcedExits;

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
    }

    public void OnDiscMounted(Ps2System sys) => ApplyVersionPolicy(sys);

    public void OnHostPresent(Ps2System sys) => _ = sys;

    public void Step(Ps2System sys)
    {
        // Re-assert after IOP reboot / RealRpc internal resets that clear open pad state
        // but leave flags; cheap idempotent set in case a future path recreates RealRpc.
        ApplyVersionPolicy(sys);
        TryBreakHeapTreeCycle(sys);
    }

    private static void ApplyVersionPolicy(Ps2System sys)
    {
        var rpc = sys.Hle?.Sony?.RealRpc;
        if (rpc == null) return;
        rpc.PadModVerMajor4 = true;
        rpc.PreferIopRpGetVersion = true;
    }

    /// <summary>
    /// Detect Midway heap range-tree walk stuck on a +0x28 cycle (post-OVL free corruption)
    /// and repair by nulling one right-child link, or force a null lookup return.
    /// Shared across DA/Deception/Armageddon — same allocator PCs on the Midway EE heap.
    /// </summary>
    private void TryBreakHeapTreeCycle(Ps2System sys)
    {
        uint pc = (uint)sys.EE.PC;
        if (pc < HeapWalkPcLo || pc > HeapWalkPcHi)
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

        // Fallback: force null return from lookup (epilogue at 0x3BA900 sets v0=0).
        // Used when the cycle walk cannot be resolved (garbage pointers).
        if (_walkBandHits >= 128 || _cycleBreaks >= 4)
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // v0
            sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 0 }); // s0
            sys.EE.PC = HeapWalkRet0;
            _walkForcedExits++;
            _walkBandHits = 0;
            _walkSameV1Hits = 0;
            if (trace && _walkForcedExits <= 16)
                Console.Error.WriteLine(
                    $"[MKFAM] heap-walk force-ret0 pc=0x{pc:X8} v1=0x{v1:X8} n={_walkForcedExits} cyc={cyc}");
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
