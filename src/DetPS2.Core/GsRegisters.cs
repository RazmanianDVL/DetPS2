using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Graphics Synthesizer register file (Phase 7).
/// 64-bit storage (native GS register width). Deterministic sorted snapshot.
/// </summary>
public sealed class GsRegisters
{
    public ulong PRIM { get; private set; }
    public ulong RGBAQ { get; private set; }
    public ulong ST { get; private set; }
    public ulong UV { get; private set; }
    /// <summary>GS 0x05 XYZ2 (drawing kick). Not 0x04 — that is XYZF2.</summary>
    public ulong XYZ2 { get; private set; }
    /// <summary>GS 0x0D XYZ3 (no kick).</summary>
    public ulong XYZ3 { get; private set; }
    /// <summary>GS 0x04 XYZF2 (kick + fog).</summary>
    public ulong XYZF2 { get; private set; }
    /// <summary>GS 0x0C XYZF3 (no kick + fog).</summary>
    public ulong XYZF3 { get; private set; }
    public ulong FOG { get; private set; }
    public ulong FOGCOL { get; private set; }
    public ulong PRMODECONT { get; private set; }
    public ulong PRMODE { get; private set; }

    public ulong TEX0_1 { get; private set; }
    public ulong TEX0_2 { get; private set; }
    public ulong TEX1_1 { get; private set; }
    public ulong TEX1_2 { get; private set; }
    public ulong TEX2_1 { get; private set; }
    public ulong TEX2_2 { get; private set; }
    public ulong CLAMP_1 { get; private set; }
    public ulong CLAMP_2 { get; private set; }
    public ulong TEXCLUT { get; private set; }

    public ulong FRAME_1 { get; private set; }
    public ulong FRAME_2 { get; private set; }
    public ulong ZBUF_1 { get; private set; }
    public ulong ZBUF_2 { get; private set; }
    public ulong XYOFFSET_1 { get; private set; }
    public ulong XYOFFSET_2 { get; private set; }
    public ulong SCISSOR_1 { get; private set; }
    public ulong SCISSOR_2 { get; private set; }
    public ulong TEST_1 { get; private set; }
    public ulong TEST_2 { get; private set; }
    public ulong ALPHA_1 { get; private set; }
    public ulong ALPHA_2 { get; private set; }
    public ulong FBA_1 { get; private set; }
    public ulong FBA_2 { get; private set; }
    public ulong DIMX { get; private set; }
    public ulong DTHE { get; private set; }
    public ulong COLCLAMP { get; private set; }
    public ulong PABE { get; private set; }

    public ulong PMODE { get; private set; }
    public ulong SMODE2 { get; private set; }
    public ulong DISPLAY1 { get; private set; }
    public ulong DISPLAY2 { get; private set; }
    public ulong DISPFB1 { get; private set; }
    public ulong DISPFB2 { get; private set; }

    // Host-to-local / local-to-local VRAM transfer registers (real addresses 0x50-0x54,
    // confirmed against multiple real ps2sdk-derived headers — see WriteRegister64 note).
    public ulong BITBLTBUF { get; private set; }
    public ulong TRXPOS { get; private set; }
    public ulong TRXREG { get; private set; }
    public ulong TRXDIR { get; private set; }
    public ulong HWREG { get; private set; }

    public void SetPmode(ulong v) => PMODE = v;
    public void SetSmode2(ulong v) => SMODE2 = v;
    public void SetDisplay1(ulong v) => DISPLAY1 = v;
    public void SetDisplay2(ulong v) => DISPLAY2 = v;
    public void SetDispfb1(ulong v) => DISPFB1 = v;
    public void SetDispfb2(ulong v) => DISPFB2 = v;

    private readonly SortedDictionary<uint, ulong> _regs = new();

    public GsRegisters() => Reset();

    public void Reset()
    {
        PRIM = 0;
        RGBAQ = 0xFFFFFFFFUL;
        ST = UV = XYZ2 = XYZ3 = XYZF2 = XYZF3 = FOG = FOGCOL = 0;
        PRMODECONT = 1;
        PRMODE = 0;

        TEX0_1 = TEX0_2 = TEX1_1 = TEX1_2 = TEX2_1 = TEX2_2 = 0;
        CLAMP_1 = CLAMP_2 = TEXCLUT = 0;

        FRAME_1 = FRAME_2 = ZBUF_1 = ZBUF_2 = 0;
        XYOFFSET_1 = XYOFFSET_2 = 0;
        SCISSOR_1 = PackScissor(0, 639, 0, 447);
        SCISSOR_2 = SCISSOR_1;
        TEST_1 = TEST_2 = 0;
        ALPHA_1 = ALPHA_2 = 0;
        FBA_1 = FBA_2 = 0;
        DIMX = DTHE = 0;
        COLCLAMP = 1;
        PABE = 0;

        PMODE = SMODE2 = DISPLAY1 = DISPLAY2 = DISPFB1 = DISPFB2 = 0;
        _regs.Clear();
    }

    /// <summary>GS register state for SaveState.cs. _regs already records every raw register
    /// write by address (WriteRegister64 populates both it and the named properties above), so
    /// replaying it through WriteRegister64 on load reconstructs every named property too —
    /// simpler and less error-prone than serializing ~35 individual properties by hand. Without
    /// this, a load resumed drawing with PRIM/TEX0/ALPHA/scissor/etc all back at Reset()
    /// defaults even though the game had configured real rendering state long before the save.</summary>
    public void WriteState(System.IO.BinaryWriter w)
    {
        w.Write(_regs.Count);
        foreach (var kv in _regs) { w.Write(kv.Key); w.Write(kv.Value); }
        w.Write(PMODE); w.Write(SMODE2); w.Write(DISPLAY1); w.Write(DISPLAY2); w.Write(DISPFB1); w.Write(DISPFB2);
    }

    public void ReadState(System.IO.BinaryReader r)
    {
        Reset();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            uint addr = r.ReadUInt32();
            ulong val = r.ReadUInt64();
            WriteRegister64(addr, val);
        }
        PMODE = r.ReadUInt64(); SMODE2 = r.ReadUInt64();
        DISPLAY1 = r.ReadUInt64(); DISPLAY2 = r.ReadUInt64();
        DISPFB1 = r.ReadUInt64(); DISPFB2 = r.ReadUInt64();
    }

    public static ulong PackScissor(int x0, int x1, int y0, int y1) =>
        ((ulong)(uint)(x0 & 0x7FF))
        | ((ulong)(uint)(x1 & 0x7FF) << 16)
        | ((ulong)(uint)(y0 & 0x7FF) << 32)
        | ((ulong)(uint)(y1 & 0x7FF) << 48);

    public void WriteRegister(uint address, uint value) => WriteRegister64(address, value);

    // Real GS register addresses (verified against multiple real ps2sdk-derived headers,
    // e.g. quake1_ps2/ps2_gs.h and the ps2dev.org GS privileged-register thread). An
    // earlier version of this map had a sweeping set of wrong IDs in the 0x18-0x54 range
    // — XYOFFSET/SCISSOR/ALPHA/TEST/FBA/ZBUF/FRAME_2/PRMODECONT/PRMODE/TEXCLUT were all
    // at the wrong addresses, and BITBLTBUF/TRXPOS/TRXREG/TRXDIR/HWREG (the real VRAM
    // transfer registers real games use to upload all texture/framebuffer data) weren't
    // mapped at all — 0x50-0x54 was occupied by SCISSOR/TEST/ALPHA/FBA instead. Any real
    // game configuring scissor clipping, alpha blending, depth test, or issuing a VRAM
    // transfer via its real, SDK-compiled register addresses would have silently hit the
    // wrong state (or nothing at all) under the old map.
    public void WriteRegister64(uint address, ulong value)
    {
        address &= 0x7F;
        _regs[address] = value;

        switch (address)
        {
            case 0x00: PRIM = value; break;
            case 0x01: RGBAQ = value; break;
            case 0x02: ST = value; break;
            case 0x03: UV = value; break;
            case 0x04: XYZF2 = value; break; // Sony: XYZF2 kick+fog
            case 0x05: XYZ2 = value; break;  // Sony: XYZ2 kick
            case 0x0C: XYZF3 = value; break; // Sony: XYZF3 no-kick+fog
            case 0x0D: XYZ3 = value; break;  // Sony: XYZ3 no-kick
            case 0x0A: FOG = value; break;
            case 0x3D: FOGCOL = value; break;
            case 0x1C: TEXCLUT = value; break;
            case 0x14: TEX1_1 = value; break;
            case 0x15: TEX1_2 = value; break;
            case 0x16: TEX2_1 = value; break;
            case 0x17: TEX2_2 = value; break;
            case 0x06: TEX0_1 = value; break;
            case 0x07: TEX0_2 = value; break;
            case 0x08: CLAMP_1 = value; break;
            case 0x09: CLAMP_2 = value; break;
            case 0x18: XYOFFSET_1 = value; break;
            case 0x19: XYOFFSET_2 = value; break;
            case 0x4C: FRAME_1 = value; break;
            case 0x4D: FRAME_2 = value; break;
            case 0x4E: ZBUF_1 = value; break;
            case 0x4F: ZBUF_2 = value; break;
            case 0x40: SCISSOR_1 = value; break;
            case 0x41: SCISSOR_2 = value; break;
            case 0x47: TEST_1 = value; break;
            case 0x48: TEST_2 = value; break;
            case 0x42: ALPHA_1 = value; break;
            case 0x43: ALPHA_2 = value; break;
            case 0x4A: FBA_1 = value; break;
            case 0x4B: FBA_2 = value; break;
            case 0x44: DIMX = value; break;
            case 0x45: DTHE = value; break;
            case 0x46: COLCLAMP = value; break;
            case 0x49: PABE = value; break;
            case 0x1A: PRMODECONT = value; break;
            case 0x1B: PRMODE = value; break;
            case 0x50: BITBLTBUF = value; break;
            case 0x51: TRXPOS = value; break;
            case 0x52: TRXREG = value; break;
            case 0x53: TRXDIR = value; break;
            case 0x54: HWREG = value; break;
        }
    }

    public ulong ReadRegister64(uint address)
    {
        address &= 0x7F;
        if (_regs.TryGetValue(address, out ulong v)) return v;
        return address switch
        {
            0x00 => PRIM,
            0x01 => RGBAQ,
            0x02 => ST,
            0x03 => UV,
            0x04 => XYZF2,
            0x05 => XYZ2,
            0x0C => XYZF3,
            0x0D => XYZ3,
            0x06 => TEX0_1,
            0x07 => TEX0_2,
            0x08 => CLAMP_1,
            0x09 => CLAMP_2,
            0x0A => FOG,
            0x18 => XYOFFSET_1,
            0x4C => FRAME_1,
            0x4D => FRAME_2,
            0x4E => ZBUF_1,
            0x40 => SCISSOR_1,
            0x47 => TEST_1,
            0x42 => ALPHA_1,
            0x4A => FBA_1,
            0x50 => BITBLTBUF,
            0x51 => TRXPOS,
            0x52 => TRXREG,
            0x53 => TRXDIR,
            0x54 => HWREG,
            _ => 0
        };
    }

    public uint ReadRegister(uint address) => (uint)ReadRegister64(address);

    public IReadOnlyDictionary<uint, ulong> GetAllRegisters()
    {
        var snap = new SortedDictionary<uint, ulong>(_regs);
        void Ensure(uint a, ulong v)
        {
            if (!snap.ContainsKey(a)) snap[a] = v;
        }
        Ensure(0x00, PRIM);
        Ensure(0x01, RGBAQ);
        Ensure(0x06, TEX0_1);
        Ensure(0x40, SCISSOR_1);
        Ensure(0x47, TEST_1);
        Ensure(0x42, ALPHA_1);
        return snap;
    }

    public int PrimType => (int)(PRIM & 0x7);
    public bool PrimIip => ((PRIM >> 3) & 1) != 0;
    public bool PrimTme => ((PRIM >> 4) & 1) != 0;
    public bool PrimFge => ((PRIM >> 5) & 1) != 0;
    public bool PrimAbe => ((PRIM >> 6) & 1) != 0;
    public bool PrimFst => ((PRIM >> 8) & 1) != 0;

    public void GetScissor(out int x0, out int x1, out int y0, out int y1)
    {
        ulong s = SCISSOR_1;
        x0 = (int)(s & 0x7FF);
        x1 = (int)((s >> 16) & 0x7FF);
        y0 = (int)((s >> 32) & 0x7FF);
        y1 = (int)((s >> 48) & 0x7FF);
        if (x0 == 0 && x1 == 0 && y0 == 0 && y1 == 0)
        {
            x0 = 0; x1 = 639; y0 = 0; y1 = 447;
            return;
        }
        // Normalize inverted ranges; expand a zeroed axis when the other is live
        // (staged SCISSOR writes would otherwise zero-width-clip all prims).
        if (x1 < x0) (x0, x1) = (x1, x0);
        if (y1 < y0) (y0, y1) = (y1, y0);
        if (x0 == 0 && x1 == 0 && y1 > y0) { x0 = 0; x1 = 639; }
        if (y0 == 0 && y1 == 0 && x1 > x0) { y0 = 0; y1 = 447; }
        if (x1 <= x0 && y1 <= y0)
        {
            x0 = 0; x1 = 639; y0 = 0; y1 = 447;
        }
    }

    public void GetXyOffset(out int ofx, out int ofy)
    {
        ofx = (int)(XYOFFSET_1 & 0xFFFF);
        ofy = (int)((XYOFFSET_1 >> 32) & 0xFFFF);
    }

    public int TexWidthLog2
    {
        get
        {
            int tw = (int)((TEX0_1 >> 26) & 0xF);
            return tw == 0 ? 6 : Math.Clamp(tw, 0, 10);
        }
    }

    public int TexHeightLog2
    {
        get
        {
            int th = (int)((TEX0_1 >> 30) & 0xF);
            return th == 0 ? 6 : Math.Clamp(th, 0, 10);
        }
    }

    public int TexWidth => 1 << TexWidthLog2;
    public int TexHeight => 1 << TexHeightLog2;
    public int TexPsm => (int)((TEX0_1 >> 20) & 0x3F);
    public uint TexBaseWords => (uint)(TEX0_1 & 0x3FFF);

    /// <summary>ZTE (bit 16). When set, depth testing is active.</summary>
    public bool DepthTestEnabled => ((TEST_1 >> 16) & 1) != 0;

    /// <summary>ZTST (bits 17-18): 0=NEVER 1=ALWAYS 2=GEQUAL 3=GREATER</summary>
    public int DepthTestMode => (int)((TEST_1 >> 17) & 0x3);

    /// <summary>ZMSK (bit 19): 0=write Z, 1=mask (no write)</summary>
    public bool DepthWriteEnabled => ((TEST_1 >> 19) & 1) == 0;

    public int AlphaA => (int)(ALPHA_1 & 0x3);
    public int AlphaB => (int)((ALPHA_1 >> 2) & 0x3);
    public int AlphaC => (int)((ALPHA_1 >> 4) & 0x3);
    public int AlphaD => (int)((ALPHA_1 >> 6) & 0x3);
    public int AlphaFix => (int)((ALPHA_1 >> 32) & 0xFF);

    public int ClampWms => (int)(CLAMP_1 & 0x3);
    public int ClampWmt => (int)((CLAMP_1 >> 2) & 0x3);
}
