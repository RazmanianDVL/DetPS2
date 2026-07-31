using System;
using System.Collections.Generic;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// C# HLE for BIOS IOP core modules that commercial IRX and EE RPC servers assume are
/// already resident after IOPBTCONF: <b>INTRMAN</b> (INTRMANP/INTRMANI), <b>TIMEMAN</b>
/// (TIMEMANP/TIMEMANI hard timers + thbase SysClock/SetAlarm surface), <b>IOMAN</b>
/// device registry (AddDrv/DelDrv + path parse), and a minimal <b>STDIO</b> printf sink.
///
/// Authority:
/// <list type="bullet">
/// <item>INTRMAN export table from extracted <c>INTRMANP.bin</c> (lib <c>intrman</c> v1.2,
/// 32 exports) — no full Ghidra dump yet; KE_* error constants grounded in the binary
/// (li immediates -100..-105) and ps2sdk <c>iop/system/intrman/include/intrman.h</c>.</item>
/// <item>VBLANK's use of RegisterIntrHandler(IRQ 0/11) + EnableIntr (decomp + ps2sdk vblank.c).</item>
/// <item>TIMEMAN hard timers: ps2sdk <c>iop/system/timrman</c> recreation of TIMEMANI (6 RTC
/// slots) / TIMEMANP (3 RTC slots) — table layout, timid encoding, KE_NO_TIMER/-150 family.
/// No BIOS <c>TIMEMAN*_ALL.txt</c> in-tree yet; contracts match open recreation + kerr.h.</item>
/// <item>thbase GetSystemTime / SetAlarm / USec2SysClock: ps2sdk threadman <c>thbase.c</c>
/// (iop_sys_clock_t lo/hi, relative delta, KE_FOUND_HANDLER on duplicate cb+arg).</item>
/// <item>IOMAN AddDrv/DelDrv/path parse: Ghidra <c>IOMAN_ALL.txt</c> FUN_00000e8c /
/// FUN_00000f44 / FUN_00000d28; classic table is 16 slots (iomanX steals 16 entries).</item>
/// <item>STDIO: non-fatal printf/puts log sink attached to tty/stderr devices.</item>
/// </list>
///
/// Not cycle-accurate R3000 — service contracts so drivers do not hang on missing destinations.
/// Distinct from <see cref="IopExcepManHost"/> (synchronous CPU exceptions, not async IRQs).
/// </summary>
public sealed class IopSystemHost
{
    // IOP IRQ numbers used by VBLANK / common modules (ps2sdk iop_irq_list).
    public const int IrqVblank = 0;   // IOP_IRQ_VBLANK
    public const int IrqSbus = 1;
    public const int IrqCdvd = 2;
    public const int IrqDma = 3;
    public const int IrqEvblank = 11; // IOP_IRQ_EVBLANK

    /// <summary>Highest hardware IRQ line HLE accepts (SW1/SW2 are 0x3E/0x3F).</summary>
    public const int MaxIrq = 0x3F;

    /// <summary>
    /// Classic BIOS IOMAN device-table capacity (Ghidra table + iomanX "16 registered
    /// device entries"). HLE uses 32 so InstallBiosDevices + dynamic AddDrv still fit
    /// without thrashing (iomanX MAX_DEVICES=32).
    /// </summary>
    public const int MaxIomanDevices = 32;

    /// <summary>IOMAN ENODEV — unknown device (FUN_00000d28 / open path).</summary>
    public const int IoManErrnoNoDevice = -19;

    /// <summary>IOMAN EMFILE-style table full for AddDrv (no free slot).</summary>
    public const int IoManErrnoDeviceTableFull = -1;

    // KE_* from kerr.h / INTRMANP binary immediates:
    public const int ResultOk = 0;
    public const int ResultIllegalContext = unchecked((int)0xFFFFFF9C); // -100
    public const int ResultIllegalIntrCode = unchecked((int)0xFFFFFF9B); // -101
    public const int ResultCpuDi = unchecked((int)0xFFFFFF9A);          // -102
    public const int ResultIntrDisable = unchecked((int)0xFFFFFF99);    // -103
    public const int ResultFoundHandler = unchecked((int)0xFFFFFF98);   // -104
    public const int ResultNotFoundHandler = unchecked((int)0xFFFFFF97); // -105

    // TIMEMAN / timrman KE_* (kerr.h)
    public const int ResultNoTimer = unchecked((int)0xFFFFFF6A);        // -150
    public const int ResultIllegalTimerId = unchecked((int)0xFFFFFF69); // -151
    public const int ResultIllegalSource = unchecked((int)0xFFFFFF68);  // -152
    public const int ResultIllegalPrescale = unchecked((int)0xFFFFFF67);// -153
    public const int ResultTimerBusy = unchecked((int)0xFFFFFF66);      // -154
    public const int ResultTimerNotSetup = unchecked((int)0xFFFFFF65);  // -155
    public const int ResultTimerNotInUse = unchecked((int)0xFFFFFF64);  // -156
    public const int ResultIllegalMode = unchecked((int)0xFFFFFE6B);    // -405
    public const int ResultNoMemory = unchecked((int)0xFFFFFE70);       // -400

    // Hard-timer source bits (timrman.h / SCE docs)
    public const int TcSysClock = 1; // 36.864 MHz
    public const int TcPixel = 2;    // 13.5 MHz
    public const int TcHLine = 4;    // H-line
    public const int TcHold = 8;

    /// <summary>
    /// IOP bus ticks per microsecond used by USec2SysClock / SysClock2USec HLE.
    /// Matches the common 36.864 MHz SYSCLOCK model (usec * 36864 / 1000).
    /// </summary>
    public const uint SysClockMul = 36864;
    public const uint SysClockDiv = 1000;

    /// <summary>SysClock ticks advanced per EE VBlank host edge (NTSC ≈ 1/60 s).</summary>
    public const ulong SysClockPerVblank =
        (ulong)SysClockMul * 1_000_000UL / SysClockDiv / 60UL; // ≈ 614_400

    private sealed class IntrHandler
    {
        public int Irq;
        public int Mode;
        public uint Callback;
        public uint Arg;
    }

    /// <summary>Hard-timer slot (TIMEMANI RTC0–5 table; TIMEMANP is first 3).</summary>
    private sealed class HardTimer
    {
        public int Index;
        public uint CountAddr;   // physical count register (e.g. 0xBF801100)
        public byte Sources;     // bitmask of TC_*
        public byte WidthBits;   // 16 or 32
        public ushort MaxPrescale;
        public byte Irq;
        public int Users;
        public bool HasIrqHandler;
        public uint Mode;
        public uint CtrlConfig;
        public uint Compare;
        public uint Counter;
        public uint TimeupFlags;
        public uint OverflowFlags;
        /// <summary>timrman SetTimerHandler callback (opaque IOP ptr; not R3000-executed).</summary>
        public uint CompareCallback;
        public uint CompareCallbackArg;
        /// <summary>timrman SetOverflowHandler callback.</summary>
        public uint OverflowCallback;
        public uint OverflowCallbackArg;
    }

    private sealed class AlarmEntry
    {
        public ulong FireAt;
        public uint Callback;
        public uint Arg;
        public bool Fired;
    }

    /// <summary>HLE device entry (iop_device_t name/desc/type subset).</summary>
    public sealed class IomanDeviceEntry
    {
        public string Name = "";
        public string Desc = "";
        public uint Type;    // IOP_DT_* bits
        public uint Version;
        public bool Builtin; // InstallBiosDevices / tty — protected lightly on DelDrv
    }

    private readonly IntrHandler?[] _handlers = new IntrHandler[MaxIrq + 1];
    private readonly bool[] _enabled = new bool[MaxIrq + 1];
    private readonly bool[] _dispatchEnabled = new bool[MaxIrq + 1]; // DisableDispatchIntr soft mask
    /// <summary>Pending (raised, not yet acknowledged) IRQ lines — HLE bookkeeping for query/status.</summary>
    private readonly bool[] _pending = new bool[MaxIrq + 1];
    /// <summary>IOMAN device slots (null = free). Classic IOMAN = 16; HLE allows 32.</summary>
    private readonly IomanDeviceEntry?[] _deviceSlots = new IomanDeviceEntry[MaxIomanDevices];
    private readonly Dictionary<string, int> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AlarmEntry> _alarms = new();
    private readonly List<string> _stdioLog = new();
    private HardTimer[] _hardTimers = Array.Empty<HardTimer>();
    private ulong _systemClock;
    private int _nextDev = 1;
    private ulong _alarmAccepts;
    private ulong _alarmFires;
    private ulong _hardTimerAllocs;
    private ulong _hardTimerCompareHits;
    private ulong _intrRegisters;
    private ulong _intrReleases;
    private ulong _intrEnables;
    private ulong _intrDisables;
    private ulong _intrAcks;
    private ulong _intrRaises;
    private ulong _addDrvCalls;
    private ulong _delDrvCalls;
    private ulong _stdioWrites;
    private int _handlerCount;
    private int _cpuIntrSuspendDepth;
    private bool _cpuIntrEnabled = true;
    private bool _intrContext;
    private bool _useMani; // true = 6 timers (TIMEMANI), false = 3 (TIMEMANP)

    public IopSystemHost()
    {
        // Seed default IOMAN devices so FILEIO host:/mc0: probes work before
        // BiosBootHost.StartCommercialIop (matches prior Reset()-time defaults).
        InitHardTimers(mani: true);
        foreach (var d in new[] { "rom", "rom0", "cdrom", "cdrom0", "host", "host0", "mc0", "mc1", "tty", "stderr" })
            RegisterDevice(d);
    }

    public ulong SystemClock => _systemClock;
    public int HandlerCount => _handlerCount;
    public int DeviceCount => _devices.Count;
    public int DeviceSlotUsed
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _deviceSlots.Length; i++)
                if (_deviceSlots[i] != null) n++;
            return n;
        }
    }
    public ulong AddDrvCalls => _addDrvCalls;
    public ulong DelDrvCalls => _delDrvCalls;
    public ulong StdioWrites => _stdioWrites;
    /// <summary>Ring of recent STDIO printf/puts lines (diagnostics / smokes).</summary>
    public IReadOnlyList<string> StdioLog => _stdioLog;
    /// <summary>STDIO.IRX contract planted (BiosBootHost / reboot handoff).</summary>
    public bool StdioReady { get; set; }
    public ulong Alarms => _alarmAccepts;
    public ulong AlarmFires => _alarmFires;
    public ulong HardTimerAllocs => _hardTimerAllocs;
    /// <summary>How many times a hard-timer counter crossed its compare value (SetTimerHandler path).</summary>
    public ulong HardTimerCompareHits => _hardTimerCompareHits;
    public int HardTimerCount => _hardTimers.Length;
    public int HardTimersInUse
    {
        get
        {
            int n = 0;
            foreach (var t in _hardTimers)
                if (t.Users > 0) n++;
            return n;
        }
    }
    public ulong IntrRegisters => _intrRegisters;
    public ulong IntrReleases => _intrReleases;
    public ulong IntrEnables => _intrEnables;
    public ulong IntrDisables => _intrDisables;
    public ulong IntrAcknowledges => _intrAcks;
    public ulong IntrRaises => _intrRaises;
    public bool CpuInterruptsEnabled => _cpuIntrEnabled && _cpuIntrSuspendDepth == 0;
    public int CpuSuspendDepth => _cpuIntrSuspendDepth;

    /// <summary>QueryIntrContext HLE: 1 inside a simulated IRQ, 0 in thread context.</summary>
    public bool InterruptContext
    {
        get => _intrContext;
        set => _intrContext = value;
    }

    public void Reset()
    {
        Array.Clear(_handlers);
        Array.Clear(_enabled);
        Array.Clear(_pending);
        for (int i = 0; i < _dispatchEnabled.Length; i++)
            _dispatchEnabled[i] = true;
        Array.Clear(_deviceSlots);
        _devices.Clear();
        _alarms.Clear();
        _stdioLog.Clear();
        _systemClock = 0;
        _nextDev = 1;
        _alarmAccepts = 0;
        _alarmFires = 0;
        _hardTimerAllocs = 0;
        _hardTimerCompareHits = 0;
        _intrRegisters = _intrReleases = 0;
        _intrEnables = _intrDisables = 0;
        _intrAcks = _intrRaises = 0;
        _addDrvCalls = _delDrvCalls = 0;
        _stdioWrites = 0;
        StdioReady = false;
        _handlerCount = 0;
        _cpuIntrSuspendDepth = 0;
        _cpuIntrEnabled = true;
        _intrContext = false;
        _useMani = true;
        InitHardTimers(_useMani);
        // Default IOMAN devices always present after BIOS boot (base names + unit aliases).
        foreach (var d in new[] { "rom", "rom0", "cdrom", "cdrom0", "host", "host0", "mc0", "mc1", "tty", "stderr" })
            RegisterDevice(d);
    }

    /// <summary>
    /// Install TIMEMANI (6 timers, PS2 IOP) or TIMEMANP (3 timers, PS1-compat) table.
    /// BiosBootHost plants TIMEMANI by default (commercial PS2 path).
    /// </summary>
    public void ConfigureTimeMan(bool useMani = true)
    {
        _useMani = useMani;
        InitHardTimers(useMani);
    }

    private void InitHardTimers(bool mani)
    {
        // Layout from ps2sdk timrman.c (SCE TIMEMAN recreation).
        // timid = ((index+1) << 28) | (countAddr >> 4)
        if (mani)
        {
            _hardTimers = new[]
            {
                NewHt(0, 0xBF801100, sources: 0xB, width: 16, maxPs: 1, irq: 4),
                NewHt(1, 0xBF801110, sources: 0xD, width: 16, maxPs: 1, irq: 5),
                NewHt(2, 0xBF801120, sources: 1, width: 16, maxPs: 8, irq: 6),
                NewHt(3, 0xBF801480, sources: 5, width: 32, maxPs: 1, irq: 0xE),
                NewHt(4, 0xBF801490, sources: 1, width: 32, maxPs: 256, irq: 0xF),
                NewHt(5, 0xBF8014A0, sources: 1, width: 32, maxPs: 256, irq: 0x10),
            };
        }
        else
        {
            _hardTimers = new[]
            {
                NewHt(0, 0xBF801100, sources: 0xB, width: 16, maxPs: 1, irq: 4),
                NewHt(1, 0xBF801110, sources: 0xD, width: 16, maxPs: 1, irq: 5),
                NewHt(2, 0xBF801120, sources: 1, width: 16, maxPs: 8, irq: 6),
            };
        }
    }

    private static HardTimer NewHt(int index, uint addr, int sources, int width, int maxPs, int irq) =>
        new()
        {
            Index = index,
            CountAddr = addr,
            Sources = (byte)sources,
            WidthBits = (byte)width,
            MaxPrescale = (ushort)maxPs,
            Irq = (byte)irq,
        };

    /// <summary>Encode timid the way real TIMEMAN does: ((idx+1)&lt;&lt;28) | (addr&gt;&gt;4).</summary>
    public static int EncodeTimid(int index, uint countAddr) =>
        (int)(((uint)(index + 1) << 28) | (countAddr >> 4));

    public static int DecodeTimerIndex(int timid)
    {
        int idx = (int)((uint)timid >> 28) - 1;
        return idx;
    }

    public int RegisterDevice(string name) =>
        RegisterDevice(name, desc: null, type: 0x10 /* IOP_DT_FS */, version: 1, builtin: true);

    /// <summary>
    /// Register a named IOMAN device (idempotent). Occupies one AddDrv slot when new.
    /// </summary>
    public int RegisterDevice(string name, string? desc, uint type, uint version, bool builtin)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        name = name.Trim();
        if (_devices.TryGetValue(name, out int id)) return id;
        int slot = FindFreeDeviceSlot();
        if (slot < 0) return IoManErrnoDeviceTableFull;
        id = _nextDev++;
        var entry = new IomanDeviceEntry
        {
            Name = name,
            Desc = desc ?? name,
            Type = type,
            Version = version,
            Builtin = builtin,
        };
        _deviceSlots[slot] = entry;
        _devices[name] = id;
        return id;
    }

    public bool HasDevice(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        name = name.Trim();
        if (_devices.ContainsKey(name)) return true;
        // Unit-suffixed lookup: "mc0" → base "mc" (FUN_00000d28 trailing-digit strip).
        string bas = StripDeviceUnit(name);
        return bas.Length > 0 && _devices.ContainsKey(bas);
    }

    /// <summary>
    /// IOMAN AddDrv (FUN_00000e8c): install device by name into first free slot.
    /// Returns 0 on success, -1 on full table or empty name. Idempotent if name exists.
    /// </summary>
    public int AddDrv(string name, string? desc = null, uint type = 0x10, uint version = 1)
    {
        _addDrvCalls++;
        if (string.IsNullOrWhiteSpace(name)) return -1;
        name = name.Trim();
        if (_devices.ContainsKey(name))
            return 0; // already registered — treat as success (real may reject; HLE soft)
        int id = RegisterDevice(name, desc, type, version, builtin: false);
        return id < 0 ? -1 : 0;
    }

    /// <summary>
    /// IOMAN DelDrv (FUN_00000f44): remove device by name. Returns 0 on success, -1 if missing.
    /// Builtin BIOS devices can still be removed (real DelDrv does); InstallBiosDevices
    /// re-seeds them on reboot handoff.
    /// </summary>
    public int DelDrv(string name)
    {
        _delDrvCalls++;
        if (string.IsNullOrWhiteSpace(name)) return -1;
        name = name.Trim();
        if (!_devices.Remove(name))
        {
            // Try base name if unit-suffixed
            string bas = StripDeviceUnit(name);
            if (bas.Length == 0 || !_devices.Remove(bas))
                return -1;
            name = bas;
        }
        for (int i = 0; i < _deviceSlots.Length; i++)
        {
            var e = _deviceSlots[i];
            if (e != null && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                _deviceSlots[i] = null;
                return 0;
            }
        }
        return 0; // name map removed even if slot missing
    }

    /// <summary>
    /// FUN_00000d28 path parse: skip spaces, require ':', extract device name + optional
    /// trailing unit digits, remainder after colon. Returns false → ENODEV.
    /// </summary>
    public bool TryParseDevicePath(string path, out string device, out int unit, out string remainder)
    {
        device = "";
        unit = 0;
        remainder = "";
        if (string.IsNullOrEmpty(path)) return false;
        int i = 0;
        while (i < path.Length && path[i] == ' ') i++;
        int colon = path.IndexOf(':', i);
        if (colon <= i) return false;
        string raw = path[i..colon];
        remainder = colon + 1 < path.Length ? path[(colon + 1)..] : "";
        // Trailing digits → unit number (mc0 → mc, unit 0).
        int end = raw.Length;
        while (end > 0 && char.IsDigit(raw[end - 1])) end--;
        if (end < raw.Length && end > 0)
        {
            if (!int.TryParse(raw[end..], out unit)) unit = 0;
            device = raw[..end];
        }
        else
        {
            device = raw;
            unit = 0;
        }
        if (device.Length == 0) return false;
        // Lookup: exact name or base name must be registered.
        if (_devices.ContainsKey(device)) return true;
        if (_devices.ContainsKey(raw)) { device = raw; return true; }
        return false;
    }

    /// <summary>True when path has a device prefix that is registered (or no colon — relative).</summary>
    public bool IsKnownDevicePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return true; // relative / empty OK for dir probes
        if (path.IndexOf(':') < 0) return true;
        return TryParseDevicePath(path, out _, out _, out _);
    }

    private int FindFreeDeviceSlot()
    {
        for (int i = 0; i < _deviceSlots.Length; i++)
            if (_deviceSlots[i] == null) return i;
        return -1;
    }

    private static string StripDeviceUnit(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        int end = name.Length;
        while (end > 0 && char.IsDigit(name[end - 1])) end--;
        return end > 0 ? name[..end] : name;
    }

    // ---- STDIO.IRX (printf / puts → non-fatal log sink) ----

    /// <summary>Ensure tty/stderr devices exist (STDIO / IOMAN CONSOLE bring-up).</summary>
    public void EnsureStdioDevices()
    {
        RegisterDevice("tty", "CONSOLE", type: 0x03 /* CHAR|CONS */, version: 1, builtin: true);
        RegisterDevice("stderr", "stderr", type: 0x01 /* CHAR */, version: 1, builtin: true);
        // IOMAN opens "tty00:" at init (decomp FUN_00000098) — unit alias for tty.
        RegisterDevice("tty00", "CONSOLE", type: 0x03, version: 1, builtin: true);
        StdioReady = true;
    }

    /// <summary>
    /// STDIO printf/puts HLE. Never throws; appends to <see cref="StdioLog"/> and optionally
    /// mirrors to host stderr when <c>DETPS2_IOP_STDIO=1</c>.
    /// </summary>
    public void Printf(string? message)
    {
        _stdioWrites++;
        string line = message ?? "";
        if (_stdioLog.Count >= 256)
            _stdioLog.RemoveAt(0);
        _stdioLog.Add(line);
        if (Environment.GetEnvironmentVariable("DETPS2_IOP_STDIO") == "1")
            Console.Error.Write(line);
    }

    /// <summary>puts-style: ensure trailing newline.</summary>
    public void Puts(string? message)
    {
        string line = message ?? "";
        if (!line.EndsWith('\n')) line += "\n";
        Printf(line);
    }

    /// <summary>Write EE/IOP buffer bytes into STDIO sink (tty write path).</summary>
    public int StdioWriteBytes(SystemMemory mem, uint buf, uint size)
    {
        if (mem == null || buf == 0 || size == 0) return 0;
        uint n = Math.Min(size, 0x1000u);
        var sb = new StringBuilder((int)n);
        for (uint i = 0; i < n; i++)
        {
            byte c = mem.Read8(buf + i);
            if (c == 0) break;
            if (c >= 0x20 && c < 0x7F || c is (byte)'\n' or (byte)'\r' or (byte)'\t')
                sb.Append((char)c);
            else
                sb.Append('.');
        }
        Printf(sb.ToString());
        return (int)n;
    }

    private static bool IsValidIrq(int irq) => (uint)irq <= MaxIrq;

    /// <summary>
    /// INTRMAN RegisterIntrHandler(irq, mode, handler, arg) — one handler per IRQ.
    /// Returns KE_FOUND_HANDLER if a handler is already installed (real INTRMAN does not stack).
    /// Rejects interrupt context (KE_ILLEGAL_CONTEXT) to match retail thread-only register path.
    /// </summary>
    public int RegisterIntrHandler(int irq, int mode, uint callback, uint arg)
    {
        _intrRegisters++;
        if (_intrContext) return ResultIllegalContext;
        if (!IsValidIrq(irq)) return ResultIllegalIntrCode;
        if (callback == 0) return ResultIllegalIntrCode;
        if (_handlers[irq] != null) return ResultFoundHandler;
        _handlers[irq] = new IntrHandler
        {
            Irq = irq,
            Mode = mode,
            Callback = callback,
            Arg = arg
        };
        _handlerCount++;
        return ResultOk;
    }

    /// <summary>
    /// INTRMAN ReleaseIntrHandler(irq) — removes the single handler for <paramref name="irq"/>.
    /// Optional <paramref name="callback"/>: if non-zero, only release when it matches (HLE
    /// convenience; real API is irq-only).
    /// </summary>
    public int ReleaseIntrHandler(int irq, uint callback = 0)
    {
        _intrReleases++;
        if (_intrContext) return ResultIllegalContext;
        if (!IsValidIrq(irq)) return ResultIllegalIntrCode;
        var h = _handlers[irq];
        if (h == null) return ResultNotFoundHandler;
        if (callback != 0 && h.Callback != callback) return ResultNotFoundHandler;
        _handlers[irq] = null;
        _enabled[irq] = false;
        _pending[irq] = false;
        _handlerCount--;
        return ResultOk;
    }

    /// <summary>INTRMAN EnableIntr(irq) — unmask hardware cause.</summary>
    public int EnableIntr(int irq)
    {
        _intrEnables++;
        if (!IsValidIrq(irq)) return ResultIllegalIntrCode;
        _enabled[irq] = true;
        // Enabling with no registered handler is still success on real INTRMAN.
        return ResultOk;
    }

    /// <summary>
    /// INTRMAN DisableIntr(irq) — mask hardware cause. Real API writes the old irq number
    /// through an optional <c>int *res</c>; HLE returns the irq on success via
    /// <see cref="TryDisableIntr"/>.
    /// </summary>
    public int DisableIntr(int irq)
    {
        _intrDisables++;
        if (!IsValidIrq(irq)) return ResultIllegalIntrCode;
        _enabled[irq] = false;
        return ResultOk;
    }

    /// <summary>DisableIntr with old-line report (ps2sdk <c>DisableIntr(irq, int *res)</c>).</summary>
    public int TryDisableIntr(int irq, out int previousIrq)
    {
        previousIrq = irq;
        int rc = DisableIntr(irq);
        // Real DisableIntr writes the irq number into *res on success so callers can re-enable.
        if (rc != ResultOk)
            previousIrq = -1;
        return rc;
    }

    public bool IsIntrEnabled(int irq) =>
        IsValidIrq(irq) && _enabled[irq];

    public bool HasIntrHandler(int irq) =>
        IsValidIrq(irq) && _handlers[irq] != null;

    public uint GetIntrHandler(int irq) =>
        IsValidIrq(irq) && _handlers[irq] != null ? _handlers[irq]!.Callback : 0;

    /// <summary>Query registered handler mode (0 if none).</summary>
    public int GetIntrHandlerMode(int irq) =>
        IsValidIrq(irq) && _handlers[irq] != null ? _handlers[irq]!.Mode : 0;

    /// <summary>Query registered handler arg (0 if none).</summary>
    public uint GetIntrHandlerArg(int irq) =>
        IsValidIrq(irq) && _handlers[irq] != null ? _handlers[irq]!.Arg : 0;

    /// <summary>True when RaiseIntr marked the line pending and AcknowledgeIntr has not cleared it.</summary>
    public bool IsIntrPending(int irq) =>
        IsValidIrq(irq) && _pending[irq];

    /// <summary>True when soft dispatch mask allows the line (EnableDispatchIntr default).</summary>
    public bool IsDispatchEnabled(int irq) =>
        IsValidIrq(irq) && _dispatchEnabled[irq];

    /// <summary>
    /// Full query status for an IRQ line — packs handler presence, enable, pending, and dispatch
    /// into a bitfield used by smokes / diagnostics (not a retail export).
    /// Bit0=has handler, bit1=enabled, bit2=pending, bit3=dispatch enabled.
    /// </summary>
    public int QueryIntrStatus(int irq)
    {
        if (!IsValidIrq(irq)) return ResultIllegalIntrCode;
        int s = 0;
        if (_handlers[irq] != null) s |= 1;
        if (_enabled[irq]) s |= 2;
        if (_pending[irq]) s |= 4;
        if (_dispatchEnabled[irq]) s |= 8;
        return s;
    }

    /// <summary>Software dispatch mask (DECI2 DisableDispatchIntr / EnableDispatchIntr).</summary>
    public void DisableDispatchIntr(int irq)
    {
        if (IsValidIrq(irq)) _dispatchEnabled[irq] = false;
    }

    public void EnableDispatchIntr(int irq)
    {
        if (IsValidIrq(irq)) _dispatchEnabled[irq] = true;
    }

    /// <summary>
    /// Raise an IOP IRQ line for HLE bookkeeping. Marks pending and counts a raise when the
    /// line is enabled, CPU interrupts are on, dispatch is enabled, and a handler is registered.
    /// Does not execute R3000 code.
    /// </summary>
    public int RaiseIntr(int irq)
    {
        if (!IsValidIrq(irq)) return ResultIllegalIntrCode;
        if (!_enabled[irq] || !_dispatchEnabled[irq] || !CpuInterruptsEnabled)
            return ResultIntrDisable;
        if (_handlers[irq] == null) return ResultNotFoundHandler;
        _pending[irq] = true;
        _intrRaises++;
        return ResultOk;
    }

    /// <summary>
    /// Acknowledge (clear pending bookkeeping) for an IOP IRQ after handler "ran".
    /// Always succeeds for a valid irq (real ICR clear is unconditional).
    /// </summary>
    public int AcknowledgeIntr(int irq)
    {
        if (!IsValidIrq(irq)) return ResultIllegalIntrCode;
        _pending[irq] = false;
        _intrAcks++;
        return ResultOk;
    }

    /// <summary>
    /// Pulse IOP VBLANK + EVBLANK lines if enabled (called from EE PCRTC edge via BiosHle).
    /// Real VBLANK.IRX handlers own these IRQs after _start. Marks pending on each raised line.
    /// </summary>
    public void OnVblankIrqPulse()
    {
        if (_enabled[IrqVblank] && _handlers[IrqVblank] != null && _dispatchEnabled[IrqVblank] && CpuInterruptsEnabled)
        {
            _pending[IrqVblank] = true;
            _intrRaises++;
        }
        if (_enabled[IrqEvblank] && _handlers[IrqEvblank] != null && _dispatchEnabled[IrqEvblank] && CpuInterruptsEnabled)
        {
            _pending[IrqEvblank] = true;
            _intrRaises++;
        }
    }

    /// <summary>CpuDisableIntr — force IE off (deprecated API, still exported).</summary>
    public int CpuDisableIntr()
    {
        _cpuIntrEnabled = false;
        return ResultOk;
    }

    /// <summary>CpuEnableIntr — force IE on.</summary>
    public int CpuEnableIntr()
    {
        _cpuIntrEnabled = true;
        return ResultOk;
    }

    /// <summary>CpuSuspendIntr — nestable; returns previous state in <paramref name="state"/> (1=enabled).</summary>
    public int CpuSuspendIntr(out int state)
    {
        state = CpuInterruptsEnabled ? 1 : 0;
        _cpuIntrSuspendDepth++;
        return ResultOk;
    }

    /// <summary>CpuResumeIntr — restore from matching Suspend.</summary>
    public int CpuResumeIntr(int state)
    {
        if (_cpuIntrSuspendDepth > 0)
            _cpuIntrSuspendDepth--;
        // Real API restores the saved state rather than blindly enabling.
        if (state != 0)
            _cpuIntrEnabled = true;
        else if (_cpuIntrSuspendDepth == 0)
            _cpuIntrEnabled = false;
        return ResultOk;
    }

    /// <summary>QueryIntrContext — 1 if inside IRQ, 0 otherwise.</summary>
    public int QueryIntrContext() => _intrContext ? 1 : 0;

    // -------------------- TIMEMAN / thbase system clock --------------------

    /// <summary>TIMEMAN / thbase GetSystemTime — returns 64-bit SysClock ticks.</summary>
    public ulong GetSystemTime() => _systemClock;

    /// <summary>
    /// thbase GetSystemTime into <c>iop_sys_clock_t { u32 lo, hi }</c> (ps2sdk thbase.h).
    /// Writes 8 bytes at <paramref name="outAddr"/> when non-zero (via <paramref name="mem"/>).
    /// </summary>
    public int GetSystemTimeStruct(SystemMemory? mem, uint outAddr)
    {
        if (mem != null && outAddr != 0)
        {
            mem.Write32(outAddr + 0, (uint)_systemClock);
            mem.Write32(outAddr + 4, (uint)(_systemClock >> 32));
        }
        return ResultOk;
    }

    /// <summary>Split current clock into lo/hi words without memory.</summary>
    public void GetSystemTimeParts(out uint lo, out uint hi)
    {
        lo = (uint)_systemClock;
        hi = (uint)(_systemClock >> 32);
    }

    /// <summary>
    /// Advance TIMEMAN clock. <paramref name="amount"/> is SysClock ticks when &gt; 1;
    /// amount==1 is treated as one EE VBlank edge (advances <see cref="SysClockPerVblank"/>)
    /// so legacy <c>Tick(1)</c> from BiosHle.OnVblank stays meaningful for alarms.
    /// Pass <paramref name="rawTicks"/> true to force literal tick count.
    /// </summary>
    public void Tick(ulong amount = 1, bool rawTicks = false)
    {
        ulong step;
        if (rawTicks)
            step = amount == 0 ? 1UL : amount;
        else if (amount == 1)
            step = SysClockPerVblank;
        else
            step = amount == 0 ? 1UL : amount;

        _systemClock += step;

        // Advance allocated hard-timer counters (free-running HLE; no MMIO).
        // When a started timer with SetTimerHandler crosses Compare, mark time-up and
        // bookkeeping-RaiseIntr on the timer's IRQ line (if registered+enabled).
        foreach (var t in _hardTimers)
        {
            if (t.Users == 0 || t.Mode == 0) continue;
            uint mask = t.WidthBits == 16 ? 0xFFFFu : 0xFFFFFFFFu;
            uint prev = t.Counter;
            uint delta = (uint)Math.Min(step, mask);
            t.Counter = (t.Counter + delta) & mask;

            // Compare-match: prev < compare <= new (or wrap past compare).
            if (t.CompareCallback != 0 && t.Compare != 0)
            {
                bool hit;
                if (prev <= t.Counter)
                    hit = prev < t.Compare && t.Counter >= t.Compare;
                else
                    // wrapped
                    hit = prev < t.Compare || t.Counter >= t.Compare;
                if (hit)
                {
                    t.TimeupFlags |= 1;
                    _hardTimerCompareHits++;
                    // Wire to INTRMAN: RaiseIntr on timer IRQ if a handler owns the line.
                    int irq = t.Irq;
                    if (IsValidIrq(irq) && _enabled[irq] && _handlers[irq] != null &&
                        _dispatchEnabled[irq] && CpuInterruptsEnabled)
                    {
                        _pending[irq] = true;
                        _intrRaises++;
                    }
                }
            }

            // Overflow: counter wrapped (prev > new after add without compare hit requirement).
            if (t.OverflowCallback != 0 && prev > t.Counter && delta > 0)
            {
                t.OverflowFlags |= 1;
                int irq = t.Irq;
                if (IsValidIrq(irq) && _enabled[irq] && _handlers[irq] != null &&
                    _dispatchEnabled[irq] && CpuInterruptsEnabled)
                {
                    _pending[irq] = true;
                    _intrRaises++;
                }
            }
        }

        // Fire due alarms (callback not R3000-executed — count only).
        for (int i = _alarms.Count - 1; i >= 0; i--)
        {
            if (_alarms[i].FireAt > _systemClock) continue;
            if (!_alarms[i].Fired)
            {
                _alarms[i].Fired = true;
                _alarmFires++;
            }
            _alarms.RemoveAt(i);
        }
    }

    /// <summary>
    /// thbase SetAlarm(iop_sys_clock_t *delta, cb, arg) — schedule relative to current SysClock.
    /// Duplicate (cb, arg) → KE_FOUND_HANDLER. Callback not R3000-executed.
    /// </summary>
    public int SetAlarmSysClock(ulong deltaTicks, uint callback, uint arg)
    {
        if (_intrContext) return ResultIllegalContext;
        // Real SetAlarm rejects duplicate (cb, arg) pairs.
        foreach (var a in _alarms)
        {
            if (a.Callback == callback && a.Arg == arg)
                return ResultFoundHandler;
        }
        if (callback == 0) return ResultIllegalMode;

        ulong delta = deltaTicks == 0 ? 1UL : deltaTicks;
        _alarmAccepts++;
        _alarms.Add(new AlarmEntry
        {
            FireAt = _systemClock + delta,
            Callback = callback,
            Arg = arg
        });
        return ResultOk;
    }

    /// <summary>
    /// TIMEMAN / legacy SetAlarm(time, callback, arg) — <paramref name="time"/> is a relative
    /// SysClock delta (same units as <see cref="GetSystemTime"/>).
    /// </summary>
    public int SetAlarm(uint time, uint callback, uint arg) =>
        SetAlarmSysClock(time == 0 ? 1UL : time, callback, arg);

    /// <summary>iSetAlarm — same as SetAlarm but requires interrupt context.</summary>
    public int ISetAlarm(uint time, uint callback, uint arg)
    {
        if (!_intrContext) return ResultIllegalContext;
        // Temporarily clear so SetAlarmSysClock's context check passes; restore after.
        // Real iSetAlarm requires IRQ context; we already verified.
        bool prev = _intrContext;
        _intrContext = false;
        try { return SetAlarmSysClock(time == 0 ? 1UL : time, callback, arg); }
        finally { _intrContext = prev; }
    }

    /// <summary>
    /// thbase CancelAlarm(cb, arg). Real returns KE_NOTFOUND_HANDLER when no match.
    /// When <paramref name="arg"/> is 0, cancels all alarms with matching callback (HLE convenience
    /// retained for older call sites that only knew the cb).
    /// </summary>
    public int CancelAlarm(uint callback, uint arg)
    {
        if (_intrContext) return ResultIllegalContext;
        bool found = false;
        for (int i = _alarms.Count - 1; i >= 0; i--)
        {
            if (_alarms[i].Callback == callback && (arg == 0 || _alarms[i].Arg == arg))
            {
                _alarms.RemoveAt(i);
                found = true;
            }
        }
        return found ? ResultOk : ResultNotFoundHandler;
    }

    /// <summary>iCancelAlarm — IRQ-context CancelAlarm.</summary>
    public int ICancelAlarm(uint callback, uint arg)
    {
        if (!_intrContext) return ResultIllegalContext;
        bool found = false;
        for (int i = _alarms.Count - 1; i >= 0; i--)
        {
            if (_alarms[i].Callback == callback && (arg == 0 || _alarms[i].Arg == arg))
            {
                _alarms.RemoveAt(i);
                found = true;
            }
        }
        return found ? ResultOk : ResultNotFoundHandler;
    }

    public int PendingAlarms => _alarms.Count;

    /// <summary>
    /// thbase USec2SysClock — convert microseconds to SysClock ticks
    /// (mul=36864, div=1000 ≈ 36.864 MHz).
    /// </summary>
    public static ulong USec2SysClock(uint usec)
    {
        // (usec * SysClockMul) / SysClockDiv using 64-bit math
        return (ulong)usec * SysClockMul / SysClockDiv;
    }

    /// <summary>thbase SysClock2USec — inverse of <see cref="USec2SysClock"/>.</summary>
    public static void SysClock2USec(ulong ticks, out uint sec, out uint usec)
    {
        // ticks * div / mul → total usec
        ulong totalUs = ticks * SysClockDiv / SysClockMul;
        sec = (uint)(totalUs / 1_000_000UL);
        usec = (uint)(totalUs % 1_000_000UL);
    }

    // -------------------- TIMEMAN hard timers (timrman) --------------------

    /// <summary>
    /// AllocHardTimer(source, size, prescale) — find free RTC matching source/width/prescale.
    /// Returns timid or KE_NO_TIMER / KE_ILLEGAL_CONTEXT.
    /// Allocation order matches ps2sdk sIndexMap preference (RTC2 first, then high timers, then 0/1).
    /// </summary>
    public int AllocHardTimer(int source, int size, int prescale)
    {
        if (_intrContext) return ResultIllegalContext;
        if (source == 0) return ResultIllegalSource;
        if (size != 16 && size != 32) return ResultIllegalMode;
        if (prescale <= 0) return ResultIllegalPrescale;

        // Preference order from ps2sdk: 2,5,4,3,0,1 (only indices that exist)
        int[] order = _hardTimers.Length >= 6
            ? new[] { 2, 5, 4, 3, 0, 1 }
            : new[] { 2, 0, 1 };

        foreach (int i in order)
        {
            if (i < 0 || i >= _hardTimers.Length) continue;
            var t = _hardTimers[i];
            if (t.Users != 0) continue;
            if ((t.Sources & source) == 0) continue;
            if (t.WidthBits != size) continue;
            if (t.MaxPrescale < prescale) continue;

            t.Users = 1;
            t.Mode = 0;
            t.CtrlConfig = 0;
            t.TimeupFlags = 0;
            t.OverflowFlags = 0;
            t.Counter = 0;
            t.Compare = 0;
            t.HasIrqHandler = false;
            t.CompareCallback = 0;
            t.CompareCallbackArg = 0;
            t.OverflowCallback = 0;
            t.OverflowCallbackArg = 0;
            _hardTimerAllocs++;
            return EncodeTimid(t.Index, t.CountAddr);
        }
        return ResultNoTimer;
    }

    /// <summary>
    /// ReferHardTimer — increment users on an already-configured timer matching source/size/mode.
    /// </summary>
    public int ReferHardTimer(int source, int size, int mode, int modemask)
    {
        if (_intrContext) return ResultIllegalContext;
        for (int i = 0; i < _hardTimers.Length; i++)
        {
            var t = _hardTimers[i];
            if (t.Users == 0 || t.Mode == 0) continue;
            if ((t.Sources & source) == 0) continue;
            if (t.WidthBits != size) continue;
            if ((t.Mode & (uint)modemask) != (uint)mode) continue;
            t.Users++;
            return EncodeTimid(t.Index, t.CountAddr);
        }
        return ResultNoTimer;
    }

    /// <summary>FreeHardTimer — drop one user; fully free when refcount hits 0.</summary>
    public int FreeHardTimer(int timid)
    {
        if (_intrContext) return ResultIllegalContext;
        if (!TryGetHardTimer(timid, out var t)) return ResultIllegalTimerId;
        if (t!.Users == 0) return ResultIllegalTimerId;
        t.Users--;
        if (t.Users == 0)
        {
            t.Mode = 0;
            t.CtrlConfig = 0;
            t.TimeupFlags = 0;
            t.OverflowFlags = 0;
            t.HasIrqHandler = false;
            t.Counter = 0;
            t.Compare = 0;
            t.CompareCallback = 0;
            t.CompareCallbackArg = 0;
            t.OverflowCallback = 0;
            t.OverflowCallbackArg = 0;
        }
        return ResultOk;
    }

    public void SetTimerMode(int timid, int mode)
    {
        if (!TryGetHardTimer(timid, out var t)) return;
        t!.Mode = (uint)mode;
    }

    public uint GetTimerMode(int timid)
    {
        if (!TryGetHardTimer(timid, out var t)) return unchecked((uint)ResultIllegalTimerId);
        return t!.Mode;
    }

    public uint GetTimerStatus(int timid) => GetTimerMode(timid);

    public void SetTimerCounter(int timid, uint count)
    {
        if (!TryGetHardTimer(timid, out var t)) return;
        uint mask = t!.WidthBits == 16 ? 0xFFFFu : 0xFFFFFFFFu;
        t.Counter = count & mask;
    }

    public uint GetTimerCounter(int timid)
    {
        if (!TryGetHardTimer(timid, out var t)) return 0;
        return t!.Counter;
    }

    public void SetTimerCompare(int timid, uint compare)
    {
        if (!TryGetHardTimer(timid, out var t)) return;
        uint mask = t!.WidthBits == 16 ? 0xFFFFu : 0xFFFFFFFFu;
        t.Compare = compare & mask;
    }

    public uint GetTimerCompare(int timid)
    {
        if (!TryGetHardTimer(timid, out var t)) return 0;
        return t!.Compare;
    }

    /// <summary>GetHardTimerIntrCode — IOP IRQ line for this timer (PADMAN uses RTC0/1 = IRQ 4/5).</summary>
    public int GetHardTimerIntrCode(int timid)
    {
        if (!TryGetHardTimer(timid, out var t)) return ResultIllegalTimerId;
        return t!.Irq;
    }

    /// <summary>
    /// SetupHardTimer(timid, source, mode, prescale) — bookkeeping-only config (no MMIO).
    /// </summary>
    public int SetupHardTimer(int timid, int source, int mode, int prescale)
    {
        if (_intrContext) return ResultIllegalContext;
        if (!TryGetHardTimer(timid, out var t)) return ResultIllegalTimerId;
        if (t!.Mode != 0) return ResultTimerBusy;
        if ((t.Sources & source) == 0) return ResultIllegalSource;
        if (prescale != 1 && prescale != 8 && prescale != 16 && prescale != 256)
            return ResultIllegalPrescale;
        if (prescale > t.MaxPrescale) return ResultIllegalPrescale;
        switch (mode)
        {
            case 0: case 1: case 3: case 5: case 7: break;
            default: return ResultIllegalMode;
        }
        // Stash config; StartHardTimer promotes it to Mode.
        t.CtrlConfig = (uint)mode | 0x80000000u;
        if (source == TcPixel || source == TcHLine)
            t.CtrlConfig |= 0x100; // TMR_CTRL_EXT_SIGNAL
        if (prescale == 8) t.CtrlConfig |= t.Index >= 3 ? 0x2000u : 0x200u;
        else if (prescale == 16) t.CtrlConfig |= 0x4000u;
        else if (prescale == 256) t.CtrlConfig |= 0x6000u;
        t.HasIrqHandler = true;
        return ResultOk;
    }

    public int StartHardTimer(int timid)
    {
        if (!TryGetHardTimer(timid, out var t)) return ResultIllegalTimerId;
        if (t!.Mode != 0) return ResultTimerBusy;
        // ps2sdk StartHardTimer returns -155 when ctrl_config==0 (not set up).
        // Note: the recreation has a suspicious inverted check; we treat zero as not-setup.
        if (t.CtrlConfig == 0) return ResultTimerNotSetup;
        t.Mode = t.CtrlConfig | t.TimeupFlags | t.OverflowFlags;
        return ResultOk;
    }

    public int StopHardTimer(int timid)
    {
        if (!TryGetHardTimer(timid, out var t)) return ResultIllegalTimerId;
        if (t!.Mode == 0) return ResultTimerNotInUse;
        t.Mode = 0;
        return ResultOk;
    }

    /// <summary>
    /// timrman SetTimerHandler(timid, compare, cb, arg) — program compare + compare-match
    /// callback. On Tick, when the free-running counter crosses <paramref name="compare"/>,
    /// HLE counts a hit and bookkeeping-raises the timer's INTRMAN IRQ if a handler is
    /// registered+enabled for that line. Callback is not R3000-executed.
    /// </summary>
    public int SetTimerHandler(int timid, uint compare, uint callback, uint arg)
    {
        if (_intrContext) return ResultIllegalContext;
        if (!TryGetHardTimer(timid, out var t)) return ResultIllegalTimerId;
        if (t!.Users == 0) return ResultTimerNotInUse;
        uint mask = t.WidthBits == 16 ? 0xFFFFu : 0xFFFFFFFFu;
        t.Compare = compare & mask;
        t.CompareCallback = callback;
        t.CompareCallbackArg = arg;
        t.HasIrqHandler = callback != 0;
        t.TimeupFlags = 0;
        return ResultOk;
    }

    /// <summary>
    /// timrman SetOverflowHandler(timid, cb, arg) — fire on counter wrap. Same INTRMAN
    /// bookkeeping raise path as <see cref="SetTimerHandler"/>.
    /// </summary>
    public int SetOverflowHandler(int timid, uint callback, uint arg)
    {
        if (_intrContext) return ResultIllegalContext;
        if (!TryGetHardTimer(timid, out var t)) return ResultIllegalTimerId;
        if (t!.Users == 0) return ResultTimerNotInUse;
        t.OverflowCallback = callback;
        t.OverflowCallbackArg = arg;
        t.HasIrqHandler = t.HasIrqHandler || callback != 0;
        t.OverflowFlags = 0;
        return ResultOk;
    }

    /// <summary>Read latched time-up flag (compare match since last clear / SetTimerHandler).</summary>
    public uint GetTimerTimeupFlags(int timid)
    {
        if (!TryGetHardTimer(timid, out var t) || t!.Users == 0) return 0;
        return t.TimeupFlags;
    }

    /// <summary>Clear latched time-up flag after handler service.</summary>
    public void ClearTimerTimeupFlags(int timid)
    {
        if (!TryGetHardTimer(timid, out var t) || t == null) return;
        t.TimeupFlags = 0;
    }

    private bool TryGetHardTimer(int timid, out HardTimer? timer)
    {
        timer = null;
        int idx = DecodeTimerIndex(timid);
        if (idx < 0 || idx >= _hardTimers.Length) return false;
        // timid low bits must match the slot's count address encoding.
        int expected = EncodeTimid(idx, _hardTimers[idx].CountAddr);
        if (timid != expected) return false;
        timer = _hardTimers[idx];
        return true;
    }

    /// <summary>
    /// Install IOPBTCONF-critical device names into IOMAN table (simulates drivers'
    /// AddDrv during boot). Idempotent; uses base names + common unit aliases.
    /// </summary>
    public void InstallBiosDevices()
    {
        // Base names first (FUN_00000d28 strips unit digits → these).
        foreach (var d in new[]
                 {
                     "rom", "cdrom", "host", "mc", "mass", "hdd", "pfs",
                     "tty", "stderr", "dvrhome", "dev"
                 })
            RegisterDevice(d, d, type: 0x10, version: 1, builtin: true);
        // Unit aliases still accepted by HasDevice / some openers that look up full token.
        foreach (var d in new[]
                 {
                     "rom0", "rom1", "cdrom0", "cdrom1", "host0",
                     "mc0", "mc1", "hdd0", "pfs0", "tty00"
                 })
            RegisterDevice(d, d, type: 0x10, version: 1, builtin: true);
        EnsureStdioDevices();
    }
}
