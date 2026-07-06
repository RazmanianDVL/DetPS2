using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Graphics Synthesizer Register File.
/// 
/// Design goals for DetPS2Sharp:
/// - Explicit fields for the most commonly used registers (clarity + easy SaveState)
/// - Indexed access for generic A+D packet processing from GIF
/// - Strong determinism: no hidden state, integer-only where possible
/// - Easy to extend as we implement more of the GS pipeline
/// 
/// References: GS User's Manual chapters on registers and packet formats.
/// </summary>
public sealed class GsRegisters
{
    // ============================================
    // Commonly used drawing registers (explicit for clarity)
    // ============================================

    public uint PRIM     { get; private set; }   // 0x00 - Primitive type and settings
    public uint RGBAQ    { get; private set; }   // 0x01 - Vertex color + Q
    public uint ST       { get; private set; }   // 0x02 - Texture ST coordinates
    public uint UV       { get; private set; }   // 0x03 - Texture UV coordinates
    public uint XYZ2     { get; private set; }   // 0x04 - Vertex XYZ (draw)
    public uint XYZ3     { get; private set; }   // 0x05 - Vertex XYZ (don't draw)
    public uint FOG      { get; private set; }   // 0x0A - Fog value

    // Texture registers
    public uint TEX0_1   { get; private set; }   // 0x06
    public uint TEX0_2   { get; private set; }   // 0x07
    public uint CLAMP_1  { get; private set; }   // 0x08
    public uint CLAMP_2  { get; private set; }   // 0x09

    // Framebuffer / Depth / Scissor (context 1 for now)
    public uint FRAME_1  { get; private set; }   // 0x4C
    public uint ZBUF_1   { get; private set; }   // 0x4D
    public uint XYOFFSET_1 { get; private set; } // 0x4E
    public uint SCISSOR_1  { get; private set; } // 0x50
    public uint TEST_1     { get; private set; } // 0x52
    public uint ALPHA_1    { get; private set; } // 0x53
    public uint FBA_1      { get; private set; } // 0x54
    public uint FRAME_2  { get; private set; }
    public uint ZBUF_2   { get; private set; }

    // Display / PCRTC related (subset)
    public uint PMODE    { get; private set; }   // 0x00 in PCRTC space, but we keep it here for now
    public uint SMODE2   { get; private set; }

    // ============================================
    // Internal storage for generic register access
    // ============================================

    private readonly Dictionary<uint, uint> _registers = new();

    public GsRegisters()
    {
        Reset();
    }

    public void Reset()
    {
        PRIM = 0;
        RGBAQ = 0xFFFFFFFF;
        ST = 0;
        UV = 0;
        XYZ2 = 0;
        XYZ3 = 0;
        FOG = 0;

        TEX0_1 = 0;
        TEX0_2 = 0;
        CLAMP_1 = 0;
        CLAMP_2 = 0;

        FRAME_1 = 0;
        ZBUF_1 = 0;
        XYOFFSET_1 = 0;
        SCISSOR_1 = 0;
        TEST_1 = 0;
        ALPHA_1 = 0;
        FBA_1 = 0;
        FRAME_2 = 0;
        ZBUF_2 = 0;

        PMODE = 0;
        SMODE2 = 0;

        _registers.Clear();
    }

    /// <summary>
    /// Generic register write used by A+D packet processing.
    /// Updates both the named field (when known) and the raw dictionary.
    /// </summary>
    public void WriteRegister(uint address, uint value)
    {
        _registers[address] = value;

        switch (address)
        {
            case 0x00: PRIM = value; break;
            case 0x01: RGBAQ = value; break;
            case 0x02: ST = value; break;
            case 0x03: UV = value; break;
            case 0x04: XYZ2 = value; break;
            case 0x05: XYZ3 = value; break;
            case 0x0A: FOG = value; break;

            case 0x06: TEX0_1 = value; break;
            case 0x07: TEX0_2 = value; break;
            case 0x08: CLAMP_1 = value; break;
            case 0x09: CLAMP_2 = value; break;

            case 0x4C: FRAME_1 = value; break;
            case 0x4D: ZBUF_1 = value; break;
            case 0x4E: XYOFFSET_1 = value; break;
            case 0x50: SCISSOR_1 = value; break;
            case 0x52: TEST_1 = value; break;
            case 0x53: ALPHA_1 = value; break;
            case 0x54: FBA_1 = value; break;
            case 0x5C: FRAME_2 = value; break;
            case 0x5D: ZBUF_2 = value; break;

            case 0x00: /* PMODE handled in PCRTC later */ break;
            case 0x01: SMODE2 = value; break;

            default:
                // Unknown or less common register - still stored for future use
                break;
        }
    }

    /// <summary>
    /// Generic read (used for save states and debugging).
    /// </summary>
    public uint ReadRegister(uint address)
    {
        if (_registers.TryGetValue(address, out uint value))
            return value;

        // Fallback to named fields for known registers
        return address switch
        {
            0x00 => PRIM,
            0x01 => RGBAQ,
            0x02 => ST,
            0x03 => UV,
            0x04 => XYZ2,
            0x05 => XYZ3,
            0x0A => FOG,
            0x06 => TEX0_1,
            0x07 => TEX0_2,
            0x08 => CLAMP_1,
            0x09 => CLAMP_2,
            0x4C => FRAME_1,
            0x4D => ZBUF_1,
            0x4E => XYOFFSET_1,
            0x50 => SCISSOR_1,
            0x52 => TEST_1,
            0x53 => ALPHA_1,
            0x54 => FBA_1,
            _ => 0
        };
    }

    /// <summary>
    /// Returns a snapshot for SaveState. Sorted for determinism.
    /// </summary>
    public IReadOnlyDictionary<uint, uint> GetAllRegisters()
    {
        var snapshot = new SortedDictionary<uint, uint>(_registers);

        // Ensure named registers are included even if never written via raw path
        snapshot.TryAdd(0x00, PRIM);
        snapshot.TryAdd(0x01, RGBAQ);
        // ... add more as needed

        return snapshot;
    }
}