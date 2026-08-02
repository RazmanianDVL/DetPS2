using System;
using System.Collections.Generic;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Graphics Synthesizer — Phase 7 software renderer.
/// GIF → registers → primitive assembly → rasterize → framebuffer.
/// Deterministic; integer-friendly depth (24-bit fixed as float for convenience).
/// </summary>
public sealed class Gs : ISchedulable
{
    public const int FB_WIDTH = 640;
    public const int FB_HEIGHT = 448;

    public SystemMemory Memory { get; }
    public GsRegisters Registers { get; } = new();

    private readonly uint[] _framebuffer = new uint[FB_WIDTH * FB_HEIGHT];
    private readonly float[] _depthBuffer = new float[FB_WIDTH * FB_HEIGHT];

    // Local GS memory for BITBLT / IMAGE (1MB word-addressable subset)
    private readonly byte[] _localMem = new byte[4 * 1024 * 1024];

    /// <summary>
    /// Legacy host present overlay (retired boot-FMV path). Kept only so older assist code that
    /// still calls <see cref="SetHostOverlay"/> / <see cref="ClearHostOverlay"/> compiles and is
    /// a no-op for Soft-GS truth: IRX-era presentation must come from the software raster FB
    /// (GIF → GS prims / honest IPU), never host-decoded logos. See docs/irx/SOFTGS_IRX_ERA.md.
    /// </summary>
    private uint[]? _hostOverlay;
    private bool _hostOverlayActive;

    // Host→local IMAGE transfer (BITBLTBUF/TRXPOS/TRXREG/TRXDIR). Commercial GIF PATH3 IMAGE
    // streams land here; linear dest=0 uploads were swizzle-inconsistent with SampleTexel.
    private bool _trxActive;
    private int _trxX, _trxY;
    private int _trxW, _trxH;
    private int _trxDsaX, _trxDsaY;
    private uint _trxDbpBytes;
    private int _trxDbwPx;
    private int _trxDpsm;
    private int _trxPending; // leftover bytes when packing multi-byte pixels across QWs
    private uint _trxPartial;
    // Largest completed Host→Local IMAGE (for residual present when DISPFB FB is empty).
    // GoW: PSMT4 @ DBP=0xA0800 while DISPFB FBP=0x1A0000 PSMCT24 is black.
    private uint _lastImageDbpBytes;
    private int _lastImageDbwPx;
    private int _lastImageDpsm;
    private int _lastImageW, _lastImageH;
    private int _lastImageDsaX, _lastImageDsaY;
    private long _lastImageByteCount;

    /// <summary>Cached DETPS2_TRACE_GS — never re-parse process env on XYZ path.</summary>
    private static readonly bool TraceGs = Environment.GetEnvironmentVariable("DETPS2_TRACE_GS") == "1";

    private uint _currentPrim;
    private uint _currentRgbaq = 0xFFFFFFFF;
    private float _lastU, _lastV, _lastS = 1f, _lastT = 1f, _lastQ = 1f;
    private float _lastFog;
    private int _texWidth = 64, _texHeight = 64;
    /// <summary>Texture buffer width in pixels (TEX0.TBW×64) used for swizzle addressing.</summary>
    private int _texBufWidth = 64;
    private uint _texBase;
    private bool _useProceduralTexture = true;
    private uint _clutBase;
    private readonly uint[] _clut = new uint[256]; // PSMCT32 palette
    private bool _hasClut;
    /// <summary>GX-035: TEX0/TEX2 was programmed this session — sample local mem, not checker.</summary>
    public bool Tex0Valid => !_useProceduralTexture;
    /// <summary>Telemetry: texels sampled from local mem (non-procedural).</summary>
    public long TexSamplesLocal { get; private set; }
    public long FragmentsRejectedAlpha { get; private set; }
    public long TexFlushCount { get; private set; }
    /// <summary>Phase 42: nearest (false) or bilinear (true) when sampling non-procedural textures.</summary>
    public bool BilinearFilter { get; set; }
    public long BilinearSamples { get; private set; }

    private readonly List<Vertex> _verts = new(16);
    private int _stripCount;

    // Stats (tests)
    public long PrimitivesDrawn { get; private set; }
    public long PixelsWritten { get; private set; }
    public long FragmentsTested { get; private set; }
    public long FragmentsRejectedDepth { get; private set; }
    /// <summary>Bytes accepted by IMAGE / BITBLT host→local (telemetry).</summary>
    public long ImageBytesWritten { get; private set; }
    /// <summary>Pixels last composited from DISPFB/FRAME local VRAM into the software FB.</summary>
    public long DispfbPixelsComposited { get; private set; }
    /// <summary>
    /// GX-040/041: pixels composited from a software-programmed DISPFB (not FRAME/FBP0 fallback).
    /// Zero when present is residual composite-only (B3-class: DISPFB1=0 → FRAME path).
    /// </summary>
    public long NaturalDispfbPixels { get; private set; }
    /// <summary>
    /// GX-041: residual composite pixels (FRAME + FBP0 synthetic) — dispfbPx − naturalDispfbPx.
    /// Honest B3 residual when software never programs DISPFB.
    /// </summary>
    public long ResidualDispfbPixels { get; private set; }
    /// <summary>GX-041: last composite source class (NaturalDispfb / Frame / SyntheticFbp0 / LastImageTrx).</summary>
    public GsCompositeSource LastCompositeSource { get; private set; }
    /// <summary>Largest Host→Local IMAGE BITBLT dest byte address (telemetry; 0 if none).</summary>
    public uint LastImageDbpBytes => _lastImageDbpBytes;
    /// <summary>Largest Host→Local IMAGE DPSM (telemetry).</summary>
    public int LastImageDpsm => _lastImageDpsm;
    /// <summary>Largest Host→Local IMAGE RRW×RRH (telemetry).</summary>
    public int LastImageWidth => _lastImageW;
    /// <summary>Largest Host→Local IMAGE RRH (telemetry).</summary>
    public int LastImageHeight => _lastImageH;
    /// <summary>Byte count of largest Host→Local IMAGE transfer (telemetry).</summary>
    public long LastImageByteCount => _lastImageByteCount;
    /// <summary>Last preferred PCRTC circuit (0/1/2) seen during composite.</summary>
    public int LastDisplayCircuit { get; private set; }
    /// <summary>Generation of privileged DISPFB/DISPLAY/PMODE writes (invalidates composite skip).</summary>
    public int DisplayCircuitGeneration { get; private set; }
    /// <summary>
    /// Soft-GS title-strip expand events (temporary MENU chrome; G-GFX-6 demotes later).
    /// Increments only when DrawSprite actually expands a legal collapsed strip.
    /// See docs/graphics/EXPAND_POLICY.md. Collapse ofx: 0/0, 0x8000/0x8000, or [0x6000,0x9000] band.
    /// Illegal expand killed when retail-center ofx + pure 12.4 map already on-FB with natural h.
    /// </summary>
    public long ExpandHits { get; private set; }

    /// <summary>
    /// Max natural sprite height (pixels) under retail-center XYOFFSET that still forbids expand.
    /// Pure-mapped on-FB strips taller than this paint as-is (GX-021 illegal-expand kill).
    /// Collapsed h=1 MENU strips (GoW/Whip) remain expandable.
    /// </summary>
    public const int ExpandRetailNaturalMinH = 2;
    /// <summary>Fragments rejected for FB bounds (before scissor).</summary>
    public long FragmentsRejectedBounds { get; private set; }
    /// <summary>Fragments rejected by SCISSOR_1.</summary>
    public long FragmentsRejectedScissor { get; private set; }
    /// <summary>Total GIF/path register writes (Soft-GS truth histogram input).</summary>
    public long RegWritesTotal { get; private set; }
    /// <summary>Writes to PRIM (0x00), including PRE from GIFtag.</summary>
    public long RegWritesPrim { get; private set; }
    /// <summary>XYZ2 kick (0x05) writes.</summary>
    public long RegWritesXyz2 { get; private set; }
    /// <summary>XYZ3 no-kick (0x0D) writes.</summary>
    public long RegWritesXyz3 { get; private set; }
    /// <summary>XYZF2 kick (0x04) writes.</summary>
    public long RegWritesXyzf2 { get; private set; }
    /// <summary>FRAME_1 (0x4C) writes — required for commercial draw target.</summary>
    public long RegWritesFrame { get; private set; }
    /// <summary>SCISSOR_1 (0x40) writes.</summary>
    public long RegWritesScissor { get; private set; }
    /// <summary>TEST_1 (0x47) writes (ATE/AFAIL/ZTE).</summary>
    public long RegWritesTest { get; private set; }
    /// <summary>XYOFFSET_1 (0x18) writes.</summary>
    public long RegWritesXyoffset { get; private set; }
    private bool _localMemHasImage;

    public struct Vertex
    {
        public int X, Y;
        public float Z;
        public uint Color;
        public float U, V, S, T, Q;
        public float Fog;
    }

    public Gs(SystemMemory memory)
    {
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public void Reset()
    {
        Registers.Reset();
        Array.Clear(_framebuffer);
        Array.Clear(_depthBuffer);
        Array.Clear(_localMem);
        _hostOverlay = null;
        _hostOverlayActive = false;
        _trxActive = false;
        _trxX = _trxY = _trxW = _trxH = 0;
        _trxDsaX = _trxDsaY = 0;
        _trxDbpBytes = 0;
        _trxDbwPx = 64;
        _trxDpsm = 0;
        _trxPending = 0;
        _trxPartial = 0;
        _lastImageDbpBytes = 0;
        _lastImageDbwPx = 0;
        _lastImageDpsm = 0;
        _lastImageW = _lastImageH = 0;
        _lastImageDsaX = _lastImageDsaY = 0;
        _lastImageByteCount = 0;
        ImageBytesWritten = 0;
        DispfbPixelsComposited = 0;
        NaturalDispfbPixels = 0;
        ResidualDispfbPixels = 0;
        LastCompositeSource = GsCompositeSource.None;
        LastDisplayCircuit = 0;
        DisplayCircuitGeneration = 0;
        _lastCompositeImageBytes = 0;
        _lastCompositeCircuitGen = -1;
        _mergeBlackBypassArmed = false;
        ExpandHits = 0;
        FragmentsRejectedBounds = 0;
        FragmentsRejectedScissor = 0;
        RegWritesTotal = RegWritesPrim = RegWritesXyz2 = RegWritesXyz3 = RegWritesXyzf2 = 0;
        RegWritesFrame = RegWritesScissor = RegWritesTest = RegWritesXyoffset = 0;
        _lastCompositeImageBytes = 0;
        _localMemHasImage = false;
        _currentPrim = 0;
        _currentRgbaq = 0xFFFFFFFF;
        _lastU = _lastV = 0;
        _lastS = _lastT = _lastQ = 1f;
        _lastFog = 0;
        _texWidth = 64;
        _texHeight = 64;
        _texBufWidth = 64;
        _texBase = 0;
        _useProceduralTexture = true;
        TexSamplesLocal = 0;
        _verts.Clear();
        _stripCount = 0;
        PrimitivesDrawn = PixelsWritten = FragmentsTested = FragmentsRejectedDepth = 0;
        FragmentsRejectedAlpha = TexFlushCount = 0;
        BilinearFilter = false;
        BilinearSamples = 0;
        _clutBase = 0;
        _hasClut = false;
        Array.Clear(_clut);
        // Default depth far
        for (int i = 0; i < _depthBuffer.Length; i++)
            _depthBuffer[i] = float.MaxValue;
    }

    /// <summary>TEXFLUSH — invalidate texture cache (stat only; soft GS samples local mem live).</summary>
    public void TexFlush() => TexFlushCount++;

    /// <summary>Full GS state for SaveState.cs. Previously SaveState.cs wrote the framebuffer
    /// pixels out but never wrote them back on load (read-and-discarded, dead code) and never
    /// touched anything else here at all — not the depth buffer, not GsRegisters (PRIM/TEX0/
    /// ALPHA/scissor/etc), and critically not _localMem, the GS's actual 4MB VRAM that
    /// SampleTexel/CLUT reads sample from live. A load without _localMem would resume with
    /// every texture/CLUT the game had already uploaded silently gone, breaking textured
    /// rendering until the game happened to re-upload. Also covers in-flight vertex assembly
    /// (_verts/_stripCount) — a save mid-triangle-strip needs this or the strip breaks.</summary>
    public void WriteState(System.IO.BinaryWriter w)
    {
        Registers.WriteState(w);
        w.Write(_framebuffer.Length);
        foreach (var p in _framebuffer) w.Write(p);
        w.Write(_depthBuffer.Length);
        foreach (var d in _depthBuffer) w.Write(d);
        w.Write(_localMem.Length);
        w.Write(_localMem);
        w.Write(_hostOverlayActive);
        if (_hostOverlayActive && _hostOverlay != null)
        {
            w.Write(_hostOverlay.Length);
            foreach (var p in _hostOverlay) w.Write(p);
        }
        else w.Write(0);

        w.Write(_currentPrim);
        w.Write(_currentRgbaq);
        w.Write(_lastU); w.Write(_lastV); w.Write(_lastS); w.Write(_lastT); w.Write(_lastQ);
        w.Write(_lastFog);
        w.Write(_texWidth); w.Write(_texHeight); w.Write(_texBase);
        w.Write(_useProceduralTexture);
        w.Write(_clutBase);
        foreach (var c in _clut) w.Write(c);
        w.Write(_hasClut);

        w.Write(_verts.Count);
        foreach (var v in _verts)
        {
            w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.Color);
            w.Write(v.U); w.Write(v.V); w.Write(v.S); w.Write(v.T); w.Write(v.Q);
            w.Write(v.Fog);
        }
        w.Write(_stripCount);

        w.Write(PrimitivesDrawn); w.Write(PixelsWritten); w.Write(FragmentsTested); w.Write(FragmentsRejectedDepth);
        w.Write(FragmentsRejectedAlpha); w.Write(TexFlushCount);
        w.Write(BilinearFilter); w.Write(BilinearSamples);
    }

    public void ReadState(System.IO.BinaryReader r)
    {
        Registers.ReadState(r);
        int fbLen = r.ReadInt32();
        for (int i = 0; i < fbLen && i < _framebuffer.Length; i++) _framebuffer[i] = r.ReadUInt32();
        int dbLen = r.ReadInt32();
        for (int i = 0; i < dbLen && i < _depthBuffer.Length; i++) _depthBuffer[i] = r.ReadSingle();
        int lmLen = r.ReadInt32();
        byte[] lm = r.ReadBytes(lmLen);
        Buffer.BlockCopy(lm, 0, _localMem, 0, Math.Min(lmLen, _localMem.Length));

        _hostOverlayActive = r.ReadBoolean();
        int ovLen = r.ReadInt32();
        if (ovLen > 0)
        {
            _hostOverlay = new uint[ovLen];
            for (int i = 0; i < ovLen; i++) _hostOverlay[i] = r.ReadUInt32();
        }
        else _hostOverlay = null;

        _currentPrim = r.ReadUInt32();
        _currentRgbaq = r.ReadUInt32();
        _lastU = r.ReadSingle(); _lastV = r.ReadSingle(); _lastS = r.ReadSingle(); _lastT = r.ReadSingle(); _lastQ = r.ReadSingle();
        _lastFog = r.ReadSingle();
        _texWidth = r.ReadInt32(); _texHeight = r.ReadInt32(); _texBase = r.ReadUInt32();
        _useProceduralTexture = r.ReadBoolean();
        _clutBase = r.ReadUInt32();
        for (int i = 0; i < _clut.Length; i++) _clut[i] = r.ReadUInt32();
        _hasClut = r.ReadBoolean();

        _verts.Clear();
        int vn = r.ReadInt32();
        for (int i = 0; i < vn; i++)
        {
            _verts.Add(new Vertex
            {
                X = r.ReadInt32(), Y = r.ReadInt32(), Z = r.ReadSingle(), Color = r.ReadUInt32(),
                U = r.ReadSingle(), V = r.ReadSingle(), S = r.ReadSingle(), T = r.ReadSingle(), Q = r.ReadSingle(),
                Fog = r.ReadSingle()
            });
        }
        _stripCount = r.ReadInt32();

        PrimitivesDrawn = r.ReadInt64(); PixelsWritten = r.ReadInt64(); FragmentsTested = r.ReadInt64(); FragmentsRejectedDepth = r.ReadInt64();
        FragmentsRejectedAlpha = r.ReadInt64(); TexFlushCount = r.ReadInt64();
        BilinearFilter = r.ReadBoolean(); BilinearSamples = r.ReadInt64();
    }

    // ===================== GIF / register writes =====================

    public void WriteGsRegister(uint reg, ulong value)
    {
        reg &= 0x7F;
        RegWritesTotal++;
        switch (reg)
        {
            case 0x00: RegWritesPrim++; break;
            case 0x04: RegWritesXyzf2++; break;
            case 0x05: RegWritesXyz2++; break;
            case 0x0D: RegWritesXyz3++; break;
            case 0x18: RegWritesXyoffset++; break;
            case 0x40: RegWritesScissor++; break;
            case 0x47: RegWritesTest++; break;
            case 0x4C: RegWritesFrame++; break;
        }
        Registers.WriteRegister64(reg, value);
        OnRegisterWrite(reg, value);
    }

    public void ProcessGifPackedWord(uint dataLow, uint dataHigh)
    {
        // PACKED A+D: low 64 = data, high byte of high dword often reg in bits 0-7 of upper...
        // Standard: QW = [data64][reg8 in bits 0-7 of second half low?]. We use prior convention:
        // dataLow = low 32 of data, dataHigh high 32 of data, reg in bits 24-30 of dataHigh when only 32-bit data used.
        // Full 64-bit data form:
        ulong data = dataLow | ((ulong)(dataHigh & 0x00FFFFFF) << 32);
        uint reg = (dataHigh >> 24) & 0x7F;
        // Also accept pure reg in low byte of high when format uses AD
        if (reg == 0 && (dataHigh & 0x7F) != 0 && (dataHigh >> 8) == 0)
            reg = dataHigh & 0x7F;

        if (reg == 0 && dataLow == 0 && dataHigh == 0) return;

        // Prefer AD style: second QW half's low 8 bits = reg when using high as reg-only
        // Keep legacy: reg from bits 24-30 of high when writing 32-bit values
        uint regAddr = (dataHigh >> 24) & 0x7F;
        if (regAddr == 0)
            regAddr = dataHigh & 0x7F;

        WriteGsRegister(regAddr, dataLow | ((ulong)(dataHigh & 0xFFFFFF) << 32));
    }

    /// <summary>Packed register write with explicit reg and 64-bit data.</summary>
    public void WritePacked(uint reg, ulong data) => WriteGsRegister(reg, data);

    private void OnRegisterWrite(uint reg, ulong value)
    {
        switch (reg)
        {
            case 0x00: // PRIM
                _currentPrim = (uint)value;
                _verts.Clear();
                _stripCount = 0;
                break;
            case 0x01: // RGBAQ
                _currentRgbaq = (uint)value;
                // Q in upper 32 bits as float
                if ((value >> 32) != 0)
                    _lastQ = BitConverter.Int32BitsToSingle((int)(value >> 32));
                break;
            case 0x02: // ST
                _lastS = BitConverter.Int32BitsToSingle((int)(value & 0xFFFFFFFF));
                _lastT = BitConverter.Int32BitsToSingle((int)(value >> 32));
                break;
            case 0x03: // UV (12.4 fixed)
                _lastU = ((value & 0x3FFF)) / 16.0f;
                _lastV = (((value >> 16) & 0x3FFF)) / 16.0f;
                break;
            case 0x0A: // FOG
                _lastFog = ((value >> 56) & 0xFF) / 255.0f;
                break;
            case 0x06: // TEX0_1
                ApplyTex0(value);
                break;
            case 0x07: // TEX0_2 — context 2; Soft-GS samples context-1 state but still arm local tex
                ApplyTex0(value);
                break;
            case 0x16: // TEX2_1 — partial TEX0 (PSM/CBP/CLD) for CLUT reload without full TEX0
            case 0x17: // TEX2_2
                ApplyTex2(value);
                break;
            // GS register map (Sony / Play! GSHandler.h):
            //   0x04 XYZF2 kick+fog, 0x05 XYZ2 kick, 0x0C XYZF3 no-kick+fog, 0x0D XYZ3 no-kick.
            // An earlier map had XYZ2/XYZ3 swapped with XYZF2/XYZF3, so commercial XYZ2
            // (reg 0x05) never kicked — MK Deadly Alliance Midway sprite prims=0 residual.
            case 0x04: // XYZF2 — XYZ + fog, kick
                _lastFog = ((value >> 56) & 0xFF) / 255.0f;
                AddVertexFromXyz(value, kick: true);
                break;
            case 0x05: // XYZ2 — kick draw
                AddVertexFromXyz(value, kick: true);
                break;
            case 0x0C: // XYZF3 — XYZ + fog, no kick
                _lastFog = ((value >> 56) & 0xFF) / 255.0f;
                AddVertexFromXyz(value, kick: false);
                break;
            case 0x0D: // XYZ3 — no kick (strip build)
                AddVertexFromXyz(value, kick: false);
                break;
            case 0x53: // TRXDIR — start host↔local transfer after BITBLTBUF/TRXPOS/TRXREG
                BeginTrxFromDir(value);
                break;
        }
    }

    /// <summary>
    /// TRXDIR.XDIR: 0 = Host→Local (GIF IMAGE), 1 = Local→Host (stub), 2 = Local→Local, 3 = deactivate.
    /// Soft-GS implements Host→Local + Local→Local for commercial texture/FB paths.
    /// </summary>
    private void BeginTrxFromDir(ulong trxdir)
    {
        int xdir = (int)(trxdir & 0x3);
        _trxPending = 0;
        _trxPartial = 0;
        if (xdir == 3)
        {
            _trxActive = false;
            return;
        }
        if (xdir == 2)
        {
            _trxActive = false;
            RunLocalToLocalBlit();
            return;
        }
        if (xdir != 0)
        {
            // Local→Host readback not needed for current title fleet; deactivate.
            _trxActive = false;
            return;
        }

        ulong blt = Registers.BITBLTBUF;
        ulong pos = Registers.TRXPOS;
        ulong reg = Registers.TRXREG;

        // DBP bits 32-45 (64-byte units), DBW bits 48-53 (64-pixel units), DPSM bits 56-61
        _trxDbpBytes = (uint)((blt >> 32) & 0x3FFF) * 64u;
        int dbwUnits = (int)((blt >> 48) & 0x3F);
        _trxDbwPx = Math.Max(64, dbwUnits * 64);
        _trxDpsm = (int)((blt >> 56) & 0x3F);
        // DSAX bits 32-42, DSAY bits 48-58
        _trxDsaX = (int)((pos >> 32) & 0x7FF);
        _trxDsaY = (int)((pos >> 48) & 0x7FF);
        // RRW bits 0-11, RRH bits 32-43
        _trxW = (int)(reg & 0xFFF);
        _trxH = (int)((reg >> 32) & 0xFFF);
        _trxX = 0;
        _trxY = 0;
        _trxActive = _trxW > 0 && _trxH > 0;
        if (_trxActive)
        {
            _useProceduralTexture = false;
            // Track largest Host→Local so residual present can sample real IMAGE pages
            // (GoW: DISPFB empty CT24 while PSMT4 BITBLT lands at high DBP).
            NoteLargestImageTransfer(_trxDbpBytes, _trxDbwPx, _trxDpsm, _trxW, _trxH, _trxDsaX, _trxDsaY);
        }
    }

    /// <summary>
    /// Remember the largest Host→Local IMAGE window for residual present composite.
    /// Byte estimate uses PSM packing (PSMT4 ≈ W×H/2).
    /// </summary>
    private void NoteLargestImageTransfer(uint dbp, int dbw, int dpsm, int w, int h, int dsax, int dsay)
    {
        if (w <= 0 || h <= 0) return;
        long bytes = dpsm == 0x14
            ? (long)w * h / 2
            : (long)w * h * Math.Max(1, TrxBytesPerPixel(dpsm));
        if (bytes < 64) return; // ignore tiny CLUT-only probes when a real tex already tracked
        // Prefer larger; equal size still refreshes dest (re-upload same logo).
        if (bytes < _lastImageByteCount) return;
        _lastImageByteCount = bytes;
        _lastImageDbpBytes = dbp;
        _lastImageDbwPx = dbw > 0 ? dbw : 64;
        _lastImageDpsm = dpsm;
        _lastImageW = w;
        _lastImageH = h;
        _lastImageDsaX = dsax;
        _lastImageDsaY = dsay;
    }

    /// <summary>
    /// GX-026: Local→Local BITBLT — copy RRW×RRH from (SSAX,SSAY)@SBP to (DSAX,DSAY)@DBP.
    /// Same-PSM path only (commercial logo/FB moves); cross-PSM conversion is residual.
    /// </summary>
    private void RunLocalToLocalBlit()
    {
        ulong blt = Registers.BITBLTBUF;
        ulong pos = Registers.TRXPOS;
        ulong reg = Registers.TRXREG;

        uint sbpBytes = (uint)(blt & 0x3FFF) * 64u;
        int sbwUnits = (int)((blt >> 16) & 0x3F);
        int sbwPx = Math.Max(64, sbwUnits * 64);
        int spsm = (int)((blt >> 24) & 0x3F);

        uint dbpBytes = (uint)((blt >> 32) & 0x3FFF) * 64u;
        int dbwUnits = (int)((blt >> 48) & 0x3F);
        int dbwPx = Math.Max(64, dbwUnits * 64);
        int dpsm = (int)((blt >> 56) & 0x3F);

        int ssax = (int)(pos & 0x7FF);
        int ssay = (int)((pos >> 16) & 0x7FF);
        int dsax = (int)((pos >> 32) & 0x7FF);
        int dsay = (int)((pos >> 48) & 0x7FF);
        int rrw = (int)(reg & 0xFFF);
        int rrh = (int)((reg >> 32) & 0xFFF);
        if (rrw <= 0 || rrh <= 0) return;

        // Same-PSM copy via LoadTrxPixel/Store helpers.
        int psm = dpsm;
        if (spsm != dpsm)
            psm = dpsm; // residual: dest format wins for store layout

        for (int y = 0; y < rrh; y++)
        {
            for (int x = 0; x < rrw; x++)
            {
                uint pix = LoadLocalPixel(sbpBytes, ssax + x, ssay + y, sbwPx, spsm);
                StoreLocalPixel(dbpBytes, dsax + x, dsay + y, dbwPx, dpsm, pix);
            }
        }
        _localMemHasImage = true;
        _useProceduralTexture = false;
        ImageBytesWritten += (long)rrw * rrh * Math.Max(1, TrxBytesPerPixel(psm));
    }

    private uint LoadLocalPixel(uint baseBytes, int px, int py, int bufW, int psm)
    {
        int bpp = TrxBytesPerPixel(psm);
        if (bpp <= 0) bpp = 4;
        int bi = LocalPixelByteOffset(baseBytes, px, py, bufW, psm, bpp);
        if (bi < 0 || bi >= _localMem.Length) return 0;
        uint pix = 0;
        for (int b = 0; b < bpp && bi + b < _localMem.Length; b++)
            pix |= (uint)_localMem[bi + b] << (8 * b); // byte is 0..255; shift is well-defined
        if (psm is 0x14) // PSMT4: return nibble in low 4 bits
        {
            int nibbleIndex = py * bufW + px;
            byte packed = _localMem[bi];
            return ((nibbleIndex & 1) == 0) ? (uint)(packed & 0xF) : (uint)(packed >> 4);
        }
        return pix;
    }

    private void StoreLocalPixel(uint baseBytes, int px, int py, int bufW, int psm, uint pixel)
    {
        int bpp = TrxBytesPerPixel(psm);
        if (bpp <= 0) bpp = 4;
        if (psm is 0x14)
        {
            int bi = (int)baseBytes + (py * bufW + px) / 2;
            if (bi < 0 || bi >= _localMem.Length) return;
            int nibbleIndex = py * bufW + px;
            if ((nibbleIndex & 1) == 0)
                _localMem[bi] = (byte)((_localMem[bi] & 0xF0) | (pixel & 0xF));
            else
                _localMem[bi] = (byte)((_localMem[bi] & 0x0F) | ((pixel & 0xF) << 4));
            return;
        }
        int off = LocalPixelByteOffset(baseBytes, px, py, bufW, psm, bpp);
        if (off < 0 || off >= _localMem.Length) return;
        for (int b = 0; b < bpp && off + b < _localMem.Length; b++)
            _localMem[off + b] = (byte)(pixel >> (8 * b));
    }

    private static int LocalPixelByteOffset(uint baseBytes, int px, int py, int bufW, int psm, int bpp)
    {
        if (psm is 0x13 or 0x1B)
            return (int)SwizzleOffset8(baseBytes, px, py, bufW);
        if (psm is 0x00 or 0x01)
            return (int)SwizzleOffset32(baseBytes, px, py, bufW);
        if (psm == 0x02)
            return (int)SwizzleOffset16(baseBytes, px, py, bufW);
        if (psm == 0x0A)
            return (int)SwizzleOffset16S(baseBytes, px, py, bufW);
        if (psm is 0x14)
            return (int)baseBytes + (py * bufW + px) / 2;
        return (int)baseBytes + (py * bufW + px) * bpp;
    }

    /// <summary>
    /// GX-035: any programmed TEX0 is a real texture descriptor — sample local mem.
    /// TBP0=0 is valid (textures at GS page 0). Procedural checker only when TEX0 never written.
    /// Also arms TW/TH/TBW/PSM and loads CLUT when CLD≠0 (GX-031).
    /// </summary>
    private void ApplyTex0(ulong tex0)
    {
        _texWidth = Registers.TexWidth;
        _texHeight = Registers.TexHeight;
        // When called for TEX0_2 before Registers.TEX0_1 update path differs — parse value directly.
        int twLog = (int)((tex0 >> 26) & 0xF);
        int thLog = (int)((tex0 >> 30) & 0xF);
        if (twLog == 0) twLog = 6;
        if (thLog == 0) thLog = 6;
        twLog = Math.Clamp(twLog, 0, 10);
        thLog = Math.Clamp(thLog, 0, 10);
        _texWidth = 1 << twLog;
        _texHeight = 1 << thLog;
        _texBase = (uint)(tex0 & 0x3FFF) * 64u;
        int tbwUnits = (int)((tex0 >> 14) & 0x3F);
        _texBufWidth = tbwUnits <= 0 ? Math.Max(64, _texWidth) : Math.Max(64, tbwUnits * 64);
        _useProceduralTexture = false;
        MaybeLoadClut(tex0);
    }

    /// <summary>TEX2 carries PSM (20-25) + CBP/CPSM/CSM/CSA/CLD like the upper TEX0 fields.</summary>
    private void ApplyTex2(ulong tex2)
    {
        // TEX2: PSM bits 20-25, CBP 37-50, CPSM 51-54, CSM 55, CSA 56-60, CLD 61-63
        // Soft-GS: merge PSM into sample path via a synthetic TEX0-shaped word using current TBP/TBW/TW/TH.
        ulong merged = (Registers.TEX0_1 & 0xFFFFFul) // keep TBP0+TBW
            | (tex2 & ~0xFFFFFul); // overlay PSM + CLUT fields from TEX2
        // If TEX0 was never set, still arm dimensions from current state.
        if ((Registers.TEX0_1 & 0x3FFF) == 0 && ((Registers.TEX0_1 >> 26) & 0xF) == 0)
        {
            // keep _texWidth/_texHeight as-is
        }
        else
        {
            _texBase = Registers.TexBaseWords * 64;
            _texWidth = Registers.TexWidth;
            _texHeight = Registers.TexHeight;
            _texBufWidth = Registers.TexBufWidthPixels;
        }
        int psm = (int)((tex2 >> 20) & 0x3F);
        // Update TEX0_1 PSM in register file via re-write so SampleTexel sees it
        ulong newTex0 = (Registers.TEX0_1 & ~((ulong)0x3F << 20))
            | ((ulong)(uint)psm << 20)
            | (tex2 & (0x7FFFFFUL << 37)); // CBP..CLD
        Registers.WriteRegister64(0x06, newTex0);
        _useProceduralTexture = false;
        MaybeLoadClut(newTex0);
    }

    /// <summary>
    /// GX-031: when TEX0/TEX2.CLD ≠ 0, load palette from local mem at CBP into Soft-GS CLUT cache.
    /// CPSM PSMCT32 (0) or PSMCT16 (2); CSA selects starting entry (×16 when CSM=0).
    /// </summary>
    private void MaybeLoadClut(ulong tex0)
    {
        int cld = (int)((tex0 >> 61) & 0x7);
        if (cld == 0) return;

        uint cbpBytes = (uint)((tex0 >> 37) & 0x3FFF) * 64u;
        int cpsm = (int)((tex0 >> 51) & 0xF);
        int csa = (int)((tex0 >> 56) & 0x1F);
        int start = csa * 16; // CSM=0 units of 16; Soft-GS ignores CSM=1 residual
        if (start >= 256) start = 0;
        int count = 256 - start;

        _clutBase = cbpBytes;
        if (cpsm is 0x02 or 0x0A)
        {
            // PSMCT16 palette
            for (int i = 0; i < count; i++)
            {
                int bi = (int)cbpBytes + i * 2;
                if (bi + 1 >= _localMem.Length) break;
                ushort p = (ushort)(_localMem[bi] | (_localMem[bi + 1] << 8));
                _clut[start + i] = ExpandRgb555(p);
            }
        }
        else
        {
            // PSMCT32 (default) — linear palette in local mem (common PATH3 upload layout)
            for (int i = 0; i < count; i++)
            {
                int bi = (int)cbpBytes + i * 4;
                if (bi + 3 >= _localMem.Length) break;
                _clut[start + i] = (uint)(_localMem[bi]
                    | (_localMem[bi + 1] << 8)
                    | (_localMem[bi + 2] << 16)
                    | (_localMem[bi + 3] << 24));
            }
        }
        _hasClut = true;
    }

    /// <summary>
    /// Expand GS PSMCT16/PSMCT16S RGB555 (+ optional A bit) with TEXA TA0/TA1 / AEM.
    /// GS CT16 bit layout (PCSX2 / GS manual): R=bits0–4, G=5–9, B=10–14, A=15.
    /// </summary>
    private uint ExpandRgb555(ushort p)
    {
        int r = (p & 0x1F) * 255 / 31;
        int g = ((p >> 5) & 0x1F) * 255 / 31;
        int b = ((p >> 10) & 0x1F) * 255 / 31;
        int aBit = (p >> 15) & 1;
        int a = aBit != 0 ? Registers.TexaTa1 : Registers.TexaTa0;
        // Default TEXA=0 → treat as opaque when TA not programmed (smoke/host uploads).
        if (Registers.TEXA == 0) a = 0xFF;
        if (Registers.TexaAem && r == 0 && g == 0 && b == 0)
            a = 0;
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    public void SetPrim(uint prim) => WriteGsRegister(0x00, prim);
    public void SetRGBAQ(uint rgbaq) => WriteGsRegister(0x01, rgbaq);

    public void DrawVertex(uint xyz) => WriteGsRegister(0x05, xyz); // GS_REG_XYZ2

    public void DrawVertex64(ulong xyz2) => WriteGsRegister(0x05, xyz2); // GS_REG_XYZ2

    // ===================== Vertex / primitives =====================

    private void AddVertexFromXyz(ulong xyz, bool kick)
    {
        // XYZ: X 16-bit 12.4, Y 16-bit 12.4, Z 24-bit in upper
        int xRaw = (int)(xyz & 0xFFFF);
        int yRaw = (int)((xyz >> 16) & 0xFFFF);
        uint zRaw = (uint)((xyz >> 32) & 0xFFFFFF);

        Registers.GetXyOffset(out int ofx, out int ofy);
        // GX-021: pure 16-bit 12.4 fixed first — screen = (raw - OFX/OFY) >> 4 (Sony/Play!).
        GsRegisters.MapScreenXy12_4(xRaw, yRaw, ofx, ofy, out int x, out int y);

        // Soft-GS rescues only when pure map is off-FB (or ofx unprogrammed). Do not invent
        // PATH3; rescues only re-interpret XYZ when commercial packs leave ofx=0 or strip
        // verts use raw Y near 0 under ofy=0x8000 (Whip MENU).
        if (ofx == 0 && ofy == 0)
        {
            // Unprogrammed XYOFFSET: verts often use 2048.0 (0x8000) origin while OFX stays 0.
            int sxRaw = unchecked((short)(ushort)xRaw);
            int syRaw = unchecked((short)(ushort)yRaw);
            if (xRaw >= 0x6000 || yRaw >= 0x6000 || sxRaw < -16 || syRaw < -16)
            {
                if (xRaw >= 0x4000 || yRaw >= 0x4000)
                {
                    x = (xRaw - 0x8000) >> 4;
                    y = (yRaw - 0x8000) >> 4;
                }
                else
                {
                    x = sxRaw >> 4;
                    y = syRaw >> 4;
                }
            }
            else if (xRaw > FB_WIDTH * 16 || yRaw > FB_HEIGHT * 16)
            {
                x = (xRaw * FB_WIDTH) / 4096;
                y = (yRaw * FB_HEIGHT) / 4096;
            }
        }
        else if ((ofx == 0 && ofy != 0) || (ofy == 0 && ofx != 0))
        {
            // Partial program: only rescue if pure map landed far off Soft-GS FB.
            if (x < -64 || y < -64 || x >= FB_WIDTH + 64 || y >= FB_HEIGHT + 64)
            {
                if (xRaw >= 0x4000 || yRaw >= 0x4000)
                {
                    x = (xRaw - 0x8000) >> 4;
                    y = (yRaw - 0x8000) >> 4;
                }
            }
        }

        // Off-FB rescue when XYOFFSET is programmed but verts still land outside Soft-GS
        // FB (B3: ofx=0x6C00 ofy=0x7200 → prims↑ rejBounds=prims fragTest=0 without this).
        // WAVE-6: Whiplash title sprite raw Y=0 with ofy=0x8000 → pure y=-2048. Prefer
        // Y=raw/16 (top of FB) while keeping ofx-based X so the sprite clamp expands a
        // full-width title surface instead of pure rejBounds.
        if (ofy == 0x8000 && yRaw < 0x1000 && (y < -64 || y >= FB_HEIGHT + 64))
            y = yRaw >> 4;
        if (x < -64 || y < -64 || x >= FB_WIDTH + 64 || y >= FB_HEIGHT + 64)
        {
            int ax = (xRaw - 0x8000) >> 4;
            int ay = (yRaw - 0x8000) >> 4;
            if (ax >= -64 && ay >= -64 && ax < FB_WIDTH + 64 && ay < FB_HEIGHT + 64)
            {
                x = ax; y = ay;
            }
            else
            {
                int px = xRaw >> 4, py = yRaw >> 4;
                if (ofy == 0x8000 && yRaw < 0x1000)
                    py = yRaw >> 4;
                if (px >= -64 && py >= -64 && px < FB_WIDTH + 64 && py < FB_HEIGHT + 64)
                {
                    x = px; y = py;
                }
                else
                {
                    int sx = unchecked((short)(ushort)xRaw) >> 4;
                    int sy = unchecked((short)(ushort)yRaw) >> 4;
                    if (sx >= -64 && sy >= -64 && sx < FB_WIDTH + 64 && sy < FB_HEIGHT + 64)
                    {
                        x = sx; y = sy;
                    }
                }
            }
        }

        if (TraceGs && PrimitivesDrawn < 12)
        {
            Console.Error.WriteLine(
                $"[GS] XYZ raw=({xRaw:X4},{yRaw:X4}) of=({ofx:X4},{ofy:X4}) -> ({x},{y}) " +
                $"kick={kick} prim={_currentPrim & 7}");
        }

        float z = zRaw / (float)0xFFFFFF;

        var v = new Vertex
        {
            X = x,
            Y = y,
            Z = z,
            Color = _currentRgbaq,
            U = _lastU,
            V = _lastV,
            S = _lastS,
            T = _lastT,
            Q = _lastQ,
            Fog = _lastFog
        };
        _verts.Add(v);

        if (kick)
            TryAssemble();
    }

    /// <summary>Add a screen-space vertex directly (tests / HLE). Kicks assembly when enough verts exist.</summary>
    public void AddScreenVertex(int x, int y, float z, uint color, float u = 0, float v = 0)
    {
        _currentRgbaq = color;
        _verts.Add(new Vertex { X = x, Y = y, Z = z, Color = color, U = u, V = v, S = u, T = v, Q = 1, Fog = 0 });
        TryAssemble();
    }

    /// <summary>Force-draw a triangle in screen pixels (bypasses GIF).</summary>
    public void DrawScreenTriangle(int x0, int y0, int x1, int y1, int x2, int y2, uint color, float z0 = 0.1f, float z1 = 0.1f, float z2 = 0.1f, float u0 = 0, float v0 = 0, float u1 = 1, float v1 = 0, float u2 = 0.5f, float v2 = 1)
    {
        DrawFilledTriangle(
            new Vertex { X = x0, Y = y0, Z = z0, Color = color, U = u0, V = v0, S = u0, T = v0, Q = 1 },
            new Vertex { X = x1, Y = y1, Z = z1, Color = color, U = u1, V = v1, S = u1, T = v1, Q = 1 },
            new Vertex { X = x2, Y = y2, Z = z2, Color = color, U = u2, V = v2, S = u2, T = v2, Q = 1 });
        PrimitivesDrawn++;
    }

    private int VertsNeeded(int primType) => primType switch
    {
        0 => 1, // point
        1 => 2, // line
        2 => 2, // line strip (first needs 2, then 1)
        3 => 3, // triangle
        4 => 3, // triangle strip
        5 => 3, // triangle fan
        6 => 2, // sprite
        _ => 3
    };

    private void TryAssemble()
    {
        int pt = (int)(_currentPrim & 0x7);
        int need = VertsNeeded(pt);

        switch (pt)
        {
            case 0: // Point
                if (_verts.Count >= 1)
                {
                    DrawPoint(_verts[0]);
                    _verts.Clear();
                    PrimitivesDrawn++;
                }
                break;
            case 1: // Line
                if (_verts.Count >= 2)
                {
                    DrawLine(_verts[0], _verts[1]);
                    _verts.Clear();
                    PrimitivesDrawn++;
                }
                break;
            case 2: // Line strip
                while (_verts.Count >= 2)
                {
                    DrawLine(_verts[0], _verts[1]);
                    _verts.RemoveAt(0);
                    PrimitivesDrawn++;
                }
                break;
            case 3: // Triangle
                if (_verts.Count >= 3)
                {
                    DrawFilledTriangle(_verts[0], _verts[1], _verts[2]);
                    _verts.Clear();
                    PrimitivesDrawn++;
                }
                break;
            case 4: // Triangle strip
                while (_verts.Count >= 3)
                {
                    if ((_stripCount & 1) == 0)
                        DrawFilledTriangle(_verts[0], _verts[1], _verts[2]);
                    else
                        DrawFilledTriangle(_verts[1], _verts[0], _verts[2]);
                    _verts.RemoveAt(0);
                    _stripCount++;
                    PrimitivesDrawn++;
                }
                break;
            case 5: // Triangle fan
                while (_verts.Count >= 3)
                {
                    DrawFilledTriangle(_verts[0], _verts[1], _verts[2]);
                    // keep v0, replace v1 with old v2 by removing index 1
                    _verts.RemoveAt(1);
                    PrimitivesDrawn++;
                }
                break;
            case 6: // Sprite: two corners
                if (_verts.Count >= 2)
                {
                    DrawSprite(_verts[0], _verts[1]);
                    _verts.Clear();
                    PrimitivesDrawn++;
                }
                break;
            default:
                if (_verts.Count >= need) _verts.Clear();
                break;
        }
    }

    // ===================== Raster =====================

    private bool InScissor(int x, int y)
    {
        Registers.GetScissor(out int x0, out int x1, out int y0, out int y1);
        return x >= x0 && x <= x1 && y >= y0 && y <= y1;
    }

    private void DrawPoint(Vertex v)
    {
        WriteFragment(v.X, v.Y, v.Z, v.Color, v.U, v.V, v.Fog);
    }

    private void DrawLine(Vertex a, Vertex b)
    {
        int x0 = a.X, y0 = a.Y, x1 = b.X, y1 = b.Y;
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        int steps = Math.Max(dx, dy);
        int step = 0;

        while (true)
        {
            float t = steps == 0 ? 0 : step / (float)steps;
            float z = a.Z + (b.Z - a.Z) * t;
            uint col = LerpColor(a.Color, b.Color, t);
            float u = a.U + (b.U - a.U) * t;
            float v = a.V + (b.V - a.V) * t;
            float fog = a.Fog + (b.Fog - a.Fog) * t;
            WriteFragment(x0, y0, z, col, u, v, fog);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
            step++;
        }
    }

    private void DrawSprite(Vertex a, Vertex b)
    {
        int minX = Math.Min(a.X, b.X);
        int maxX = Math.Max(a.X, b.X);
        int minY = Math.Min(a.Y, b.Y);
        int maxY = Math.Max(a.Y, b.Y);
        float z = Math.Min(a.Z, b.Z);
        uint col = a.Color;
        float u0 = a.U, v0 = a.V, u1 = b.U, v1 = b.V;
        int w = Math.Max(1, maxX - minX);
        int h = Math.Max(1, maxY - minY);
        // Clip to Soft-GS FB before raster (triangle path already clips). Unclipped
        // off-FB sprites inflated rejBounds (Whiplash: prims=1 rejBounds=2049 px=0).
        int x0 = Math.Max(0, minX);
        int x1 = Math.Min(FB_WIDTH - 1, maxX);
        int y0 = Math.Max(0, minY);
        int y1 = Math.Min(FB_HEIGHT - 1, maxY);
        Registers.GetXyOffset(out int ofxR, out int ofyR);
        // Title-strip expand (TEMPORARY Soft-GS MENU chrome; telemetry = ExpandHits).
        // Collapse ofx/ofy classes (full-width thin strip — see EXPAND_POLICY.md):
        //   1) ofx=0 && ofy=0          — GoW Path2: XYOFFSET often unprogrammed at kick
        //   2) ofx=ofy=0x8000          — Whiplash/BO2 retail center origin (2048.0 12.4)
        //   3) retail-center band      — ofx,ofy ∈ [0x6000,0x9000] (B3-class offsets)
        // Geometry gate: w ≥ FB_WIDTH/2 && h < FB_HEIGHT/2 (thin strip / logo band).
        // GX-021: kill illegal expand when retail-center ofx + pure 12.4 map already places
        // a natural-height rect on-FB (no Y-collapse). Collapsed h=1 MENU strips still expand.
        // Color/UV from the real prim — no invent PATH3 / no planted pixels.
        // G-GFX-6 demotes remaining expand once retail XYOFFSET+PRIM sizes hold MENU px.
        bool collapseOfs = GsRegisters.IsCollapseOffsetClass(ofxR, ofyR);
        bool titleStrip = collapseOfs && w >= FB_WIDTH / 2 && h < FB_HEIGHT / 2;
        // GX-021 illegal expand: retail-center OFX/OFY + already on-FB + natural height
        // (not a 1-row collapse). Collapsed MENU strips (h=1 after map/rescue) still expand.
        // ofx=0 (unprogrammed) keeps expand for GoW-class Path2 chrome until G-GFX-6.
        bool fullyOffFb = x0 > x1 || y0 > y1;
        if (titleStrip && GsRegisters.IsRetailCenterOffset(ofxR, ofyR)
            && !fullyOffFb && h >= ExpandRetailNaturalMinH)
            titleStrip = false;

        if (x0 > x1 || y0 > y1)
        {
            // Commercial rescue: sprite fully off Soft-GS FB after XYOFFSET — clamp onto
            // FB origin. Cap to FB size so huge wrong-space rects still produce a Soft-GS
            // surface instead of pure rejBounds. ExpandHits only when titleStrip expands H.
            int sw = Math.Clamp(w, 1, FB_WIDTH);
            int sh = Math.Clamp(h, 1, FB_HEIGHT);
            if (titleStrip)
            {
                sh = FB_HEIGHT;
                ExpandHits++;
            }
            minX = 0; minY = 0;
            maxX = sw; maxY = sh;
            x0 = 0; y0 = 0;
            x1 = sw - 1; y1 = sh - 1;
            w = sw; h = sh;
        }
        else if (titleStrip)
        {
            // Partially on-FB one-row (WAVE-6 Y=0 rescue / ofx=0 Y=0): expand to full title FB.
            // ExpandHits accurate: only when we actually grow the raster rect.
            int beforeW = w, beforeH = h;
            minX = 0; minY = 0;
            maxX = FB_WIDTH; maxY = FB_HEIGHT;
            x0 = 0; y0 = 0;
            x1 = FB_WIDTH - 1; y1 = FB_HEIGHT - 1;
            w = FB_WIDTH; h = FB_HEIGHT;
            if (w != beforeW || h != beforeH)
                ExpandHits++;
        }

        for (int y = y0; y <= y1; y++)
        {
            float tv = v0 + (v1 - v0) * ((y - minY) / (float)h);
            for (int x = x0; x <= x1; x++)
            {
                float tu = u0 + (u1 - u0) * ((x - minX) / (float)w);
                WriteFragment(x, y, z, col, tu, tv, a.Fog);
            }
        }
    }

    public void DrawQuad(int x, int y, int w, int h, uint color)
    {
        SetPrim(6); // sprite
        _currentRgbaq = color;
        _verts.Clear();
        AddScreenVertex(x, y, 0, color, 0, 0);
        // second corner — AddScreenVertex kicks; need both without clearing
        // Use raw assembly:
        _verts.Clear();
        _currentPrim = 6;
        _verts.Add(new Vertex { X = x, Y = y, Z = 0, Color = color, U = 0, V = 0 });
        _verts.Add(new Vertex { X = x + w, Y = y + h, Z = 0, Color = color, U = 1, V = 1 });
        DrawSprite(_verts[0], _verts[1]);
        _verts.Clear();
        PrimitivesDrawn++;
    }

    public void DrawLine(int x0, int y0, int x1, int y1, uint color)
    {
        DrawLine(
            new Vertex { X = x0, Y = y0, Z = 0, Color = color },
            new Vertex { X = x1, Y = y1, Z = 0, Color = color });
        PrimitivesDrawn++;
    }

    private void DrawFilledTriangle(Vertex v0, Vertex v1, Vertex v2)
    {
        int minX = Math.Max(0, Math.Min(v0.X, Math.Min(v1.X, v2.X)));
        int maxX = Math.Min(FB_WIDTH - 1, Math.Max(v0.X, Math.Max(v1.X, v2.X)));
        int minY = Math.Max(0, Math.Min(v0.Y, Math.Min(v1.Y, v2.Y)));
        int maxY = Math.Min(FB_HEIGHT - 1, Math.Max(v0.Y, Math.Max(v1.Y, v2.Y)));

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!PointInTriangle(x, y, v0, v1, v2, out float wa, out float wb, out float wc))
                    continue;

                float z = v0.Z * wa + v1.Z * wb + v2.Z * wc;
                uint color = Registers.PrimIip || true
                    ? InterpolateColor(v0.Color, v1.Color, v2.Color, wa, wb, wc)
                    : v0.Color;
                float u = v0.U * wa + v1.U * wb + v2.U * wc;
                float v = v0.V * wa + v1.V * wb + v2.V * wc;
                // ST path
                if (!Registers.PrimFst)
                {
                    float s = v0.S * wa + v1.S * wb + v2.S * wc;
                    float t = v0.T * wa + v1.T * wb + v2.T * wc;
                    float q = v0.Q * wa + v1.Q * wb + v2.Q * wc;
                    if (Math.Abs(q) > 1e-6f) { s /= q; t /= q; }
                    u = s; v = t;
                }
                float fog = v0.Fog * wa + v1.Fog * wb + v2.Fog * wc;
                WriteFragment(x, y, z, color, u, v, fog);
            }
        }
    }

    private void WriteFragment(int x, int y, float z, uint color, float u, float v, float fog)
    {
        if (x < 0 || y < 0 || x >= FB_WIDTH || y >= FB_HEIGHT)
        {
            FragmentsRejectedBounds++;
            return;
        }
        if (!InScissor(x, y))
        {
            FragmentsRejectedScissor++;
            return;
        }

        int idx = y * FB_WIDTH + x;
        FragmentsTested++;

        // Hardware: ZTE=0 disables depth test entirely. Soft less-z against a cleared
        // buffer is not a GS feature and blocked commercial overdraw paths.
        if (Registers.DepthTestEnabled)
        {
            if (!DepthPass(z, _depthBuffer[idx], Registers.DepthTestMode))
            {
                FragmentsRejectedDepth++;
                return;
            }
        }

        uint final = color;
        if (Registers.PrimTme)
        {
            uint tex = SampleTexture(u, v);
            final = Modulate(color, tex);
        }

        // Alpha test — ATE; AFAIL bits 12-13: 0=KEEP 1=FB_ONLY 2=ZB_ONLY 3=RGB_ONLY
        bool alphaPass = AlphaTestPass(final);
        int afail = (int)((Registers.TEST_1 >> 12) & 3);
        if (!alphaPass)
        {
            FragmentsRejectedAlpha++;
            if (afail == 0)
                return; // KEEP
            if (afail == 2)
            {
                if (Registers.DepthWriteEnabled)
                    _depthBuffer[idx] = z;
                return;
            }
            // FB_ONLY / RGB_ONLY: still paint RGB below
        }

        if (Registers.PrimFge)
            final = ApplyFog(final, fog);

        if (Registers.PrimAbe || Registers.ALPHA_1 != 0)
            final = Blend(final, _framebuffer[idx]);

        if (alphaPass || afail is 1 or 3)
        {
            _framebuffer[idx] = (final & 0x00FFFFFFu) | 0xFF000000u;
            WriteFrameLocal(x, y, _framebuffer[idx]);
            PixelsWritten++;
        }
        if (alphaPass && (!Registers.DepthTestEnabled || Registers.DepthWriteEnabled))
            _depthBuffer[idx] = z;
        else if (!Registers.DepthTestEnabled)
            _depthBuffer[idx] = z;
    }

    /// <summary>
    /// Mirror a painted pixel into local VRAM at FRAME_1 (FBP units = 8192 bytes).
    /// Real GS draws into local mem; DISPFB composite then matches prim paint.
    /// PSMCT32/24 and PSMCT16/16S (GX-029) so commercial FRAME.PSM=0x0A still lands in local.
    /// </summary>
    private void WriteFrameLocal(int x, int y, uint pixel)
    {
        ulong frame = Registers.FRAME_1;
        int fbp = (int)(frame & 0x1FF);
        int fbw = (int)((frame >> 16) & 0x3F) * 64;
        int psm = (int)((frame >> 24) & 0x3F);
        if (fbw <= 0) fbw = FB_WIDTH;
        if (fbw > 4096) fbw = 4096;
        uint baseBytes = (uint)fbp * 8192u;
        if (baseBytes >= (uint)_localMem.Length) return;
        if (psm is 0x00 or 0x01 or 0)
        {
            int bi = (int)SwizzleOffset32(baseBytes, x, y, fbw);
            if ((uint)bi + 3u >= (uint)_localMem.Length) return;
            _localMem[bi] = (byte)pixel;
            _localMem[bi + 1] = (byte)(pixel >> 8);
            _localMem[bi + 2] = (byte)(pixel >> 16);
            _localMem[bi + 3] = (byte)(pixel >> 24);
            _localMemHasImage = true;
        }
        else if (psm is 0x02 or 0x0A)
        {
            // Pack Soft-GS 0xAARRGGBB → GS CT16 (R low, B high).
            int r5 = (int)((pixel >> 16) & 0xFF) >> 3;
            int g5 = (int)((pixel >> 8) & 0xFF) >> 3;
            int b5 = (int)(pixel & 0xFF) >> 3;
            int a1 = ((pixel >> 24) & 0x80) != 0 ? 1 : 0;
            ushort p16 = (ushort)((r5 & 0x1F) | ((g5 & 0x1F) << 5) | ((b5 & 0x1F) << 10) | (a1 << 15));
            // PSMCT16S (0x0A) uses a distinct page/block table from PSMCT16 (0x02).
            int bi = psm == 0x0A
                ? (int)SwizzleOffset16S(baseBytes, x, y, fbw)
                : (int)SwizzleOffset16(baseBytes, x, y, fbw);
            if ((uint)bi + 1u >= (uint)_localMem.Length) return;
            _localMem[bi] = (byte)p16;
            _localMem[bi + 1] = (byte)(p16 >> 8);
            _localMemHasImage = true;
        }
    }

    private static bool DepthPass(float z, float buf, int mode) => mode switch
    {
        0 => false,                         // NEVER
        1 => true,                          // ALWAYS
        2 => z <= buf,                      // GEQUAL (treat as closer-or-equal with smaller z)
        3 => z < buf,                       // GREATER → closer
        _ => z <= buf
    };

    /// <summary>
    /// GS texture MODULATE: components multiply with 0x80 = 1.0 (not 0xFF).
    /// Using /255 dimmed 0x80×0x80 → 0x40 so ATE GEQUAL AREF=0x80 rejected every
    /// commercial textured fragment (B3 claim: fragTest=1904 rejAlpha=1904 px=0).
    /// </summary>
    private static uint Modulate(uint color, uint tex)
    {
        byte cr = (byte)((color >> 16) & 0xFF);
        byte cg = (byte)((color >> 8) & 0xFF);
        byte cb = (byte)(color & 0xFF);
        byte ca = (byte)((color >> 24) & 0xFF);
        byte tr = (byte)((tex >> 16) & 0xFF);
        byte tg = (byte)((tex >> 8) & 0xFF);
        byte tb = (byte)(tex & 0xFF);
        byte ta = (byte)((tex >> 24) & 0xFF);
        return (uint)((Mul80(ca, ta) << 24) | (Mul80(cr, tr) << 16) | (Mul80(cg, tg) << 8) | Mul80(cb, tb));
    }

    /// <summary>PS2 GS (a*b)/128 with clamp; 0x80×0x80 → 0x80.</summary>
    private static byte Mul80(byte a, byte b)
    {
        int v = (a * b) >> 7;
        return v > 255 ? (byte)255 : (byte)v;
    }

    private uint ApplyFog(uint color, float fog)
    {
        // fog 0 = full fog color, 1 = full source
        uint fc = (uint)Registers.FOGCOL;
        if (fc == 0) fc = 0x00808080;
        float f = Math.Clamp(fog, 0f, 1f);
        byte sr = (byte)((color >> 16) & 0xFF);
        byte sg = (byte)((color >> 8) & 0xFF);
        byte sb = (byte)(color & 0xFF);
        byte fr = (byte)((fc >> 16) & 0xFF);
        byte fg = (byte)((fc >> 8) & 0xFF);
        byte fb = (byte)(fc & 0xFF);
        byte r = (byte)(sr * f + fr * (1 - f));
        byte g = (byte)(sg * f + fg * (1 - f));
        byte b = (byte)(sb * f + fb * (1 - f));
        return (color & 0xFF000000) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    // ===================== GS local memory swizzle addressing =====================
    // Real GS VRAM is not laid out row-major; it's tiled into 8KB "pages" made of 32
    // fixed-layout blocks, each block internally column-swizzled. Real games'
    // texture/framebuffer data is written and expected to be read back in this layout —
    // naive row-major addressing only round-trips data this engine wrote itself, and
    // would render real GS-hardware-authored texture data as scrambled noise.
    // Block/column tables below are transcribed VERBATIM from PCSX2's GSTables.cpp
    // (github.com/PCSX2/pcsx2). PSMCT16 (0x02) and PSMCT16S (0x0A) share columnTable16
    // but have DISTINCT block tables — using CT16 blocks for DISPFB PSM=0x0A (Dec-class
    // FBW=832) produces jumbled Soft-GS present. PSMT8 column uses Morton interleave
    // extension of columnTable32 (reasoned, not verbatim). PSMT4 remains linear residual.
    private static readonly int[,] BlockTable32 =
    {
        { 0, 1, 4, 5,16,17,20,21},
        { 2, 3, 6, 7,18,19,22,23},
        { 8, 9,12,13,24,25,28,29},
        {10,11,14,15,26,27,30,31}
    };
    private static readonly int[,] ColumnTable32 =
    {
        { 0, 1, 4, 5, 8, 9,12,13},
        { 2, 3, 6, 7,10,11,14,15},
        {16,17,20,21,24,25,28,29},
        {18,19,22,23,26,27,30,31},
        {32,33,36,37,40,41,44,45},
        {34,35,38,39,42,43,46,47},
        {48,49,52,53,56,57,60,61},
        {50,51,54,55,58,59,62,63}
    };

    /// <summary>Bit-interleave (Morton/Z-order) x and y into a single index — the pattern
    /// ColumnTable32 was confirmed to follow (x supplies even bits, y supplies odd bits).</summary>
    private static int MortonInterleave(int x, int y, int bits)
    {
        int r = 0;
        for (int i = 0; i < bits; i++)
        {
            r |= ((x >> i) & 1) << (2 * i);
            r |= ((y >> i) & 1) << (2 * i + 1);
        }
        return r;
    }

    /// <summary>PSMCT32 swizzled byte offset for pixel (x,y). Page 64x32px, block 8x8px,
    /// 4 bytes/pixel, 256 bytes/block (8192/32), 8192 bytes/page.</summary>
    private static uint SwizzleOffset32(uint texBaseBytes, int x, int y, int bufferWidthPx)
    {
        const int pageW = 64, pageH = 32, blockW = 8, blockH = 8;
        int pagesPerRow = Math.Max(1, (bufferWidthPx + pageW - 1) / pageW);
        int pageX = x / pageW, pageY = y / pageH;
        int pageIdx = pageY * pagesPerRow + pageX;
        int ix = x % pageW, iy = y % pageH;
        int blockIdx = BlockTable32[(iy / blockH) % 4, (ix / blockW) % 8];
        int pixelIdx = ColumnTable32[iy % blockH, ix % blockW];
        return texBaseBytes + (uint)(pageIdx * 8192 + blockIdx * 256 + pixelIdx * 4);
    }

    /// <summary>PSMT8 swizzled byte offset for pixel (x,y). Page 128x64px, block 16x16px
    /// (256 bytes/block, matching PSMT32's block-table shape), 1 byte/pixel.</summary>
    private static uint SwizzleOffset8(uint texBaseBytes, int x, int y, int bufferWidthPx)
    {
        const int pageW = 128, pageH = 64, blockW = 16, blockH = 16;
        int pagesPerRow = Math.Max(1, (bufferWidthPx + pageW - 1) / pageW);
        int pageX = x / pageW, pageY = y / pageH;
        int pageIdx = pageY * pagesPerRow + pageX;
        int ix = x % pageW, iy = y % pageH;
        int blockIdx = BlockTable32[(iy / blockH) % 4, (ix / blockW) % 8];
        int pixelIdx = MortonInterleave(ix % blockW, iy % blockH, 4);
        return texBaseBytes + (uint)(pageIdx * 8192 + blockIdx * 256 + pixelIdx);
    }

    // PCSX2-derived block table for PSMCT16 (0x02): page 64×64, block 16×8, 2 bytes/pixel.
    private static readonly int[,] BlockTable16 =
    {
        { 0,  2,  8, 10},
        { 1,  3,  9, 11},
        { 4,  6, 12, 14},
        { 5,  7, 13, 15},
        {16, 18, 24, 26},
        {17, 19, 25, 27},
        {20, 22, 28, 30},
        {21, 23, 29, 31}
    };

    // PCSX2-derived block table for PSMCT16S (0x0A) — same page/block geometry as CT16,
    // different block order (frame/DISPFB storage layout). Shared columnTable16 within-block.
    private static readonly int[,] BlockTable16S =
    {
        { 0,  2, 16, 18},
        { 1,  3, 17, 19},
        { 8, 10, 24, 26},
        { 9, 11, 25, 27},
        { 4,  6, 20, 22},
        { 5,  7, 21, 23},
        {12, 14, 28, 30},
        {13, 15, 29, 31}
    };

    // PCSX2 columnTable16[y][x] — within-block pixel index for both PSMCT16 and PSMCT16S.
    // Block is 16×8; values 0..127. Distinct from Morton interleave.
    private static readonly byte[,] ColumnTable16 =
    {
        {  0,  2,  8, 10, 16, 18, 24, 26,  1,  3,  9, 11, 17, 19, 25, 27 },
        {  4,  6, 12, 14, 20, 22, 28, 30,  5,  7, 13, 15, 21, 23, 29, 31 },
        { 32, 34, 40, 42, 48, 50, 56, 58, 33, 35, 41, 43, 49, 51, 57, 59 },
        { 36, 38, 44, 46, 52, 54, 60, 62, 37, 39, 45, 47, 53, 55, 61, 63 },
        { 64, 66, 72, 74, 80, 82, 88, 90, 65, 67, 73, 75, 81, 83, 89, 91 },
        { 68, 70, 76, 78, 84, 86, 92, 94, 69, 71, 77, 79, 85, 87, 93, 95 },
        { 96, 98,104,106,112,114,120,122, 97, 99,105,107,113,115,121,123 },
        {100,102,108,110,116,118,124,126,101,103,109,111,117,119,125,127 }
    };

    /// <summary>
    /// PSMCT16 (0x02) swizzled byte offset (GX-029). Page 64×64px, block 16×8px,
    /// 2 bytes/pixel, 256 bytes/block. Block table = BlockTable16; column = ColumnTable16.
    /// </summary>
    private static uint SwizzleOffset16(uint texBaseBytes, int x, int y, int bufferWidthPx)
    {
        const int pageW = 64, pageH = 64, blockW = 16, blockH = 8;
        int pagesPerRow = Math.Max(1, (bufferWidthPx + pageW - 1) / pageW);
        int pageX = x / pageW, pageY = y / pageH;
        int pageIdx = pageY * pagesPerRow + pageX;
        int ix = x % pageW, iy = y % pageH;
        int blockIdx = BlockTable16[(iy / blockH) % 8, (ix / blockW) % 4];
        int pixelIdx = ColumnTable16[iy % blockH, ix % blockW];
        // 16×8 block = 128 texels × 2 bytes = 256 bytes
        return texBaseBytes + (uint)(pageIdx * 8192 + blockIdx * 256 + pixelIdx * 2);
    }

    /// <summary>
    /// PSMCT16S (0x0A) swizzled byte offset — distinct from PSMCT16 for DISPFB/FRAME storage.
    /// Same page 64×64 / block 16×8 / column table as CT16; BlockTable16S only (PCSX2).
    /// Deception DISPFB2 PSM=10 FBW=832 requires this table for coherent Soft-GS present.
    /// </summary>
    private static uint SwizzleOffset16S(uint texBaseBytes, int x, int y, int bufferWidthPx)
    {
        const int pageW = 64, pageH = 64, blockW = 16, blockH = 8;
        int pagesPerRow = Math.Max(1, (bufferWidthPx + pageW - 1) / pageW);
        int pageX = x / pageW, pageY = y / pageH;
        int pageIdx = pageY * pagesPerRow + pageX;
        int ix = x % pageW, iy = y % pageH;
        int blockIdx = BlockTable16S[(iy / blockH) % 8, (ix / blockW) % 4];
        int pixelIdx = ColumnTable16[iy % blockH, ix % blockW];
        return texBaseBytes + (uint)(pageIdx * 8192 + blockIdx * 256 + pixelIdx * 2);
    }

    public uint SampleTexture(float u, float v)
    {
        int tw = _texWidth;
        int th = _texHeight;
        float fu = u, fv = v;

        // GX-034: CLAMP WMS/WMT — 0=REPEAT, 1=CLAMP, 2=REGION_CLAMP, 3=REGION_REPEAT
        int wms = Registers.ClampWms;
        int wmt = Registers.ClampWmt;
        ApplyClampCoord(ref fu, wms, Registers.ClampMinU, Registers.ClampMaxU, tw);
        ApplyClampCoord(ref fv, wmt, Registers.ClampMinV, Registers.ClampMaxV, th);

        if (BilinearFilter && !_useProceduralTexture && tw > 1 && th > 1)
        {
            BilinearSamples++;
            float x = fu * (tw - 1);
            float y = fv * (th - 1);
            int x0 = Math.Clamp((int)MathF.Floor(x), 0, tw - 1);
            int y0 = Math.Clamp((int)MathF.Floor(y), 0, th - 1);
            int x1 = Math.Min(x0 + 1, tw - 1);
            int y1 = Math.Min(y0 + 1, th - 1);
            float fx = x - x0, fy = y - y0;
            uint c00 = SampleTexel(x0, y0);
            uint c10 = SampleTexel(x1, y0);
            uint c01 = SampleTexel(x0, y1);
            uint c11 = SampleTexel(x1, y1);
            return Bilerp(c00, c10, c01, c11, fx, fy);
        }

        int tu = Math.Clamp((int)(fu * tw), 0, tw - 1);
        int tv = Math.Clamp((int)(fv * th), 0, th - 1);

        if (_useProceduralTexture)
        {
            bool checker = ((tu / 8) + (tv / 8)) % 2 == 0;
            return checker ? 0xFFFF00FF : 0xFF00FFFF;
        }

        return SampleTexel(tu, tv);
    }

    /// <summary>Normalize ST/UV scalar into [0,1]-ish domain per CLAMP mode, then texel scale.</summary>
    private static void ApplyClampCoord(ref float f, int mode, int minT, int maxT, int texSize)
    {
        switch (mode)
        {
            case 1: // CLAMP
                f = Math.Clamp(f, 0f, 1f);
                break;
            case 2: // REGION_CLAMP — clamp to [MIN,MAX] in texel space, return as normalized
            {
                float t = f * texSize;
                float lo = minT;
                float hi = Math.Max(minT, maxT);
                t = Math.Clamp(t, lo, hi);
                f = texSize > 0 ? t / texSize : 0f;
                break;
            }
            case 3: // REGION_REPEAT — wrap within [MIN, MAX]
            {
                float t = f * texSize;
                float lo = minT;
                float hi = Math.Max(minT + 1, maxT + 1);
                float span = hi - lo;
                if (span <= 0) span = 1;
                float rel = t - lo;
                rel = rel - span * MathF.Floor(rel / span);
                f = texSize > 0 ? (lo + rel) / texSize : 0f;
                break;
            }
            default: // REPEAT
                f = f - MathF.Floor(f);
                break;
        }
    }

    private uint SampleTexel(int tu, int tv)
    {
        int tw = _texWidth;
        int th = _texHeight;
        tu = Math.Clamp(tu, 0, tw - 1);
        tv = Math.Clamp(tv, 0, th - 1);
        if (_useProceduralTexture)
        {
            bool checker = ((tu / 8) + (tv / 8)) % 2 == 0;
            return checker ? 0xFFFF00FF : 0xFF00FFFF;
        }

        TexSamplesLocal++;
        int psm = Registers.TexPsm;
        int bufW = _texBufWidth > 0 ? _texBufWidth : Math.Max(64, tw);

        // PSMT8 (0x13) / PSMT8H (0x1B): 8-bit index → CLUT (swizzled)
        if (psm is 0x13 or 0x1B)
        {
            int bi = (int)SwizzleOffset8(_texBase, tu, tv, bufW);
            if (bi < 0 || bi >= _localMem.Length) return 0xFFFFFFFF;
            byte idx8 = _localMem[bi];
            return _hasClut ? _clut[idx8] : 0xFF000000u | ((uint)idx8 << 16) | ((uint)idx8 << 8) | idx8;
        }
        // PSMT4 (0x14): 4-bit index → CLUT (linear residual layout)
        if (psm == 0x14)
        {
            int bi = (int)(_texBase + (tv * bufW + tu) / 2);
            if (bi < 0 || bi >= _localMem.Length) return 0xFFFFFFFF;
            byte packed = _localMem[bi];
            int nibble = ((tu + tv * bufW) & 1) == 0 ? (packed & 0xF) : (packed >> 4);
            return _hasClut ? _clut[nibble & 0xF] : 0xFF000000u | (uint)(nibble * 17) * 0x010101u;
        }
        // PSMCT16 (0x02) / PSMCT16S (0x0A): RGB555 + TEXA; 16S uses distinct block table
        if (psm is 0x02 or 0x0A)
        {
            int bi = psm == 0x0A
                ? (int)SwizzleOffset16S(_texBase, tu, tv, bufW)
                : (int)SwizzleOffset16(_texBase, tu, tv, bufW);
            if (bi < 0 || bi + 1 >= _localMem.Length) return 0xFFFFFFFF;
            ushort p = (ushort)(_localMem[bi] | (_localMem[bi + 1] << 8));
            return ExpandRgb555(p);
        }
        // PSMCT24 (0x01): 24-bit RGB in 32-bit slot (low 24), alpha from TEXA.TA0
        if (psm == 0x01)
        {
            int bi = (int)SwizzleOffset32(_texBase, tu, tv, bufW);
            if (bi < 0 || bi + 2 >= _localMem.Length) return 0xFFFFFFFF;
            int b = _localMem[bi];
            int g = _localMem[bi + 1];
            int r = _localMem[bi + 2];
            int a = Registers.TEXA == 0 ? 0xFF : Registers.TexaTa0;
            if (Registers.TexaAem && r == 0 && g == 0 && b == 0) a = 0;
            return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
        }

        // PSMCT32 / default — real block-swizzled addressing (see SwizzleOffset32).
        int byteIndex = (int)SwizzleOffset32(_texBase, tu, tv, bufW);
        if (byteIndex < 0 || byteIndex + 3 >= _localMem.Length)
            return 0xFFFFFFFF;
        return (uint)(_localMem[byteIndex]
            | (_localMem[byteIndex + 1] << 8)
            | (_localMem[byteIndex + 2] << 16)
            | (_localMem[byteIndex + 3] << 24));
    }

    private static uint Bilerp(uint c00, uint c10, uint c01, uint c11, float fx, float fy)
    {
        static byte Lerp(byte a, byte b, float t) => (byte)(a + (b - a) * t);
        static uint Chan(uint c, int shift) => (c >> shift) & 0xFF;
        byte r = Lerp(Lerp((byte)Chan(c00, 16), (byte)Chan(c10, 16), fx),
                      Lerp((byte)Chan(c01, 16), (byte)Chan(c11, 16), fx), fy);
        byte g = Lerp(Lerp((byte)Chan(c00, 8), (byte)Chan(c10, 8), fx),
                      Lerp((byte)Chan(c01, 8), (byte)Chan(c11, 8), fx), fy);
        byte b = Lerp(Lerp((byte)Chan(c00, 0), (byte)Chan(c10, 0), fx),
                      Lerp((byte)Chan(c01, 0), (byte)Chan(c11, 0), fx), fy);
        byte a = Lerp(Lerp((byte)Chan(c00, 24), (byte)Chan(c10, 24), fx),
                      Lerp((byte)Chan(c01, 24), (byte)Chan(c11, 24), fx), fy);
        return (uint)((a << 24) | (r << 16) | (g << 8) | b);
    }

    /// <summary>Upload 8-bit indexed texture + optional 256-entry CLUT (PSMT8).</summary>
    public void UploadTexture8(int destWordAddr, int width, int height, ReadOnlySpan<byte> indices, ReadOnlySpan<uint> clutRgba)
    {
        _texBase = (uint)(destWordAddr * 64);
        _texWidth = width;
        _texHeight = height;
        _texBufWidth = Math.Max(64, width);
        _useProceduralTexture = false;
        int tbw = Math.Max(1, (width + 63) / 64);
        Registers.WriteRegister64(0x06,
            (ulong)(destWordAddr & 0x3FFF)
            | ((ulong)(tbw & 0x3F) << 14)
            | ((ulong)0x13 << 20) // PSMT8
            | ((ulong)Log2(width) << 26)
            | ((ulong)Log2(height) << 30));
        int n = Math.Min(indices.Length, width * height);
        for (int i = 0; i < n; i++)
        {
            int bi = (int)SwizzleOffset8(_texBase, i % width, i / width, _texBufWidth);
            if (bi >= _localMem.Length) break;
            _localMem[bi] = indices[i];
        }
        if (clutRgba.Length > 0)
        {
            int c = Math.Min(256, clutRgba.Length);
            for (int i = 0; i < c; i++)
                _clut[i] = clutRgba[i] | 0xFF000000u;
            _hasClut = true;
            _clutBase = _texBase;
        }
    }

    private bool AlphaTestPass(uint color)
    {
        // TEST_1: ATE bit 0 of low; ATST bits 1-3; AREF bits 4-11 (simplified via Registers if present)
        // If no alpha test configured, pass
        ulong test = Registers.TEST_1;
        bool ate = (test & 1) != 0;
        if (!ate) return true;
        int atst = (int)((test >> 1) & 7);
        int aref = (int)((test >> 4) & 0xFF);
        int a = (int)((color >> 24) & 0xFF);
        return atst switch
        {
            0 => false,           // NEVER
            1 => true,            // ALWAYS
            2 => a < aref,        // LESS
            3 => a <= aref,       // LEQUAL
            4 => a == aref,       // EQUAL
            5 => a >= aref,       // GEQUAL
            6 => a > aref,        // GREATER
            7 => a != aref,       // NOTEQUAL
            _ => true
        };
    }

    /// <summary>Upload texture pixels into local GS memory (PSMCT32).</summary>
    public void UploadTexture(int destWordAddr, int width, int height, ReadOnlySpan<uint> pixels)
    {
        _texBase = (uint)(destWordAddr * 64);
        _texWidth = width;
        _texHeight = height;
        _texBufWidth = Math.Max(64, width);
        _useProceduralTexture = false;
        int tbw = Math.Max(1, (width + 63) / 64);
        Registers.WriteRegister64(0x06,
            (ulong)(destWordAddr & 0x3FFF)
            | ((ulong)(tbw & 0x3F) << 14)
            | ((ulong)0 << 20) // PSMCT32
            | ((ulong)Log2(width) << 26)
            | ((ulong)Log2(height) << 30));

        int n = Math.Min(pixels.Length, width * height);
        for (int i = 0; i < n; i++)
        {
            int bi = (int)SwizzleOffset32(_texBase, i % width, i / width, _texBufWidth);
            if (bi + 3 >= _localMem.Length) break;
            uint p = pixels[i];
            _localMem[bi] = (byte)p;
            _localMem[bi + 1] = (byte)(p >> 8);
            _localMem[bi + 2] = (byte)(p >> 16);
            _localMem[bi + 3] = (byte)(p >> 24);
        }
        if (n > 0)
        {
            ImageBytesWritten += (long)n * 4;
            _localMemHasImage = true;
        }
    }

    /// <summary>Upload 16-bit RGB555 texture (PSMCT16) into local GS memory (swizzled, GX-029).</summary>
    public void UploadTexture16(int destWordAddr, int width, int height, ReadOnlySpan<ushort> pixels)
    {
        _texBase = (uint)(destWordAddr * 64);
        _texWidth = width;
        _texHeight = height;
        _texBufWidth = Math.Max(64, width);
        _useProceduralTexture = false;
        int tbw = Math.Max(1, (width + 63) / 64);
        Registers.WriteRegister64(0x06,
            (ulong)(destWordAddr & 0x3FFF)
            | ((ulong)(tbw & 0x3F) << 14)
            | ((ulong)0x02 << 20) // PSMCT16
            | ((ulong)Log2(width) << 26)
            | ((ulong)Log2(height) << 30));

        int n = Math.Min(pixels.Length, width * height);
        for (int i = 0; i < n; i++)
        {
            int bi = (int)SwizzleOffset16(_texBase, i % width, i / width, _texBufWidth);
            if (bi + 1 >= _localMem.Length) break;
            ushort p = pixels[i];
            _localMem[bi] = (byte)p;
            _localMem[bi + 1] = (byte)(p >> 8);
        }
        if (n > 0)
        {
            ImageBytesWritten += (long)n * 2;
            _localMemHasImage = true;
        }
    }

    /// <summary>
    /// IMAGE path (GIF FLG=2): host→local VRAM fill.
    /// When a Host→Local transfer is active (TRXDIR.XDIR=0 after BITBLTBUF/TRXPOS/TRXREG),
    /// bytes are written through the per-pixel TRX cursor with PSMCT32/PSMT8 swizzle so
    /// SampleTexel sees the same layout as <see cref="UploadTexture"/> / <see cref="UploadTexture8"/>.
    /// Without an active transfer, falls back to a linear write at <paramref name="destByteOffset"/>
    /// (legacy / synthetic packets that never programmed BITBLT).
    /// </summary>
    public void WriteImageData(ReadOnlySpan<byte> data, int destByteOffset)
    {
        if (_trxActive)
        {
            WriteImageTransfer(data);
            return;
        }

        int n = Math.Min(data.Length, _localMem.Length - destByteOffset);
        if (n <= 0) return;
        data.Slice(0, n).CopyTo(_localMem.AsSpan(destByteOffset));
        _useProceduralTexture = false;
        ImageBytesWritten += n;
        _localMemHasImage = true;
    }

    /// <summary>Stream host IMAGE bytes into local mem using BITBLT/TRX cursor.</summary>
    private void WriteImageTransfer(ReadOnlySpan<byte> data)
    {
        // PSMT4: two pixels per byte — special packing path.
        if (_trxDpsm == 0x14)
        {
            WriteImageTransferPsmt4(data);
            return;
        }

        int bpp = TrxBytesPerPixel(_trxDpsm);
        if (bpp <= 0) bpp = 4;

        for (int i = 0; i < data.Length && _trxActive; i++)
        {
            _trxPartial |= (uint)data[i] << (8 * _trxPending);
            _trxPending++;
            if (_trxPending < bpp) continue;

            uint pixel = _trxPartial;
            _trxPartial = 0;
            _trxPending = 0;
            StoreTrxPixel(pixel, bpp);

            _trxX++;
            if (_trxX >= _trxW)
            {
                _trxX = 0;
                _trxY++;
                if (_trxY >= _trxH)
                    _trxActive = false;
            }
        }
    }

    /// <summary>PSMT4 Host→Local: each host byte holds two 4-bit indices (lo nibble first).</summary>
    private void WriteImageTransferPsmt4(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length && _trxActive; i++)
        {
            byte b = data[i];
            ImageBytesWritten++;
            _localMemHasImage = true;
            for (int n = 0; n < 2 && _trxActive; n++)
            {
                uint nibble = (n == 0) ? (uint)(b & 0xF) : (uint)(b >> 4);
                int px = _trxDsaX + _trxX;
                int py = _trxDsaY + _trxY;
                StoreLocalPixel(_trxDbpBytes, px, py, _trxDbwPx, 0x14, nibble);
                _trxX++;
                if (_trxX >= _trxW)
                {
                    _trxX = 0;
                    _trxY++;
                    if (_trxY >= _trxH)
                        _trxActive = false;
                }
            }
        }
    }

    private static int TrxBytesPerPixel(int psm) => psm switch
    {
        0x00 => 4, // PSMCT32
        0x01 => 3, // PSMCT24
        0x02 => 2, // PSMCT16
        0x0A => 2, // PSMCT16S
        0x13 => 1, // PSMT8
        0x1B => 1, // PSMT8H
        0x14 => 1, // PSMT4 counted as packed bytes (special path)
        _ => 4
    };

    private void StoreTrxPixel(uint pixel, int bpp)
    {
        int px = _trxDsaX + _trxX;
        int py = _trxDsaY + _trxY;
        StoreLocalPixel(_trxDbpBytes, px, py, _trxDbwPx, _trxDpsm, pixel);
        ImageBytesWritten += bpp;
        _localMemHasImage = true;
    }

    private static int Log2(int v)
    {
        int l = 0;
        while ((1 << l) < v && l < 10) l++;
        return l;
    }

    public void SetTexture(uint baseAddr, int width, int height)
    {
        _texBase = baseAddr;
        _texWidth = width;
        _texHeight = height;
        _texBufWidth = Math.Max(64, width);
        // GX-035: only pure zero-size / unprogrammed host path keeps procedural.
        // Non-zero base or any positive size arms local sample (commercial TBP0=0 still uses Upload*).
        _useProceduralTexture = baseAddr == 0 && width <= 0;
        if (width > 0 && height > 0 && baseAddr == 0)
            _useProceduralTexture = false; // TBP0=0 is valid local page 0
    }

    private uint Blend(uint src, uint dst)
    {
        // Formula: (A - B) * C >> 7 + D  with A/B/D in {Cs, Cd, 0}, C in {As, Ad, FIX}
        int aSel = Registers.AlphaA;
        int bSel = Registers.AlphaB;
        int cSel = Registers.AlphaC;
        int dSel = Registers.AlphaD;
        int fix = Registers.AlphaFix;

        // Default to standard SRC_OVER if ALPHA not programmed
        if (Registers.ALPHA_1 == 0)
        {
            byte srcA = (byte)((src >> 24) & 0xFF);
            if (srcA == 0) return dst;
            if (srcA == 255) return src;
            return LerpColor(dst, src, srcA / 255f) | 0xFF000000;
        }

        Span<int> cs = stackalloc int[3];
        Span<int> cd = stackalloc int[3];
        cs[0] = (int)((src >> 16) & 0xFF);
        cs[1] = (int)((src >> 8) & 0xFF);
        cs[2] = (int)(src & 0xFF);
        cd[0] = (int)((dst >> 16) & 0xFF);
        cd[1] = (int)((dst >> 8) & 0xFF);
        cd[2] = (int)(dst & 0xFF);
        int as_ = (int)((src >> 24) & 0xFF);
        int ad = (int)((dst >> 24) & 0xFF);

        int Cv = cSel switch { 0 => as_, 1 => ad, 2 => fix, _ => 128 };

        int r = ChannelBlend(cs[0], cd[0], aSel, bSel, dSel, Cv);
        int g = ChannelBlend(cs[1], cd[1], aSel, bSel, dSel, Cv);
        int b = ChannelBlend(cs[2], cd[2], aSel, bSel, dSel, Cv);
        r = Math.Clamp(r, 0, 255);
        g = Math.Clamp(g, 0, 255);
        b = Math.Clamp(b, 0, 255);
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private static int ChannelBlend(int cs, int cd, int aSel, int bSel, int dSel, int c)
    {
        int A = aSel switch { 0 => cs, 1 => cd, _ => 0 };
        int B = bSel switch { 0 => cs, 1 => cd, _ => 0 };
        int D = dSel switch { 0 => cs, 1 => cd, _ => 0 };
        return ((A - B) * c) / 128 + D;
    }

    private static uint LerpColor(uint a, uint b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        byte ar = (byte)((a >> 16) & 0xFF), ag = (byte)((a >> 8) & 0xFF), ab = (byte)(a & 0xFF), aa = (byte)((a >> 24) & 0xFF);
        byte br = (byte)((b >> 16) & 0xFF), bg = (byte)((b >> 8) & 0xFF), bb = (byte)(b & 0xFF), ba = (byte)((b >> 24) & 0xFF);
        byte r = (byte)(ar + (br - ar) * t);
        byte g = (byte)(ag + (bg - ag) * t);
        byte bl = (byte)(ab + (bb - ab) * t);
        byte al = (byte)(aa + (ba - aa) * t);
        return (uint)((al << 24) | (r << 16) | (g << 8) | bl);
    }

    private static bool PointInTriangle(int px, int py, Vertex v0, Vertex v1, Vertex v2, out float a, out float b, out float c)
    {
        float denom = (v1.Y - v2.Y) * (v0.X - v2.X) + (v2.X - v1.X) * (v0.Y - v2.Y);
        if (Math.Abs(denom) < 0.0001f) { a = b = c = 0; return false; }
        a = ((v1.Y - v2.Y) * (px - v2.X) + (v2.X - v1.X) * (py - v2.Y)) / denom;
        b = ((v2.Y - v0.Y) * (px - v2.X) + (v0.X - v2.X) * (py - v2.Y)) / denom;
        c = 1 - a - b;
        return a >= -0.001f && b >= -0.001f && c >= -0.001f;
    }

    private static uint InterpolateColor(uint c0, uint c1, uint c2, float a, float b, float c)
    {
        int r = (int)(((c0 >> 16) & 0xFF) * a + ((c1 >> 16) & 0xFF) * b + ((c2 >> 16) & 0xFF) * c);
        int g = (int)(((c0 >> 8) & 0xFF) * a + ((c1 >> 8) & 0xFF) * b + ((c2 >> 8) & 0xFF) * c);
        int bl = (int)((c0 & 0xFF) * a + (c1 & 0xFF) * b + (c2 & 0xFF) * c);
        int al = (int)(((c0 >> 24) & 0xFF) * a + ((c1 >> 24) & 0xFF) * b + ((c2 >> 24) & 0xFF) * c);
        r = Math.Clamp(r, 0, 255); g = Math.Clamp(g, 0, 255); bl = Math.Clamp(bl, 0, 255); al = Math.Clamp(al, 0, 255);
        return (uint)((al << 24) | (r << 16) | (g << 8) | bl);
    }

    // ===================== Legacy / test scene =====================

    public void ReceiveCommandList(uint address, uint qwc)
    {
        // Fallback A+D stream without GIF tags (legacy)
        if (qwc == 0) return;
        uint addr = address;
        for (uint i = 0; i < qwc; i++)
        {
            uint lo = Memory.Read32(addr);
            uint hi = Memory.Read32(addr + 4);
            ProcessGifPackedWord(lo, hi);
            addr += 16;
        }
    }

    public void RenderTestScene()
    {
        uint bg = 0xFF1A1A3A;
        for (int i = 0; i < _framebuffer.Length; i++)
        {
            _framebuffer[i] = bg;
            _depthBuffer[i] = float.MaxValue;
        }

        _useProceduralTexture = true;
        _currentPrim = 0x13; // tri + IIP + TME bits optional
        Registers.WriteRegister64(0x00, 0x13);
        DrawFilledTriangle(
            new Vertex { X = 120, Y = 80, Color = 0xFF00FF00, U = 0, V = 0, Z = 0.1f },
            new Vertex { X = 320, Y = 80, Color = 0xFF00FF00, U = 1, V = 0, Z = 0.1f },
            new Vertex { X = 220, Y = 280, Color = 0xFF00FF00, U = 0.5f, V = 1, Z = 0.5f });
        PrimitivesDrawn++;

        DrawFilledTriangle(
            new Vertex { X = 340, Y = 100, Color = 0xFFFF0000, U = 0, V = 0, Z = 0.9f },
            new Vertex { X = 540, Y = 100, Color = 0xFFFF0000, U = 1, V = 0, Z = 0.9f },
            new Vertex { X = 440, Y = 300, Color = 0xFFFF0000, U = 0.5f, V = 1, Z = 0.2f });
        PrimitivesDrawn++;

        DrawQuad(80, 320, 160, 80, 0xFF00BFFF);
        DrawQuad(400, 320, 160, 80, 0xFFFFD700);
        DrawLine(100, 60, 540, 60, 0xFFFFFFFF);
        DrawLine(100, 380, 540, 380, 0xFFFFFFFF);
    }

    public void DrawTestTriangle()
    {
        DrawFilledTriangle(
            new Vertex { X = 200, Y = 150, Color = _currentRgbaq, U = 0, V = 0, Z = 0 },
            new Vertex { X = 440, Y = 150, Color = _currentRgbaq, U = 1, V = 0, Z = 0 },
            new Vertex { X = 320, Y = 350, Color = _currentRgbaq, U = 0.5f, V = 1, Z = 0 });
        PrimitivesDrawn++;
    }

    public void SaveFramebufferAsPPM(string filename)
    {
        using var writer = new StreamWriter(filename);
        writer.WriteLine("P3");
        writer.WriteLine($"{FB_WIDTH} {FB_HEIGHT}");
        writer.WriteLine("255");
        for (int y = 0; y < FB_HEIGHT; y++)
        {
            for (int x = 0; x < FB_WIDTH; x++)
            {
                uint p = _framebuffer[y * FB_WIDTH + x];
                writer.WriteLine($"{(p >> 16) & 0xFF} {(p >> 8) & 0xFF} {p & 0xFF}");
            }
        }
    }

    public uint[] GetFramebuffer() => (uint[])_framebuffer.Clone();

    /// <summary>Zero-copy view of the software raster buffer (game draws).</summary>
    public ReadOnlySpan<uint> GetFramebufferSpan() => _framebuffer;

    /// <summary>Raw local-VRAM byte access (textures/CLUTs live here — see SwizzleOffset32/
    /// SampleTexel). Exposed directly for tooling/tests that need to verify _localMem's
    /// contents without going through the full UploadTexture+draw+sample path.</summary>
    public byte[] ReadLocalMem(int offset, int length)
    {
        var buf = new byte[length];
        Array.Copy(_localMem, offset, buf, 0, Math.Min(length, _localMem.Length - offset));
        return buf;
    }

    public void WriteLocalMem(int offset, ReadOnlySpan<byte> data) =>
        data.CopyTo(_localMem.AsSpan(offset));

    /// <summary>Restores framebuffer pixels from an old (pre-v5) save state — those versions
    /// only ever saved the pixel bytes, not any of the rest of GS state WriteState now covers,
    /// so this is a narrow, backward-compat-only helper, not part of the current save format.</summary>
    public void RestoreFramebuffer(ReadOnlySpan<uint> pixels)
    {
        int n = Math.Min(pixels.Length, _framebuffer.Length);
        pixels.Slice(0, n).CopyTo(_framebuffer);
    }

    /// <summary>
    /// What the host should show for Soft-GS truth: the software raster framebuffer only.
    /// Host FMV/boot overlay is retired (IRX era) — never preferred over Soft-GS, even if a
    /// legacy assist still toggled <see cref="HostOverlayActive"/>.
    /// Desktop and PresentPipeline should use this for display / PPM.
    /// When prim raster is still empty but IMAGE filled local VRAM and DISPFB/FRAME is set,
    /// composite local→FB (SOFTGS_IRX_ERA residual #1 — commercial logo path).
    /// </summary>
    public ReadOnlySpan<uint> GetPresentSpan()
    {
        // Wave-5: merge composite even after sparse prim paint so logo IMAGE under
        // DISPFB/FRAME/FBP0 can still fill Soft-GS chrome (B3 early px blocked this).
        // Prefer flag, but ImageBytesWritten alone is enough (defensive after black wipe).
        if (_localMemHasImage || ImageBytesWritten > 0)
        {
            CompositeDispfbToFramebuffer();
            // Black full-FB prims (BO2 class: px=full FB lit=0) stamp Soft-GS while logo
            // lives only in local IMAGE. Force one re-merge when still mostly black;
            // _mergeBlackBypassArmed prevents per-present thrash when IMAGE is truly empty RGB.
            if (IsPresentMostlyBlack() && !_mergeBlackBypassArmed)
                ForceRefreshPresentComposite();
        }
        return _framebuffer;
    }

    /// <summary>
    /// Invalidate the composite skip cache and re-run DISPFB/local IMAGE → Soft-GS FB.
    /// Host present uses this when <see cref="PixelsWritten"/> &gt; 0 but the present buffer
    /// is still mostly black (stale skip after wrong FBP, or IMAGE under DISPFB not yet merged).
    /// Zeros <see cref="DispfbPixelsComposited"/> so the mergeMode early-return cannot re-skip.
    /// </summary>
    public long ForceRefreshPresentComposite()
    {
        DispfbPixelsComposited = 0;
        _lastCompositeImageBytes = -1;
        _lastCompositeCircuitGen = -1;
        _mergeBlackBypassArmed = false;
        if (!_localMemHasImage && ImageBytesWritten <= 0) return 0;
        // If IMAGE bytes landed but flag was cleared, re-arm so composite may run.
        if (ImageBytesWritten > 0)
            _localMemHasImage = true;
        long written = CompositeDispfbToFramebuffer();
        // One forced re-merge is enough when local RGB under DISPFB is still empty —
        // avoid GetPresentSpan thrashing a full local scan every host tick.
        if (IsPresentMostlyBlack())
            _mergeBlackBypassArmed = true;
        return written;
    }

    /// <summary>Alias for <see cref="ForceRefreshPresentComposite"/> (older call sites).</summary>
    public long ForcePresentComposite() => ForceRefreshPresentComposite();

    /// <summary>
    /// Count Soft-GS present pixels with non-zero RGB (A ignored). Used by Desktop HUD / black-screen diagnostics.
    /// </summary>
    public int CountLitPresentPixels(int step = 1)
    {
        if (step < 1) step = 1;
        int lit = 0;
        for (int i = 0; i < _framebuffer.Length; i += step)
        {
            if ((_framebuffer[i] & 0x00FFFFFFu) != 0)
                lit++;
        }
        return lit;
    }

    /// <summary>
    /// True when Soft-GS FB has fewer than ~1% lit samples (stride sample).
    /// Used to decide whether merge-composite cache is safe to keep.
    /// </summary>
    public bool IsPresentMostlyBlack(int stride = 16)
    {
        if (stride < 1) stride = 1;
        int lit = 0;
        int slots = 0;
        for (int i = 0; i < _framebuffer.Length; i += stride)
        {
            slots++;
            if ((_framebuffer[i] & 0x00FFFFFFu) != 0)
                lit++;
        }
        // &lt;1% of sampled slots lit → mostly black (commercial logo often missing).
        return lit * 100 < slots;
    }

    private long _lastCompositeImageBytes;
    private int _lastCompositeCircuitGen = -1;
    /// <summary>One-shot arm so black-FB cache bypass does not thrash every present when composite writes 0.</summary>
    private bool _mergeBlackBypassArmed;

    /// <summary>GX-040: snapshot of privileged DISPFB/DISPLAY/PMODE circuit state.</summary>
    public GsDisplayCircuitInfo GetDisplayCircuitInfo() =>
        GsDisplayCircuitInfo.FromRegisters(Registers);

    /// <summary>
    /// Copy DISPFB1/2 (else FRAME_1, else FBP=0 IMAGE) local VRAM into the software present FB.
    /// When raster <see cref="PixelsWritten"/> is already &gt;0, only fills black Soft-GS
    /// pixels (merge) so sparse AFAIL prims no longer block commercial logo IMAGE chrome.
    /// GX-040/041: prefers software-programmed DISPFB (even when PMODE EN=0); does not plant DISPFB.
    /// FRAME/FBP0 fallback remains for residual composite-only titles (B3 DISPFB1=0 residual).
    /// When natural DISPFB merge writes 0 but IMAGE exists and Soft-GS is still mostly black
    /// (GoW-class empty RGB under programmed FBP), also tries FRAME then FBP0 residual.
    /// </summary>
    public long CompositeDispfbToFramebuffer()
    {
        // IMAGE bytes without the flag (legacy linear upload races) still deserve a scan.
        if (!_localMemHasImage)
        {
            if (ImageBytesWritten <= 0) return 0;
            _localMemHasImage = true;
        }

        bool mergeMode = PixelsWritten > 0;
        // Avoid re-scanning every host-present once a full merge already ran and IMAGE
        // has not grown (1M-slice OnHostPresent). Invalidate when DISPFB/DISPLAY/PMODE
        // generation advances so natural DISPFB programmed after residual still binds (GX-041).
        // Keep Soft-GS claim determinism: skip only when present already has chrome.
        // When px>0 but FB is mostly black and local IMAGE exists, ignore merge cache once
        // (commercial logos live in IMAGE; Clear/wrong-FBP can stamp cache while FB is black).
        if (mergeMode && DispfbPixelsComposited > 0
            && ImageBytesWritten <= _lastCompositeImageBytes
            && DisplayCircuitGeneration == _lastCompositeCircuitGen)
        {
            bool hasImage = _localMemHasImage || ImageBytesWritten > 0;
            if (hasImage && IsPresentMostlyBlack())
            {
                if (_mergeBlackBypassArmed)
                    return 0;
                _mergeBlackBypassArmed = true;
                // Fall through — re-merge into black Soft-GS pixels once.
            }
            else
            {
                // Present has chrome — re-arm black bypass for a future FB wipe.
                _mergeBlackBypassArmed = false;
                return 0;
            }
        }

        var circuit = GetDisplayCircuitInfo();
        LastDisplayCircuit = circuit.PreferredCircuit;

        bool fromDispfb = false;
        ulong fb = 0;
        bool natural = false;
        GsCompositeSource source = GsCompositeSource.None;

        // Natural path (GX-041): any software-written DISPFB raw — PMODE EN optional.
        if (circuit.HasNaturalDispfb)
        {
            fb = circuit.PreferredDispfbRaw;
            // Prefer non-zero raw even if PreferredCircuit collapsed oddly.
            if (fb == 0)
                fb = Registers.DISPFB1 != 0 ? Registers.DISPFB1 : Registers.DISPFB2;
            fromDispfb = true;
            natural = true;
            source = GsCompositeSource.NaturalDispfb;
            if (LastDisplayCircuit == 0)
                LastDisplayCircuit = Registers.DISPFB1 != 0 ? 1 : 2;
        }
        else
        {
            fb = Registers.FRAME_1;
            source = GsCompositeSource.Frame;
        }

        bool syntheticFb = false;
        if (fb == 0)
        {
            if (ImageBytesWritten <= 0 && !_localMemHasImage) return 0;
            fromDispfb = false;
            natural = false;
            syntheticFb = true;
            source = GsCompositeSource.SyntheticFbp0;
        }

        // When DISPLAY is programmed with a sensible rect, limit composite size (natural CRT).
        // Use preferred circuit's DISPLAY (not DISPFB1-only) so dual-circuit EN2 binds correctly.
        DisplayRect? outRect = null;
        if (fromDispfb)
        {
            DisplayDecoded disp = circuit.PreferredCircuit == 2
                ? circuit.Display2
                : (circuit.PreferredCircuit == 1
                    ? circuit.Display1
                    : (Registers.DISPFB1 != 0 ? circuit.Display1 : circuit.Display2));
            // If preferred DISPLAY is empty but the sibling has a rect, fall back.
            var r = disp.GetOutputRect();
            if (!r.IsSensible)
            {
                var alt = circuit.Display1.GetOutputRect().IsSensible
                    ? circuit.Display1.GetOutputRect()
                    : circuit.Display2.GetOutputRect();
                if (alt.IsSensible) r = alt;
            }
            if (r.IsSensible)
                outRect = r;
        }

        long written = CompositeLocalToFb(fb, fromDispfb, syntheticFb, mergeMode, outRect);
        long residualExtra = 0;

        // When DISPFB unset and FRAME is a high FBP (draw target), also try FBP=0 IMAGE
        // page — commercial logo BITBLT often lands at page 0 while FRAME holds sparse UI.
        // This is residual (not natural) — B3-class DISPFB1=0 path.
        if (!fromDispfb && !syntheticFb && ImageBytesWritten > 0
            && (Registers.FRAME_1 & 0x1FF) != 0)
        {
            residualExtra = CompositeLocalToFb(0, fromDispfb: false, syntheticFb: true, mergeMode: true, outRect: null);
            written += residualExtra;
            if (residualExtra > 0 && written == residualExtra)
                source = GsCompositeSource.SyntheticFbp0;
        }

        // GX-041 residual (GoW-class): natural DISPFB is programmed but local RGB under that
        // FBP is empty (IMAGE BITBLT lives at FBP0 / FRAME / high-page PSMT4). When the natural
        // merge writes 0, Soft-GS is still mostly black, and IMAGE exists — try FRAME, FBP0,
        // then largest Host→Local BITBLT dest (PSMT4 texture page residual).
        // Honest residual metrics only (no DISPFB plant, no invent PATH3).
        if (fromDispfb && written == 0 && ImageBytesWritten > 0 && IsPresentMostlyBlack())
        {
            natural = false;
            ulong frame = Registers.FRAME_1;
            if (frame != 0)
            {
                residualExtra = CompositeLocalToFb(frame, fromDispfb: false, syntheticFb: false,
                    mergeMode: true, outRect: null);
                if (residualExtra > 0)
                {
                    written += residualExtra;
                    source = GsCompositeSource.Frame;
                }
            }
            // FBP=0 IMAGE page — logo BITBLT often lands here while DISPFB points at an empty
            // high-page draw target (GoW: DISPFB FBP=0x1A0000 PSMCT24 empty, imgBytes>0).
            if (written == 0 || IsPresentMostlyBlack())
            {
                long fbp0Extra = CompositeLocalToFb(0, fromDispfb: false, syntheticFb: true,
                    mergeMode: true, outRect: null);
                if (fbp0Extra > 0)
                {
                    written += fbp0Extra;
                    residualExtra += fbp0Extra;
                    if (source != GsCompositeSource.Frame)
                        source = GsCompositeSource.SyntheticFbp0;
                }
            }
            // GoW / PSMT4 residual: Host→Local lands at high DBP (e.g. 0xA0800 DPSM=0x14)
            // while DISPFB/FRAME/FBP0 have no RGB. Sample the real transfer with proper PSM.
            if ((written == 0 || IsPresentMostlyBlack()) && _lastImageByteCount > 0)
            {
                long imgExtra = CompositeLastImageTransfer(mergeMode: true);
                if (imgExtra > 0)
                {
                    written += imgExtra;
                    residualExtra += imgExtra;
                    source = GsCompositeSource.LastImageTrx;
                }
            }
        }

        // GX-041b / Vexx-class: natural DISPFB FBP≠FRAME FBP and natural only lands sparse
        // chrome (lit≈2% while prim raster painted a different FBP page). GoW residual above
        // requires written==0; sparse non-black natural must still pull FRAME into present.
        // Merge only (never overwrite existing chrome). No DISPFB plant / invent PATH3.
        if (fromDispfb && written > 0 && Registers.FRAME_1 != 0)
        {
            int dispfbFbp = (int)(DispfbDecoded.From(fb).Fbp);
            int frameFbp = (int)(Registers.FRAME_1 & 0x1FF);
            // Sparse: natural wrote less than ~4% of present FB, or lit samples still thin.
            bool sparseNatural = written < (FB_WIDTH * FB_HEIGHT) / 25
                || CountLitPresentPixels(8) < (FB_WIDTH * FB_HEIGHT) / 20;
            if (frameFbp != 0 && frameFbp != dispfbFbp && sparseNatural
                && (PixelsWritten > written || ImageBytesWritten > 0))
            {
                long frameExtra = CompositeLocalToFb(Registers.FRAME_1, fromDispfb: false,
                    syntheticFb: false, mergeMode: true, outRect: null);
                if (frameExtra > 0)
                {
                    written += frameExtra;
                    residualExtra += frameExtra;
                    natural = false; // present now includes residual FRAME pages
                    source = GsCompositeSource.Frame;
                }
            }
        }

        // Also when DISPFB was unset: after FRAME/FBP0 residual still black, try last IMAGE.
        if (!fromDispfb && (written == 0 || IsPresentMostlyBlack())
            && ImageBytesWritten > 0 && _lastImageByteCount > 0)
        {
            long imgExtra = CompositeLastImageTransfer(mergeMode: true);
            if (imgExtra > 0)
            {
                written += imgExtra;
                residualExtra += imgExtra;
                natural = false;
                source = GsCompositeSource.LastImageTrx;
            }
        }

        // Always stamp circuit gen so a no-op scan after DISPFB bind does not thrash every present.
        _lastCompositeImageBytes = ImageBytesWritten;
        _lastCompositeCircuitGen = DisplayCircuitGeneration;

        if (written > 0)
        {
            PixelsWritten += written;
            PrimitivesDrawn++;
            DispfbPixelsComposited += written;
            if (natural)
                NaturalDispfbPixels += written;
            else
                ResidualDispfbPixels += written;
            LastCompositeSource = source;
        }
        return written;
    }

    /// <summary>Inner local-mem → Soft-GS FB copy used by <see cref="CompositeDispfbToFramebuffer"/>.</summary>
    private long CompositeLocalToFb(ulong fb, bool fromDispfb, bool syntheticFb, bool mergeMode, DisplayRect? outRect)
    {
        int fbp;
        int fbw;
        int psm;
        int dbx = 0, dby = 0;
        if (syntheticFb)
        {
            fbp = 0;
            fbw = FB_WIDTH;
            psm = 0x00;
        }
        else if (fromDispfb)
        {
            // GX-040: same bit layout as DispfbDecoded / Play! DISPFB.
            var d = DispfbDecoded.From(fb);
            fbp = d.Fbp;
            fbw = d.BufWidthPixels;
            psm = d.Psm;
            dbx = d.Dbx;
            dby = d.Dby;
        }
        else
        {
            fbp = (int)(fb & 0x1FF);
            fbw = (int)((fb >> 16) & 0x3F) * 64;
            psm = (int)((fb >> 24) & 0x3F);
        }
        if (fbw <= 0) fbw = FB_WIDTH;
        if (fbw > 4096) fbw = 4096;
        // FRAME/DISPFB FBP: units of 2048 words → 8192 bytes (GS page / PCSX2 Block()).
        uint baseBytes = (uint)fbp * 8192u;
        if (baseBytes >= (uint)_localMem.Length) return 0;
        // Include indexed PSMs for residual LastImageTrx composite (GoW PSMT4).
        if (psm is not (0x00 or 0x01 or 0x02 or 0x0A or 0x13 or 0x14 or 0x1B))
            psm = 0x00;

        long written = 0;
        // Host Soft-GS FB is a fixed 640×448 present buffer. DISPLAY Width/Height clamp the
        // *source* window size, but CRT DX/DY blanking offsets must NOT become Soft-GS dest
        // offsets — they were clipping commercial logos to a thin strip (Dec/Vexx out+160,50).
        int h = FB_HEIGHT;
        int w = Math.Min(FB_WIDTH, fbw);
        const int dstOx = 0;
        const int dstOy = 0;
        if (outRect is { } rect && rect.IsSensible)
        {
            w = Math.Min(w, (int)rect.Width);
            h = Math.Min(h, (int)rect.Height);
            w = Math.Min(w, FB_WIDTH);
            h = Math.Min(h, FB_HEIGHT);
        }
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sx = dbx + x;
                int sy = dby + y;
                uint pixel = LoadLocalPixelForPresent(baseBytes, sx, sy, fbw, psm);
                if ((pixel & 0x00FFFFFF) == 0) continue;
                int dx = dstOx + x;
                int dy = dstOy + y;
                if ((uint)dx >= (uint)FB_WIDTH || (uint)dy >= (uint)FB_HEIGHT) continue;
                int idx = dy * FB_WIDTH + dx;
                // Merge: never overwrite prim/AFAIL chrome already on Soft-GS FB.
                // Pure black (0xFF000000) is *not* chrome — commercial clears stamp full FB
                // black (BO2 px=286720 lit=0); allow IMAGE under DISPFB to replace it.
                if (mergeMode && (_framebuffer[idx] & 0x00FFFFFF) != 0)
                    continue;
                _framebuffer[idx] = pixel | 0xFF000000u;
                written++;
            }
        }
        return written;
    }

    /// <summary>
    /// Residual present: composite the largest Host→Local IMAGE window into Soft-GS FB.
    /// Used when DISPFB/FRAME/FBP0 have no RGB but a real BITBLT filled a texture page
    /// (GoW: PSMT4 @ 0xA0800). Reads real local mem; CLUT when loaded else grayscale indices
    /// (same as <see cref="SampleTexel"/> without inventing PATH3 packets).
    /// </summary>
    private long CompositeLastImageTransfer(bool mergeMode)
    {
        if (_lastImageByteCount <= 0 || _lastImageW <= 0 || _lastImageH <= 0)
            return 0;
        if (_lastImageDbpBytes >= (uint)_localMem.Length)
            return 0;

        int psm = _lastImageDpsm;
        if (psm is not (0x00 or 0x01 or 0x02 or 0x0A or 0x13 or 0x14 or 0x1B))
            psm = 0x00;
        int fbw = _lastImageDbwPx > 0 ? _lastImageDbwPx : 64;
        int srcW = Math.Min(_lastImageW, fbw);
        int srcH = _lastImageH;
        // Scale-to-fit Soft-GS title FB when transfer is a full-ish width strip/tex.
        int dstW = Math.Min(FB_WIDTH, srcW > 0 ? srcW : FB_WIDTH);
        int dstH = Math.Min(FB_HEIGHT, srcH > 0 ? srcH : FB_HEIGHT);
        // Full-width commercial textures: stretch height into title FB when source is short band.
        bool stretchH = srcW >= FB_WIDTH / 2 && srcH >= 32 && srcH < FB_HEIGHT / 2;
        if (stretchH)
            dstH = FB_HEIGHT;
        // Tall/wide enough tex: map 1:1 into top-left of Soft-GS (clamp).
        if (srcW >= FB_WIDTH / 2 && srcH >= FB_HEIGHT / 2)
        {
            dstW = Math.Min(FB_WIDTH, srcW);
            dstH = Math.Min(FB_HEIGHT, srcH);
        }

        long written = 0;
        for (int y = 0; y < dstH; y++)
        {
            int sy = _lastImageDsaY + (dstH <= 1 ? 0 : y * srcH / dstH);
            if (sy >= _lastImageDsaY + srcH) sy = _lastImageDsaY + srcH - 1;
            for (int x = 0; x < dstW; x++)
            {
                int sx = _lastImageDsaX + (dstW <= 1 ? 0 : x * srcW / dstW);
                if (sx >= _lastImageDsaX + srcW) sx = _lastImageDsaX + srcW - 1;
                uint pixel = LoadLocalPixelForPresent(_lastImageDbpBytes, sx, sy, fbw, psm);
                if ((pixel & 0x00FFFFFF) == 0) continue;
                if ((uint)x >= (uint)FB_WIDTH || (uint)y >= (uint)FB_HEIGHT) continue;
                int idx = y * FB_WIDTH + x;
                if (mergeMode && (_framebuffer[idx] & 0x00FFFFFF) != 0)
                    continue;
                _framebuffer[idx] = pixel | 0xFF000000u;
                written++;
            }
        }
        return written;
    }

    /// <summary>
    /// Read one local-VRAM pixel as Soft-GS 0xAARRGGBB for DISPFB present.
    /// PSMCT32/24 use SwizzleOffset32; PSMCT16 uses SwizzleOffset16; PSMCT16S uses
    /// SwizzleOffset16S (distinct block table — Dec-class DISPFB PSM=0x0A).
    /// PSMT4/PSMT8 use CLUT when loaded, else grayscale indices (SampleTexel parity).
    /// </summary>
    private uint LoadLocalPixelForPresent(uint baseBytes, int sx, int sy, int fbw, int psm)
    {
        if (psm == 0x14) // PSMT4 — linear nibble pack (Host→Local residual layout)
        {
            int bi = (int)baseBytes + (sy * fbw + sx) / 2;
            if ((uint)bi >= (uint)_localMem.Length) return 0;
            byte packed = _localMem[bi];
            int nibble = ((sx + sy * fbw) & 1) == 0 ? (packed & 0xF) : (packed >> 4);
            if (_hasClut)
                return _clut[nibble & 0xF] | 0xFF000000u;
            // Grayscale from real index (not planted logo) — matches SampleTexel no-CLUT path.
            if (nibble == 0) return 0;
            uint g = (uint)(nibble * 17);
            return 0xFF000000u | (g << 16) | (g << 8) | g;
        }
        if (psm is 0x13 or 0x1B) // PSMT8 / PSMT8H
        {
            int bi = (int)SwizzleOffset8(baseBytes, sx, sy, fbw);
            if ((uint)bi >= (uint)_localMem.Length) return 0;
            byte idx8 = _localMem[bi];
            if (_hasClut)
                return _clut[idx8] | 0xFF000000u;
            if (idx8 == 0) return 0;
            return 0xFF000000u | ((uint)idx8 << 16) | ((uint)idx8 << 8) | idx8;
        }
        if (psm is 0x02 or 0x0A)
        {
            int bi = psm == 0x0A
                ? (int)SwizzleOffset16S(baseBytes, sx, sy, fbw)
                : (int)SwizzleOffset16(baseBytes, sx, sy, fbw);
            if ((uint)bi + 1u >= (uint)_localMem.Length) return 0;
            ushort p16 = (ushort)(_localMem[bi] | ((uint)_localMem[bi + 1] << 8));
            return ExpandRgb555(p16) | 0xFF000000u;
        }
        if (psm == 0x01)
        {
            int bi = (int)SwizzleOffset32(baseBytes, sx, sy, fbw);
            if ((uint)bi + 2u >= (uint)_localMem.Length) return 0;
            // GS PSMCT24: low 24 bits B,G,R in byte order (same as CT32 RGB lanes).
            uint b = _localMem[bi];
            uint g = _localMem[bi + 1];
            uint r = _localMem[bi + 2];
            return 0xFF000000u | (r << 16) | (g << 8) | b;
        }
        // PSMCT32 / default — LE B,G,R,A → Soft-GS 0xAARRGGBB
        {
            int bi = (int)SwizzleOffset32(baseBytes, sx, sy, fbw);
            if ((uint)bi + 3u >= (uint)_localMem.Length) return 0;
            return (uint)_localMem[bi]
                   | ((uint)_localMem[bi + 1] << 8)
                   | ((uint)_localMem[bi + 2] << 16)
                   | ((uint)_localMem[bi + 3] << 24);
        }
    }

    public bool HostOverlayActive => _hostOverlayActive;

    /// <summary>
    /// Legacy API: store overlay bytes but do <b>not</b> affect present or <see cref="PixelsWritten"/>.
    /// Boot FMV must not use this (host FFmpeg path removed). IRX-era Soft-GS present ignores overlay.
    /// </summary>
    public void SetHostOverlay(ReadOnlySpan<uint> argb, bool active = true)
    {
        if (!active || argb.Length < FB_WIDTH * FB_HEIGHT)
        {
            _hostOverlayActive = false;
            return;
        }
        if (_hostOverlay == null || _hostOverlay.Length != FB_WIDTH * FB_HEIGHT)
            _hostOverlay = new uint[FB_WIDTH * FB_HEIGHT];
        argb.Slice(0, FB_WIDTH * FB_HEIGHT).CopyTo(_hostOverlay);
        // Accept the call for ABI stability with MidwayBootAssist dead paths, but never mark
        // active for present/metrics — Soft-GS FB is the only truth (SOFTGS_IRX_ERA).
        _hostOverlayActive = false;
    }

    public void ClearHostOverlay()
    {
        _hostOverlayActive = false;
    }

    public int FramebufferWidth => FB_WIDTH;
    public int FramebufferHeight => FB_HEIGHT;
    public uint GetPixel(int x, int y) =>
        (x < 0 || y < 0 || x >= FB_WIDTH || y >= FB_HEIGHT) ? 0 : _framebuffer[y * FB_WIDTH + x];

    /// <summary>Bulk clear using Span fill (hot path).</summary>
    public void ClearFast(uint color)
    {
        _framebuffer.AsSpan().Fill(color);
        _depthBuffer.AsSpan().Fill(float.MaxValue);
        // FB wipe must not leave merge-composite cache thinking chrome is still present.
        InvalidatePresentCompositeCache();
    }

    /// <summary>
    /// Host-side blit of ARGB8888 pixels into the software framebuffer only.
    /// Does <b>not</b> install a host overlay (boot FMV must not re-enter via this path).
    /// </summary>
    public void BlitArgb8888(ReadOnlySpan<uint> argb, int width, int height)
    {
        int w = Math.Min(width, FB_WIDTH);
        int h = Math.Min(height, FB_HEIGHT);
        for (int y = 0; y < h; y++)
        {
            int srcRow = y * width;
            int dstRow = y * FB_WIDTH;
            for (int x = 0; x < w; x++)
            {
                if (srcRow + x >= argb.Length) break;
                // Force opaque A so host present (Avalonia Bgra8888 Opaque) never treats RGB as transparent black.
                _framebuffer[dstRow + x] = argb[srcRow + x] | 0xFF000000u;
                PixelsWritten++;
            }
        }
        PrimitivesDrawn++;
    }

    public void Clear(uint color, float depth = float.MaxValue)
    {
        for (int i = 0; i < _framebuffer.Length; i++)
        {
            _framebuffer[i] = color;
            _depthBuffer[i] = depth;
        }
        // FB wipe must not leave merge-composite cache thinking chrome is still present.
        InvalidatePresentCompositeCache();
    }

    /// <summary>Drop merge skip so the next composite re-scans local IMAGE → Soft-GS FB.</summary>
    private void InvalidatePresentCompositeCache()
    {
        DispfbPixelsComposited = 0;
        _lastCompositeImageBytes = -1;
        _lastCompositeCircuitGen = -1;
        _mergeBlackBypassArmed = false;
    }

    public int Step(ulong maxCycles)
    {
        int cost = CalculateWorkCost(1, 1);
        return Math.Min(cost, (int)Math.Max(1, (long)maxCycles));
    }

    public int CalculateWorkCost(uint qwc, uint nreg = 1)
    {
        const int BaseGsOverhead = 2;
        const int CostPerQwc = 5;
        const int CostPerRegister = 3;
        return Math.Max(1, BaseGsOverhead + (int)qwc * CostPerQwc + (int)nreg * CostPerRegister);
    }

    // -------- Privileged GS path (0x1200_0000) — CSR/IMR/PMODE/DISPFB --------
    private ulong _gsCsr = 0x55; // idle + revision-ish
    private ulong _gsImr = 0xFF00;
    private ulong _busdir;

    /// <summary>Privileged 32-bit view (EE often hits low half of 64-bit regs).</summary>
    public uint ReadPrivileged32(uint address)
    {
        ulong v = ReadPrivileged64(address & ~7u);
        return ((address & 4) != 0) ? (uint)(v >> 32) : (uint)v;
    }

    public void WritePrivileged32(uint address, uint value)
    {
        uint baseAddr = address & ~7u;
        ulong cur = ReadPrivileged64(baseAddr);
        if ((address & 4) != 0)
            cur = (cur & 0xFFFFFFFFUL) | ((ulong)value << 32);
        else
            cur = (cur & 0xFFFFFFFF00000000UL) | value;
        WritePrivileged64(baseAddr, cur);
    }

    public ulong ReadPrivileged64(uint address)
    {
        return (address & 0xFFFF) switch
        {
            0x0000 => Registers.PMODE,
            0x0020 => Registers.SMODE2,
            0x0070 => Registers.DISPFB1,
            0x0080 => Registers.DISPLAY1,
            0x0090 => Registers.DISPFB2,
            0x00A0 => Registers.DISPLAY2,
            0x1000 => _gsCsr | (1UL << 13), // FIFO empty-ish / idle
            0x1010 => _gsImr,
            0x1040 => _busdir,
            _ => 0
        };
    }

    public void WritePrivileged64(uint address, ulong value)
    {
        switch (address & 0xFFFF)
        {
            case 0x0000: SetPrivilegedDisplay(0x0000, value); break;
            case 0x0020: SetPrivilegedDisplay(0x0020, value); break;
            case 0x0070: SetPrivilegedDisplay(0x0070, value); break;
            case 0x0080: SetPrivilegedDisplay(0x0080, value); break;
            case 0x0090: SetPrivilegedDisplay(0x0090, value); break;
            case 0x00A0: SetPrivilegedDisplay(0x00A0, value); break;
            case 0x1000: // GS_CSR — w1c on some bits; RESET when bit 9?
                if ((value & (1UL << 9)) != 0)
                {
                    // soft reset drawing state but keep FB
                    _verts.Clear();
                }
                // FINISH / SIGNAL / VSINT clear when written 1
                _gsCsr &= ~(value & 0xF);
                _gsCsr |= 0x8; // idle
                break;
            case 0x1010: _gsImr = value; break;
            case 0x1040: _busdir = value; break;
        }
    }

    private void SetPrivilegedDisplay(uint which, ulong value)
    {
        // Write through to GsRegisters display fields used by present helpers.
        // Bump circuit generation so CompositeDispfbToFramebuffer rebinds after DISPFB/DISPLAY (GX-041).
        switch (which)
        {
            case 0x0000: Registers.SetPmode(value); break;
            case 0x0020: Registers.SetSmode2(value); break;
            case 0x0070: Registers.SetDispfb1(value); break;
            case 0x0080: Registers.SetDisplay1(value); break;
            case 0x0090: Registers.SetDispfb2(value); break;
            case 0x00A0: Registers.SetDisplay2(value); break;
            default: return;
        }
        DisplayCircuitGeneration++;
    }
}
