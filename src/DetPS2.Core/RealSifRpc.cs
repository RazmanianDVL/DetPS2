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

    // Known real service ids (sid) bound by retail libcdvd/libpad/libmc.
    public const uint SidCdScmd = 0x80000593; // sync CDVD status calls
    public const uint SidCdNcmd = 0x80000595; // async CDVD read/seek — the real disc-data path
    public const uint SidPad1 = 0x80000100;
    public const uint SidPad2 = 0x80000101;
    public const uint SidMcServ = 0x80000400;
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

    // CD_SCMD function numbers (ee/rpc/cdvd/src/scmd.c)
    private const uint ScmdReadClock = 0x01;
    private const uint ScmdGetDiskType = 0x03;
    private const uint ScmdTrayReq = 0x05;
    private const uint ScmdStatus = 0x0C;

    // CD_NCMD function numbers (ee/rpc/cdvd/src/ncmd.c)
    private const uint NcmdRead = 0x01;
    private const uint NcmdDvdRead = 0x03;
    private const uint NcmdSeek = 0x05;

    // Reserved scratch region: top 64KB of the 2MB IOP RAM, used only for the
    // opaque cd->buf/cd->cbuf handles we hand back at bind time.
    private const uint ScratchBase = 0x1F0000;
    private const uint ScratchSlotSize = 0x400;
    private const int ScratchSlots = 64;

    private readonly Dictionary<uint, uint> _cdToSid = new();    // cd struct EE addr -> bound sid
    private readonly Dictionary<uint, uint> _cdToArgBuf = new(); // cd struct EE addr -> absolute arg-buffer addr
    private int _nextSlot;

    public ulong Binds { get; private set; }
    public ulong Calls { get; private set; }
    public ulong UnknownServiceCalls { get; private set; }
    public ulong UnknownBindSids { get; private set; }
    private readonly List<uint> _unknownSidsSeen = new();
    public IReadOnlyList<uint> UnknownSidsSeen => _unknownSidsSeen;

    public void Reset()
    {
        _cdToSid.Clear();
        _cdToArgBuf.Clear();
        _nextSlot = 0;
        _iopHeapNext = IopHeapBase;
        Binds = Calls = UnknownServiceCalls = UnknownBindSids = 0;
        _unknownSidsSeen.Clear();
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

    /// <summary>Recognizes and handles a real RPC bind/call packet. Returns false for
    /// anything else (caller falls back to existing system-cid / heuristic handling).</summary>
    public bool TryHandle(SystemMemory mem, KernelState kernel, Cdvd cdvd, PadInput pad, IopModuleHost iopModules, uint pktAddr)
    {
        uint cid = mem.Read32(pktAddr + 8);
        switch (cid)
        {
            case CidRpcBind: HandleBind(mem, kernel, pktAddr); return true;
            case CidRpcCall: HandleCall(mem, kernel, cdvd, pad, iopModules, pktAddr); return true;
            default: return false;
        }
    }

    /// <summary>Cheap peek (no state mutation) so the caller can decide whether to queue
    /// this packet for real, IOP-tick-scheduled handling without processing it early.</summary>
    public static bool IsRealRpcPacket(SystemMemory mem, uint pktAddr)
    {
        uint cid = mem.Read32(pktAddr + 8);
        return cid == CidRpcBind || cid == CidRpcCall;
    }

    private uint AssignSlot()
    {
        uint off = ScratchBase + (uint)(_nextSlot % ScratchSlots) * ScratchSlotSize;
        _nextSlot++;
        return SystemMemory.IOP_RAM_BASE + off;
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

        if (sid != SidCdScmd && sid != SidCdNcmd && sid != SidPad1 && sid != SidPad2 && sid != SidMcServ && sid != SidCdBase && sid != SidSysmem && sid != SidSndf && sid != SidCriAdx && sid != SidSdReg && sid != SidLoadFile && sid != SidSfsv)
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

        uint semaId = mem.Read32(cdPtr + 8);
        if (semaId != 0) kernel.SignalSema((int)semaId);

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine($"[RPC] HandleBind sid=0x{sid:X8} cdPtr=0x{cdPtr:X8} semaId={semaId} argBuf=0x{argBuf:X8} eePC=0x{SystemMemory.CurrentPcForWatch:X8}");

        // Same pool-leak fix as HandleCall — bind packets come from the same
        // rpc_data->pkt_table and must be released the same way.
        uint recId = mem.Read32(pktAddr + 16);
        mem.Write32(pktAddr + 16, recId & ~1u);
        mem.Write32(pktAddr + 24, 0);
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

        // CRI Middleware's ADX codec driver (CRI_ADXI.IRX, sid=0x90000200) — extracted and
        // disassembled since CRI never published this integration's RPC wire protocol. Its
        // registered callback (real vaddr 0x720) special-cases fno 2 and 3 (each validates 3
        // packed request words for alignment via `andi ...,0x1F`/`0x3F`, erroring through a
        // logging helper on failure, then calls a further processing helper whose result gets
        // stored back into the request buffer's own first word before the callback loops to
        // process what looks like a queue of further entries — real vaddr 0x9D0/0xAE8, not
        // fully traced). Every other fno (observed: 0x400/0x403/0x40A/0x40C/0x422, i.e.
        // "0x400 + subcommand") falls through a chain of not-taken branches straight to the
        // exit, where v0 still holds the delay-slot-set `s0` (the callback's own `buffer`
        // argument) — real SifRpcFunc_t callbacks return a pointer to their reply data, so for
        // these fno values the real reply IS the caller's own request buffer, unmodified.
        // Extended to also cover fno 2/3 here (2026-07-28): this title's boot creates a
        // dedicated worker thread that polls CRI ADX init to completion (see
        // DEVELOPER_GUIDE.md §7.24's decompiled FUN_004147f8/FUN_00414d40) via exactly these
        // two function numbers, and the previous behavior (falling to this file's generic
        // "unknown service -> hardcoded 1" fallback) overwrote request-buffer bytes the real
        // driver reads back on its own next queued-entry iteration with a value that has no
        // relationship to the real 0x9D0/0xAE8 processing result. Echoing the caller's own data
        // back unchanged doesn't correctly emulate the real validation+processing logic, but is
        // a strictly smaller deviation from real behavior than substituting an unrelated fixed
        // int over data the real driver would have read on its next pass.
        if (sid == SidCriAdx && recvBuf != 0 && argBuf != 0)
        {
            uint echoLen = Math.Min(sendSize, recvSize);
            for (uint i = 0; i < echoLen; i++)
                mem.Write8(recvBuf + i, mem.Read8(argBuf + i));
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[RPC] HandleCall sid=0x{sid:X8} fno=0x{rpcNumber:X} recvBuf=0x{recvBuf:X8} ECHO({echoLen}B) eePC=0x{SystemMemory.CurrentPcForWatch:X8}");
            uint echoSemaId = cdPtr != 0 ? mem.Read32(cdPtr + 8) : 0;
            if (echoSemaId != 0) kernel.SignalSema((int)echoSemaId);
            uint echoRecId = mem.Read32(pktAddr + 16);
            mem.Write32(pktAddr + 16, echoRecId & ~1u);
            mem.Write32(pktAddr + 24, 0);
            return;
        }

        // LOADFILE (sid=0x80000006) — real ps2sdk replies with a { result, modres } pair
        // (8 bytes, struct _lf_module_buffer_load_arg's `p`/`q` union fields), not the single
        // int word every other service here returns, so it's special-cased directly rather
        // than routed through Dispatch. See SidLoadFile's declaration comment for the source
        // verification and HandleLoadFile for the per-function-number behavior.
        if (sid == SidLoadFile)
        {
            HandleLoadFile(mem, iopModules, rpcNumber, argBuf, recvBuf);
            uint lfSemaId = cdPtr != 0 ? mem.Read32(cdPtr + 8) : 0;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[RPC] HandleCall sid=0x{sid:X8} fno=0x{rpcNumber:X} LOADFILE recvBuf=0x{recvBuf:X8} semaId={lfSemaId} eePC=0x{SystemMemory.CurrentPcForWatch:X8}");
            if (lfSemaId != 0) kernel.SignalSema((int)lfSemaId);
            uint lfRecId = mem.Read32(pktAddr + 16);
            mem.Write32(pktAddr + 16, lfRecId & ~1u);
            mem.Write32(pktAddr + 24, 0);
            return;
        }

        int result = Dispatch(mem, cdvd, pad, sid, rpcNumber, argBuf, recvBuf);

        if (recvBuf != 0)
            mem.Write32(recvBuf, unchecked((uint)result));

        uint semaId = cdPtr != 0 ? mem.Read32(cdPtr + 8) : 0;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine($"[RPC] HandleCall sid=0x{sid:X8} fno=0x{rpcNumber:X} recvBuf=0x{recvBuf:X8} result={result} semaId={semaId} eePC=0x{SystemMemory.CurrentPcForWatch:X8}");
        if (semaId != 0) kernel.SignalSema((int)semaId);

        // Real ps2sdk only releases this packet back to its fixed-size EE-side pool
        // (_rpc_get_packet's rpc_data->pkt_table) when the IOP's SIF_CMD_RPC_END reply comes
        // back through a registered command handler that calls rpc_packet_free(). We answer
        // calls synchronously and never send that reply, so without this the pool silently
        // leaks one slot per call — after ~pkt_table_len calls, EVERY subsequent
        // sceSifCallRpc/BindRpc silently fails at packet allocation (returns -E_SIF_PKT_ALLOC)
        // before ever attempting to send, which looks identical to a real transport failure.
        // Mirror rpc_packet_free() directly: clear PACKET_F_ALLOC (0x01) in rec_id (+16) and
        // zero rpc_id (+24) — see sifrpc.c's rpc_packet_free()/_rpc_get_packet().
        uint recId = mem.Read32(pktAddr + 16);
        mem.Write32(pktAddr + 16, recId & ~1u);
        mem.Write32(pktAddr + 24, 0);
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
                result = iopModules.TryGetModule(name, out int existingId)
                    ? existingId
                    : iopModules.RegisterModule(name);
                break;
            }
            case LfSearchModByName:
            {
                string name = argBuf != 0 ? ReadCString(mem, argBuf + 8, LfPathMax) : "";
                result = iopModules.TryGetModule(StripDevicePrefix(name), out int foundId) ? foundId : -1;
                break;
            }
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
                // LF_F_ELF_LOAD/SET_ADDR/GET_ADDR/MG_*/MOD_STOP/MOD_UNLOAD/SEARCH_BY_ADDRESS --
                // not observed in this title's boot path, not independently verified. Negative
                // (failure) rather than this file's usual optimistic "1": real callers of a
                // module-load-shaped RPC branch on success/failure of the returned id/result,
                // so a wrong-but-positive value risks worse downstream behavior than an honest
                // "didn't handle this" failure.
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
                        return unchecked((int)(SystemMemory.IOP_RAM_BASE + addr));
                    }
                    case SysmemFree:
                        return 0; // bump allocator — no real free, but callers only check for failure (<0)
                    default:
                        return 1; // SysmemLoad and anything else unmapped — see SidCdScmd comment
                }

            case SidCdBase:
                // Exact function-number table for this sid isn't independently confirmed —
                // real CDVDMAN's 0x592 family member is likely a base/init-style service.
                // Answer with a generic "ok" (1) rather than the unknown-service 0, on the
                // hypothesis that this caller's own logic treats 0 as failure and halts.
                return 1;

            case SidCdScmd:
                return fno switch
                {
                    ScmdGetDiskType => (int)cdvd.DiscType,
                    ScmdTrayReq => cdvd.TrayOpen ? 1 : 0,
                    ScmdStatus => (int)cdvd.MechaconStatus,
                    ScmdReadClock => 0,
                    _ => 1 // unmapped function number on a known service — same "0 reads as
                           // failure" reasoning as the top-level unknown-service fallback
                };

            case SidCdNcmd:
                if (fno is NcmdRead or NcmdDvdRead)
                {
                    // ee/rpc/cdvd/src/ncmd.c sceCdRead-family arg layout: lbn(u32), sectors(u32), bufaddr(ptr).
                    uint lbn = argBuf != 0 ? mem.Read32(argBuf) : 0;
                    uint sectors = argBuf != 0 ? Math.Max(1u, mem.Read32(argBuf + 4)) : 1u;
                    uint bufAddr = argBuf != 0 ? mem.Read32(argBuf + 8) : 0;
                    sectors = Math.Min(sectors, 64u);
                    uint ok = 0;
                    for (uint i = 0; i < sectors; i++)
                    {
                        if (!cdvd.ReadSector(lbn + i)) break;
                        if (bufAddr != 0) cdvd.CopySectorToMemory(mem, bufAddr + i * 2048);
                        ok++;
                    }
                    return ok > 0 ? 1 : 0;
                }
                if (fno == NcmdSeek) return 1;
                return 1; // unmapped fno — see SidCdScmd comment

            case SidPad1:
            case SidPad2:
                // Real libpad has many function numbers (open/read/state/etc.) we haven't
                // individually verified — answer every call with current pad state, a safe
                // default that keeps titles moving instead of hanging on an unmapped fno.
                if (recvBuf != 0) pad.WriteStatusBuffer(mem, recvBuf);
                return (int)pad.Buttons;

            case SidMcServ:
                return 1; // unmapped fno — see SidCdScmd comment

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
}
