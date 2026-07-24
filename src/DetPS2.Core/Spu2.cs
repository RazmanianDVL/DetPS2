using System;

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
    private bool _irqPending;
    private Intc? _intc;

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
        _irqPending = false;
        ReverbEnabled = false;
        _reverbPos = 0;
        Array.Clear(_reverbL);
        Array.Clear(_reverbR);
        for (int i = 0; i < VoiceCount; i++)
            _voices[i] = new Voice();
    }

    public uint ReadRegister(uint address)
    {
        Reads++;
        uint idx = (address & 0x3FF) / 4;
        if (idx < _regs.Length) return _regs[idx];
        return 0;
    }

    public void WriteRegister(uint address, uint value)
    {
        Writes++;
        uint off = address & 0x3FF;
        uint idx = off / 4;
        if (idx < _regs.Length)
            _regs[idx] = value;
        if (off == 0x1A0)
            Enabled = (value & 1) != 0;
        if (off is >= 0x1A8 and <= 0x1B0)
            Enabled = true;
        // Key-on low voices 0-15
        if (off == 0x1A8 || off == 0x1AA)
        {
            Enabled = true;
            KeyOnMask(value, start: 0);
        }
        if (off == 0x1AC || off == 0x1AE)
        {
            Enabled = true;
            KeyOnMask(value, start: 16);
        }
    }

    private void KeyOnMask(uint mask, int start)
    {
        for (int i = 0; i < 16 && start + i < VoiceCount; i++)
        {
            if (((mask >> i) & 1) == 0) continue;
            var v = _voices[start + i];
            v.KeyOn = true;
            v.Playing = true;
            v.SamplePos = 0;
            v.AdsrPhase = 0;
            v.Envelope = 0;
            if (v.Pcm == null || v.Pcm.Length == 0)
                v.Pcm = GenerateSquarePcm(ToneFrequencyHz, ToneAmplitude, OutputSampleRate / 4);
        }
    }

    /// <summary>Load PCM samples into a voice (tests / HLE).</summary>
    public void LoadVoicePcm(int voice, short[] pcm)
    {
        if ((uint)voice >= VoiceCount) return;
        _voices[voice].Pcm = pcm;
        _voices[voice].SamplePos = 0;
    }

    /// <summary>Decode one PSX/PS2-style ADPCM block (16 bytes → 28 samples).</summary>
    public static short[] DecodeAdpcmBlock(ReadOnlySpan<byte> block)
    {
        // Minimal SPU-ADPCM: filter 0, range from nibble
        short[] outS = new short[28];
        if (block.Length < 16) return outS;
        int shift = block[0] & 0xF;
        int filter = (block[0] >> 4) & 0x7;
        int o = 0;
        short hist1 = 0, hist2 = 0;
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
        return outS;
    }

    /// <summary>Decode ADPCM stream into voice PCM.</summary>
    public void LoadVoiceAdpcm(int voice, ReadOnlySpan<byte> adpcm)
    {
        if ((uint)voice >= VoiceCount) return;
        int blocks = adpcm.Length / 16;
        var list = new short[blocks * 28];
        int p = 0;
        for (int b = 0; b < blocks; b++)
        {
            var s = DecodeAdpcmBlock(adpcm.Slice(b * 16, 16));
            s.CopyTo(list.AsSpan(p));
            p += 28;
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
                _irqPending = true;
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
}
