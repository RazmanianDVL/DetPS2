using System;

namespace DetPS2.Core;

/// <summary>
/// C# HLE of BIOS <b>DMACMAN.IRX</b> (IOP DMA Controller Manager) — export library
/// <c>dmacman</c> v1.x used by SIFMAN, CDVDMAN, SIO2MAN, SPU, and other IOP drivers.
///
/// Authority:
/// <list type="bullet">
/// <item>IOPBTCONF placement after SSBUSC (docs/BIOS_DISSECTION.md §1–2).</item>
/// <item>ps2sdk <c>iop/system/dmacman</c> recreation of SCE SDK 1.3.4
/// (dmacman.h / dmacman.c / exports.tab) — retail <c>DMACMAN.bin</c> / Ghidra dump not
/// yet in-tree; contracts match the open recreation + IRX import ordinals.</item>
/// <item>SIFMAN decomp (BIOS_DISSECTION §6.3) programs physical IOP DMAC regs; this host
/// models the <b>dmacman export surface</b> IRX modules import, not EE <see cref="Dmac"/>.</item>
/// </list>
///
/// Distinct from EE <see cref="Dmac"/> (Emotion Engine 10-channel DMAC at 0x1000_8xxx) and
/// from <see cref="Sif"/> (abstract EE↔IOP transport). Contract-level only — no full IOP
/// MMIO model; StartDMA completes immediately so CHCR.TR pollers and enable/priority paths
/// used by LOADCORE-linked modules do not hang.
/// </summary>
public sealed class IopDmacManHost
{
    // Channel indices (ps2sdk dmacman.h enum _iop_dmac_ch)
    public const int ChMdecIn = 0;
    public const int ChMdecOut = 1;
    public const int ChSif2 = 2;
    public const int ChCdvd = 3;
    public const int ChSpu = 4;
    public const int ChPio = 5;
    public const int ChOtc = 6;
    public const int ChSpu2 = 7;
    public const int ChDev9 = 8;
    public const int ChSif0 = 9;
    public const int ChSif1 = 10;
    public const int ChSio2In = 11;
    public const int ChSio2Out = 12;
    public const int ChannelCount = 13; // 0..0xC

    public const int ChFdma0 = 13;
    public const int ChFdma1 = 14;
    public const int ChFdma2 = 15;
    public const int ChCpu = 67;  // 'C'
    public const int ChUsb = 85;  // 'U'

    // CHCR bits (dmacman.h)
    public const uint ChcrTr = 1u << 24;   // transfer active
    public const uint ChcrLi = 1u << 10;   // linked list
    public const uint ChcrCo = 1u << 9;    // continuous
    public const uint ChcrDr = 1u << 0;    // direction

    public const int DmacToMem = 0;
    public const int DmacFromMem = 1;

    // _start defaults (ps2sdk dmacman.c)
    public const uint DefaultDpcr = 0x07777777;
    public const uint DefaultDpcr2 = 0x07777777;
    public const uint DefaultDpcr3 = 0x777;
    public const uint DefaultMasterEnable = 1; // BF801578

    private sealed class Channel
    {
        public uint Madr;
        public uint Bcr;
        public uint Chcr;
        public uint Tadr;
        public uint Ext49A; // BF801560/564/568 family
        public bool Enabled; // DPCR enable bit bookkeeping
        public int Priority; // 0..7
    }

    private readonly Channel[] _ch = new Channel[ChannelCount];
    private uint _dpcr;
    private uint _dpcr2;
    private uint _dpcr3;
    private uint _dicr;
    private uint _dicr2;
    private uint _reg578; // BF801578 master enable
    private uint _reg57C; // BF80157C
    private ulong _setSliceCount;
    private ulong _startCount;
    private ulong _completeCount;
    private ulong _enableCount;
    private ulong _disableCount;
    private ulong _releaseCount;
    private ulong _dicrIrqCount;
    private bool _started;

    public bool Started => _started;
    public uint DPCR => _dpcr;
    public uint DPCR2 => _dpcr2;
    public uint DPCR3 => _dpcr3;
    public uint DICR => _dicr;
    public uint DICR2 => _dicr2;
    public uint MasterEnable => _reg578;
    public ulong SetSliceCount => _setSliceCount;
    public ulong StartCount => _startCount;
    public ulong CompleteCount => _completeCount;
    public ulong EnableCount => _enableCount;
    public ulong DisableCount => _disableCount;
    /// <summary>How many times <see cref="ReleaseChannel"/> cleared a channel.</summary>
    public ulong ReleaseCount => _releaseCount;
    /// <summary>How many times a complete raised a DICR interrupt flag.</summary>
    public ulong DicrIrqCount => _dicrIrqCount;

    public IopDmacManHost() => Reset();

    /// <summary>
    /// DMACMAN._start contract: register library, zero channels, DPCR*=0x777…, master enable=1.
    /// Called from <see cref="BiosBootHost"/> after IOPBTCONF services are planted.
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < _ch.Length; i++)
            _ch[i] = new Channel();
        _dpcr = DefaultDpcr;
        _dpcr2 = DefaultDpcr2;
        _dpcr3 = DefaultDpcr3;
        _dicr = 0;
        _dicr2 = 0;
        _reg578 = DefaultMasterEnable;
        _reg57C = 0;
        _setSliceCount = _startCount = _completeCount = 0;
        _enableCount = _disableCount = 0;
        _releaseCount = _dicrIrqCount = 0;
        _started = false;
        // Decode default priorities (nibble low 3 bits) without enable bits set.
        for (int i = 0; i < 7; i++)
            _ch[i].Priority = 7;
        for (int i = 7; i < ChannelCount; i++)
            _ch[i].Priority = 7;
    }

    /// <summary>Mark library resident (idempotent — matches already-loaded IOPBTCONF module).</summary>
    public void Start()
    {
        if (_started) return;
        // Re-apply _start channel wipe + DPCR defaults (safe if Reset already did this).
        for (int i = 0; i < ChannelCount; i++)
        {
            _ch[i].Madr = 0;
            _ch[i].Bcr = 0;
            _ch[i].Chcr = 0;
            _ch[i].Tadr = 0;
            _ch[i].Ext49A = 0;
        }
        _dpcr = DefaultDpcr;
        _dpcr2 = DefaultDpcr2;
        _dpcr3 = DefaultDpcr3;
        _reg578 = DefaultMasterEnable;
        _started = true;
    }

    private static bool IsHwChannel(int channel) => (uint)channel < ChannelCount;

    // -------------------- register accessors (exports 4–13) --------------------

    public void SetMadr(int channel, uint val)
    {
        if (!IsHwChannel(channel)) return;
        _ch[channel].Madr = val & 0x00FFFFFF;
    }

    public uint GetMadr(int channel) =>
        IsHwChannel(channel) ? _ch[channel].Madr : 0;

    public void SetBcr(int channel, uint val)
    {
        if (!IsHwChannel(channel)) return;
        _ch[channel].Bcr = val;
    }

    public uint GetBcr(int channel) =>
        IsHwChannel(channel) ? _ch[channel].Bcr : 0;

    public void SetChcr(int channel, uint val)
    {
        if (!IsHwChannel(channel)) return;
        _ch[channel].Chcr = val;
    }

    public uint GetChcr(int channel) =>
        IsHwChannel(channel) ? _ch[channel].Chcr : 0;

    /// <summary>TADR only exists on SPU (4) and SIF0 (9) in the real module.</summary>
    public void SetTadr(int channel, uint val)
    {
        if (channel != ChSpu && channel != ChSif0) return;
        _ch[channel].Tadr = val & 0x00FFFFFF;
    }

    public uint GetTadr(int channel)
    {
        if (channel != ChSpu && channel != ChSif0) return 0;
        return _ch[channel].Tadr;
    }

    /// <summary>Extra regs BF801560/564/568 for SPU / SIF0 / SIF1 (exports 12–13).</summary>
    public void Set49A(int channel, uint val)
    {
        if (channel != ChSpu && channel != ChSif0 && channel != ChSif1) return;
        _ch[channel].Ext49A = val;
    }

    public uint Get49A(int channel)
    {
        if (channel != ChSpu && channel != ChSif0 && channel != ChSif1) return 0;
        return _ch[channel].Ext49A;
    }

    // -------------------- DPCR / DICR (exports 14–27) --------------------

    public void SetDpcr(uint val) => _dpcr = val;
    public uint GetDpcr() => _dpcr;
    public void SetDpcr2(uint val) => _dpcr2 = val;
    public uint GetDpcr2() => _dpcr2;
    public void SetDpcr3(uint val) => _dpcr3 = val;
    public uint GetDpcr3() => _dpcr3;
    public void SetDicr(uint val) => _dicr = val;
    public uint GetDicr() => _dicr;
    public void SetDicr2(uint val) => _dicr2 = val;
    public uint GetDicr2() => _dicr2;
    public void SetBF80157C(uint val) => _reg57C = val;
    public uint GetBF80157C() => _reg57C;
    public void SetBF801578(uint val) => _reg578 = val;
    public uint GetBF801578() => _reg578;

    // -------------------- high-level ops (exports 28–35) --------------------

    /// <summary>
    /// sceSetSliceDMA(channel, addr, size, count, dir) — export 28.
    /// Returns 1 on success, 0 on invalid channel or OTC.
    /// </summary>
    public int SetSliceDma(int channel, uint addr, uint size, uint count, int dir)
    {
        if (!IsHwChannel(channel) || channel == ChOtc)
            return 0;
        SetMadr(channel, addr);
        SetBcr(channel, (size & 0xFFFF) | (count << 16));
        // dir&1 | 0x200 | (dir==0 ? 0x40000000 : 0)
        uint chcr = (uint)(dir & 1) | 0x200u | ((dir == 0) ? 0x40000000u : 0u);
        SetChcr(channel, chcr);
        _setSliceCount++;
        return 1;
    }

    /// <summary>dmac_set_dma_chained_spu_sif0 — export 29. SPU or SIF0 only.</summary>
    public int SetDmaChainedSpuSif0(int channel, uint size, uint tadr)
    {
        if (channel != ChSpu && channel != ChSif0) return 0;
        SetBcr(channel, size & 0xFFFF);
        SetChcr(channel, 0x601);
        SetTadr(channel, tadr);
        return 1;
    }

    /// <summary>dmac_set_dma_sif0 — export 30.</summary>
    public int SetDmaSif0(int channel, uint size, uint tadr)
    {
        if (channel != ChSif0) return 0;
        SetBcr(ChSif0, size & 0xFFFF);
        SetChcr(ChSif0, 0x701);
        SetTadr(ChSif0, tadr);
        return 1;
    }

    /// <summary>dmac_set_dma_sif1 — export 31.</summary>
    public int SetDmaSif1(int channel, uint size)
    {
        if (channel != ChSif1) return 0;
        SetBcr(ChSif1, size & 0xFFFF);
        SetChcr(ChSif1, 0x40000300);
        return 1;
    }

    /// <summary>
    /// sceStartDMA(channel) — export 32. Sets CHCR.TR then immediately completes the transfer
    /// (clears TR). Real hardware clears TR on completion; without IOP DMAC MMIO we finish
    /// synchronously so pollers never hang. EE SIF transport remains in <see cref="Sif"/>.
    /// </summary>
    public void StartDma(int channel)
    {
        if ((uint)channel >= 0xF) return;
        if (!IsHwChannel(channel)) return;
        _startCount++;
        uint chcr = GetChcr(channel) | ChcrTr;
        SetChcr(channel, chcr);
        // Immediate complete: clear TR, count bytes from BCR.
        CompleteChannel(channel);
    }

    private void CompleteChannel(int channel)
    {
        var c = _ch[channel];
        c.Chcr &= ~ChcrTr;
        uint size = c.Bcr & 0xFFFF;
        uint count = (c.Bcr >> 16) & 0xFFFF;
        // slice: size * count words; chained setups often use count=0 and size as word count
        ulong words = count != 0 ? (ulong)size * count : size;
        _ = words;
        _completeCount++;
        // DICR interrupt flags: if channel IE bit set, set IF bit (bookkeeping).
        // PS1-class DICR: IE in bits 16+ch, IF in bits 24+ch (channels 0–6).
        // DICR2 covers later channels in the same layout for ch 7–12 (HLE extension).
        if (channel <= 6)
        {
            uint ie = 1u << (16 + channel);
            if ((_dicr & ie) != 0)
            {
                _dicr |= 1u << (24 + channel);
                _dicr |= 0x80000000u; // master IF
                _dicrIrqCount++;
            }
        }
        else if (channel < ChannelCount)
        {
            int bit = channel - 7;
            uint ie = 1u << (16 + bit);
            if ((_dicr2 & ie) != 0)
            {
                _dicr2 |= 1u << (24 + bit);
                _dicr2 |= 0x80000000u;
                _dicrIrqCount++;
            }
        }
    }

    /// <summary>sceSetDMAPriority(channel, val) — export 33. val is 0..7 priority field.</summary>
    public void SetDmaPriority(int channel, uint val)
    {
        val &= 7;
        switch (channel)
        {
            case ChMdecIn:
                _dpcr = (_dpcr & ~0x7u) | val; break;
            case ChMdecOut:
                _dpcr = (_dpcr & ~0x70u) | (val << 4); break;
            case ChSif2:
                _dpcr = (_dpcr & ~0x700u) | (val << 8); break;
            case ChCdvd:
                _dpcr = (_dpcr & ~0x7000u) | (val << 12); break;
            case ChSpu:
                _dpcr = (_dpcr & ~0x70000u) | (val << 16); break;
            case ChPio:
                _dpcr = (_dpcr & ~0x700000u) | (val << 20); break;
            case ChOtc:
                _dpcr = (_dpcr & ~0x7000000u) | (val << 24); break;
            case ChSpu2:
                _dpcr2 = (_dpcr2 & ~0x7u) | val; break;
            case ChDev9:
                _dpcr2 = (_dpcr2 & ~0x70u) | (val << 4); break;
            case ChSif0:
                _dpcr2 = (_dpcr2 & ~0x700u) | (val << 8); break;
            case ChSif1:
                _dpcr2 = (_dpcr2 & ~0x7000u) | (val << 12); break;
            case ChSio2In:
                _dpcr2 = (_dpcr2 & ~0x70000u) | (val << 16); break;
            case ChSio2Out:
                _dpcr2 = (_dpcr2 & ~0x700000u) | (val << 20); break;
            case ChFdma0:
                _dpcr3 = (_dpcr3 & ~0x7u) | val; break;
            case ChFdma1:
                _dpcr3 = (_dpcr3 & ~0x70u) | (val << 4); break;
            case ChFdma2:
                _dpcr3 = (_dpcr3 & ~0x700u) | (val << 8); break;
            case ChCpu:
                _dpcr = (_dpcr & ~0x70000000u) | (val << 28); break;
            case ChUsb:
                _dpcr2 = (_dpcr2 & ~0x7000000u) | (val << 24); break;
        }
        if (IsHwChannel(channel))
            _ch[channel].Priority = (int)val;
    }

    /// <summary>sceEnableDMAChannel(channel) — export 34. Sets DPCR enable bit.</summary>
    public void EnableDmaChannel(int channel)
    {
        _enableCount++;
        switch (channel)
        {
            case ChMdecIn: _dpcr |= 0x8; break;
            case ChMdecOut: _dpcr |= 0x80; break;
            case ChSif2: _dpcr |= 0x800; break;
            case ChCdvd: _dpcr |= 0x8000; break;
            case ChSpu: _dpcr |= 0x80000; break;
            case ChPio: _dpcr |= 0x800000; break;
            case ChOtc: _dpcr |= 0x8000000; break;
            case ChSpu2: _dpcr2 |= 0x8; break;
            case ChDev9: _dpcr2 |= 0x80; break;
            case ChSif0: _dpcr2 |= 0x800; break;
            case ChSif1: _dpcr2 |= 0x8000; break;
            case ChSio2In: _dpcr2 |= 0x80000; break;
            case ChSio2Out: _dpcr2 |= 0x800000; break;
            case ChFdma0: _dpcr3 |= 0x8; break;
            case ChFdma1: _dpcr3 |= 0x80; break;
            case ChFdma2: _dpcr3 |= 0x800; break;
            case ChUsb: _dpcr2 |= 0x8000000; break;
        }
        if (IsHwChannel(channel))
            _ch[channel].Enabled = true;
    }

    /// <summary>sceDisableDMAChannel(channel) — export 35.</summary>
    public void DisableDmaChannel(int channel)
    {
        _disableCount++;
        switch (channel)
        {
            case ChMdecIn: _dpcr &= ~0x8u; break;
            case ChMdecOut: _dpcr &= ~0x80u; break;
            case ChSif2: _dpcr &= ~0x800u; break;
            case ChCdvd: _dpcr &= ~0x8000u; break;
            case ChSpu: _dpcr &= ~0x80000u; break;
            case ChPio: _dpcr &= ~0x800000u; break;
            case ChOtc: _dpcr &= ~0x8000000u; break;
            case ChSpu2: _dpcr2 &= ~0x8u; break;
            case ChDev9: _dpcr2 &= ~0x80u; break;
            case ChSif0: _dpcr2 &= ~0x800u; break;
            case ChSif1: _dpcr2 &= ~0x8000u; break;
            case ChSio2In: _dpcr2 &= ~0x80000u; break;
            case ChSio2Out: _dpcr2 &= ~0x800000u; break;
            case ChFdma0: _dpcr3 &= ~0x8u; break;
            case ChFdma1: _dpcr3 &= ~0x80u; break;
            case ChFdma2: _dpcr3 &= ~0x800u; break;
            case ChUsb: _dpcr2 &= ~0x8000000u; break;
        }
        if (IsHwChannel(channel))
            _ch[channel].Enabled = false;
    }

    public bool IsChannelEnabled(int channel) =>
        IsHwChannel(channel) && _ch[channel].Enabled;

    public int GetChannelPriority(int channel) =>
        IsHwChannel(channel) ? _ch[channel].Priority : -1;

    /// <summary>True while CHCR.TR is set (normally false after synchronous StartDMA complete).</summary>
    public bool IsTransferActive(int channel) =>
        IsHwChannel(channel) && (_ch[channel].Chcr & ChcrTr) != 0;

    /// <summary>
    /// Request a channel: enable DPCR bit + SetSliceDMA setup without starting.
    /// Returns 1 if setup accepted, 0 otherwise. Pair with <see cref="StartDma"/> /
    /// <see cref="ReleaseChannel"/>.
    /// </summary>
    public int RequestChannel(int channel, uint addr, uint size, uint count, int dir)
    {
        if (SetSliceDma(channel, addr, size, count, dir) == 0)
            return 0;
        EnableDmaChannel(channel);
        return 1;
    }

    /// <summary>
    /// Release a channel: clear TR, zero MADR/BCR/CHCR/TADR, disable DPCR enable bit.
    /// Completes the channel lifecycle after Request/Start.
    /// </summary>
    public int ReleaseChannel(int channel)
    {
        if (!IsHwChannel(channel)) return 0;
        var c = _ch[channel];
        c.Chcr &= ~ChcrTr;
        c.Madr = 0;
        c.Bcr = 0;
        c.Chcr = 0;
        c.Tadr = 0;
        c.Ext49A = 0;
        DisableDmaChannel(channel);
        _releaseCount++;
        return 1;
    }

    /// <summary>
    /// Enable DICR interrupt for a channel (IE bit). After StartDMA complete, IF bit latches.
    /// ch 0–6 → DICR; ch 7–12 → DICR2.
    /// </summary>
    public void SetChannelInterruptEnable(int channel, bool enable)
    {
        if (channel >= 0 && channel <= 6)
        {
            uint ie = 1u << (16 + channel);
            if (enable) _dicr |= ie;
            else _dicr &= ~ie;
        }
        else if (channel >= 7 && channel < ChannelCount)
        {
            int bit = channel - 7;
            uint ie = 1u << (16 + bit);
            if (enable) _dicr2 |= ie;
            else _dicr2 &= ~ie;
        }
    }

    /// <summary>True when DICR/DICR2 IF bit is latched for the channel.</summary>
    public bool IsChannelInterruptPending(int channel)
    {
        if (channel >= 0 && channel <= 6)
            return (_dicr & (1u << (24 + channel))) != 0;
        if (channel >= 7 && channel < ChannelCount)
        {
            int bit = channel - 7;
            return (_dicr2 & (1u << (24 + bit))) != 0;
        }
        return false;
    }

    /// <summary>Clear DICR/DICR2 IF bit for a channel (and master IF when no others remain).</summary>
    public void AcknowledgeChannelInterrupt(int channel)
    {
        if (channel >= 0 && channel <= 6)
        {
            _dicr &= ~(1u << (24 + channel));
            if ((_dicr & 0x7F000000u) == 0)
                _dicr &= ~0x80000000u;
        }
        else if (channel >= 7 && channel < ChannelCount)
        {
            int bit = channel - 7;
            _dicr2 &= ~(1u << (24 + bit));
            if ((_dicr2 & 0x3F000000u) == 0)
                _dicr2 &= ~0x80000000u;
        }
    }

    /// <summary>
    /// Convenience used by HLE drivers: enable + set slice + start in one call.
    /// Returns 1 if setup accepted, 0 otherwise.
    /// </summary>
    public int RequestAndStart(int channel, uint addr, uint size, uint count, int dir)
    {
        if (RequestChannel(channel, addr, size, count, dir) == 0)
            return 0;
        StartDma(channel);
        return 1;
    }

    /// <summary>dmacman_deinit — stop active channels, clear master enable.</summary>
    public int Deinit()
    {
        for (int i = 0; i < ChannelCount; i++)
        {
            uint v = GetChcr(i);
            if ((v & ChcrTr) != 0)
                SetChcr(i, v & ~ChcrTr);
        }
        _reg578 = 0;
        _started = false;
        return 1;
    }
}
