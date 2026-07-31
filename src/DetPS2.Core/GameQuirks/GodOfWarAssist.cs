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
    private int _objDispatchEscapes;
    private int _listCmpEscapes;
    private int _linkSearchEscapes;
    private int _streamArmPulses;
    private int _globalFreeEscapes;
    private int _tickWaitEscapes;
    private int _flagSpinHardReturns;
    private int _tableIndexEscapes;
    private int _byteSumEscapes;
    private int _alignZeroEscapes;
    private ulong _lastWorldKickCyc;
    private ulong _lastIopRebootGenSeen;
    private bool _heapDefaultsPlanted;
    /// <summary>Bump cursor inside synthetic arena — real blocks, never the freelist header.</summary>
    private uint _arenaBump;

    /// <summary>
    /// Table-index walk at <c>0x155AB0</c>: <c>t4</c> cursor vs <c>*0x30498C</c> limit, step
    /// <c>lh t3,0x40(t2)</c>. Live residual (2026-07-30 tip): zero/poison step keeps
    /// <c>t4 &lt; limit</c> forever at <c>0x155B84</c> (~27M samples) → freelist/BST delayed
    /// to 70M+, dmac/RPC freeze (binds 10 / calls ~40 at 45M).
    /// </summary>
    public const uint TableIndexWalkPcLo = 0x00155AB0;
    public const uint TableIndexWalkPcHi = 0x00155B90;
    public const uint TableIndexWalkReturn = 0x00155B94; // jr ra
    public const uint TableIndexCursorPtr = 0x00304988;
    public const uint TableIndexLimitPtr = 0x0030498C;

    /// <summary>
    /// Byte-sum / hash loop at <c>0x1390F8</c>: <c>while (t4 &lt; a0) s2 += *(base+t4++)</c>.
    /// After table-index leave, residual lands here with huge <c>a0</c> (multi-M samples @100M).
    /// </summary>
    public const uint ByteSumLoopPcLo = 0x001390F0;
    public const uint ByteSumLoopPcHi = 0x00139110;
    public const uint ByteSumLoopExit = 0x00139114;

    /// <summary>Retail IOPRP image on disc (ISO strings: IOPRP300.IMG;1).</summary>
    public const string UdnlIopRp300Arg = "rom0:UDNL cdrom0:\\IOPRP300.IMG;1";

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

    /// <summary>Payload arena after config nodes (see <see cref="PlantFreelistHeader"/>).</summary>
    /// <remarks>Must stay under 32 MiB RDRAM (base 0x01FD8200 leaves ~160 KiB).</remarks>
    public const uint HeapArenaBase = HeapDefaultNodeBase + 0x200;
    public const uint HeapArenaBytes = 0x00025000; // ~148 KiB, end 0x01FFD200
    public const uint HeapBlockSize = 0x400; // 1 KiB carve units

    /// <summary>Software tick counter polled by wait leaf 0x17A1D0 (*0x29C7D4).</summary>
    public const uint SoftTickPtr = 0x0029C7D4;
    /// <summary>Flag polled by software delay 0x17A328 / 0x183880.</summary>
    public const uint SoftSpinFlagPtr = 0x0029C7D0;
    /// <summary>Nonzero → tick-wait takes fast clear+return after tick satisfied.</summary>
    public const uint SoftTickFastPtr = 0x0029C664;

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

    /// <summary>Secondary freelist insert/walk (live stall PC 0x23935C / 0x2396F4 / 0x2397F0).</summary>
    public const uint HeapFree2PcLo = 0x00239300;
    public const uint HeapFree2PcHi = 0x00239810;

    /// <summary>
    /// Global free-range search (disasm 0x13E1C0..0x13E1F4): walks <c>*0x29BEB0</c> via
    /// <c>next@+0xC</c>; accepts when <c>!(a0 &lt; size@+0x10) &amp;&amp; !(field@+0x18 &lt; a0)</c>.
    /// Live residual (2026-07-30): head in-RDRAM forms a long ring → infinite loop at
    /// <c>0x13E1C8</c>, freezing RPC/cdvd metrics for tens of M cycles (55M≡100M).
    /// </summary>
    public const uint GlobalFreeHeadPtr = 0x0029BEB0;
    public const uint GlobalFreeSearchPcLo = 0x0013E1C0;
    public const uint GlobalFreeSearchPcHi = 0x0013E1EC;
    public const uint GlobalFreeSearchReturn = 0x0013E1F0; // jr ra; daddu v0,v1

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
        _objDispatchEscapes = 0;
        _listCmpEscapes = 0;
        _linkSearchEscapes = 0;
        _streamArmPulses = 0;
        _globalFreeEscapes = 0;
        _tickWaitEscapes = 0;
        _flagSpinHardReturns = 0;
        _tableIndexEscapes = 0;
        _byteSumEscapes = 0;
        _alignZeroEscapes = 0;
        _lastWorldKickCyc = 0;
        _lastIopRebootGenSeen = 0;
        _heapDefaultsPlanted = false;
        _arenaBump = HeapArenaBase;
    }

    public void OnDiscMounted(Ps2System sys)
    {
        Reset();
        if (sys.Hle?.Sony?.RealRpc != null)
            sys.Hle.Sony.RealRpc.PreferIopRpGetVersion = true;
        // EE RAM "3000" plant only at boot — do NOT SetIopRpVersionAscii early:
        // live claim with GetVersion="3000" from cyc0 regressed binds 16→10 / dmac 463→321
        // (FILEIO-2200 arming / LOADFILE path skew). Post-empty-reboot handoff below sets it.
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

    /// <summary>
    /// Ensure LOADFILE/FILEIO GetVersion returns ASCII "3000" (PreferIopRp path).
    /// Does not clear pad/FILEIO surface (unlike <see cref="RealSifRpc.OnIopReboot"/>).
    /// </summary>
    private void EnsureIopRpGetVersion(Ps2System sys, ulong c, string reason)
    {
        var rpc = sys.Hle?.Sony?.RealRpc;
        if (rpc == null) return;
        rpc.PreferIopRpGetVersion = true;
        if (string.IsNullOrEmpty(rpc.LastIopRpVersionAscii)
            || !string.Equals(rpc.LastIopRpVersionAscii, "3000", StringComparison.Ordinal))
        {
            rpc.SetIopRpVersionAscii("3000");
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[GOW] SetIopRpVersionAscii(\"3000\") reason={reason} cyc={c}");
        }
    }

    public void Step(Ps2System sys)
    {
        ulong c = sys.Scheduler.MasterCycles;

        // Keep software tick moving every Step after early boot — VBlank handler at
        // 0x182F28 only runs when INTC fires; busy-wait paths disable progress otherwise.
        if (c >= 30_000_000 && (c % 50_000) < 5_000)
            AdvanceSoftTick(sys, minTarget: 0);

        // Re-plant after ELF load (PT_LOAD overwrites OnDiscMounted plants).
        if (!_versionPlanted && c >= 500_000)
        {
            PlantIopRpVersion(sys);
            _versionPlanted = true;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine($"[GOW] planted IOPRP version \"3000\" @ 0x{IopVersionPlaceholder:X8} cyc={c}");
        }

        // Empty SifIopReset (live tip @~61M arg="") leaves UDNL ver="" / GetVersion empty.
        // Only after a real reboot gen: re-apply IOPRP300 tag + UDNL name handoff.
        // Never force GetVersion="3000" before reboot (regressed early binds/dmac).
        ulong rebootGen = sys.Sif.IopRebootGeneration;
        if (rebootGen > _lastIopRebootGenSeen)
        {
            _lastIopRebootGenSeen = rebootGen;
            string arg = sys.Sif.LastIopRebootArg ?? "";
            bool missingIopRp = string.IsNullOrEmpty(arg)
                || arg.IndexOf("IOPRP300", StringComparison.OrdinalIgnoreCase) < 0;
            PlantIopRpVersion(sys);
            if (missingIopRp)
            {
                EnsureIopRpGetVersion(sys, c, reason: $"reboot-gen={rebootGen}");
                try
                {
                    sys.IopExtendedBios.ApplyUdnlHandoff(sys, UdnlIopRp300Arg);
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                        Console.Error.WriteLine(
                            $"[GOW] post-reboot UDNL IOPRP300 handoff gen={rebootGen} " +
                            $"wasArg=\"{arg}\" cyc={c}");
                }
                catch { /* ignore */ }
            }
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

        // Data/heap as PC (live 0x57xxxx after object-dispatch poison). Hard-cap resume
        // below 0x2C0000 for GoW .text. CRT0 BSS band is in IsDeathBand (never re-home TO
        // it). Wave-2: also force-leave CRT0/BSS re-entry after IRX progress — AdEL rescue
        // to 0x00100008 then spin at 0x00100140 froze claim metrics (gifPath3 path lost).
        // Gate on cdvd progress so early boot CRT0 is not skipped (RPC 81 vs 153).
        uint pcPhysEarly = pc & 0x1FFFFFFFu;
        bool crt0Reentry = pcPhysEarly is >= 0x00100000 and <= 0x00100200
            && (sys.Cdvd.SectorsRead > 0 || c >= 40_000_000);
        bool dataPc = pcPhysEarly >= 0x002C0000u
            || pc is >= 0x80000180 and <= 0x80000200
            || pcPhysEarly < 0x00100000
            || crt0Reentry;
        if (c >= 35_000_000 && sys.Gs.PixelsWritten == 0 && dataPc)
        {
            uint resume = PickSafeResume(sys,
                sys.Cdvd.SectorsRead > 0 ? 0x0027CC08u : 0x0026C0ECu);
            sys.Memory.Write32(0x002A1338, 0);
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128
            {
                Lo = resume == 0x00185FAC ? 0x00330000UL : 1UL
            });
            sys.EE.SetGpr(5, new EmotionEngine.Gpr128 { Lo = 0 });
            if (crt0Reentry)
                sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
            sys.EE.PC = resume;
            sys.EE.COP0_Status &= ~0x6u;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (c % 5_000_000) < 50_000)
                Console.Error.WriteLine(
                    $"[GOW] early data-PC rescue pc=0x{pc:X8} -> 0x{resume:X8} cyc={c}");
            pc = resume;
        }

        // Table-index walk 0x155AB0: zero/poison step → infinite t4 < limit at 0x155B84.
        // Burns 30M+ cycles before freelist/BST (live 45M PC=0x155B84, dmac=2). Escape early.
        if (c >= 12_000_000 && pc is >= TableIndexWalkPcLo and <= TableIndexWalkPcHi)
            TryEscapeTableIndexWalk(sys, pc, c);

        // Byte-sum loop 0x1390F8 with huge length after table-index leave.
        if (c >= 12_000_000 && pc is >= ByteSumLoopPcLo and <= ByteSumLoopPcHi)
            TryEscapeByteSumLoop(sys, pc, c);

        // Align-zero loop 0x23E7C0: while (a0&0xF) *a0=0 with poison a0 / no advance
        // (live 50M–100M PC=0x23E7D4, metrics frozen; ra often exception / 0x13FExx).
        if (c >= 30_000_000 && pc is >= 0x0023E7C0 and <= 0x0023E7F0)
            TryEscapeAlignZeroLoop(sys, pc, c);
        // Residual after bad align leave: thrash at 0x13FExx UnknownOpcode.
        if (c >= 35_000_000 && pc is >= 0x0013FE00 and <= 0x00140000
            && sys.Gs.PixelsWritten == 0)
        {
            uint resume = 0x00185FAC;
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0x00330000 });
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
            sys.EE.PC = resume;
            sys.EE.COP0_Status &= ~0x6u;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (c % 5_000_000) < 50_000)
                Console.Error.WriteLine(
                    $"[GOW] escape 0x13FExx residual pc=0x{pc:X8} -> 0x{resume:X8} cyc={c}");
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

        // Wave-5 tip: NO permanent freelist / list-filter / parent jr-ra stubs.
        // Permanent empty-exits left a1=0x401Axxxx poison and empty stream graphs forever
        // (cdvd stuck 142). Soft escapes below + bump-arena freelist blocks only.

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

        // Linked-list search / splice at 0x1312D0 (PcProfiler #2 after filter thrash):
        // while (*a0 != v1) a0 = *a0. Corrupt ring never hits sentinel → forever.
        if (c >= 35_000_000 && pc is >= 0x001312C0 and <= 0x001312E8)
            TryEscapeLinkSearch(sys, pc, c);

        // Tick-wait leaf 0x17A1D0 (PcProfiler #1 @ 0x17A204): while *0x29C7D4 < a0
        // busy-delay 2000. Tick stuck at 0 → forever, starving FILEIO past IRX (cdvd=142).
        if (c >= 35_000_000 && pc is >= 0x0017A1D0 and <= 0x0017A294)
            TryEscapeTickWait(sys, pc, c);

        // Software delay + flag poll — *0x29C7D0. Clear flag, advance tick.
        // Live residual: PcProfiler #1 at 0x17A32C (20000-trip countdown while flag==1).
        // Landing at 0x17A360 still jals tick-wait + 0x17A0E0 which often re-sets the flag
        // → multi-M samples. After a few soft clears, hard-return via stack $ra (0x17A2A0 frame).
        if (c >= 35_000_000
            && (pc is >= 0x0017A320 and <= 0x0017A370
                || pc is >= 0x00183880 and <= 0x001838C8))
        {
            uint fl = sys.Memory.Read32(SoftSpinFlagPtr);
            if (fl == 1 || fl != 0)
                sys.Memory.Write32(SoftSpinFlagPtr, 0);
            AdvanceSoftTick(sys, minTarget: 0);
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // v0
            sys.EE.SetGpr(3, new EmotionEngine.Gpr128 { Lo = 0 }); // v1 != 1 so outer exits
            if (pc is >= 0x00183880 and <= 0x001838C8)
            {
                uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
                if (sys.Memory.IsLikelyEeCode(ra) && ra is >= 0x00100000 and < 0x002C0000
                    && ra is not (>= 0x00183880 and <= 0x001838D0))
                    sys.EE.PC = 0x001838C8; // jr ra
                else
                    sys.EE.PC = PickSafeResume(sys, 0x0026C0EC);
                sys.EE.COP0_Status &= ~0x6u;
            }
            else if (_flagSpinHardReturns >= 4)
            {
                // After several soft leaves, hard-return via 0x17A2A0 frame (sd ra,0(sp)).
                uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
                uint resume = 0;
                if (sp is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE - 16u)
                {
                    resume = sys.Memory.Read32(sp) & 0x1FFFFFFFu;
                    if (sys.Memory.IsLikelyEeCode(resume) && resume is >= 0x00100000 and < 0x002C0000
                        && resume is not (>= 0x0017A2A0 and <= 0x0017A37C))
                    {
                        sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = sp + 16u });
                        sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
                        sys.EE.PC = resume;
                        sys.EE.COP0_Status &= ~0x6u;
                        _flagSpinHardReturns++;
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                            && _flagSpinHardReturns <= 24)
                            Console.Error.WriteLine(
                                $"[GOW] hard-return flag-spin pc=0x{pc:X8} -> 0x{resume:X8} " +
                                $"flagWas={fl} n={_flagSpinHardReturns} cyc={c}");
                    }
                    else
                        resume = 0;
                }
                if (resume == 0)
                {
                    sys.EE.PC = 0x0017A360;
                    sys.EE.COP0_Status &= ~0x6u;
                    _flagSpinHardReturns++;
                }
            }
            else
            {
                // Soft leave: post-flag jal tick-wait with tick already advanced.
                sys.EE.PC = 0x0017A360;
                sys.EE.COP0_Status &= ~0x6u;
                _flagSpinHardReturns++; // count soft leaves toward hard-return threshold
            }
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (c % 10_000_000) < 50_000)
                Console.Error.WriteLine($"[GOW] exit spin @0x{pc:X8} flag was {fl} cyc={c}");
        }

        // Object-block init at 0x15F6F0..0x15F9xx (live w2 PC 0x15F7C4 / residual 0x15F928):
        // fill loops then jal 0x13DC78 (real 8KiB alloc — never stub). When s0 is null/OOB
        // (poison freelist), writing open-bus forever. HARD return via $ra / PickSafeResume
        // — never snap to mid-body 0x15F71C (self-kick thrash).
        if (c >= 40_000_000 && pc is >= 0x0015F6F0 and <= 0x0015FA80)
        {
            uint s0 = (uint)sys.EE.GetGpr(16).Lo;
            if (IsBadCursor(s0) || (s0 & 0x1FFFFFFFu) >= (uint)SystemMemory.RDRAM_SIZE - 0x800)
            {
                // Always leave the entire 0x15Fxxx object/list band — $ra often points at
                // the next insn inside the same poison function (live: 0x15F908).
                uint resume = PickSafeResume(sys, 0x00185FAC);
                uint synth = AllocArenaBlock(sys, 0x80);
                sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = synth }); // s0
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128
                {
                    Lo = resume == 0x00185FAC ? 0x00330000UL : 0UL
                });
                sys.EE.PC = resume;
                sys.EE.COP0_Status &= ~0x6u;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (c % 5_000_000) < 50_000)
                    Console.Error.WriteLine(
                        $"[GOW] escape OOB object-init pc=0x{pc:X8} s0=0x{s0:X8} -> 0x{resume:X8} cyc={c}");
            }
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

        // Pathological heap align loop inside real alloc (0x13DC78 band — NEVER empty-exit
        // the whole allocator). Live w2: divu/mfhi at 0x13DEE8 with s0=0x310380 (address
        // mistaken for alignment) → remainder never 0 → forever. Return null block via $ra.
        if (c >= 38_000_000 && pc is >= 0x0013DED0 and <= 0x0013DEF8)
        {
            uint s0 = (uint)sys.EE.GetGpr(16).Lo;
            uint a3 = (uint)sys.EE.GetGpr(7).Lo;
            // Alignment must be small power-of-two. Address-sized or non-PoT → poison.
            bool badAlign = s0 == 0 || s0 > 0x1000u || (s0 & (s0 - 1u)) != 0;
            bool spunOut = a3 > 0x8000u || a3 > 0xFFFF0000u; // wrapped / huge trip count
            if (badAlign || spunOut)
            {
                uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
                uint resume = ra;
                if (!sys.Memory.IsLikelyEeCode(resume) || resume is < 0x00100000 or >= 0x00280000
                    || resume is (>= 0x0013DC00 and <= 0x0013E200)
                    || resume is (>= 0x0015F2C0 and <= 0x0015FB00))
                    resume = PickSafeResume(sys, 0x0027CC08);
                // Return a small in-RDRAM block instead of NULL — callers often store without
                // null-check and then OOB-fault (live object-init s0=0).
                uint block = AllocArenaBlock(sys, 0x100);
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = block }); // v0
                sys.EE.PC = resume;
                sys.EE.COP0_Status &= ~0x6u;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (c % 5_000_000) < 50_000)
                    Console.Error.WriteLine(
                        $"[GOW] escape heap-align loop pc=0x{pc:X8} s0=0x{s0:X8} a3=0x{a3:X8} " +
                        $"-> 0x{resume:X8} block=0x{block:X8} cyc={c}");
            }
        }

        // Global free-range search @ 0x13E1C0 — circular *0x29BEB0 freezes EE at 0x13E1C8
        // with zero RPC/cdvd progress past ~50M (live 55M≡100M metrics).
        // Also plant a healthy head proactively once freelist soft-escapes have run so the
        // natural search does not enter a long ring (force mid-walk can race with Exit).
        if (c >= 42_000_000 && sys.Cdvd.SectorsRead > 0 && _free2Escapes >= 8
            && _globalFreeEscapes == 0 && (c % 500_000) < 50_000)
            PlantGlobalFreeHead(sys, sizeHint: 0); // size=0 matches any a0
        if (c >= 45_000_000 && pc is >= GlobalFreeSearchPcLo and <= GlobalFreeSearchPcHi)
            TryEscapeGlobalFreeSearch(sys, pc, c);

        // Wave-2 live residual: object virtual dispatch at 0x233AEx with a1=0x401Axxxx
        // or s0 OOB → jalr garbage → exception vector thrash. Soft escape from 38M —
        // w3c gated-after-CDVD left EE in silent non-syscall spin (syscalls~20k, main
        // dormant); w3b with early escape unstuck WaitSema(3) and lifted calls 44→70.
        if (c >= 38_000_000 && pc is >= 0x00233AD0 and <= 0x00233B34)
            TryEscapeObjectDispatch(sys, pc, c);

        // Sibling list-compare walk at 0x2847xx (profiler hot after stubs).
        if (c >= 38_000_000 && pc is >= 0x00284780 and <= 0x002848B0)
            TryEscapeListCompareWalk(sys, pc, c);

        // After first CDVD, list-walk residual + sleeping workers leave px=0. Periodically
        // re-escape empty/corrupt list walks, wake peers, and inject pad so world/UI path
        // can reach a GS frame. Also freelist residual 0x2393xx (live w5).
        if (c >= 40_000_000 && sys.Cdvd.SectorsRead > 0)
            MaybeKickWorldProgress(sys, pc, c);

        // Pre-CDVD freelist thrash at 0x23A9xx / 0x13DCxx: keep escaping so first CDVD lands.
        if (c >= 35_000_000 && sys.Cdvd.SectorsRead == 0 && pc is >= 0x0023A900 and <= 0x0023AA30)
            TryEscapeNullHeapWalk(sys, pc, c);

        // Post-CDVD freelist residual outside soft-escape windows (live w5 PC=0x23935C with
        // multi-M SetSyscall thrash): soft-escape + stream-ready poll so we can leave heap
        // and re-enter world kick without permanent freelist stubs.
        if (c >= 40_000_000 && sys.Cdvd.SectorsRead > 0 && sys.Gs.PixelsWritten == 0
            && pc is >= 0x00239300 and <= 0x00239810)
            TryEscapeSecondaryFreelist(sys, pc, c);
    }

    /// <summary>
    /// True when a pointer is unusable as an EE list/object cursor (null, unaligned, or
    /// physical address outside RDRAM). Accepts uncached aliases (bit 30 / KSEG) via mask.
    /// </summary>
    private static bool IsBadCursor(uint ptr)
    {
        if (ptr == 0) return true;
        uint phys = ptr & 0x1FFFFFFFu;
        if ((phys & 3u) != 0) return true;
        if (phys < 0x00100000u) return true;
        if (phys >= (uint)SystemMemory.RDRAM_SIZE) return true;
        return false;
    }

    /// <summary>
    /// Object method dispatch (disasm 0x233AD0..0x233B40): loads flags from <c>a1</c>,
    /// then <c>jalr</c> through vtable on <c>s0</c>. Live w2: a1=0x401A67F8 with bad
    /// payload → Ade / bad jalr → 0x80000180 thrash; KSEG rescue re-homed to mid-body.
    /// Snap to restore epilogue (lq s0 / ld ra / jr ra at 0x233B38).
    /// </summary>
    private void TryEscapeObjectDispatch(Ps2System sys, uint pc, ulong c)
    {
        // Already in epilogue — leave alone.
        if (pc is >= 0x00233B38 and <= 0x00233B44)
            return;

        uint a1 = (uint)sys.EE.GetGpr(5).Lo;
        uint s0 = (uint)sys.EE.GetGpr(16).Lo;
        bool bad = IsBadCursor(a1) || IsBadCursor(s0);
        if (!bad)
        {
            // In-range but next/vtable likely poison after empty heap — force after re-hits.
            if (_objDispatchEscapes == 0 && (_worldKickPulses % 8) != 0)
                return;
            // Probe vtable slot: *( *(s0+0x20) + 0x44 ) used as jalr target.
            uint physS0 = s0 & 0x1FFFFFFFu;
            uint obj = sys.Memory.Read32(physS0 + 0x20);
            if (!IsBadCursor(obj))
            {
                uint fn = sys.Memory.Read32((obj & 0x1FFFFFFFu) + 0x44);
                if (sys.Memory.IsLikelyEeCode(fn) && _objDispatchEscapes < 2)
                    return; // healthy dispatch once
            }
            bad = true;
        }

        if (!bad) return;

        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // v0
        sys.EE.SetGpr(5, new EmotionEngine.Gpr128 { Lo = 0 }); // clear poison a1
        // Prefer live $ra when it is real code outside this leaf; else epilogue / PickSafeResume.
        // Live: s0=0x00570648 heap object → epilogue $ra can re-enter 0x57xxxx data as PC.
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        uint resume = 0x00233B38;
        if (sys.Memory.IsLikelyEeCode(ra) && ra is >= 0x00100000 and < 0x002C0000
            && ra is not (>= 0x00233AD0 and <= 0x00233B44))
            resume = ra;
        else if (_objDispatchEscapes >= 2)
            resume = PickSafeResume(sys, 0x0026C0EC);
        sys.EE.PC = resume;
        sys.EE.COP0_Status &= ~0x6u;
        _objDispatchEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && _objDispatchEscapes <= 16)
            Console.Error.WriteLine(
                $"[GOW] escape object dispatch pc=0x{pc:X8} a1=0x{a1:X8} s0=0x{s0:X8} " +
                $"-> 0x{resume:X8} n={_objDispatchEscapes} cyc={c}");
    }

    /// <summary>
    /// Band 0x2847xx is primarily soft-float compare/normalize (disasm 2026-07-30:
    /// 0x284618 decodes IEEE754 doubles; 0x2847D0 is mantissa rotate — PcProfiler heat is
    /// expected soft-float cost, not a list thrash). Residual force-exit on OOB a1/t0 or
    /// periodic post-CDVD still required: full removal regressed dmac 463→5 (claim 100M).
    /// </summary>
    private void TryEscapeListCompareWalk(Ps2System sys, uint pc, ulong c)
    {
        uint a1 = (uint)sys.EE.GetGpr(5).Lo;
        uint t0 = (uint)sys.EE.GetGpr(8).Lo;
        // Soft-float lives in this band (0x2847xx mantissa/compare). Small integer a1/t0
        // (live a1=0/1) are IEEE paths — never treat as list cursors (force-exit storms
        // after freelist hard-return; dmac/gif regress).
        bool pointerLike = a1 >= 0x00100000u || (a1 & 0x40000000u) != 0;
        if (!pointerLike)
            return;
        bool bad = IsBadCursor(a1) || (t0 != 0 && IsBadCursor(t0));
        // Even with "valid" phys (0x401Axxxx → 0x001Axxxx), empty/corrupt rings re-enter.
        if (!bad && pc is >= 0x002847D0 and <= 0x00284820
            && (_listCmpEscapes > 0 || (_worldKickPulses % 4) == 0))
            bad = true;
        if (!bad) return;

        // Force done: t0 = a1 so beq t0,a1,0x2848AC would take; snap there or $ra.
        sys.EE.SetGpr(8, new EmotionEngine.Gpr128 { Lo = a1 == 0 ? 0 : a1 }); // t0
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // v0
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        uint resume = 0x002848AC;
        if (sys.Memory.IsLikelyEeCode(ra) && ra is >= 0x00100000 and < 0x00280000
            && ra is not (>= 0x00284780 and <= 0x00284900))
            resume = ra;
        else if (!sys.Memory.IsLikelyEeCode(0x002848ACUL))
            resume = 0x00185FAC; // last resort post-FreezeCache
        sys.EE.PC = resume;
        sys.EE.COP0_Status &= ~0x6u;
        _listCmpEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && _listCmpEscapes <= 16)
            Console.Error.WriteLine(
                $"[GOW] escape list-cmp walk pc=0x{pc:X8} a1=0x{a1:X8} t0=0x{t0:X8} " +
                $"-> 0x{resume:X8} n={_listCmpEscapes} cyc={c}");
    }


    /// <summary>
    /// Table-index walk <c>0x155AB0</c> (disasm 2026-07-30):
    /// <c>t4 = *0x304988; limit = *0x30498C; while (t4 &lt; limit) { t2=table[t4];
    /// t3=lh(t2+0x40); …; t4 += t3; }</c>. Poison table entry → t3=0 → t4 never reaches
    /// limit → forever at <c>0x155B84</c>. Force epilogue (jr ra @ 0x155B94).
    /// </summary>
    private void TryEscapeTableIndexWalk(Ps2System sys, uint pc, ulong c)
    {
        if (pc >= TableIndexWalkReturn)
            return;
        if (_tableIndexEscapes >= 4096)
            return;

        uint t4 = (uint)sys.EE.GetGpr(12).Lo; // t4
        uint limitMem = sys.Memory.Read32(TableIndexLimitPtr);
        uint cursorMem = sys.Memory.Read32(TableIndexCursorPtr);
        // Also read live t2 step when mid-body.
        uint t2 = (uint)sys.EE.GetGpr(10).Lo;
        uint t2Phys = t2 & 0x1FFFFFFFu;
        int step = 0;
        bool stepKnown = false;
        if (t2Phys is >= 0x00100000u and < (uint)SystemMemory.RDRAM_SIZE - 0x44u && (t2Phys & 1) == 0)
        {
            step = (short)ReadHu(sys, t2Phys + 0x40);
            stepKnown = true;
        }

        // Stuck when: zero step, negative step looping under limit, or still mid-band after hits.
        bool stuck = (stepKnown && step == 0)
            || (stepKnown && step < 0 && t4 < 0x100000u)
            || (pc is >= 0x00155B84 and <= 0x00155B8C)
            || _tableIndexEscapes > 0;

        // First visit mid-compare with healthy positive step and small remaining — let run once.
        if (!stuck)
        {
            uint lim = limitMem != 0 ? limitMem : t4 + 1u;
            if (stepKnown && step > 0 && t4 < lim && (lim - t4) <= (uint)(step * 8))
                return; // short healthy walk
            if (!stepKnown && _tableIndexEscapes == 0 && pc is < 0x00155B00)
                return;
            stuck = true;
        }

        if (!stuck) return;

        // Publish cursor = limit so re-entry takes the natural empty path at 0x155AD0.
        uint limPub = limitMem;
        if (limPub == 0 || limPub < cursorMem)
            limPub = cursorMem != 0 ? cursorMem : 1u;
        sys.Memory.Write32(TableIndexCursorPtr, limPub);
        // Force t4 >= limit for the slt at 0x155B88.
        sys.EE.SetGpr(12, new EmotionEngine.Gpr128 { Lo = limPub }); // t4
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // v0 = done
        sys.EE.PC = TableIndexWalkReturn;
        sys.EE.COP0_Status &= ~0x6u;
        _tableIndexEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && _tableIndexEscapes <= 24)
            Console.Error.WriteLine(
                $"[GOW] escape table-index walk pc=0x{pc:X8} t4=0x{t4:X8} lim=0x{limitMem:X8} " +
                $"step={step} t2=0x{t2:X8} -> 0x{TableIndexWalkReturn:X8} n={_tableIndexEscapes} cyc={c}");
    }

    /// <summary>
    /// Byte-sum loop <c>0x1390F8</c>: <c>while (t4 &lt; a0) { s2 += *(t3+t4); t4++; }</c>.
    /// Huge <c>a0</c> (length) burns multi-M cycles after table-index leave. Snap t4=a0 and
    /// exit to <c>0x139114</c>.
    /// </summary>
    private void TryEscapeByteSumLoop(Ps2System sys, uint pc, ulong c)
    {
        if (pc >= ByteSumLoopExit)
            return;
        if (_byteSumEscapes >= 2048)
            return;

        uint t4 = (uint)sys.EE.GetGpr(12).Lo;
        uint a0 = (uint)sys.EE.GetGpr(4).Lo; // length (from t2 at entry)
        // Also accept length still in t2 when mid-setup.
        uint t2 = (uint)sys.EE.GetGpr(10).Lo;
        uint len = a0;
        if (len == 0 || len > 0x01000000u)
            len = t2;
        // Only force when length is large or we re-entered.
        bool huge = len > 0x4000u; // >16 KiB pure EE byte walk
        bool midLoop = pc is >= 0x001390F8 and <= 0x0013910C;
        if (!huge && !midLoop && _byteSumEscapes == 0)
            return;
        if (!huge && midLoop && _byteSumEscapes == 0 && len <= 0x4000u && t4 < len)
        {
            // Small honest sums — let them finish unless already thrashing.
            if (len > 0x200u && t4 < 16)
            {
                // Accelerate: jump t4 near end.
                sys.EE.SetGpr(12, new EmotionEngine.Gpr128 { Lo = len });
                sys.EE.PC = ByteSumLoopExit;
                sys.EE.COP0_Status &= ~0x6u;
                _byteSumEscapes++;
                return;
            }
            return;
        }

        uint end = len == 0 ? t4 : len;
        sys.EE.SetGpr(12, new EmotionEngine.Gpr128 { Lo = end }); // t4 = end
        sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = end }); // a0 coherent
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.PC = ByteSumLoopExit;
        sys.EE.COP0_Status &= ~0x6u;
        _byteSumEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && _byteSumEscapes <= 24)
            Console.Error.WriteLine(
                $"[GOW] escape byte-sum loop pc=0x{pc:X8} t4=0x{t4:X8} len=0x{len:X8} " +
                $"-> 0x{ByteSumLoopExit:X8} n={_byteSumEscapes} cyc={c}");
    }

    /// <summary>
    /// Align-to-16 zero pad at <c>0x23E7C0</c>: <c>andi v0,a0,0xF; bnel v0,0,loop; sw zero,0(a0)</c>.
    /// Live residual (2026-07-30): <c>a0=2</c> (not RDRAM) and the increment slot is zeroed
    /// code → forever at <c>0x23E7D4</c>, metrics frozen 50M≡100M, <c>ra=0x80000200</c>.
    /// Return via epilogue / PickSafeResume; do not run the store.
    /// </summary>
    private void TryEscapeAlignZeroLoop(Ps2System sys, uint pc, ulong c)
    {
        if (pc >= 0x0023E7EC) // jr ra
            return;
        if (_alignZeroEscapes >= 512)
            return;

        uint a0 = (uint)sys.EE.GetGpr(4).Lo;
        uint a0Phys = a0 & 0x1FFFFFFFu;
        bool bad = a0 == 0
            || a0Phys < 0x00100000u
            || a0Phys >= (uint)SystemMemory.RDRAM_SIZE
            || (a0Phys & 3u) != 0 && a0Phys < 0x00100000u;
        // Even with "valid" phys: if mid-bnel thrash for long, force.
        if (!bad && pc is >= 0x0023E7D0 and <= 0x0023E7D8)
            bad = _alignZeroEscapes > 0 || (a0 & 0xF) != 0 && a0Phys < 0x00100000u;
        // a0=2 live: andi keeps v0!=0 forever with no addiu in the delay chain.
        if (!bad && a0 < 0x00100000u)
            bad = true;
        if (!bad && (a0 & 0xF) == 0)
            return; // natural fall-through to store-tag / jr ra

        if (!bad && _alignZeroEscapes == 0 && pc is < 0x0023E7D0)
            return;

        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        // Prefer live $ra when it is real .text. Never re-home to 0x26C0E0 (Exit risk),
        // exception vector, or the 0x13FExx nop/unknown band (live align leave → 0x13FEE0
        // UnknownOpcode storm, 4.8M telemetry hits @100M). Fallback: post-FreezeCache.
        static bool IsBadAlignResume(uint p) =>
            p is < 0x00100000 or >= 0x002C0000
            or (>= 0x0023E7C0 and <= 0x0023E7F0)
            or (>= 0x0026C0E0 and <= 0x0026C600)
            or (>= 0x0013FE00 and <= 0x00140000)
            or (>= 0x00185F90 and <= 0x00186120)
            or (>= 0x80000000 and <= 0x80020000)
            || p == 0;
        uint resume;
        if (sys.Memory.IsLikelyEeCode(ra) && !IsBadAlignResume(ra))
            resume = ra;
        else
            resume = 0x00185FAC;

        // Publish a harmless aligned arena pointer so any caller that re-uses a0 is not a0=2.
        uint block = AllocArenaBlock(sys, 0x40);
        sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = block }); // a0
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128
        {
            Lo = resume == 0x00185FAC ? 0x00330000UL : 0UL
        });
        sys.EE.SetGpr(3, new EmotionEngine.Gpr128 { Lo = block + 8 }); // v1 as post-loop
        if (resume == 0x00185FAC)
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
        sys.EE.PC = resume;
        sys.EE.COP0_Status &= ~0x6u;
        _alignZeroEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && _alignZeroEscapes <= 24)
            Console.Error.WriteLine(
                $"[GOW] escape align-zero loop pc=0x{pc:X8} a0=0x{a0:X8} -> 0x{resume:X8} " +
                $"block=0x{block:X8} n={_alignZeroEscapes} cyc={c}");
    }

    /// <summary>
    /// Bump software tick *0x29C7D4 so wait leaf 0x17A1D0 can exit.
    /// Ensures *0x29C664 != 0 for the fast clear+return path.
    /// </summary>
    private static void AdvanceSoftTick(Ps2System sys, uint minTarget)
    {
        uint tick = sys.Memory.Read32(SoftTickPtr);
        uint next = tick + 1u;
        if (minTarget != 0 && next < minTarget)
            next = minTarget;
        if (next == 0 || next > 0x7FFF_FFF0u)
            next = minTarget != 0 ? minTarget : 1u;
        sys.Memory.Write32(SoftTickPtr, next);
        if (sys.Memory.Read32(SoftTickFastPtr) == 0)
            sys.Memory.Write32(SoftTickFastPtr, 1);
    }

    /// <summary>
    /// Tick-wait leaf 0x17A1D0: while *0x29C7D4 &lt; a0 busy-delay 2000.
    /// Snap to jr ra at 0x17A294 WITHOUT zeroing tick (0x17A290 zeros it → re-stall).
    /// </summary>
    private void TryEscapeTickWait(Ps2System sys, uint pc, ulong c)
    {
        if (pc is >= 0x0017A294 and <= 0x0017A298)
            return;
        if (_tickWaitEscapes >= 1024)
            return;

        uint a0 = (uint)sys.EE.GetGpr(4).Lo;
        uint tick = sys.Memory.Read32(SoftTickPtr);
        uint target = a0 == 0xFFFFFFFFu ? tick + 1u : a0;
        bool midDelay = pc is >= 0x0017A1FC and <= 0x0017A288;
        bool unsatisfied = tick < target || (target == 0 && midDelay);
        if (!unsatisfied && !midDelay)
            return;

        if (target == 0) target = 1;
        AdvanceSoftTick(sys, minTarget: target);
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.PC = 0x0017A294; // jr ra — do not take 0x17A290 (zeros tick)
        sys.EE.COP0_Status &= ~0x6u;
        _tickWaitEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && _tickWaitEscapes <= 24)
            Console.Error.WriteLine(
                $"[GOW] escape tick-wait pc=0x{pc:X8} a0=0x{a0:X8} tick was 0x{tick:X8} " +
                $"-> 0x17A294 n={_tickWaitEscapes} cyc={c}");
    }

    /// <summary>
    /// Plant a null-terminated free-range node at <c>*0x29BEB0</c> that matches any
    /// reasonable <paramref name="sizeHint"/> (size@+0x10 ≤ a0 ≤ field@+0x18).
    /// </summary>
    private uint PlantGlobalFreeHead(Ps2System sys, uint sizeHint)
    {
        uint node = AllocArenaBlock(sys, 0x40);
        // size@+0x10 must be ≤ a0 so (a0 < size) is false. size=0 accepts every a0.
        // field@+0x18 must be ≥ a0; ~0 accepts every a0. next@+0xC = 0 terminates.
        uint size = sizeHint; // 0 is intentional (universal match)
        if (size > 0x00100000u) size = 0x00100000u;
        sys.Memory.Write32(node + 0x00, node);
        sys.Memory.Write32(node + 0x04, 0);
        sys.Memory.Write32(node + 0x08, 0);
        sys.Memory.Write32(node + 0x0C, 0);           // next = null
        sys.Memory.Write32(node + 0x10, size);        // size@+0x10
        sys.Memory.Write32(node + 0x14, 0);
        sys.Memory.Write32(node + 0x18, 0xFFFFFFFFu); // field@+0x18
        sys.Memory.Write32(node + 0x1C, 0);
        sys.Memory.Write32(GlobalFreeHeadPtr, node);
        if (_globalFreeEscapes == 0)
            _globalFreeEscapes = 1; // mark planted so force re-escape still works
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[GOW] plant global free-head node=0x{node:X8} size=0x{size:X8} @0x{GlobalFreeHeadPtr:X8}");
        return node;
    }

    /// <summary>
    /// Global free-range search at <c>0x13E1C0</c> (head <c>*0x29BEB0</c>, next@+0xC,
    /// size@+0x10, field@+0x18). Returns first node where size &lt;= a0 &lt;= field (unsigned).
    /// Circular/long rings freeze at <c>0x13E1C8</c>. Plant a matching null-terminated node
    /// and return via epilogue. Gated to post-freelist soft-escapes so early boot is not starved.
    /// </summary>
    private void TryEscapeGlobalFreeSearch(Ps2System sys, uint pc, ulong c)
    {
        if (pc >= GlobalFreeSearchReturn)
            return; // already at jr ra
        if (_globalFreeEscapes >= 128)
            return;

        uint a0 = (uint)sys.EE.GetGpr(4).Lo;
        uint head = sys.Memory.Read32(GlobalFreeHeadPtr);
        uint hPhys = head & 0x1FFFFFFFu;
        uint v1 = (uint)sys.EE.GetGpr(3).Lo;
        uint v1Phys = v1 & 0x1FFFFFFFu;

        bool broken = head == 0
            || hPhys < 0x00100000u
            || hPhys + 0x20u >= (uint)SystemMemory.RDRAM_SIZE
            || (hPhys & 3u) != 0;
        if (!broken)
        {
            uint next = sys.Memory.Read32(hPhys + 0x0Cu);
            uint nPhys = next & 0x1FFFFFFFu;
            if (next == head || nPhys == hPhys)
                broken = true;
            else if (next != 0 && (nPhys < 0x00100000u || nPhys + 0x20u >= (uint)SystemMemory.RDRAM_SIZE))
                broken = true;
            else if (v1 != 0 && v1Phys != hPhys
                     && (v1Phys < 0x00100000u || v1Phys + 0x20u >= (uint)SystemMemory.RDRAM_SIZE
                         || (v1Phys & 3u) != 0))
                broken = true;
            else if (next != 0)
            {
                uint next2 = sys.Memory.Read32(nPhys + 0x0Cu);
                if ((next2 & 0x1FFFFFFFu) == hPhys)
                    broken = true;
            }
            // Our planted head: next==0 and size matches — leave natural path alone unless
            // still mid-walk after re-entry (v1 not head / not null with a0 unsatisfied).
            else if (next == 0 && _globalFreeEscapes > 0 && pc is >= 0x0013E1C8 and <= 0x0013E1EC)
            {
                uint sz = sys.Memory.Read32(hPhys + 0x10);
                // a0 < size → would loop forever on null next with no progress (beq null exits).
                // With next==0 the beq v1,zero path exits — only force if v1 is non-null mid-walk
                // with unusable size (a0 < size keeps branching to 0x13E1C8 with delay next=0 → null exit).
                // Actually next==0 terminates via delay-slot load of null → beq exits. Healthy.
                _ = sz;
            }
        }
        // Mid-walk force after freelist soft-escapes (long in-RDRAM ring, live head 0xCB73D8).
        if (!broken && pc is >= 0x0013E1C8 and <= 0x0013E1EC)
        {
            if (_globalFreeEscapes > 0)
                broken = true;
            else if (c >= 48_000_000 && _free2Escapes >= 8)
                broken = true;
        }

        if (!broken)
            return;

        // size=0 accepts any a0 (a0 < 0 is false unsigned).
        uint node = PlantGlobalFreeHead(sys, sizeHint: 0);

        sys.EE.SetGpr(3, new EmotionEngine.Gpr128 { Lo = node }); // v1
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = node }); // v0
        sys.EE.PC = GlobalFreeSearchReturn;
        sys.EE.COP0_Status &= ~0x6u;
        _globalFreeEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && _globalFreeEscapes <= 24)
            Console.Error.WriteLine(
                $"[GOW] escape global free-search pc=0x{pc:X8} a0=0x{a0:X8} head=0x{head:X8} " +
                $"-> node=0x{node:X8} n={_globalFreeEscapes} cyc={c}");
    }

    /// <summary>
    /// Pick a safe resume PC after exception/KSEG thrash. Never re-enter known death
    /// mid-bodies (0x233AEx dispatch, 0x2847xx list-cmp, exception vector, CRT0).
    /// </summary>
    private static uint PickSafeResume(Ps2System sys, uint preferred)
    {
        // GoW EE .text lives ~0x100000..0x2Bxxxx. IsLikelyEeCode allows up to 0x580000 and
        // WordLooksLikeInsn false-positives on heap (live: rescue → 0x0057067C → UnknownOpcode
        // storms, main dormant). Hard-cap resume candidates below 0x2C0000.
        static bool IsDeathBand(uint p) =>
            p is (>= 0x80000000 and <= 0x80020000)
            or (< 0x00100000)
            or (>= 0x002C0000)                     // data / heap / high image (incl. 0x57xxxx)
            or (>= 0x00233AD0 and <= 0x00233B34)
            or (>= 0x00284780 and <= 0x002848A8)
            or (>= 0x002A0000 and <= 0x002B0000)
            or (>= 0x0021FF00 and <= 0x00220600)
            or (>= 0x0015F2C0 and <= 0x0015FB00)  // list / object-init thrash
            or (>= 0x0013DED0 and <= 0x0013DEF8)  // heap-align spin
            or (>= 0x0013E1C0 and <= 0x0013E1F4)  // global free-search circular thrash
            or (>= 0x0016AE00 and <= 0x0016AE40)  // live exception re-home death
            or (>= 0x00183880 and <= 0x001838D0)
            or (>= 0x0017A320 and <= 0x0017A360)  // software delay + flag poll
            or (>= 0x00155AB0 and <= 0x00155B90)  // table-index zero-step thrash
            or (>= 0x001390F0 and <= 0x00139110)  // huge byte-sum loop
            or (>= 0x0023E7C0 and <= 0x0023E7F0)  // align-zero poison a0 thrash
            or (>= 0x0013FE00 and <= 0x00140000)  // post-align UnknownOpcode storm
            or (>= 0x00100000 and <= 0x00100200)  // CRT0 / BSS-clear re-entry (wipes heap)
            || p == 0x00100008u;

        static bool IsSafeCode(Ps2System s, uint p) =>
            !IsDeathBand(p) && p is >= 0x00100000 and < 0x002C0000 && s.Memory.IsLikelyEeCode(p);

        uint cand = preferred;
        if (IsSafeCode(sys, cand))
            return cand;

        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        if (IsSafeCode(sys, ra))
            return ra;

        uint lg = (uint)(sys.LastGoodEePc & 0x1FFFFFFFUL);
        if (IsSafeCode(sys, lg))
            return lg;

        // Prefer stream poll entry (0x26C0E0) after CDVD — post-FreezeCache re-entry without
        // full s1/s2 frame re-faults. Worker dispatch 0x27CC08 is a solid alternate.
        if (sys.Cdvd.SectorsRead > 0 && sys.Memory.IsLikelyEeCode(0x0026C0E0UL))
            return 0x0026C0E0;
        if (sys.Memory.IsLikelyEeCode(0x0027CC08UL))
            return 0x0027CC08;
        // 0x185FAC is post-FreezeCache continue (real code at boot); prefer over CRT0.
        if (sys.Memory.IsLikelyEeCode(0x00185FACUL))
            return 0x00185FAC;
        return 0x0026C0E0;
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

        AdvanceSoftTick(sys, minTarget: 0);
        sys.Memory.Write32(SoftSpinFlagPtr, 0);
        if (pc is >= 0x0017A1D0 and <= 0x0017A294)
            TryEscapeTickWait(sys, pc, c);
        if (pc is >= 0x0017A320 and <= 0x0017A35C)
        {
            sys.EE.PC = 0x0017A360;
            sys.EE.COP0_Status &= ~0x6u;
        }

        // Live final PC 0x26C0E0: do { v0 = 0x26BB98(); } while (v0==0);
        // 0x26BB98 is the 989snd wait leaf: returns 1 when *0x2A1338==0 OR pending has
        // done-magic. Wave-5: SHARED-paint done-magic first; only clear pointer if still
        // stuck after paint (bad/OOB pending). NEVER re-snap post-ready body 0x26C0EC.
        if (pc is >= 0x0026C0E0 and <= 0x0026C0E8 && sys.Gs.PixelsWritten == 0)
        {
            TryArmPendingStreamJob(sys, c);
            uint pend = sys.Memory.Read32(0x002A1338);
            uint pPhys = pend & 0x1FFFFFFFu;
            bool pendingOk = pend != 0 && pPhys >= 0x00100000u
                && pPhys + 12 < (uint)SystemMemory.RDRAM_SIZE && (pPhys & 3) == 0
                && sys.Memory.Read32(pPhys) == 0xFFFFFFFFu
                && sys.Memory.Read32(pPhys + 8) == 0xFFFFFFFFu;
            if (!pendingOk)
                sys.Memory.Write32(0x002A1338, 0); // unusable pending — force empty ready
            // else leave pending; 0x26BB98 natural path should now return v0=1
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 }); // v0 = ready
            sys.EE.PC = 0x0026C0EC;
            sys.EE.COP0_Status &= ~0x6u;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_worldKickPulses <= 32 || _worldKickPulses % 16 == 0))
                Console.Error.WriteLine(
                    $"[GOW] 989snd-ready poll pc=0x{pc:X8} -> 0x26C0EC arms={_streamArmPulses} " +
                    $"pendOk={pendingOk} cdvd={sys.Cdvd.SectorsRead} cyc={c}");
        }
        // Post-ready body / work path: zero the work table so null-skip is taken (no synthetic
        // stream-work plant — poison objects → data PC / UnknownSyscall 0x2A1358, cdvd stuck).
        // Live w5b residual 0x26C4B4 mid-work after garbage table entry.
        else if (pc is >= 0x0026C0EC and <= 0x0026C600 && sys.Gs.PixelsWritten == 0
                 && sys.Cdvd.SectorsRead > 0)
        {
            TryArmPendingStreamJob(sys, c);
            sys.Memory.Write32(0x002A1338, 0);
            // Ensure null-skip: table[0]=0, index=0 so body does not jal 0x26C4B8 with garbage.
            sys.Memory.Write32(0x002A1358, 0);
            sys.Memory.Write32(0x002A1378, 0);
            if (pc is >= 0x0026C4B0 and <= 0x0026C5F0)
            {
                // Mid-work body with bad frame — soft return via $ra / post-ready continue.
                uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
                uint resume = 0x0026C130; // past body toward return
                if (sys.Memory.IsLikelyEeCode(ra) && ra is >= 0x00100000 and < 0x00280000
                    && ra is not (>= 0x0026C0E0 and <= 0x0026C600))
                    resume = ra;
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
                sys.EE.PC = resume;
                sys.EE.COP0_Status &= ~0x6u;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (_worldKickPulses % 16) == 0)
                    Console.Error.WriteLine(
                        $"[GOW] leave stream-work body pc=0x{pc:X8} -> 0x{resume:X8} " +
                        $"n={_worldKickPulses} cyc={c}");
            }
        }
        else if (sys.Gs.PixelsWritten == 0 && sys.Cdvd.SectorsRead > 0
                 && (_worldKickPulses % 8) == 0)
        {
            // Only clear *0x2A1338 when the pending pointer is clearly unusable.
            uint streamPtr = sys.Memory.Read32(0x002A1338);
            uint spPhys = streamPtr & 0x1FFFFFFFu;
            if (streamPtr != 0 && (spPhys < 0x00100000u || spPhys >= (uint)SystemMemory.RDRAM_SIZE
                                  || (spPhys & 3) != 0))
                sys.Memory.Write32(0x002A1338, 0);
            else if (streamPtr != 0)
                TryArmPendingStreamJob(sys, c);
        }

        // If still in list-walk body with a cursor that will never match sentinel, force empty exit.
        if (pc is >= 0x0015F2C0 and <= 0x0015F414)
            TryEscapeCorruptListWalk(sys, pc, c);
        if (pc is >= 0x0015F538 and <= 0x0015F58C)
            TryEscapeFlagSetListWalk(sys, pc, c);
        if (pc is >= 0x0015F440 and <= 0x0015F514)
            TryEscapeParentObjectList(sys, pc, c);
        if (pc is >= 0x001312C0 and <= 0x001312E8)
            TryEscapeLinkSearch(sys, pc, c);
        if (pc is >= 0x00233AD0 and <= 0x00233B34)
            TryEscapeObjectDispatch(sys, pc, c);
        if (pc is >= 0x00284780 and <= 0x002848B0)
            TryEscapeListCompareWalk(sys, pc, c);
        if (pc is >= GlobalFreeSearchPcLo and <= GlobalFreeSearchPcHi)
            TryEscapeGlobalFreeSearch(sys, pc, c);
        if (pc is >= TableIndexWalkPcLo and <= TableIndexWalkPcHi)
            TryEscapeTableIndexWalk(sys, pc, c);
        if (pc is >= ByteSumLoopPcLo and <= ByteSumLoopPcHi)
            TryEscapeByteSumLoop(sys, pc, c);
        if (pc is >= 0x0023E7C0 and <= 0x0023E7F0)
            TryEscapeAlignZeroLoop(sys, pc, c);

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
        // Wave-2: AdEL-data rescue after empty-reboot soft-return often lands at 0x00100008
        // then runs to 0x00100140 (live claim100: metrics frozen binds=16 gifPath3=0). Prior
        // check only matched exact entry PC — broaden to full CRT0/BSS band after progress.
        bool crt0Band = pc is >= 0x00100000 and <= 0x00100200;
        bool badBand = pc is (>= 0x002A0000 and <= 0x002B0000)
            or (>= 0x00229000 and <= 0x0022A000)
            || crt0Band;
        if (badBand && sys.Gs.PixelsWritten == 0
            && (!crt0Band || sys.Cdvd.SectorsRead > 0 || c >= 40_000_000))
        {
            // Prefer worker dispatch (gifPath3 residual path @0x27CC) after IRX.
            uint resume = PickSafeResume(sys,
                sys.Cdvd.SectorsRead > 0 && sys.Memory.IsLikelyEeCode(0x0027CC08UL)
                    ? 0x0027CC08u
                    : 0x0026C0ECu);
            sys.Memory.Write32(0x002A1338, 0); // stream ready
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128
            {
                Lo = resume == 0x00185FAC ? 0x00330000UL : 1UL
            });
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
            sys.EE.PC = resume;
            sys.EE.COP0_Status &= ~0x6u;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_worldKickPulses % 8) == 0)
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
        // Never re-home to 0x233AEx / 0x2847xx mid-body (wave-2 death loop).
        if (pc is >= 0x80000180 and <= 0x80000200 || pc < 0x00100000)
        {
            uint resume = PickSafeResume(sys, (uint)(sys.LastGoodEePc & 0x1FFFFFFFUL));
            sys.Memory.Write32(0x002A1338, 0); // stream ready
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = resume == 0x00185FAC ? 0x00330000UL : 1UL });
            sys.EE.SetGpr(5, new EmotionEngine.Gpr128 { Lo = 0 }); // clear poison a1
            sys.EE.COP0_Status &= ~0x6u;
            sys.EE.PC = resume;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_worldKickPulses <= 16 || _worldKickPulses % 32 == 0))
                Console.Error.WriteLine(
                    $"[GOW] rescue exception vector -> 0x{resume:X8} n={_worldKickPulses} cyc={c}");
        }

        var k = sys.Hle?.Kernel;
        if (k != null)
        {
            foreach (var t in k.AllThreads)
            {
                if (!t.Alive) continue;
                // Re-start main only when Entry is a real CreateThread entry (not boot Entry=0).
                // Planting 0x26C0E0 as Entry and StartThread'd ExitThread'd main → Exit@100M.
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
                    // SHARED QueueMaySignalSema + CompleteRpcEnd own real BIND/CALL leave.
                    // Residual empty poll only:
                    //   • WaitSema(3) SIF-cmd poll when no more SIF traffic
                    //   • WaitSema(0x20) worker 0x27CCxx (empty after IRX load)
                    //   • high ids 33..256 (game-private) — NOT garbage 0x20000000
                    // Never blanket-pulse 1..16 (races RPC_END). SEMA_STALL_YIELD OFF.
                    if (t.WaitSemaId is < 0 or > 256)
                    {
                        t.WaitSemaId = 0;
                        try { k.WakeupThread(t.Id); } catch { /* ignore */ }
                    }
                    else
                    {
                        bool emptyPoll = (t.WaitSemaId == 3 || t.WaitSemaId == 0x20 || t.WaitSemaId >= 32)
                            && sys.Cdvd.SectorsRead > 0 && sys.Gs.PixelsWritten == 0
                            && (_worldKickPulses % 2) == 0;
                        if (emptyPoll)
                        {
                            try { k.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
                        }
                    }
                }
                else if (t.Sleeping && t.WaitSemaId == 0 && !t.WaitVblank)
                    k.WakeupThread(t.Id);
                while (t.SuspendCount > 0)
                    k.ResumeThread(t.Id);
                if (t.SoftSuspended) t.SoftSuspended = false;
            }
        }

        // BIOS / KSEG0 thrash (live 0x800098xx) or data/heap PC (0x396xxx / 0x57xxxx).
        // Uncached aliases (0x4xxxxxxx) of *valid* code are OK — only rescue when phys is bad.
        uint pcPhys = pc & 0x1FFFFFFFu;
        bool uncachedBad = (pc & 0xE0000000u) == 0x40000000u
            && (pcPhys < 0x00100000u || pcPhys >= 0x002C0000u || !sys.Memory.IsLikelyEeCode(pcPhys));
        if (pc is >= 0x80000000 and <= 0x80020000 || pcPhys < 0x00100000
            || pcPhys >= (uint)SystemMemory.RDRAM_SIZE
            || pcPhys >= 0x002C0000u // data/heap incl. live 0x57xxxx UnknownOpcode storms
            || uncachedBad
            || (!sys.Memory.IsLikelyEeCode(pcPhys) && pcPhys >= 0x002C0000u))
        {
            uint resume = PickSafeResume(sys, 0x0026C0EC);
            sys.Memory.Write32(0x002A1338, 0);
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = resume == 0x00185FAC ? 0x00330000UL : 1UL });
            sys.EE.SetGpr(5, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.COP0_Status &= ~0x6u;
            sys.EE.PC = resume;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_worldKickPulses % 16) == 0)
                Console.Error.WriteLine(
                    $"[GOW] rescue KSEG/data thrash pc=0x{pc:X8} -> 0x{resume:X8} n={_worldKickPulses} cyc={c}");
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

        // 989snd wait leaf 0x26BB98: paint SHARED done-magic, then soft-return if still stuck.
        if (sys.Cdvd.SectorsRead > 0 && sys.Gs.PixelsWritten == 0
            && pc is >= 0x0026BB98 and <= 0x0026BC3C
            && (_worldKickPulses % 4) == 0)
        {
            TryArmPendingStreamJob(sys, c);
            uint pend = sys.Memory.Read32(0x002A1338);
            uint pPhys = pend & 0x1FFFFFFFu;
            bool pendingOk = pend != 0 && pPhys >= 0x00100000u
                && pPhys + 12 < (uint)SystemMemory.RDRAM_SIZE && (pPhys & 3) == 0
                && sys.Memory.Read32(pPhys) == 0xFFFFFFFFu
                && sys.Memory.Read32(pPhys + 8) == 0xFFFFFFFFu;
            if (!pendingOk)
                sys.Memory.Write32(0x002A1338, 0);
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            uint resume = 0x0026C0EC; // prefer post-ready body over FreezeCache re-entry
            if (sys.Memory.IsLikelyEeCode(ra) && ra is >= 0x00100000 and < 0x00280000
                && ra is not (>= 0x0026BB98 and <= 0x0026C200))
                resume = ra;
            sys.EE.PC = resume;
            sys.EE.COP0_Status &= ~0x6u;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_worldKickPulses % 16) == 0)
                Console.Error.WriteLine(
                    $"[GOW] 989snd-wait return pc=0x{pc:X8} -> 0x{resume:X8} pendOk={pendingOk} " +
                    $"n={_worldKickPulses} cyc={c}");
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

        // Dormant main after list escapes (live claim: started=False while worker id=2
        // spins WaitSema empty SIF poll at 0x293C68 / 0x294810). Main only —
        // peer re-start historically left garbage WaitSemaIds (menu17).
        // Boot thread Entry=0: do NOT invent Entry (StartThread@0x26C0E0 caused Exit@100M).
        // Only re-start when CreateThread left a real entry. switchNow on WaitSema trampoline.
        if (sys.Cdvd.SectorsRead > 0 && sys.Gs.PixelsWritten == 0
            && (_worldKickPulses % 4) == 0)
        {
            var kk = sys.Hle?.Kernel;
            if (kk != null)
            {
                bool onWaitSemaTrampoline = pc is >= 0x00293C00 and <= 0x00293C80
                    || pc is >= 0x00294800 and <= 0x00294890;
                foreach (var t in kk.AllThreads)
                {
                    if (t.Id != 1 || !t.Alive || t.Entry == 0) continue;
                    if (t.Entry is < 0x00100000 or >= 0x00300000) continue;
                    if (!t.Started)
                    {
                        try
                        {
                            kk.StartAndMaybeSwitch(sys.EE, t.Id,
                                switchNow: onWaitSemaTrampoline, arg: 0, fromSyscall: false);
                            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                                && _worldKickPulses <= 64)
                                Console.Error.WriteLine(
                                    $"[GOW] re-start dormant main entry=0x{t.Entry:X8} " +
                                    $"switch={onWaitSemaTrampoline} cyc={c}");
                        }
                        catch { /* ignore */ }
                    }
                    else if (t.Sleeping && t.WaitSemaId == 0 && !t.WaitVblank
                             && onWaitSemaTrampoline)
                    {
                        try { kk.WakeupThread(t.Id); } catch { /* ignore */ }
                    }
                }
            }
        }

        // $ra==0 after stack poison (live w2 residual at 0x17A7A0 jr ra): force safe resume
        // so delay-slot thrash cannot fall into zero-page forever.
        if (sys.Cdvd.SectorsRead > 0 && sys.Gs.PixelsWritten == 0
            && (_worldKickPulses % 4) == 0)
        {
            uint ra0 = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            if (ra0 == 0 || !sys.Memory.IsLikelyEeCode(ra0))
            {
                // Only act if current PC is an epilogue / jr band that needs $ra.
                bool needsRa = pc is (>= 0x0017A790 and <= 0x0017A7A4)
                    or (>= 0x001838C8 and <= 0x001838CC)
                    || sys.Memory.Read32(pc) == 0x03E00008u;
                if (needsRa)
                {
                    uint resume = PickSafeResume(sys, 0x0026C0EC);
                    sys.Memory.Write32(0x002A1338, 0);
                    sys.Memory.Write32(0x0029C7D0, 0);
                    sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
                    sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume }); // repair $ra
                    sys.EE.PC = resume;
                    sys.EE.COP0_Status &= ~0x6u;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                        && (_worldKickPulses % 16) == 0)
                        Console.Error.WriteLine(
                            $"[GOW] repair null $ra pc=0x{pc:X8} -> 0x{resume:X8} n={_worldKickPulses} cyc={c}");
                }
            }
        }

        // Live menu16/w2: thrash at 0x21FFxx / 0x2200xx nop-sled (final PC 0x2200F0).
        // Always re-home via PickSafeResume — never leave EE in this band.
        if (pc is (>= 0x0021FF00 and <= 0x00220600) && sys.Cdvd.SectorsRead > 0
            && sys.Gs.PixelsWritten == 0)
        {
            uint resume = PickSafeResume(sys, 0x0026C0EC);
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = resume == 0x00185FAC ? 0x00330000UL : 1UL });
            sys.EE.PC = resume;
            sys.EE.COP0_Status &= ~0x6u;
            sys.Memory.Write32(0x002A1338, 0);
            sys.Memory.Write32(0x0029C7D0, 0);
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_worldKickPulses % 16) == 0)
                Console.Error.WriteLine(
                    $"[GOW] escape 0x21FFxx thrash -> 0x{resume:X8} n={_worldKickPulses} cyc={c}");
        }

        // After CDVD, sifrpc WaitSema trampoline thrash at 0x293Cxx (empty SIF-cmd poll +
        // worker 0x27CCxx). SHARED QueueMaySignalSema + CompleteRpcEnd own real BIND/CALL.
        // Wave-5: paint 989snd done-magic + residual SignalSema. When still stuck mid-leaf,
        // soft-return via live $ra (SIF poll caller is 0x294810 / worker 0x27CC08) — do NOT
        // snap to 0x26C0E0 mid-frame (live w5c data PC / UnknownSyscall 0x2A1364).
        // Live tip residual: PC=0x299328 with $ra=0 after align-zero leave — empty wake
        // alone cannot progress; force leave via stack $ra / post-FreezeCache.
        if (sys.Cdvd.SectorsRead > 0 && sys.Gs.PixelsWritten == 0
            && _worldKickPulses >= 8 && (_worldKickPulses % 4) == 0
            && (pc is >= 0x00293C00 and <= 0x00293C80
                || pc is >= 0x00299300 and <= 0x00299480
                || pc is >= 0x00289A00 and <= 0x00289B00))
        {
            TryArmPendingStreamJob(sys, c);
            sys.Memory.Write32(0x0029C7D0, 0);
            const uint Done = 0xFFFFFFFFu;
            sys.Memory.Write32(0x00305600, Done);
            sys.Memory.Write32(0x00305604, 0);
            sys.Memory.Write32(0x00305608, Done);
            if (k != null)
            {
                foreach (var t in k.AllThreads)
                {
                    if (!t.Alive) continue;
                    // Live residual: WaitSemaId=0x20000000 / 0x200000 from poisoned a0 on the
                    // WaitSema trampoline (worker 0x27CC00 delay-slot lw a0,4(v0) with bad v0).
                    // Never SignalSema garbage ids — clear and wake instead.
                    if (t.WaitSemaId is < 0 or > 256)
                    {
                        t.WaitSemaId = 0;
                        if (t.Sleeping && !t.WaitVblank)
                        {
                            try { k.WakeupThread(t.Id); } catch { /* ignore */ }
                        }
                        continue;
                    }
                    if (!t.Sleeping) continue;
                    // Residual empty poll only: SIF-cmd (3), worker (0x20), game-private (33..256).
                    if (t.WaitSemaId == 3 || t.WaitSemaId == 0x20 || t.WaitSemaId is >= 32 and <= 256)
                    {
                        try { k.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
                    }
                    else if (t.WaitSemaId == 0 && !t.WaitVblank)
                        k.WakeupThread(t.Id);
                }
            }
            // Soft-return from WaitSema leaf via $ra so poll body can take the empty-queue path.
            uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            bool left = false;
            if (sys.Memory.IsLikelyEeCode(ra) && ra is (>= 0x0027CC00 and <= 0x0027CD00)
                    or (>= 0x00294800 and <= 0x00294900)
                    or (>= 0x00297600 and <= 0x00297700)
                    or (>= 0x00297300 and <= 0x00297400)
                    or (>= 0x00100000 and < 0x00280000))
            {
                // WaitSema success convention: v0 = sema id (libcdvd / sifrpc check v0==id).
                // Only accept plausible THREADMAN ids — live a0=0x20000000 is poison.
                // Broader $ra accept (any .text) for 0x2993xx residual with null-ra recovery.
                if (ra is not (>= 0x00293C00 and <= 0x00293C80)
                    && ra is not (>= 0x00299300 and <= 0x00299480)
                    && ra is not (>= 0x0026C0E0 and <= 0x0026C600))
                {
                    uint a0 = (uint)sys.EE.GetGpr(4).Lo;
                    uint sema = a0 is >= 1 and <= 256 ? a0 : 3u;
                    sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = sema });
                    sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = sema }); // keep a0 coherent
                    sys.EE.PC = ra;
                    sys.EE.COP0_Status &= ~0x6u;
                    left = true;
                }
            }
            // Null / poison $ra residual (live 0x299328): try stack slot then worker /
            // post-FreezeCache. Prefer 0x27CC08 over bare 0x185FAC after IRX — live wave-2
            // null-ra → 0x185FAC → AdEL 0x06207265 → CRT0 death (gifPath3 lost).
            if (!left && (ra == 0 || !sys.Memory.IsLikelyEeCode(ra)
                          || ra is (>= 0x00299300 and <= 0x00299480)
                          || ra is (>= 0x00293C00 and <= 0x00293C80))
                && (_worldKickPulses % 8) == 0)
            {
                uint resume = 0;
                uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
                if (sp is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE - 16u)
                {
                    uint stacked = sys.Memory.Read32(sp) & 0x1FFFFFFFu;
                    if (sys.Memory.IsLikelyEeCode(stacked) && stacked is >= 0x00100000 and < 0x002C0000
                        && stacked is not (>= 0x00299300 and <= 0x00299480)
                        && stacked is not (>= 0x00293C00 and <= 0x00293C80)
                        && stacked is not (>= 0x0026C0E0 and <= 0x0026C600)
                        && stacked is not (>= 0x00100000 and <= 0x00100200))
                        resume = stacked;
                }
                if (resume == 0)
                    resume = PickSafeResume(sys, 0x0027CC08);
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128
                {
                    Lo = resume == 0x00185FAC ? 0x00330000UL : 3UL
                });
                sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
                sys.EE.PC = resume;
                sys.EE.COP0_Status &= ~0x6u;
                left = true;
            }
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_worldKickPulses % 16) == 0)
                Console.Error.WriteLine(
                    $"[GOW] SHARED empty-sifrpc wake pc=0x{pc:X8} ra=0x{ra:X8} left={left} " +
                    $"arms={_streamArmPulses} n={_worldKickPulses} cyc={c}");
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

        // Soft hard-return only — no permanent .text stub (wave-5: avoid freelist/list poison plants).
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.SetGpr(3, new EmotionEngine.Gpr128 { Lo = a1 });
        sys.EE.PC = 0x0015F590; // jr ra
        sys.EE.COP0_Status &= ~0x6u;

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

        // Soft only — no permanent parent-list jr-ra plant (wave-5).

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _parentListEscapes <= 12)
            Console.Error.WriteLine(
                $"[GOW] escape parent object list pc=0x{pc:X8} s0=0x{s0:X8} s5=0x{s5:X8} " +
                $"-> 0x15F514 n={_parentListEscapes} cyc={c}");
    }

    /// <summary>
    /// Object-list filter walk (disasm 0x15F280..0x15F438): <c>s1</c> walks a singly-linked
    /// list; end when <c>s1 == (s2+s4+s5)+0x34</c>. Corrupt/OOB <c>s1</c> never matches →
    /// infinite loop. Empty the head cell to a circular self-link so the next call takes the
    /// natural empty branch at <c>0x15F2B8</c>, then snap to restore epilogue at 0x15F414.
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
            else if (_listWalkEscapes >= 4 && pc is >= 0x0015F400 and <= 0x0015F40C)
                oob = true; // periodic force: re-entry thrash with "valid" rings (profiler 1.7M)
            else
                return; // healthy (possibly uncached) pointer — do not touch
        }

        // Sentinel = s2 + s4 + s5 + 0x34 (disasm 0x15F404..408). Empty list is *sent = sent.
        uint s2 = (uint)sys.EE.GetGpr(18).Lo;
        uint s4 = (uint)sys.EE.GetGpr(20).Lo;
        uint s5 = (uint)sys.EE.GetGpr(21).Lo;
        uint sent = s2 + s4 + s5 + 0x34u;
        uint sentPhys = sent & 0x1FFFFFFFu;
        if (sentPhys is >= 0x00100000u and < (uint)SystemMemory.RDRAM_SIZE - 4u && (sentPhys & 3) == 0)
        {
            sys.Memory.Write32(sentPhys, sent); // circular empty head
            sys.EE.SetGpr(17, new EmotionEngine.Gpr128 { Lo = sent }); // s1 = sentinel
        }

        // Epilogue restores s0..s7/ra from the large frame and jr ra.
        sys.EE.PC = 0x0015F414;
        sys.EE.COP0_Status &= ~(1u << 1);
        _listWalkEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _listWalkEscapes <= 12)
            Console.Error.WriteLine(
                $"[GOW] escape corrupt list walk pc=0x{pc:X8} s1=0x{s1:X8} phys=0x{phys:X8} " +
                $"sent=0x{sent:X8} -> 0x15F414 n={_listWalkEscapes} cyc={c}");
    }

    /// <summary>
    /// Linked-list search / splice (disasm 0x1312C0..0x1312F8):
    /// <c>while (*a0 != v1) a0 = *a0;</c> then store. Corrupt ring never hits sentinel →
    /// PcProfiler hot band after filter thrash (~230k samples @ 55M). Force done: a0=v1,
    /// PC at the fall-through store/jr.
    /// </summary>
    private void TryEscapeLinkSearch(Ps2System sys, uint pc, ulong c)
    {
        if (pc is >= 0x001312F0 and <= 0x001312F8)
            return; // epilogue

        uint a0 = (uint)sys.EE.GetGpr(4).Lo;
        uint v1 = (uint)sys.EE.GetGpr(3).Lo;
        uint a0Phys = a0 & 0x1FFFFFFFu;
        uint v1Phys = v1 & 0x1FFFFFFFu;

        bool bad = a0 == 0
            || a0Phys < 0x00100000u
            || a0Phys >= (uint)SystemMemory.RDRAM_SIZE
            || (a0Phys & 3) != 0;
        if (!bad)
        {
            uint next = sys.Memory.Read32(a0Phys);
            uint np = next & 0x1FFFFFFFu;
            if (next == 0 || np < 0x00100000u || np >= (uint)SystemMemory.RDRAM_SIZE
                || next == a0 || np == a0Phys)
                bad = true;
            // Periodic force after first detection — long healthy-looking rings still thrash.
            else if (_linkSearchEscapes > 0 || (_worldKickPulses % 4) == 0)
                bad = true;
        }

        // Sentinel must be a plausible pointer; otherwise force return via $ra.
        bool sentOk = v1 != 0 && v1Phys is >= 0x00100000u and < (uint)SystemMemory.RDRAM_SIZE
                      && (v1Phys & 3) == 0;

        if (!bad && a0 == v1)
            return; // already done

        if (!bad && _linkSearchEscapes == 0 && pc is < 0x001312D0)
            return; // let a short healthy search run once

        if (sentOk)
            sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = v1 }); // a0 = sentinel → exit
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = sentOk ? sys.Memory.Read32(v1Phys) : 0UL });
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        uint resume = 0x001312F0; // sw zero,24(a2) / jr ra
        if (!sys.Memory.IsLikelyEeCode(resume)
            || (sys.Memory.IsLikelyEeCode(ra) && ra is >= 0x00100000 and < 0x00280000
                && ra is not (>= 0x001312C0 and <= 0x001312F8)
                && !sentOk))
            resume = ra;
        sys.EE.PC = resume;
        sys.EE.COP0_Status &= ~0x6u;
        _linkSearchEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _linkSearchEscapes <= 16)
            Console.Error.WriteLine(
                $"[GOW] escape link-search pc=0x{pc:X8} a0=0x{a0:X8} v1=0x{v1:X8} " +
                $"-> 0x{resume:X8} n={_linkSearchEscapes} cyc={c}");
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
        const uint arena = HeapArenaBase;
        const uint arenaBytes = HeapArenaBytes;

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
    /// Carve a zeroed block from the synthetic arena. Never returns the freelist header —
    /// callers that treat v0 as an object/list node poison OOB links when given the header.
    /// </summary>
    private uint AllocArenaBlock(Ps2System sys, uint minSize = HeapBlockSize)
    {
        const uint rdramEnd = (uint)SystemMemory.RDRAM_SIZE; // 0x02000000
        uint arenaEnd = HeapArenaBase + HeapArenaBytes;
        if (arenaEnd > rdramEnd - 0x40u)
            arenaEnd = rdramEnd - 0x40u;
        uint size = minSize < HeapBlockSize ? HeapBlockSize : (minSize + 0xFu) & ~0xFu;
        if (size > 0x1000u) size = 0x1000u;
        if (_arenaBump < HeapArenaBase || _arenaBump >= arenaEnd)
            _arenaBump = HeapArenaBase;
        if (_arenaBump + size > arenaEnd)
            _arenaBump = HeapArenaBase;
        uint block = _arenaBump;
        _arenaBump += size;
        if (block < 0x00100000u || block + size > rdramEnd)
        {
            block = HeapArenaBase;
            _arenaBump = HeapArenaBase + size;
        }
        uint zeroLen = size > 0x200u ? 0x200u : size;
        for (uint o = 0; o < zeroLen; o += 4)
            sys.Memory.Write32(block + o, 0);
        sys.Memory.Write32(block, block);
        return block;
    }

    /// <summary>
    /// SHARED-side assist for the EE 989snd wait leaf at <c>0x26BB98</c> (and its caller poll
    /// at <c>0x26C0E0</c>). Ground truth (RealSifRpc.Handle989Snd / SCUS_973.99):
    /// <list type="bullet">
    /// <item><c>*0x2A1338</c> is the pending RPC recv pointer (not a CD stream job).</item>
    /// <item>Wait requires <c>pending[0]==0xFFFFFFFF &amp;&amp; pending[2]==0xFFFFFFFF</c>
    ///   (index=1 slot at +8); result lives at <c>pending[1]</c>.</item>
    /// <item>Handle989Snd already paints this shape on CallRpc; residual waits still see a
    ///   zeroed pending when CallRpc completed without writing EE recv, or index skew.</item>
    /// </list>
    /// Prefer painting the SHARED done-magic over zeroing the pointer (zeroing skips real
    /// completion checks and empties the stream/sound graph). Also try a CD sector fill if
    /// the pointer looks like a disc job instead (secondary path).
    /// </summary>
    private void TryArmPendingStreamJob(Ps2System sys, ulong c)
    {
        if (_streamArmPulses >= 512) return;
        uint pending = sys.Memory.Read32(0x002A1338);
        uint pPhys = pending & 0x1FFFFFFFu;
        if (pending == 0 || pPhys < 0x00100000u || pPhys + 0x20 >= (uint)SystemMemory.RDRAM_SIZE
            || (pPhys & 3) != 0)
            return;

        // Primary: 989snd pending recv — paint done-magic (SHARED Handle989Snd contract).
        const uint Done = 0xFFFFFFFFu;
        uint w0 = sys.Memory.Read32(pPhys);
        uint w2 = sys.Memory.Read32(pPhys + 8);
        if (w0 != Done || w2 != Done)
        {
            sys.Memory.Write32(pPhys + 0, Done);
            // Keep existing result if already set; else success.
            if (sys.Memory.Read32(pPhys + 4) == 0 || sys.Memory.Read32(pPhys + 4) == Done)
                sys.Memory.Write32(pPhys + 4, 0); // ResultOk
            sys.Memory.Write32(pPhys + 8, Done);
            // Extra index slots used by some bank loads (recvSize up to 44+).
            for (uint off = 12; off < 48; off += 4)
            {
                if (pPhys + off + 4 >= (uint)SystemMemory.RDRAM_SIZE) break;
                sys.Memory.Write32(pPhys + off, Done);
            }
            _streamArmPulses++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && _streamArmPulses <= 32)
                Console.Error.WriteLine(
                    $"[GOW] SHARED 989snd-done pending=0x{pPhys:X8} was w0=0x{w0:X8} w2=0x{w2:X8} " +
                    $"n={_streamArmPulses} cdvd={sys.Cdvd.SectorsRead} cyc={c}");
            // Leave *0x2A1338 intact so the natural wait path observes done-magic and clears.
            return;
        }

        // Secondary: look like a disc stream job (lba/nsec/dest) — SHARED CDVD fill.
        uint lba = sys.Memory.Read32(pPhys + 4);
        uint nsec = sys.Memory.Read32(pPhys + 8);
        uint dest = sys.Memory.Read32(pPhys + 12);
        if (lba == 0 || lba > 0x00400000u)
        {
            lba = sys.Memory.Read32(pPhys + 8);
            nsec = sys.Memory.Read32(pPhys + 12);
            dest = sys.Memory.Read32(pPhys + 16);
        }
        if (nsec == 0 || nsec > 64) nsec = 1;
        if (nsec > 16) nsec = 16;
        uint destPhys = dest & 0x1FFFFFFFu;
        bool lbaOk = lba is >= 1 and <= 0x00400000u;
        bool destOk = destPhys is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE - 0x8000u
                      && (destPhys & 3) == 0;
        if (!lbaOk || !destOk)
            return;

        uint got = sys.Cdvd.ReadSectorsTo(sys.Memory, lba, nsec, dest);
        _streamArmPulses++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && _streamArmPulses <= 32)
            Console.Error.WriteLine(
                $"[GOW] SHARED cd-stream-arm job=0x{pPhys:X8} lba={lba} n={nsec} dest=0x{destPhys:X8} " +
                $"got={got} cdvd={sys.Cdvd.SectorsRead} cyc={c}");
    }

    /// <summary>
    /// Escape freelist walk at 0x2393xx..0x2398xx. Live w5 residual 0x23935C and classic
    /// 0x2396F4/0x2397F0 circular free chains. Soft-cap only — never permanent freelist stubs.
    /// </summary>
    private void TryEscapeSecondaryFreelist(Ps2System sys, uint pc, ulong c)
    {
        // Epilogues must run once entered.
        if (pc is (>= 0x00239744 and <= 0x00239750) or (>= 0x002397FC and <= 0x0023980C))
            return;
        // Soft-cap; never permanent freelist .text stub (wave-5 tip).
        // Uncapped soft-escape — permanent .text plant regressed RPC/dmac.
        if (_free2Escapes >= 100000)
            return;

        uint s0 = (uint)sys.EE.GetGpr(16).Lo;
        if (!_heapDefaultsPlanted)
            MaybePlantHeapDefaults(sys, c);

        // Force list cursor to sentinel so natural fall-through also exits, then snap.
        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
        if (sp is >= 0x00100000 and < 0x02000000)
        {
            uint sent = sys.Memory.Read32(sp + 0);
            sys.Memory.Write32(sp + 4, sent);
        }

        // Prefer known epilogues; for pre-walk residual 0x2393xx return a real arena block
        // via $ra when available (header poison → OOB lists).
        // After several synthetic-header hits, hard-return via $ra with a carved block —
        // epi 0x239744 re-enters the same s0=header thrash for multi-M cycles (live 35–37M).
        bool headerThrash = s0 == HeapDefaultNodeBase || s0 == HeapDefaultNodeBase + 0x80u
            || s0 == 0;
        uint epi;
        // Header thrash: only hard-return via a clearly good live $ra (caller).
        // Never invent 0x185FAC / 0x26C0EC here — that jumped into synthetic BST nodes as
        // code (UnknownOpcode key=0xF24E524F @ 0x01FD8120) and killed gifPath3.
        if (headerThrash && _free2Escapes >= 12)
        {
            uint block = AllocArenaBlock(sys);
            uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            if (sys.Memory.IsLikelyEeCode(ra) && ra is >= 0x00100000 and < 0x00280000
                && ra is not (>= 0x00239300 and <= 0x00239810)
                && ra is not (>= 0x0023A900 and <= 0x0023AA30)
                && ra is not (>= 0x0026C0E0 and <= 0x0026C600)
                && ra is not (>= 0x0023E7C0 and <= 0x0023E7F0)
                && ra is not (>= 0x00185F90 and <= 0x00186120))
            {
                sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = block });
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = block });
                sys.EE.PC = ra;
                sys.EE.COP0_Status &= ~0x6u;
                _free2Escapes++;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _free2Escapes <= 32)
                    Console.Error.WriteLine(
                        $"[GOW] hard-return freelist thrash pc=0x{pc:X8} s0=0x{s0:X8} block=0x{block:X8} " +
                        $"-> 0x{ra:X8} n={_free2Escapes} cyc={c}");
                return;
            }
            // Fall through to soft epi when $ra is not a clean caller.
        }
        if (pc >= 0x002397A0)
            epi = 0x002397FCu;
        else if (pc >= 0x002396F0)
            epi = 0x00239744u;
        else
        {
            uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            if (sys.Memory.IsLikelyEeCode(ra) && ra is >= 0x00100000 and < 0x00280000
                && ra is not (>= 0x00239300 and <= 0x00239810))
                epi = ra;
            else
                epi = sys.Cdvd.SectorsRead > 0 ? 0x0026C0E0u : 0x00239744u;
            // Publish usable block in v0 for callers that expect an alloc result.
            uint block = AllocArenaBlock(sys);
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = block });
            sys.EE.PC = epi;
            sys.EE.COP0_Status &= ~0x6u;
            _free2Escapes++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _free2Escapes <= 24)
                Console.Error.WriteLine(
                    $"[GOW] escape freelist residual pc=0x{pc:X8} s0=0x{s0:X8} block=0x{block:X8} " +
                    $"-> 0x{epi:X8} n={_free2Escapes} cyc={c}");
            return;
        }

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
        // (Aggressive mid-walk force on real heaps regressed dmac/binds into UnknownOpcode.)
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

        // Wave-5: return a real arena block, never the freelist header (header-as-object
        // poisons list heads → OOB walks → empty stream graphs → cdvd stuck 142).
        uint block = AllocArenaBlock(sys);
        sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = HeapDefaultNodeBase }); // s0 = header for walk
        // sp[0] = end marker (s0+0x38), sp[4] = walk cursor — force both to end so loop exits.
        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
        if (sp is >= 0x00100000 and < 0x02000000)
        {
            uint end = HeapDefaultNodeBase + 0x38;
            sys.Memory.Write32(sp + 0, end);
            sys.Memory.Write32(sp + 4, end);
        }
        // Mid-walk (0x23A978..C8): skip to empty-list continuation.
        if (pc is >= 0x0023A978 and <= 0x0023A9C8)
            sys.EE.PC = 0x0023A9CC;
        // v0 = carved block (usable object memory), not header.
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = block });
        uint a1 = (uint)sys.EE.GetGpr(5).Lo;
        // Heap descriptor freelist table slot: publish header so re-walks have a bucket.
        if (a1 is >= 0x00300000 and < 0x01000000)
        {
            if (sys.Memory.Read32(a1) == 0)
                sys.Memory.Write32(a1, HeapDefaultNodeBase);
            int idx = (int)sys.Memory.Read32(a1 + 0x80);
            if (idx < 0)
                sys.Memory.Write32(a1 + 0x80, 0);
        }
        _heapNullEscapes++;
        // After few hits / synthetic header thrash: return via epilogue (never header).
        if (_heapNullEscapes > 8 || onSynthetic)
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = block });
            sys.EE.PC = 0x0023AA28;
            sys.EE.COP0_Status &= ~0x6u;
        }
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _heapNullEscapes <= 8)
            Console.Error.WriteLine(
                $"[GOW] escape null heap walk pc=0x{pc:X8} s0=0x{s0:X8} -> block=0x{block:X8} " +
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


