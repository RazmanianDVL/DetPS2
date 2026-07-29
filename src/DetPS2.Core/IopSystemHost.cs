using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Minimal C# HLE for BIOS IOP core modules that commercial IRX and EE RPC servers assume
/// are already resident after IOPBTCONF: INTRMAN*, TIMEMAN*, IOMAN, HEAPLIB, SYSCLIB.
///
/// Not cycle-accurate R3000 — just the service contracts (register handler / enable IRQ /
/// get time / device table presence) so drivers do not hang on missing destinations.
/// See docs/BIOS_DISSECTION.md §1–2.
/// </summary>
public sealed class IopSystemHost
{
    private sealed class IntrHandler
    {
        public int Irq;
        public uint Callback;
        public uint Arg;
        public bool Enabled;
    }

    private readonly List<IntrHandler> _handlers = new();
    private readonly Dictionary<string, int> _devices = new(StringComparer.OrdinalIgnoreCase);
    private ulong _systemClock; // TIMEMAN ticks (arbitrary unit advanced with EE VBlank)
    private int _nextDev = 1;
    private ulong _alarms;
    private ulong _intrRegisters;
    private ulong _intrEnables;

    public ulong SystemClock => _systemClock;
    public int HandlerCount => _handlers.Count;
    public int DeviceCount => _devices.Count;
    public ulong Alarms => _alarms;
    public ulong IntrRegisters => _intrRegisters;
    public ulong IntrEnables => _intrEnables;

    public void Reset()
    {
        _handlers.Clear();
        _devices.Clear();
        _systemClock = 0;
        _nextDev = 1;
        _alarms = 0;
        _intrRegisters = 0;
        _intrEnables = 0;
        // Default IOMAN devices always present after BIOS boot
        foreach (var d in new[] { "rom", "rom0", "cdrom", "cdrom0", "host", "host0", "mc0", "mc1", "tty", "stderr" })
            RegisterDevice(d);
    }

    public int RegisterDevice(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        name = name.Trim();
        if (_devices.TryGetValue(name, out int id)) return id;
        id = _nextDev++;
        _devices[name] = id;
        return id;
    }

    public bool HasDevice(string name) =>
        !string.IsNullOrEmpty(name) && _devices.ContainsKey(name.Trim());

    /// <summary>INTRMAN RegisterIntrHandler(irq, mode, handler, arg) — store only.</summary>
    public int RegisterIntrHandler(int irq, int mode, uint callback, uint arg)
    {
        _intrRegisters++;
        if (callback == 0) return unchecked((int)0xFFFFFFFE);
        for (int i = 0; i < _handlers.Count; i++)
        {
            if (_handlers[i].Irq == irq && _handlers[i].Callback == callback)
                return 0; // already
        }
        _handlers.Add(new IntrHandler { Irq = irq, Callback = callback, Arg = arg, Enabled = false });
        _ = mode;
        return 0;
    }

    public int ReleaseIntrHandler(int irq, uint callback)
    {
        for (int i = 0; i < _handlers.Count; i++)
        {
            if (_handlers[i].Irq == irq && (callback == 0 || _handlers[i].Callback == callback))
            {
                _handlers.RemoveAt(i);
                return 0;
            }
        }
        return -1;
    }

    public int EnableIntr(int irq)
    {
        _intrEnables++;
        foreach (var h in _handlers)
        {
            if (h.Irq == irq) h.Enabled = true;
        }
        // Enabling an IRQ with no registered handler is still success on real INTRMAN.
        return 0;
    }

    public int DisableIntr(int irq)
    {
        foreach (var h in _handlers)
            if (h.Irq == irq) h.Enabled = false;
        return 0;
    }

    /// <summary>TIMEMAN GetSystemTime / GetTimerCounter — returns synthetic ticks.</summary>
    public ulong GetSystemTime() => _systemClock;

    /// <summary>Advance TIMEMAN clock (called from EE VBlank / host step).</summary>
    public void Tick(ulong amount = 1)
    {
        _systemClock += amount == 0 ? 1UL : amount;
    }

    /// <summary>TIMEMAN SetAlarm — accept and count (callback not R3000-executed).</summary>
    public int SetAlarm(uint time, uint callback, uint arg)
    {
        _alarms++;
        _ = time; _ = callback; _ = arg;
        return 0;
    }

    public int CancelAlarm(uint callback, uint arg)
    {
        _ = callback; _ = arg;
        return 0;
    }

    /// <summary>Install IOPBTCONF-critical device names into IOMAN table.</summary>
    public void InstallBiosDevices()
    {
        foreach (var d in new[]
                 {
                     "rom", "rom0", "rom1", "cdrom", "cdrom0", "cdrom1",
                     "host", "host0", "mc", "mc0", "mc1", "mass", "hdd0", "pfs0",
                     "tty", "stderr", "dvrhome"
                 })
            RegisterDevice(d);
    }
}
