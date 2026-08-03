using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// SPU2 (Phases 17/30): registers + multi-voice mix with ADPCM decode + ADSR envelope.
/// Sample rate 48000 Hz; CyclesPerSample = 6144 (EE clock model).
/// </summary>
public sealed class Spu2 : ISchedulable
{
    public const uint PhysBase = 0x1F900000;
    public const uint MmioAlias = 0x1000F500;
    public const int OutputSampleRate = 48000;
    public const ulong CyclesPerSample = 6144;
    public const int VoiceCount = 24;

    private readonly uint[] _regs = new uint[256];
    private readonly short[] _mixScratch = new short[4096];
    private readonly Voice[] _voices = new Voice[VoiceCount];
    private ulong _cycleAccum;
    private int _phase; // global tone phase fallback
    private IAudioSink? _sink;
    private Intc? _intc;

    // Real SPU2 local work RAM (2MB, confirmed size) games upload ADPCM sample data
    // into via the transfer-address/data register pair, then reference by byte offset
    // from each voice's SSA (sample start address) register on key-on.
    private const int SpuRamSize = 2 * 1024 * 1024;
    private readonly byte[] _spuRam = new byte[SpuRamSize];
    private uint _transferAddr;
    private readonly uint[] _voiceSsa = new uint[VoiceCount];

    public ulong Writes { get; private set; }
    public ulong Reads { get; private set; }
    public bool Enabled { get; private set; }
    public ulong SamplesGenerated { get; private set; }
    public ulong AdpcmBlocksDecoded { get; private set; }
    public ulong VoiceEnds { get; private set; }
    public int ToneFrequencyHz { get; set; } = 440;
    public short ToneAmplitude { get; set; } = 2000;
    /// <summary>
    /// When true and no voices are active, emit a test square wave (causes pops with host audio).
    /// Default false for retail boots — silence until real ADPCM/voices play.
    /// </summary>
    public bool UseSimpleToneFallback { get; set; }
    /// <summary>Phase 43: simple comb reverb on mix bus.</summary>
    public bool ReverbEnabled { get; set; }
    public int ReverbDelaySamples { get; set; } = 2400; // 50ms @ 48k
    public int ReverbFeedback { get; set; } = 40; // percent
    private readonly short[] _reverbL = new short[8192];
    private readonly short[] _reverbR = new short[8192];
    private int _reverbPos;

    private sealed class Voice
    {
        public bool KeyOn;
        public bool Playing;
        public int SamplePos;
        public short[]? Pcm;
        public int VolumeL = 0x3FFF;
        public int VolumeR = 0x3FFF;
        public int Pitch = 0x1000; // 0x1000 = 1.0
        public int AdsrPhase; // 0=attack 1=decay 2=sustain 3=release 4=off
        public int Envelope = 0x7FFF;
        public int AttackRate = 0x10;
        public int DecayRate = 0x10;
        public int SustainLevel = 0x4000;
        public int ReleaseRate = 0x20;
        public bool EndIrq;
    }

    public Spu2()
    {
        for (int i = 0; i < VoiceCount; i++)
            _voices[i] = new Voice();
    }

    public void SetSink(IAudioSink? sink) => _sink = sink;
    public void SetIntc(Intc? intc) => _intc = intc;

    public void Reset()
    {
        Array.Clear(_regs);
        Writes = Reads = 0;
        Enabled = false;
        SamplesGenerated = 0;
        AdpcmBlocksDecoded = 0;
        VoiceEnds = 0;
        _cycleAccum = 0;
        _phase = 0;
        ReverbEnabled = false;
        _reverbPos = 0;
        Array.Clear(_reverbL);
        Array.Clear(_reverbR);
        for (int i = 0; i < VoiceCount; i++)
            _voices[i] = new Voice();
        Array.Clear(_spuRam);
        Array.Clear(_voiceSsa);
        _transferAddr = 0;
    }

    /// <summary>SPU2 state for SaveState.cs — previously not saved at all, so a load resumed
    /// with every voice silent and the 2MB SPU RAM (ADPCM sample data games DMA in once and
    /// re-key-on repeatedly, not re-upload every playback) empty, breaking audio playback
    /// after any load until the game happened to re-upload samples on its own.</summary>
    public void WriteState(System.IO.BinaryWriter w)
    {
        for (int i = 0; i < _regs.Length; i++) w.Write(_regs[i]);
        w.Write(_cycleAccum);
        w.Write(_phase);
        w.Write(Writes); w.Write(Reads);
        w.Write(Enabled);
        w.Write(SamplesGenerated); w.Write(AdpcmBlocksDecoded); w.Write(VoiceEnds);
        w.Write(ToneFrequencyHz); w.Write(ToneAmplitude);
        w.Write(UseSimpleToneFallback);
        w.Write(ReverbEnabled); w.Write(ReverbDelaySamples); w.Write(ReverbFeedback);
        for (int i = 0; i < _reverbL.Length; i++) w.Write(_reverbL[i]);
        for (int i = 0; i < _reverbR.Length; i++) w.Write(_reverbR[i]);
        w.Write(_reverbPos);
        w.Write(_spuRam.Length);
        w.Write(_spuRam);
        for (int i = 0; i < _voiceSsa.Length; i++) w.Write(_voiceSsa[i]);
        w.Write(_transferAddr);

        w.Write(_voices.Length);
        foreach (var v in _voices)
        {
            w.Write(v.KeyOn); w.Write(v.Playing); w.Write(v.SamplePos);
            w.Write(v.Pcm != null);
            if (v.Pcm != null) { w.Write(v.Pcm.Length); foreach (var s in v.Pcm) w.Write(s); }
            w.Write(v.VolumeL); w.Write(v.VolumeR); w.Write(v.Pitch);
            w.Write(v.AdsrPhase); w.Write(v.Envelope);
            w.Write(v.AttackRate); w.Write(v.DecayRate); w.Write(v.SustainLevel); w.Write(v.ReleaseRate);
            w.Write(v.EndIrq);
        }
    }

    public void ReadState(System.IO.BinaryReader r)
    {
        for (int i = 0; i < _regs.Length; i++) _regs[i] = r.ReadUInt32();
        _cycleAccum = r.ReadUInt64();
        _phase = r.ReadInt32();
        Writes = r.ReadUInt64(); Reads = r.ReadUInt64();
        Enabled = r.ReadBoolean();
        SamplesGenerated = r.ReadUInt64(); AdpcmBlocksDecoded = r.ReadUInt64(); VoiceEnds = r.ReadUInt64();
        ToneFrequencyHz = r.ReadInt32(); ToneAmplitude = r.ReadInt16();
        UseSimpleToneFallback = r.ReadBoolean();
        ReverbEnabled = r.ReadBoolean(); ReverbDelaySamples = r.ReadInt32(); ReverbFeedback = r.ReadInt32();
        for (int i = 0; i < _reverbL.Length; i++) _reverbL[i] = r.ReadInt16();
        for (int i = 0; i < _reverbR.Length; i++) _reverbR[i] = r.ReadInt16();
        _reverbPos = r.ReadInt32();
        int spuLen = r.ReadInt32();
        byte[] spu = r.ReadBytes(spuLen);
        Buffer.BlockCopy(spu, 0, _spuRam, 0, Math.Min(spuLen, _spuRam.Length));
        for (int i = 0; i < _voiceSsa.Length; i++) _voiceSsa[i] = r.ReadUInt32();
        _transferAddr = r.ReadUInt32();

        int n = r.ReadInt32();
        for (int i = 0; i < n && i < _voices.Length; i++)
        {
            var v = _voices[i];
            v.KeyOn = r.ReadBoolean(); v.Playing = r.ReadBoolean(); v.SamplePos = r.ReadInt32();
            bool hasPcm = r.ReadBoolean();
            if (hasPcm)
            {
                int pcmLen = r.ReadInt32();
                v.Pcm = new short[pcmLen];
                for (int j = 0; j < pcmLen; j++) v.Pcm[j] = r.ReadInt16();
            }
            else v.Pcm = null;
            v.VolumeL = r.ReadInt32(); v.VolumeR = r.ReadInt32(); v.Pitch = r.ReadInt32();
            v.AdsrPhase = r.ReadInt32(); v.Envelope = r.ReadInt32();
            v.AttackRate = r.ReadInt32(); v.DecayRate = r.ReadInt32(); v.SustainLevel = r.ReadInt32(); v.ReleaseRate = r.ReadInt32();
            v.EndIrq = r.ReadBoolean();
        }
    }

    public uint ReadRegister(uint address)
    {
        Reads++;
        uint idx = (address & 0x3FF) / 4;
        uint val = idx < _regs.Length ? _regs[idx] : 0;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_SPU2REG") == "1")
            Console.Error.WriteLine($"[SPU2-REG-R] addr=0x{address:X8} off=0x{address & 0x3FF:X3} val=0x{val:X8} reads={Reads}");
        return val;
    }

    public void WriteRegister(uint address, uint value)
    {
        Writes++;
        uint off = address & 0x3FF;
        uint idx = off / 4;
        if (idx < _regs.Length)
            _regs[idx] = value;

        // Per-voice core block: each of the 24 voices owns a 16-byte window
        // (VOLL/VOLR/PITCH/ADSR1/ADSR2/ENVX/VOLXL/VOLXR at +0x0.._0xE), the
        // same layout PS1 SPU established and PS2 SPU2 kept for voices 0-23.
        if (off < 0x180)
        {
            int voice = (int)(off / 0x10);
            uint reg = off % 0x10;
            var v = _voices[voice];
            switch (reg)
            {
                case 0x0: v.VolumeL = SignExtend16(value); break;
                case 0x2: v.VolumeR = SignExtend16(value); break;
                case 0x4: v.Pitch = (int)(value & 0x3FFF); break;
                case 0x6: ApplyAdsr1(v, value); break;
                case 0x8: ApplyAdsr2(v, value); break;
            }
        }

        // Real SPU2 register map (verified against PCSX2 zerospu2/reg.h): 0x1A0/0x1A2 =
        // SPUON1/2 (key-on bitmask, voices 0-15 / 16-23), 0x1A4/0x1A6 = SPUOFF1/2
        // (key-off), 0x1A8/0x1AA = transfer address hi/lo into SPU2's own local RAM,
        // 0x1AC = transfer data port (each write appends 16 bits and auto-increments).
        // An earlier version of this mapped 0x1A8-0x1AE as key-on instead — wrong, and
        // it meant there was never a way for real ADPCM sample data to reach SPU2 RAM
        // in the first place, so playback could only ever fall back to a synthetic tone.
        switch (off)
        {
            case 0x1A0: Enabled = true; KeyOnMask(value, start: 0); break;
            case 0x1A2: Enabled = true; KeyOnMask(value, start: 16); break;
            case 0x1A4: KeyOffMask(value, start: 0); break;
            case 0x1A6: KeyOffMask(value, start: 16); break;
            case 0x1A8: _transferAddr = (_transferAddr & 0x0000FFFFu) | (value << 16); break;
            case 0x1AA: _transferAddr = (_transferAddr & 0xFFFF0000u) | (value & 0xFFFFu); break;
            case 0x1AC:
                if (_transferAddr + 1 < _spuRam.Length)
                {
                    _spuRam[_transferAddr] = (byte)value;
                    _spuRam[_transferAddr + 1] = (byte)(value >> 8);
                }
                _transferAddr += 2;
                break;
        }

        // Per-voice SSA (waveform/ADPCM start address in SPU2 RAM). Real hardware
        // documents one SSA register; the per-voice offset/stride here (0x1C0 + voice*0xC)
        // is a reasoned inference from the address gap to Core 1's base, not an
        // independently confirmed layout — worth validating against real boot telemetry
        // if voice playback ever starts from a visibly wrong offset.
        if (off >= 0x1C0 && off < 0x1C0 + VoiceCount * 0xC)
        {
            int voice = (int)((off - 0x1C0) / 0xC);
            uint reg = (off - 0x1C0) % 0xC;
            if (reg == 0) _voiceSsa[voice] = value;
        }
    }

    private static int SignExtend16(uint v) => (short)(v & 0xFFFF);

    // ADSR1: bits 0-3 sustain-rate step size (unused here), 4-9 decay shift,
    // 10-14 attack shift, 15 attack mode. ADSR2: 0-4 release shift, 5 release mode,
    // 6-9 sustain shift bits, 10-14 sustain level index. Rates are simplified to a
    // linear step-per-sample scale (see TickAdsr) rather than PSX's exact
    // exponential/pseudo-exponential curve — real curve shape is future accuracy work.
    private static void ApplyAdsr1(Voice v, uint value)
    {
        int decayShift = (int)(value >> 4) & 0xF;
        int attackShift = (int)(value >> 10) & 0x1F;
        v.AttackRate = Math.Max(1, 0x20 - attackShift);
        v.DecayRate = Math.Max(1, 0x20 - decayShift);
    }

    private static void ApplyAdsr2(Voice v, uint value)
    {
        int releaseShift = (int)value & 0x1F;
        int sustainLevelIdx = (int)(value >> 10) & 0x1F;
        v.ReleaseRate = Math.Max(1, 0x20 - releaseShift);
        v.SustainLevel = (sustainLevelIdx + 1) * (0x7FFF / 32);
    }

    private void KeyOnMask(uint mask, int start)
    {
        for (int i = 0; i < 16 && start + i < VoiceCount; i++)
        {
            if (((mask >> i) & 1) == 0) continue;
            int voice = start + i;
            var v = _voices[voice];
            v.KeyOn = true;
            v.Playing = true;
            v.SamplePos = 0;
            v.AdsrPhase = 0;
            v.Envelope = 0;

            // Real playback: if the game configured a sample start address, decode
            // whatever ADPCM data actually sits in SPU2 RAM there — same as real
            // hardware, which doesn't know or care whether upload "succeeded".
            uint ssa = _voiceSsa[voice];
            v.Pcm = ssa != 0 && ssa < (uint)_spuRam.Length ? DecodeAdpcmFromRam(ssa) : null;
            if (v.Pcm == null || v.Pcm.Length == 0)
                v.Pcm = GenerateSquarePcm(ToneFrequencyHz, ToneAmplitude, OutputSampleRate / 4);
        }
    }

    private void KeyOffMask(uint mask, int start)
    {
        for (int i = 0; i < 16 && start + i < VoiceCount; i++)
        {
            if (((mask >> i) & 1) != 0)
                ReleaseVoice(start + i);
        }
    }

    /// <summary>Decode consecutive 16-byte ADPCM blocks from SPU2 RAM starting at
    /// <paramref name="startOffset"/> until the loop-end flag (block header byte[1] bit0)
    /// is set — standard SPU-ADPCM flag byte convention shared across the PS1/PS2 SPU
    /// family. Carries predictor history across block boundaries (required for correct
    /// reconstruction; resetting it per block, as an earlier version of this did,
    /// produces an audible discontinuity every 16 bytes / 28 samples).</summary>
    private short[] DecodeAdpcmFromRam(uint startOffset, int maxBlocks = 8192)
    {
        var samples = new List<short>(Math.Min(maxBlocks, 256) * 28);
        short hist1 = 0, hist2 = 0;
        uint offset = startOffset;
        Span<short> block28 = stackalloc short[28];
        for (int b = 0; b < maxBlocks && offset + 16 <= _spuRam.Length; b++)
        {
            var block = _spuRam.AsSpan((int)offset, 16);
            byte flags = block[1];
            DecodeAdpcmBlock(block, ref hist1, ref hist2, block28);
            samples.AddRange(block28.ToArray());
            AdpcmBlocksDecoded++;
            offset += 16;
            if ((flags & 1) != 0) break; // loop end
        }
        return samples.ToArray();
    }

    /// <summary>Load PCM samples into a voice (tests / HLE).</summary>
    public void LoadVoicePcm(int voice, short[] pcm)
    {
        if ((uint)voice >= VoiceCount) return;
        _voices[voice].Pcm = pcm;
        _voices[voice].SamplePos = 0;
    }

    /// <summary>Decode one PSX/PS2-style ADPCM block (16 bytes → 28 samples), starting
    /// from zero predictor history. For multi-block streams, use the ref-history overload
    /// instead — starting each block fresh from zero produces an audible discontinuity
    /// at every block boundary, since ADPCM prediction depends on the previous block's
    /// tail samples.</summary>
    public static short[] DecodeAdpcmBlock(ReadOnlySpan<byte> block)
    {
        short hist1 = 0, hist2 = 0;
        Span<short> outS = stackalloc short[28];
        DecodeAdpcmBlock(block, ref hist1, ref hist2, outS);
        return outS.ToArray();
    }

    /// <summary>Decode one ADPCM block, carrying predictor history in/out via
    /// <paramref name="hist1"/>/<paramref name="hist2"/> — pass the same variables across
    /// consecutive calls for a real multi-block stream.</summary>
    public static void DecodeAdpcmBlock(ReadOnlySpan<byte> block, ref short hist1, ref short hist2, Span<short> outS)
    {
        if (block.Length < 16 || outS.Length < 28) return;
        int shift = block[0] & 0xF;
        int filter = (block[0] >> 4) & 0x7;
        int o = 0;
        // Predictor coeffs (common set)
        int f0 = filter switch { 1 => 60, 2 => 115, 3 => 98, 4 => 122, _ => 0 };
        int f1 = filter switch { 2 => -52, 3 => -55, 4 => -60, _ => 0 };
        for (int i = 2; i < 16 && o < 28; i++)
        {
            int b = block[i];
            for (int n = 0; n < 2 && o < 28; n++)
            {
                int nib = (n == 0) ? (b & 0xF) : (b >> 4);
                if (nib >= 8) nib -= 16;
                int sample = (nib << 12) >> shift;
                sample += (hist1 * f0 + hist2 * f1) / 64;
                if (sample > 32767) sample = 32767;
                if (sample < -32768) sample = -32768;
                outS[o++] = (short)sample;
                hist2 = hist1;
                hist1 = (short)sample;
            }
        }
    }

    /// <summary>Decode ADPCM stream into voice PCM.</summary>
    public void LoadVoiceAdpcm(int voice, ReadOnlySpan<byte> adpcm)
    {
        if ((uint)voice >= VoiceCount) return;
        int blocks = adpcm.Length / 16;
        var list = new short[blocks * 28];
        short hist1 = 0, hist2 = 0;
        for (int b = 0; b < blocks; b++)
        {
            DecodeAdpcmBlock(adpcm.Slice(b * 16, 16), ref hist1, ref hist2, list.AsSpan(b * 28, 28));
            AdpcmBlocksDecoded++;
        }
        _voices[voice].Pcm = list;
        _voices[voice].SamplePos = 0;
        _voices[voice].Playing = true;
        _voices[voice].KeyOn = true;
        _voices[voice].AdsrPhase = 0;
        Enabled = true;
    }

    public void MixSilence(Span<short> output) => output.Clear();

    public int Step(ulong maxCycles)
    {
        if (maxCycles == 0) return 0;
        _cycleAccum += maxCycles;

        int produced = 0;
        int outIdx = 0;
        while (_cycleAccum >= CyclesPerSample && outIdx + 1 < _mixScratch.Length)
        {
            _cycleAccum -= CyclesPerSample;
            int mixL = 0, mixR = 0;
            bool anyVoice = false;

            for (int vi = 0; vi < VoiceCount; vi++)
            {
                var v = _voices[vi];
                if (!v.Playing || v.Pcm == null || v.Pcm.Length == 0) continue;
                anyVoice = true;
                TickAdsr(v);
                if (v.AdsrPhase >= 4) { v.Playing = false; VoiceEnds++; v.EndIrq = true; continue; }

                int idx = v.SamplePos;
                if (idx >= v.Pcm.Length)
                {
                    v.Playing = false;
                    VoiceEnds++;
                    v.EndIrq = true;
                    continue;
                }
                int s = v.Pcm[idx] * v.Envelope / 0x7FFF;
                mixL += s * v.VolumeL / 0x3FFF;
                mixR += s * v.VolumeR / 0x3FFF;
                // Pitch: advance 1 sample at 0x1000
                v.SamplePos += Math.Max(1, v.Pitch / 0x1000);
            }

            short sl, sr;
            if (anyVoice)
            {
                sl = (short)Math.Clamp(mixL, short.MinValue, short.MaxValue);
                sr = (short)Math.Clamp(mixR, short.MinValue, short.MaxValue);
            }
            else if (Enabled && UseSimpleToneFallback)
            {
                int period = Math.Max(1, OutputSampleRate / Math.Max(1, ToneFrequencyHz));
                short s = ((_phase / (period / 2 + 1)) % 2 == 0) ? ToneAmplitude : (short)(-ToneAmplitude);
                _phase++;
                sl = sr = s;
            }
            else
            {
                sl = sr = 0;
            }

            if (ReverbEnabled)
            {
                int d = Math.Clamp(ReverbDelaySamples, 1, _reverbL.Length - 1);
                int rp = (_reverbPos + _reverbL.Length - d) % _reverbL.Length;
                int fb = ReverbFeedback;
                int rl = _reverbL[rp] * fb / 100;
                int rr = _reverbR[rp] * fb / 100;
                int ol = Math.Clamp(sl + rl, short.MinValue, short.MaxValue);
                int orr = Math.Clamp(sr + rr, short.MinValue, short.MaxValue);
                _reverbL[_reverbPos] = (short)ol;
                _reverbR[_reverbPos] = (short)orr;
                _reverbPos = (_reverbPos + 1) % _reverbL.Length;
                sl = (short)ol;
                sr = (short)orr;
            }

            _mixScratch[outIdx++] = sl;
            _mixScratch[outIdx++] = sr;
            SamplesGenerated += 2;
            produced += 2;
        }

        // End IRQ
        for (int i = 0; i < VoiceCount; i++)
        {
            if (_voices[i].EndIrq)
            {
                _voices[i].EndIrq = false;
                _intc?.Raise(Intc.InterruptSource.SbUs); // stand-in for SPU2 IRQ
            }
        }

        if (produced > 0 && _sink != null)
            _sink.Submit(_mixScratch.AsSpan(0, produced));

        return (int)Math.Min(maxCycles, (ulong)int.MaxValue);
    }

    private static void TickAdsr(Voice v)
    {
        switch (v.AdsrPhase)
        {
            case 0: // attack
                v.Envelope += v.AttackRate * 64;
                if (v.Envelope >= 0x7FFF) { v.Envelope = 0x7FFF; v.AdsrPhase = 1; }
                break;
            case 1: // decay
                v.Envelope -= v.DecayRate * 32;
                if (v.Envelope <= v.SustainLevel) { v.Envelope = v.SustainLevel; v.AdsrPhase = 2; }
                break;
            case 2: // sustain hold
                break;
            case 3: // release
                v.Envelope -= v.ReleaseRate * 64;
                if (v.Envelope <= 0) { v.Envelope = 0; v.AdsrPhase = 4; }
                break;
        }
        if (v.Envelope < 0) v.Envelope = 0;
        if (v.Envelope > 0x7FFF) v.Envelope = 0x7FFF;
    }

    private static short[] GenerateSquarePcm(int freq, short amp, int samples)
    {
        var a = new short[Math.Max(8, samples)];
        int period = Math.Max(1, OutputSampleRate / Math.Max(1, freq));
        for (int i = 0; i < a.Length; i++)
            a[i] = ((i / (period / 2 + 1)) % 2 == 0) ? amp : (short)(-amp);
        return a;
    }

    public void ReleaseVoice(int voice)
    {
        if ((uint)voice >= VoiceCount) return;
        _voices[voice].AdsrPhase = 3;
    }

    public bool IsVoicePlaying(int voice) =>
        (uint)voice < VoiceCount && _voices[voice].Playing;

    // ---- LIBSD-facing host hooks (IopLibSdHost) ---------------------------------

    /// <summary>Key-on a voice bitmask (voices <paramref name="start"/>..+15). LIBSD <c>sceSdSetSwitch(KON)</c>.</summary>
    public void HostKeyOnMask(uint mask, int start = 0)
    {
        Enabled = true;
        KeyOnMask(mask, start);
    }

    /// <summary>Key-off a voice bitmask. LIBSD <c>sceSdSetSwitch(KOFF)</c>.</summary>
    public void HostKeyOffMask(uint mask, int start = 0) => KeyOffMask(mask, start);

    /// <summary>Set sample-start address in SPU RAM for a voice (LIBSD <c>sceSdSetAddr(SSA)</c>).</summary>
    public void HostSetVoiceSsa(int voice, uint ssa)
    {
        if ((uint)voice >= VoiceCount) return;
        _voiceSsa[voice] = ssa;
    }

    public uint HostGetVoiceSsa(int voice) =>
        (uint)voice < VoiceCount ? _voiceSsa[voice] : 0;

    /// <summary>Direct voice param poke used by LIBSD <c>sceSdSetParam</c> without full MMIO.</summary>
    public void HostSetVoiceParam(int voice, int paramKind, int value)
    {
        if ((uint)voice >= VoiceCount) return;
        var v = _voices[voice];
        switch (paramKind)
        {
            case 0: // VOLL
                v.VolumeL = SignExtend16((uint)value);
                break;
            case 1: // VOLR
                v.VolumeR = SignExtend16((uint)value);
                break;
            case 2: // PITCH
                v.Pitch = value & 0x3FFF;
                break;
            case 3: // ADSR1
                ApplyAdsr1(v, (uint)value);
                break;
            case 4: // ADSR2
                ApplyAdsr2(v, (uint)value);
                break;
        }
    }

    public int HostGetVoiceParam(int voice, int paramKind)
    {
        if ((uint)voice >= VoiceCount) return 0;
        var v = _voices[voice];
        return paramKind switch
        {
            0 => v.VolumeL & 0xFFFF,
            1 => v.VolumeR & 0xFFFF,
            2 => v.Pitch & 0x3FFF,
            5 => v.Envelope & 0x7FFF, // ENVX
            _ => 0
        };
    }

    /// <summary>Upload bytes into SPU RAM at <paramref name="addr"/> (LIBSD VoiceTrans WRITE).</summary>
    public int HostWriteSpuRam(uint addr, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return 0;
        int written = 0;
        for (int i = 0; i < data.Length && addr + (uint)i < (uint)_spuRam.Length; i++)
        {
            _spuRam[addr + (uint)i] = data[i];
            written++;
        }
        return written;
    }

    public int HostReadSpuRam(uint addr, Span<byte> dest)
    {
        int n = 0;
        for (int i = 0; i < dest.Length && addr + (uint)i < (uint)_spuRam.Length; i++)
        {
            dest[i] = _spuRam[addr + (uint)i];
            n++;
        }
        return n;
    }
}
