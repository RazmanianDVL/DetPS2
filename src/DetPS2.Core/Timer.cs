using System;

namespace DetPS2.Core;

/// <summary>
/// Single EE timer channel (Phase 8).
/// Mode bits (ps2tek): bit 7 = count enable, bit 8 = compare IRQ enable, bit 9 = overflow IRQ
/// enable, bit 10 = compare interrupt flag (R sticky / W1C), bit 11 = overflow interrupt flag
/// (R sticky / W1C). Prescale: bits 0-1 → 1, 16, 256, 1; bits 13-14 clock-select override.
/// </summary>
public sealed class TimerChannel
{
    /// <summary>MODE bit 10 — compare (equal) interrupt flag.</summary>
    public const uint ModeCompareFlag = 0x400;
    /// <summary>MODE bit 11 — overflow interrupt flag.</summary>
    public const uint ModeOverflowFlag = 0x800;

    public uint Count { get; private set; }
    /// <summary>Configuration bits only — sticky IRQ flags live in CompareIrqRaised/OverflowIrqRaised and are ORed onto reads.</summary>
    public uint Mode { get; private set; }
    public uint Compare { get; private set; }
    public bool CompareIrqRaised { get; private set; }
    public bool OverflowIrqRaised { get; private set; }

    private readonly Intc _intc;
    private readonly Intc.InterruptSource _irq;
    private ulong _prescaleAccum;

    public TimerChannel(Intc intc, Intc.InterruptSource irq)
    {
        _intc = intc ?? throw new ArgumentNullException(nameof(intc));
        _irq = irq;
        Reset();
    }

    public void Reset()
    {
        Count = 0;
        Mode = 0;
        Compare = 0;
        CompareIrqRaised = false;
        OverflowIrqRaised = false;
        _prescaleAccum = 0;
    }

    public void WriteState(System.IO.BinaryWriter w)
    {
        w.Write(Count); w.Write(Mode); w.Write(Compare);
        w.Write(CompareIrqRaised); w.Write(OverflowIrqRaised);
        w.Write(GateOpen);
        w.Write(_prescaleAccum);
    }

    public void ReadState(System.IO.BinaryReader r)
    {
        Count = r.ReadUInt32(); Mode = r.ReadUInt32(); Compare = r.ReadUInt32();
        CompareIrqRaised = r.ReadBoolean(); OverflowIrqRaised = r.ReadBoolean();
        GateOpen = r.ReadBoolean();
        _prescaleAccum = r.ReadUInt64();
    }

    public bool Enabled => (Mode & 0x80) != 0;
    public bool CompareIrqEnable => (Mode & 0x100) != 0;
    public bool OverflowIrqEnable => (Mode & 0x200) != 0;
    /// <summary>Phase 27: gate enable (bit 2) — when set, only count if GateOpen.</summary>
    public bool GateEnable => (Mode & 0x4) != 0;
    public bool GateOpen { get; set; } = true;
    /// <summary>Clock select bits 13-14: 0=bus, 1=1/16, 2=1/256, 3=external stub (bus).</summary>
    public int ClockSelect => (int)((Mode >> 13) & 0x3);

    private uint PrescaleDivisor
    {
        get
        {
            // Prefer clock select if set; else classic bits 0-1
            int cs = ClockSelect;
            if (cs != 0)
            {
                return cs switch
                {
                    1 => 16u,
                    2 => 256u,
                    _ => 1u
                };
            }
            return (Mode & 0x3) switch
            {
                0 => 1u,
                1 => 16u,
                2 => 256u,
                _ => 1u
            };
        }
    }

    public void Tick(ulong cycles)
    {
        if (!Enabled || cycles == 0) return;
        if (GateEnable && !GateOpen) return;

        _prescaleAccum += cycles;
        uint div = PrescaleDivisor;
        while (_prescaleAccum >= div)
        {
            _prescaleAccum -= div;
            uint prev = Count;
            Count = (Count + 1) & 0xFFFF;

            // Compare match when COUNT lands on COMP (including COMP==0 after wrap).
            // Previous `Compare != 0 && prev < Compare` skipped COMP=0 — Burnout 3's Timer2
            // alarm ISR gates callback work on MODE bit10, so a silent flag meant permanent
            // early-out of the alarm list walk (jr ra with ra=0 death at ~30M).
            if (Count == Compare)
            {
                bool wasRaised = CompareIrqRaised;
                CompareIrqRaised = true;
                if (CompareIrqEnable && !wasRaised)
                    _intc.Raise(_irq);
                if ((Mode & 0x40) != 0)
                    Count = 0;
            }

            if (Count == 0 && prev == 0xFFFF)
            {
                bool wasOv = OverflowIrqRaised;
                OverflowIrqRaised = true;
                if (OverflowIrqEnable && !wasOv)
                    _intc.Raise(_irq);
            }
        }
    }

    public void WriteCount(uint value) => Count = value & 0xFFFF;

    /// <summary>
    /// Write TN_MODE. Bits 10/11 are write-1-to-clear of sticky interrupt flags (ps2tek);
    /// they are not latched as configuration.
    /// </summary>
    public void WriteMode(uint value)
    {
        if ((value & ModeCompareFlag) != 0)
            CompareIrqRaised = false;
        if ((value & ModeOverflowFlag) != 0)
            OverflowIrqRaised = false;
        // Strip sticky flag bits from stored configuration.
        Mode = value & ~(ModeCompareFlag | ModeOverflowFlag);
    }
    public void WriteCompare(uint value) => Compare = value & 0xFFFF;

    public uint ReadCount() => Count;
    public uint ReadMode() =>
        Mode
        | (CompareIrqRaised ? ModeCompareFlag : 0)
        | (OverflowIrqRaised ? ModeOverflowFlag : 0);
    public uint ReadCompare() => Compare;
}

/// <summary>
/// EE Timers T0–T3 as a single schedulable unit (Phase 8).
/// </summary>
public sealed class EeTimers : ISchedulable
{
    public TimerChannel T0 { get; }
    public TimerChannel T1 { get; }
    public TimerChannel T2 { get; }
    public TimerChannel T3 { get; }

    private readonly TimerChannel[] _all;

    public EeTimers(Intc intc)
    {
        T0 = new TimerChannel(intc, Intc.InterruptSource.Timer0);
        T1 = new TimerChannel(intc, Intc.InterruptSource.Timer1);
        T2 = new TimerChannel(intc, Intc.InterruptSource.Timer2);
        T3 = new TimerChannel(intc, Intc.InterruptSource.Timer3);
        _all = new[] { T0, T1, T2, T3 };
    }

    public void Reset()
    {
        foreach (var t in _all) t.Reset();
    }

    public void WriteState(System.IO.BinaryWriter w)
    {
        foreach (var t in _all) t.WriteState(w);
    }

    public void ReadState(System.IO.BinaryReader r)
    {
        foreach (var t in _all) t.ReadState(r);
    }

    public int Step(ulong maxCycles)
    {
        if (maxCycles == 0) return 0;
        foreach (var t in _all)
            t.Tick(maxCycles);
        return (int)Math.Min(maxCycles, (ulong)int.MaxValue);
    }

    public TimerChannel this[int index] => _all[index & 3];

    // MMIO base 0x10000000 + n*0x800 roughly — use simple map
    public uint ReadRegister(uint address)
    {
        int which = GetTimerIndex(address);
        uint reg = (address >> 4) & 0xF;
        var t = _all[which];
        return reg switch
        {
            0 => t.ReadCount(),
            1 => t.ReadMode(),
            2 => t.ReadCompare(),
            _ => 0
        };
    }

    public void WriteRegister(uint address, uint value)
    {
        int which = GetTimerIndex(address);
        uint reg = (address >> 4) & 0xF;
        var t = _all[which];
        switch (reg)
        {
            case 0: t.WriteCount(value); break;
            case 1: t.WriteMode(value); break;
            case 2: t.WriteCompare(value); break;
        }
    }

    private static int GetTimerIndex(uint address)
    {
        // 0x10000000 T0, 0x10000800 T1, 0x10001000 T2, 0x10001800 T3
        uint off = (address - 0x10000000) & 0xFFFF;
        return (int)((off / 0x800) & 3);
    }
}

/// <summary>Back-compat alias used by older references.</summary>
public sealed class Timer
{
    private readonly TimerChannel _ch;
    public uint Count => _ch.ReadCount();
    public uint Compare => _ch.ReadCompare();
    public uint Mode => _ch.ReadMode();

    public Timer(Intc intc, Intc.InterruptSource interruptSource)
    {
        _ch = new TimerChannel(intc, interruptSource);
    }

    public void Reset() => _ch.Reset();
    public void Tick(ulong cycles) => _ch.Tick(cycles);
    public void WriteMode(uint value) => _ch.WriteMode(value);
    public void WriteCompare(uint value) => _ch.WriteCompare(value);
}
