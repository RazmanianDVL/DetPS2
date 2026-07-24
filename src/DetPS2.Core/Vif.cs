using System;

namespace DetPS2.Core;

/// <summary>
/// VIF (Phases 6/26): DMAC feed + VIF command stream (MPG / MSCAL / UNPACK stub).
/// </summary>
public sealed class Vif : ISchedulable
{
    // VIF CMD field (bits 24-31 of VIF code word)
    public const uint CmdNop = 0x00;
    public const uint CmdStcycl = 0x01;
    public const uint CmdOffset = 0x02;
    public const uint CmdBase = 0x03;
    public const uint CmdITop = 0x04;
    public const uint CmdStMod = 0x05;
    public const uint CmdMskPath3 = 0x06;
    public const uint CmdMark = 0x07;
    public const uint CmdFlushE = 0x10;
    public const uint CmdFlush = 0x11;
    public const uint CmdFlushA = 0x13;
    public const uint CmdMpg = 0x4A;
    public const uint CmdMscal = 0x14;
    public const uint CmdMscnt = 0x17;
    public const uint CmdMscalf = 0x15;
    public const uint CmdUnpack = 0x60; // base; actual 0x60-0x6F

    private readonly SystemMemory _memory;
    private Vu0? _vu0;
    private Vu1? _vu1;

    public ulong CommandsProcessed { get; private set; }
    public ulong UnpackWords { get; private set; }
    public ulong MpgWords { get; private set; }
    public ulong MscalCount { get; private set; }
    public uint Itop { get; private set; }
    public uint Base { get; private set; }
    public uint Cycle { get; private set; } = 0x0101;

    private uint _mpgAddr;
    private uint _mpgRemaining;
    private bool _inMpg;
    private uint _unpackRemaining;
    private uint _unpackDest;
    private int _unpackVnVl; // V4_32=0x6C style
    public ulong UnpackV4_32 { get; private set; }
    /// <summary>Phase 54: non-V4_32 unpack units (V3_32, V2_16, …).</summary>
    public ulong UnpackOther { get; private set; }

    public Vif(SystemMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    public void SetVu0(Vu0 vu0) => _vu0 = vu0 ?? throw new ArgumentNullException(nameof(vu0));
    public void SetVu1(Vu1 vu1) => _vu1 = vu1 ?? throw new ArgumentNullException(nameof(vu1));

    public void Reset()
    {
        CommandsProcessed = UnpackWords = MpgWords = MscalCount = 0;
        UnpackV4_32 = 0;
        UnpackOther = 0;
        Itop = Base = 0;
        Cycle = 0x0101;
        _mpgAddr = _mpgRemaining = 0;
        _inMpg = false;
        _unpackRemaining = 0;
        _unpackDest = 0;
        _unpackVnVl = 0;
    }

    public int Step(ulong maxCycles)
    {
        const int BaseVifCost = 3;
        return Math.Min(BaseVifCost, (int)maxCycles);
    }

    /// <summary>Process one VIF code word (32-bit).</summary>
    public void ProcessVifCode(uint code)
    {
        CommandsProcessed++;
        uint cmd = (code >> 24) & 0xFF;
        uint num = (code >> 16) & 0xFF;
        uint imm = code & 0xFFFF;

        if (_inMpg && _mpgRemaining > 0)
        {
            // Should not happen if called correctly; data path uses FeedData
            return;
        }

        switch (cmd)
        {
            case CmdNop:
            case CmdFlush:
            case CmdFlushE:
            case CmdFlushA:
            case CmdMskPath3:
            case CmdMark:
            case CmdStMod:
                break;
            case CmdStcycl:
                Cycle = imm;
                break;
            case CmdBase:
                Base = imm;
                break;
            case CmdOffset:
                break;
            case CmdITop:
                Itop = imm;
                break;
            case CmdMpg:
                // NUM = quadwords of micro; IMM = VU micro addr / 8
                _mpgRemaining = num == 0 ? 256u : num;
                _mpgAddr = imm * 8; // byte-ish word index*8 simplified to word index
                _inMpg = true;
                break;
            case CmdMscal:
            case CmdMscalf:
                MscalCount++;
                _vu1?.Mscal(imm);
                break;
            case CmdMscnt:
                MscalCount++;
                _vu1?.Mscnt();
                break;
            default:
                if (cmd >= 0x60 && cmd <= 0x6F)
                {
                    // UNPACK: NUM = units; cmd encodes vn/vl (Phase 42)
                    _unpackRemaining = num == 0 ? 256u : num;
                    _unpackDest = imm;
                    _unpackVnVl = (int)(cmd & 0xF);
                    if ((cmd & 0xF) == 0xC) // V4_32
                        UnpackV4_32++;
                    else
                        UnpackOther++;
                }
                break;
        }
    }

    /// <summary>Feed data word following a command (MPG/UNPACK).</summary>
    public void FeedData(uint word)
    {
        if (_inMpg && _mpgRemaining > 0 && _vu1 != null)
        {
            uint idx = (_mpgAddr / 4) % VectorUnit.MicroMemWords;
            _vu1.WriteMicroWord(idx, word);
            _mpgAddr += 4;
            MpgWords++;
            // Each VIF data is one word; NUM is often in qwords — count words
            if (MpgWords % 4 == 0)
                _mpgRemaining--;
            if (_mpgRemaining == 0)
                _inMpg = false;
            return;
        }

        if (_unpackRemaining > 0 && _vu1 != null)
        {
            // V4_32 (0xC): 4 words/unit; V3_32 (0x8): 3; V2_* (~0x5): 1 word packs 2×16
            int wordsPer = (_unpackVnVl & 0xF) switch
            {
                0xC => 4, // V4_32
                0x8 => 3, // V3_32
                0x4 => 2, // V2_32
                0x5 => 1, // V2_16
                0x6 => 1, // V2_8
                _ => 4
            };
            if (_unpackVnVl == 0xC || (_unpackVnVl & 0xF) == 0x8)
            {
                uint idx = (_unpackDest + (uint)(UnpackWords % 1024)) % VectorUnit.MicroMemWords;
                _vu1.WriteMicroWord(idx, word);
            }
            _vu1.ReceiveFromVif1(word);
            UnpackWords++;
            if (UnpackWords % (ulong)wordsPer == 0)
            {
                _unpackRemaining--;
                _unpackDest++;
            }
        }
    }

    /// <summary>Process a stream of VIF words from memory (tag + data).</summary>
    public void ProcessStream(uint address, uint wordCount)
    {
        for (uint i = 0; i < wordCount; i++)
        {
            uint w = _memory.Read32(address + i * 4);
            if (_inMpg || _unpackRemaining > 0)
                FeedData(w);
            else
                ProcessVifCode(w);
        }
    }

    public void SendQuadwordToVu1(uint address)
    {
        if (_vu1 == null) return;
        uint w0 = _memory.Read32(address);
        uint w1 = _memory.Read32(address + 4);
        uint w2 = _memory.Read32(address + 8);
        uint w3 = _memory.Read32(address + 12);

        uint cmd = (w0 >> 24) & 0xFF;
        if (cmd is CmdMpg or CmdMscal or CmdMscalf or CmdMscnt or CmdITop or CmdBase
            || (cmd >= 0x60 && cmd <= 0x6F))
        {
            ProcessVifCode(w0);
            FeedData(w1);
            FeedData(w2);
            FeedData(w3);
            return;
        }

        _vu1.ReceiveFromVif1(w0);
        _vu1.ReceiveFromVif1(w1);
        _vu1.ReceiveFromVif1(w2);
        _vu1.ReceiveFromVif1(w3);
    }
}
