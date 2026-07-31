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
    public const uint CmdDirect = 0x50;
    public const uint CmdDirectHl = 0x51;
    public const uint CmdUnpack = 0x60; // base; actual 0x60-0x6F

    private readonly SystemMemory _memory;
    private Vu0? _vu0;
    private Vu1? _vu1;
    private Gif? _gif;
    /// <summary>Remaining QWs expected by DIRECT/DIRECTHL (PATH2 → GIF).</summary>
    private uint _directRemaining;
    private int _path2TraceLeft = 8;

    public ulong CommandsProcessed { get; private set; }
    public ulong UnpackWords { get; private set; }
    public ulong MpgWords { get; private set; }
    public ulong MscalCount { get; private set; }
    /// <summary>Count of MSKPATH3 commands processed (diagnostics / smokes).</summary>
    public ulong MskPath3Count { get; private set; }
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
    /// <summary>Optional GIF link so MSKPATH3 can raise GIF_STAT.M3P (PATH3 mask).</summary>
    public void SetGif(Gif gif) => _gif = gif ?? throw new ArgumentNullException(nameof(gif));

    public void Reset()
    {
        CommandsProcessed = UnpackWords = MpgWords = MscalCount = 0;
        MskPath3Count = 0;
        UnpackV4_32 = 0;
        UnpackOther = 0;
        Itop = Base = 0;
        Cycle = 0x0101;
        _mpgAddr = _mpgRemaining = 0;
        _inMpg = false;
        _unpackRemaining = 0;
        _unpackDest = 0;
        _unpackVnVl = 0;
        _directRemaining = 0;
        _path2TraceLeft = 8;
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
            case CmdMark:
            case CmdStMod:
                break;
            case CmdMskPath3:
                // ps2tek / GS manuals: PATH3 mask is IMM bit 15 (0x8000), not bit 0.
                // Commercial streams (Burnout 3 stack word 0x06008000) set bit 15; bit 0 is unused.
                MskPath3Count++;
                _gif?.SetMskPath3((imm & 0x8000) != 0);
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
            case CmdDirect:
            case CmdDirectHl:
                // IMM = number of QWs to forward to GIF PATH2 (0 means 65536).
                // A new DIRECT supersedes any unfinished prior DIRECT / GIF mid-packet so
                // truncated garbage (wrong IMM or non-GIF payload) cannot sticky-swallow
                // later real PACKED A+D setup (WAVE-11C GoW Path2 residual).
                if (_directRemaining > 0 || (_gif?.PacketInFlight ?? false))
                {
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_GIF") == "1")
                    {
                        Console.Error.WriteLine(
                            $"[VIF] DIRECT supersede prevRem={_directRemaining} " +
                            $"gifInFlight={_gif?.PacketInFlight} code=0x{code:X8}");
                    }
                    _directRemaining = 0;
                    _gif?.AbortIncompletePacket("new-DIRECT");
                }
                _directRemaining = imm == 0 ? 65536u : imm;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_GIF") == "1")
                {
                    Console.Error.WriteLine(
                        $"[VIF] DIRECT/HL imm={_directRemaining} code=0x{code:X8}");
                }
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

    /// <summary>
    /// Feed one word from the VIF FIFO MMIO window (0x10004000 / 0x10005000).
    /// When a prior MPG/UNPACK still expects data, consume as payload; otherwise treat
    /// the word as a VIF code (NOP/MSKPATH3/DIRECT/…). Previously non-data words were
    /// silently dropped, so MSKPATH3 written via FIFO never raised GIF_STAT.M3P.
    /// </summary>
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
            return;
        }

        // Idle VIF: FIFO poke is a command word (matches DMAC ProcessStream path).
        ProcessVifCode(word);
    }

    /// <summary>Process a stream of VIF words from memory (tag + data).</summary>
    public void ProcessStream(uint address, uint wordCount)
    {
        uint i = 0;
        while (i < wordCount)
        {
            // DIRECT/DIRECTHL: next N QWs go to GIF PATH2 as a contiguous packet.
            // ps2tek: data is the *following QWs* after the DIRECT code — residual words
            // in the same QW as the DIRECT command are padding, not GIF. Starting Path2
            // mid-QW (e.g. after 3 command words → addr&0xF==0xC) misparses the first
            // GIFtag (GoW residual: flg=IMAGE nloop=20586 at 0x…BE8C, FRAME never seen).
            if (_directRemaining > 0 && _gif != null)
            {
                uint byteAddr = address + i * 4;
                uint misalign = byteAddr & 15u;
                if (misalign != 0)
                {
                    // Skip pad words to the next 16-byte boundary; do not debit DIRECT QWC.
                    uint padWords = (16u - misalign) / 4u;
                    uint skip = Math.Min(padWords, wordCount - i);
                    i += skip;
                    continue;
                }
                uint qws = Math.Min(_directRemaining, (wordCount - i) / 4);
                if (qws > 0)
                {
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_GIF") == "1"
                        && _path2TraceLeft > 0)
                    {
                        _path2TraceLeft--;
                        uint w0 = _memory.Read32(byteAddr);
                        uint w1 = _memory.Read32(byteAddr + 4);
                        uint w2 = _memory.Read32(byteAddr + 8);
                        uint w3 = _memory.Read32(byteAddr + 12);
                        Console.Error.WriteLine(
                            $"[VIF] Path2 feed addr=0x{byteAddr:X8} qws={qws} remDirect={_directRemaining} " +
                            $"qw0={w0:X8}_{w1:X8}_{w2:X8}_{w3:X8}");
                    }
                    _gif.ReceivePath2Data(byteAddr, qws);
                    _directRemaining -= qws;
                    i += qws * 4;
                    // DIRECT exhausted: if GIF still mid-packet the stream was truncated —
                    // drop sticky so the next DIRECT's GIFtag is not treated as body data.
                    if (_directRemaining == 0 && (_gif?.PacketInFlight ?? false))
                        _gif.AbortIncompletePacket("DIRECT-end-truncated");
                    continue;
                }
                // Partial QW left — wait for more words on next ProcessStream
                break;
            }

            uint w = _memory.Read32(address + i * 4);
            if (_inMpg || _unpackRemaining > 0)
                FeedData(w);
            else
                ProcessVifCode(w);
            i++;
        }
    }

    /// <summary>
    /// One QW of VIF1/VIF0 DMA. Always run as a VIF command/data stream (not raw VU mem).
    /// Previous allow-list omitted MSKPATH3/DIRECT/FLUSH/… so PATH3 mask via DMA never latched
    /// (Burnout 3 path-sync @ 0x001F19C0 spins on GIF_STAT.M3P forever).
    /// </summary>
    public void SendQuadwordToVu1(uint address)
    {
        ProcessStream(address, 4);
    }
}
