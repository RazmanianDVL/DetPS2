using System;

namespace DetPS2.Core;

/// <summary>
/// GX-040: decode of GS privileged DISPFB1/2 + DISPLAY1/2 (+ PMODE circuit select).
/// Field layout matches Play! <c>CGSHandler::DISPFB</c> / <c>DISPLAY</c> / <c>GetDisplayRect</c>
/// (oracle: <c>C:\Windows\Play\Source\gs\GSHandler.h</c>).
/// Does <b>not</b> invent DISPFB plant values — only decodes what software wrote.
/// </summary>
public readonly struct DispfbDecoded
{
    /// <summary>Frame buffer base pointer in pages of 2048 words (bits 0–8).</summary>
    public int Fbp { get; init; }
    /// <summary>Buffer width / 64 (bits 9–14).</summary>
    public int FbwUnits { get; init; }
    /// <summary>Pixel storage mode (bits 15–19).</summary>
    public int Psm { get; init; }
    /// <summary>X read offset in buffer (bits 32–42).</summary>
    public int Dbx { get; init; }
    /// <summary>Y read offset in buffer (bits 43–53).</summary>
    public int Dby { get; init; }

    public bool IsZero => Fbp == 0 && FbwUnits == 0 && Psm == 0 && Dbx == 0 && Dby == 0;
    /// <summary>Byte address in local GS mem (FBP × 8192).</summary>
    public uint BufPtrBytes => (uint)Fbp * 8192u;
    /// <summary>Buffer width in pixels (FBW × 64).</summary>
    public int BufWidthPixels => FbwUnits * 64;

    public static DispfbDecoded From(ulong raw) => new()
    {
        Fbp = (int)(raw & 0x1FF),
        FbwUnits = (int)((raw >> 9) & 0x3F),
        Psm = (int)((raw >> 15) & 0x1F),
        Dbx = (int)((raw >> 32) & 0x7FF),
        Dby = (int)((raw >> 43) & 0x7FF),
    };

    public ulong Pack() =>
        ((ulong)(uint)(Fbp & 0x1FF))
        | ((ulong)(uint)(FbwUnits & 0x3F) << 9)
        | ((ulong)(uint)(Psm & 0x1F) << 15)
        | ((ulong)(uint)(Dbx & 0x7FF) << 32)
        | ((ulong)(uint)(Dby & 0x7FF) << 43);

    public override string ToString() =>
        $"FBP=0x{BufPtrBytes:X} FBW={BufWidthPixels} PSM={Psm} DBX={Dbx} DBY={Dby}";
}

/// <summary>DISPLAY1/2 output rectangle + magnification (Play! bit layout).</summary>
public readonly struct DisplayDecoded
{
    public int Dx { get; init; }
    public int Dy { get; init; }
    /// <summary>Horizontal magnification minus 1 (0 ⇒ 1×).</summary>
    public int MagH { get; init; }
    /// <summary>Vertical magnification minus 1 (0 ⇒ 1×).</summary>
    public int MagV { get; init; }
    /// <summary>Display width minus 1 (raw DW field).</summary>
    public int Dw { get; init; }
    /// <summary>Display height minus 1 (raw DH field).</summary>
    public int Dh { get; init; }

    public bool IsZero => Dx == 0 && Dy == 0 && MagH == 0 && MagV == 0 && Dw == 0 && Dh == 0;

    public static DisplayDecoded From(ulong raw) => new()
    {
        Dx = (int)(raw & 0xFFF),
        Dy = (int)((raw >> 12) & 0x7FF),
        MagH = (int)((raw >> 23) & 0xF),
        MagV = (int)((raw >> 27) & 0x3),
        Dw = (int)((raw >> 32) & 0xFFF),
        Dh = (int)((raw >> 44) & 0xFFF),
    };

    public ulong Pack() =>
        ((ulong)(uint)(Dx & 0xFFF))
        | ((ulong)(uint)(Dy & 0x7FF) << 12)
        | ((ulong)(uint)(MagH & 0xF) << 23)
        | ((ulong)(uint)(MagV & 0x3) << 27)
        | ((ulong)(uint)(Dw & 0xFFF) << 32)
        | ((ulong)(uint)(Dh & 0xFFF) << 44);

    /// <summary>
    /// Play! <c>GetDisplayRect</c>: pixel size after MAGH/MAGV, with soft height clamp (&gt;640 → /2).
    /// Interlace half-height is left to PCRTC (GX-042); this is progressive decode only.
    /// </summary>
    public DisplayRect GetOutputRect(bool halfHeightForInterlace = false)
    {
        int magX = MagH + 1;
        int magY = MagV + 1;
        if (magX <= 0) magX = 1;
        if (magY <= 0) magY = 1;
        uint offsetX = (uint)(Dx / magX);
        uint offsetY = (uint)(Dy / magY);
        uint width = (uint)((Dw + 1) / magX);
        uint height = (uint)((Dh + 1) / magY);
        if (height > 640)
            height /= 2;
        if (halfHeightForInterlace)
        {
            offsetY /= 2;
            height /= 2;
        }
        return new DisplayRect(offsetX, offsetY, width, height);
    }

    public override string ToString() =>
        $"DX={Dx} DY={Dy} MAGH={MagH} MAGV={MagV} DW={Dw} DH={Dh}";
}

/// <summary>Resolved CRT output rectangle in framebuffer pixels.</summary>
public readonly struct DisplayRect
{
    public uint OffsetX { get; }
    public uint OffsetY { get; }
    public uint Width { get; }
    public uint Height { get; }

    public DisplayRect(uint offsetX, uint offsetY, uint width, uint height)
    {
        OffsetX = offsetX;
        OffsetY = offsetY;
        Width = width;
        Height = height;
    }

    public bool IsSensible => Width > 1 && Height > 1 && Width <= 4096 && Height <= 2048;
}

/// <summary>
/// Snapshot of PMODE-selected read circuits + decoded DISPFB/DISPLAY for telemetry / present.
/// </summary>
public sealed class GsDisplayCircuitInfo
{
    public ulong Pmode { get; init; }
    public bool En1 => (Pmode & 1) != 0;
    public bool En2 => (Pmode & 2) != 0;

    public DispfbDecoded Dispfb1 { get; init; }
    public DispfbDecoded Dispfb2 { get; init; }
    public DisplayDecoded Display1 { get; init; }
    public DisplayDecoded Display2 { get; init; }

    /// <summary>0 = none, 1 = circuit1, 2 = circuit2, 3 = both enabled.</summary>
    public int CircuitMode => (int)(Pmode & 3);

    /// <summary>
    /// Preferred single circuit for Soft-GS present (Play! dual-circuit collapse rules simplified):
    /// EN1 only → 1; EN2 only → 2; both → pick non-zero DISPFB raw (prefer 1); none → 0.
    /// </summary>
    public int PreferredCircuit
    {
        get
        {
            int mode = CircuitMode;
            if (mode == 1) return 1;
            if (mode == 2) return 2;
            if (mode == 3)
            {
                // Play! Capcom dual-circuit: prefer the circuit whose DISPFB raw is non-zero.
                if (RegistersRawDispfb1 != 0 && RegistersRawDispfb2 == 0) return 1;
                if (RegistersRawDispfb1 == 0 && RegistersRawDispfb2 != 0) return 2;
                return 1; // both non-zero or both zero — report circuit 1
            }
            return 0;
        }
    }

    public ulong RegistersRawDispfb1 { get; init; }
    public ulong RegistersRawDispfb2 { get; init; }
    public ulong RegistersRawDisplay1 { get; init; }
    public ulong RegistersRawDisplay2 { get; init; }

    public DispfbDecoded PreferredDispfb => PreferredCircuit == 2 ? Dispfb2 : Dispfb1;
    public DisplayDecoded PreferredDisplay => PreferredCircuit == 2 ? Display2 : Display1;
    public ulong PreferredDispfbRaw => PreferredCircuit == 2 ? RegistersRawDispfb2 : RegistersRawDispfb1;
    public ulong PreferredDisplayRaw => PreferredCircuit == 2 ? RegistersRawDisplay2 : RegistersRawDisplay1;

    /// <summary>True when the preferred circuit has a non-zero DISPFB programmed by software.</summary>
    public bool HasNaturalDispfb => PreferredCircuit != 0 && PreferredDispfbRaw != 0;

    public static GsDisplayCircuitInfo FromRegisters(GsRegisters regs)
    {
        if (regs == null) throw new ArgumentNullException(nameof(regs));
        return new GsDisplayCircuitInfo
        {
            Pmode = regs.PMODE,
            Dispfb1 = DispfbDecoded.From(regs.DISPFB1),
            Dispfb2 = DispfbDecoded.From(regs.DISPFB2),
            Display1 = DisplayDecoded.From(regs.DISPLAY1),
            Display2 = DisplayDecoded.From(regs.DISPLAY2),
            RegistersRawDispfb1 = regs.DISPFB1,
            RegistersRawDispfb2 = regs.DISPFB2,
            RegistersRawDisplay1 = regs.DISPLAY1,
            RegistersRawDisplay2 = regs.DISPLAY2,
        };
    }

    public string SummaryLine()
    {
        var d = PreferredDispfb;
        var r = PreferredDisplay.GetOutputRect();
        return $"pmode=0x{Pmode:X} circ={PreferredCircuit} naturalDispfb={(HasNaturalDispfb ? 1 : 0)} " +
               $"dispfb1=0x{RegistersRawDispfb1:X} display1=0x{RegistersRawDisplay1:X} " +
               $"out={r.Width}x{r.Height}+{r.OffsetX},{r.OffsetY} {d}";
    }
}
