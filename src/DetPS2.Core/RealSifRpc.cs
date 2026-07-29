using System;
using System.Collections.Generic;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Real Sony SIF RPC wire-protocol HLE (bind + call), layered under the existing
/// SifSetDma (EE syscall 0x77) intercept in SonyKernelHle. This is distinct from
/// the synthetic 16-byte "DetPS2 RPC ABI" in SifRpc.cs (still used by our own HLE
/// syscalls) — retail-compiled ps2sdk sifrpc.c code speaks THIS protocol over real
/// SIF DMA, so unmodified commercial binaries only get serviced through here.
///
/// Packet shapes and cid values verified against ps2sdk source (github.com/ps2dev/ps2sdk):
///   common/include/sifcmd-common.h, sifrpc-common.h; ee/kernel/src/sifrpc.c;
///   ee/rpc/cdvd/src/{scmd,ncmd}.c; ee/rpc/pad/src/libpad.c; ee/rpc/memorycard/src/libmc.c
///
/// Design: this is a transport-level shim, not real IOP CPU emulation. The real
/// EE-side sifrpc.c library already creates and waits on a semaphore
/// (SifRpcClientData_t.hdr.sema_id) for both bind and call — we don't need to
/// fake that wait/wake mechanism, just read the (already valid, real) semaphore
/// id the game wrote and signal it via the existing, real KernelState semaphore
/// implementation once we've written our response.
/// </summary>
public sealed class RealSifRpc
{
    public const uint CidRpcEnd = 0x80000008;
    public const uint CidRpcBind = 0x80000009;
    public const uint CidRpcCall = 0x8000000A;
    public const uint CidRpcRdata = 0x8000000C;
    /// <summary>SIFCMD INIT / system command family (BIOS SIFCMD.IRX registers 0x80000001).</summary>
    public const uint CidSifInit = 0x80000000;
    public const uint CidSifSetSreg = 0x80000001;

    // Known real service ids (sid) bound by retail libcdvd/libpad/libmc/fileio.
    public const uint SidCdScmd = 0x80000593; // sync CDVD status calls
    public const uint SidCdNcmd = 0x80000595; // async CDVD read/seek — the real disc-data path
    public const uint SidPad1 = 0x80000100;
    public const uint SidPad2 = 0x80000101;
    public const uint SidMcServ = 0x80000400;
    /// <summary>
    /// BIOS FILEIO.IRX / retail <c>sceOpen</c> RPC service (ps2tek + ps2sdk fileio:
    /// <c>sceSifBindRpc(&amp;cd, 0x80000001, 0)</c>). Distinct from SIFCMD <see cref="CidSifSetSreg"/>
    /// which shares the same numeric value in the *command-id* namespace, not RPC sid space.
    /// </summary>
    public const uint SidFileIo = 0x80000001;
    // Confirmed real CDVDMAN also registers additional sids in the same 0x8000059x family
    // (a public search over a disassembled CDVDMAN v0.1.1 module lists 0x592/593/595/597/
    // 59A/59C) — 0x592 is the one observed in a real boot; treated as the same CDVD family
    // (status-query semantics) as SidCdScmd rather than left unknown, since it's clearly
    // sibling infrastructure, not a fundamentally different module.
    public const uint SidCdBase = 0x80000592;

    // Real ps2sdk IOP-heap allocator RPC service (ee/kernel/src/iopheap.c: SifInitIopHeap
    // binds sid=0x80000003 unconditionally). Confirmed via a real boot: MK Shaolin Monks'
    // PS2RNA_Init calls SifAllocIopHeap(size), whose panic string ("Failed, sceSifAllocIopHeap
    // (%d) in PS2RNA_Init") is what's printed when this call's reply isn't a real allocated
    // address. Unlike the other "unknown service -> return 1" fallbacks, this one MUST return
    // an actual usable IOP RAM pointer in recvbuf, since the caller does `(void*)arg.addr`
    // with no further success/failure check on the value itself.
    public const uint SidSysmem = 0x80000003;

    // Midway/Surreal's proprietary "SNDF_Driver" (SNDFI.IRX, v2.24b, built 2005-07-22) —
    // extracted from the real disc image and disassembled (R3000A) since no public
    // documentation of this protocol exists anywhere. sid is the module's own name read
    // literally from its .iopmod header ("SNDF_Driver" -> "SNDF" as a big-endian-in-register
    // fourCC, matching sceSifRegisterRpc's actual call args at file offset 0xBAD8 in the
    // extracted IRX). The RPC callback (real vaddr 0xBAE0) dispatches on (fno & 0xFF00);
    // fno=0x1300 (the only value this title's boot path calls) reached a handler (real vaddr
    // 0xB718) that returns 0 on success, -1 only if the first packed dword (arg buf word 0)
    // is zero — a plain C "0 == success" contract, NOT this file's usual "return 1" idiom.
    public const uint SidSndf = 0x534E4446;
    private const uint SndfInitAudio = 0x1300;

    // SNDFI.IRX's second registered service, "SFSV" as the same fourCC-in-register convention
    // as SidSndf (registered at real vaddr 0xC2C8, same module/.iopmod as SNDF_Driver). Bound
    // early but not called until real forward progress unblocks the code path that reaches it
    // (confirmed live: 2026-07-28, only reached after the LOADFILE/sid=0x80000006 fix below let
    // the boot sequence's reference-counted init gate get its first real decrements). Only
    // fno=0x8000 observed so far; not independently disassembled the way SNDF's fno=0x1300 was,
    // but same module/toolchain ("MW MIPS C Compiler") as SNDF_Driver and SDRDRV, both confirmed
    // to use a 0-for-success convention -- applying that same convention here rather than this
    // file's generic "unknown service -> 1" fallback, since the latter comes from a different,
    // unrelated toolchain assumption that's already been shown wrong for this module's siblings.
    public const uint SidSfsv = 0x53465356;

    // CRI Middleware's ADX audio codec IOP driver (CRI_ADXI.IRX) — extracted from the disc
    // and disassembled; sid read directly out of its .data section (a runtime variable, not
    // a compile-time constant, unlike Midway's own SNDF/SFSV services) at the call site for
    // sceSifRegisterRpc (real vaddr 0x8AC). See the echo-handling comment in HandleCall for
    // why this service's fno!=2/3 calls are special-cased there rather than in Dispatch.
    public const uint SidCriAdx = 0x90000200;

    // Midway/Surreal's raw SPU2 register RPC driver (SDRDRV.IRX) — extracted and
    // disassembled; sid confirmed via its literal sceSifRegisterRpc call args (real vaddr
    // 0x5BC: lui a1,0x8000 / ori a1,a1,0x701). Its callback (real vaddr 0x628) dispatches on
    // (fno & 0xFFF0) across ~17 groups matching a 24-voice, 0x10-byte-stride register block
    // (observed calls: fno=0x8000/0x8010 = voice 0/1 setup) — a full raw-register pass-
    // through we haven't individually mapped. Same toolchain as SNDF_Driver ("MW MIPS C
    // Compiler" per both modules' .comment sections) — 0 for success is that toolchain's
    // convention here too, confirmed independently for SNDF's fno=0x1300 handler.
    public const uint SidSdReg = 0x80000701;
    private const uint SysmemAlloc = 1; // SifAllocIopHeap(size) -> addr
    private const uint SysmemFree = 2;  // SifFreeIopHeap(addr) -> result
    private const uint SysmemLoad = 3;  // SifLoadIopHeap(path, addr) -> result

    // Real ps2sdk IOP module loader RPC service (ee/kernel/src/loadfile.c: SifLoadFileInit
    // binds sid=0x80000006 unconditionally; verified against the real ps2sdk source,
    // github.com/ps2dev/ps2sdk). This is the service SifLoadModule/SifLoadModuleBuffer/
    // SifExecModuleBuffer all ultimately call through -- the real, general mechanism for "load
    // and start an IRX module", as opposed to the disc-file preload path
    // (Ps2System.PreloadIopModulesFromDisc) which only covers files already sitting under IOP/
    // on the mounted ISO. Games that bundle their own IRX modules inside EE-side data (loaded via
    // SifExecModuleBuffer, which DMAs the bytes to an IOP heap allocation via SidSysmem/
    // SysmemAlloc first, then calls this service's LF_F_MOD_BUF_LOAD) were completely unhandled --
    // this sid fell through to the generic "unknown service" fallback, which returns a bare `1`
    // and never populates the real 8-byte { result, modres } reply struct real callers read.
    // Function numbers and arg struct layout (common/include/loadfile-common.h) confirmed
    // directly against real ps2sdk source, not guessed.
    public const uint SidLoadFile = 0x80000006;
    private const uint LfModLoad = 0;           // SifLoadModule(path, ...) -- struct _lf_module_load_arg
    private const uint LfSearchModByName = 9;    // SifSearchModuleByName(name) -- struct _lf_search_module_by_name_arg
    private const uint LfModBufLoad = 6;         // SifLoadModuleBuffer/SifExecModuleBuffer -- struct _lf_module_buffer_load_arg
    private const int LfPathMax = 252;
    // Real IOP RAM module bytes are self-describing (ELF/IRX section headers carry their own
    // sizes) -- copying a generous upper bound and letting IrxLoader.Load parse only what it
    // actually needs avoids having to duplicate ELF-header size computation here. Real driver
    // IRX modules on this title's disc top out around 100KB (THREADMAN, the largest BIOS-
    // resident kernel module, is 36KB -- see RomdirExtractor's Phase 2 findings); 512KB comfortably
    // covers any real game-bundled module without reading past the 2MB IOP RAM window.
    private const int LfModuleCopyCap = 0x80000;

    // Simple bump allocator carved out of IOP RAM below the bind-scratch region (0x1F0000+),
    // so real titles calling SifAllocIopHeap during boot get back an address that's actually
    // theirs to use rather than colliding with anything else.
    private const uint IopHeapBase = 0x180000;
    private const uint IopHeapLimit = ScratchBase;
    private uint _iopHeapNext = IopHeapBase;

    // CD_SCMD function numbers (ee/rpc/cdvd/src/scmd.c + BIOS CDVDFSV)
    private const uint ScmdReadClock = 0x01;
    private const uint ScmdWriteClock = 0x02;
    private const uint ScmdGetDiskType = 0x03;
    private const uint ScmdGetError = 0x04;
    private const uint ScmdTrayReq = 0x05;
    private const uint ScmdApplySCmd = 0x07;
    private const uint ScmdStatus = 0x0C;
    private const uint ScmdBreak = 0x16;

    // CD_NCMD function numbers (ee/rpc/cdvd/src/ncmd.c + BIOS CDVDFSV)
    private const uint NcmdRead = 0x01;
    private const uint NcmdCddaRead = 0x02;
    private const uint NcmdDvdRead = 0x03;
    private const uint NcmdGetToc = 0x04;
    private const uint NcmdSeek = 0x05;
    private const uint NcmdStandby = 0x06;
    private const uint NcmdStop = 0x07;
    private const uint NcmdPause = 0x08;
    private const uint NcmdStream = 0x09;
    private const uint NcmdDiskReady = 0x0F;

    // FILEIO function numbers (ps2sdk common/include/fileio-common.h)
    private const uint FioOpen = 0;
    private const uint FioClose = 1;
    private const uint FioRead = 2;
    private const uint FioWrite = 3;
    private const uint FioLseek = 4;
    private const uint FioIoctl = 5;
    private const uint FioRemove = 6;
    private const uint FioMkdir = 7;
    private const uint FioRmdir = 8;
    private const uint FioDopen = 9;
    private const uint FioDclose = 10;
    private const uint FioDread = 11;
    private const uint FioGetstat = 12;
    private const uint FioChstat = 13;
    private const uint FioFormat = 14;

    // Reserved scratch region: top 64KB of the 2MB IOP RAM, used only for the
    // opaque cd->buf/cd->cbuf handles we hand back at bind time.
    private const uint ScratchBase = 0x1F0000;
    private const uint ScratchSlotSize = 0x400;
    private const int ScratchSlots = 64;

    private readonly Dictionary<uint, uint> _cdToSid = new();    // cd struct EE addr -> bound sid
    private readonly Dictionary<uint, uint> _cdToArgBuf = new(); // cd struct EE addr -> absolute arg-buffer addr
    private int _nextSlot;

    /// <summary>
    /// Active CRI DTX channels created via fno=2. Real CRI_ADXI.IRX owns matching IOP-side
    /// state and, after each EE→IOP SifSetDma of channel work, advances a completion counter
    /// in the EE work buffer so the EE DTX poll loop can clear its busy flag.
    /// Without that write-back the EE hard-spins with busy=1 forever (live-traced on Shaolin
    /// Monks: slot+1 stays 1, progress==counter, clear-path condition progress &lt; counter
    /// never holds). Tracked here so PerformSifSetDma can HLE the missing IOP completion.
    /// </summary>
    private readonly List<DtxChannel> _dtxChannels = new();

    private struct DtxChannel
    {
        public uint Handle;
        public uint Eewk;   // EE-side work buffer (may have 0x20000000 uncached bit)
        public uint IopWk;  // IOP RAM buffer the EE DMAs into
        public uint WkLen;
        /// <summary>Last counter value we wrote; used to avoid re-completing a still-pending unit.</summary>
        public uint LastCompleted;
    }

    public ulong Binds { get; private set; }
    public ulong Calls { get; private set; }
    public ulong RdataOps { get; private set; }
    public ulong FileIoOps { get; private set; }
    public ulong UnknownServiceCalls { get; private set; }
    public ulong UnknownBindSids { get; private set; }
    private readonly List<uint> _unknownSidsSeen = new();
    public IReadOnlyList<uint> UnknownSidsSeen => _unknownSidsSeen;

    public void Reset()
    {
        _cdToSid.Clear();
        _cdToArgBuf.Clear();
        _dtxChannels.Clear();
        _nextSlot = 0;
        _iopHeapNext = IopHeapBase;
        Binds = Calls = RdataOps = FileIoOps = UnknownServiceCalls = UnknownBindSids = 0;
        _unknownSidsSeen.Clear();
        _padAreas.Clear();
        _padFrame = 0;
    }

    /// <summary>In-flight bind state for SaveState.cs — _cdToSid/_cdToArgBuf track which
    /// service each EE-side "cd" struct is bound to and which scratch slot answers its calls;
    /// without this a save/load between a real sceSifBindRpc and its first sceSifCallRpc would
    /// resume with the binding forgotten, and the next call would look unbound.</summary>
    public void WriteState(BinaryWriter w)
    {
        w.Write(_cdToSid.Count);
        foreach (var kv in _cdToSid) { w.Write(kv.Key); w.Write(kv.Value); }
        w.Write(_cdToArgBuf.Count);
        foreach (var kv in _cdToArgBuf) { w.Write(kv.Key); w.Write(kv.Value); }
        w.Write(_nextSlot);
        w.Write(_iopHeapNext);
        w.Write(Binds); w.Write(Calls); w.Write(UnknownServiceCalls); w.Write(UnknownBindSids);
        w.Write(_unknownSidsSeen.Count);
        foreach (var sid in _unknownSidsSeen) w.Write(sid);
    }

    public void ReadState(BinaryReader r)
    {
        _cdToSid.Clear();
        int n1 = r.ReadInt32();
        for (int i = 0; i < n1; i++) { uint k = r.ReadUInt32(); uint v = r.ReadUInt32(); _cdToSid[k] = v; }
        _cdToArgBuf.Clear();
        int n2 = r.ReadInt32();
        for (int i = 0; i < n2; i++) { uint k = r.ReadUInt32(); uint v = r.ReadUInt32(); _cdToArgBuf[k] = v; }
        _nextSlot = r.ReadInt32();
        _iopHeapNext = r.ReadUInt32();
        Binds = r.ReadUInt64(); Calls = r.ReadUInt64(); UnknownServiceCalls = r.ReadUInt64(); UnknownBindSids = r.ReadUInt64();
        _unknownSidsSeen.Clear();
        int n3 = r.ReadInt32();
        for (int i = 0; i < n3; i++) _unknownSidsSeen.Add(r.ReadUInt32());
    }

    /// <summary>Recognizes and handles a real RPC bind/call/rdata packet. Returns false for
    /// anything else (caller falls back to existing system-cid / heuristic handling).</summary>
    public bool TryHandle(SystemMemory mem, KernelState kernel, Cdvd cdvd, PadInput pad, IopModuleHost iopModules, uint pktAddr)
    {
        uint cid = mem.Read32(pktAddr + 8);
        switch (cid)
        {
            case CidRpcBind: HandleBind(mem, kernel, pktAddr); return true;
            case CidRpcCall: HandleCall(mem, kernel, cdvd, pad, iopModules, pktAddr); return true;
            case CidRpcRdata: HandleRdata(mem, kernel, pktAddr); return true;
            default: return false;
        }
    }

    /// <summary>Cheap peek (no state mutation) so the caller can decide whether to queue
    /// this packet for real, IOP-tick-scheduled handling without processing it early.</summary>
    public static bool IsRealRpcPacket(SystemMemory mem, uint pktAddr)
    {
        uint cid = mem.Read32(pktAddr + 8);
        return cid == CidRpcBind || cid == CidRpcCall || cid == CidRpcRdata;
    }

    private uint AssignSlot()
    {
        uint off = ScratchBase + (uint)(_nextSlot % ScratchSlots) * ScratchSlotSize;
        _nextSlot++;
        return SystemMemory.IOP_RAM_BASE + off;
    }

    /// <summary>
    /// Called after an EE→IOP SifSetDma completes. If the destination lands in a tracked
    /// CRI DTX IOP workspace, advance that channel's EE completion counter so the EE-side
    /// DTX poll (progress &lt; counter) can clear its busy flag. Real CRI_ADXI does the
    /// equivalent from its IOP DTX thread after consuming the DMA'd payload.
    ///
    /// Only advances when the counter still equals what the EE just issued (counter has not
    /// already been pushed ahead of progress). Unconditional +1 on every matching DMA caused
    /// an infinite issue/complete thrash (sifBytes climbing unbounded, boot never left DTX).
    /// </summary>
    public void NotifyDtxEeToIopDma(SystemMemory mem, uint iopDest, uint size)
    {
        if (_dtxChannels.Count == 0 || size == 0) return;
        // Dest may be IOP-physical (0x000000-0x1FFFFF) or EE-visible IOP window (0x1C000000+).
        uint destPhys = iopDest & 0x1FFFFFu;
        for (int i = 0; i < _dtxChannels.Count; i++)
        {
            var ch = _dtxChannels[i];
            uint wkPhys = ch.IopWk & 0x1FFFFFu;
            uint wkLen = ch.WkLen != 0 ? ch.WkLen : 0x1000u;
            // Match if DMA dest overlaps the channel's IOP workspace (or its synthetic handle slot).
            uint handlePhys = ch.Handle & 0x1FFFFFu;
            bool hit = (destPhys >= wkPhys && destPhys < wkPhys + wkLen)
                       || (destPhys >= handlePhys && destPhys < handlePhys + ScratchSlotSize);
            if (!hit) continue;

            // EE DTX create (FUN_0041e988) points the live counter at
            //   uncached = (eewk + (wklen - 64)) | 0x20000000
            //   counter  = uncached + 0x3C
            // i.e. phys counter = eewk + wklen - 64 + 0x3C.
            uint eewk = ch.Eewk & 0x1FFFFFFFu;
            if (eewk == 0) continue;
            uint counterAddr = wkLen >= 64 ? eewk + (wkLen - 64) + 0x3Cu : eewk + 0x3Cu;
            uint completed = mem.Read32(counterAddr);
            // EE issue path: counter = progress; SifSetDma. Ack with counter = progress+1.
            // If we already pushed past `completed`, EE has not issued a new unit yet.
            if (ch.LastCompleted > completed)
                return;
            // Unbounded acks make the EE DTX issue path spin (sifBytes climb without bound)
            // because real IOP ring-buffer consumption / flow-control is not modelled. Budget
            // enough units for SJX_Init + early stream setup without infinite DMA thrash.
            const uint maxAutoComplete = 32;
            if (ch.LastCompleted >= maxAutoComplete)
                return;
            uint next = completed + 1;
            mem.Write32(counterAddr, next);
            ch.LastCompleted = next;
            _dtxChannels[i] = ch;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[RPC] DTX_COMPLETE eewk=0x{ch.Eewk:X8} counter=0x{counterAddr:X8} iopDest=0x{iopDest:X8} " +
                    $"completed {completed}->{next} size={size}");
            return;
        }
    }

    private void HandleBind(SystemMemory mem, KernelState kernel, uint pktAddr)
    {
        // SifRpcBindPkt_t (36B): +0 sifcmd(16) +16 rec_id +20 pkt_addr +24 rpc_id +28 cd(ptr) +32 sid
        uint cdPtr = mem.Read32(pktAddr + 28);
        uint sid = mem.Read32(pktAddr + 32);
        Binds++;
        if (cdPtr == 0)
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[RPC] HandleBind sid=0x{sid:X8} cdPtr=NULL (dropped, no reply) eePC=0x{SystemMemory.CurrentPcForWatch:X8}");
            return;
        }

        uint argBuf = AssignSlot();
        uint ctrlBuf = AssignSlot();
        _cdToSid[cdPtr] = sid;
        _cdToArgBuf[cdPtr] = argBuf;

        if (sid != SidCdScmd && sid != SidCdNcmd && sid != SidPad1 && sid != SidPad2 && sid != SidMcServ && sid != SidCdBase && sid != SidSysmem && sid != SidSndf && sid != SidCriAdx && sid != SidSdReg && sid != SidLoadFile && sid != SidSfsv && sid != SidFileIo)
        {
            UnknownBindSids++;
            _unknownSidsSeen.Add(sid);
        }

        // SifRpcClientData_t (40B): +8 hdr.sema_id, +20 buf, +24 cbuf, +36 server.
        // buf/cbuf/server are opaque handles the client only ever echoes back to us
        // in later Call packets — content just needs to round-trip, not mean anything.
        mem.Write32(cdPtr + 20, argBuf);
        mem.Write32(cdPtr + 24, ctrlBuf);
        mem.Write32(cdPtr + 36, sid);

        // BIOS SIFCMD.IRX FUN_00000c48 (Ghidra, tools/bios-decomp/SIFCMD_ALL.txt):
        // after filling server fields, IOP does SendCmd(SIF_CMD_RPC_END=0x80000008, reply, 0x40).
        // EE sifrpc.c's RPC_END handler then: SignalSema(cd->hdr.sema_id) + rpc_packet_free().
        // We collapse that IOP→EE DMA into the same two side-effects (docs/BIOS_DISSECTION.md §3).
        CompleteRpcEnd(mem, kernel, pktAddr, cdPtr);

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine($"[RPC] HandleBind sid=0x{sid:X8} cdPtr=0x{cdPtr:X8} semaId={mem.Read32(cdPtr + 8)} argBuf=0x{argBuf:X8} eePC=0x{SystemMemory.CurrentPcForWatch:X8}");
    }

    /// <summary>
    /// EE-side effects of IOP <c>SIF_CMD_RPC_END</c> (0x80000008).
    /// <para>
    /// Producers (BIOS SIFCMD.IRX, Ghidra): BIND <c>FUN_00000c48</c>, CALL completion
    /// <c>FUN_000013a4</c>, and RDATA <c>FUN_00000a68</c> all end in
    /// <c>SendCmd(0x80000008, reply, 0x40, …)</c>.
    /// Consumer (ps2sdk <c>ee/kernel/src/sifrpc.c</c> <c>_request_end</c>): for BIND, copy
    /// server/buf/cbuf into the client; always <c>iSignalSema(cd->hdr.sema_id)</c> if
    /// <c>sema_id &gt;= 0</c>; <c>rpc_packet_free(cd->hdr.pkt_addr)</c>; clear
    /// <c>cd->hdr.pkt_addr</c>.
    /// </para>
    /// We do not DMA a real IOP→EE packet; we apply the same EE state transitions so
    /// <c>WaitSema</c> + <c>sceSifCheckStatRpc</c> see a completed transfer.
    /// </summary>
    private static void CompleteRpcEnd(SystemMemory mem, KernelState kernel, uint pktAddr, uint cdPtr)
    {
        // Free the EE packet pool slot first (rpc_packet_free on the request packet).
        // PACKET_F_ALLOC = 0x01 in rec_id; rpc_id at +24. Also clear SIFCMD.cid so
        // sceSifCheckStatRpc sees a finished slot (not still BIND/CALL/RDATA).
        if (pktAddr != 0)
        {
            uint recId = mem.Read32(pktAddr + 16);
            mem.Write32(pktAddr + 16, recId & ~1u);
            mem.Write32(pktAddr + 24, 0);
            // SifCmdHeader_t.cid at +8 — BIOS reply stamps RPC_END then EE frees; we
            // collapse to "done" by writing RPC_END then clearing alloc.
            mem.Write32(pktAddr + 8, CidRpcEnd);
        }

        if (cdPtr == 0) return;

        // SifRpcClientData_t.hdr: +0 pkt_addr, +4 rpc_id, +8 sema_id
        // BIND path already wrote +20 buf, +24 cbuf, +36 server (HandleBind).
        mem.Write32(cdPtr + 0, 0); // hdr.pkt_addr = NULL after free

        int semaId = unchecked((int)mem.Read32(cdPtr + 8));
        // ps2sdk: if (cd->hdr.sema_id >= 0) iSignalSema — NOWAIT paths leave -1.
        if (semaId >= 0)
            kernel.ISignalSema(semaId);
    }

    /// <summary>
    /// BIOS SIFCMD RDATA handler (<c>FUN_00000a68</c>): DMA size bytes between EE/IOP
    /// addresses from the request packet, then <c>SendCmd(RPC_END)</c>.
    /// EE uses this for <c>sceSifGetOtherData</c> / buffer pulls off IOP RAM.
    /// Packet layout (after 16B SifCmdHeader): +0x10 rec_id … +0x1c client,
    /// +0x20 src, +0x24 dest, +0x28 size (Ghidra FUN_00000a68 → FUN_00000524 args).
    /// </summary>
    private void HandleRdata(SystemMemory mem, KernelState kernel, uint pktAddr)
    {
        RdataOps++;
        uint src = mem.Read32(pktAddr + 0x20);
        uint dest = mem.Read32(pktAddr + 0x24);
        uint size = mem.Read32(pktAddr + 0x28);
        // Cap to avoid pathological packets; real IOP DMA is also size-limited per transfer.
        if (size > 0 && size <= 0x100000 && src != 0 && dest != 0)
            CopyMemoryWindow(mem, src, dest, size);

        // RDATA completion uses the client pointer at +0x1c when present (same as BIND/CALL).
        uint cdPtr = mem.Read32(pktAddr + 0x1c);
        CompleteRpcEnd(mem, kernel, pktAddr, cdPtr);

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine(
                $"[RPC] HandleRdata src=0x{src:X8} dest=0x{dest:X8} size={size} cd=0x{cdPtr:X8}");
    }

    /// <summary>
    /// RDATA DMA: IOP source → EE destination (sceSifGetOtherData direction).
    /// Src may be bare IOP physical (0–2MB); dest is EE RDRAM (must not remap low EE
    /// addresses into the IOP window — RDRAM and IOP both use 0–2MB physical ranges).
    /// </summary>
    private static void CopyMemoryWindow(SystemMemory mem, uint src, uint dest, uint size)
    {
        uint s = NormalizeIopAddr(src);
        uint d = dest & 0x1FFFFFFFu; // EE phys / uncached strip only
        for (uint i = 0; i < size; i++)
            mem.Write8(d + i, mem.Read8(s + i));
    }

    /// <summary>Map bare IOP physical or KSEG IOP pointer into the EE IOP RAM window.</summary>
    private static uint NormalizeIopAddr(uint addr)
    {
        uint p = addr & 0x1FFFFFFFu;
        // Already in EE-visible IOP window
        if (p >= SystemMemory.IOP_RAM_BASE && p < SystemMemory.IOP_RAM_BASE + (uint)SystemMemory.IOP_RAM_SIZE)
            return p;
        // Bare IOP physical (0x000000–0x1FFFFF) — RDATA src convention
        if (p < (uint)SystemMemory.IOP_RAM_SIZE)
            return SystemMemory.IOP_RAM_BASE + p;
        return p;
    }

    private void HandleCall(SystemMemory mem, KernelState kernel, Cdvd cdvd, PadInput pad, IopModuleHost iopModules, uint pktAddr)
    {
        // SifRpcCallPkt_t (56B): +0 sifcmd(16) +16 rec_id +20 pkt_addr +24 rpc_id +28 cd(ptr)
        //   +32 rpc_number +36 send_size +40 recvbuf(ptr) +44 recv_size +48 rmode +52 sd(ptr)
        uint cdPtr = mem.Read32(pktAddr + 28);
        uint rpcNumber = mem.Read32(pktAddr + 32);
        uint sendSize = mem.Read32(pktAddr + 36);
        uint recvBuf = mem.Read32(pktAddr + 40);
        uint recvSize = mem.Read32(pktAddr + 44);
        Calls++;

        uint sid = _cdToSid.TryGetValue(cdPtr, out var s) ? s : 0;
        uint argBuf = _cdToArgBuf.TryGetValue(cdPtr, out var a) ? a : 0;

        // CRI Middleware ADX (CRI_ADXI.IRX, sid=0x90000200).
        // Live-traced (2026-07-29) on Shaolin Monks SJX_Init:
        //   bind sid=0x90000200 → client 0x53DDF0
        //   call fno=2 send=16B (id,eewk,iopwk,wklen) recv=4B → DTX_Create
        // The EE wrapper FUN_0041e830 returns *(u32*)recvBuf as the DTX handle; 0 is fatal
        // ("E0100302: SJX_Init can't create DTX" / "DTX_Create: can't create DTX of server").
        // Previous HLE *echoed* the send buffer into recv — so fno=2 returned id (often 0)
        // and SJX_Init hard-spun forever at 0x426BA8. Real fno 2/3 create server-side state
        // and return a non-zero handle/status; we allocate a synthetic IOP-side handle.
        // Other fno values still echo (real callback returns the caller's own buffer).
        if (sid == SidCriAdx)
        {
            if (rpcNumber is 2 or 3)
            {
                // DTX create (2) / related setup (3): non-zero handle in first recv dword.
                // AssignSlot returns EE-mappable IOP RAM so later calls can round-trip it.
                // Send layout (live-traced FUN_0041e830): +0 id, +4 eewk, +8 iopwk, +12 wklen.
                uint handle = AssignSlot();
                uint eewk = argBuf != 0 && sendSize >= 8 ? mem.Read32(argBuf + 4) : 0;
                uint iopWk = argBuf != 0 && sendSize >= 12 ? mem.Read32(argBuf + 8) : 0;
                uint wkLen = argBuf != 0 && sendSize >= 16 ? mem.Read32(argBuf + 12) : 0;
                // Prefer our synthetic handle as the IOP workspace when the caller passed 0 /
                // garbage; otherwise keep the caller's iopwk so later SifSetDma dest matches.
                if (iopWk == 0) iopWk = handle;
                if (rpcNumber == 2 && eewk != 0)
                {
                    _dtxChannels.Add(new DtxChannel
                    {
                        Handle = handle,
                        Eewk = eewk,
                        IopWk = iopWk,
                        WkLen = wkLen != 0 ? wkLen : 0x880,
                        LastCompleted = 0
                    });
                }
                if (recvBuf != 0 && recvSize >= 4)
                    mem.Write32(recvBuf, handle);
                // Real driver also stores the result into the request buffer's first word.
                if (argBuf != 0 && sendSize >= 4)
                    mem.Write32(argBuf, handle);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine(
                        $"[RPC] HandleCall sid=0x{sid:X8} fno=0x{rpcNumber:X} DTX_CREATE handle=0x{handle:X8} " +
                        $"eewk=0x{eewk:X8} iopwk=0x{iopWk:X8} recvBuf=0x{recvBuf:X8} eePC=0x{SystemMemory.CurrentPcForWatch:X8}");
            }
            else if (recvBuf != 0 && argBuf != 0)
            {
                uint echoLen = Math.Min(sendSize, recvSize);
                for (uint i = 0; i < echoLen; i++)
                    mem.Write8(recvBuf + i, mem.Read8(argBuf + i));
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine($"[RPC] HandleCall sid=0x{sid:X8} fno=0x{rpcNumber:X} recvBuf=0x{recvBuf:X8} ECHO({echoLen}B) eePC=0x{SystemMemory.CurrentPcForWatch:X8}");
            }
            CompleteRpcEnd(mem, kernel, pktAddr, cdPtr);
            return;
        }

        // LOADFILE (sid=0x80000006) — real BIOS LOADFILE.IRX registers this sid at init
        // (Ghidra FUN_000000c8). Replies with a { result, modres } pair.
        if (sid == SidLoadFile)
        {
            HandleLoadFile(mem, iopModules, rpcNumber, argBuf, recvBuf);
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[RPC] HandleCall sid=0x{sid:X8} fno=0x{rpcNumber:X} LOADFILE recvBuf=0x{recvBuf:X8} eePC=0x{SystemMemory.CurrentPcForWatch:X8}");
            CompleteRpcEnd(mem, kernel, pktAddr, cdPtr);
            return;
        }

        // FILEIO (sid=0x80000001) — BIOS FILEIO.IRX / sceOpen family.
        if (sid == SidFileIo)
        {
            int fioResult = HandleFileIo(mem, iopModules, pad, cdvd, rpcNumber, argBuf, sendSize, recvBuf, recvSize);
            if (recvBuf != 0 && recvSize >= 4)
                mem.Write32(recvBuf, unchecked((uint)fioResult));
            FileIoOps++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[RPC] HandleCall sid=FILEIO fno={rpcNumber} result={fioResult}");
            CompleteRpcEnd(mem, kernel, pktAddr, cdPtr);
            return;
        }

        int result = Dispatch(mem, cdvd, pad, sid, rpcNumber, argBuf, recvBuf);

        if (recvBuf != 0)
            mem.Write32(recvBuf, unchecked((uint)result));

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine($"[RPC] HandleCall sid=0x{sid:X8} fno=0x{rpcNumber:X} recvBuf=0x{recvBuf:X8} result={result} eePC=0x{SystemMemory.CurrentPcForWatch:X8}");

        // BIOS SIFCMD: CALL completion always ends in SendCmd(RPC_END=0x80000008) (Ghidra
        // FUN_000013a4). EE handler = SignalSema + rpc_packet_free — CompleteRpcEnd.
        CompleteRpcEnd(mem, kernel, pktAddr, cdPtr);
    }

    /// <summary>
    /// BIOS FILEIO RPC (sid=0x80000001). Function numbers match ps2sdk fileio-common.h.
    /// Backed by <see cref="IopModuleHost"/> ISO-aware open/read/stat/dir so commercial
    /// <c>sceOpen("cdrom0:...")</c> returns real disc bytes and directory probes work.
    /// </summary>
    private int HandleFileIo(SystemMemory mem, IopModuleHost iopModules, PadInput pad, Cdvd cdvd,
        uint fno, uint argBuf, uint sendSize, uint recvBuf, uint recvSize)
    {
        _ = pad; _ = cdvd; _ = recvSize;
        switch (fno)
        {
            case FioOpen:
            {
                uint pathAddr = argBuf;
                int mode = 0;
                if (argBuf != 0 && sendSize >= 4)
                {
                    uint maybePtr = mem.Read32(argBuf);
                    if (maybePtr >= 0x10000 && maybePtr < SystemMemory.RDRAM_SIZE)
                        pathAddr = maybePtr;
                    // Inline path[256] + mode at +252 common; also mode at +4 when ptr form
                    if (pathAddr == argBuf)
                        mode = sendSize >= 260 ? (int)mem.Read32(argBuf + 256) : 0;
                    else if (sendSize >= 8)
                        mode = (int)mem.Read32(argBuf + 4);
                }
                string path = pathAddr != 0 ? ReadCString(mem, pathAddr, 256) : "";
                int fd = iopModules.FileOpen(path, mode);
                if (recvBuf != 0) mem.Write32(recvBuf, unchecked((uint)fd));
                return fd;
            }
            case FioClose:
            {
                int fd = argBuf != 0 ? (int)mem.Read32(argBuf) : -1;
                return iopModules.FileClose(fd);
            }
            case FioRead:
            {
                int fd = argBuf != 0 ? (int)mem.Read32(argBuf) : -1;
                uint buf = argBuf != 0 && sendSize >= 8 ? mem.Read32(argBuf + 4) : recvBuf;
                uint size = argBuf != 0 && sendSize >= 12 ? mem.Read32(argBuf + 8)
                    : (recvSize != 0 ? recvSize : 0);
                if (buf == 0) buf = recvBuf;
                int n = iopModules.FileRead(mem, fd, buf, size);
                if (recvBuf != 0 && recvBuf != buf) mem.Write32(recvBuf, unchecked((uint)n));
                return n;
            }
            case FioWrite:
            {
                int fd = argBuf != 0 ? (int)mem.Read32(argBuf) : -1;
                uint buf = argBuf != 0 && sendSize >= 8 ? mem.Read32(argBuf + 4) : 0;
                uint size = argBuf != 0 && sendSize >= 12 ? mem.Read32(argBuf + 8) : 0;
                return iopModules.FileWrite(mem, fd, buf, size);
            }
            case FioLseek:
            {
                int fd = argBuf != 0 ? (int)mem.Read32(argBuf) : -1;
                int off = argBuf != 0 && sendSize >= 8 ? (int)mem.Read32(argBuf + 4) : 0;
                int whence = argBuf != 0 && sendSize >= 12 ? (int)mem.Read32(argBuf + 8) : 0;
                return iopModules.FileSeek(fd, off, whence);
            }
            case FioGetstat:
            {
                // path at arg, stat ptr at arg+4 or inline
                string path = "";
                uint statAddr = recvBuf;
                if (argBuf != 0)
                {
                    uint p0 = mem.Read32(argBuf);
                    if (p0 >= 0x10000 && p0 < SystemMemory.RDRAM_SIZE)
                    {
                        path = ReadCString(mem, p0, 256);
                        if (sendSize >= 8) statAddr = mem.Read32(argBuf + 4);
                    }
                    else
                    {
                        path = ReadCString(mem, argBuf, 256);
                        if (sendSize >= 260) statAddr = mem.Read32(argBuf + 256);
                    }
                }
                if (statAddr == 0) statAddr = recvBuf;
                return iopModules.FileGetStat(mem, path, statAddr);
            }
            case FioChstat:
                return 0;
            case FioRemove:
            {
                string path = argBuf != 0 ? ReadCString(mem, ResolvePathArg(mem, argBuf, sendSize), 256) : "";
                return iopModules.FileRemove(path);
            }
            case FioMkdir:
            case FioRmdir:
                return 0;
            case FioDopen:
            {
                string path = argBuf != 0 ? ReadCString(mem, ResolvePathArg(mem, argBuf, sendSize), 256) : "";
                int dfd = iopModules.DirOpen(path);
                if (recvBuf != 0) mem.Write32(recvBuf, unchecked((uint)dfd));
                return dfd;
            }
            case FioDclose:
            {
                int dfd = argBuf != 0 ? (int)mem.Read32(argBuf) : -1;
                return iopModules.DirClose(dfd);
            }
            case FioDread:
            {
                int dfd = argBuf != 0 ? (int)mem.Read32(argBuf) : -1;
                uint dirent = argBuf != 0 && sendSize >= 8 ? mem.Read32(argBuf + 4) : recvBuf;
                return iopModules.DirRead(mem, dfd, dirent);
            }
            case FioIoctl:
            case FioFormat:
                return 0;
            default:
                return 0;
        }
    }

    private static uint ResolvePathArg(SystemMemory mem, uint argBuf, uint sendSize)
    {
        if (argBuf == 0) return 0;
        uint maybe = mem.Read32(argBuf);
        if (maybe >= 0x10000 && maybe < SystemMemory.RDRAM_SIZE) return maybe;
        _ = sendSize;
        return argBuf;
    }

    /// <summary>Real LOADFILE service (sid=0x80000006). Handles the function numbers this
    /// title's boot actually calls; unhandled function numbers report a load failure (negative
    /// result) rather than a bare "success" -- unlike the single-int Dispatch fallback's "assume
    /// success" reasoning, a caller checking a genuine module-load result for success/failure is
    /// exactly the case where silently claiming success would be actively misleading (game code
    /// may branch on the returned module id).</summary>
    private void HandleLoadFile(SystemMemory mem, IopModuleHost iopModules, uint fno, uint argBuf, uint recvBuf)
    {
        int result;
        int modres = 0;
        switch (fno)
        {
            case LfModBufLoad:
            {
                uint ptr = argBuf != 0 ? mem.Read32(argBuf) : 0;
                result = TryLoadModuleFromMemory(mem, iopModules, ptr, null);
                break;
            }
            case LfModLoad:
            {
                string path = argBuf != 0 ? ReadCString(mem, argBuf + 8, LfPathMax) : "";
                string name = StripDevicePrefix(path);
                if (iopModules.TryGetModule(name, out int existingId))
                {
                    result = existingId;
                    break;
                }
                // Prefer real IRX bytes from mounted disc (cdrom0:IOP/FOO.IRX etc.)
                byte[]? discElf = iopModules.ReadDiscFileBytes(path);
                if (discElf != null && discElf.Length > 52 && discElf[0] == 0x7F && discElf[1] == (byte)'E')
                {
                    try
                    {
                        var lr = iopModules.LoadIrx(discElf, mem, name);
                        result = lr.Success && iopModules.TryGetModule(lr.ModuleName, out int mid) ? mid : -1;
                        break;
                    }
                    catch { /* fall through register */ }
                }
                result = iopModules.RegisterModule(name);
                break;
            }
            case LfSearchModByName:
            {
                string name = argBuf != 0 ? ReadCString(mem, argBuf + 8, LfPathMax) : "";
                result = iopModules.TryGetModule(StripDevicePrefix(name), out int foundId) ? foundId : -1;
                break;
            }
            case 1: // LF_F_ELF_LOAD — path at arg+8; register name as loaded "module"
            {
                string path = argBuf != 0 ? ReadCString(mem, argBuf + 8, LfPathMax) : "";
                string name = StripDevicePrefix(path);
                result = string.IsNullOrEmpty(name) ? -1 : iopModules.RegisterModule(name);
                break;
            }
            case 2: // LF_F_SET_Q_ADDR / set load address — accept
            case 3: // LF_F_GET_Q_ADDR
                result = 0;
                break;
            case 4: // LF_F_MGR_LOAD / memory-card related load — soft success
            case 5:
                result = 1;
                break;
            case 7: // LF_F_MOD_STOP
            case 8: // LF_F_MOD_UNLOAD
                result = 0;
                break;
            case 0xFF: // LF_F_GET_VERSION -- confirmed called live by this title (not part of
                // any public ps2sdk client wrapper; called directly). Unlike the module-load
                // functions below, a version query has no real "did the wrong thing load"
                // failure mode -- the risk calculus that justifies returning -1 for unhandled
                // load functions doesn't apply here, and an unexpected negative "version" is
                // exactly the kind of value that could make defensive client code refuse to use
                // the service at all. Plausible placeholder (encoded like a packed major.minor)
                // rather than a value shaped like an error.
                result = 0x00020000;
                break;

            default:
                // Remaining unmapped LOADFILE fnos: honest failure (negative id).
                result = -1;
                break;
        }
        if (recvBuf != 0)
        {
            mem.Write32(recvBuf, unchecked((uint)result));
            mem.Write32(recvBuf + 4, unchecked((uint)modres));
        }
    }

    /// <summary>Copies a generous window of real module bytes out of IOP RAM starting at
    /// <paramref name="ptr"/> and loads it through the existing, Phase-1/2-verified
    /// IrxLoader/IopModuleHost pipeline. Returns a positive module id on success, -1 on
    /// failure (bad pointer, load/relocation error).</summary>
    private int TryLoadModuleFromMemory(SystemMemory mem, IopModuleHost iopModules, uint ptr, string? nameOverride)
    {
        if (ptr < SystemMemory.IOP_RAM_BASE) return -1;
        uint offset = ptr - SystemMemory.IOP_RAM_BASE;
        if (offset >= SystemMemory.IOP_RAM_SIZE) return -1;
        int len = Math.Min(LfModuleCopyCap, SystemMemory.IOP_RAM_SIZE - (int)offset);
        var span = mem.GetIopRamSpan().Slice((int)offset, len);
        byte[] elf = span.ToArray();
        try
        {
            var r = iopModules.LoadIrx(elf, mem, nameOverride);
            return r.Success ? (iopModules.TryGetModule(r.ModuleName, out int id) ? id : 1) : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string ReadCString(SystemMemory mem, uint addr, int maxLen)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < maxLen; i++)
        {
            byte b = mem.Read8(addr + (uint)i);
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    /// <summary>Strips a PS2 device prefix ("cdrom0:", "rom0:", "host0:", etc.) and any
    /// trailing ";version" suffix, so the remaining path component reaches IopModuleHost's own
    /// NormalizeName (which only strips slashes/extension, not the colon-delimited device
    /// scheme) as a plain, comparable module name.</summary>
    private static string StripDevicePrefix(string path)
    {
        int colon = path.IndexOf(':');
        if (colon >= 0) path = path[(colon + 1)..];
        int semi = path.IndexOf(';');
        if (semi >= 0) path = path[..semi];
        return path;
    }

    private int Dispatch(SystemMemory mem, Cdvd cdvd, PadInput pad, uint sid, uint fno, uint argBuf, uint recvBuf)
    {
        switch (sid)
        {
            case SidSysmem:
                switch (fno)
                {
                    case SysmemAlloc:
                    {
                        uint reqSize = argBuf != 0 ? mem.Read32(argBuf) : 0;
                        uint aligned = (reqSize + 15u) & ~15u;
                        if (aligned == 0) aligned = 16;
                        uint addr = _iopHeapNext;
                        if (addr + aligned > IopHeapLimit) return 0; // heap exhausted: NULL, matches real SifAllocIopHeap's failure contract
                        _iopHeapNext += aligned;
                        // Reply buffer also gets the address (some clients read recv, some echo arg).
                        if (recvBuf != 0) mem.Write32(recvBuf, SystemMemory.IOP_RAM_BASE + addr);
                        return unchecked((int)(SystemMemory.IOP_RAM_BASE + addr));
                    }
                    case SysmemFree:
                        return 0; // bump allocator — no real free, but callers only check for failure (<0)
                    case SysmemLoad:
                        // SifLoadIopHeap(path, addr): treat as success; path load is LOADFILE's job.
                        return 0;
                    default:
                        return 1;
                }

            case SidCdBase:
                // CDVDMAN base/init-family (0x80000592). Init/ready-style calls return success.
                return fno switch
                {
                    0 => 1, // init
                    1 => (int)cdvd.DiscType,
                    2 => (int)cdvd.MechaconStatus,
                    _ => 1
                };

            case SidCdScmd:
                return fno switch
                {
                    ScmdReadClock => WriteCdClock(mem, recvBuf),
                    ScmdWriteClock => 1,
                    ScmdGetDiskType => (int)cdvd.DiscType,
                    ScmdGetError => 0, // SCECdErNO
                    ScmdTrayReq => cdvd.TrayOpen ? 1 : 0,
                    ScmdApplySCmd => 1,
                    ScmdStatus => cdvd.ReadPending ? 0x80 : (int)cdvd.MechaconStatus,
                    ScmdBreak => BreakCdvd(cdvd), // 0x16 — also used as layer-break probe in some builds; break wins
                    // sceCdSync (often via SCMD): 0 = complete, 1 = busy
                    0x08 => cdvd.SyncStatus,
                    0x09 => cdvd.SyncStatus,
                    0x0E => cdvd.DiscPresent ? 1 : 0,
                    0x15 => (int)cdvd.TocLeadOutSector,
                    0x17 => (int)cdvd.LayerBreakLba,
                    _ => 1
                };

            case SidCdNcmd:
                if (fno is NcmdRead or NcmdDvdRead or NcmdCddaRead)
                {
                    // ee/rpc/cdvd/src/ncmd.c sceCdRead-family: lbn, sectors, buf, mode…
                    uint lbn = argBuf != 0 ? mem.Read32(argBuf) : 0;
                    uint sectors = argBuf != 0 ? Math.Max(1u, mem.Read32(argBuf + 4)) : 1u;
                    uint bufAddr = argBuf != 0 ? mem.Read32(argBuf + 8) : 0;
                    // Synchronous fill inside RPC so WaitSema on CALL sees data ready.
                    // Also arm async state so late sceCdSync / status polls see ready.
                    uint ok = cdvd.ReadSectorsTo(mem, lbn, sectors, bufAddr);
                    return ok > 0 ? 1 : 0;
                }
                return fno switch
                {
                    NcmdGetToc => WriteCdToc(mem, recvBuf, cdvd),
                    NcmdSeek => 1,
                    NcmdStandby => 1,
                    NcmdStop => 1,
                    NcmdPause => 1,
                    NcmdStream => StartCdStream(cdvd, argBuf, mem),
                    NcmdDiskReady => cdvd.ReadPending ? 0 : 2, // 2 = SCECdComplete
                    0x0A => 1, // cdReadChain / readIOPm
                    0x0B => 1,
                    0x0C => 1, // power off
                    0x0D => 1,
                    0x0E => cdvd.ReadPending ? 0 : 2,
                    _ => 1
                };

            case SidPad1:
            case SidPad2:
                // libpad RPC fno (ps2sdk ee/rpc/pad): 0x01 open, 0x02 close, 0x03 setActuator,
                // 0x04 init, 0x05 getState/read, 0x06 setMMode, 0x07 infoAct, 0x08 infoComb,
                // 0x09 infoMode, 0x0C setVrefMode, 0x0D getPortMax, 0x0E getSlotMax, …
                return HandlePad(mem, pad, fno, argBuf, recvBuf);

            case SidMcServ:
                // libmc MCSERV (sid=0x80000400). Function numbers from ps2sdk libmc:
                // 0x00 init, 0x01 getInfo, 0x02 open, 0x03 close, 0x04 seek, 0x05 read,
                // 0x06 write, 0x07 flush, 0x0A format, 0x0C delete, 0x0D getDir, …
                return HandleMcServ(mem, fno, argBuf, recvBuf);

            case SidSndf:
                switch (fno & 0xFF00)
                {
                    case SndfInitAudio:
                    {
                        // Real handler: return -1 only if arg word 0 is literally zero,
                        // else 0 (success). We don't independently know what the game sent
                        // as word 0, but a shipped title's own init call sending 0 there
                        // (and thus intentionally failing its own audio init) is not a
                        // realistic scenario, so treat it as present/nonzero.
                        uint w0 = argBuf != 0 ? mem.Read32(argBuf) : 1;
                        return w0 == 0 ? -1 : 0;
                    }
                    default:
                        // Other SNDF_Driver commands (0xFE00/0xFD00/0xFC00/0xFB00/0x1B00/
                        // 0x1700/0xF900/0x0E00/0x0A00/0x0800/0x0700/0x1E00/0x0300/0x0200/
                        // 0x1600/0x0100 per the real dispatch table at real vaddr 0xBB80)
                        // aren't independently traced yet. 0 matches this module's own
                        // observed success convention better than this file's usual 1.
                        return 0;
                }

            case SidSdReg:
                return 0; // see SidSdReg's declaration comment

            case SidSfsv:
                return 0; // see SidSfsv's declaration comment

            default:
                // Generic fallback for a genuinely unrecognized service: return a
                // success-shaped 1 rather than 0. Untested assumption, but a defensible
                // one — many PS2 SDK RPC wrappers treat a 0 return as failure, and for a
                // module we don't understand at all, "pretend it succeeded and let the
                // caller carry on" risks less than "return a value that reads as failure
                // and makes the caller give up/halt", which is what we were observing.
                UnknownServiceCalls++;
                return 1;
        }
    }

    /// <summary>sceCdReadClock: fill SCECdCLOCK (8 bytes) with a stable synthetic RTC.</summary>
    private static int WriteCdClock(SystemMemory mem, uint recvBuf)
    {
        if (recvBuf == 0) return 1;
        // BCD-ish layout used by libcdvd: second, minute, hour, day, month, year…
        mem.Write8(recvBuf + 0, 0x00); // second
        mem.Write8(recvBuf + 1, 0x00); // minute
        mem.Write8(recvBuf + 2, 0x12); // hour
        mem.Write8(recvBuf + 3, 0x01); // day
        mem.Write8(recvBuf + 4, 0x01); // month
        mem.Write8(recvBuf + 5, 0x24); // year (2000+0x24)
        mem.Write8(recvBuf + 6, 0);
        mem.Write8(recvBuf + 7, 0);
        return 1;
    }

    /// <summary>sceCdGetToc: write a minimal single-track TOC into recvbuf.</summary>
    private static int WriteCdToc(SystemMemory mem, uint recvBuf, Cdvd cdvd)
    {
        if (recvBuf == 0) return 1;
        // Minimal 1024-byte TOC region: track count + lead-out style fields.
        mem.Write32(recvBuf + 0, cdvd.TocTracks);
        mem.Write32(recvBuf + 4, cdvd.TocLeadOutSector);
        mem.Write32(recvBuf + 8, cdvd.DiscType);
        mem.Write32(recvBuf + 12, cdvd.LayerBreakLba);
        return 1;
    }

    private static int BreakCdvd(Cdvd cdvd)
    {
        cdvd.CancelAsync();
        return 1;
    }

    private static int StartCdStream(Cdvd cdvd, uint argBuf, SystemMemory mem)
    {
        uint lba = argBuf != 0 ? mem.Read32(argBuf) : 0;
        return (int)cdvd.BeginStream(lba);
    }

    // libpad always calls sceSifCallRpc(..., rpc_number=1, ...); the real command is
    // buffer.command (PAD_RPCCMD_*). See ps2sdk ee/rpc/pad/src/libpad.c.
    private const uint PadRpcCmdOpenNew = 0x01;
    private const uint PadRpcCmdSetMModeNew = 0x06;
    private const uint PadRpcCmdSetActDirNew = 0x07;
    private const uint PadRpcCmdSetActAlignNew = 0x08;
    private const uint PadRpcCmdGetBtnMaskNew = 0x09;
    private const uint PadRpcCmdSetBtnInfoNew = 0x0A;
    private const uint PadRpcCmdSetVrefNew = 0x0B;
    private const uint PadRpcCmdGetPortMaxNew = 0x0C;
    private const uint PadRpcCmdGetSlotMaxNew = 0x0D;
    private const uint PadRpcCmdCloseNew = 0x0E;
    private const uint PadRpcCmdEndNew = 0x0F;
    private const uint PadRpcCmdInit = 0x10;
    private const uint PadRpcCmdGetModVer = 0x12;
    private const int PadStateStable = 6;
    private const int PadRstatComplete = 0;

    // padArea double-buffers of pad_data_new we last opened (port/slot -> EE padArea).
    private readonly Dictionary<uint, uint> _padAreas = new(); // (port<<8|slot) -> padArea
    private uint _padFrame;

    private int HandlePad(SystemMemory mem, PadInput pad, uint fno, uint argBuf, uint recvBuf)
    {
        // fno is almost always 1; command lives in arg buffer word 0.
        // Command is arg buffer word 0 (PAD_RPCCMD_*). RPC number is always 1 for libpad.
        uint cmd = argBuf != 0 ? mem.Read32(argBuf) : fno;
        if (cmd == 0)
            cmd = fno != 0 ? fno : PadRpcCmdOpenNew;

        int result = 1;
        switch (cmd)
        {
            case PadRpcCmdInit: // 0x10 — padPortInit: result at +0x0C, needs openSlot DMA
            {
                // padInitArgs.statBuf at +0x10 — open_slot[2] buffer for connection status
                uint statBuf = argBuf != 0 ? mem.Read32(argBuf + 0x10) : 0;
                if (statBuf != 0 && statBuf < SystemMemory.RDRAM_SIZE - 0x100)
                {
                    // open_slot: frame, openSlots[2], padding — mark both ports connected (bit0)
                    mem.Write32(statBuf + 0, ++_padFrame);
                    mem.Write32(statBuf + 4, 0x01); // port0 slot0 open
                    mem.Write32(statBuf + 8, 0x01); // port1 slot0 open
                    // Second buffer half (double-buffer)
                    mem.Write32(statBuf + 0x80, _padFrame);
                    mem.Write32(statBuf + 0x84, 0x01);
                    mem.Write32(statBuf + 0x88, 0x01);
                }
                result = 1;
                WritePadResult(mem, argBuf, recvBuf, result);
                break;
            }
            case PadRpcCmdOpenNew: // 0x01
            case 0x80000100: // PAD_RPCCMD_OPEN_OLD
            {
                // padOpenArgs: +0 cmd, +4 port, +8 slot, +0xC unk, +0x10 padArea
                int port = argBuf != 0 ? (int)mem.Read32(argBuf + 4) : 0;
                int slot = argBuf != 0 ? (int)mem.Read32(argBuf + 8) : 0;
                uint padArea = argBuf != 0 ? mem.Read32(argBuf + 0x10) : 0;
                if (padArea != 0 && padArea < SystemMemory.RDRAM_SIZE - 0x200)
                {
                    uint key = ((uint)port << 8) | (uint)(slot & 0xFF);
                    _padAreas[key] = padArea;
                    InitPadArea(mem, pad, padArea);
                    // padOpenResult: result @+0x0C, padBuf @+0x14 (libpad.c)
                    result = 1;
                    if (argBuf != 0)
                    {
                        mem.Write32(argBuf + 0x0C, 1);
                        mem.Write32(argBuf + 0x14, padArea);
                    }
                    if (recvBuf != 0)
                    {
                        mem.Write32(recvBuf + 0x0C, 1);
                        mem.Write32(recvBuf + 0x14, padArea);
                    }
                }
                else result = 0;
                WritePadResult(mem, argBuf, recvBuf, result);
                break;
            }
            case PadRpcCmdCloseNew:
            case 0x8000010D:
            case PadRpcCmdEndNew:
            case 0x8000010E:
                result = 1;
                WritePadResult(mem, argBuf, recvBuf, result);
                break;
            case PadRpcCmdGetPortMaxNew:
            case 0x8000010B:
                result = 2;
                WritePadResult(mem, argBuf, recvBuf, result);
                break;
            case PadRpcCmdGetSlotMaxNew:
            case 0x8000010C:
                result = 1;
                WritePadResult(mem, argBuf, recvBuf, result);
                break;
            case PadRpcCmdGetModVer:
                result = 0x0300;
                WritePadResult(mem, argBuf, recvBuf, result);
                break;
            case PadRpcCmdSetMModeNew:
            case PadRpcCmdSetActDirNew:
            case PadRpcCmdSetActAlignNew:
            case PadRpcCmdGetBtnMaskNew:
            case PadRpcCmdSetBtnInfoNew:
            case PadRpcCmdSetVrefNew:
            case 0x80000102:
            case 0x80000103:
            case 0x80000104:
            case 0x80000105:
            case 0x80000106:
            case 0x80000107:
            case 0x80000108:
            case 0x80000109:
            case 0x8000010A:
                result = 1;
                WritePadResult(mem, argBuf, recvBuf, result);
                // Keep DMA pad state STABLE after mode changes
                RefreshAllPadAreas(mem, pad);
                break;
            default:
                // fno=1 with unknown command word: treat as open/init success
                result = 1;
                WritePadResult(mem, argBuf, recvBuf, result);
                RefreshAllPadAreas(mem, pad);
                break;
        }

        // Always refresh open pad DMA buffers so padGetState (EE-side, not RPC) sees STABLE
        RefreshAllPadAreas(mem, pad);
        return result;
    }

    private static void WritePadResult(SystemMemory mem, uint argBuf, uint recvBuf, int result)
    {
        // padResult.result at +0x0C in the reply buffer (libpad.c)
        if (argBuf != 0) mem.Write32(argBuf + 0x0C, unchecked((uint)result));
        if (recvBuf != 0)
        {
            mem.Write32(recvBuf, unchecked((uint)result)); // also first dword for generic Dispatch
            mem.Write32(recvBuf + 0x0C, unchecked((uint)result));
        }
    }

    /// <summary>
    /// pad_data_new offsets (ps2sdk libpad.c) — double-buffered, 256B stride per SyncDCache.
    /// data[32]@0, actDir@0x20, actAlign@0x28, actData@0x30, modeTable@0x50,
    /// frame@0x58, findPadRetries@0x5C, length@0x60, modeConfig@0x64, modeCurId@0x65,
    /// model@0x66, buttonDataReady@0x67, state@0x70, reqState@0x71, currentTask@0x72.
    /// </summary>
    private void InitPadArea(SystemMemory mem, PadInput pad, uint padArea)
    {
        for (int i = 0; i < 2; i++)
            WritePadDataNew(mem, pad, padArea + (uint)(i * 256), preferHigherFrame: i == 1);
    }

    private void WritePadDataNew(SystemMemory mem, PadInput pad, uint baseP, bool preferHigherFrame)
    {
        _padFrame++;
        uint frame = preferHigherFrame ? _padFrame + 1 : _padFrame;
        // Button report at data[0..31] — dualshock: hi-nibble id, lo buttons active-low
        for (uint o = 0; o < 32; o++)
            mem.Write8(baseP + o, 0xFF);
        pad.WriteStatusBuffer(mem, baseP);
        mem.Write8(baseP + 0, 0x00); // success
        mem.Write8(baseP + 1, 0x79); // analog dualshock
        // length of button payload often at data+2 for some readers; keep digital mask:
        // WriteStatusBuffer wrote buttons at +2/+3

        // pad_data_new metadata (ps2sdk exact offsets)
        mem.Write32(baseP + 0x58, frame);
        mem.Write32(baseP + 0x5C, 0); // findPadRetries
        mem.Write32(baseP + 0x60, 32); // length
        mem.Write8(baseP + 0x64, 1); // modeConfig
        mem.Write8(baseP + 0x65, 0x07); // modeCurId dualshock
        mem.Write8(baseP + 0x66, 3); // model
        mem.Write8(baseP + 0x67, 1); // buttonDataReady
        mem.Write8(baseP + 0x68, 1); // nrOfModes
        mem.Write8(baseP + 0x70, (byte)PadStateStable);
        mem.Write8(baseP + 0x71, (byte)PadRstatComplete);
        mem.Write8(baseP + 0x72, 1); // currentTask = 1 (ready)
        mem.Write8(baseP + 0x73, 0); // runTask
    }

    private void RefreshAllPadAreas(SystemMemory mem, PadInput pad)
    {
        foreach (var kv in _padAreas)
        {
            uint padArea = kv.Value;
            if (padArea == 0 || padArea >= SystemMemory.RDRAM_SIZE - 0x200) continue;
            // Bump the higher-frame buffer so padGetDmaStrNew picks it
            WritePadDataNew(mem, pad, padArea, preferHigherFrame: false);
            WritePadDataNew(mem, pad, padArea + 256, preferHigherFrame: true);
        }
    }

    /// <summary>
    /// IOP PADMAN continuous update — padGetState/padRead are EE-side DMA buffer polls,
    /// not RPC. Call once per VBlank so STABLE + button data stay live.
    /// </summary>
    public void TickPadDma(SystemMemory mem, PadInput pad)
    {
        if (_padAreas.Count == 0) return;
        RefreshAllPadAreas(mem, pad);
    }

    /// <summary>
    /// MCSERV RPC (sid=0x80000400). Enough of libmc for boot probes: init, getInfo,
    /// open/close/read/write/seek — return codes match "card present / formatted".
    /// </summary>
    private int HandleMcServ(SystemMemory mem, uint fno, uint argBuf, uint recvBuf)
    {
        switch (fno)
        {
            case 0x00: // mcInit
                return 0;
            case 0x01: // mcGetInfo — type/free/format into recv or arg
                // type=2 (PS2), free clusters high, format=1
                if (recvBuf != 0)
                {
                    mem.Write32(recvBuf + 0, 2);       // type PS2
                    mem.Write32(recvBuf + 4, 8000);    // free
                    mem.Write32(recvBuf + 8, 1);       // formatted
                }
                if (argBuf != 0 && argBuf != recvBuf)
                {
                    // Some clients pass out-ptrs in arg: +0 port, +4 slot, +8 *type, +C *free, +10 *format
                    uint typePtr = mem.Read32(argBuf + 8);
                    uint freePtr = mem.Read32(argBuf + 12);
                    uint fmtPtr = mem.Read32(argBuf + 16);
                    if (typePtr != 0) mem.Write32(typePtr, 2);
                    if (freePtr != 0) mem.Write32(freePtr, 8000);
                    if (fmtPtr != 0) mem.Write32(fmtPtr, 1);
                }
                return 0;
            case 0x02: // mcOpen
                if (recvBuf != 0) mem.Write32(recvBuf, 1); // fd=1
                return 1;
            case 0x03: // mcClose
                return 0;
            case 0x04: // mcSeek
                return argBuf != 0 ? (int)mem.Read32(argBuf + 4) : 0;
            case 0x05: // mcRead — zero-fill buffer
                {
                    uint buf = argBuf != 0 ? mem.Read32(argBuf + 4) : recvBuf;
                    int size = argBuf != 0 ? (int)mem.Read32(argBuf + 8) : 0;
                    size = Math.Clamp(size, 0, 0x10000);
                    for (int i = 0; i < size && buf != 0; i++)
                        mem.Write8(buf + (uint)i, 0);
                    return size;
                }
            case 0x06: // mcWrite
                return argBuf != 0 ? (int)mem.Read32(argBuf + 8) : 0;
            case 0x07: // mcFlush
            case 0x08: // mcMkDir
            case 0x09: // mcChDir
            case 0x0A: // mcFormat
            case 0x0B: // mcUnformat
            case 0x0C: // mcDelete
            case 0x0E: // mcSetInfo
            case 0x0F: // mcRename
            case 0x14: // mcSync
                return 0;
            case 0x0D: // mcGetDir — write 0 entries
                if (recvBuf != 0) mem.Write32(recvBuf, 0);
                return 0;
            default:
                return 0;
        }
    }
}
