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
    /// MasterCycles-based earliest time each STAT bit may be write-1-cleared. Gives busy-pollers
    /// (read INTC_STAT in a tight loop with IE still on) a window to observe VBlankStart before
    /// an ISR or sibling thread acks it. Real frames are ~0.5–1ms; a few thousand EE cycles is
    /// enough for a 5-instruction poll to win the race (Shaolin Monks 0x4803D0, 2026-07-29).
    /// </summary>
    private readonly ulong[] _statHoldUntil = new ulong[16];
    // Must outlast both the forced-preempt quantum (0x10000) and a full PCRTC VBlank period
    // (~500k) so a busy-poller that loses the CPU for a few slices still observes
    // VBlankStart. Shaolin Monks CRT0 at 0x4803D0 polls INTC_STAT bit2; with 200k hold the
    // game's own write-1-clear after the hold window stole the bit before the poller ran
    // (live STAT=0x2008 = VBlankEnd|Sif, no bit2 — 2026-07-29).
    private const ulong StatHoldCycles = 2_000_000;

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
        CpuLatched |= bit;
        if (edge)
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
        _onChanged?.Invoke();
    }

    public void Acknowledge(InterruptSource source)
    {
        int idx = (int)source;
        if ((uint)idx < (uint)_statHoldUntil.Length
            && CurrentCycleForTrace < _statHoldUntil[idx])
            return; // hold sticky for busy-pollers
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
        // Respect per-source hold so a VBlank ISR / sibling clear cannot erase Start
        // before the thread that is busy-polling INTC_STAT observes it.
        for (int i = 0; i < 16; i++)
        {
            uint bit = 1u << i;
            if ((allowed & bit) == 0) continue;
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
