using System;

namespace DetPS2.Core;

/// <summary>
/// Interrupt Controller (Phase 8).
/// STAT / MASK registers with MMIO; notifies EE to sync COP0 Cause.
///
/// <para><b>Stat vs CPU latch (real hardware semantics):</b>
/// On real PS2, <c>INTC_STAT</c> is sticky until software write-1-clear. Taking a COP0
/// exception does <b>not</b> clear STAT — games legitimately busy-poll <c>0x1000F000</c>
/// for VBlank (bit 2) with interrupts still enabled. Our synthesized ISR is a bare
/// <c>eret</c>, so if we left <c>GetPendingInterrupts() == Stat &amp; Mask</c> forever,
/// the EE would re-enter the exception on every instruction (storm). If we instead
/// auto-ack STAT in that fallback, pollers never see the bit (Shaolin Monks CRT0
/// spin at <c>0x00480330</c>, 2026-07-29). Solution: keep STAT sticky for MMIO;
/// deliver COP0 from a separate edge latch that is armed on 0→1 Raise and cleared
/// when the CPU "accepts" the interrupt (handler dispatch or no-handler fallback).
/// </para>
/// </summary>
public sealed class Intc : ISchedulable
{
    public enum InterruptSource
    {
        GS = 0,
        SbUs = 1,
        VBlankStart = 2,
        VBlankEnd = 3,
        Vif0 = 4,
        Vif1 = 5,
        Vu0 = 6,
        Vu1 = 7,
        Ipu = 8,
        Timer0 = 9,
        Timer1 = 10,
        Timer2 = 11,
        Timer3 = 12,
        Sif = 13,
        DmaController = 14,
    }

    // EE INTC MMIO
    public const uint AddrStat = 0x1000F000;
    public const uint AddrMask = 0x1000F010;

    public uint Stat { get; private set; }
    public uint Mask { get; private set; }

    /// <summary>
    /// Bits still pending delivery to COP0. Armed on Raise when STAT 0→1 (or re-Raise after
    /// software cleared STAT); cleared by <see cref="ClearCpuLatch"/> / full Acknowledge.
    /// </summary>
    public uint CpuLatched { get; private set; }

    /// <summary>
    /// MasterCycles-based earliest time <b>VBlank</b> STAT bits may be write-1-cleared / HLE-acked.
    /// Gives busy-pollers (read INTC_STAT in a tight loop with IE still on) a window to observe
    /// VBlankStart before an ISR or sibling thread acks it. Hold applies <b>only</b> to
    /// VBlankStart/End — never Timer/SIF (Vexx Timer0 storm when hold blocked Acknowledge while
    /// CpuLatched stayed armed, 2026-07-30).
    /// </summary>
    private readonly ulong[] _statHoldUntil = new ulong[16];
    // Must outlast both the forced-preempt quantum (0x10000) and a full PCRTC VBlank period
    // (~500k) so a busy-poller that loses the CPU for a few slices still observes
    // VBlankStart. Shaolin Monks CRT0 at 0x4803D0 polls INTC_STAT bit2; with 200k hold the
    // game's own write-1-clear after the hold window stole the bit before the poller ran
    // (live STAT=0x2008 = VBlankEnd|Sif, no bit2 — 2026-07-29).
    private const ulong StatHoldCycles = 2_000_000;

    /// <summary>
    /// MasterCycles at which each source's <see cref="CpuLatched"/> bit last made a genuine
    /// 0→1 transition (armed on the CpuLatched edge itself, not the Stat edge — Raise()
    /// unconditionally re-arms CpuLatched even when Stat is already sticky, and
    /// RearmCpuLatch is a second, independent CpuLatched 0→1 path). Used by
    /// EmotionEngine.TryDispatchRegisteredIntcHandler to impose <see cref="MinDispatchLatencyCycles"/>
    /// only on a freshly-latched source, never on one that's been pending for a while
    /// (multi-handler re-Raise chains, steady VBlank cadence) — those already paid the
    /// latency once, on their own fresh edge.
    /// </summary>
    private readonly ulong[] _latchedAtCycle = new ulong[16];

    /// <summary>
    /// Minimum cycles between a source's CpuLatched 0→1 edge and it becoming eligible for
    /// TryDispatchRegisteredIntcHandler. Deliberately small/conservative — not a claimed
    /// hardware-accurate datasheet figure, just enough that "interrupt lands on literal
    /// instruction 1 of an arbitrary callee" (confirmed live: Blood Omen 2 SLUS_200.24,
    /// DMAC src=14 landing on 0x0048A980's entry, corrupting an intermediate tail-called
    /// function's stack frame) becomes structurally rare instead of structurally guaranteed.
    /// </summary>
    public const ulong MinDispatchLatencyCycles = 16;

    /// <summary>Cycle of the given source's last genuine CpuLatched 0→1 edge (0 if never latched).</summary>
    public ulong LatchedAtCycle(int source) =>
        (uint)source < (uint)_latchedAtCycle.Length ? _latchedAtCycle[source] : 0;

    private Action? _onChanged;

    public Intc() => Reset();

    public void SetNotify(Action onChanged) => _onChanged = onChanged;

    public void Reset()
    {
        Stat = 0;
        Mask = 0;
        CpuLatched = 0;
        Array.Clear(_statHoldUntil);
    }

    public static ulong CurrentCycleForTrace;
    public static readonly bool TraceRaise = Environment.GetEnvironmentVariable("DETPS2_TRACE_INTC") == "1";

    public void Raise(InterruptSource source)
    {
        if (TraceRaise)
            Console.Error.WriteLine($"[INTC] Raise {source} cyc={CurrentCycleForTrace} alreadyRaised={(Stat & (1u << (int)source)) != 0} mask={Mask:X8}");
        uint bit = 1u << (int)source;
        bool edge = (Stat & bit) == 0;
        Stat |= bit;
        // Always re-arm COP0 delivery on each hardware Raise, even when STAT is already sticky.
        // Real INTC keeps IP2 asserted while STAT&MASK is set; we model delivery as a
        // one-shot latch that the CPU consumes (ClearCpuLatch / Acknowledge) so bare-eret
        // doesn't storm, but each *new hardware event* (next VBlank period, next SIF DMA,
        // …) must re-arm or the source goes permanently silent after the first accept.
        // Edge-only re-arm left God of War's VBlankStart handler (cause=2 @ 0x00182F28)
        // dead after the first pre-registration ClearCpuLatch: STAT stayed sticky, every
        // subsequent Pcrtc Raise saw alreadyRaised=true, CpuLatched never came back, and
        // the frame-counter wait at 0x0021FF00 spun forever on a counter that never moved.
        //
        // Stamp _latchedAtCycle on CpuLatched's OWN 0->1 edge (cpuEdge), NOT on `edge`
        // above (which is Stat's edge and only gates the VBlank StatHold logic below) --
        // CpuLatched re-arms here unconditionally even when Stat was already sticky, so a
        // source can produce a fresh CpuLatched edge on a Raise where `edge` is false.
        bool cpuEdge = (CpuLatched & bit) == 0;
        CpuLatched |= bit;
        if (cpuEdge)
        {
            int srcIdx = (int)source;
            if ((uint)srcIdx < (uint)_latchedAtCycle.Length)
                _latchedAtCycle[srcIdx] = CurrentCycleForTrace;
        }
        // Hold only for VBlank sticky-poll assist (Shaolin Monks CRT0 INTC_STAT spin).
        // Applying the 2M-cycle hold to Timer/SIF/etc. makes HLE Acknowledge a no-op while
        // TryDispatchRegisteredIntcHandler still relies on Acknowledge to clear CpuLatched for
        // timers (ClearCpuLatch is deliberately skipped on that path) → permanent Timer0 storm
        // (Vexx SLUS_203.83: 334k src=9 dispatches in 8M cyc, LOADFILE/RPC starved, 2026-07-30).
        if (edge && (source is InterruptSource.VBlankStart or InterruptSource.VBlankEnd))
        {
            int idx = (int)source;
            if ((uint)idx < (uint)_statHoldUntil.Length)
                _statHoldUntil[idx] = CurrentCycleForTrace + StatHoldCycles;
        }
        _onChanged?.Invoke();
    }

    /// <summary>
    /// Re-arm COP0 delivery for a source whose STAT bit is already sticky (e.g. right after
    /// <c>AddIntcHandler</c> registers ownership of a cause that fired before the handler
    /// existed and was consumed by the no-handler ClearCpuLatch path).
    /// </summary>
    public void RearmCpuLatch(InterruptSource source)
    {
        uint bit = 1u << (int)source;
        if ((Stat & bit) == 0) return;
        if ((CpuLatched & bit) != 0) return;
        CpuLatched |= bit;
        int srcIdx = (int)source;
        if ((uint)srcIdx < (uint)_latchedAtCycle.Length)
            _latchedAtCycle[srcIdx] = CurrentCycleForTrace;
        _onChanged?.Invoke();
    }

    public void Acknowledge(InterruptSource source)
    {
        int idx = (int)source;
        // Hold applies only to VBlank sources (see Raise); other sources must ack immediately
        // so HLE timer/SIF dispatch cannot storm on a stuck CpuLatched bit.
        if (source is InterruptSource.VBlankStart or InterruptSource.VBlankEnd
            && (uint)idx < (uint)_statHoldUntil.Length
            && CurrentCycleForTrace < _statHoldUntil[idx])
            return; // hold sticky for VBlank busy-pollers
        uint bit = 1u << idx;
        Stat &= ~bit;
        CpuLatched &= ~bit;
        _onChanged?.Invoke();
    }

    /// <summary>
    /// CPU has accepted this interrupt (handler ran or default ISR). Clear COP0 latch only —
    /// leave sticky STAT for software busy-poll / write-1-clear. Required so bare-eret HLE
    /// does not storm while VBlank pollers still see bit 2.
    /// </summary>
    public void ClearCpuLatch(InterruptSource source)
    {
        uint bit = 1u << (int)source;
        if ((CpuLatched & bit) == 0) return;
        CpuLatched &= ~bit;
        _onChanged?.Invoke();
    }

    /// <summary>Clear all CPU latches for currently pending (latched &amp; masked) sources.</summary>
    public void ClearCpuLatchPending()
    {
        uint clear = CpuLatched & Mask;
        if (clear == 0) return;
        CpuLatched &= ~clear;
        _onChanged?.Invoke();
    }

    /// <summary>Write-1-to-clear style for STAT (and matching COP0 latch bits).</summary>
    public void WriteStatClear(uint value)
    {
        uint allowed = value;
        // Respect VBlank hold so a sibling ISR clear cannot erase Start before the thread
        // that is busy-polling INTC_STAT observes it. Timer/SIF/etc. clear immediately.
        for (int i = 0; i < 16; i++)
        {
            uint bit = 1u << i;
            if ((allowed & bit) == 0) continue;
            if (i is not ((int)InterruptSource.VBlankStart or (int)InterruptSource.VBlankEnd))
                continue;
            if (CurrentCycleForTrace < _statHoldUntil[i])
                allowed &= ~bit;
        }
        if (allowed == 0) return;
        Stat &= ~allowed;
        CpuLatched &= ~allowed;
        _onChanged?.Invoke();
    }

    public bool IsRaised(InterruptSource source) =>
        (Stat & (1u << (int)source)) != 0;

    public bool IsPending(InterruptSource source) =>
        (CpuLatched & (1u << (int)source)) != 0 &&
        (Mask & (1u << (int)source)) != 0;

    public void SetMask(uint mask)
    {
        Mask = mask;
        _onChanged?.Invoke();
    }

    /// <summary>Restore STAT/MASK from save state.</summary>
    public void RestoreState(uint stat, uint mask)
    {
        Stat = stat;
        Mask = mask;
        // After restore, treat sticky STAT as already-delivered so we don't storm on load;
        // next Raise edge re-arms. Callers that need immediate re-fire can Raise again.
        CpuLatched = 0;
        _onChanged?.Invoke();
    }

    /// <summary>
    /// Sources that should assert COP0 right now (latched edge &amp; masked).
    /// Sticky STAT alone does not re-assert after the CPU has accepted the edge.
    /// </summary>
    public uint GetPendingInterrupts() => CpuLatched & Mask;

    public bool AnyPending => GetPendingInterrupts() != 0;

    public uint ReadRegister(uint address)
    {
        return address switch
        {
            AddrStat => Stat,
            AddrMask => Mask,
            _ when (address & ~0xFu) == AddrStat => Stat,
            _ when (address & ~0xFu) == AddrMask => Mask,
            _ => 0
        };
    }

    public void WriteRegister(uint address, uint value)
    {
        if (address == AddrStat || (address & ~0xFu) == AddrStat)
            WriteStatClear(value);
        else if (address == AddrMask || (address & ~0xFu) == AddrMask)
            SetMask(value);
    }

    public int Step(ulong maxCycles) => 0;
}
