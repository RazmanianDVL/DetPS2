using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// PCRTC — display + VBlank (Phase 7/14).
/// </summary>
public sealed class Pcrtc : ISchedulable
{
    private readonly Gs _gs;
    private Intc? _intc;
    private Action? _onVblank;
    private ulong _cyclesAccum;
    private ulong _vblankPeriod = 500_000;
    private bool _inVblank;
    private SystemMemory? _iopMem;

    public ulong FrameCount { get; private set; }
    public bool InVblank => _inVblank;

    /// <summary>GX-040: DISPFB/DISPLAY circuit as seen by present path.</summary>
    public GsDisplayCircuitInfo GetDisplayCircuitInfo() => _gs.GetDisplayCircuitInfo();
    public ulong VblankPeriod
    {
        get => _vblankPeriod;
        set => _vblankPeriod = Math.Max(1000UL, value);
    }
    public ulong VblankCount { get; private set; }

    public Pcrtc(Gs gs) => _gs = gs ?? throw new ArgumentNullException(nameof(gs));

    public void SetIntc(Intc intc) => _intc = intc;
    public void SetVblankCallback(Action? cb) => _onVblank = cb;

    /// <summary>Real IOP INTC needs a real hardware VBLANK source (bit 0 start, bit 11 end —
    /// ground-truthed against VBLANK.IRX's own real IRQ registration, see IopVblankHost's doc
    /// comment). Distinct from <see cref="_intc"/>, which is the EE's own separate INTC.</summary>
    public void AttachIopMemory(SystemMemory mem) => _iopMem = mem;

    public void Reset()
    {
        _cyclesAccum = 0;
        FrameCount = 0;
        VblankCount = 0;
        _inVblank = false;
    }

    public void Present(string filename = "detps2_frame.ppm")
    {
        // GX-041: bind natural DISPFB (or residual FRAME/FBP0) before dump.
        _gs.CompositeDispfbToFramebuffer();
        _gs.SaveFramebufferAsPPM(filename);
        FrameCount++;
    }

    /// <summary>
    /// GX-002 helper: write Soft-GS PPM only when raster px&gt;0 (claim-friendly dump).
    /// Returns false when nothing has been drawn (avoids empty black claim artifacts).
    /// </summary>
    public bool DumpSoftGsIfDrawn(string filename)
    {
        _gs.CompositeDispfbToFramebuffer();
        if (_gs.PixelsWritten <= 0) return false;
        string? dir = Path.GetDirectoryName(filename);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _gs.SaveFramebufferAsPPM(filename);
        FrameCount++;
        return true;
    }

    public void PresentFrame()
    {
        // Host/vblank present path: composite DISPFB circuit before raising VBlank (GX-041).
        _gs.CompositeDispfbToFramebuffer();
        FrameCount++;
        RaiseVblank();
    }

    private void RaiseVblank()
    {
        _inVblank = true;
        VblankCount++;
        _intc?.Raise(Intc.InterruptSource.VBlankStart);
        _iopMem?.RaiseIopInterrupt(0); // real IOP INTC bit 0 = VBLANK start
        _onVblank?.Invoke();
    }

    public void EndVblank()
    {
        _inVblank = false;
        // Do NOT clear VBlankStart here — busy-pollers (e.g. MKSM 0x4803D0) need sticky STAT
        // bit2 until software write-1-clear. Only raise End edge.
        _intc?.Raise(Intc.InterruptSource.VBlankEnd);
        _iopMem?.RaiseIopInterrupt(11); // real IOP INTC bit 11 = VBLANK end (EVBLANK)
    }

    public int Step(ulong maxCycles)
    {
        if (maxCycles == 0) return 0;
        _cyclesAccum += maxCycles;
        // Half-period: raise VBlankStart and leave it set so software can poll INTC.STAT bit2.
        // Only end/raise VBlankEnd after another half-period (or if software already ACKed).
        ulong half = Math.Max(1UL, _vblankPeriod / 2);
        if (!_inVblank && _cyclesAccum >= half)
        {
            PresentFrame(); // raises VBlankStart, sets _inVblank
        }
        if (_inVblank && _cyclesAccum >= _vblankPeriod)
        {
            _cyclesAccum -= _vblankPeriod;
            // Do NOT auto-ack VBlankStart — games poll/ACK via INTC_STAT write-1-clear.
            // Only raise VBlankEnd edge; leave Start sticky until software clears it.
            // Always re-Raise Start (edge re-arms hold) so busy-pollers at 0x4803D0 see bit2
            // even if software cleared mid-frame — MKSM spent 250M+ cycles missing it.
            _inVblank = false;
            _intc?.Raise(Intc.InterruptSource.VBlankStart);
            _intc?.Raise(Intc.InterruptSource.VBlankEnd);
        }
        // Mid-period insurance: if Start is somehow clear while we still claim in-vblank,
        // re-assert so a tight poll cannot miss the entire frame window.
        else if (_inVblank && _intc != null && !_intc.IsRaised(Intc.InterruptSource.VBlankStart))
        {
            _intc.Raise(Intc.InterruptSource.VBlankStart);
        }
        return (int)Math.Min(maxCycles, (ulong)int.MaxValue);
    }
}
