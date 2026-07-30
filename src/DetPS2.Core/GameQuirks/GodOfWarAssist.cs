using System;

namespace DetPS2.Core;

/// <summary>
/// God of War (SCUS_973.99) — IOPRP version + FreezeCache unlock + BST walk guard.
///
/// After 989snd RPC returns correctly, boot calls a freeze-region constructor
/// (<c>0x00283570</c>) whose failure path stores <c>0xFFFEFFFC</c> at
/// <c>0x0029C4DC</c>. Later <c>0x00185F28</c> does <c>bltz *0x29C4DC → infinite spin</c>
/// (the "FreezeCache" hard-lock). Root cause ground-truthed 2026-07-30:
/// <list type="number">
/// <item><c>0x00298A10</c> memcmp's the LOADFILE GetVersion cell against ASCII
///   <c>"3000"</c> (IOPRP300) and against a version pointer that still holds the
///   unfilled <c>"...."</c> placeholder at <c>0x002C6D30</c>.</item>
/// <item>Real hardware fills that cell when UDNL applies <c>IOPRP300.IMG</c>;
///   HLE has no UDNL image apply, so the cell stays <c>"...."</c> → version check
///   fails → <c>0xFFFEFFFC</c> → FreezeCache spin forever (PC <c>0x00185F9x</c>).</item>
/// </list>
/// Same class of gap as <see cref="BloodOmen2SnAssist"/>'s IOPRP <c>"2340"</c> plant.
/// Prefer real UDNL version handoff when available; until then plant <c>"3000"</c>
/// over the <c>"...."</c> placeholder and clear a stuck freeze flag after sound init.
///
/// <para>
/// Post-FreezeCache (2026-07-30 list-walk investigation): a hashed-string dict BST at
/// <c>0x001769F8</c> (used e.g. for <c>HERO_HEAP_SIZE</c>) walks index-linked 16-byte
/// nodes. Pool base lives at <c>*(0x0029C4BC)</c>; sentinel "nil" head at
/// <c>*(0x0029C4B4)</c> (key 0, self-links). Healthy leaves use <c>head_index</c> as nil.
/// A child halfword of <c>0</c> (left by <c>0x00239100</c> node-clear, or corruption)
/// resolves to the freelist header at base — not a tree node — and the search only
/// terminates when the walk returns to the sentinel head, so it infinite-loops through
/// freelist words misread as indices (observed PC <c>0x00176B00</c>, s0 → base / OOB
/// zero page). Guard forces the not-found epilogue when the current node is unusable.
/// </para>
/// <para>
/// Heap config (2026-07-30): lookups go string → hash (<c>0x175740</c>, h=h*127+c) → BST
/// search → 8-byte entry <c>{hash,size}</c> at node.value&amp;0x7FFFFFFF; <c>0x175AB0</c>
/// returns <c>*(entry+4)</c>. Retail never plants <c>HERO_HEAP_SIZE</c> (hash
/// <c>0xF24E524F</c>) into the primary dict at <c>*(0x00304A64)</c> on this HLE path, so
/// size=0 → null freelist → empty object lists → no CDVD. Assist inserts real BST nodes
/// for HERO/SLOT/UPGRADE_HEAP_SIZE and fills residual lookup misses.
/// </para>
/// <para>
/// Post-CDVD residual (agent 2026-07-30): after freelist/list-filter stubs, EE stuck at
/// flag-set bucket walk <c>0x15F538..590</c> (live PC <c>0x15F560</c>) following OOB next
/// links (<c>v1=0x401A6800</c>). Escape + body break; do NOT empty-exit the <c>0x13DCxx</c>
/// band (real heap alloc at <c>0x13DC78</c>). Parent object list at <c>0x15F440</c> /
/// head <c>0x2CBC78</c> also emptied when corrupt.
/// </para>
/// </summary>
public sealed class GodOfWarAssist : IGameQuirkModule
{
    public string Serial => "SCUS_973.99";
    public string DisplayName => "God of War (USA)";

    /// <summary>Unfilled IOPRP version placeholder in EE .data ("....").</summary>
    public const uint IopVersionPlaceholder = 0x002C6D30;

    /// <summary>FreezeCache lock word — bltz → infinite spin at 0x185F90.</summary>
    public const uint FreezeCacheFlag = 0x0029C4DC;

    /// <summary>SCE LOADFILE / freeze-region error code written on version mismatch.</summary>
    public const uint FreezeErrorCode = 0xFFFEFFFCu;

    /// <summary>Global BST sentinel / head pointer (empty key-0 node).</summary>
    public const uint BstHeadPtr = 0x0029C4B4;

    /// <summary>Global BST node-pool base (index 0); freelist header lives here.</summary>
    public const uint BstBasePtr = 0x0029C4BC;

    /// <summary>Pool item count used at create (0x1F40 × 16 B) — upper bound for OOB.</summary>
    public const uint BstPoolItems = 0x1F40;

    /// <summary>BST search body (compare / navigate / head check).</summary>
    public const uint BstWalkPcLo = 0x00176A80;
    public const uint BstWalkPcHi = 0x00176B08;

    /// <summary>Not-found epilogue: restore s0/s1/s2/ra and jr ra (v0 already 0 at B08).</summary>
    public const uint BstSearchNotFound = 0x00176B08;

    private bool _versionPlanted;
    private bool _freezeCleared;
    private int _bstEscapes;
    private int _heapNullEscapes;
    private int _listWalkEscapes;
    private int _flagSetEscapes;
    private int _parentListEscapes;
    private int _lookupFills;
    private int _free2Escapes;
    private int _worldKickPulses;
    private int _padInjectPulses;
    private ulong _lastWorldKickCyc;
    private bool _heapDefaultsPlanted;

    /// <summary>
    /// Scratch for synthetic freelist header + 8-byte config entries (hash,size).
    /// Entries live here permanently; BST nodes are allocated from the live pool.
    /// </summary>
    public const uint HeapDefaultNodeBase = 0x01FD8000;

    /// <summary>8-byte {hash, size} entries returned by string→dict lookup (0x175890/0x175AB0).</summary>
    public const uint HeapEntryHero = HeapDefaultNodeBase + 0x100;
    public const uint HeapEntrySlot = HeapDefaultNodeBase + 0x108;
    public const uint HeapEntryUpgrade = HeapDefaultNodeBase + 0x110;

    /// <summary>Synthetic 16-byte BST nodes (not in pool) returned on forced hits.</summary>
    public const uint HeapNodeHero = HeapDefaultNodeBase + 0x120;
    public const uint HeapNodeSlot = HeapDefaultNodeBase + 0x130;
    public const uint HeapNodeUpgrade = HeapDefaultNodeBase + 0x140;

    /// <summary>
    /// Default hero/slot/upgrade heap sizes (bytes) when dict miss returns NULL.
    /// Keep modest: some init paths treat the looked-up word as a loop count / block
    /// count; 8 MiB stalls for tens of millions of cycles in zero-fill loops.
    /// </summary>
    public const uint DefaultHeroHeapSize = 0x00100000; // 1 MiB
    public const uint DefaultSlotHeapSize = 0x00080000; // 512 KiB
    public const uint DefaultUpgradeHeapSize = 0x00040000; // 256 KiB

    /// <summary>h = h*127 + c (uint32) of "HERO_HEAP_SIZE" / SLOT / UPGRADE — matches 0x175740.</summary>
    public const uint HashHeroHeapSize = 0xF24E524Fu;
    public const uint HashSlotHeapSize = 0xB22CC653u;
    public const uint HashUpgradeHeapSize = 0xBBE88F91u;

    /// <summary>Global pointer to primary string-dict object (written at 0x17599C).</summary>
    public const uint GlobalDictPtr = 0x00304A64;

    /// <summary>String→value wrappers that call BST search then mask node.value.</summary>
    public const uint DictLookupAfterSearchA = 0x001758C0; // 0x175890 body
    public const uint DictLookupAfterSearchB = 0x00175858; // 0x175828 body
    public const uint DictLookupAfterSearchC = 0x00175910; // 0x1758F8 body (hash in a1)

    /// <summary>Heap freelist walk with null s0 after HERO_HEAP_SIZE miss (live PC band).</summary>
    public const uint HeapNullPcLo = 0x0023A900;
    public const uint HeapNullPcHi = 0x0023AA00;

    /// <summary>Secondary freelist insert/walk (live stall PC 0x2396F4 / 0x2397F0).</summary>
    public const uint HeapFree2PcLo = 0x002396B0;
    public const uint HeapFree2PcHi = 0x00239810;

    public void Reset()
    {
        _versionPlanted = false;
        _freezeCleared = false;
        _bstEscapes = 0;
        _heapNullEscapes = 0;
        _listWalkEscapes = 0;
        _flagSetEscapes = 0;
        _parentListEscapes = 0;
        _lookupFills = 0;
        _free2Escapes = 0;
        _worldKickPulses = 0;
        _padInjectPulses = 0;
        _lastWorldKickCyc = 0;
        _heapDefaultsPlanted = false;
    }

    public void OnDiscMounted(Ps2System sys)
    {
        Reset();
        PlantIopRpVersion(sys);
    }

    public void OnHostPresent(Ps2System sys) => _ = sys;

    /// <summary>
    /// Plant IOPRP 3.0.0 version tag the freeze-region constructor compares after GetVersion.
    /// Real hardware fills this when UDNL applies IOPRP300.IMG.
    /// </summary>
    public static void PlantIopRpVersion(Ps2System sys)
    {
        // Only overwrite the unfilled "...." placeholder — never clobber a real version.
        uint w = sys.Memory.Read32(IopVersionPlaceholder);
        if (w == 0x2E2E2E2Eu || w == 0) // "...." or zero
        {
            // ASCII "3000" little-endian = 0x30303033
            sys.Memory.Write8(IopVersionPlaceholder + 0, (byte)'3');
            sys.Memory.Write8(IopVersionPlaceholder + 1, (byte)'0');
            sys.Memory.Write8(IopVersionPlaceholder + 2, (byte)'0');
            sys.Memory.Write8(IopVersionPlaceholder + 3, (byte)'0');
        }
    }

    public void Step(Ps2System sys)
    {
        ulong c = sys.Scheduler.MasterCycles;

        // Re-plant after ELF load (PT_LOAD overwrites OnDiscMounted plants).
        if (!_versionPlanted && c >= 500_000)
        {
            PlantIopRpVersion(sys);
            _versionPlanted = true;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine($"[GOW] planted IOPRP version \"3000\" @ 0x{IopVersionPlaceholder:X8} cyc={c}");
        }

        // Belt-and-suspenders: if freeze flag still holds the version-mismatch error after
        // sound/RPC surface is up, clear it so 0x185F28 can take the non-bltz path.
        // Only the exact SCE error code — never clear a real negative lock held by design.
        if (!_freezeCleared && c >= 5_000_000)
        {
            uint fl = sys.Memory.Read32(FreezeCacheFlag);
            if (fl == FreezeErrorCode)
            {
                // Re-plant version first so a re-check would also pass.
                PlantIopRpVersion(sys);
                sys.Memory.Write32(FreezeCacheFlag, 0);
                _freezeCleared = true;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                    Console.Error.WriteLine($"[GOW] cleared FreezeCache 0xFFFEFFFC @ 0x{FreezeCacheFlag:X8} cyc={c}");
            }
            else if (fl == 0 || (int)fl >= 0)
            {
                _freezeCleared = true; // already healthy
            }
        }

        // If EE is already trapped in the FreezeCache nop-spin (0x185F90..FA8) with a
        // non-negative flag, force PC to the continue path at 0x185FAC (flag re-check is
        // one-shot — once inside the spin, clearing memory alone cannot exit).
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFu);
        if (pc is >= 0x00185F90 and <= 0x00185FA8 && c >= 5_000_000)
        {
            uint fl = sys.Memory.Read32(FreezeCacheFlag);
            if (fl != FreezeErrorCode && (int)fl >= 0)
            {
                // Restore a0/s0 context is not required for the continue path's first uses;
                // delay slot of the original beq already set v0=0x330000 when flag was 0.
                // For bltz path we skipped that — set v0 to what the delay of the zero-check
                // would have produced so s2 = v0 + (-6072) = 0x32E848 is correct.
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0x00330000 }); // v0
                sys.EE.PC = 0x00185FAC;
                sys.EE.COP0_Status &= ~(1u << 1); // clear EXL if sticky
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                    Console.Error.WriteLine($"[GOW] escape FreezeCache spin -> 0x00185FAC cyc={c}");
            }
            else if (fl == FreezeErrorCode)
            {
                PlantIopRpVersion(sys);
                sys.Memory.Write32(FreezeCacheFlag, 0);
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0x00330000 });
                sys.EE.PC = 0x00185FAC;
                sys.EE.COP0_Status &= ~(1u << 1);
                _freezeCleared = true;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                    Console.Error.WriteLine($"[GOW] escape FreezeCache spin (cleared error) -> 0x00185FAC cyc={c}");
            }
        }

        // BST search walk guard — see class doc. Only after the dict pool exists (~3.5M).
        if (c >= 5_000_000 && pc is >= BstWalkPcLo and <= BstWalkPcHi)
            TryEscapeBstWalk(sys, pc, c);

        // After BST pool + global dict exist, plant 8-byte entries and insert HERO/SLOT/
        // UPGRADE_HEAP_SIZE into the live tree. Defer until ~28M so the game finishes its
        // own bulk dict inserts (early mutation at 5M corrupted freelist / stalled boot).
        if (!_heapDefaultsPlanted && c >= 28_000_000)
            MaybePlantHeapDefaults(sys, c);

        // If a string→dict lookup still misses a heap-size key after search (PC at post-jal
        // 0x1758C0 / 0x175858 / 0x175910), force the entry pointer so 0x175AB0 returns size.
        if (c >= 28_000_000 && (pc == DictLookupAfterSearchA || pc == DictLookupAfterSearchB || pc == DictLookupAfterSearchC))
            TryFillHeapLookupMiss(sys, pc, c);

        // Null freelist head: lhu v1,2(s0) with s0=0 after dict miss — force a synthetic
        // non-null freelist cursor so boot can continue toward first disc I/O.
        if (c >= 35_000_000 && pc is >= HeapNullPcLo and <= HeapNullPcHi)
            TryEscapeNullHeapWalk(sys, pc, c);

        // Secondary freelist at 0x2396B0 (insert/coalesce) — same null/garbage s0 failure mode;
        // live stall at 0x2396F4 after HERO sizes resolve.
        if (c >= 35_000_000 && pc is >= HeapFree2PcLo and <= HeapFree2PcHi)
            TryEscapeSecondaryFreelist(sys, pc, c);

        // Permanent freelist leaf stubs after many escapes so boot cannot re-thrash
        // 0x2396xx / 0x23A9xx forever with px=0 (live menu14 residual).
        // Earlier plant once CDVD is live or escapes accumulate — menu17 still thrashing.
        if (c >= 40_000_000 && sys.Gs.PixelsWritten == 0
            && (_free2Escapes >= 4 || _heapNullEscapes >= 4 || sys.Cdvd.SectorsRead > 0))
        {
            if (sys.Memory.Read32(HeapFree2PcLo) != 0x03E00008u)
            {
                sys.Memory.Write32(HeapFree2PcLo, 0x03E00008u); // jr ra
                sys.Memory.Write32(HeapFree2PcLo + 4, 0x0000102Du); // v0=0
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                    Console.Error.WriteLine($"[GOW] plant freelist2 stub @ 0x{HeapFree2PcLo:X8} cyc={c}");
            }
            if (sys.Memory.Read32(HeapNullPcLo) != 0x03E00008u)
            {
                sys.Memory.Write32(HeapNullPcLo, 0x03E00008u);
                sys.Memory.Write32(HeapNullPcLo + 4, 0x0000102Du);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                    Console.Error.WriteLine($"[GOW] plant freelist-null stub @ 0x{HeapNullPcLo:X8} cyc={c}");
            }
            // Also stub list-filter walk head when empty/corrupt forever (live 0x15F2C8).
            if (sys.Cdvd.SectorsRead > 0 && sys.Memory.Read32(0x0015F2C0) != 0x03E00008u
                && _listWalkEscapes >= 6)
            {
                sys.Memory.Write32(0x0015F2C0, 0x03E00008u); // jr ra
                sys.Memory.Write32(0x0015F2C4, 0x0000102Du); // v0=0
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                    Console.Error.WriteLine($"[GOW] plant list-walk stub @ 0x15F2C0 cyc={c}");
            }
            // Flag-set sibling at 0x15F538 (live menu19 residual 0x15F560).
            // Entry stub alone is not enough mid-body thrash (50k-cycle slices) — also
            // plant an unconditional branch out of the follow-next body.
            if (_flagSetEscapes >= 1)
            {
                if (sys.Memory.Read32(0x0015F538) != 0x03E00008u)
                {
                    sys.Memory.Write32(0x0015F538, 0x03E00008u); // jr ra
                    sys.Memory.Write32(0x0015F53C, 0x00000000u); // nop
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                        Console.Error.WriteLine($"[GOW] plant flag-set list stub @ 0x15F538 cyc={c}");
                }
                // beq zero,zero,0x15F590  (unconditional) at follow body so re-entry dies in 1 insn
                uint followPatch = 0x10000000u | (((0x0015F590u - 0x0015F560u - 4u) >> 2) & 0xFFFFu);
                if (sys.Memory.Read32(0x0015F560) != followPatch)
                {
                    sys.Memory.Write32(0x0015F560, followPatch);
                    sys.Memory.Write32(0x0015F564, 0x00000000u);
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                        Console.Error.WriteLine($"[GOW] plant flag-set body break @ 0x15F560 cyc={c}");
                }
            }
            // Parent object-list walker entry (0x15F440) — empty after flag-set residual.
            if (_parentListEscapes >= 2 && sys.Memory.Read32(0x0015F440) != 0x03E00008u)
            {
                sys.Memory.Write32(0x0015F440, 0x03E00008u); // jr ra
                sys.Memory.Write32(0x0015F444, 0x0000102Du); // v0=0
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                    Console.Error.WriteLine($"[GOW] plant parent-list stub @ 0x15F440 cyc={c}");
            }
            // Tag-list walk permanent empty exit.
            if (sys.Cdvd.SectorsRead > 0 && sys.Memory.Read32(0x00170BB0) != 0x03E00008u
                && _worldKickPulses >= 8)
            {
                sys.Memory.Write32(0x00170BB0, 0x03E00008u);
                sys.Memory.Write32(0x00170BB4, 0x0000102Du);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                    Console.Error.WriteLine($"[GOW] plant tag-list stub @ 0x170BB0 cyc={c}");
            }
        }

        // Post-freelist object list walk at 0x15F2xx: after synthetic/empty heap returns,
        // list heads often hold OOB pointers (live: s1=0x401A6800 past 32MiB RDRAM). The
        // walker does s1=*s1 and only stops when s1 equals the sentinel (base+0x34), so
        // open-bus zeros / garbage loops forever at 0x15F2C8. Force the empty-list epilogue.
        if (c >= 35_000_000 && pc is >= 0x0015F2C0 and <= 0x0015F414)
            TryEscapeCorruptListWalk(sys, pc, c);

        // Sibling flag-set walk at 0x15F538..590 (live residual 0x15F560 after list-walk stub):
        // for each of 5×8 bucket sentinels, while (v1=*cursor) != sentinel: flags|=2; v1=*v1.
        // Null/OOB next never equals sentinel → infinite loop at 0x15F560, px=0 forever.
        if (c >= 35_000_000 && pc is >= 0x0015F538 and <= 0x0015F58C)
            TryEscapeFlagSetListWalk(sys, pc, c);

        // Parent object-list at 0x15F440 (live residual 0x15F4D8 after flag-set escape):
        // s0 walks circular list at *0x2CBC78 with sentinel s5=0x2CBC78. Corrupt next never
        // hits sentinel → forever at 0x15F4D8. Empty the global head and force epilogue.
        if (c >= 35_000_000 && pc is >= 0x0015F440 and <= 0x0015F514)
            TryEscapeParentObjectList(sys, pc, c);

        // Software delay + flag poll at 0x17A328..0x17A35C (live PC 0x17A334):
        //   do { v0 = 20000; while (--v0); } while (*0x29C7D0 == 1);
        // Flag stuck at 1 → forever. Clear AND snap past the outer beq restart.
        if (c >= 40_000_000 && pc is >= 0x0017A320 and <= 0x0017A360)
        {
            uint fl = sys.Memory.Read32(0x0029C7D0);
            if (fl == 1)
                sys.Memory.Write32(0x0029C7D0, 0);
            // Snap countdown to 0 and jump to post-loop (0x17A350 loads next state).
            // Outer loop at 0x17A358 only restarts when flag==1 — with flag 0 we fall through.
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // v0
            if (pc < 0x0017A350)
                sys.EE.PC = 0x0017A350;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (c % 10_000_000) < 50_000)
                Console.Error.WriteLine($"[GOW] exit spin @0x17A3xx flag was {fl} cyc={c}");
        }

        // Cache writeback loop at 0x2944F8..0x29457C (live final 0x29457C): a2+=64 until
        // a2>=4096 with HLE cache-as-nop — re-entered forever by callers with px=0.
        // Permanent entry stub + force epilogue so world progress can continue.
        if (c >= 40_000_000 && sys.Gs.PixelsWritten == 0)
        {
            // Permanent: make the leaf a no-op (jr ra) so re-entry never re-stalls.
            if (sys.Memory.Read32(0x002944F0) != 0x03E00008u)
            {
                sys.Memory.Write32(0x002944F0, 0x03E00008u); // jr ra
                sys.Memory.Write32(0x002944F4, 0x00000000u); // nop
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                    Console.Error.WriteLine($"[GOW] plant cache-wb stub @ 0x002944F0 cyc={c}");
            }
            if (pc is >= 0x002944F0 and <= 0x00294580)
            {
                uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
                sys.EE.SetGpr(6, new EmotionEngine.Gpr128 { Lo = 0x1000 }); // a2 = 4096 done
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
                // Prefer live $ra when it is real code; else leaf jr.
                if (sys.Memory.IsLikelyEeCode(ra) && ra is >= 0x00100000 and < 0x00290000
                    && ra is not (>= 0x002944F0 and <= 0x00294580))
                    sys.EE.PC = ra;
                else
                    sys.EE.PC = 0x00294584; // jr ra
                sys.EE.COP0_Status &= ~0x6u;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (c % 5_000_000) < 50_000)
                    Console.Error.WriteLine($"[GOW] skip cache-wb loop pc=0x{pc:X8} -> 0x{(uint)sys.EE.PC:X8} cyc={c}");
            }
        }

        // After first CDVD, list-walk residual + sleeping workers leave px=0. Periodically
        // re-escape empty/corrupt list walks, wake peers, and inject pad so world/UI path
        // can reach a GS frame.
        if (c >= 45_000_000 && sys.Cdvd.SectorsRead > 0)
            MaybeKickWorldProgress(sys, pc, c);

        // Pre-CDVD freelist thrash at 0x23A9xx / 0x13DCxx: keep escaping so first CDVD lands.
        if (c >= 35_000_000 && sys.Cdvd.SectorsRead == 0 && pc is >= 0x0023A900 and <= 0x0023AA30)
            TryEscapeNullHeapWalk(sys, pc, c);
    }

    /// <summary>
    /// Post-CDVD world progress: empty object lists park at <c>0x15F2xx</c> and workers
    /// Sleep/WaitSema forever with <c>px=0</c>. Re-snap corrupt walks, pulse waiters, pad.
    /// Also kick past pure busy-loops at the live final PC band <c>0x13DCxx</c> /
    /// stream-ready poll at <c>0x26C0E0</c>.
    /// </summary>
    private void MaybeKickWorldProgress(Ps2System sys, uint pc, ulong c)
    {
        if (c - _lastWorldKickCyc < 200_000) return;
        _lastWorldKickCyc = c;
        if (_worldKickPulses >= 768) return;
        _worldKickPulses++;

        // Live final PC 0x26C0E0: do { v0 = 0x26BB98(); } while (v0==0);
        // 0x26BB98 returns 1 immediately when *0x2A1338==0; otherwise waits on stream
        // state that never completes under HLE → forever poll, px=0. Force ready.
        // ONLY act on the poll jal/beq (0x26C0E0..E8) — never re-snap the post-ready
        // body at 0x26C0EC (live menu18 self-kick thrash prevented body from running).
        if (pc is >= 0x0026C0E0 and <= 0x0026C0E8 && sys.Gs.PixelsWritten == 0)
        {
            // Clear the pending-stream pointer so 0x26BB98 takes the fast v0=1 path.
            sys.Memory.Write32(0x002A1338, 0);
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 }); // v0 = ready
            // Fall through past beq v0,zero,0x26C0E0 into the post-ready body.
            sys.EE.PC = 0x0026C0EC;
            sys.EE.COP0_Status &= ~0x6u;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_worldKickPulses <= 32 || _worldKickPulses % 16 == 0))
                Console.Error.WriteLine(
                    $"[GOW] force stream-ready poll pc=0x{pc:X8} -> 0x26C0EC *0x2A1338=0 " +
                    $"n={_worldKickPulses} cdvd={sys.Cdvd.SectorsRead} cyc={c}");
        }
        // Post-ready body at 0x26C0EC: table lookup *0x2A1378 → object; if null skips
        // jal 0x26C4B8 and just returns. Plant a non-null slot so the work path runs.
        else if (pc is >= 0x0026C0EC and <= 0x0026C130 && sys.Gs.PixelsWritten == 0
                 && sys.Cdvd.SectorsRead > 0)
        {
            sys.Memory.Write32(0x002A1338, 0);
            // Index 0 into table at 0x2A1358; plant pointer to a tiny synthetic object
            // whose first word is non-zero so beq v0,zero is not taken.
            const uint synthObj = 0x01FD7F00;
            const uint tableBase = 0x002A1358;
            sys.Memory.Write32(0x002A1378, 0); // index = 0
            sys.Memory.Write32(tableBase, synthObj); // table[0] = &obj
            sys.Memory.Write32(synthObj, synthObj + 16); // *obj = payload ptr (non-null)
            sys.Memory.Write32(synthObj + 16, 1); // payload non-zero
            sys.Memory.Write32(0x002A137C, 0); // allow jal 0x26C4B8 (bne v0,zero skip)
            // Also ensure s0 load source *0x305604 looks sane later.
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_worldKickPulses % 32) == 0)
                Console.Error.WriteLine(
                    $"[GOW] plant stream-work object @ 0x{synthObj:X8} pc=0x{pc:X8} " +
                    $"n={_worldKickPulses} cyc={c}");
        }
        // Also clear periodically if still px=0 after CDVD (poll may live on another thread).
        else if (sys.Gs.PixelsWritten == 0 && sys.Cdvd.SectorsRead > 0
                 && (_worldKickPulses % 8) == 0)
        {
            uint streamPtr = sys.Memory.Read32(0x002A1338);
            if (streamPtr != 0)
                sys.Memory.Write32(0x002A1338, 0);
        }

        // If still in list-walk body with a cursor that will never match sentinel, force empty exit.
        if (pc is >= 0x0015F2C0 and <= 0x0015F414)
            TryEscapeCorruptListWalk(sys, pc, c);
        if (pc is >= 0x0015F538 and <= 0x0015F58C)
            TryEscapeFlagSetListWalk(sys, pc, c);
        if (pc is >= 0x0015F440 and <= 0x0015F514)
            TryEscapeParentObjectList(sys, pc, c);

        // Live final PC band 0x170BBx — list tag walk (sb flags @ node+0x18). With empty/
        // corrupt a1 (often zeroed then movn'd) the loop at 0x170BB0..BF8 never hits the
        // sentinel at 0x170BFC → px stays 0. Force empty-list epilogue.
        if (pc is >= 0x00170BB0 and <= 0x00170BF8 && sys.Gs.PixelsWritten == 0)
        {
            uint a1 = (uint)(sys.EE.GetGpr(5).Lo & 0x1FFFFFFFUL);
            bool badCursor = a1 == 0
                || a1 < 0x00100000
                || a1 >= (uint)SystemMemory.RDRAM_SIZE
                || (_worldKickPulses % 8) == 0; // periodic force even if cursor looks ok
            if (badCursor)
            {
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
                sys.EE.PC = 0x00170BFC; // jr ra empty-list exit
                sys.EE.COP0_Status &= ~0x6u;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (_worldKickPulses <= 32 || _worldKickPulses % 32 == 0))
                    Console.Error.WriteLine(
                        $"[GOW] escape tag-list walk pc=0x{pc:X8} a1=0x{a1:X8} -> 0x170BFC " +
                        $"n={_worldKickPulses} cyc={c}");
            }
        }

        // Tag-list residual only at 0x170BBx. Do NOT include 0x13DCxx — that band is a real
        // heap allocator (0x13DC78, jal'd from 0x160280 for 2048-byte blocks). Forcing
        // empty-exit to 0x170BFC mid-prologue stole allocs and dumped EE into 0x2A0xxx data
        // (live agent3: world-list empty-exit pc=0x13DC84 → telemetry UnknownSpecial storms).
        if (pc is >= 0x00170B80 and <= 0x00170C20
            && sys.Gs.PixelsWritten == 0
            && _worldKickPulses >= 16 && (_worldKickPulses % 8) == 0)
        {
            uint resume = 0x00170BFCu;
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.PC = resume;
            sys.EE.COP0_Status &= ~0x6u;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _worldKickPulses <= 64)
                Console.Error.WriteLine(
                    $"[GOW] world-list empty-exit pc=0x{pc:X8} -> 0x{resume:X8} n={_worldKickPulses} cyc={c}");
        }

        // Rescue if world kick landed in unknown-opcode data (0x2A0xxx), mid-function
        // 0x229xxx, or CRT0 re-entry. Always leave for a safe epilogue — never re-CRT0.
        bool badBand = pc is (>= 0x002A0000 and <= 0x002B0000)
            or (>= 0x00229000 and <= 0x0022A000)
            || pc == 0x00100008u;
        if (badBand && sys.Gs.PixelsWritten == 0)
        {
            // Prefer stream-ready poll continue (0x26C0EC) or post-FreezeCache — not empty
            // tag epilogue which re-enters and $ra's into 0x2A0xxx again (live menu17).
            uint resume = 0x0026C0EC;
            if (!sys.Memory.IsLikelyEeCode(resume))
                resume = 0x00185FAC;
            uint lg = (uint)(sys.LastGoodEePc & 0x1FFFFFFFUL);
            if (lg is >= 0x00100000 and < 0x00280000
                && lg != 0x00100008
                && lg is not (>= 0x002A0000 and <= 0x002B0000)
                && lg is not (>= 0x00229000 and <= 0x0022A000)
                && lg is not (>= 0x00170BB0 and <= 0x00170C20))
                resume = lg;
            sys.Memory.Write32(0x002A1338, 0); // stream ready
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.PC = resume;
            sys.EE.COP0_Status &= ~0x6u;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_worldKickPulses % 16) == 0)
                Console.Error.WriteLine(
                    $"[GOW] rescue bad band pc=0x{pc:X8} -> 0x{resume:X8} n={_worldKickPulses} cyc={c}");
        }

        // After CDVD + many kicks still px=0: keep stream ready + IRQ credits.
        // Do NOT re-snap PC when already in post-ready body (0x26C0EC+) — that was
        // the menu18 self-kick stall. Only re-enter from true poll or dead bands.
        if (sys.Cdvd.SectorsRead > 0 && sys.Gs.PixelsWritten == 0
            && _worldKickPulses >= 32 && (_worldKickPulses % 16) == 0)
        {
            sys.Memory.Write32(0x002A1338, 0);
            sys.Memory.Write32(0x0029C7D0, 0); // spin flag
            if (pc is (>= 0x0026C0E0 and <= 0x0026C0E8))
            {
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
                sys.EE.PC = 0x0026C0EC;
                sys.EE.COP0_Status &= ~0x6u;
            }
            else if (pc is (>= 0x002A0000 and <= 0x002B0000) || pc < 0x00100000)
            {
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
                sys.EE.PC = 0x00185FAC; // post-FreezeCache continue
                sys.EE.COP0_Status &= ~0x6u;
            }
            try
            {
                sys.Intc.SetMask(sys.Intc.Mask | (1u << (int)Intc.InterruptSource.DmaController)
                    | (1u << (int)Intc.InterruptSource.VBlankStart)
                    | (1u << (int)Intc.InterruptSource.VBlankEnd));
                sys.Dmac.EnableChannelIrq((int)Dmac.Channel.GIF);
                sys.Dmac.EnableChannelIrq((int)Dmac.Channel.VIF1);
                sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.GIF, 4);
                sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.VIF1, 4);
            }
            catch { /* ignore */ }
        }

        // Live: 0x1177C4 is real float code, but after corrupt s0 the path zeros and the
        // generic nop-sled rescuer re-homes to ELF entry (catastrophic). If EE is there
        // with bad s0 after CDVD, force function epilogue via $ra / last-good.
        if (pc is >= 0x001177A0 and <= 0x00117840 && sys.Cdvd.SectorsRead > 0
            && sys.Gs.PixelsWritten == 0 && (_worldKickPulses % 4) == 0)
        {
            uint s0 = (uint)(sys.EE.GetGpr(16).Lo & 0x1FFFFFFFUL);
            bool badS0 = s0 < 0x00100000 || s0 >= (uint)SystemMemory.RDRAM_SIZE || (s0 & 3) != 0;
            if (badS0 || sys.Memory.Read32(pc) == 0)
            {
                uint resume = 0;
                uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
                if (sys.Memory.IsLikelyEeCode(ra) && ra is >= 0x00100000 and < 0x00280000
                    && ra != 0x00100008)
                    resume = ra;
                if (resume == 0 && sys.LastGoodEePc is >= 0x00100000 and < 0x00280000)
                    resume = (uint)(sys.LastGoodEePc & 0x1FFFFFFFUL);
                if (resume == 0 || resume == 0x00100008)
                    resume = 0x00170BFC;
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
                sys.EE.PC = resume;
                sys.EE.COP0_Status &= ~0x6u;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && _worldKickPulses <= 48)
                    Console.Error.WriteLine(
                        $"[GOW] escape 0x1177xx s0=0x{s0:X8} -> 0x{resume:X8} n={_worldKickPulses} cyc={c}");
            }
        }

        // After CDVD+many kicks still px=0: credit GIF/VIF1 IRQs so any queued PATH3 drains.
        if (sys.Gs.PixelsWritten == 0 && sys.Cdvd.SectorsRead > 0 && (_worldKickPulses % 16) == 0)
        {
            try
            {
                sys.Intc.SetMask(sys.Intc.Mask | (1u << (int)Intc.InterruptSource.DmaController));
                sys.Dmac.EnableChannelIrq((int)Dmac.Channel.GIF);
                sys.Dmac.EnableChannelIrq((int)Dmac.Channel.VIF1);
                sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.GIF, 2);
                sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.VIF1, 2);
            }
            catch { /* ignore */ }
        }

        // Exception-vector rescue: bad freelist/list snaps can land on 0x80000180.
        // Re-home to last good EE PC or a safe post-heap continue when EXL sticky.
        if (pc is >= 0x80000180 and <= 0x80000200 || pc < 0x00100000)
        {
            uint resume = 0;
            if (sys.LastGoodEePc is >= 0x00100000 and < 0x00300000)
                resume = (uint)(sys.LastGoodEePc & 0x1FFFFFFFUL);
            if (resume == 0)
                resume = 0x00185FAC; // post-FreezeCache continue
            sys.EE.COP0_Status &= ~0x6u;
            sys.EE.PC = resume;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _worldKickPulses <= 16)
                Console.Error.WriteLine(
                    $"[GOW] rescue exception vector -> 0x{resume:X8} n={_worldKickPulses} cyc={c}");
        }

        var k = sys.Hle?.Kernel;
        if (k != null)
        {
            foreach (var t in k.AllThreads)
            {
                if (!t.Alive) continue;
                // Re-start main only (live menu17 peer re-start left garbage WaitSemaIds).
                if (!t.Started && t.Id == 1 && t.Entry != 0 && t.Entry is >= 0x00100000 and < 0x00300000)
                {
                    try
                    {
                        k.StartAndMaybeSwitch(sys.EE, t.Id, switchNow: false, arg: 0, fromSyscall: false);
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                            && _worldKickPulses <= 16)
                            Console.Error.WriteLine(
                                $"[GOW] re-start main entry=0x{t.Entry:X8} cyc={c}");
                    }
                    catch { /* ignore */ }
                }
                if (t.Sleeping && t.WaitSemaId != 0)
                {
                    // After CDVD, residual WaitSema(3) SIF-cmd poll (live final8 0x293C64)
                    // parks forever with px=0. Pulse low ids sparingly so RPC can complete
                    // without the global SEMA_STALL_YIELD hammer.
                    bool lowSif = t.WaitSemaId > 0 && t.WaitSemaId <= 8
                        && sys.Cdvd.SectorsRead > 0 && sys.Gs.PixelsWritten == 0
                        && (_worldKickPulses % 4) == 0;
                    bool high = t.WaitSemaId >= 32;
                    if (high || lowSif)
                    {
                        try { k.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
                    }
                }
                else if (t.Sleeping && t.WaitSemaId == 0 && !t.WaitVblank)
                    k.WakeupThread(t.Id);
                while (t.SuspendCount > 0)
                    k.ResumeThread(t.Id);
                if (t.SoftSuspended) t.SoftSuspended = false;
            }
        }

        // BIOS / KSEG0 thrash (live 0x800098xx) after freelist stubs — re-home.
        if (pc is >= 0x80000000 and <= 0x80020000 || pc < 0x00100000)
        {
            uint resume = 0;
            if (sys.LastGoodEePc is >= 0x00100000 and < 0x00280000)
                resume = (uint)(sys.LastGoodEePc & 0x1FFFFFFFUL);
            if (resume == 0) resume = 0x00185FAC;
            sys.EE.COP0_Status &= ~0x6u;
            sys.EE.PC = resume;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_worldKickPulses % 16) == 0)
                Console.Error.WriteLine(
                    $"[GOW] rescue KSEG thrash -> 0x{resume:X8} n={_worldKickPulses} cyc={c}");
        }

        // Live final band 0x182A08 after stubs — force stream-ready + pad so world can draw.
        if (pc is >= 0x00182A00 and <= 0x00182B00 && sys.Gs.PixelsWritten == 0)
        {
            sys.Memory.Write32(0x002A1338, 0); // stream ready
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            // Prefer fall-through / last good rather than self-loop.
            uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            if (sys.Memory.IsLikelyEeCode(ra) && ra is >= 0x00100000 and < 0x00280000)
                sys.EE.PC = ra;
            try
            {
                sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.GIF, 2);
                sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.VIF1, 2);
            }
            catch { /* ignore */ }
        }

        // Stream-ready leaf 0x26BB98: when *0x2A1338==0 it should return v0=1 immediately.
        // Live residual agent5: PC lands on jr-ra delay 0x26BC3C with corrupt $ra after we
        // mid-jumped into the poll body without a frame. Force clean v0=1 return via $ra
        // only when $ra is real code; else post-FreezeCache continue.
        if (sys.Cdvd.SectorsRead > 0 && sys.Gs.PixelsWritten == 0
            && pc is >= 0x0026BB98 and <= 0x0026BC3C
            && (_worldKickPulses % 4) == 0)
        {
            sys.Memory.Write32(0x002A1338, 0);
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            uint resume = 0x00185FAC;
            if (sys.Memory.IsLikelyEeCode(ra) && ra is >= 0x00100000 and < 0x00280000
                && ra is not (>= 0x0026BB98 and <= 0x0026C200))
                resume = ra;
            sys.EE.PC = resume;
            sys.EE.COP0_Status &= ~0x6u;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_worldKickPulses % 16) == 0)
                Console.Error.WriteLine(
                    $"[GOW] force stream-ready return pc=0x{pc:X8} -> 0x{resume:X8} n={_worldKickPulses} cyc={c}");
        }

        // Post-list residual jr-ra delay thrash (live agent4 PC=0x186110). Do NOT mid-jump
        // into 0x26C0EC (needs a real stack frame). Do NOT include 0x185FAC (re-home target)
        // or we self-kick forever. Clear stream pending + re-home via $ra / post-FreezeCache.
        if (sys.Cdvd.SectorsRead > 0 && sys.Gs.PixelsWritten == 0
            && _flagSetEscapes >= 1 && (_worldKickPulses % 8) == 0
            && pc is >= 0x001860B0 and <= 0x00186114)
        {
            sys.Memory.Write32(0x002A1338, 0);
            uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            uint resume = 0x00185FAC;
            if (sys.Memory.IsLikelyEeCode(ra) && ra is >= 0x00100000 and < 0x00280000
                && ra is not (>= 0x001860B0 and <= 0x00186120))
                resume = ra;
            // 0x185FAC expects v0=0x330000 so s2=v0-6072 = 0x32E848 (same as FreezeCache leave).
            if (resume == 0x00185FAC)
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0x00330000 });
            else
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.PC = resume;
            sys.EE.COP0_Status &= ~0x6u;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_worldKickPulses % 16) == 0)
                Console.Error.WriteLine(
                    $"[GOW] re-home residual pc=0x{pc:X8} -> 0x{resume:X8} n={_worldKickPulses} cyc={c}");
        }

        // Dormant main after list escapes (live agent4: started=False). Main only —
        // peer re-start historically left garbage WaitSemaIds (menu17).
        if (sys.Cdvd.SectorsRead > 0 && sys.Gs.PixelsWritten == 0
            && _flagSetEscapes >= 1 && (_worldKickPulses % 16) == 0)
        {
            var kk = sys.Hle?.Kernel;
            if (kk != null)
            {
                foreach (var t in kk.AllThreads)
                {
                    if (t.Id != 1 || !t.Alive || t.Started || t.Entry == 0) continue;
                    if (t.Entry is < 0x00100000 or >= 0x00300000) continue;
                    try
                    {
                        kk.StartAndMaybeSwitch(sys.EE, t.Id, switchNow: false, arg: 0, fromSyscall: false);
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                            && _worldKickPulses <= 48)
                            Console.Error.WriteLine(
                                $"[GOW] re-start dormant main entry=0x{t.Entry:X8} cyc={c}");
                    }
                    catch { /* ignore */ }
                }
            }
        }

        // Live menu16: thrash at 0x21FFxx / 0x2200xx nop-sled (BIOS rescuer re-homes nearby).
        // Force a known post-heap continue once CDVD is live and px still 0.
        if (pc is (>= 0x0021FF00 and <= 0x00220600) && sys.Cdvd.SectorsRead > 0
            && sys.Gs.PixelsWritten == 0 && (_worldKickPulses % 4) == 0)
        {
            uint resume = 0x00185FAC; // post-FreezeCache
            uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            if (sys.Memory.IsLikelyEeCode(ra) && ra is >= 0x00100000 and < 0x00200000
                && ra is not (>= 0x0021FF00 and <= 0x00220600))
                resume = ra;
            else if (sys.LastGoodEePc is >= 0x00100000 and < 0x00200000)
                resume = (uint)(sys.LastGoodEePc & 0x1FFFFFFFUL);
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.PC = resume;
            sys.EE.COP0_Status &= ~0x6u;
            sys.Memory.Write32(0x002A1338, 0);
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_worldKickPulses % 16) == 0)
                Console.Error.WriteLine(
                    $"[GOW] escape 0x21FFxx thrash -> 0x{resume:X8} n={_worldKickPulses} cyc={c}");
        }

        if (_padInjectPulses < 8192)
        {
            _padInjectPulses++;
            int phase = _padInjectPulses % 5;
            uint buttons = phase switch
            {
                0 or 1 => (uint)PadInput.Button.Start,
                2 or 3 => (uint)PadInput.Button.Cross,
                _ => 0u
            };
            if (_padInjectPulses % 13 == 0)
                buttons = (uint)(PadInput.Button.Start | PadInput.Button.Cross);
            try { sys.Pad.SetButtons(buttons); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Flag-set bucket walk (disasm 0x15F538..0x15F590): for 5 groups × 8 sentinels,
    /// <c>v1 = *a1; while (v1 != a1) { *(v1+0xC) |= 2; v1 = *v1; }</c>. When a next-link
    /// is 0 or OOB, <c>v1</c> never equals the sentinel → infinite loop at <c>0x15F560</c>
    /// (live residual after the 0x15F2C0 filter stub). Empty each bad bucket (circular
    /// self-link) and advance; after many hits return immediately.
    /// </summary>
    private void TryEscapeFlagSetListWalk(Ps2System sys, uint pc, ulong c)
    {
        // Already at jr ra — leave alone.
        if (pc is >= 0x0015F590 and <= 0x0015F594)
            return;

        uint a0 = (uint)sys.EE.GetGpr(4).Lo;
        uint a1 = (uint)sys.EE.GetGpr(5).Lo;
        uint v1 = (uint)sys.EE.GetGpr(3).Lo;
        uint a1Phys = a1 & 0x1FFFFFFFu;
        uint v1Phys = v1 & 0x1FFFFFFFu;

        // Done with this bucket (empty or just finished) — never force.
        if (v1 == a1)
            return;

        bool badCursor = v1 == 0
            || v1Phys < 0x00100000u
            || v1Phys >= (uint)SystemMemory.RDRAM_SIZE
            || (v1Phys & 3) != 0;
        if (!badCursor)
        {
            uint next = sys.Memory.Read32(v1Phys);
            uint nextPhys = next & 0x1FFFFFFFu;
            if (next == 0
                || nextPhys < 0x00100000u
                || nextPhys >= (uint)SystemMemory.RDRAM_SIZE
                || next == v1 || nextPhys == v1Phys)
                badCursor = true;
        }

        // Live thrash is always at the follow-next body 0x15F560..570. Outside that, only
        // act on a clearly bad cursor so healthy short walks complete.
        bool inFollowBody = pc is >= 0x0015F560 and <= 0x0015F570;
        if (!badCursor && !inFollowBody)
            return;
        // First visit to follow-body with a "valid" multi-node ring: still force after re-kick.
        if (!badCursor && inFollowBody && _flagSetEscapes == 0 && (_worldKickPulses % 8) != 0)
            return;

        // Sanitize current sentinel to empty circular list so re-entry is cheap.
        if (a1Phys is >= 0x00100000u and < (uint)SystemMemory.RDRAM_SIZE && (a1Phys & 3) == 0)
            sys.Memory.Write32(a1Phys, a1);

        // Also empty broken sibling buckets around a0 when a0 looks like a world block.
        uint a0Phys = a0 & 0x1FFFFFFFu;
        if (a0Phys is >= 0x00100000u and < (uint)SystemMemory.RDRAM_SIZE - 0x180u && (a0Phys & 3) == 0)
        {
            for (uint g = 0; g < 5; g++)
            {
                uint group = a0Phys + g * 0x40u;
                for (uint b = 0; b < 8; b++)
                {
                    uint sent = group + 0x34u + b * 8u;
                    if (sent + 4 >= (uint)SystemMemory.RDRAM_SIZE) break;
                    uint head = sys.Memory.Read32(sent);
                    uint hp = head & 0x1FFFFFFFu;
                    if (head == 0 || hp < 0x00100000u || hp >= (uint)SystemMemory.RDRAM_SIZE)
                        sys.Memory.Write32(sent, sent);
                }
            }
        }

        _flagSetEscapes++;

        // Always hard-return: soft bucket advance left a0/a1 corrupt across 50k slices
        // (live: a0=0x59FA68 → a0=0) and re-entered the thrash. jr ra is safe for empty world.
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.SetGpr(3, new EmotionEngine.Gpr128 { Lo = a1 });
        sys.EE.PC = 0x0015F590; // jr ra
        sys.EE.COP0_Status &= ~0x6u;

        // Permanent body break so the next 50k-cycle slice cannot re-thrash mid-function.
        uint followPatch = 0x10000000u | (((0x0015F590u - 0x0015F560u - 4u) >> 2) & 0xFFFFu);
        if (sys.Memory.Read32(0x0015F560) != followPatch)
        {
            sys.Memory.Write32(0x0015F560, followPatch);
            sys.Memory.Write32(0x0015F564, 0x00000000u);
        }
        if (sys.Memory.Read32(0x0015F538) != 0x03E00008u)
        {
            sys.Memory.Write32(0x0015F538, 0x03E00008u);
            sys.Memory.Write32(0x0015F53C, 0x00000000u);
        }

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _flagSetEscapes <= 16)
            Console.Error.WriteLine(
                $"[GOW] escape flag-set list pc=0x{pc:X8} a0=0x{a0:X8} a1=0x{a1:X8} v1=0x{v1:X8} " +
                $"-> 0x15F590 n={_flagSetEscapes} cyc={c}");
    }

    /// <summary>
    /// Global object list walker (disasm 0x15F440..0x15F534): <c>s0 = *0x2CBC78</c>,
    /// sentinel <c>s5 = 0x2CBC78</c>, advance <c>s0 = *s0</c> until equal. Corrupt links
    /// (live residual 0x15F4D8 after flag-set escape) never hit sentinel. Empty the head
    /// and snap to restore epilogue.
    /// </summary>
    private void TryEscapeParentObjectList(Ps2System sys, uint pc, ulong c)
    {
        // Epilogue only — leave alone.
        if (pc is >= 0x0015F514 and <= 0x0015F534)
            return;

        const uint listHeadCell = 0x002CBC78; // lui 0x2D; addiu -17288
        uint s0 = (uint)sys.EE.GetGpr(16).Lo;
        uint s5 = (uint)sys.EE.GetGpr(21).Lo;
        uint s0Phys = s0 & 0x1FFFFFFFu;

        // If s5 was not yet set (prologue), use the known cell.
        if (s5 == 0 || s5 < 0x00100000u)
            s5 = listHeadCell;

        bool bad = s0 == 0
            || s0Phys < 0x00100000u
            || s0Phys >= (uint)SystemMemory.RDRAM_SIZE
            || (s0Phys & 3) != 0
            || s0 == listHeadCell && sys.Memory.Read32(listHeadCell) != listHeadCell && _parentListEscapes > 0;

        if (!bad && s0 != s5)
        {
            uint next = sys.Memory.Read32(s0Phys);
            uint np = next & 0x1FFFFFFFu;
            if (next == 0 || np < 0x00100000u || np >= (uint)SystemMemory.RDRAM_SIZE
                || next == s0 || np == s0Phys)
                bad = true;
            // Live thrash body: force after first detection / periodic kick.
            if (!bad && pc is >= 0x0015F4D8 and <= 0x0015F4E0
                && (_parentListEscapes > 0 || (_worldKickPulses % 4) == 0))
                bad = true;
        }

        if (!bad && s0 == s5)
            return; // healthy empty
        if (!bad && _parentListEscapes == 0 && pc is < 0x0015F4D0)
            return; // let short healthy walks run once

        // Empty circular global head.
        sys.Memory.Write32(listHeadCell, listHeadCell);
        sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = s5 }); // s0 = sentinel
        sys.EE.SetGpr(21, new EmotionEngine.Gpr128 { Lo = s5 }); // s5
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
        // Snap to restore epilogue (same as empty-list path at 0x15F514).
        sys.EE.PC = 0x0015F514;
        sys.EE.COP0_Status &= ~0x6u;
        _parentListEscapes++;

        if (_parentListEscapes >= 2 && sys.Memory.Read32(0x0015F440) != 0x03E00008u)
        {
            sys.Memory.Write32(0x0015F440, 0x03E00008u);
            sys.Memory.Write32(0x0015F444, 0x0000102Du);
        }

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _parentListEscapes <= 12)
            Console.Error.WriteLine(
                $"[GOW] escape parent object list pc=0x{pc:X8} s0=0x{s0:X8} s5=0x{s5:X8} " +
                $"-> 0x15F514 n={_parentListEscapes} cyc={c}");
    }

    /// <summary>
    /// Object-list filter walk (disasm 0x15F280..0x15F438): <c>s1</c> walks a singly-linked
    /// list; end when <c>s1 == (s2+s4+s5)+0x34</c>. Corrupt/OOB <c>s1</c> never matches →
    /// infinite loop. Snap to restore epilogue at 0x15F414 (same as empty-list exit).
    /// </summary>
    private void TryEscapeCorruptListWalk(Ps2System sys, uint pc, ulong c)
    {
        uint s1 = (uint)sys.EE.GetGpr(17).Lo; // list cursor
        // EE list nodes often use uncached KSEG0-style pointers (bit30 set: 0x4xxxxxxx).
        // Phys = & 0x1FFFFFFF. Live false-positive: s1=0x401A6800 → phys 0x001A6800 (valid).
        uint phys = s1 & 0x1FFFFFFFu;
        bool oob = s1 == 0
            || phys < 0x00100000u
            || phys >= (uint)SystemMemory.RDRAM_SIZE
            || (phys & 3) != 0;
        if (!oob)
        {
            // In-range but next link already truly OOB (not just uncached) — one step from disaster.
            uint next = sys.Memory.Read32(phys);
            uint nextPhys = next & 0x1FFFFFFFu;
            if (next != 0 && (nextPhys < 0x00100000u || nextPhys >= (uint)SystemMemory.RDRAM_SIZE))
                oob = true;
            else if (next == s1 || next == phys)
                oob = true; // self-loop
            else
                return; // healthy (possibly uncached) pointer — do not touch
        }

        // Epilogue restores s0..s7/ra from the large frame and jr ra.
        sys.EE.PC = 0x0015F414;
        sys.EE.COP0_Status &= ~(1u << 1);
        _listWalkEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _listWalkEscapes <= 12)
            Console.Error.WriteLine(
                $"[GOW] escape corrupt list walk pc=0x{pc:X8} s1=0x{s1:X8} phys=0x{phys:X8} -> 0x15F414 n={_listWalkEscapes} cyc={c}");
    }

    /// <summary>
    /// Plant 8-byte config entries + insert BST nodes for HERO/SLOT/UPGRADE_HEAP_SIZE into
    /// the live string dict so 0x175AB0 returns real arena sizes (not NULL → null freelist).
    /// Node layout (disasm 0x1769F8 / 0x239100): key@0, value@4 (entry*|bit31), parent@8,
    /// right@10 (key &lt; node), left@12 (key &gt; node); nil index = head.
    /// </summary>
    private void MaybePlantHeapDefaults(Ps2System sys, ulong c)
    {
        uint baseAddr = sys.Memory.Read32(BstBasePtr);
        uint head = sys.Memory.Read32(BstHeadPtr);
        if (baseAddr == 0 || head == 0)
            return;

        uint dict = sys.Memory.Read32(GlobalDictPtr);
        if (dict == 0 || dict < 0x00100000u || dict >= (uint)SystemMemory.RDRAM_SIZE)
            return;
        uint rootSlot = dict + 4;
        uint root = sys.Memory.Read32(rootSlot);
        if (root == 0)
            return; // tree not linked yet

        // 8-byte entries: {hash, size} — 0x175890 returns entry*, 0x175AB0 loads size at +4.
        WriteHeapEntry(sys, HeapEntryHero, HashHeroHeapSize, DefaultHeroHeapSize);
        WriteHeapEntry(sys, HeapEntrySlot, HashSlotHeapSize, DefaultSlotHeapSize);
        WriteHeapEntry(sys, HeapEntryUpgrade, HashUpgradeHeapSize, DefaultUpgradeHeapSize);

        int inserted = 0;
        if (BstInsert(sys, baseAddr, head, rootSlot, HashHeroHeapSize, HeapEntryHero)) inserted++;
        if (BstInsert(sys, baseAddr, head, rootSlot, HashSlotHeapSize, HeapEntrySlot)) inserted++;
        if (BstInsert(sys, baseAddr, head, rootSlot, HashUpgradeHeapSize, HeapEntryUpgrade)) inserted++;

        // Keep synthetic freelist as fallback if real heap init still mis-wires buckets.
        PlantFreelistHeader(sys);

        _heapDefaultsPlanted = true;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[GOW] BST heap config insert n={inserted} dict=0x{dict:X8} root=0x{root:X8} " +
                $"base=0x{baseAddr:X8} head=0x{head:X8} hero=0x{DefaultHeroHeapSize:X} cyc={c}");
    }

    private static void WriteHeapEntry(Ps2System sys, uint entryAddr, uint hash, uint size)
    {
        sys.Memory.Write32(entryAddr + 0, hash);
        sys.Memory.Write32(entryAddr + 4, size);
    }

    /// <summary>
    /// Insert (key → entryPtr|0x80000000) into the live index-linked BST. Returns false if
    /// key already present or no free pool slot. Navigation mirrors 0x176A80 (inverted L/R).
    /// </summary>
    private static bool BstInsert(Ps2System sys, uint baseAddr, uint head, uint rootSlot, uint key, uint entryPtr)
    {
        uint headIdx = (head - baseAddr) >> 4;
        uint poolEnd = baseAddr + BstPoolItems * 16u;
        if (head < baseAddr || head >= poolEnd || ((head - baseAddr) & 0xF) != 0)
            return false;

        // Already present?
        if (BstFind(sys, baseAddr, head, rootSlot, key) != 0)
            return true;

        uint node = AllocBstNode(sys, baseAddr, head, poolEnd);
        if (node == 0)
            return false;

        uint nodeIdx = (node - baseAddr) >> 4;
        // Clear + fill (same as 0x176C58 / 0x239140).
        sys.Memory.Write32(node + 0, key);
        sys.Memory.Write32(node + 4, entryPtr | 0x80000000u);
        WriteHu(sys, node + 8, (ushort)headIdx);  // parent nil until linked
        WriteHu(sys, node + 10, (ushort)headIdx); // right = nil
        WriteHu(sys, node + 12, (ushort)headIdx); // left = nil

        uint root = sys.Memory.Read32(rootSlot);
        if (root == 0 || root == head)
        {
            sys.Memory.Write32(rootSlot, node);
            return true;
        }

        // Walk to nil leaf parent (same compare as search).
        uint cur = root;
        for (int guard = 0; guard < 4096; guard++)
        {
            if (cur == head || cur < baseAddr || cur >= poolEnd)
                break;
            uint curKey = sys.Memory.Read32(cur);
            if (curKey == key)
            {
                // Race: update value in place.
                sys.Memory.Write32(cur + 4, entryPtr | 0x80000000u);
                return true;
            }

            bool goRight = key < curKey; // matches 0x176A90: key < node → lhu +10
            uint childOff = goRight ? 10u : 12u;
            ushort childIdx = ReadHu(sys, cur + childOff);
            uint child = baseAddr + ((uint)childIdx << 4);
            if (childIdx == headIdx || child == head || child < baseAddr || child >= poolEnd)
            {
                // Link as child of cur.
                uint parentIdx = (cur - baseAddr) >> 4;
                WriteHu(sys, node + 8, (ushort)parentIdx);
                WriteHu(sys, cur + childOff, (ushort)nodeIdx);
                return true;
            }
            cur = child;
        }

        // Fallback: make new root, hang old root on inverted child side.
        uint oldRoot = sys.Memory.Read32(rootSlot);
        if (oldRoot != 0 && oldRoot != head)
        {
            uint oldKey = sys.Memory.Read32(oldRoot);
            uint oldIdx = (oldRoot - baseAddr) >> 4;
            WriteHu(sys, oldRoot + 8, (ushort)nodeIdx);
            if (key < oldKey)
                WriteHu(sys, node + 10, (ushort)oldIdx); // key < old → old is right child
            else
                WriteHu(sys, node + 12, (ushort)oldIdx);
        }
        sys.Memory.Write32(rootSlot, node);
        return true;
    }

    private static uint BstFind(Ps2System sys, uint baseAddr, uint head, uint rootSlot, uint key)
    {
        uint headIdx = (head - baseAddr) >> 4;
        uint poolEnd = baseAddr + BstPoolItems * 16u;
        uint cur = sys.Memory.Read32(rootSlot);
        for (int guard = 0; guard < 4096; guard++)
        {
            if (cur == 0 || cur == head || cur < baseAddr || cur >= poolEnd)
                return 0;
            uint curKey = sys.Memory.Read32(cur);
            if (curKey == key)
                return cur;
            bool goRight = key < curKey;
            ushort childIdx = ReadHu(sys, cur + (goRight ? 10u : 12u));
            if (childIdx == headIdx)
                return 0;
            cur = baseAddr + ((uint)childIdx << 4);
        }
        return 0;
    }

    private static ushort ReadHu(Ps2System sys, uint addr) =>
        (ushort)(sys.Memory.Read8(addr) | (sys.Memory.Read8(addr + 1) << 8));

    private static void WriteHu(Ps2System sys, uint addr, ushort v)
    {
        sys.Memory.Write8(addr, (byte)(v & 0xFF));
        sys.Memory.Write8(addr + 1, (byte)(v >> 8));
    }

    /// <summary>
    /// Grab a free 16-byte pool slot. Prefer the live freelist at pool-manager+0x10
    /// (same contract as 0x13DA10: next pointer at word0). Fall back to scanning for
    /// all-zero slots. Never returns base (index 0) or the sentinel head.
    /// </summary>
    private static uint AllocBstNode(Ps2System sys, uint baseAddr, uint head, uint poolEnd)
    {
        uint manager = sys.Memory.Read32(0x0029C4B8);
        if (manager is >= 0x00100000 and < 0x02000000)
        {
            // 0x13DA10: free = *(manager+0x10); if free: *(manager+0x10) = *free
            uint free = sys.Memory.Read32(manager + 0x10);
            if (free >= baseAddr && free < poolEnd && ((free - baseAddr) & 0xF) == 0
                && free != head && free != baseAddr)
            {
                uint next = sys.Memory.Read32(free);
                if (next == 0 || (next >= baseAddr && next < poolEnd && ((next - baseAddr) & 0xF) == 0))
                {
                    sys.Memory.Write32(manager + 0x10, next);
                    for (uint o = 0; o < 16; o += 4)
                        sys.Memory.Write32(free + o, 0);
                    return free;
                }
            }
        }

        uint headIdx = (head - baseAddr) >> 4;
        for (int i = (int)BstPoolItems - 1; i >= 1; i--)
        {
            if ((uint)i == headIdx) continue;
            uint addr = baseAddr + ((uint)i << 4);
            if (addr >= poolEnd || addr == head || addr == baseAddr) continue;
            uint k = sys.Memory.Read32(addr);
            uint v = sys.Memory.Read32(addr + 4);
            if (k != 0 || v != 0) continue;
            ushort p = ReadHu(sys, addr + 8);
            ushort r = ReadHu(sys, addr + 10);
            ushort l = ReadHu(sys, addr + 12);
            if (p == 0 && r == 0 && l == 0)
                return addr;
        }
        return 0;
    }

    /// <summary>
    /// After BST search in 0x175828/0x175890/0x1758F8: if miss on a heap-size key, return
    /// the planted 8-byte entry pointer so the 0x175AB0 epilogue yields a non-zero size.
    /// </summary>
    private void TryFillHeapLookupMiss(Ps2System sys, uint pc, ulong c)
    {
        uint v0 = (uint)sys.EE.GetGpr(2).Lo;
        if (v0 != 0)
            return; // already found

        // Ensure entries exist even if insert has not fired yet.
        if (!_heapDefaultsPlanted)
            MaybePlantHeapDefaults(sys, c);
        WriteHeapEntry(sys, HeapEntryHero, HashHeroHeapSize, DefaultHeroHeapSize);
        WriteHeapEntry(sys, HeapEntrySlot, HashSlotHeapSize, DefaultSlotHeapSize);
        WriteHeapEntry(sys, HeapEntryUpgrade, HashUpgradeHeapSize, DefaultUpgradeHeapSize);

        uint entry = 0;
        if (pc == DictLookupAfterSearchC)
        {
            // 0x1758F8: a1 still holds the hash key (no stack string).
            uint hash = (uint)sys.EE.GetGpr(5).Lo;
            entry = EntryForHash(hash);
        }
        else
        {
            // 0x175828 / 0x175890: uppercased key string was built at sp+0 (24-byte cap).
            uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFuL);
            if (sp is >= 0x00100000 and < 0x02000000)
                entry = EntryForStackString(sys, sp);
        }

        if (entry == 0)
            return;

        // Land on the common "have value" path: v0 = entry (already bit31-clear).
        // 0x1758C0/58: bnel would have taken found path; force PC past the v0=0 store.
        // Found path ends at 0x1758DC / 0x175874 / 0x17592C with v0 = value&0x7FFFFFFF.
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = entry });
        sys.EE.SetGpr(3, new EmotionEngine.Gpr128 { Lo = entry }); // v1 used on found path
        uint cont = pc switch
        {
            DictLookupAfterSearchA => 0x001758DC, // after and-mask
            DictLookupAfterSearchB => 0x00175874,
            DictLookupAfterSearchC => 0x0017592C,
            _ => pc
        };
        sys.EE.PC = cont;
        _lookupFills++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _lookupFills <= 12)
            Console.Error.WriteLine(
                $"[GOW] fill heap lookup miss pc=0x{pc:X8} entry=0x{entry:X8} -> 0x{cont:X8} " +
                $"n={_lookupFills} cyc={c}");
    }

    private static uint EntryForHash(uint hash) => hash switch
    {
        HashHeroHeapSize => HeapEntryHero,
        HashSlotHeapSize => HeapEntrySlot,
        HashUpgradeHeapSize => HeapEntryUpgrade,
        _ => 0
    };

    private static uint EntryForStackString(Ps2System sys, uint sp)
    {
        // Compare first bytes of uppercased key on stack.
        // "HERO_HEAP_SIZE\0", "SLOT_HEAP_SIZE\0", "UPGRADE_HEAP_SIZE\0"
        uint w0 = sys.Memory.Read32(sp);
        uint w1 = sys.Memory.Read32(sp + 4);
        uint w2 = sys.Memory.Read32(sp + 8);
        uint w3 = sys.Memory.Read32(sp + 12);
        // LE ASCII
        if (w0 == 0x4F524548 && w1 == 0x4145485F && w2 == 0x49535F50 && (w3 & 0xFFFF) == 0x455A)
            return HeapEntryHero; // HERO_HEAP_SIZE
        if (w0 == 0x544F4C53 && w1 == 0x4145485F && w2 == 0x49535F50 && (w3 & 0xFFFF) == 0x455A)
            return HeapEntrySlot; // SLOT_HEAP_SIZE
        if (w0 == 0x52475055 && w1 == 0x5F454441 && w2 == 0x50414548 && w3 == 0x5A49535F)
            return HeapEntryUpgrade; // UPGRADE_HEAP_SIZE (16 chars incl. partial)
        return 0;
    }

    /// <summary>
    /// Freelist bucket with one free node so alloc can carve real arena bytes instead of
    /// only taking the empty-header path (which returned the header itself and left list
    /// heads as garbage OOB pointers).
    /// Layout (disasm 0x23A9xx): header at base; free list circular at header+0x38;
    /// free nodes also carry a next-link at +0x38 and size at +2.
    /// </summary>
    private static void PlantFreelistHeader(Ps2System sys)
    {
        const uint header = HeapDefaultNodeBase;
        const uint freeNode = HeapDefaultNodeBase + 0x80;
        // Arena payload after free-node header (~1.5 MiB in high RDRAM scratch — keep clear of 0x01FE*).
        // Entries occupy +0x100..+0x118; put arena after that.
        const uint arena = HeapDefaultNodeBase + 0x200;
        const uint arenaBytes = 0x00180000; // 1.5 MiB

        // Header: tag=1, sizeUnits large.
        sys.Memory.Write32(header + 0x00, 0x2000_0001u);
        for (uint o = 4; o < 0x38; o += 4)
            sys.Memory.Write32(header + o, 0);
        // Non-empty: *(header+0x38) = freeNode (not self).
        sys.Memory.Write32(header + 0x38, freeNode);
        sys.Memory.Write32(header + 0x3C, DefaultHeroHeapSize);

        // Free node: sizeUnits at +2, next at +0x38 points back to header+0x38 (circular end).
        sys.Memory.Write32(freeNode + 0x00, 0x1800_0001u); // ~1.5MiB in 1KiB units-ish
        for (uint o = 4; o < 0x38; o += 4)
            sys.Memory.Write32(freeNode + o, 0);
        sys.Memory.Write32(freeNode + 0x38, header + 0x38);
        // Some walkers store the usable payload pointer at +0x3C / use node+0x40 as data.
        sys.Memory.Write32(freeNode + 0x3C, arena);
        sys.Memory.Write32(freeNode + 0x40, arenaBytes);
        // Zero a small prefix of the arena so first stores are defined.
        for (uint o = 0; o < 0x100; o += 4)
            sys.Memory.Write32(arena + o, 0);
    }

    /// <summary>
    /// Escape freelist walk at 0x2396B0. Live: after HERO sizes resolve, PC sticks at
    /// 0x2396F4 for tens of M cycles on a circular/garbage free chain. Healthy path is a
    /// few dozen instructions — after the first assist (or any primary freelist help),
    /// snap straight to the epilogue at 0x239744.
    /// </summary>
    private void TryEscapeSecondaryFreelist(Ps2System sys, uint pc, ulong c)
    {
        // Only snap walk bodies (not setup prologues / epilogues which must run).
        // Two sibling walkers: 0x2396F0..740 and 0x2397A0..7F8 (live final 0x2397F0).
        // Do NOT re-snap epilogue (0x239744..750 / 0x2397FC..80C) — live menu13 thrash.
        if (pc is (>= 0x00239744 and <= 0x00239750) or (>= 0x002397FC and <= 0x0023980C))
            return;
        if (pc is < 0x002396F0 or (> 0x00239740 and < 0x002397A0) or > 0x002397F8)
            return;
        // Cap escapes — endless snap→re-entry can corrupt callers (live final7: EXL 0x80000200
        // after permanent entry stubs). Soft-cap without patching .text.
        if (_free2Escapes >= 48)
            return;

        uint s0 = (uint)sys.EE.GetGpr(16).Lo;
        if (!_heapDefaultsPlanted)
            MaybePlantHeapDefaults(sys, c);

        // Force list cursor to sentinel so natural fall-through also exits, then snap.
        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
        if (sp is >= 0x00100000 and < 0x02000000)
        {
            // sp+0 = sentinel, sp+4 = cursor — make them equal so bne never restarts.
            uint sent = sys.Memory.Read32(sp + 0);
            sys.Memory.Write32(sp + 4, sent);
        }

        uint epi = pc >= 0x002397A0 ? 0x002397FCu : 0x00239744u;
        sys.EE.PC = epi;
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
        sys.EE.COP0_Status &= ~0x6u;
        _free2Escapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _free2Escapes <= 24)
            Console.Error.WriteLine(
                $"[GOW] escape secondary freelist pc=0x{pc:X8} s0=0x{s0:X8} -> 0x{epi:X8} n={_free2Escapes} cyc={c}");
    }

    /// <summary>
    /// Escape freelist walk at 0x23A9xx when <c>s0</c> is null/garbage after a dict miss.
    /// Plants empty-bucket header (self-link at +0x38) and, if already mid-walk, snaps PC
    /// to the empty-list path at <c>0x23A9CC</c> which returns the header in <c>v0</c>.
    /// </summary>
    private void TryEscapeNullHeapWalk(Ps2System sys, uint pc, ulong c)
    {
        uint s0 = (uint)sys.EE.GetGpr(16).Lo;
        bool onSynthetic = s0 == HeapDefaultNodeBase;
        // Live freelist pointer in low RDRAM that is NOT our plant — leave alone.
        if (!onSynthetic && s0 is >= 0x00100000 and < 0x01FD0000)
            return;
        // Outside any plausible freelist — treat as null.
        if (!onSynthetic && s0 is >= 0x00100000 and < 0x02000000 && s0 != HeapDefaultNodeBase)
        {
            // Pointer into mid-RDRAM that isn't our header: only assist if +0x38 link is broken
            // (walk would infinite-loop on garbage). Otherwise leave real heaps alone.
            uint link = sys.Memory.Read32(s0 + 0x38);
            if (link != 0 && link != s0 + 0x38 && link >= 0x00100000 && link < 0x02000000)
                return;
        }

        if (!_heapDefaultsPlanted)
            MaybePlantHeapDefaults(sys, c);
        else
            PlantFreelistHeader(sys);

        sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = HeapDefaultNodeBase }); // s0
        // sp[0] = end marker (s0+0x38), sp[4] = walk cursor — force both to end so loop exits.
        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
        if (sp is >= 0x00100000 and < 0x02000000)
        {
            uint end = HeapDefaultNodeBase + 0x38;
            sys.Memory.Write32(sp + 0, end);
            sys.Memory.Write32(sp + 4, end);
        }
        // Mid-walk (0x23A978..C8): skip to empty-list continuation which returns s0.
        if (pc is >= 0x0023A978 and <= 0x0023A9C8)
            sys.EE.PC = 0x0023A9CC;
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = HeapDefaultNodeBase }); // v0
        uint a1 = (uint)sys.EE.GetGpr(5).Lo;
        // Heap descriptor freelist table slot (a1+0x80 was the index); publish header at a1 if null.
        if (a1 is >= 0x00300000 and < 0x01000000)
        {
            if (sys.Memory.Read32(a1) == 0)
                sys.Memory.Write32(a1, HeapDefaultNodeBase);
            // Clear negative freelist index so re-entry takes s0 from table not zero.
            int idx = (int)sys.Memory.Read32(a1 + 0x80);
            if (idx < 0)
                sys.Memory.Write32(a1 + 0x80, 0);
        }
        _heapNullEscapes++;
        // Hard bail after many hits: return header as allocated block (epilogue at 0x23AA28).
        if (_heapNullEscapes > 32)
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = HeapDefaultNodeBase });
            sys.EE.PC = 0x0023AA28;
        }
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _heapNullEscapes <= 8)
            Console.Error.WriteLine(
                $"[GOW] escape null heap walk pc=0x{pc:X8} s0=0x{s0:X8} -> 0x{HeapDefaultNodeBase:X8} " +
                $"n={_heapNullEscapes} cyc={c}");
    }

    /// <summary>
    /// If the current BST walk node is the freelist header (index 0) or outside the pool,
    /// force the search not-found path (v0=0, PC=<see cref="BstSearchNotFound"/>).
    /// </summary>
    private void TryEscapeBstWalk(Ps2System sys, uint pc, ulong c)
    {
        uint baseAddr = sys.Memory.Read32(BstBasePtr);
        uint head = sys.Memory.Read32(BstHeadPtr);
        if (baseAddr == 0 || head == 0)
            return;

        uint s0 = (uint)sys.EE.GetGpr(16).Lo; // current node
        uint poolEnd = baseAddr + BstPoolItems * 16u;

        // Only escape when the walk has already left the pool (or landed on index-0 freelist
        // header). Do NOT inspect self-links / zeroed nodes: create path briefly zeros a node
        // then writes self-links, and insert reuses the same search PC range — aggressive
        // escapes collapsed boot (calls 40→21, syscalls 13k→220).
        bool oob = s0 < baseAddr || s0 >= poolEnd || (s0 & 0xF) != 0;
        bool atBase = s0 == baseAddr;
        if (!(oob || atBase))
            return;

        // If the search key (s2) is a known heap-size hash, return a synthetic node so the
        // 0x175890/0x175AB0 chain yields a real size — more reliable than sampling the
        // exact post-return PC for TryFillHeapLookupMiss.
        uint key = (uint)sys.EE.GetGpr(18).Lo; // s2 = search key during 0x1769F8
        uint synth = EnsureSyntheticNode(sys, key);
        if (synth != 0)
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = synth });
            // Found path at 0x176B0C: daddu v0,s0 then epilogue — jump to restore with v0=node.
            // 0x176B08 is not-found (v0=0). Found epilogue starts at 0x176B0C after setting v0=s0.
            // Safest: set v0=synth and use not-found epilogue which still restores regs and returns v0
            // if we skip the "move v0,zero" — disasm: 176B08 is start of restore with v0 already set
            // only when falling through from match at 176A88. Check:
            //   176A88: b 176B0C; daddu v0,s0
            //   176B08: ... restore ...
            // Actually not-found falls into 176B08 with v0=0 from earlier. Found goes to 176B0C.
            // Force found-style return: v0=synth, PC=epilogue restore.
            sys.EE.PC = 0x00176B0C;
            sys.EE.COP0_Status &= ~(1u << 1);
            _bstEscapes++;
            _lookupFills++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _lookupFills <= 12)
                Console.Error.WriteLine(
                    $"[GOW] BST forced hit key=0x{key:X8} node=0x{synth:X8} pc=0x{pc:X8} cyc={c}");
            return;
        }

        // Not-found: v0=0, epilogue at 0x176B08 (move v0,zero is idempotent if already 0).
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.PC = BstSearchNotFound;
        sys.EE.COP0_Status &= ~(1u << 1);
        _bstEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
        {
            Console.Error.WriteLine(
                $"[GOW] escape BST walk pc=0x{pc:X8} s0=0x{s0:X8} base=0x{baseAddr:X8} head=0x{head:X8} " +
                $"oob={oob} atBase={atBase} key=0x{key:X8} escapes={_bstEscapes} cyc={c}");
        }
    }

    /// <summary>Ensure synthetic node+entry for a heap-size hash; return node addr or 0.</summary>
    private static uint EnsureSyntheticNode(Ps2System sys, uint key)
    {
        uint entry = EntryForHash(key);
        if (entry == 0) return 0;
        uint node = key switch
        {
            HashHeroHeapSize => HeapNodeHero,
            HashSlotHeapSize => HeapNodeSlot,
            HashUpgradeHeapSize => HeapNodeUpgrade,
            _ => 0u
        };
        if (node == 0) return 0;
        uint size = key switch
        {
            HashHeroHeapSize => DefaultHeroHeapSize,
            HashSlotHeapSize => DefaultSlotHeapSize,
            HashUpgradeHeapSize => DefaultUpgradeHeapSize,
            _ => 0u
        };
        WriteHeapEntry(sys, entry, key, size);
        sys.Memory.Write32(node + 0, key);
        sys.Memory.Write32(node + 4, entry | 0x80000000u);
        sys.Memory.Write32(node + 8, 0);
        sys.Memory.Write32(node + 12, 0);
        return node;
    }
}


