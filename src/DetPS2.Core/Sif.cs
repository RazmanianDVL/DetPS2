using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Subsystem Interface (Phase 8 + 13 RPC).
/// DMA SIF0/SIF1 + command queue + RPC packet queue processed in Step().
/// </summary>
public sealed class Sif : ISchedulable
{
    public enum DmaDirection
    {
        IopToEe = 0,
        EeToIop = 1
    }

    private readonly SystemMemory _memory;
    private readonly Intc? _intc;
    private readonly Queue<uint> _cmdQueue = new();
    private readonly Queue<uint> _rpcPacketAddrs = new();

    private IopModuleHost? _modules;
    private PadInput? _pad;
    private Cdvd? _cdvd;

    public bool DmaBusy { get; private set; }
    public uint LastCommand { get; private set; }
    public uint Status { get; private set; }
    public ulong CommandsProcessed { get; private set; }
    public ulong BytesTransferred { get; private set; }
    public ulong RpcProcessed { get; private set; }

    public uint MsCom { get; private set; }
    public uint SmCom { get; private set; }
    public uint MsFlag { get; private set; }
    public uint SmFlag { get; private set; }

    /// <summary>
    /// Real ps2sdk (ee/kernel/include/sifdma.h) bit values for SIF_REG_SMFLAG, the register
    /// real EE library code polls to learn the IOP's boot progress: sceSifInitCmd's own real
    /// source (ee/kernel/src/sifcmd.c) literally reads
    /// "while (!(sceSifGetReg(SIF_REG_SMFLAG) & SIF_STAT_CMDINIT))" before doing anything else.
    /// This emulator's IOP core (Iop.cs) doesn't model real IOP-side hardware registers or
    /// firmware execution faithfully enough for a real IOP kernel to ever set these for real
    /// (see MaybeUnblockStarvedSema's doc comment and DEVELOPER_GUIDE.md 2026-07-26 for how that
    /// was confirmed) — so, like a real BIOS handing off to a game only once its own boot is
    /// actually complete, present these as already set from boot: SIFINIT (0x10000, basic SIF
    /// up), CMDINIT (0x20000, cmd/rpc layer up), BOOTEND (0x40000, full IOP boot done).
    /// </summary>
    public const uint SifStatSifInit = 0x10000;
    public const uint SifStatCmdInit = 0x20000;
    public const uint SifStatBootEnd = 0x40000;

    /// <summary>Last RPC result written (for tests).</summary>
    public uint LastRpcResult { get; private set; }

    public Sif(SystemMemory memory, Intc? intc = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _intc = intc;
    }

    public void BindServices(IopModuleHost modules, PadInput pad, Cdvd cdvd)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _pad = pad ?? throw new ArgumentNullException(nameof(pad));
        _cdvd = cdvd ?? throw new ArgumentNullException(nameof(cdvd));
    }

    public void Reset()
    {
        DmaBusy = false;
        LastCommand = 0;
        Status = 0;
        CommandsProcessed = 0;
        BytesTransferred = 0;
        RpcProcessed = 0;
        LastRpcResult = 0;
        MsCom = SmCom = MsFlag = 0;
        SmFlag = SifStatSifInit | SifStatCmdInit | SifStatBootEnd;
        _cmdQueue.Clear();
        _rpcPacketAddrs.Clear();
    }

    public void SendCommand(uint command)
    {
        LastCommand = command;
        Status |= 0x2;
        _cmdQueue.Enqueue(command);
        MsCom = command;
        MsFlag |= 1;
        _intc?.Raise(Intc.InterruptSource.Sif);
    }

    /// <summary>Queue an EE RPC packet address for IOP-side processing.</summary>
    public void SubmitRpc(uint packetEeAddr)
    {
        _rpcPacketAddrs.Enqueue(packetEeAddr);
        Status |= 0x8; // RPC pending
        MsFlag |= 2;
        _intc?.Raise(Intc.InterruptSource.Sif);
    }

    public bool TryDequeueCommand(out uint command)
    {
        if (_cmdQueue.Count == 0)
        {
            command = 0;
            return false;
        }
        command = _cmdQueue.Dequeue();
        CommandsProcessed++;
        return true;
    }

    public int CommandQueueCount => _cmdQueue.Count;
    public int RpcQueueCount => _rpcPacketAddrs.Count;

    public void DoDmaTransfer(uint eeAddr, uint iopAddr, uint size, DmaDirection direction = DmaDirection.EeToIop)
    {
        if (size == 0) return;

        DmaBusy = true;
        Status |= 0x1;

        uint iopPhys = NormalizeIopAddr(iopAddr);
        if (TransferLog.Enabled)
        {
            bool eeToIop = direction == DmaDirection.EeToIop;
            TransferLog.Log(eeToIop ? "SIF:EE->IOP" : "SIF:IOP->EE",
                eeToIop ? eeAddr : iopPhys, eeToIop ? iopPhys : eeAddr, size);
        }

        for (uint i = 0; i < size; i++)
        {
            if (direction == DmaDirection.EeToIop)
            {
                byte b = _memory.Read8(eeAddr + i);
                _memory.Write8(iopPhys + i, b);
            }
            else
            {
                byte b = _memory.Read8(iopPhys + i);
                _memory.Write8(eeAddr + i, b);
            }
        }

        BytesTransferred += size;
        DmaBusy = false;
        Status &= ~0x1u;
        Status |= 0x4;
        _intc?.Raise(Intc.InterruptSource.Sif);
    }

    public void DoDmaTransfer(uint eeAddr, uint iopAddr, uint size) =>
        DoDmaTransfer(eeAddr, iopAddr, size, DmaDirection.EeToIop);

    public void Sif0IopToEe(uint iopAddr, uint eeAddr, uint size) =>
        DoDmaTransfer(eeAddr, iopAddr, size, DmaDirection.IopToEe);

    public void Sif1EeToIop(uint eeAddr, uint iopAddr, uint size) =>
        DoDmaTransfer(eeAddr, iopAddr, size, DmaDirection.EeToIop);

    private static uint NormalizeIopAddr(uint iopAddr)
    {
        if (iopAddr < SystemMemory.IOP_RAM_SIZE)
            return SystemMemory.IOP_RAM_BASE + iopAddr;
        return iopAddr;
    }

    public void WriteSmCom(uint value)
    {
        SmCom = value;
        SmFlag |= 1;
        _intc?.Raise(Intc.InterruptSource.Sif);
    }

    public uint GetStatus() => Status;

    public uint ReadRegister(uint address)
    {
        return (address & 0xFF) switch
        {
            0x00 => MsCom,
            0x10 => SmCom,
            0x20 => MsFlag,
            0x30 => SmFlag,
            0x40 => Status,
            0x50 => LastRpcResult,
            _ => 0
        };
    }

    public void WriteRegister(uint address, uint value)
    {
        switch (address & 0xFF)
        {
            case 0x00:
                SendCommand(value);
                break;
            case 0x10:
                WriteSmCom(value);
                break;
            case 0x20:
                MsFlag = value;
                break;
            case 0x30:
                SmFlag = value;
                break;
            case 0x40:
                Status = value;
                break;
            case 0x60:
                // Write packet address to submit RPC via MMIO
                SubmitRpc(value);
                break;
        }
    }

    /// <summary>Process pending RPC packets (deterministic, no host I/O).</summary>
    public int Step(ulong maxCycles)
    {
        if (_modules == null || _pad == null || _cdvd == null)
            return 0;

        int n = 0;
        while (_rpcPacketAddrs.Count > 0 && n < 16)
        {
            uint addr = _rpcPacketAddrs.Dequeue();
            var pkt = SifRpcPacket.Read(_memory, addr);
            var done = _modules.Dispatch(pkt, _memory, _pad, _cdvd);
            done.Write(_memory, addr);
            LastRpcResult = done.Result;
            SmCom = done.Result;
            SmFlag |= 4;
            Status |= 0x10; // RPC complete
            Status &= ~0x8u;
            RpcProcessed++;
            n++;
            _intc?.Raise(Intc.InterruptSource.Sif);
        }

        return n > 0 ? n : 0;
    }
}
