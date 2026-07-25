using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Extended kernel HLE state (Phase 14): threads, semaphores, event flags, VSync wait.
/// Integrated into <see cref="BiosHle"/>.
/// </summary>
public sealed class KernelState
{
    public sealed class Thread
    {
        public int Id;
        public bool Alive;
        public bool Sleeping;
        public bool WaitVblank;
        public bool Started;
        public int WaitSemaId;
        public uint Entry;
        public uint Gp;
        public uint Stack;
        /// <summary>Saved PC when switched out (0 = never run / use Entry).</summary>
        public ulong SavedPc;
        public ulong SavedSp;
        public ulong SavedGp;
        public ulong SavedRa;
        public ulong SavedS0, SavedS1, SavedS2, SavedS3, SavedS4, SavedS5, SavedS6, SavedS7, SavedS8;
        public ulong SavedFp;
        /// <summary>Argument passed to StartThread (becomes $a0 on first entry).</summary>
        public ulong StartArg;
        public bool FreshStart;
    }

    public sealed class Sema
    {
        public int Id;
        public int Count;
        public int MaxCount;
    }

    public sealed class EventFlag
    {
        public int Id;
        public uint Bits;
    }

    private readonly List<Thread> _threads = new();
    private readonly Dictionary<int, Sema> _semas = new();
    private readonly Dictionary<int, EventFlag> _flags = new();
    private int _nextTid = 1;
    private int _nextSema = 1;
    private int _nextEf = 1;
    private int _currentTid = 1;

    public bool WaitingVblank { get; private set; }
    public ulong VblankWaits { get; private set; }
    public int ThreadCount => _threads.Count;
    public int CurrentThreadId => _currentTid;

    public void Reset()
    {
        _threads.Clear();
        _semas.Clear();
        _flags.Clear();
        _nextTid = 1;
        _nextSema = 1;
        _nextEf = 1;
        _currentTid = 1;
        WaitingVblank = false;
        VblankWaits = 0;
        // Main thread — already running
        _threads.Add(new Thread { Id = 1, Alive = true, Started = true, Entry = 0 });
    }

    public int CreateThread(uint entry, uint gp, uint stack)
    {
        int id = ++_nextTid;
        _threads.Add(new Thread
        {
            Id = id,
            Alive = true,
            Started = false,
            Sleeping = true, // not runnable until StartThread
            Entry = entry,
            Gp = gp,
            Stack = stack,
            SavedPc = entry,
            SavedSp = stack,
            SavedGp = gp
        });
        return id;
    }

    public int DeleteThread(int id)
    {
        var t = FindThread(id);
        if (t == null) return -1;
        t.Alive = false;
        return 0;
    }

    public int StartThread(int id, ulong arg = 0)
    {
        var t = FindThread(id);
        if (t == null || !t.Alive) return -1;
        t.Sleeping = false;
        t.WaitVblank = false;
        t.Started = true;
        t.StartArg = arg;
        t.FreshStart = true;
        t.SavedPc = t.Entry;
        if (t.SavedSp == 0)
            t.SavedSp = t.Stack != 0 ? t.Stack : 0x01F00000u - (uint)(id * 0x10000);
        return 0;
    }

    public int SleepThread()
    {
        var t = FindThread(_currentTid);
        if (t != null) t.Sleeping = true;
        return 0;
    }

    public int WakeupThread(int id)
    {
        var t = FindThread(id);
        if (t == null) return -1;
        t.Sleeping = false;
        t.WaitVblank = false;
        return 0;
    }

    public int RotateThread()
    {
        // Cooperative: pick next alive non-sleeping
        int start = _currentTid;
        for (int i = 0; i < _threads.Count; i++)
        {
            var t = _threads[(i + 1) % _threads.Count];
            if (t.Alive && !t.Sleeping && !t.WaitVblank)
            {
                _currentTid = t.Id;
                return 0;
            }
        }
        _currentTid = start;
        return 0;
    }

    /// <summary>Find next runnable thread id, or current if none.</summary>
    public int FindNextRunnable(int afterId)
    {
        int idx = 0;
        for (int i = 0; i < _threads.Count; i++)
            if (_threads[i].Id == afterId) { idx = i; break; }
        for (int i = 1; i <= _threads.Count; i++)
        {
            var t = _threads[(idx + i) % _threads.Count];
            if (t.Alive && t.Started && !t.Sleeping && !t.WaitVblank)
                return t.Id;
        }
        // Also allow main thread (id 1) even if Started flag never set
        var main = FindThread(1);
        if (main != null && main.Alive && !main.Sleeping)
            return 1;
        return afterId;
    }

    public Thread? GetThread(int id) => FindThread(id);

    /// <summary>Save minimal context from EE into the current thread slot.</summary>
    /// <param name="fromSyscall">When true, resume at PC+4 (skip SYSCALL insn).</param>
    public void SaveCurrentContext(EmotionEngine ee, bool fromSyscall = true)
    {
        var t = FindThread(_currentTid);
        if (t == null) return;
        // From SYSCALL: PC is the SYSCALL insn → resume after it.
        // From preemptive yield: PC is the next insn to run → keep as-is.
        t.SavedPc = fromSyscall ? ee.PC + 4 : ee.PC;
        t.SavedSp = ee.GetGpr(29).Lo;
        t.SavedGp = ee.GetGpr(28).Lo;
        t.SavedRa = ee.GetGpr(31).Lo;
        t.SavedS0 = ee.GetGpr(16).Lo;
        t.SavedS1 = ee.GetGpr(17).Lo;
        t.SavedS2 = ee.GetGpr(18).Lo;
        t.SavedS3 = ee.GetGpr(19).Lo;
        t.SavedS4 = ee.GetGpr(20).Lo;
        t.SavedS5 = ee.GetGpr(21).Lo;
        t.SavedS6 = ee.GetGpr(22).Lo;
        t.SavedS7 = ee.GetGpr(23).Lo;
        t.SavedS8 = ee.GetGpr(30).Lo;
        t.SavedFp = ee.GetGpr(30).Lo;
    }

    /// <summary>Switch EE execution to thread id (assumes SaveCurrentContext already done if needed).</summary>
    /// <param name="fromSyscall">When true, use HleRedirectPc (skips post-SYSCALL PC+=4). When false, set PC directly.</param>
    public bool RestoreContext(EmotionEngine ee, int id, bool fromSyscall = true)
    {
        var t = FindThread(id);
        if (t == null || !t.Alive) return false;
        _currentTid = id;
        ulong pc = t.SavedPc != 0 ? t.SavedPc : t.Entry;
        if (pc == 0) return false;
        if (fromSyscall)
            ee.HleRedirectPc = pc; // bypass PC+=4 after SYSCALL
        else
            ee.PC = pc;
        if (t.SavedSp != 0)
            ee.SetGpr(29, new EmotionEngine.Gpr128 { Lo = t.SavedSp });
        if (t.SavedGp != 0)
            ee.SetGpr(28, new EmotionEngine.Gpr128 { Lo = t.SavedGp });
        if (t.FreshStart)
        {
            // First entry: $a0 = StartThread arg, $ra = 0 so ExitThread path is clean
            ee.SetGpr(4, new EmotionEngine.Gpr128 { Lo = t.StartArg });
            ee.SetGpr(31, new EmotionEngine.Gpr128 { Lo = 0 });
            t.FreshStart = false;
        }
        else
        {
            ee.SetGpr(31, new EmotionEngine.Gpr128 { Lo = t.SavedRa });
            ee.SetGpr(16, new EmotionEngine.Gpr128 { Lo = t.SavedS0 });
            ee.SetGpr(17, new EmotionEngine.Gpr128 { Lo = t.SavedS1 });
            ee.SetGpr(18, new EmotionEngine.Gpr128 { Lo = t.SavedS2 });
            ee.SetGpr(19, new EmotionEngine.Gpr128 { Lo = t.SavedS3 });
            ee.SetGpr(20, new EmotionEngine.Gpr128 { Lo = t.SavedS4 });
            ee.SetGpr(21, new EmotionEngine.Gpr128 { Lo = t.SavedS5 });
            ee.SetGpr(22, new EmotionEngine.Gpr128 { Lo = t.SavedS6 });
            ee.SetGpr(23, new EmotionEngine.Gpr128 { Lo = t.SavedS7 });
            ee.SetGpr(30, new EmotionEngine.Gpr128 { Lo = t.SavedS8 != 0 ? t.SavedS8 : t.SavedFp });
        }
        t.Sleeping = false;
        t.Started = true;
        return true;
    }

    /// <summary>
    /// Cooperative switch: save current, pick next runnable, restore.
    /// Returns true if PC changed to a different thread.
    /// </summary>
    public bool SwitchToNext(EmotionEngine ee, bool fromSyscall = true)
    {
        SaveCurrentContext(ee, fromSyscall);
        int next = FindNextRunnable(_currentTid);
        if (next == _currentTid)
        {
            // Nobody else — wake ourselves if we were sleeping so boot doesn't freeze
            var cur = FindThread(_currentTid);
            if (cur != null && cur.Sleeping)
            {
                cur.Sleeping = false;
                cur.WaitSemaId = 0;
            }
            return false;
        }
        return RestoreContext(ee, next, fromSyscall);
    }

    /// <summary>Start thread and optionally switch to it immediately (first-run boost).</summary>
    public int StartAndMaybeSwitch(EmotionEngine ee, int id, bool switchNow, ulong arg = 0, bool fromSyscall = true)
    {
        int r = StartThread(id, arg);
        if (r < 0) return r;
        if (switchNow)
        {
            SaveCurrentContext(ee, fromSyscall);
            RestoreContext(ee, id, fromSyscall);
        }
        return 0;
    }

    /// <summary>If a non-current runnable worker exists, switch to it (commercial assist).</summary>
    public bool YieldToWorker(EmotionEngine ee)
    {
        int next = FindNextRunnable(_currentTid);
        if (next == _currentTid) return false;
        SaveCurrentContext(ee, fromSyscall: false);
        return RestoreContext(ee, next, fromSyscall: false);
    }

    /// <summary>Block until next VBlank (PCRTC). EE Step should stall while WaitingVblank.</summary>
    public int WaitSemaVblank()
    {
        WaitingVblank = true;
        VblankWaits++;
        var t = FindThread(_currentTid);
        if (t != null) t.WaitVblank = true;
        return 0;
    }

    public void OnVblank()
    {
        WaitingVblank = false;
        foreach (var t in _threads)
        {
            if (t.WaitVblank)
            {
                t.WaitVblank = false;
                t.Sleeping = false;
            }
        }
    }

    public int CreateSema(int init, int max)
    {
        int id = _nextSema++;
        _semas[id] = new Sema { Id = id, Count = init, MaxCount = max > 0 ? max : 1 };
        return id;
    }

    public int DeleteSema(int id) => _semas.Remove(id) ? 0 : -1;

    /// <summary>Non-mutating existence check — unlike WaitSemaBlocking, does not consume a count.</summary>
    public bool SemaExists(int id) => _semas.ContainsKey(id);

    public int SignalSema(int id)
    {
        if (!_semas.TryGetValue(id, out var s)) return -1;
        if (s.Count < s.MaxCount) s.Count++;
        // Wake one thread waiting on this sema
        foreach (var t in _threads)
        {
            if (t.Alive && t.Sleeping && t.WaitSemaId == id)
            {
                t.Sleeping = false;
                t.WaitSemaId = 0;
                break;
            }
        }
        return s.Count;
    }

    public int WaitSema(int id)
    {
        if (!_semas.TryGetValue(id, out var s)) return -1;
        if (s.Count > 0) { s.Count--; return s.Count; }
        // Block current thread until SignalSema (cooperative scheduler)
        var t = FindThread(_currentTid);
        if (t != null)
        {
            t.Sleeping = true;
            t.WaitSemaId = id;
        }
        return -2; // special: caller should SwitchToNext
    }

    /// <summary>True if last WaitSema blocked (count was 0).</summary>
    public bool LastWaitSemaBlocked { get; private set; }

    public int WaitSemaBlocking(int id)
    {
        LastWaitSemaBlocked = false;
        if (!_semas.TryGetValue(id, out var s)) return -1;
        if (s.Count > 0) { s.Count--; return s.Count; }
        LastWaitSemaBlocked = true;
        var t = FindThread(_currentTid);
        if (t != null)
        {
            t.Sleeping = true;
            t.WaitSemaId = id;
        }
        return -1;
    }

    public int CreateEventFlag(uint init)
    {
        int id = _nextEf++;
        _flags[id] = new EventFlag { Id = id, Bits = init };
        return id;
    }

    public int SetEventFlag(int id, uint bits)
    {
        if (!_flags.TryGetValue(id, out var f)) return -1;
        f.Bits |= bits;
        return 0;
    }

    public int ClearEventFlag(int id, uint bits)
    {
        if (!_flags.TryGetValue(id, out var f)) return -1;
        f.Bits &= ~bits;
        return 0;
    }

    public uint PollEventFlag(int id) =>
        _flags.TryGetValue(id, out var f) ? f.Bits : 0;

    private Thread? FindThread(int id)
    {
        foreach (var t in _threads)
            if (t.Id == id) return t;
        return null;
    }
}

public enum HleLevel
{
    Minimal = 0,
    Standard = 1,
    Full = 2
}
