using System;
using System.Collections.Generic;

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

    public void Reset()
    {
        _cdToSid.Clear();
        _cdToArgBuf.Clear();
        _nextSlot = 0;
        Binds = Calls = UnknownServiceCalls = UnknownBindSids = 0;
    }

    /// <summary>Recognizes and handles a real RPC bind/call packet. Returns false for
    /// anything else (caller falls back to existing system-cid / heuristic handling).</summary>
    public bool TryHandle(SystemMemory mem, KernelState kernel, Cdvd cdvd, PadInput pad, uint pktAddr)
    {
        uint cid = mem.Read32(pktAddr + 8);
        switch (cid)
        {
            case CidRpcBind: HandleBind(mem, kernel, pktAddr); return true;
            case CidRpcCall: HandleCall(mem, kernel, cdvd, pad, pktAddr); return true;
            default: return false;
        }
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
        if (cdPtr == 0) return;

        uint argBuf = AssignSlot();
        uint ctrlBuf = AssignSlot();
        _cdToSid[cdPtr] = sid;
        _cdToArgBuf[cdPtr] = argBuf;

        if (sid != SidCdScmd && sid != SidCdNcmd && sid != SidPad1 && sid != SidPad2 && sid != SidMcServ)
            UnknownBindSids++;

        // SifRpcClientData_t (40B): +8 hdr.sema_id, +20 buf, +24 cbuf, +36 server.
        // buf/cbuf/server are opaque handles the client only ever echoes back to us
        // in later Call packets — content just needs to round-trip, not mean anything.
        mem.Write32(cdPtr + 20, argBuf);
        mem.Write32(cdPtr + 24, ctrlBuf);
        mem.Write32(cdPtr + 36, sid);

        uint semaId = mem.Read32(cdPtr + 8);
        if (semaId != 0) kernel.SignalSema((int)semaId);
    }

    private void HandleCall(SystemMemory mem, KernelState kernel, Cdvd cdvd, PadInput pad, uint pktAddr)
    {
        // SifRpcCallPkt_t (56B): +0 sifcmd(16) +16 rec_id +20 pkt_addr +24 rpc_id +28 cd(ptr)
        //   +32 rpc_number +36 send_size +40 recvbuf(ptr) +44 recv_size +48 rmode +52 sd(ptr)
        uint cdPtr = mem.Read32(pktAddr + 28);
        uint rpcNumber = mem.Read32(pktAddr + 32);
        uint recvBuf = mem.Read32(pktAddr + 40);
        Calls++;

        uint sid = _cdToSid.TryGetValue(cdPtr, out var s) ? s : 0;
        uint argBuf = _cdToArgBuf.TryGetValue(cdPtr, out var a) ? a : 0;

        int result = Dispatch(mem, cdvd, pad, sid, rpcNumber, argBuf, recvBuf);

        if (recvBuf != 0)
            mem.Write32(recvBuf, unchecked((uint)result));

        uint semaId = cdPtr != 0 ? mem.Read32(cdPtr + 8) : 0;
        if (semaId != 0) kernel.SignalSema((int)semaId);
    }

    private int Dispatch(SystemMemory mem, Cdvd cdvd, PadInput pad, uint sid, uint fno, uint argBuf, uint recvBuf)
    {
        switch (sid)
        {
            case SidCdScmd:
                return fno switch
                {
                    ScmdGetDiskType => (int)cdvd.DiscType,
                    ScmdTrayReq => cdvd.TrayOpen ? 1 : 0,
                    ScmdStatus => (int)cdvd.MechaconStatus,
                    ScmdReadClock => 0,
                    _ => 0
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
                return 0;

            case SidPad1:
            case SidPad2:
                // Real libpad has many function numbers (open/read/state/etc.) we haven't
                // individually verified — answer every call with current pad state, a safe
                // default that keeps titles moving instead of hanging on an unmapped fno.
                if (recvBuf != 0) pad.WriteStatusBuffer(mem, recvBuf);
                return (int)pad.Buttons;

            case SidMcServ:
                return 0;

            default:
                UnknownServiceCalls++;
                return 0;
        }
    }
}
