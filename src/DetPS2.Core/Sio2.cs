using System;

namespace DetPS2.Core;

/// <summary>
/// SIO2 bus HLE (pad / multitap / memory card) — contract surface that retail
/// <c>rom0:SIO2MAN</c> + <c>PADMAN</c> / <c>MCSERV</c> drive when IRX runs on the IOP.
///
/// <para><b>Not</b> cycle-accurate IOP R3000 of SIO2MAN.IRX. Models the transfer/ctrl
/// contracts those modules expect: SPI device framing (addr + cmd), DualShock config FSM,
/// memcard probe/specs stubs, CTRL start + STAT ready, SEND3 command queue, port/slot select,
/// and transfer-complete iStat (IOP INTC line 17).</para>
///
/// <para>Two address windows:</para>
/// <list type="bullet">
/// <item><see cref="IopPhysBase"/> <c>0x1F808200</c> — real IOP map (SEND3 @ +0x00…3C, DATA_IN @ +0x60).
///   PADMAN/SIO2MAN IRX program this via KSEG1 <c>0xBF808200</c> (phys after mask).</item>
/// <item><see cref="MmioBase"/> <c>0x1000F600</c> — DetPS2 EE/test compact alias (Phase-31 smokes);
///   +0x00 DATA, +0x04 STAT/CTRL, +0x08 port/slot; real-relative aliases also accepted at +0x60+.</item>
/// </list>
/// See <c>docs/irx/SIO2_PAD_DEVICE.md</c> and <c>docs/bios-ports/SIO2MAN.md</c>.
/// </summary>
public sealed class Sio2
{
    /// <summary>DetPS2 EE/test MMIO base (compact alias of IOP hardware).</summary>
    public const uint MmioBase = 0x1000F600;

    /// <summary>Real IOP SIO2 physical base (PCSX2 <c>Sio2</c> / SIO2MAN).</summary>
    public const uint IopPhysBase = 0x1F808200;

    /// <summary>IOP INTC IRQ line raised on transfer complete (PCSX2 <c>iopIntcIrq(17)</c>).</summary>
    public const int IopTransferIrqLine = 17;

    // Device addresses (first TX byte) — BlueRetro / PCSX2 SioMode
    public const byte AddrPad = 0x01;
    public const byte AddrMultitap = 0x21;
    public const byte AddrInfrared = 0x61;
    public const byte AddrMemcard = 0x81;

    // Pad commands
    public const byte CmdPadMystery = 0x40;
    public const byte CmdPadQueryButtons = 0x41;
    public const byte CmdPadPoll = 0x42;
    public const byte CmdPadConfig = 0x43;
    public const byte CmdPadModeSwitch = 0x44;
    public const byte CmdPadStatus = 0x45;
    public const byte CmdPadConst1 = 0x46;
    public const byte CmdPadConst2 = 0x47;
    public const byte CmdPadConst3 = 0x4C;
    public const byte CmdPadVibration = 0x4D;
    public const byte CmdPadResponseBytes = 0x4F;

    // Memcard commands (PS2 protocol subset)
    public const byte CmdMcProbe = 0x11;
    public const byte CmdMcGetSpecs = 0x26;
    public const byte CmdMcSetTerminator = 0x27;
    public const byte CmdMcGetTerminator = 0x28;
    public const byte CmdMcAuthXor = 0xF0;

    // Mode IDs returned in header byte 1 (upper nibble = type, lower = payload words)
    public const byte ModeDigital = 0x41;
    public const byte ModeAnalog = 0x73;
    public const byte ModeDualShock2 = 0x79;
    public const byte ModeConfig = 0xF3;
    public const byte ModeMultitap = 0x80;

    // CTRL / STAT bits (Sio2Ctrl / simplified ready)
    public const uint CtrlStartTransfer = 0x1;
    public const uint CtrlReset = 0xC;
    public const uint CtrlSio2manReset = 0x000003BC;
    public const uint StatTxReady = 0x1000;
    public const uint StatRxReady = 0x2000;
    public const uint CmdStatConnected = 0x1100;
    public const uint CmdStatDisconnected = 0x1D100;
    public const uint CmdStatNoDevicesMissing = 0x1000;
    public const uint CmdStatOnePortOpen = 0x100;
    public const uint CmdStatTwoPortsOpen = 0x200;
    public const uint CmdStatPort1Missing = 0x1D000;
    public const uint CmdStatPort2Missing = 0x2D000;

    // SEND3 command descriptor (Sio2Cmd)
    public const uint Send3PortMask = 0x1;
    public const uint Send3LengthMask = 0x3FF;
    public const int Send3LengthShift = 8;

    private readonly byte[] _inFifo = new byte[256];
    private readonly byte[] _outFifo = new byte[256];
    private int _inLen, _outLen, _outPos;
    private PadInput? _pad;
    private PadInput[]? _multitapPads;
    private MemoryCard? _memcard;
    private int _port; // 0 or 1
    private int _slot; // multitap slot 0-3

    // Per-port DualShock config FSM (PADMAN open path: find → config → mode → act → exit)
    private readonly PadWireState[] _wire = new PadWireState[2];

    private uint _ctrl = CtrlSio2manReset;
    private uint _cmdStat = CmdStatDisconnected;
    private uint _portStat = 0xF;
    private uint _fifoStat;
    private uint _iStat; // bit0 = transfer complete IRQ sticky (PCSX2 iStat)
    private readonly uint[] _send3 = new uint[16];
    private readonly uint[] _portCtrl0 = new uint[4];
    private readonly uint[] _portCtrl1 = new uint[4];
    private byte _mcTerminator = 0x55;

    // SEND3 queue state (PCSX2 Sio2 SoftReset/Write model, batch-friendly)
    private int _queuePosition;
    private int _commandLength;
    private bool _queueComplete;

    /// <summary>True when SEND3[0] length was 0 (queue complete / no more descriptors).</summary>
    public bool Send3QueueComplete => _queueComplete;

    public ulong Transfers { get; private set; }
    public ulong Commands { get; private set; }
    public bool MultitapEnabled { get; set; }

    /// <summary>True after a successful pad transfer when a pad was attached.</summary>
    public bool LastTransferConnected { get; private set; }

    /// <summary>CMD_STAT after last transfer (CONNECTED / DISCONNECTED shape).</summary>
    public uint CmdStat => _cmdStat;

    public uint Ctrl => _ctrl;

    /// <summary>iStat after last transfer (bit0 set = IRQ pending until cleared).</summary>
    public uint IStat => _iStat;

    /// <summary>True when transfer-complete bit is set (IOP INTC line <see cref="IopTransferIrqLine"/>).</summary>
    public bool TransferIrqPending => (_iStat & 1u) != 0;

    /// <summary>
    /// Optional hook when a transfer completes with iStat bit0 set.
    /// Wire to IOP INTC raise(17) when that surface exists (T1/T2 handoff).
    /// </summary>
    public Action? OnTransferComplete { get; set; }

    /// <summary>Last programmed SEND3[0] command length (bytes), or 0 if unused.</summary>
    public int LastSend3CommandLength => _commandLength;

    public Sio2()
    {
        for (int i = 0; i < _wire.Length; i++)
            _wire[i] = new PadWireState();
    }

    public void Attach(PadInput pad, MemoryCard? memcard = null)
    {
        _pad = pad;
        _memcard = memcard;
    }

    public void AttachMultitap(PadInput[] pads)
    {
        _multitapPads = pads;
        // Do not auto-enable: games opt in; tests set MultitapEnabled explicitly
    }

    public void Reset()
    {
        _inLen = _outLen = _outPos = 0;
        Transfers = Commands = 0;
        _port = _slot = 0;
        Array.Clear(_inFifo);
        Array.Clear(_outFifo);
        _ctrl = CtrlSio2manReset;
        _cmdStat = CmdStatDisconnected;
        _portStat = 0xF;
        _fifoStat = 0;
        _iStat = 0;
        _mcTerminator = 0x55;
        LastTransferConnected = false;
        SoftResetQueue();
        for (int i = 0; i < _wire.Length; i++)
            _wire[i].Reset();
        Array.Clear(_send3);
        Array.Clear(_portCtrl0);
        Array.Clear(_portCtrl1);
    }

    /// <summary>Whether the selected port/slot pad is currently in DualShock config mode.</summary>
    public bool IsPadInConfig(int port = 0) =>
        port >= 0 && port < _wire.Length && _wire[port].InConfig;

    /// <summary>Current pad mode ID for a port (0x41 / 0x73 / 0x79 / 0xF3).</summary>
    public byte GetPadModeId(int port = 0)
    {
        if (port < 0 || port >= _wire.Length) return ModeDigital;
        var w = _wire[port];
        if (w.InConfig) return ModeConfig;
        return w.ModeId;
    }

    /// <summary>
    /// Map a physical or KSEG-mapped IOP address onto a SIO2 real-map offset.
    /// <c>0xBF808200</c> and <c>0x1F808200</c> both resolve (mask to 0x1FFFFFFF).
    /// </summary>
    public static bool TryGetIopOffset(uint address, out uint offset)
    {
        uint p = address & 0x1FFFFFFFu;
        if (p >= IopPhysBase && p < IopPhysBase + 0x100)
        {
            offset = p - IopPhysBase;
            return true;
        }
        offset = 0;
        return false;
    }

    /// <summary>True if <paramref name="address"/> targets the real IOP SIO2 window.</summary>
    public static bool IsIopAddress(uint address) => TryGetIopOffset(address, out _);

    /// <summary>
    /// Encode a SEND3 descriptor: port in bit0, length in bits 8..17 (0x3FF).
    /// </summary>
    public static uint EncodeSend3(int port, int length) =>
        ((uint)(port & 1)) | (((uint)length & Send3LengthMask) << Send3LengthShift);

    /// <summary>Program SEND3[index] and, for index 0, soft the command queue (PCSX2 SetCmd).</summary>
    public void ProgramSend3(int index, int port, int length)
    {
        if ((uint)index >= 16) return;
        WriteSend3(index, EncodeSend3(port, length));
    }

    /// <summary>Clear transfer-complete iStat bit (software ack before next packet).</summary>
    public void ClearTransferIrq() => _iStat = 0;

    public uint ReadRegister(uint address)
    {
        if (TryGetIopOffset(address, out uint ioff))
            return ReadReal(ioff);

        uint off = address - MmioBase;
        return off switch
        {
            // Compact DetPS2 map
            0x00 => ReadDataOut(),
            0x04 => ReadStatCompact(),
            0x08 => MultitapEnabled ? 1u : 0u,

            // Partial SEND3 aliases on compact base (legacy tests / older smokes)
            0x10 => _send3[0],
            0x14 => _send3[1],
            0x18 => _send3[2],
            0x1C => _send3[3],

            0x40 => _portCtrl0[0],
            0x44 => _portCtrl0[1],
            0x48 => _portCtrl0[2],
            0x4C => _portCtrl0[3],
            0x50 => _portCtrl1[0],
            0x54 => _portCtrl1[1],
            0x58 => _portCtrl1[2],
            0x5C => _portCtrl1[3],

            // Real-relative aliases on compact base (pre-WP-21 smokes)
            0x60 => 0, // DATA_IN write-only
            0x64 => ReadDataOut(),
            0x68 => _ctrl,
            0x6C => _cmdStat,
            0x70 => _portStat,
            0x74 => _fifoStat,
            0x78 => (uint)_outPos,
            0x7C => (uint)_outLen,
            0x80 => _iStat,
            _ => 0
        };
    }

    public void WriteRegister(uint address, uint value)
    {
        if (TryGetIopOffset(address, out uint ioff))
        {
            WriteReal(ioff, value);
            return;
        }

        uint off = address - MmioBase;
        switch (off)
        {
            case 0x00: // DATA in (compact)
            case 0x60: // DATA_IN (real-relative on compact base)
                PushDataIn((byte)value);
                break;

            case 0x04: // CTRL compact / start
            case 0x68: // CTRL real-relative
                WriteCtrl(value);
                break;

            case 0x08: // port/slot select (DetPS2 extension used by multitap smoke)
                _port = (int)(value & 1);
                _slot = (int)((value >> 1) & 3);
                break;

            case 0x10:
            case 0x14:
            case 0x18:
            case 0x1C:
                WriteSend3((int)((off - 0x10) / 4), value);
                break;

            case 0x40:
            case 0x44:
            case 0x48:
            case 0x4C:
                _portCtrl0[(off - 0x40) / 4] = value;
                // Low bit of PORT_CTRL0 selects port for HLE convenience
                if (off == 0x40)
                    _port = (int)(value & 1);
                break;

            case 0x50:
            case 0x54:
            case 0x58:
            case 0x5C:
                _portCtrl1[(off - 0x50) / 4] = value;
                break;

            case 0x6C:
                _cmdStat = value;
                break;

            case 0x80:
                // Write-1-clear style for bit0 (also accept full zero clear)
                _iStat &= ~value;
                break;
        }
    }

    /// <summary>Real IOP register map (offsets from <see cref="IopPhysBase"/>).</summary>
    private uint ReadReal(uint off)
    {
        if (off <= 0x3C && (off & 3) == 0)
            return _send3[off / 4];
        return off switch
        {
            0x40 => _portCtrl0[0],
            0x44 => _portCtrl0[1],
            0x48 => _portCtrl0[2],
            0x4C => _portCtrl0[3],
            0x50 => _portCtrl1[0],
            0x54 => _portCtrl1[1],
            0x58 => _portCtrl1[2],
            0x5C => _portCtrl1[3],
            0x60 => 0, // DATA_IN write-only
            0x64 => ReadDataOut(),
            0x68 => _ctrl,
            0x6C => _cmdStat,
            0x70 => _portStat,
            0x74 => _fifoStat,
            0x78 => (uint)_outPos,
            0x7C => (uint)Math.Max(0, _outLen - _outPos),
            0x80 => _iStat,
            _ => 0
        };
    }

    private void WriteReal(uint off, uint value)
    {
        if (off <= 0x3C && (off & 3) == 0)
        {
            WriteSend3((int)(off / 4), value);
            return;
        }

        switch (off)
        {
            case 0x40:
            case 0x44:
            case 0x48:
            case 0x4C:
                _portCtrl0[(off - 0x40) / 4] = value;
                if (off == 0x40)
                    _port = (int)(value & 1);
                break;
            case 0x50:
            case 0x54:
            case 0x58:
            case 0x5C:
                _portCtrl1[(off - 0x50) / 4] = value;
                break;
            case 0x60:
                PushDataIn((byte)value);
                break;
            case 0x68:
                WriteCtrl(value);
                break;
            case 0x6C:
                _cmdStat = value;
                break;
            case 0x80:
                _iStat &= ~value;
                break;
        }
    }

    private void WriteSend3(int index, uint value)
    {
        if ((uint)index >= 16) return;
        _send3[index] = value;
        // PCSX2: writing SEND3[0] soft-resets the command queue for the next packet.
        if (index == 0)
        {
            SoftResetQueue();
            _port = (int)(value & Send3PortMask);
            _commandLength = (int)((value >> Send3LengthShift) & Send3LengthMask);
            if (_commandLength == 0)
                _queueComplete = true;
        }
    }

    private void SoftResetQueue()
    {
        _queuePosition = 0;
        _commandLength = 0;
        _queueComplete = false;
        // Leftover TX from a prior packet is discarded (PCSX2 SoftReset clears g_Sio2FifoIn).
        // Do not clear RX here — DMA12 may still be draining.
    }

    private void PushDataIn(byte value)
    {
        if (_inLen < _inFifo.Length)
            _inFifo[_inLen++] = value;
    }

    private void WriteCtrl(uint value)
    {
        if ((value & CtrlReset) == CtrlReset)
        {
            _inLen = _outLen = _outPos = 0;
            Array.Clear(_inFifo);
            Array.Clear(_outFifo);
            _ctrl = CtrlSio2manReset;
            SoftResetQueue();
            return;
        }

        _ctrl = value;
        if ((value & CtrlStartTransfer) != 0)
            ProcessTransfer();
    }

    private uint ReadDataOut()
    {
        if (_outPos < _outLen)
            return _outFifo[_outPos++];
        return 0xFF;
    }

    private uint ReadStatCompact()
    {
        // Bit layout kept for Phase-31 smokes: RX ready when unread out bytes remain.
        uint st = StatTxReady;
        if ((_outLen - _outPos) > 0)
            st |= StatRxReady;
        return st;
    }

    /// <summary>Run pad/memcard command in FIFO (high-level batch transfer).</summary>
    public void ProcessTransfer()
    {
        Transfers++;
        if (_inLen == 0)
        {
            _cmdStat = CmdStatDisconnected;
            LastTransferConnected = false;
            SignalTransferComplete();
            return;
        }

        // Apply SEND3[0] port/length when programmed (SIO2MAN always does this).
        if (_send3[0] != 0)
        {
            _port = (int)(_send3[0] & Send3PortMask);
            int len = (int)((_send3[0] >> Send3LengthShift) & Send3LengthMask);
            if (len > 0)
            {
                _commandLength = len;
                // Clamp TX to declared command length (DMA block may be larger / padded).
                if (_inLen > len)
                    _inLen = len;
            }
        }

        Commands++;
        _outLen = 0;
        _outPos = 0;

        byte addr = _inFifo[0];
        switch (addr)
        {
            case AddrPad:
            case CmdPadPoll: // bare poll without leading 0x01 (legacy / short paths)
                ProcessPadTransfer(addr == AddrPad);
                break;
            case AddrMultitap: // 0x21
                ProcessMultitapTransfer();
                break;
            case AddrInfrared: // 0x61 — no device; silent bus
                EmitByte(0xFF);
                while (_outLen < _inLen)
                    EmitByte(0xFF);
                _cmdStat = CmdStatDisconnected;
                LastTransferConnected = false;
                break;
            case AddrMemcard:
                ProcessMemcardTransfer();
                break;
            default:
                // Unknown device — silent bus
                EmitByte(0xFF);
                _cmdStat = CmdStatDisconnected;
                LastTransferConnected = false;
                break;
        }

        _inLen = 0;
        // Clear START bit after transfer (hardware auto-clears)
        _ctrl &= ~CtrlStartTransfer;
        // Advance SEND3 queue position (single-descriptor HLE; multi-slot drain residual)
        if (_send3[0] != 0)
            _queuePosition = Math.Min(15, _queuePosition + 1);
        SignalTransferComplete();
    }

    private void SignalTransferComplete()
    {
        // PCSX2: Interrupt() on START_TRANSFER if iStat was clear.
        if (_iStat == 0)
        {
            _iStat = 1;
            OnTransferComplete?.Invoke();
        }
        else
        {
            // Already pending — still set bit; avoid double-callback storm.
            _iStat |= 1;
        }
    }

    private void ProcessPadTransfer(bool hasAddressByte)
    {
        var pad = SelectPad();
        int portIdx = _port & 1;
        var wire = _wire[portIdx];

        if (pad == null)
        {
            // Missing pad: entire response high-Z
            for (int i = 0; i < Math.Max(1, _inLen); i++)
                EmitByte(0xFF);
            _cmdStat = CmdStatDisconnected | CmdStatOnePortOpen
                       | (portIdx == 0 ? CmdStatPort1Missing : CmdStatPort2Missing);
            LastTransferConnected = false;
            return;
        }

        // Sync wire mode with PadInput.AnalogMode when not locked by 0x44
        if (!wire.AnalogLocked)
        {
            if (pad.AnalogMode && wire.ModeId == ModeDigital)
                wire.ModeId = ModeDualShock2;
            else if (!pad.AnalogMode && (wire.ModeId == ModeAnalog || wire.ModeId == ModeDualShock2))
                wire.ModeId = ModeDigital;
        }

        int iIn = 0;
        // Full-duplex framing
        if (hasAddressByte)
        {
            EmitByte(0xFF); // hi-Z while address is clocked
            iIn = 1;
        }
        else
        {
            // Legacy: first byte is already the command — still emit hi-Z for smoke shape
            EmitByte(0x00);
        }

        if (iIn >= _inLen)
        {
            // Address-only transaction (multitap smoke uses {0x01})
            EmitByte(wire.InConfig ? ModeConfig : EffectiveModeId(pad, wire));
            FinishPadConnected();
            return;
        }

        byte cmd = _inFifo[iIn++];
        // Header byte 1 = mode id; byte 2 = 0x5A
        EmitByte(wire.InConfig ? ModeConfig : EffectiveModeId(pad, wire));
        EmitByte(0x5A);
        // TX byte clocked during the 0x5A response is not a command parameter
        // (e.g. 01 43 00 01… → enter flag is the 01 after the dummy 00).
        if (iIn < _inLen)
            iIn++;

        // Payload parameters start at TX index 3 (addr+cmd+dummy)
        switch (cmd)
        {
            case CmdPadPoll:
                EmitPadPoll(pad, wire, iIn);
                break;
            case CmdPadConfig:
                EmitPadConfig(pad, wire, iIn);
                break;
            case CmdPadModeSwitch:
                EmitPadModeSwitch(pad, wire, iIn);
                break;
            case CmdPadStatus:
                EmitPadStatus(pad, wire);
                break;
            case CmdPadConst1:
                EmitPadConst1(iIn < _inLen ? _inFifo[iIn] : (byte)0);
                break;
            case CmdPadConst2:
                EmitPadConst2();
                break;
            case CmdPadConst3:
                EmitPadConst3(iIn < _inLen ? _inFifo[iIn] : (byte)0);
                break;
            case CmdPadVibration:
                EmitPadVibration(wire, iIn);
                break;
            case CmdPadResponseBytes:
                EmitPadResponseBytes(pad, wire, iIn);
                break;
            case CmdPadQueryButtons:
                EmitPadQueryButtons(pad, wire);
                break;
            case CmdPadMystery:
                EmitPadMystery();
                break;
            default:
                // Unknown command — zero payload padding
                while (_outLen < _inLen)
                    EmitByte(0x00);
                break;
        }

        // Pad out to at least input length (full duplex)
        while (_outLen < _inLen)
            EmitByte(0x00);

        FinishPadConnected();
    }

    private void FinishPadConnected()
    {
        _cmdStat = CmdStatConnected | CmdStatNoDevicesMissing | CmdStatOnePortOpen;
        LastTransferConnected = true;
    }

    private static byte EffectiveModeId(PadInput pad, PadWireState wire)
    {
        if (wire.ModeId != ModeDigital)
            return wire.ModeId;
        // Fall back to PadInput.AnalogMode for callers that only toggle that flag
        return pad.AnalogMode ? ModeDualShock2 : ModeDigital;
    }

    private void EmitPadPoll(PadInput pad, PadWireState wire, int iIn)
    {
        // Capture rumble motor bytes from TX (typically first two payload bytes)
        if (iIn < _inLen)
            wire.MotorSmall = _inFifo[iIn];
        if (iIn + 1 < _inLen)
            wire.MotorLarge = _inFifo[iIn + 1];

        ushort btns = (ushort)(~pad.Buttons & 0xFFFF); // active-low
        EmitByte((byte)(btns & 0xFF));
        EmitByte((byte)(btns >> 8));

        byte mode = EffectiveModeId(pad, wire);
        if (mode == ModeDigital)
            return;

        EmitByte(pad.Rx);
        EmitByte(pad.Ry);
        EmitByte(pad.Lx);
        EmitByte(pad.Ly);

        // DualShock 2 pressure (12 buttons) when mode 0x79
        if (mode == ModeDualShock2)
        {
            // Order: right left up down triangle circle cross square l1 r1 l2 r2
            EmitPressure(pad, PadInput.Button.Right);
            EmitPressure(pad, PadInput.Button.Left);
            EmitPressure(pad, PadInput.Button.Up);
            EmitPressure(pad, PadInput.Button.Down);
            EmitPressure(pad, PadInput.Button.Triangle);
            EmitPressure(pad, PadInput.Button.Circle);
            EmitPressure(pad, PadInput.Button.Cross);
            EmitPressure(pad, PadInput.Button.Square);
            EmitPressure(pad, PadInput.Button.L1);
            EmitPressure(pad, PadInput.Button.R1);
            EmitPressure(pad, PadInput.Button.L2);
            EmitPressure(pad, PadInput.Button.R2);
        }
    }

    private void EmitPressure(PadInput pad, PadInput.Button b)
    {
        // Digital pressure: 0xFF pressed, 0x00 released (no analog pressure sensors HLE)
        EmitByte(pad.IsDown(b) ? (byte)0xFF : (byte)0x00);
    }

    private void EmitPadConfig(PadInput pad, PadWireState wire, int iIn)
    {
        byte enter = iIn < _inLen ? _inFifo[iIn] : (byte)0;
        if (enter != 0)
        {
            wire.InConfig = true;
            // While entering, if not already in config, 0x43 may still return poll data once
            // (BlueRetro). We return zeros after config bit is set.
        }
        else
        {
            wire.InConfig = false;
        }

        // Config enter often carries residual poll-shaped padding; zeros are fine once in config.
        if (wire.InConfig)
        {
            for (int i = 0; i < 6; i++)
                EmitByte(0x00);
        }
        else
        {
            // Exit: zeros
            for (int i = 0; i < 6; i++)
                EmitByte(0x00);
        }
    }

    private void EmitPadModeSwitch(PadInput pad, PadWireState wire, int iIn)
    {
        // TX: 01 44 00 MODE LOCK ...
        byte mode = iIn < _inLen ? _inFifo[iIn] : (byte)0;
        byte lockB = iIn + 1 < _inLen ? _inFifo[iIn + 1] : (byte)0;
        if (mode != 0)
        {
            wire.ModeId = ModeDualShock2;
            pad.AnalogMode = true;
        }
        else
        {
            wire.ModeId = ModeDigital;
            pad.AnalogMode = false;
        }
        wire.AnalogLocked = lockB == 0x03;
        for (int i = 0; i < 6; i++)
            EmitByte(0x00);
    }

    private void EmitPadStatus(PadInput pad, PadWireState wire)
    {
        // RX after header: 03 02 AL 02 01 00  (DS2, BlueRetro / PCSX2 StatusInfo)
        EmitByte(0x03); // model DualShock 2
        EmitByte(0x02);
        EmitByte(pad.AnalogMode || wire.ModeId != ModeDigital ? (byte)0x01 : (byte)0x00);
        EmitByte(0x02);
        EmitByte(0x01);
        EmitByte(0x00);
    }

    private void EmitPadConst1(byte offset)
    {
        // 0x46 offset 0: 00 00 01 02 00 0A ; offset 1: 00 00 01 01 01 14
        if (offset != 0)
        {
            EmitByte(0x00);
            EmitByte(0x00);
            EmitByte(0x01);
            EmitByte(0x01);
            EmitByte(0x01);
            EmitByte(0x14);
        }
        else
        {
            EmitByte(0x00);
            EmitByte(0x00);
            EmitByte(0x01);
            EmitByte(0x02);
            EmitByte(0x00);
            EmitByte(0x0A);
        }
    }

    private void EmitPadConst2()
    {
        // 0x47: 00 00 02 00 01 00
        EmitByte(0x00);
        EmitByte(0x00);
        EmitByte(0x02);
        EmitByte(0x00);
        EmitByte(0x01);
        EmitByte(0x00);
    }

    private void EmitPadConst3(byte offset)
    {
        // 0x4C offset 0: 00 00 00 04 00 00 ; offset 1: 00 00 00 07 00 00
        EmitByte(0x00);
        EmitByte(0x00);
        EmitByte(0x00);
        EmitByte(offset != 0 ? (byte)0x07 : (byte)0x04);
        EmitByte(0x00);
        EmitByte(0x00);
    }

    private void EmitPadVibration(PadWireState wire, int iIn)
    {
        // Echo previous map then accept new (typically 00 01 FF FF FF FF)
        byte prev0 = wire.VibMap0;
        byte prev1 = wire.VibMap1;
        if (iIn < _inLen) wire.VibMap0 = _inFifo[iIn];
        if (iIn + 1 < _inLen) wire.VibMap1 = _inFifo[iIn + 1];
        EmitByte(prev0);
        EmitByte(prev1);
        EmitByte(0xFF);
        EmitByte(0xFF);
        EmitByte(0xFF);
        EmitByte(0xFF);
    }

    private void EmitPadResponseBytes(PadInput pad, PadWireState wire, int iIn)
    {
        // 0x4F: enable mask — FFFF 03 = digital+analog+pressure (DS2)
        uint mask = 0;
        if (iIn < _inLen) mask |= _inFifo[iIn];
        if (iIn + 1 < _inLen) mask |= (uint)_inFifo[iIn + 1] << 8;
        if (iIn + 2 < _inLen) mask |= (uint)_inFifo[iIn + 2] << 16;
        wire.ResponseMask = mask;
        if ((mask & 0x3FFFF) == 0x3FFFF || (mask & 0xFF) == 0xFF)
        {
            wire.ModeId = ModeDualShock2;
            pad.AnalogMode = true;
        }
        else if (mask != 0)
        {
            wire.ModeId = ModeAnalog;
            pad.AnalogMode = true;
        }
        EmitByte(0x00);
        EmitByte(0x00);
        EmitByte(0x00);
        EmitByte(0x00);
        EmitByte(0x00);
        EmitByte(0x5A);
    }

    private void EmitPadQueryButtons(PadInput pad, PadWireState wire)
    {
        // 0x41: mask of digital+analog buttons
        if (EffectiveModeId(pad, wire) != ModeDigital)
        {
            EmitByte(0xFF);
            EmitByte(0xFF);
            EmitByte(0x03);
            EmitByte(0x00);
            EmitByte(0x00);
            EmitByte(0x5A);
        }
        else
        {
            for (int i = 0; i < 6; i++)
                EmitByte(0x00);
        }
    }

    private void EmitPadMystery()
    {
        // 0x40 pressure config — stub success
        EmitByte(0x00);
        EmitByte(0x00);
        EmitByte(0x02);
        EmitByte(0x00);
        EmitByte(0x00);
        EmitByte(0x5A);
    }

    private void ProcessMultitapTransfer()
    {
        // Presence + slot status. Full 4-pad aggregate packet is out of scope for HLE depth.
        EmitByte(0xFF);
        EmitByte(MultitapEnabled ? ModeMultitap : (byte)0x00);
        EmitByte(0x5A);
        if (MultitapEnabled)
        {
            EmitByte(0x00); // slot status
            EmitByte(0x00);
            EmitByte(0x00);
            EmitByte(0x00);
        }
        _cmdStat = MultitapEnabled
            ? CmdStatConnected | CmdStatNoDevicesMissing | CmdStatOnePortOpen
            : CmdStatDisconnected | CmdStatPort1Missing;
        LastTransferConnected = MultitapEnabled;
    }

    private void ProcessMemcardTransfer()
    {
        if (_memcard == null)
        {
            for (int i = 0; i < Math.Max(1, _inLen); i++)
                EmitByte(0xFF);
            _cmdStat = CmdStatDisconnected;
            LastTransferConnected = false;
            return;
        }

        // Full duplex: addr 0x81 → 0x00 (present) or 0xFF (missing)
        EmitByte(0x00);

        if (_inLen < 2)
        {
            // Legacy short probe used by Phase-31 MemCard_ViaSio2: {0x81} alone
            // Historical shape: 00 5A 5D fileCount
            EmitByte(0x5A);
            EmitByte(_memcard.Formatted ? (byte)0x5D : (byte)0x00);
            EmitByte((byte)_memcard.FileCount);
            _cmdStat = CmdStatConnected | CmdStatNoDevicesMissing | CmdStatOnePortOpen;
            LastTransferConnected = true;
            return;
        }

        byte cmd = _inFifo[1];
        EmitByte(0x00); // second ACK-ish

        switch (cmd)
        {
            case CmdMcProbe: // 0x11
                // Probe: present + terminator ready
                EmitByte(0x2B); // intermediate
                EmitByte(_mcTerminator);
                _fifoStat = 0x8B;
                break;
            case CmdMcGetSpecs: // 0x26
                // Specs: page size 512, pages — align with MemoryCard.DefaultPages when possible
                {
                    int pages = MemoryCard.DefaultPages;
                    EmitByte(0x2B);
                    EmitByte(0x00);
                    EmitByte(0x02); // page size lo (512)
                    EmitByte(0x00); // page size hi
                    EmitByte((byte)(pages & 0xFF));
                    EmitByte((byte)((pages >> 8) & 0xFF));
                    EmitByte((byte)((pages >> 16) & 0xFF));
                    EmitByte(0x00);
                    EmitByte(0x00);
                    EmitByte(_mcTerminator);
                    _fifoStat = 0x83;
                }
                break;
            case CmdMcSetTerminator: // 0x27
                if (_inLen > 2)
                    _mcTerminator = _inFifo[2];
                EmitByte(0x2B);
                EmitByte(_mcTerminator);
                _fifoStat = 0x8B;
                break;
            case CmdMcGetTerminator: // 0x28
                EmitByte(0x2B);
                EmitByte(_mcTerminator);
                _fifoStat = 0x8B;
                break;
            case CmdMcAuthXor: // 0xF0 — auth chain stub (always succeed shape)
                EmitByte(0x2B);
                EmitByte(0x00);
                EmitByte(_mcTerminator);
                break;
            default:
                // Unknown: still mark present so MCSERV probe paths progress
                EmitByte(0x2B);
                EmitByte(_mcTerminator);
                break;
        }

        while (_outLen < _inLen)
            EmitByte(0x00);

        _cmdStat = CmdStatConnected | CmdStatNoDevicesMissing | CmdStatOnePortOpen;
        LastTransferConnected = true;
    }

    private void EmitByte(byte b)
    {
        if (_outLen < _outFifo.Length)
            _outFifo[_outLen++] = b;
    }

    private PadInput? SelectPad()
    {
        if (MultitapEnabled && _multitapPads != null && _slot < _multitapPads.Length)
            return _multitapPads[_slot];
        // Port 1 with multitap array: use index 1 if available and no multitap
        if (!MultitapEnabled && _port == 1 && _multitapPads != null && _multitapPads.Length > 1)
            return _multitapPads[1];
        // Port 1 without second pad: only port 0 has the primary attach
        if (_port == 1 && (_multitapPads == null || _multitapPads.Length < 2))
        {
            // Still return primary pad for single-controller HLE if only one attached —
            // real hardware would be empty; SIO2MAN find-pad on port1 gets disconnect.
            // Prefer accurate missing when no multitap array was provided as dual pads.
            if (_multitapPads == null)
                return null;
        }
        return _pad;
    }

    /// <summary>
    /// SIO2MAN-shaped batch transfer: queue TX bytes, run transfer, return RX.
    /// Models <c>sceSio2Transfer</c> completion (CTRL START → FIFO process → STAT ready).
    /// </summary>
    public byte[] Transact(ReadOnlySpan<byte> cmd)
    {
        _inLen = 0;
        foreach (byte b in cmd)
        {
            if (_inLen < _inFifo.Length) _inFifo[_inLen++] = b;
        }
        ProcessTransfer();
        byte[] r = new byte[_outLen];
        Buffer.BlockCopy(_outFifo, 0, r, 0, _outLen);
        return r;
    }

    /// <summary>
    /// PADMAN/SIO2MAN-shaped transfer via real SEND3 descriptor + IOP register window.
    /// Programs SEND3[0] = (port | length&lt;&lt;8), pushes TX into DATA_IN, pulses CTRL start,
    /// drains DATA_OUT. Exercises the path IRX will use once SystemMemory wires 0x1F808200.
    /// </summary>
    public byte[] TransactIop(int port, ReadOnlySpan<byte> cmd)
    {
        ClearTransferIrq();
        ProgramSend3(0, port, cmd.Length);
        // Terminator entry so multi-command queue drain stops
        if (_send3.Length > 1)
            _send3[1] = 0;

        foreach (byte b in cmd)
            WriteRegister(IopPhysBase + 0x60, b);

        WriteRegister(IopPhysBase + 0x68, CtrlStartTransfer);

        int n = Math.Max(_outLen, cmd.Length);
        byte[] r = new byte[n];
        for (int i = 0; i < n; i++)
            r[i] = (byte)ReadRegister(IopPhysBase + 0x64);
        return r;
    }

    /// <summary>
    /// Run the standard DualShock config sequence PADMAN uses after find-pad
    /// (enter config → status → constants → mode → vibration map → exit).
    /// Generic DualShock 2; no title quirks.
    /// </summary>
    public void RunPadmanConfigSequence(int port = 0)
    {
        _port = port & 1;
        // Enter config
        Transact(new byte[] { 0x01, 0x43, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00 });
        // Status / identity
        Transact(new byte[] { 0x01, 0x45, 0x00, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A });
        // Constants
        Transact(new byte[] { 0x01, 0x46, 0x00, 0x00, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A });
        Transact(new byte[] { 0x01, 0x46, 0x00, 0x01, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A });
        Transact(new byte[] { 0x01, 0x47, 0x00, 0x00, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A });
        Transact(new byte[] { 0x01, 0x4C, 0x00, 0x00, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A });
        Transact(new byte[] { 0x01, 0x4C, 0x00, 0x01, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A });
        // Enable analog + lock
        Transact(new byte[] { 0x01, 0x44, 0x00, 0x01, 0x03, 0x00, 0x00, 0x00, 0x00 });
        // Vibration map
        Transact(new byte[] { 0x01, 0x4D, 0x00, 0x00, 0x01, 0xFF, 0xFF, 0xFF, 0xFF });
        // Exit config
        Transact(new byte[] { 0x01, 0x43, 0x00, 0x00, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A });
    }

    /// <summary>
    /// Snapshot of active-low button word as PADMAN DMA <c>padButtonStatus.btns</c> expects.
    /// </summary>
    public static ushort ActiveLowButtons(PadInput pad) =>
        (ushort)(~pad.Buttons & 0xFFFF);

    private sealed class PadWireState
    {
        public bool InConfig;
        public bool AnalogLocked;
        public byte ModeId = ModeDigital;
        public byte VibMap0 = 0xFF;
        public byte VibMap1 = 0xFF;
        public byte MotorSmall;
        public byte MotorLarge;
        public uint ResponseMask;

        public void Reset()
        {
            InConfig = false;
            AnalogLocked = false;
            ModeId = ModeDigital;
            VibMap0 = VibMap1 = 0xFF;
            MotorSmall = MotorLarge = 0;
            ResponseMask = 0;
        }
    }
}

/// <summary>Four-port multitap holding pads.</summary>
public sealed class Multitap
{
    public PadInput[] Ports { get; } = new PadInput[4];

    public Multitap()
    {
        for (int i = 0; i < 4; i++)
            Ports[i] = new PadInput();
    }

    public void Reset()
    {
        // Reset pad state in place (do not replace instances — port 0 may share system Pad)
        for (int i = 0; i < Ports.Length; i++)
            Ports[i]?.Reset();
    }

    public PadInput this[int i] => Ports[i & 3];
}
