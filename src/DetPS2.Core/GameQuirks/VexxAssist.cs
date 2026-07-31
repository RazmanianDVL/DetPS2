using System;
using System.Collections.Generic;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Vexx (USA) SLUS_203.83 — IOPRP252 + null-path basename + CRT/string heap plant +
/// SearchFile 0x128 path-layout (+0x24) + freelist bump escape + STREE0 re-plant.
///
/// Wave-1 residual: GAME.TXT SearchFile+CdRead (cdvd=4). Wave-2: STREE0.TRE SearchFile ok
/// (lsn/size ~1GB). Wave-3: hang was null CD I/O vtable @0x3BD3A8 (install never ran) →
/// STREE open fails → hash-map walk thrash @0x1DD2E0 with table=null. Plant game default
/// open/read stubs (partial TRE stream, not full 1GB map); expand freelist/bump for TOC
/// (~4.6MB header, not full TRE); escape null-table walk.
///
/// Wave-4: retail open prefixes <c>host:</c> then FILEIO RPC — bind never appears after
/// SearchFile (empty SIFCMD cid=0 thrash), so STREE TOC never CdReads (cdvd=0). Host-serve
/// CD I/O open/read/seek/tell/size/close against the mounted ISO (real sector stream for
/// TRE TOC / GAME.TXT); strip <c>$/</c> virtual root. Soft-GS residual. See issue #19.
///
/// Wave-5: stream-map open at 0x1DCEB0 loads the CD I/O vtable from <c>0x3AD3A8</c>
/// (lui at,0x3B + lw -0x2C58), NOT the 0x3BD3A8 plant target used in waves 3–4. Correct
/// base so host open/read runs: first u32 (entry count) → malloc(count×24) table → full
/// index CdRead-equivalent host read → Soft-GS residual after assets bind.
/// </summary>
public sealed class VexxAssist : IGameQuirkModule
{
    public string Serial => "SLUS_203.83";
    public string DisplayName => "Vexx (USA)";

    public const uint IopVersionCellA = 0x003D18B8;
    public const uint IopVersionCellB = 0x003D1938;
    public const uint PathBasenameA = 0x00146170;
    public const uint PathBasenameB = 0x00146230;
    public const uint StubA = 0x00090000;
    public const uint StubB = 0x00090040;
    public const uint CrtMallocSlot = 0x003BCD00;
    public const uint CrtFreeSlot = 0x003BCD04;
    public const uint CrtReallocSlot = 0x003BCD08;
    public const uint StringAllocHook = 0x00444998;
    public const uint StringFreeHook = 0x004449A0;
    public const uint SmallPoolRoot = 0x003F71B0;
    public const uint MallocStub = 0x00090100;
    public const uint FreeStub = 0x00090140;
    public const uint ReallocStub = 0x00090160;
    public const uint BumpCursorCell = 0x00090180;
    public const uint BumpArenaBase = 0x01800000;
    /// <summary>16 MiB bump — STREE0 TOC (~4.6MB) + stream tables + GAME.TXT headroom; never full 1GB TRE.</summary>
    public const uint BumpArenaEnd = 0x02800000;
    /// <summary>Freelist host-bump cap (partial TRE header / stream tables, not full map).</summary>
    public const uint FreelistMaxBump = 0x00A00000;
    public const uint PathNormalizeLoop = 0x00372ABC;
    public const uint PathNormalizeAfterLoop = 0x00372B04;
    public const uint EmptyStringSentinel = 0x003C4C58;
    public const uint FreelistWalkLo = 0x001CE190;
    public const uint FreelistWalkHi = 0x001CE210;
    public const uint FreelistSuccessStore = 0x001CE280;
    public const uint SearchFileArgBuf = 0x1C1F4000;

    /// <summary>
    /// CD file-backend vtable the stream open path loads (EE 0x1DCEFC: lui at,0x3B;
    /// lw open=-0x2C58 → <c>0x3AD3A8</c>). Wave 3–4 wrongly planted <c>0x3BD3A8</c> (never
    /// read) while retail defaults live here — open returned through host:+FILEIO (fail) so
    /// STREE stream map table at obj+8 stayed null. Defaults: 0x1D0CE0 open, 0x1D0CA0 read.
    /// </summary>
    public const uint CdIoVtableBase = 0x003AD3A8;
    public const uint CdIoDefaultOpen = 0x001D0CE0;
    public const uint CdIoDefaultClose = 0x001D0C40;
    public const uint CdIoDefaultRead = 0x001D0CA0;
    public const uint CdIoDefaultWrite = 0x001D0CB0;
    public const uint CdIoDefaultStub0 = 0x001D0CC0;
    public const uint CdIoDefaultSeek = 0x001D0E60;
    public const uint CdIoDefaultTell = 0x001D0CD0;
    public const uint CdIoDefaultSize = 0x001D0ED0;
    public const uint CdIoDefaultMisc = 0x001D0F40;

    /// <summary>
    /// Host-serve CD I/O stubs (spin until Step fulfills). Vtable points here so open/read
    /// cannot race past a single-instruction PC sample (wave-4).
    /// Layout: open, close, read, write, seek, tell, size — 0x20 bytes each (spin + nops).
    /// Must live at ≥0x00100000 — <see cref="KernelBootstrap.RescueIfLostInLowMem"/> treats
    /// PC below 1MiB as lost and re-homes before ActiveQuirk.Step can service the spin
    /// (wave-5: stubs at 0x90200 never ran; stream open hung).
    /// </summary>
    public const uint HostCdStubBase = 0x00F00000;
    public const uint HostCdStubOpen = HostCdStubBase + 0x00;
    public const uint HostCdStubClose = HostCdStubBase + 0x20;
    public const uint HostCdStubRead = HostCdStubBase + 0x40;
    public const uint HostCdStubWrite = HostCdStubBase + 0x60;
    public const uint HostCdStubSeek = HostCdStubBase + 0x80;
    public const uint HostCdStubTell = HostCdStubBase + 0xA0;
    public const uint HostCdStubSize = HostCdStubBase + 0xC0;
    public const uint HostCdStubEnd = HostCdStubBase + 0xE0;

    /// <summary>Hash-map lookup thrash when stream table at s5+8 is null (PC 0x1DD2E0).</summary>
    public const uint StreamMapLookupLo = 0x001DD2C0;
    public const uint StreamMapLookupHi = 0x001DD370;
    public const uint StreamMapLookupFail = 0x001DD370;

    /// <summary>Allow freelist bump after CRT plant settles (not during whip-era thrash).</summary>
    public const ulong FreelistEscapeMinCycles = 1_000_000UL;

    /// <summary>Cap single host read (TOC / stream tables — never full 1GB TRE).</summary>
    public const uint HostReadMaxBytes = 0x00800000;

    private bool _pathPatched, _mallocPlanted, _cdIoPlanted;
    private int _versionReplants, _nullPathEscapes, _pathNormEscapes, _mallocReplants;
    private int _hookReplants, _freelistEscapes, _searchPathFixes, _searchPlants;
    private int _stackRescues, _cdIoReplants, _streamMapEscapes;
    private int _hostOpens, _hostReads, _hostCloses, _hostSeeks;
    private int _streamMapProbes, _streamMapPlants;
    private int _streamMapLookupHits;
    private bool _tocProbeDone;
    private Iso9660.Volume? _isoVol;
    private string? _isoVolPath;
    /// <summary>Game 1-based handle → IopModules FILEIO fd (0-based).</summary>
    private readonly Dictionary<int, int> _hostFds = new();

    public void Reset()
    {
        _pathPatched = _mallocPlanted = _cdIoPlanted = false;
        _versionReplants = _nullPathEscapes = _pathNormEscapes = _mallocReplants = 0;
        _hookReplants = _freelistEscapes = _searchPathFixes = _searchPlants = 0;
        _stackRescues = _cdIoReplants = _streamMapEscapes = 0;
        _hostOpens = _hostReads = _hostCloses = _hostSeeks = 0;
        _streamMapProbes = _streamMapPlants = 0;
        _streamMapLookupHits = 0;
        _tocProbeDone = false;
        _streamMapTable = _streamMapCount = _streamMapObj = 0;
        _hostFds.Clear();
        try { _isoVol?.Disc?.Dispose(); } catch { }
        _isoVol = null; _isoVolPath = null;
    }

    public void OnDiscMounted(Ps2System sys)
    {
        Reset();
        if (sys.Hle?.Sony?.RealRpc != null)
            sys.Hle.Sony.RealRpc.PreferIopRpGetVersion = true;
        PlantIopRpVersion(sys);
        PlantCrtMallocTable(sys);
        PlantStringHeapHook(sys);
        // Host CD stubs ready; live vtable wired after STREE0 TOC CdReads (see Step).
        PlantHostCdStubs(sys);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine("[VEXX] OnDiscMounted: IOPRP252 + CRT/string heap; CD I/O stubs planted");
    }

    public void OnHostPresent(Ps2System sys) => _ = sys;

    public void Step(Ps2System sys)
    {
        if (!VersionCellsOk(sys)) { PlantIopRpVersion(sys); _versionReplants++; }

        if (!_mallocPlanted || sys.Memory.Read32(CrtMallocSlot) == 0)
        {
            PlantCrtMallocTable(sys);
            _mallocPlanted = true;
            _mallocReplants++;
        }

        if (sys.Memory.Read32(StringAllocHook) != MallocStub)
        {
            PlantStringHeapHook(sys);
            _hookReplants++;
        }

        if (!_pathPatched || !PathStubActive(sys, PathBasenameA))
        {
            PatchNullPathBasename(sys);
            _pathPatched = true;
        }

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFu);

        // Wire live CD I/O vtable after STREE0 TOC CdReads (~89 sectors) so multi-chunk
        // libcdvd assembly is not interrupted; then host-serve secondary .TRE opens.
        if (sys.Cdvd.SectorsRead >= 80UL
            && (!_cdIoPlanted || sys.Memory.Read32(CdIoVtableBase) != HostCdStubOpen))
        {
            PlantCdIoVtable(sys);
            _cdIoReplants++;
        }

        // SearchFile path slide/plant + TRE size cap (TOC only, not full ~1GB).
        // Only the IOP arg buffer (sceCdlFILE) — never the EE SIF packet at 0x3F7B00
        // (wave-4: sliding the packet produced "E.TXT;1" / "EE0.TRE;1" garbage).
        if (sys.Scheduler.MasterCycles >= 500_000UL)
        {
            uint buf = SearchFileArgBuf;
            if (MaybeFixSearchFilePathLayout(sys, buf)) _searchPathFixes++;
            if (MaybePlantSearchFileResult(sys, buf)) _searchPlants++;
            if (MaybeCapTreSearchSize(sys, buf)) _searchPlants++;
        }

        // Wave-5: host-serve CD I/O once vtable is wired (STREE0 stream-map open/read).
        if (_cdIoPlanted && MaybeHostCdIo(sys, pc))
            return;

        if ((pc is >= 0x0014619C and <= 0x001461BC) || (pc is >= 0x0014625C and <= 0x0014627C))
        {
            if (sys.EE.GetGpr(16).Lo == 0)
            {
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
                sys.EE.PC = sys.EE.GetGpr(31).Lo;
                _nullPathEscapes++;
            }
        }

        if (pc is >= PathNormalizeLoop and <= PathNormalizeAfterLoop)
        {
            uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFu);
            if (sp >= 0x1000 && sp + 0x40 < SystemMemory.RDRAM_SIZE)
            {
                uint pathPtr = sys.Memory.Read32(sp + 0x38);
                if (pathPtr < 0x10000u)
                {
                    sys.Memory.Write32(sp + 0x38, EmptyStringSentinel);
                    sys.EE.SetGpr(7, new EmotionEngine.Gpr128 { Lo = EmptyStringSentinel });
                    sys.EE.SetGpr(6, new EmotionEngine.Gpr128 { Lo = 0 });
                    sys.EE.PC = PathNormalizeAfterLoop;
                    _pathNormEscapes++;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
                        Console.Error.WriteLine(
                            $"[VEXX] path-normalize escape #{_pathNormEscapes} wasPtr=0x{pathPtr:X} cyc={sys.Scheduler.MasterCycles}");
                }
            }
        }

        // Early freelist escape (pre-pad) corrupts CRT and open-bus thrash (binds=0).
        // Wave-3: allow up to FreelistMaxBump for STREE TOC / stream tables (not full 1GB).
        if (sys.Scheduler.MasterCycles >= FreelistEscapeMinCycles
            && pc is >= FreelistWalkLo and <= FreelistWalkHi)
        {
            long walks = (long)sys.EE.GetGpr(22).Lo;
            uint size = (uint)sys.EE.GetGpr(16).Lo;
            if (walks > 64)
            {
                // size==0 often follows a failed open; give a tiny block so callers that
                // store through the pointer do not hard-fault, and the freelist loop exits.
                if (size == 0)
                    size = 16;
                if (size > 0 && size < FreelistMaxBump)
                {
                    uint mem = HostBumpAlloc(sys, size + 64);
                    if (mem != 0)
                    {
                        sys.EE.SetGpr(20, new EmotionEngine.Gpr128 { Lo = mem });
                        sys.EE.SetGpr(21, new EmotionEngine.Gpr128 { Lo = mem + 32 });
                        sys.EE.PC = FreelistSuccessStore;
                        _freelistEscapes++;
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _freelistEscapes <= 16)
                            Console.Error.WriteLine(
                                $"[VEXX] freelist bump #{_freelistEscapes} size=0x{size:X} mem=0x{mem:X} cyc={sys.Scheduler.MasterCycles}");
                    }
                }
                else
                {
                    // Absurd sizes (~1GB TRE): fail alloc cleanly.
                    sys.EE.SetGpr(20, new EmotionEngine.Gpr128 { Lo = 0 });
                    sys.EE.SetGpr(21, new EmotionEngine.Gpr128 { Lo = 0 });
                    sys.EE.PC = FreelistSuccessStore;
                    _freelistEscapes++;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _freelistEscapes <= 16)
                        Console.Error.WriteLine(
                            $"[VEXX] freelist fail size=0x{size:X} cyc={sys.Scheduler.MasterCycles}");
                }
            }
        }

        // Null / planted stream-table hash walk.
        if (sys.Scheduler.MasterCycles >= FreelistEscapeMinCycles
            && pc is >= StreamMapLookupLo and <= StreamMapLookupHi)
        {
            _streamMapLookupHits++;
            MaybeEscapeNullStreamMap(sys, pc);
        }
        else
            _streamMapLookupHits = 0;

        // Wave-5: after STREE TOC CdReads (cdvd≥50), probe/build stream map so asset
        // lookups leave null-table thrash and Soft-GS can receive real prims.
        if (!_tocProbeDone && sys.Cdvd.SectorsRead >= 50UL
            && sys.Scheduler.MasterCycles >= 4_000_000UL)
            MaybeFinishStreamMap(sys);

        // Stack death residual: PC lands in path ASCII (STREE0.TRE / GAME.TXT) as code.
        if (sys.Scheduler.MasterCycles >= FreelistEscapeMinCycles && LooksLikePathAsciiPc(sys, pc))
            MaybeRescueStackDeath(sys, pc);
    }

    /// <summary>
    /// Install host-serve CD file backends. Wave-3 planted retail defaults that go through
    /// <c>host:</c>+FILEIO RPC (bind never appears). Wave-4 points the vtable at spin stubs
    /// serviced by <see cref="MaybeHostCdIo"/> with real ISO open/read + sector credit.
    /// </summary>
    public void PlantCdIoVtable(Ps2System sys)
    {
        PlantHostCdStubs(sys);
        // Slot layout (8-byte stride): +0 open, +8 close, +16 read, +24 write, +32 stub0,
        // +40 seek, +48 tell, +56 size, +64 misc — matches default-install order.
        // Live open path (0x1DCEFC) loads 0x3AD3A8; also keep legacy 0x3BD3A8 covered.
        foreach (uint baseAddr in new[] { CdIoVtableBase, 0x003BD3A8u })
        {
            sys.Memory.Write32(baseAddr + 0x00, HostCdStubOpen);
            sys.Memory.Write32(baseAddr + 0x08, HostCdStubClose);
            sys.Memory.Write32(baseAddr + 0x10, HostCdStubRead);
            sys.Memory.Write32(baseAddr + 0x18, HostCdStubWrite);
            sys.Memory.Write32(baseAddr + 0x20, CdIoDefaultStub0);
            sys.Memory.Write32(baseAddr + 0x28, HostCdStubSeek);
            sys.Memory.Write32(baseAddr + 0x30, HostCdStubTell);
            sys.Memory.Write32(baseAddr + 0x38, HostCdStubSize);
            sys.Memory.Write32(baseAddr + 0x40, CdIoDefaultMisc);
        }
        _cdIoPlanted = true;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine(
                $"[VEXX] CD I/O vtable @0x{CdIoVtableBase:X} (+legacy 0x3BD3A8) host-stubs open=0x{HostCdStubOpen:X} read=0x{HostCdStubRead:X}");
    }

    /// <summary>
    /// Wave-5: after STREE0 TOC CdReads, host-load the hash index (u32 count + count×24)
    /// into bump RAM. On null-stream-map lookup, plant obj+8 = table so asset paths resolve.
    /// </summary>
    private uint _streamMapTable;
    private uint _streamMapCount;
    private uint _streamMapObj;

    private void MaybeFinishStreamMap(Ps2System sys)
    {
        _streamMapProbes++;
        if (_streamMapTable == 0)
            TryBuildStreamMapFromIso(sys);

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _streamMapProbes <= 3)
            Console.Error.WriteLine(
                $"[VEXX] stream-map probe #{_streamMapProbes} cdvd={sys.Cdvd.SectorsRead} " +
                $"table=0x{_streamMapTable:X} count={_streamMapCount} " +
                $"hostOpen={_hostOpens} hostRead={_hostReads} mapEsc={_streamMapEscapes} " +
                $"cyc={sys.Scheduler.MasterCycles}");

        if (_streamMapTable != 0)
            _tocProbeDone = true;
    }

    /// <summary>
    /// STREE0 on-disk: u32 count, then count × 24-byte hash entries (stream open @ 0x1DCFE0).
    /// </summary>
    private void TryBuildStreamMapFromIso(Ps2System sys)
    {
        string? isoPath = sys.Cdvd.MountedPath;
        if (string.IsNullOrEmpty(isoPath)) return;
        try
        {
            if (_isoVol == null || _isoVolPath != isoPath)
            {
                try { _isoVol?.Disc?.Dispose(); } catch { }
                _isoVol = Iso9660.OpenFile(isoPath);
                _isoVolPath = isoPath;
            }
            if (_isoVol?.Disc == null) return;
            var entry = Iso9660.FindFile(_isoVol, "STREE0.TRE");
            if (entry == null) return;

            var hdr = new byte[8];
            int got = _isoVol.Disc.ReadAt((long)entry.ExtentLba * Iso9660.SectorSize, hdr);
            if (got < 4) return;
            uint count = BitConverter.ToUInt32(hdr, 0);
            if (count is 0 or > 200_000) return;

            uint bytes = count * 24u;
            uint alloc = bytes + 32u;
            uint table = HostBumpAlloc(sys, alloc);
            if (table == 0) return;

            // File layout: +0 count (4), +4 entries. Stream open reads count then entries.
            var buf = new byte[bytes];
            int n = _isoVol.Disc.ReadAt((long)entry.ExtentLba * Iso9660.SectorSize + 4, buf);
            if (n <= 0) return;
            for (int i = 0; i < n; i++)
                sys.Memory.Write8(table + (uint)i, buf[i]);
            for (int i = n; i < (int)bytes; i++)
                sys.Memory.Write8(table + (uint)i, 0);

            _streamMapTable = table;
            _streamMapCount = count;
            _streamMapPlants++;
            sys.Cdvd.NoteHostReadSectors((int)((4 + bytes + 2047) / 2048));
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
                Console.Error.WriteLine(
                    $"[VEXX] stream-map BUILD table=0x{table:X} count={count} bytes={bytes} cyc={sys.Scheduler.MasterCycles}");
        }
        catch
        {
            /* keep trying next probe */
        }
    }

    /// <summary>Plant host-built hash table into the live stream object (s5 / a0).</summary>
    private void MaybePlantStreamMapOnObject(Ps2System sys, uint obj)
    {
        if (_streamMapTable == 0 || obj < 0x1000 || obj + 0x420 >= SystemMemory.RDRAM_SIZE)
            return;
        uint cur = sys.Memory.Read32(obj + 8);
        if (cur == _streamMapTable) return;
        sys.Memory.Write32(obj + 8, _streamMapTable);
        sys.Memory.Write32(obj + 0xC, _streamMapCount);
        // Zero bucket count used by insert path; lookups walk the flat table via hash.
        if (sys.Memory.Read32(obj + 0x418) == 0)
            sys.Memory.Write32(obj + 0x418, 0);
        _streamMapObj = obj;
        _streamMapPlants++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _streamMapPlants <= 8)
            Console.Error.WriteLine(
                $"[VEXX] stream-map PLANT obj=0x{obj:X} table=0x{_streamMapTable:X} count={_streamMapCount}");
    }

    /// <summary>Spin loops so Step cannot miss the open/read PC (single-insn race).</summary>
    private static void PlantHostCdStubs(Ps2System sys)
    {
        // beq r0,r0,0; nop  — tight spin at each stub base
        for (uint s = HostCdStubBase; s < HostCdStubEnd; s += 0x20)
        {
            sys.Memory.Write32(s + 0, 0x1000FFFFu); // beq zero,zero,-1 (branch to self in delay)
            sys.Memory.Write32(s + 4, 0x00000000u); // nop delay
            for (uint i = 8; i < 0x20; i += 4)
                sys.Memory.Write32(s + i, 0);
        }
    }

    /// <summary>
    /// Host-serve CD I/O vtable entries: open/read/close/seek/tell/size against mounted ISO.
    /// Real disc bytes + <see cref="Cdvd.NoteHostReadSectors"/> — honest TRE TOC stream.
    /// </summary>
    private bool MaybeHostCdIo(Ps2System sys, uint pc)
    {
        var mods = sys.IopModules;
        if (mods == null) return false;
        // Accept any PC inside the stub slot (spin may land on +0 or +4).
        if (pc is >= HostCdStubOpen and < HostCdStubClose)
            return HostCdOpen(sys, mods);
        if (pc is >= HostCdStubClose and < HostCdStubRead)
            return HostCdClose(sys, mods);
        if (pc is >= HostCdStubRead and < HostCdStubWrite)
            return HostCdRead(sys, mods);
        if (pc is >= HostCdStubWrite and < HostCdStubSeek)
        {
            ReturnHost(sys, unchecked((uint)(-1))); // write not used for TRE rb
            return true;
        }
        if (pc is >= HostCdStubSeek and < HostCdStubTell)
            return HostCdSeek(sys, mods);
        if (pc is >= HostCdStubTell and < HostCdStubSize)
            return HostCdTell(sys, mods);
        if (pc is >= HostCdStubSize and < HostCdStubEnd)
            return HostCdSize(sys, mods);
        // Also catch retail entries if something still jumps there
        if (pc == CdIoDefaultOpen) return HostCdOpen(sys, mods);
        if (pc == CdIoDefaultRead) return HostCdRead(sys, mods);
        if (pc == CdIoDefaultClose) return HostCdClose(sys, mods);
        if (pc == CdIoDefaultSeek) return HostCdSeek(sys, mods);
        if (pc == CdIoDefaultTell) return HostCdTell(sys, mods);
        if (pc == CdIoDefaultSize) return HostCdSize(sys, mods);
        return false;
    }

    private bool HostCdOpen(Ps2System sys, IopModuleHost mods)
    {
        uint pathPtr = (uint)(sys.EE.GetGpr(4).Lo & 0x1FFFFFFFu); // a0
        string raw = ReadCString(sys, pathPtr, 256);
        string path = NormalizeHostCdPath(raw);
        if (path.Length == 0)
        {
            ReturnHost(sys, 0);
            return true;
        }

        // Prefer cdrom0: so FileOpen hits disc path.
        string tryPath = path.Contains(':') ? path : "cdrom0:\\" + path;
        int fd = mods.FileOpen(tryPath, 1);
        if (fd < 0 && !tryPath.Equals(path, StringComparison.OrdinalIgnoreCase))
            fd = mods.FileOpen(path, 1);
        if (fd < 0)
        {
            string leaf = System.IO.Path.GetFileName(path.Replace('/', '\\'));
            if (!string.IsNullOrEmpty(leaf))
                fd = mods.FileOpen("cdrom0:\\" + leaf, 1);
        }

        if (fd < 0)
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _hostOpens < 24)
                Console.Error.WriteLine(
                    $"[VEXX] host-open FAIL \"{raw}\" → \"{path}\" cyc={sys.Scheduler.MasterCycles}");
            ReturnHost(sys, 0);
            return true;
        }

        int handle = fd + 1; // retail open returns 1-based; read does a0--
        _hostFds[handle] = fd;
        _hostOpens++;
        // Do NOT credit full 1GB TRE at open — only actual FileRead bytes (TOC stream).
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _hostOpens <= 16)
        {
            mods.TryGetOpenFileSize(fd, out uint sz);
            Console.Error.WriteLine(
                $"[VEXX] host-open #{_hostOpens} \"{raw}\" → \"{path}\" h={handle} fd={fd} size={sz} cyc={sys.Scheduler.MasterCycles}");
        }
        ReturnHost(sys, unchecked((uint)handle));
        return true;
    }

    private bool HostCdRead(Ps2System sys, IopModuleHost mods)
    {
        // Entry is `j real_read; addiu a0,a0,-1` — intercept before delay-slot, a0 still 1-based.
        int handle = (int)sys.EE.GetGpr(4).Lo;
        uint buf = (uint)sys.EE.GetGpr(5).Lo;
        uint size = (uint)sys.EE.GetGpr(6).Lo;
        if (!_hostFds.TryGetValue(handle, out int fd))
        {
            // Maybe already 0-based from a direct call
            if (_hostFds.TryGetValue(handle + 1, out fd))
                handle = handle + 1;
            else
            {
                ReturnHost(sys, unchecked((uint)(-9))); // EBADF-ish
                return true;
            }
        }

        // Reject non-RDRAM destinations (wave-5: Game.txt thrash used buf=0xFFFFFFF0).
        uint phys = buf & 0x1FFFFFFFu;
        if (buf == 0 || phys < 0x1000u || phys >= SystemMemory.RDRAM_SIZE
            || size == 0 || size > HostReadMaxBytes && phys + Math.Min(size, HostReadMaxBytes) > SystemMemory.RDRAM_SIZE)
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _hostReads < 24)
                Console.Error.WriteLine(
                    $"[VEXX] host-read BADARGS h={handle} buf=0x{buf:X} size=0x{size:X} cyc={sys.Scheduler.MasterCycles}");
            ReturnHost(sys, unchecked((uint)(-14))); // EFAULT-ish
            return true;
        }

        if (size > HostReadMaxBytes)
            size = HostReadMaxBytes;
        // Cap to remaining RDRAM from phys.
        if (phys + size > SystemMemory.RDRAM_SIZE)
            size = SystemMemory.RDRAM_SIZE - phys;

        int n = mods.FileRead(sys.Memory, fd, phys, size);
        if (n > 0)
            sys.Cdvd.NoteHostReadSectors((n + 2047) / 2048);
        _hostReads++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _hostReads <= 24)
            Console.Error.WriteLine(
                $"[VEXX] host-read #{_hostReads} h={handle} buf=0x{buf:X} size=0x{size:X} n={n} cdvd={sys.Cdvd.SectorsRead} cyc={sys.Scheduler.MasterCycles}");
        ReturnHost(sys, unchecked((uint)n));
        return true;
    }

    private bool HostCdClose(Ps2System sys, IopModuleHost mods)
    {
        int handle = (int)sys.EE.GetGpr(4).Lo;
        if (_hostFds.TryGetValue(handle, out int fd))
        {
            mods.FileClose(fd);
            _hostFds.Remove(handle);
            _hostCloses++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _hostCloses <= 16)
                Console.Error.WriteLine(
                    $"[VEXX] host-close #{_hostCloses} h={handle} cyc={sys.Scheduler.MasterCycles}");
        }
        ReturnHost(sys, 0);
        return true;
    }

    private bool HostCdSeek(Ps2System sys, IopModuleHost mods)
    {
        // seek(fd, off, whence): a0=handle, a1=off, a2=whence (retail wrapper).
        int handle = (int)sys.EE.GetGpr(4).Lo;
        int off = (int)sys.EE.GetGpr(5).Lo;
        int whence = (int)sys.EE.GetGpr(6).Lo;
        if (!_hostFds.TryGetValue(handle, out int fd))
        {
            // tell path uses a0-- before jump; accept 0-based
            if (!_hostFds.TryGetValue(handle + 1, out fd))
            {
                ReturnHost(sys, unchecked((uint)(-1)));
                return true;
            }
        }
        int pos = mods.FileSeek(fd, off, whence);
        _hostSeeks++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _hostSeeks <= 16)
            Console.Error.WriteLine(
                $"[VEXX] host-seek #{_hostSeeks} h={handle} off={off} wh={whence} → {pos} cyc={sys.Scheduler.MasterCycles}");
        ReturnHost(sys, unchecked((uint)pos));
        return true;
    }

    private bool HostCdTell(Ps2System sys, IopModuleHost mods)
    {
        // tell entry: addiu a0,a0,-1 then j seek-like with whence=1 off=0 — handle still 1-based here.
        int handle = (int)sys.EE.GetGpr(4).Lo;
        if (!_hostFds.TryGetValue(handle, out int fd))
        {
            ReturnHost(sys, unchecked((uint)(-1)));
            return true;
        }
        int pos = mods.FileSeek(fd, 0, 1); // SEEK_CUR
        ReturnHost(sys, unchecked((uint)pos));
        return true;
    }

    private bool HostCdSize(Ps2System sys, IopModuleHost mods)
    {
        int handle = (int)sys.EE.GetGpr(4).Lo;
        if (!_hostFds.TryGetValue(handle, out int fd))
        {
            ReturnHost(sys, 0);
            return true;
        }
        if (!mods.TryGetOpenFileSize(fd, out uint sz))
            sz = 0;
        // Cap absurd TRE (~1GB) so malloc(size) takes TOC headroom only. Stream open uses the
        // first u32 entry-count for the hash table (count×24), not this size.
        if (sz > 8u * 1024 * 1024)
            sz = 0x00492570; // STREE0 TOC byte length from header w1
        ReturnHost(sys, sz);
        return true;
    }

    private static void ReturnHost(Ps2System sys, uint v0)
    {
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = v0 });
        sys.EE.PC = sys.EE.GetGpr(31).Lo;
    }

    /// <summary>
    /// Map retail <c>host:$/stree0.tre</c> / <c>$/Data/…</c> / bare leaves onto ISO open paths.
    /// </summary>
    internal static string NormalizeHostCdPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string path = raw.Trim();
        // Strip device prefixes (open also strcat's "host:" in retail — we intercept before that
        // when a0 is the caller's path; stream open passes "$/stree0.tre" or resolved leaf).
        if (path.StartsWith("host0:", StringComparison.OrdinalIgnoreCase))
            path = path[6..];
        else if (path.StartsWith("host:", StringComparison.OrdinalIgnoreCase))
            path = path[5..];
        else if (path.StartsWith("cdrom0:", StringComparison.OrdinalIgnoreCase))
            path = path[7..];
        else if (path.StartsWith("cdrom:", StringComparison.OrdinalIgnoreCase))
            path = path[6..];
        path = path.TrimStart('\\', '/');
        if (path.StartsWith("$/", StringComparison.Ordinal) || path.StartsWith("$\\", StringComparison.Ordinal))
            path = path[2..];
        else if (path.Length > 0 && path[0] == '$')
            path = path[1..].TrimStart('\\', '/');
        int semi = path.IndexOf(';');
        if (semi >= 0) path = path[..semi];
        // Prefer leaf for ISO root files (STREE0.TRE / GAME.TXT live at disc root).
        string leaf = System.IO.Path.GetFileName(path.Replace('/', '\\'));
        if (!string.IsNullOrEmpty(leaf) && leaf.IndexOf('.') > 0
            && leaf.IndexOfAny(new[] { '/', '\\' }) < 0)
            return leaf;
        return path.Replace('/', '\\');
    }

    /// <summary>
    /// When hash-map lookup runs with a null table pointer, return miss (v0=0) instead of
    /// infinite chain walk / AdEL thrash.
    /// </summary>
    private void MaybeEscapeNullStreamMap(Ps2System sys, uint pc)
    {
        uint s5 = (uint)(sys.EE.GetGpr(21).Lo & 0x1FFFFFFFu); // s5
        uint table = 0;
        if (s5 >= 0x1000 && s5 + 0x20 < SystemMemory.RDRAM_SIZE)
            table = sys.Memory.Read32(s5 + 8);

        // Wave-5: plant host-built STREE0 index before giving up on the lookup.
        if ((table == 0 || table >= SystemMemory.RDRAM_SIZE) && _streamMapTable == 0
            && sys.Cdvd.SectorsRead >= 50UL)
            TryBuildStreamMapFromIso(sys);
        if ((table == 0 || table >= SystemMemory.RDRAM_SIZE) && _streamMapTable != 0)
        {
            MaybePlantStreamMapOnObject(sys, s5);
            table = sys.Memory.Read32(s5 + 8);
            if (table == _streamMapTable)
            {
                // Restart at `lw v1, 8(s5)` so the walk uses the planted table.
                sys.EE.PC = 0x001DD2CCu;
                return;
            }
        }

        bool tableBad = table == 0
            || table >= SystemMemory.RDRAM_SIZE
            || (table & 3) != 0;
        // Also bail if a3 is a non-canonical / high garbage pointer mid-walk.
        uint a3 = (uint)sys.EE.GetGpr(7).Lo;
        bool a3Bad = a3 >= SystemMemory.RDRAM_SIZE || (a3 & 0x80000000u) != 0;
        // Planted flat STREE0 index: entry pointer must fall inside [table, table+count*24).
        if (!tableBad && _streamMapTable != 0 && table == _streamMapTable && _streamMapCount > 0)
        {
            uint mapEnd = _streamMapTable + _streamMapCount * 24u;
            if (a3 < _streamMapTable || a3 >= mapEnd)
                a3Bad = true;
        }
        // Stuck in lookup band across many quirk slices (planted table, bad chain) → miss.
        if (!tableBad && !a3Bad && _streamMapLookupHits < 8)
            return;

        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // v0 = miss
        sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 0 }); // s0 = not-found
        sys.EE.PC = StreamMapLookupFail;
        _streamMapEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _streamMapEscapes <= 16)
            Console.Error.WriteLine(
                $"[VEXX] null-stream-map escape #{_streamMapEscapes} pc=0x{pc:X} s5=0x{s5:X} table=0x{table:X} a3=0x{a3:X} cyc={sys.Scheduler.MasterCycles}");
    }

    private void MaybeRescueStackDeath(Ps2System sys, uint pc)
    {
        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFu);
        uint resume = 0;
        if (sp is >= 0x00100000 and < SystemMemory.RDRAM_SIZE)
        {
            for (uint off = 0; off <= 0x80; off += 4)
            {
                uint cand = sys.Memory.Read32(sp + off);
                if ((cand & 3) == 0 && sys.Memory.IsLikelyEeCode(cand)
                    && (cand & 0x1FFFFFFFu) is >= 0x00100000 and < 0x00400000)
                {
                    resume = cand & 0x1FFFFFFFu;
                    break;
                }
            }
        }
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFu);
        if (resume == 0 && (ra & 3) == 0 && sys.Memory.IsLikelyEeCode(ra)
            && ra is >= 0x00100000 and < 0x00400000)
            resume = ra;
        if (resume == 0 && sys.Memory.IsLikelyEeCode(0x0011C200u))
            resume = 0x0011C200u;
        if (resume == 0) return;

        sys.EE.PC = resume;
        _stackRescues++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _stackRescues <= 16)
            Console.Error.WriteLine(
                $"[VEXX] stack-death rescue #{_stackRescues} from=0x{pc:X} -> 0x{resume:X} cyc={sys.Scheduler.MasterCycles}");
    }

    private static bool LooksLikePathAsciiPc(Ps2System sys, uint pc)
    {
        if (pc < 0x00300000 || pc + 4 >= SystemMemory.RDRAM_SIZE) return false;
        if (sys.Memory.IsLikelyEeCode(pc)) return false;
        int printable = 0;
        for (int i = 0; i < 4; i++)
        {
            byte b = sys.Memory.Read8(pc + (uint)i);
            if (b is >= 0x20 and <= 0x7E) printable++;
        }
        if (printable < 3) return false;
        for (int i = 0; i < 12; i++)
        {
            byte b = sys.Memory.Read8(pc + (uint)i);
            if (b is (byte)'.' or (byte)'\\' or (byte)'/' or (byte)';') return true;
        }
        uint w = sys.Memory.Read32(pc);
        // "STRE" "GAME" "e0.t" fragments from STREE0.TRE
        if (w is 0x45525453u or 0x454D4147u or 0x742E3065u) return true;
        return printable >= 4;
    }

    public static void PlantIopRpVersion(Ps2System sys)
    {
        WriteCString4(sys, IopVersionCellA, "2520");
        WriteCString4(sys, IopVersionCellB, "2520");
    }

    public static void PlantStringHeapHook(Ps2System sys)
    {
        if (sys.Memory.Read32(MallocStub) == 0)
            PlantCrtMallocTable(sys);
        sys.Memory.Write32(StringAllocHook, MallocStub);
        sys.Memory.Write32(StringFreeHook, 0x001CEBC0); // CRT free trampoline
        sys.Memory.Write32(SmallPoolRoot, 0);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine($"[VEXX] string-hook malloc=0x{MallocStub:X} free→CRT; pool cleared");
    }

    public static void PlantCrtMallocTable(Ps2System sys)
    {
        uint cur = BumpCursorCell, stub = MallocStub, end = BumpArenaEnd;
        uint existing = sys.Memory.Read32(cur);
        if (existing < BumpArenaBase || existing >= BumpArenaEnd)
            sys.Memory.Write32(cur, BumpArenaBase);

        uint[] mallocOps =
        {
            0x3C080000u | (cur >> 16), 0x35080000u | (cur & 0xFFFF), 0x8D020000u,
            0x2489000Fu, 0x00094902u, 0x00094900u, 0x00495021u,
            0x3C0B0000u | (end >> 16), 0x356B0000u | (end & 0xFFFF),
            0x014B602Bu, 0x11800004u, 0x00000000u, 0xAD0A0000u,
            0x03E00008u, 0x00000000u, 0x03E00008u, 0x0000102Du,
        };
        for (int i = 0; i < mallocOps.Length; i++)
            sys.Memory.Write32(stub + (uint)(i * 4), mallocOps[i]);

        sys.Memory.Write32(FreeStub + 0, 0x03E00008u);
        sys.Memory.Write32(FreeStub + 4, 0x00000000u);
        sys.Memory.Write32(ReallocStub + 0, 0x00A0202Du);
        sys.Memory.Write32(ReallocStub + 4, 0x08000000u | ((MallocStub >> 2) & 0x03FFFFFF));
        sys.Memory.Write32(ReallocStub + 8, 0x00000000u);
        sys.Memory.Write32(CrtMallocSlot, MallocStub);
        sys.Memory.Write32(CrtFreeSlot, FreeStub);
        sys.Memory.Write32(CrtReallocSlot, ReallocStub);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine($"[VEXX] CRT malloc table → bump 0x{BumpArenaBase:X}-0x{BumpArenaEnd:X}");
    }

    public static uint HostBumpAlloc(Ps2System sys, uint size)
    {
        uint cur = sys.Memory.Read32(BumpCursorCell);
        if (cur < BumpArenaBase || cur >= BumpArenaEnd)
        {
            cur = BumpArenaBase;
            sys.Memory.Write32(BumpCursorCell, cur);
        }
        uint aligned = (size + 15u) & ~15u;
        if (aligned == 0) aligned = 16;
        ulong next = (ulong)cur + aligned;
        if (next >= BumpArenaEnd) return 0;
        sys.Memory.Write32(BumpCursorCell, (uint)next);
        return cur;
    }

    public static bool MaybeFixSearchFilePathLayout(Ps2System sys, uint buf)
    {
        if (buf + 0x120 >= SystemMemory.RDRAM_SIZE) return false;
        // Do not touch a completed sceCdlFILE (valid lsn + planted leaf) — sliding mid-string
        // fragments like "E.TXT;1" after GAME.TXT corrupts the live SearchFile result while
        // NCMD CdRead is in flight (wave-4 residual).
        uint curLsn = sys.Memory.Read32(buf);
        string planted = ReadCStringStatic(sys, buf + 8, 16);
        if (curLsn != 0 && IsPlausibleSearchLeaf(planted))
            return false;

        byte at24 = sys.Memory.Read8(buf + 0x24);
        // Require path-shaped start: \ / $ or drive-ish — not mid-leaf "E.TXT".
        if (at24 is not ((byte)'\\' or (byte)'/' or (byte)'$'))
            return false;

        var tmp = new byte[0x100];
        int len = 0;
        for (; len < tmp.Length; len++)
        {
            byte b = sys.Memory.Read8(buf + 0x24 + (uint)len);
            tmp[len] = b;
            if (b == 0) { len++; break; }
        }
        if (len <= 1) return false;

        // Slide when +0x20 empty OR stale (different leaf than +0x24) — STREE0 after GAME.TXT.
        string path24 = Encoding.ASCII.GetString(tmp, 0, Math.Max(0, len - 1));
        string path20 = ReadCStringStatic(sys, buf + 0x20, 128);
        string leaf24 = NormalizeSearchLeaf(path24);
        string leaf20 = NormalizeSearchLeaf(path20);
        if (!IsPlausibleSearchLeaf(leaf24)) return false;
        bool needSlide = path20.Length == 0 || (leaf24.Length > 0 && leaf24 != leaf20);
        if (!needSlide) return false;

        for (int i = 0; i < len; i++)
            sys.Memory.Write8(buf + 0x20 + (uint)i, tmp[i]);
        // New path: clear stale lsn/size so plant / HLE rewrite for STREE0 etc.
        if (leaf24.Length > 0 && leaf24 != leaf20)
        {
            sys.Memory.Write32(buf + 0, 0);
            sys.Memory.Write32(buf + 4, 0);
        }

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine($"[VEXX] SearchFile path slide @0x{buf:X} → \"{path24}\"");
        return true;
    }

    public bool MaybePlantSearchFileResult(Ps2System sys, uint buf)
    {
        string? isoPath = sys.Cdvd.MountedPath;
        if (string.IsNullOrEmpty(isoPath) || buf + 0x30 >= SystemMemory.RDRAM_SIZE) return false;

        string name = ReadCString(sys, buf + 0x20, 128);
        if (name.Length == 0) name = ReadCString(sys, buf + 0x24, 128);
        if (name.Length == 0) return false;

        name = NormalizeSearchLeaf(name);
        if (!IsPlausibleSearchLeaf(name)) return false;
        if (name.Contains('\\') || name.Contains('/') || name.StartsWith('$')) return false;

        // Re-plant when lsn empty OR planted leaf at +8 mismatches requested path (STREE0).
        string plantedLeaf = ReadCString(sys, buf + 8, 16);
        uint curLsn = sys.Memory.Read32(buf);
        if (curLsn != 0 && string.Equals(plantedLeaf, name, StringComparison.OrdinalIgnoreCase))
            return false;
        if (curLsn != 0 && plantedLeaf.Length > 0
            && name.StartsWith(plantedLeaf, StringComparison.OrdinalIgnoreCase))
            return false;

        if (_isoVol == null || _isoVolPath != isoPath)
        {
            try { _isoVol?.Disc?.Dispose(); } catch { }
            _isoVol = Iso9660.OpenFile(isoPath);
            _isoVolPath = isoPath;
        }
        if (_isoVol == null) return false;

        try
        {
            var entry = Iso9660.FindFile(_isoVol, name)
                ?? Iso9660.FindFile(_isoVol, System.IO.Path.GetFileName(name));
            if (entry == null) return false;

            uint reportSize = CapTreSizeIfNeeded(entry.Name, entry.Size, _isoVol, entry);
            sys.Memory.Write32(buf + 0, entry.ExtentLba);
            sys.Memory.Write32(buf + 4, reportSize);
            string leaf = entry.Name.Length > 15 ? entry.Name[..15] : entry.Name;
            for (int i = 0; i < 16; i++)
                sys.Memory.Write8(buf + 8 + (uint)i, i < leaf.Length ? (byte)leaf[i] : (byte)0);

            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
                Console.Error.WriteLine(
                    $"[VEXX] SearchFile plant @0x{buf:X} \"{name}\" lsn={entry.ExtentLba} size={reportSize}" +
                    (reportSize != entry.Size ? $" (full={entry.Size})" : ""));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// After HLE SearchFile writes full ~1GB STREE size, rewrite +4 to TOC byte length so
    /// freelist/bump can allocate and host-open can stream the header.
    /// </summary>
    private bool MaybeCapTreSearchSize(Ps2System sys, uint buf)
    {
        if (buf + 0x20 >= SystemMemory.RDRAM_SIZE) return false;
        uint size = sys.Memory.Read32(buf + 4);
        if (size <= 8 * 1024 * 1024u) return false;
        string leaf = ReadCString(sys, buf + 8, 16);
        if (leaf.Length < 4 || !leaf.EndsWith(".TRE", StringComparison.OrdinalIgnoreCase)
            && !leaf.StartsWith("STREE", StringComparison.OrdinalIgnoreCase))
        {
            // Also sniff path at +0x20/+0x24
            string p = ReadCString(sys, buf + 0x20, 64);
            if (p.Length == 0) p = ReadCString(sys, buf + 0x24, 64);
            if (p.IndexOf(".TRE", StringComparison.OrdinalIgnoreCase) < 0
                && p.IndexOf("STREE", StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            leaf = System.IO.Path.GetFileName(p.Replace('/', '\\'));
        }

        uint lsn = sys.Memory.Read32(buf);
        uint toc = 0;
        string? isoPath = sys.Cdvd.MountedPath;
        if (!string.IsNullOrEmpty(isoPath) && lsn != 0)
        {
            try
            {
                if (_isoVol == null || _isoVolPath != isoPath)
                {
                    try { _isoVol?.Disc?.Dispose(); } catch { }
                    _isoVol = Iso9660.OpenFile(isoPath);
                    _isoVolPath = isoPath;
                }
                if (_isoVol?.Disc != null)
                {
                    var hdr = new byte[16];
                    int got = _isoVol.Disc.ReadAt((long)lsn * Iso9660.SectorSize, hdr);
                    if (got >= 8)
                    {
                        uint w0 = BitConverter.ToUInt32(hdr, 0);
                        uint w1 = BitConverter.ToUInt32(hdr, 4);
                        if (w1 is >= 0x10000 and <= 0x800000) toc = w1;
                        else if (((ulong)w0 << 4) is >= 0x10000 and <= 0x800000) toc = w0 << 4;
                    }
                }
            }
            catch { /* fall through */ }
        }
        if (toc == 0) toc = 0x00480000; // ~4.5MB default
        if (toc >= size) return false;
        sys.Memory.Write32(buf + 4, toc);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine(
                $"[VEXX] TRE size cap @0x{buf:X} \"{leaf}\" {size} → {toc} cyc={sys.Scheduler.MasterCycles}");
        return true;
    }

    private static uint CapTreSizeIfNeeded(string name, uint fullSize, Iso9660.Volume? vol, Iso9660.FileEntry entry)
    {
        if (fullSize <= 8 * 1024 * 1024u) return fullSize;
        if (name.IndexOf(".TRE", StringComparison.OrdinalIgnoreCase) < 0
            && name.IndexOf("STREE", StringComparison.OrdinalIgnoreCase) < 0)
            return fullSize;
        try
        {
            if (vol?.Disc != null && entry.ExtentLba != 0)
            {
                var hdr = new byte[16];
                int got = vol.Disc.ReadAt((long)entry.ExtentLba * Iso9660.SectorSize, hdr);
                if (got >= 8)
                {
                    uint w0 = BitConverter.ToUInt32(hdr, 0);
                    uint w1 = BitConverter.ToUInt32(hdr, 4);
                    if (w1 is >= 0x10000 and <= 0x800000) return w1;
                    if (((ulong)w0 << 4) is >= 0x10000 and <= 0x800000) return w0 << 4;
                }
            }
        }
        catch { /* ignore */ }
        return 0x00480000;
    }

    private static string NormalizeSearchLeaf(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        int colon = name.IndexOf(':');
        if (colon >= 0) name = name[(colon + 1)..];
        name = name.TrimStart('\\', '/');
        int semi = name.IndexOf(';');
        if (semi >= 0) name = name[..semi];
        return name.Trim();
    }

    /// <summary>ISO leaf like GAME.TXT / STREE0.TRE — not "." or empty junk.</summary>
    private static bool IsPlausibleSearchLeaf(string leaf)
    {
        if (string.IsNullOrEmpty(leaf) || leaf.Length is < 3 or > 64) return false;
        if (leaf is "." or "..") return false;
        bool hasAlnum = false, hasDot = false;
        foreach (char c in leaf)
        {
            if (char.IsAsciiLetterOrDigit(c)) hasAlnum = true;
            else if (c == '.') hasDot = true;
            else if (c is not ('_' or '-' or ' ')) return false;
        }
        return hasAlnum && hasDot;
    }

    private static string ReadCStringStatic(Ps2System sys, uint addr, int max)
    {
        var sb = new StringBuilder(max);
        for (int i = 0; i < max; i++)
        {
            byte b = sys.Memory.Read8(addr + (uint)i);
            if (b == 0) break;
            if (b is < 32 or >= 127) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static bool VersionCellsOk(Ps2System sys) =>
        ReadCString4(sys, IopVersionCellA) == "2520" || ReadCString4(sys, IopVersionCellB) == "2520";

    private static bool PathStubActive(Ps2System sys, uint entry) =>
        (sys.Memory.Read32(entry) >> 26) == 2;

    public static void PatchNullPathBasename(Ps2System sys)
    {
        PlantOne(sys, PathBasenameA, StubA);
        PlantOne(sys, PathBasenameB, StubB);
    }

    private static void PlantOne(Ps2System sys, uint entry, uint stub)
    {
        uint w0 = sys.Memory.Read32(entry);
        uint w1 = sys.Memory.Read32(entry + 4);
        if ((w0 >> 26) == 2) return;
        uint cont = (entry + 8) >> 2;
        sys.Memory.Write32(stub + 0x00, 0x10800005u);
        sys.Memory.Write32(stub + 0x04, 0x00000000u);
        sys.Memory.Write32(stub + 0x08, w0);
        sys.Memory.Write32(stub + 0x0C, w1);
        sys.Memory.Write32(stub + 0x10, 0x08000000u | (cont & 0x03FFFFFF));
        sys.Memory.Write32(stub + 0x14, 0x00000000u);
        sys.Memory.Write32(stub + 0x18, 0x03E00008u);
        sys.Memory.Write32(stub + 0x1C, 0x0000102Du);
        sys.Memory.Write32(entry + 0x00, 0x08000000u | ((stub >> 2) & 0x03FFFFFF));
        sys.Memory.Write32(entry + 0x04, 0x00000000u);
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

    private static string ReadCString(Ps2System sys, uint addr, int max)
    {
        var sb = new StringBuilder(max);
        for (int i = 0; i < max; i++)
        {
            byte b = sys.Memory.Read8(addr + (uint)i);
            if (b == 0) break;
            if (b is < 32 or >= 127) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }
}
