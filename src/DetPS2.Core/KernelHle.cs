using System;
using System.Collections.Generic;
using System.IO;

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
        /// <summary>0 = not waiting on an event flag. See WaitEventFlag/SetEventFlag in
        /// SonyKernelHle.cs and KernelState.EventFlagSatisfied/ConsumeEventFlag/ParkOnEventFlag.</summary>
        public int WaitEfId;
        public uint WaitEfPattern;
        public uint WaitEfMode;
        public uint WaitEfResultAddr;
        public uint Entry;
        public uint Gp;
        public uint Stack;
        public uint StackSize;
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

        /// <summary>Full 32-GPR save for forced (non-cooperative) preemption — see
        /// <see cref="SaveFullContext"/>. SaveCurrentContext/RestoreContext only preserve the
        /// callee-saved set (sp/gp/ra/s0-s8) because every EXISTING caller switches only at a
        /// syscall boundary, where caller-saved registers (v0/v1/a0-a3/t0-t9/...) are already
        /// expected to be volatile by normal MIPS calling convention. A forced preemption can
        /// land literally anywhere — including mid-loop code actively using v0/v1 as a counter
        /// (the exact real scenario this exists for) — so it needs the full register file.</summary>
        public ulong[]? SavedGprFull;
        public bool HasFullSave;
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

    /// <summary>Diagnostic-only: a chronological log of every thread lifecycle/switch event —
    /// built specifically to stop misattributing a register/stack observation at some cycle to
    /// the wrong logical thread (the actual root cause of several false leads while tracing MK
    /// Shaolin Monks on 2026-07-26 — see DEVELOPER_GUIDE.md §7.4). Given ONLY a raw PC trace, two
    /// completely unrelated calls into the same shared library function are indistinguishable
    /// from one continuous call; this answers "which thread, with what stack/entry, was actually
    /// active at cycle N" directly, without re-deriving it from call-chain guesswork. Off by
    /// default; opt-in via blocker-trace --trace-threads. EmotionEngine.Step() stamps
    /// CurrentCycle before every instruction when this is on (see its own call site).</summary>
    public static bool TraceThreads;
    public static ulong CurrentCycle;
    public readonly record struct ThreadEvent(ulong Cycle, string Kind, int ThreadId, ulong Pc, ulong Sp, string Detail);
    public static readonly List<ThreadEvent> ThreadLog = new();

    private void LogThreadEvent(string kind, int tid, ulong pc, ulong sp, string detail = "")
    {
        if (!TraceThreads) return;
        ThreadLog.Add(new ThreadEvent(CurrentCycle, kind, tid, pc, sp, detail));
    }

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
        _cyclesSinceLastPreempt = 0;
        // Main thread — already running
        _threads.Add(new Thread { Id = 1, Alive = true, Started = true, Entry = 0 });
        LogThreadEvent("MainReset", 1, 0, 0);
    }

    /// <summary>Full thread/semaphore/event-flag state for SaveState.cs — a save/load mid-boot
    /// on a multi-thread title needs every thread's saved context (SwitchToNext/RestoreContext
    /// only resume a thread correctly if its SavedPc/SavedSp/... and Sleeping/WaitSemaId are
    /// intact) and every semaphore's live count, not just the currently-running thread's own
    /// register file (which is all the EE.PC/GPR fields SaveState.cs saved before this existed).
    /// Without this, loading a save mid-wait silently resumed with the wrong scheduler state —
    /// e.g. a thread genuinely WaitSema-blocked would come back Sleeping=false (a fabricated,
    /// wrong resume) since nothing recreated its wait.</summary>
    public void WriteState(BinaryWriter w)
    {
        w.Write(_nextTid);
        w.Write(_nextSema);
        w.Write(_nextEf);
        w.Write(_currentTid);
        w.Write(WaitingVblank);
        w.Write(VblankWaits);
        w.Write(_cyclesSinceLastPreempt);

        w.Write(_threads.Count);
        foreach (var t in _threads)
        {
            w.Write(t.Id);
            w.Write(t.Alive);
            w.Write(t.Sleeping);
            w.Write(t.WaitVblank);
            w.Write(t.Started);
            w.Write(t.WaitSemaId);
            w.Write(t.WaitEfId);
            w.Write(t.WaitEfPattern);
            w.Write(t.WaitEfMode);
            w.Write(t.WaitEfResultAddr);
            w.Write(t.Entry);
            w.Write(t.Gp);
            w.Write(t.Stack);
            w.Write(t.StackSize);
            w.Write(t.SavedPc);
            w.Write(t.SavedSp);
            w.Write(t.SavedGp);
            w.Write(t.SavedRa);
            w.Write(t.SavedS0); w.Write(t.SavedS1); w.Write(t.SavedS2); w.Write(t.SavedS3);
            w.Write(t.SavedS4); w.Write(t.SavedS5); w.Write(t.SavedS6); w.Write(t.SavedS7); w.Write(t.SavedS8);
            w.Write(t.SavedFp);
            w.Write(t.StartArg);
            w.Write(t.FreshStart);
            w.Write(t.HasFullSave);
            if (t.HasFullSave && t.SavedGprFull != null)
            {
                w.Write(t.SavedGprFull.Length);
                foreach (var v in t.SavedGprFull) w.Write(v);
            }
            else w.Write(0);
        }

        w.Write(_semas.Count);
        foreach (var kv in _semas)
        {
            w.Write(kv.Value.Id);
            w.Write(kv.Value.Count);
            w.Write(kv.Value.MaxCount);
        }

        w.Write(_flags.Count);
        foreach (var kv in _flags)
        {
            w.Write(kv.Value.Id);
            w.Write(kv.Value.Bits);
        }
    }

    public void ReadState(BinaryReader r)
    {
        _threads.Clear();
        _semas.Clear();
        _flags.Clear();

        _nextTid = r.ReadInt32();
        _nextSema = r.ReadInt32();
        _nextEf = r.ReadInt32();
        _currentTid = r.ReadInt32();
        WaitingVblank = r.ReadBoolean();
        VblankWaits = r.ReadUInt64();
        _cyclesSinceLastPreempt = r.ReadUInt64();

        int threadCount = r.ReadInt32();
        for (int i = 0; i < threadCount; i++)
        {
            var t = new Thread
            {
                Id = r.ReadInt32(),
                Alive = r.ReadBoolean(),
                Sleeping = r.ReadBoolean(),
                WaitVblank = r.ReadBoolean(),
                Started = r.ReadBoolean(),
                WaitSemaId = r.ReadInt32(),
                WaitEfId = r.ReadInt32(),
                WaitEfPattern = r.ReadUInt32(),
                WaitEfMode = r.ReadUInt32(),
                WaitEfResultAddr = r.ReadUInt32(),
                Entry = r.ReadUInt32(),
                Gp = r.ReadUInt32(),
                Stack = r.ReadUInt32(),
                StackSize = r.ReadUInt32(),
                SavedPc = r.ReadUInt64(),
                SavedSp = r.ReadUInt64(),
                SavedGp = r.ReadUInt64(),
                SavedRa = r.ReadUInt64(),
                SavedS0 = r.ReadUInt64(), SavedS1 = r.ReadUInt64(), SavedS2 = r.ReadUInt64(), SavedS3 = r.ReadUInt64(),
                SavedS4 = r.ReadUInt64(), SavedS5 = r.ReadUInt64(), SavedS6 = r.ReadUInt64(), SavedS7 = r.ReadUInt64(), SavedS8 = r.ReadUInt64(),
                SavedFp = r.ReadUInt64(),
                StartArg = r.ReadUInt64(),
                FreshStart = r.ReadBoolean(),
                HasFullSave = r.ReadBoolean()
            };
            int fullLen = r.ReadInt32();
            if (fullLen > 0)
            {
                t.SavedGprFull = new ulong[fullLen];
                for (int j = 0; j < fullLen; j++) t.SavedGprFull[j] = r.ReadUInt64();
            }
            _threads.Add(t);
        }

        int semaCount = r.ReadInt32();
        for (int i = 0; i < semaCount; i++)
        {
            var s = new Sema { Id = r.ReadInt32(), Count = r.ReadInt32(), MaxCount = r.ReadInt32() };
            _semas[s.Id] = s;
        }

        int flagCount = r.ReadInt32();
        for (int i = 0; i < flagCount; i++)
        {
            var f = new EventFlag { Id = r.ReadInt32(), Bits = r.ReadUInt32() };
            _flags[f.Id] = f;
        }
    }

    public int CreateThread(uint entry, uint gp, uint stack, uint stackSize = 0)
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
            StackSize = stackSize,
            SavedPc = entry,
            SavedSp = stack,
            SavedGp = gp
        });
        LogThreadEvent("Create", id, entry, stack, $"gp=0x{gp:X8} stackSize=0x{stackSize:X}");
        return id;
    }

    public int DeleteThread(int id)
    {
        var t = FindThread(id);
        if (t == null) return -1;
        t.Alive = false;
        LogThreadEvent("Delete", id, t.SavedPc, t.SavedSp);
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
        LogThreadEvent("Start", id, t.SavedPc, t.SavedSp, $"arg=0x{arg:X}");
        return 0;
    }

    public int SleepThread()
    {
        var t = FindThread(_currentTid);
        if (t != null) t.Sleeping = true;
        return 0;
    }

    /// <summary>Real ExitThread/ExitDeleteThread semantics: the thread is done forever, back to
    /// the same DORMANT state it was in before StartThread (ReferThreadStatus already reports
    /// !Started as DORMANT — this makes that true again). Distinct from the plain SleepThread()
    /// above (used for the real SleepThread syscall, a legitimate voluntary nap a thread expects
    /// to be woken back up from) specifically to also clear WaitSemaId: a thread can exit while
    /// still carrying a stale WaitSemaId from an EARLIER wait it already returned from, and
    /// MidwayBootAssist.MaybeUnblockStarvedSema's starved-semaphore rescue (or any other future
    /// SignalSema caller) matches purely on "Sleeping && WaitSemaId == id" with no way to tell an
    /// exited thread from a genuinely-still-waiting one — confirmed exactly this: a worker thread
    /// that had legitimately finished (called ExitThread once already) kept getting resurrected
    /// every ~2M-cycle grace period because its stale WaitSemaId still matched, immediately
    /// re-running its own tail and calling ExitThread again each time (261 calls by 100M cycles
    /// in one trace, pure wasted scheduling churn since its real work was already done).</summary>
    public void ExitCurrentThread()
    {
        var t = FindThread(_currentTid);
        if (t == null) return;
        t.Sleeping = true;
        t.Started = false;
        t.WaitSemaId = 0;
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
        // i < Count (not <=): must NOT wrap around to re-check afterId itself. With <=, the last
        // iteration lands back on (idx + Count) % Count == idx, i.e. the calling thread — which
        // trivially satisfies its own Alive/Started/!Sleeping check (it's the one currently
        // running), so it got returned as "the next runnable thread" before ever reaching the
        // main-thread special case below. SwitchToNext then sees next==afterId and concludes
        // "nobody else runnable," permanently starving thread 1 (whose Started flag is never set,
        // since it's the primordial thread and never goes through StartThread) any time the
        // current thread happened to satisfy its own criteria — confirmed live (2026-07-27):
        // thread 2's own dispatch loop never yielded back to thread 1 once it stopped needing to
        // genuinely block, even though thread 1 was Alive and !Sleeping the whole time.
        for (int i = 1; i < _threads.Count; i++)
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
    public IReadOnlyList<Thread> AllThreads => _threads;

    /// <summary>Save minimal context from EE into the current thread slot.</summary>
    /// <param name="fromSyscall">When true, resume at PC+4 (skip SYSCALL insn).</param>
    public void SaveCurrentContext(EmotionEngine ee, bool fromSyscall = true)
    {
        var t = FindThread(_currentTid);
        if (t == null) return;
        // From SYSCALL: PC is the SYSCALL insn → resume after it.
        // From preemptive yield: PC is the next insn to run → keep as-is.
        t.SavedPc = fromSyscall ? ee.PC + 4 : ee.PC;
        // Invalidate any older full-preemption snapshot (see SaveFullContext's doc comment for
        // the matching half of this fix): once this thread has taken a normal cooperative save,
        // that snapshot's caller-saved registers (v0/v1/a0-a3/t0-t9) are stale relative to
        // whatever this save just captured. A later MaybePreempt on THIS thread always calls
        // SaveFullContext fresh before anyone restores it, but a later RestoreFullContext call
        // could otherwise resurrect the old SavedGprFull array instead of the fields just written
        // below -- clearing the flag forces it to fall back to these (always current) fields.
        t.HasFullSave = false;
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
        LogThreadEvent("SaveOut", _currentTid, t.SavedPc, t.SavedSp, fromSyscall ? "fromSyscall" : "cooperative");
    }

    /// <summary>Switch EE execution to thread id (assumes SaveCurrentContext already done if needed).</summary>
    /// <param name="fromSyscall">When true, use HleRedirectPc (skips post-SYSCALL PC+=4). When false, set PC directly.</param>
    public bool RestoreContext(EmotionEngine ee, int id, bool fromSyscall = true)
    {
        var t = FindThread(id);
        if (t == null || !t.Alive) return false;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RESTORE1") == "1" && id == 1)
            Console.Error.WriteLine($"[RESTORE1] cyc={CurrentCycle} wasStarted={t.Started} wasSleeping={t.Sleeping} savedPc=0x{t.SavedPc:X8}");
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
        LogThreadEvent("SwitchTo", id, pc, t.SavedSp, fromSyscall ? "fromSyscall" : "cooperative");
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
            // Nobody else — wake ourselves if we were sleeping so boot doesn't freeze. But only
            // for a genuine temporary wait (Started stays true): ExitCurrentThread deliberately
            // sets Started=false for a real ExitThread/ExitDeleteThread, and that's permanent by
            // design — reviving it here defeats the whole point and creates exactly the bug this
            // guard is now written to avoid. Confirmed live: without the Started check, a thread
            // that legitimately called exit(1) (a real error exit, not a hang) got silently
            // un-slept every time SwitchToNext ran with nothing else runnable, so execution fell
            // through past the syscall as if the exit had never happened and immediately hit the
            // same exit call again on its next pass through whatever loop led there — 261 times
            // by 100M cycles in one trace, masking the real underlying error as a fake loop
            // instead of surfacing it.
            var cur = FindThread(_currentTid);
            if (cur != null && cur.Sleeping && cur.Started)
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

    private uint _preemptQuantum = 0x10000; // ~65536 EE cycles per timeslice
    private ulong _cyclesSinceLastPreempt;

    /// <summary>Save every GPR (not just the callee-saved subset) — see the
    /// SavedGprFull doc comment on why a forced preemption needs this.</summary>
    private void SaveFullContext(EmotionEngine ee)
    {
        var t = FindThread(_currentTid);
        if (t == null) return;
        t.SavedGprFull ??= new ulong[32];
        for (int i = 0; i < 32; i++)
            t.SavedGprFull[i] = ee.GetGpr(i).Lo;
        t.HasFullSave = true;
        t.SavedPc = ee.PC; // preempted mid-stream: resume at the exact interrupted PC
        // Also refresh the partial-save fields SaveCurrentContext normally maintains. A thread
        // preempted here can later be resumed via the ordinary cooperative path instead of
        // RestoreFullContext (e.g. another thread's own SwitchToNext/StartAndMaybeSwitch picking
        // this thread as "next runnable") -- RestoreContext has no idea SavedGprFull exists and
        // only ever reads these partial fields. Without this, it would silently resurrect
        // whatever ancient SavedSp/SavedRa/etc. this thread had from its LAST COOPERATIVE yield
        // (which can be millions of cycles and many nested calls stale) instead of the position
        // it was actually just interrupted at -- PC gets restored correctly (both paths use
        // SavedPc) but every stack-relative access afterward computes from the wrong SP, silently
        // reading/writing whatever unrelated data now sits at that offset. Confirmed exactly this
        // failure mode (2026-07-26): a stale SavedSp resurrected a shallower stack frame under a
        // deeper PC, and the resulting offset mismatch looked like memory corruption (near-null
        // writes, tiny garbage values in place of real pointers) several layers downstream before
        // being traced back here.
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
        LogThreadEvent("PreemptOut", _currentTid, t.SavedPc, ee.GetGpr(29).Lo);
    }

    /// <summary>Restore every GPR saved by <see cref="SaveFullContext"/>, or fall back to the
    /// existing partial restore for a thread that's never been force-preempted before (only
    /// ever entered fresh or resumed from a syscall boundary).</summary>
    private bool RestoreFullContext(EmotionEngine ee, int id)
    {
        var t = FindThread(id);
        if (t == null || !t.Alive) return false;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RESTORE1") == "1" && id == 1)
            Console.Error.WriteLine($"[RESTOREFULL1] cyc={CurrentCycle} wasStarted={t.Started} wasSleeping={t.Sleeping} hasFullSave={t.HasFullSave}");
        if (!t.HasFullSave || t.SavedGprFull == null)
            return RestoreContext(ee, id, fromSyscall: false);

        _currentTid = id;
        ee.PC = t.SavedPc;
        for (int i = 1; i < 32; i++) // skip $zero
            ee.SetGpr(i, new EmotionEngine.Gpr128 { Lo = t.SavedGprFull[i] });
        t.Sleeping = false;
        t.Started = true;
        LogThreadEvent("PreemptIn", id, t.SavedPc, t.SavedGprFull[29]);
        return true;
    }

    /// <summary>
    /// Real PS2 kernel threads are preempted by a periodic timer tick regardless of whether
    /// they ever call a blocking syscall — our scheduler otherwise only switches at explicit
    /// SleepThread/WaitSema/etc. call sites (see SwitchToNext), so a thread that busy-waits
    /// without ever yielding (legal, common real-hardware code — e.g. a bind-retry loop with a
    /// local software delay) would starve every other thread forever. Called periodically from
    /// EmotionEngine.Step(); a full GPR save/restore is required (see SaveFullContext) since
    /// this can land anywhere, unlike every other switch point in this file.
    /// </summary>
    public void MaybePreempt(EmotionEngine ee)
    {
        _cyclesSinceLastPreempt++;
        if (_cyclesSinceLastPreempt < _preemptQuantum) return;
        _cyclesSinceLastPreempt = 0;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_PREEMPT") == "1")
            Console.Error.WriteLine($"[PREEMPT] tick threads={_threads.Count} cur={_currentTid} pc=0x{ee.PC:X8}");
        if (_threads.Count < 2) return; // common case: nothing else to switch to

        int next = FindNextRunnable(_currentTid);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_PREEMPT") == "1")
            Console.Error.WriteLine($"[PREEMPT] cur={_currentTid} next={next}");
        if (next == _currentTid) return;
        SaveFullContext(ee);
        RestoreFullContext(ee, next);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_PREEMPT") == "1")
            Console.Error.WriteLine($"[PREEMPT] switched {_currentTid} -> pc=0x{ee.PC:X8}");
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

    /// <summary>Real WaitEventFlag semantics (ps2sdk EventFlagMode): mode bit 0x01 selects
    /// OR (any pattern bit set satisfies) vs AND (all pattern bits set, the default); mode bit
    /// 0x10 requests the matched bits be cleared on successful wait. Auto-creates a missing flag
    /// (id 0 bits) rather than erroring, matching WaitSema's existing race-tolerant auto-create —
    /// titles sometimes Wait before Create races under HLE timing.</summary>
    public bool EventFlagSatisfied(int id, uint pattern, uint mode)
    {
        if (!_flags.TryGetValue(id, out var f))
            _flags[id] = f = new EventFlag { Id = id, Bits = 0 };
        // AND mode (default) with pattern=0 is trivially satisfied ((bits & 0) == 0 always) -
        // a real, legitimate case (e.g. a fresh CreateEventFlag+WaitEventFlag(0, AND) pairing
        // used purely as a rendezvous point), not something that should ever block. The
        // auto-create branch above previously short-circuited to "not satisfied" unconditionally
        // for a just-created flag, forcing a needless block/SwitchToNext on every such call.
        bool or = (mode & 0x01) != 0;
        return or ? (f.Bits & pattern) != 0 : (f.Bits & pattern) == pattern;
    }

    /// <summary>Reads the satisfying bits and applies clear-on-exit (mode bit 0x10). Caller must
    /// have already confirmed EventFlagSatisfied for the same id/pattern/mode.</summary>
    public uint ConsumeEventFlag(int id, uint pattern, uint mode)
    {
        if (!_flags.TryGetValue(id, out var f)) return 0;
        uint result = f.Bits;
        if ((mode & 0x10) != 0) f.Bits &= ~pattern;
        return result;
    }

    /// <summary>Marks a thread as blocked waiting on an event flag — mirrors WaitSemaBlocking's
    /// own Sleeping=true/WaitSemaId=id side effect, but for event flags. SetEventFlag re-checks
    /// every parked thread's condition and wakes any that are now satisfied.</summary>
    public void ParkOnEventFlag(int threadId, int efId, uint pattern, uint mode, uint resultAddr)
    {
        var t = FindThread(threadId);
        if (t == null) return;
        t.Sleeping = true;
        t.WaitEfId = efId;
        t.WaitEfPattern = pattern;
        t.WaitEfMode = mode;
        t.WaitEfResultAddr = resultAddr;
    }

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
