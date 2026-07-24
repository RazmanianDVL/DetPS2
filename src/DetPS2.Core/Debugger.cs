using System;
using System.Collections.Generic;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// EE debugger (Phase 11): breakpoints, single-step, hit reporting.
/// </summary>
public sealed class Debugger
{
    private readonly HashSet<ulong> _breakpoints = new();
    private bool _stepOnce;
    private bool _halted;
    private ulong _haltPc;
    private ulong _haltCycle;

    public bool Enabled { get; set; }
    public bool Halted => _halted;
    public ulong HaltPc => _haltPc;
    public ulong HaltCycle => _haltCycle;
    public int BreakpointCount => _breakpoints.Count;
    public IReadOnlyCollection<ulong> Breakpoints => _breakpoints;

    public event Action<ulong, ulong>? OnBreakpointHit;

    public void Reset()
    {
        _halted = false;
        _haltPc = 0;
        _haltCycle = 0;
        _stepOnce = false;
    }

    public void ClearBreakpoints() => _breakpoints.Clear();
    public void AddBreakpoint(ulong address) => _breakpoints.Add(address & ~3UL);
    public void RemoveBreakpoint(ulong address) => _breakpoints.Remove(address & ~3UL);
    public bool HasBreakpoint(ulong address) => _breakpoints.Contains(address & ~3UL);

    /// <summary>Run one instruction then halt.</summary>
    public void RequestStep()
    {
        Enabled = true;
        _stepOnce = true;
        _halted = false;
    }

    public void Continue()
    {
        _halted = false;
        _stepOnce = false;
    }

    /// <summary>Before fetch: halt if breakpoint at pc (not for pure step-from-halt).</summary>
    public bool ShouldHaltBefore(ulong pc, ulong masterCycles)
    {
        if (!Enabled) return false;
        if (_halted) return true;

        if (_breakpoints.Contains(pc & ~3UL))
        {
            Hit(pc, masterCycles);
            return true;
        }
        return false;
    }

    /// <summary>After one instruction when stepping.</summary>
    public void AfterInstruction(ulong pc, ulong masterCycles)
    {
        if (!Enabled || !_stepOnce) return;
        _stepOnce = false;
        Hit(pc, masterCycles);
    }

    private void Hit(ulong pc, ulong masterCycles)
    {
        _halted = true;
        _haltPc = pc;
        _haltCycle = masterCycles;
        OnBreakpointHit?.Invoke(pc, masterCycles);
    }

    public string FormatRegisters(EmotionEngine ee)
    {
        var lines = new StringBuilder();
        lines.AppendLine($"PC=0x{ee.PC:X8} LO=0x{ee.LO:X16} HI=0x{ee.HI:X16}");
        lines.AppendLine($"Status=0x{ee.COP0_Status:X8} Cause=0x{ee.COP0_Cause:X8} EPC=0x{ee.COP0_EPC:X16}");
        for (int i = 0; i < 32; i += 4)
        {
            lines.Append($"r{i:D2}={ee.GetGpr(i).Lo:X16}  ");
            lines.Append($"r{i + 1:D2}={ee.GetGpr(i + 1).Lo:X16}  ");
            lines.Append($"r{i + 2:D2}={ee.GetGpr(i + 2).Lo:X16}  ");
            lines.AppendLine($"r{i + 3:D2}={ee.GetGpr(i + 3).Lo:X16}");
        }
        return lines.ToString();
    }

    public string FormatMemory(SystemMemory mem, ulong address, int words = 8)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < words; i++)
        {
            ulong a = address + (ulong)(i * 4);
            sb.AppendLine($"0x{a:X8}: 0x{mem.Read32(a):X8}");
        }
        return sb.ToString();
    }
}
