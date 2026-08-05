using System;
using System.Collections.Generic;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Extended kernel HLE state (Phase 14 + THREADMAN Phase 1): threads, semaphores, event flags,
/// message boxes, variable/fixed pools, delay, priority ready selection, VSync wait.
/// Integrated into <see cref="BiosHle"/>.
/// </summary>
public sealed class KernelState
{
    /// <summary>THREADMAN DeleteSema/DeleteMbx/DeleteVpl/DeleteFpl waiter return (FUN_00003164).</summary>
    public const int KeWaitDelete = unchecked((int)0xfffffe57);
    /// <summary>THREADMAN ReleaseWaitThread waiter return.</summary>
    public const int KeReleaseWait = unchecked((int)0xfffffe5e);
    /// <summary>THREADMAN PollMbx empty (FUN_00003de4).</summary>
    public const int KeMboxNomsg = unchecked((int)0xfffffe58);
    /// <summary>Generic missing/unknown object (approx. KE_UNKNOWN_MBOX family).</summary>
    public const int KeUnknownObject = unchecked((int)0xfffffe66);
    /// <summary>AllocateVpl/Fpl would block (poll path).</summary>
    public const int KeNoMemory = -400;

    public sealed class Thread
    {
        public int Id;
        public bool Alive;
        public bool Sleeping;
        public bool WaitVblank;
        public bool Started;
        /// <summary>True after the first successful StartThread. Distinguishes never-started
        /// DORMANT (Refer must report 0x10 so games can StartThread) from exited-after-run.</summary>
        public bool EverStarted;
        /// <summary>Logical suspend for exited (DORMANT) threads. MK ADX lock at 0x414A58 does
        /// Suspend on peers; unlock at 0x4149F0 Resumes if status==SUSPEND. Reporting permanent
        /// SUSPEND for all EverStarted DORMANT caused 2.6M× Resume thrash; never-SUSPEND caused
        /// Suspend thrash. SoftSuspended is set by SuspendThread on exited peers and cleared by
        /// ResumeThread — mutual exclusion without resurrecting ExitThread'd workers.</summary>
        public bool SoftSuspended;
        /// <summary>EE SuspendThread nest count. Non-zero ⇒ not runnable until ResumeThread
        /// drains it (independent of SleepThread / WaitSema). Was a no-op stub — ADX workers
        /// burned 160k+ SuspendThread calls/80M cycles with no yield.</summary>
        public int SuspendCount;
        /// <summary>BIOS THREADMAN SleepThread wakeup counter (thread+0x1e). WakeupThread on a
        /// non-SLEEP-waiting thread increments; SleepThread consumes one without parking when
        /// &gt; 0. CancelWakeupThread returns-and-clears. Distinct from WaitSema (wait type 3).</summary>
        public int WakeupCount;
        public int WaitSemaId;
        /// <summary>0 = not waiting on an event flag. See WaitEventFlag/SetEventFlag in
        /// SonyKernelHle.cs and KernelState.EventFlagSatisfied/ConsumeEventFlag/ParkOnEventFlag.</summary>
        public int WaitEfId;
        public uint WaitEfPattern;
        public uint WaitEfMode;
        public uint WaitEfResultAddr;
        /// <summary>Bits observed when SetEventFlag last released this waiter (for result_ptr write).</summary>
        public uint WaitEfLastBits;
        /// <summary>True after SetEventFlag released this waiter until the result_ptr is written.</summary>
        public bool WaitEfNeedsResultWrite;
        /// <summary>EE/IOP thread priority. Lower value = higher priority (μITRON / ps2sdk).</summary>
        public int Priority = 64;
        /// <summary>Create-time priority (ee_thread_t.initial_priority / ReferThreadStatus).</summary>
        public int InitialPriority = 64;
        /// <summary>When set, next restore writes this into $v0 (DeleteSema/ReleaseWait waiter ABI).</summary>
        public bool HasWaitReturn;
        public int WaitReturnCode;
        /// <summary>0 = not waiting on a message box (ReceiveMbx).</summary>
        public int WaitMbxId;
        /// <summary>Message pointer delivered by SendMbx to a ReceiveMbx waiter.</summary>
        public uint MbxReceivedMsg;
        /// <summary>0 = not waiting on AllocateVpl.</summary>
        public int WaitVplId;
        public int WaitVplSize;
        public uint VplAllocatedPtr;
        /// <summary>0 = not waiting on AllocateFpl.</summary>
        public int WaitFplId;
        public uint FplAllocatedPtr;
        /// <summary>&gt;0 ⇒ DelayThread park; decremented by <see cref="TickDelays"/> / OnVblank.</summary>
        public int DelayRemainingUs;
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
        /// <summary>CreateSema init_count (THREADMAN +0x28) — ReferSemaStatus reports this
        /// separately from the live <see cref="Count"/>.</summary>
        public int InitCount;
    }

    public sealed class EventFlag
    {
        public int Id;
        public uint Bits;
    }

    /// <summary>THREADMAN message box (thmsgbx / magic 0x7f04).</summary>
    public sealed class Mbx
    {
        public int Id;
        public uint Attr;
        public uint Option;
        public readonly Queue<uint> Messages = new();
    }

    /// <summary>THREADMAN variable pool (thvpool / magic 0x7f05) — host-tracked freelist.</summary>
    public sealed class Vpl
    {
        public int Id;
        public uint Attr;
        public uint Option;
        public int PoolSize;
        public int FreeBytes;
        /// <summary>Synthetic base address for pointers returned by AllocateVpl.</summary>
        public uint BasePtr;
        public readonly List<(uint Ptr, int Size)> FreeBlocks = new();
        public readonly List<(uint Ptr, int Size)> UsedBlocks = new();
    }

    /// <summary>THREADMAN fixed pool (thfpool / magic 0x7f06) — fixed-size block freelist.</summary>
    public sealed class Fpl
    {
        public int Id;
        public uint Attr;
        public uint Option;
        public int BlockSize;
        public int BlockCount;
        public uint BasePtr;
        public readonly Queue<uint> FreeBlocks = new();
        public readonly HashSet<uint> UsedBlocks = new();
    }

    private readonly List<Thread> _threads = new();
    private readonly Dictionary<int, Sema> _semas = new();
    private readonly Dictionary<int, EventFlag> _flags = new();
    private readonly Dictionary<int, Mbx> _mbxs = new();
    private readonly Dictionary<int, Vpl> _vpls = new();
    private readonly Dictionary<int, Fpl> _fpls = new();
    private int _nextTid = 1;
    private int _nextSema = 1;
    private int _nextEf = 1;
    private int _nextMbx = 1;
    private int _nextVpl = 1;
    private int _nextFpl = 1;
    private int _currentTid = 1;
    /// <summary>Bump allocator for synthetic Vpl/Fpl pointer cookies (not real RDRAM).</summary>
    private uint _nextPoolCookie = 0x0E000000;

    public bool WaitingVblank { get; set; } // nested end_function/alarm clear temporarily
    public ulong VblankWaits { get; private set; }
    public int ThreadCount => _threads.Count;
    public int CurrentThreadId => _currentTid;

    /// <summary>
    /// General (title-independent) version of MidwayBootAssist.MaybeUnblockStarvedSema — a
    /// last-resort rescue for a real deadlock: a thread genuinely, correctly blocked on WaitSema
    /// (Play!'s reference sc_WaitSema semantics, ground-truthed 2026-08-03) whose real producer
    /// never signals it, because some other real mechanism DetPS2 doesn't yet model correctly
    /// never runs. Confirmed live (Shadow of the Colossus, SCUS_974.72): every thread ends up
    /// genuinely asleep simultaneously with no thread left to make the real producer's work
    /// happen — a true full deadlock, not a scheduler-fairness bug (the would-be producer is
    /// provably Sleeping too, not merely unscheduled).
    ///
    /// Was previously only implemented per-title inside MidwayBootAssist (proven safe there);
    /// this promotes the same, already-validated logic (long real grace period, drain the real
    /// RPC queue first, force-signal only as a last resort) to the core scheduler so every title
    /// benefits, not just the one where it happened to be hand-wired first. Grace period is
    /// deliberately long (matching Midway's 1.5M-cycle default) so it never preempts a genuine,
    /// eventually-self-resolving real completion.
    /// </summary>
    private readonly Dictionary<int, (int semaId, ulong sinceCycle)> _genericSemaWaitStart = new();

    public int GenericStarvedSemaRescues { get; private set; }

    public void MaybeRescueGenericStarvedSema(Ps2System sys, ulong graceCycles = 1_500_000UL)
    {
        foreach (var t in _threads)
        {
            if (!t.Alive || !t.Sleeping || t.WaitSemaId == 0)
            {
                _genericSemaWaitStart.Remove(t.Id);
                continue;
            }
            if (!_genericSemaWaitStart.TryGetValue(t.Id, out var w) || w.semaId != t.WaitSemaId)
            {
                _genericSemaWaitStart[t.Id] = (t.WaitSemaId, sys.MasterCycles);
                continue;
            }
            if (sys.MasterCycles - w.sinceCycle < graceCycles) continue;

            // Only rescue a true, whole-system deadlock: every other thread must ALSO be
            // genuinely non-runnable right now, not just lower priority. If anyone else could
            // still make real progress, let the real scheduler run them instead of guessing.
            bool anyoneElseRunnable = false;
            foreach (var other in _threads)
            {
                if (other.Id == t.Id) continue;
                if (IsRunnable(other)) { anyoneElseRunnable = true; break; }
            }
            if (anyoneElseRunnable)
            {
                _genericSemaWaitStart[t.Id] = (t.WaitSemaId, sys.MasterCycles);
                continue;
            }

            // Drain real RPC first — often the producer for this WaitSema (matches
            // MidwayBootAssist.MaybeUnblockStarvedSema exactly).
            sys.Hle?.Sony?.DrainRealRpcQueue(sys.SchedulerGeneration + 1);
            if (!t.Sleeping) { _genericSemaWaitStart.Remove(t.Id); continue; }

            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[RPC] generic force-unblocking starved sema={t.WaitSemaId} thread={t.Id} " +
                    $"cyc={sys.MasterCycles} (whole-system deadlock)");
            SignalSema(t.WaitSemaId);
            _genericSemaWaitStart[t.Id] = (t.WaitSemaId, sys.MasterCycles);
            GenericStarvedSemaRescues++;
        }
    }

    /// <summary>
    /// M6-b1: title-independent SleepThread / SuspendThread starve rescue. Sibling of
    /// <see cref="MaybeRescueGenericStarvedSema"/> for parks with <c>WaitSemaId == 0</c>
    /// (pure SleepThread or Suspend nest). Never calls <see cref="SignalSema"/> — B3
    /// thrash history forbids fabricating WaitSema progress from this path.
    ///
    /// Default gate = whole-system deadlock only (no other <see cref="IsRunnable"/> peer),
    /// same spirit as generic WaitSema rescue. Grace mirrors Midway: 2M cycles pure sleep,
    /// 400k suspend. Kill-switch: <c>DETPS2_DISABLE_M6B_SLEEP_RESCUE=1</c>. Optional
    /// peer-runnable pure-sleep orphan (off by default): <c>DETPS2_STARVED_SLEEP_ORPHAN=1</c>
    /// after 2× pure-sleep grace; still never force-Resume Suspend under orphan mode.
    /// </summary>
    /// <remarks>kind: 0 = pure sleep, 1 = suspend nest</remarks>
    private readonly Dictionary<int, (int kind, ulong sinceCycle)> _genericSleepWaitStart = new();

    public int GenericStarvedSleepRescues { get; private set; }

    public void MaybeRescueGenericStarvedSleep(
        Ps2System sys,
        ulong graceSleep = 2_000_000UL,
        ulong graceSuspend = 400_000UL)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("DETPS2_DISABLE_M6B_SLEEP_RESCUE"), "1",
                StringComparison.Ordinal))
            return;

        bool orphanEnv = string.Equals(
            Environment.GetEnvironmentVariable("DETPS2_STARVED_SLEEP_ORPHAN"), "1",
            StringComparison.Ordinal);

        foreach (var t in _threads)
        {
            // Candidate: Alive, no WaitSema, not VBlank, pure sleep or suspend nest,
            // lifecycle Started||tid1; skip SoftSuspended ExitThread sticky DORMANT peers.
            bool pureSleep = t.Sleeping && t.SuspendCount == 0;
            bool suspendNest = t.SuspendCount > 0;
            bool lifecycleOk = t.Started || t.Id == 1;
            bool softExitSticky = t.SoftSuspended && t.EverStarted && !t.Started;
            bool candidate = t.Alive
                && t.WaitSemaId == 0
                && !t.WaitVblank
                && (pureSleep || suspendNest)
                && lifecycleOk
                && !softExitSticky;

            if (!candidate)
            {
                _genericSleepWaitStart.Remove(t.Id);
                continue;
            }

            int kind = suspendNest ? 1 : 0; // 1 = suspend, 0 = pure sleep
            if (!_genericSleepWaitStart.TryGetValue(t.Id, out var w) || w.kind != kind)
            {
                _genericSleepWaitStart[t.Id] = (kind, sys.MasterCycles);
                continue;
            }

            ulong grace = kind == 1 ? graceSuspend : graceSleep;
            ulong elapsed = sys.MasterCycles - w.sinceCycle;
            if (elapsed < grace) continue;

            // Whole-system deadlock gate (default). Orphan env may allow pure sleep after 2× grace.
            bool anyoneElseRunnable = false;
            foreach (var other in _threads)
            {
                if (other.Id == t.Id) continue;
                if (IsRunnable(other)) { anyoneElseRunnable = true; break; }
            }
            if (anyoneElseRunnable)
            {
                // Orphan: pure Sleep only, after 2× pure-sleep grace; never force-Resume Suspend.
                bool orphanOk = orphanEnv && kind == 0 && elapsed >= graceSleep * 2UL;
                if (!orphanOk)
                {
                    // Keep timer armed while orphan pure-sleep accumulates toward 2×;
                    // otherwise reset so intentional multi-thread parks do not thrash.
                    if (!(orphanEnv && kind == 0))
                        _genericSleepWaitStart[t.Id] = (kind, sys.MasterCycles);
                    continue;
                }
            }

            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[RPC] generic force-waking starved sleep/suspend thread={t.Id} " +
                    $"susp={t.SuspendCount} cyc={sys.MasterCycles}");

            if (kind == 1)
            {
                // Drain suspend nest; safety cap avoids infinite loop if Resume no-ops
                // (SoftSuspended sticky already filtered above, but be defensive).
                for (int i = 0; i < 16 && t.SuspendCount > 0; i++)
                    ResumeThread(t.Id);
            }
            else
            {
                // Pure sleep: WakeupThread only (refuses WaitSema parks and Suspend-only).
                WakeupThread(t.Id);
            }

            _genericSleepWaitStart.Remove(t.Id); // fresh grace if it re-parks
            GenericStarvedSleepRescues++;
        }
    }

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
        _mbxs.Clear();
        _vpls.Clear();
        _fpls.Clear();
        _nextTid = 1;
        _nextSema = 1;
        _nextEf = 1;
        _nextMbx = 1;
        _nextVpl = 1;
        _nextFpl = 1;
        _nextPoolCookie = 0x0E000000;
        _currentTid = 1;
        WaitingVblank = false;
        VblankWaits = 0;
        _cyclesSinceLastPreempt = 0;
        _genericSemaWaitStart.Clear();
        _genericSleepWaitStart.Clear();
        // Main thread — already running; priority 1 (high) matches typical EE idle/main setup
        _threads.Add(new Thread { Id = 1, Alive = true, Started = true, Entry = 0, Priority = 1, InitialPriority = 1 });
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
            w.Write(t.EverStarted);
            w.Write(t.SoftSuspended);
            w.Write(t.SuspendCount);
            w.Write(t.WakeupCount);
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
            // Phase-1 THREADMAN extensions (appended so older fields keep fixed offsets)
            w.Write(t.Priority);
            w.Write(t.InitialPriority);
            w.Write(t.HasWaitReturn);
            w.Write(t.WaitReturnCode);
            w.Write(t.WaitMbxId);
            w.Write(t.MbxReceivedMsg);
            w.Write(t.WaitVplId);
            w.Write(t.WaitVplSize);
            w.Write(t.VplAllocatedPtr);
            w.Write(t.WaitFplId);
            w.Write(t.FplAllocatedPtr);
            w.Write(t.DelayRemainingUs);
        }

        w.Write(_semas.Count);
        foreach (var kv in _semas)
        {
            w.Write(kv.Value.Id);
            w.Write(kv.Value.Count);
            w.Write(kv.Value.MaxCount);
            w.Write(kv.Value.InitCount);
        }

        w.Write(_flags.Count);
        foreach (var kv in _flags)
        {
            w.Write(kv.Value.Id);
            w.Write(kv.Value.Bits);
        }

        w.Write(_nextMbx);
        w.Write(_nextVpl);
        w.Write(_nextFpl);
        w.Write(_nextPoolCookie);

        w.Write(_mbxs.Count);
        foreach (var kv in _mbxs)
        {
            var m = kv.Value;
            w.Write(m.Id);
            w.Write(m.Attr);
            w.Write(m.Option);
            w.Write(m.Messages.Count);
            foreach (var msg in m.Messages) w.Write(msg);
        }

        w.Write(_vpls.Count);
        foreach (var kv in _vpls)
        {
            var v = kv.Value;
            w.Write(v.Id);
            w.Write(v.Attr);
            w.Write(v.Option);
            w.Write(v.PoolSize);
            w.Write(v.FreeBytes);
            w.Write(v.BasePtr);
            w.Write(v.FreeBlocks.Count);
            foreach (var b in v.FreeBlocks) { w.Write(b.Ptr); w.Write(b.Size); }
            w.Write(v.UsedBlocks.Count);
            foreach (var b in v.UsedBlocks) { w.Write(b.Ptr); w.Write(b.Size); }
        }

        w.Write(_fpls.Count);
        foreach (var kv in _fpls)
        {
            var f = kv.Value;
            w.Write(f.Id);
            w.Write(f.Attr);
            w.Write(f.Option);
            w.Write(f.BlockSize);
            w.Write(f.BlockCount);
            w.Write(f.BasePtr);
            w.Write(f.FreeBlocks.Count);
            foreach (var p in f.FreeBlocks) w.Write(p);
            w.Write(f.UsedBlocks.Count);
            foreach (var p in f.UsedBlocks) w.Write(p);
        }
    }

    public void ReadState(BinaryReader r)
    {
        _threads.Clear();
        _semas.Clear();
        _flags.Clear();
        _mbxs.Clear();
        _vpls.Clear();
        _fpls.Clear();

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
                EverStarted = r.ReadBoolean(),
                SoftSuspended = r.ReadBoolean(),
                SuspendCount = r.ReadInt32(),
                WakeupCount = r.ReadInt32(),
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
            t.Priority = r.ReadInt32();
            t.InitialPriority = r.ReadInt32();
            t.HasWaitReturn = r.ReadBoolean();
            t.WaitReturnCode = r.ReadInt32();
            t.WaitMbxId = r.ReadInt32();
            t.MbxReceivedMsg = r.ReadUInt32();
            t.WaitVplId = r.ReadInt32();
            t.WaitVplSize = r.ReadInt32();
            t.VplAllocatedPtr = r.ReadUInt32();
            t.WaitFplId = r.ReadInt32();
            t.FplAllocatedPtr = r.ReadUInt32();
            t.DelayRemainingUs = r.ReadInt32();
            _threads.Add(t);
        }

        int semaCount = r.ReadInt32();
        for (int i = 0; i < semaCount; i++)
        {
            var s = new Sema
            {
                Id = r.ReadInt32(),
                Count = r.ReadInt32(),
                MaxCount = r.ReadInt32(),
                InitCount = r.ReadInt32()
            };
            _semas[s.Id] = s;
        }

        int flagCount = r.ReadInt32();
        for (int i = 0; i < flagCount; i++)
        {
            var f = new EventFlag { Id = r.ReadInt32(), Bits = r.ReadUInt32() };
            _flags[f.Id] = f;
        }

        _nextMbx = r.ReadInt32();
        _nextVpl = r.ReadInt32();
        _nextFpl = r.ReadInt32();
        _nextPoolCookie = r.ReadUInt32();

        int mbxCount = r.ReadInt32();
        for (int i = 0; i < mbxCount; i++)
        {
            var m = new Mbx
            {
                Id = r.ReadInt32(),
                Attr = r.ReadUInt32(),
                Option = r.ReadUInt32()
            };
            int nMsg = r.ReadInt32();
            for (int j = 0; j < nMsg; j++) m.Messages.Enqueue(r.ReadUInt32());
            _mbxs[m.Id] = m;
        }

        int vplCount = r.ReadInt32();
        for (int i = 0; i < vplCount; i++)
        {
            var v = new Vpl
            {
                Id = r.ReadInt32(),
                Attr = r.ReadUInt32(),
                Option = r.ReadUInt32(),
                PoolSize = r.ReadInt32(),
                FreeBytes = r.ReadInt32(),
                BasePtr = r.ReadUInt32()
            };
            int nFree = r.ReadInt32();
            for (int j = 0; j < nFree; j++) v.FreeBlocks.Add((r.ReadUInt32(), r.ReadInt32()));
            int nUsed = r.ReadInt32();
            for (int j = 0; j < nUsed; j++) v.UsedBlocks.Add((r.ReadUInt32(), r.ReadInt32()));
            _vpls[v.Id] = v;
        }

        int fplCount = r.ReadInt32();
        for (int i = 0; i < fplCount; i++)
        {
            var f = new Fpl
            {
                Id = r.ReadInt32(),
                Attr = r.ReadUInt32(),
                Option = r.ReadUInt32(),
                BlockSize = r.ReadInt32(),
                BlockCount = r.ReadInt32(),
                BasePtr = r.ReadUInt32()
            };
            int nFree = r.ReadInt32();
            for (int j = 0; j < nFree; j++) f.FreeBlocks.Enqueue(r.ReadUInt32());
            int nUsed = r.ReadInt32();
            for (int j = 0; j < nUsed; j++) f.UsedBlocks.Add(r.ReadUInt32());
            _fpls[f.Id] = f;
        }
    }

    public int CreateThread(uint entry, uint gp, uint stack, uint stackSize = 0, int priority = 64)
    {
        int id = ++_nextTid;
        int prio = priority < 1 ? 1 : (priority > 127 ? 127 : priority);
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
            SavedGp = gp,
            Priority = prio,
            InitialPriority = prio
        });
        LogThreadEvent("Create", id, entry, stack, $"gp=0x{gp:X8} stackSize=0x{stackSize:X} prio={prio}");
        return id;
    }

    /// <summary>ps2sdk ChangeThreadPriority — lower value runs first. Returns previous priority.</summary>
    public int ChangeThreadPriority(int id, int priority)
    {
        var t = FindThread(id == 0 ? _currentTid : id);
        if (t == null || !t.Alive) return -1;
        int old = t.Priority;
        int prio = priority < 1 ? 1 : (priority > 127 ? 127 : priority);
        t.Priority = prio;
        return old;
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
        t.EverStarted = true;
        t.StartArg = arg;
        t.FreshStart = true;
        t.SavedPc = t.Entry;
        t.SuspendCount = 0;
        if (t.SavedSp == 0)
            t.SavedSp = t.Stack != 0 ? t.Stack : 0x01F00000u - (uint)(id * 0x10000);
        LogThreadEvent("Start", id, t.SavedPc, t.SavedSp, $"arg=0x{arg:X}");
        return 0;
    }

    /// <summary>BIOS THREADMAN SleepThread (FUN_0000200c): if a pending
    /// <see cref="Thread.WakeupCount"/> exists, consume one and return without parking;
    /// otherwise mark pure-sleep (WaitSemaId stays 0) and yield.</summary>
    public int SleepThread()
    {
        var t = FindThread(_currentTid);
        if (t == null) return -1;
        if (t.WakeupCount > 0)
        {
            t.WakeupCount--;
            return 0;
        }
        t.Sleeping = true;
        // Pure SleepThread: not a WaitSema park (WaitSemaId must stay 0 so WakeupThread
        // — not SignalSema — is the matching producer).
        t.WaitSemaId = 0;
        return 0;
    }

    /// <summary>BIOS THREADMAN CancelWakeupThread (FUN_000022dc): return the pending
    /// wakeup count and clear it. Does not wake a currently-sleeping thread.</summary>
    public int CancelWakeupThread(int id)
    {
        var t = FindThread(id == 0 ? _currentTid : id);
        if (t == null || !t.Alive) return -1;
        int old = t.WakeupCount;
        t.WakeupCount = 0;
        return old;
    }

    /// <summary>BIOS THREADMAN SuspendThread — nestable; thread not runnable while count &gt; 0.</summary>
    public int SuspendThread(int id)
    {
        var t = FindThread(id == 0 ? _currentTid : id);
        if (t == null || !t.Alive) return -1;
        // Exited (DORMANT) peer: record logical suspend for Refer/Resume mutual-exclusion
        // without resurrecting the thread (see SoftSuspended doc).
        if (!t.Started && t.EverStarted)
        {
            t.SoftSuspended = true;
            return 0;
        }
        if (!t.Started) return 0; // never-started: no-op success
        t.SuspendCount++;
        // Suspend implies not runnable (same as sleeping for the ready-queue scan).
        t.Sleeping = true;
        return 0;
    }

    /// <summary>BIOS THREADMAN ResumeThread — decrements suspend nest; ready when count hits 0
    /// and not blocked on a sema/vblank.</summary>
    public int ResumeThread(int id)
    {
        var t = FindThread(id == 0 ? _currentTid : id);
        if (t == null || !t.Alive) return -1;
        // Exited peers (EverStarted && !Started): keep SoftSuspended sticky. Unlock path
        // 0x4149F0 Resumes if status==SUSPEND; if we clear SoftSuspended, the lock path
        // immediately Suspends again → 390k× Suspend/Refer thrash after WaitSemaVblank
        // freeze was removed. Permanent park is correct for ExitThread'd ADX waiters.
        if (t.SoftSuspended)
        {
            if (!(t.EverStarted && !t.Started))
                t.SoftSuspended = false;
            return 0;
        }
        if (t.SuspendCount > 0)
            t.SuspendCount--;
        if (t.SuspendCount == 0 && t.WaitSemaId == 0 && !t.WaitVblank)
            t.Sleeping = false;
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
        t.WakeupCount = 0;
        t.WaitEfId = 0;
    }

    public int WakeupThread(int id)
    {
        // Retail code (esp. Midway SIF-RPC dispatch workers) often calls WakeupThread(0)
        // because the primordial EE thread never went through CreateThread/GetThreadId, so
        // the id stored for "wake main when done" is 0. Real kernels treat invalid ids as
        // errors; under HLE a permanent no-op deadlocks every SleepThread waiter that
        // expected that wake (Shaolin Monks threads 4–6, 2026-07-27). Map id 0 to: wake
        // every pure SleepThread waiter (WaitSemaId==0, not VBlank), preferring thread 1.
        if (id == 0)
        {
            int woken = 0;
            foreach (var t in _threads)
            {
                // Don't clear SuspendThread park via WakeupThread(0)
                if (!t.Alive || !t.Sleeping || t.WaitSemaId != 0 || t.WaitVblank || t.SuspendCount > 0) continue;
                t.Sleeping = false;
                t.WaitVblank = false;
                woken++;
            }
            // Also clear a pure-sleep on the current thread if it SleepThread'd itself
            // waiting for a worker that only ever WakeupThread(0)'s.
            if (woken == 0)
            {
                var main = FindThread(1);
                if (main != null && main.Alive && main.Sleeping && main.WaitSemaId == 0 && main.SuspendCount == 0)
                {
                    main.Sleeping = false;
                    main.WaitVblank = false;
                    woken = 1;
                }
            }
            return woken > 0 ? 0 : -1;
        }

        var th = FindThread(id);
        if (th == null) return -1;
        // Real THREADMAN: WakeupThread only affects SleepThread waiters. A WaitSema
        // park is released by SignalSema. MK ADX helper at 0x414988 does
        //   ReferStatus; if WAIT(4) or WAIT|SUSPEND(12) → WakeupThread
        // against the SIF worker (WaitSemaId=3). Clearing Sleeping without
        // clearing WaitSemaId left Refer status stuck at WAIT forever → 5.8M×
        // Refer thrash. Route WaitSema waiters through SignalSema instead.
        if (th.WaitSemaId != 0)
            return SignalSema(th.WaitSemaId);
        th.WaitVblank = false;
        // Decomp FUN_000020e4: if currently WAIT+SLEEP → mark READY; else increment
        // wakeup-count (+0x1e). Pending wakes are consumed by the next SleepThread.
        if (th.Sleeping && th.WaitSemaId == 0 && !th.WaitVblank)
        {
            // WakeupThread does not cancel SuspendThread — only ResumeThread does.
            if (th.SuspendCount == 0)
                th.Sleeping = false;
            return 0;
        }
        th.WakeupCount++;
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

    /// <summary>True if thread is eligible for the ready set (Alive, started/main, not parked).</summary>
    private static bool IsRunnable(Thread t)
    {
        if (!t.Alive || t.Sleeping || t.WaitVblank || t.SuspendCount > 0) return false;
        // Primordial main (id 1) is runnable even when Started was never set via StartThread.
        if (t.Id == 1) return true;
        return t.Started;
    }

    /// <summary>
    /// When true, <see cref="FindNextRunnable"/> uses circular RR (ignores Priority).
    /// Midway SM (SLUS_210.87) needs this: G0 priority band + preempt reordered ADX pump
    /// vs main → Exit@12.4M / no WAD. Default false preserves THREADMAN priority smokes.
    /// Env <c>DETPS2_RR_SCHED=1</c> forces RR; <c>DETPS2_PRIO_SCHED=1</c> forces priority.
    /// </summary>
    public bool PreferRoundRobinSched { get; set; }

    /// <summary>
    /// S1 (b3-ee-sched-fairness-design.md, dual-ACK 2026-08-05): per-priority-tier "last
    /// picked" tid, so tie-breaks among threads sharing a priority level rotate fairly
    /// instead of always favoring whichever tied thread sits array-closest to whichever
    /// thread is currently yielding. Real THREADMAN rotates a ready-queue per priority
    /// level; this restores that shape without touching real priority ordering (a strictly
    /// better-priority thread still always wins, same as before). Never populated/consulted
    /// when a priority tier has only one runnable candidate — zero behavior change for the
    /// common (no-tie) case. Threads are never removed from <see cref="_threads"/> (dense
    /// table, matches Iop.cs's own convention), so no cursor invalidation is needed on
    /// thread create/delete — a stale cursor tid simply won't be found and the scan starts
    /// from its last valid array position instead.
    /// </summary>
    private readonly Dictionary<int, int> _lastPickedTidByPriority = new();

    /// <summary>
    /// Find next runnable thread id, or <paramref name="afterId"/> if none.
    /// Default: priority-aware (THREADMAN readyq / μITRON: lower Priority runs first).
    /// Circular RR when <see cref="PreferRoundRobinSched"/> or <c>DETPS2_RR_SCHED=1</c>.
    /// </summary>
    public int FindNextRunnable(int afterId)
    {
        int idx = 0;
        for (int i = 0; i < _threads.Count; i++)
            if (_threads[i].Id == afterId) { idx = i; break; }

        bool forcePrio = string.Equals(
            Environment.GetEnvironmentVariable("DETPS2_PRIO_SCHED"), "1",
            StringComparison.Ordinal);
        bool forceRr = string.Equals(
            Environment.GetEnvironmentVariable("DETPS2_RR_SCHED"), "1",
            StringComparison.Ordinal);
        bool prioSched = forcePrio || (!forceRr && !PreferRoundRobinSched);

        if (prioSched)
        {
            // Best priority among OTHER runnable threads (exclude afterId for selection).
            int bestPrio = int.MaxValue;
            bool found = false;
            for (int i = 0; i < _threads.Count; i++)
            {
                var t = _threads[i];
                if (t.Id == afterId) continue;
                if (!IsRunnable(t)) continue;
                if (t.Priority < bestPrio)
                {
                    bestPrio = t.Priority;
                    found = true;
                }
            }
            if (!found)
            {
                var mainP = FindThread(1);
                if (mainP != null && mainP.Id != afterId && mainP.Alive && !mainP.Sleeping
                    && mainP.SuspendCount == 0)
                    return 1;
                return afterId;
            }

            // S1: rotate among tied-priority candidates starting from this tier's last pick
            // (falling back to afterId's own position the first time this tier is ever used —
            // identical starting behavior to the pre-fix scan in that case). Only changes the
            // outcome when 2+ threads share bestPrio; a single candidate is found regardless
            // of start position.
            int tierStartTid = _lastPickedTidByPriority.TryGetValue(bestPrio, out var lastTid) ? lastTid : afterId;
            int tierStartIdx = idx;
            for (int i = 0; i < _threads.Count; i++)
                if (_threads[i].Id == tierStartTid) { tierStartIdx = i; break; }

            for (int i = 1; i <= _threads.Count; i++)
            {
                var t = _threads[(tierStartIdx + i) % _threads.Count];
                if (t.Id == afterId) continue;
                if (IsRunnable(t) && t.Priority == bestPrio)
                {
                    _lastPickedTidByPriority[bestPrio] = t.Id;
                    return t.Id;
                }
            }
            return afterId;
        }

        // Default commercial RR (pre-G0): ignore Priority field; circular after afterId.
        // i < Count (not <=): must NOT wrap onto afterId itself (see historical thrash note).
        for (int i = 1; i < _threads.Count; i++)
        {
            var t = _threads[(idx + i) % _threads.Count];
            if (t.Id == afterId) continue;
            // Match pre-G0: Alive+Started+!Sleeping+!WaitVblank. Also respect Suspend nest.
            if (t.Alive && t.Started && !t.Sleeping && !t.WaitVblank && t.SuspendCount == 0)
                return t.Id;
        }
        // Also allow main thread (id 1) even if Started flag never set
        var main = FindThread(1);
        if (main != null && main.Id != afterId && main.Alive && !main.Sleeping
            && main.SuspendCount == 0)
            return 1;
        return afterId;
    }

    public Thread? GetThread(int id) => FindThread(id);
    public IReadOnlyList<Thread> AllThreads => _threads;

    /// <summary>Save context from EE into the current thread slot.</summary>
    /// <param name="fromSyscall">When true, resume at PC+4 (skip SYSCALL insn).</param>
    /// <remarks>
    /// Always snapshots the full GPR file. Partial (callee-saved-only) saves leaked the previous
    /// thread's v0/v1/a0-a3 across SwitchToNext — e.g. WaitSema stub leaves v1=0x44, then the
    /// resumed thread's INTC busy-poll does lw via v1 and reads address 0x44 forever
    /// (Shaolin Monks 0x4803D0, 2026-07-29). Full save is cheap relative to that class of bug.
    /// While EXL is set <b>or</b> an HLE INTC dispatch frame is outstanding (software may clear
    /// EXL mid-handler), prefer keeping CaptureInterruptedContext's user snapshot.
    /// </remarks>
    public void SaveCurrentContext(EmotionEngine ee, bool fromSyscall = true)
    {
        var t = FindThread(_currentTid);
        if (t == null) return;
        // From SYSCALL: PC is the SYSCALL insn → resume after it.
        // From preemptive yield: PC is the next insn to run → keep as-is.
        ulong resumePc = fromSyscall ? ee.PC + 4 : ee.PC;

        // Inside ISR: do not overwrite the interrupted-user full snapshot with ISR GPRs.
        // EXL alone is not enough — registered handlers (GoW/SotC) clear EXL via ERL critical
        // sections while _savedGprAcrossIntcDispatch still holds the user frame.
        bool inHleIsr = (ee.COP0_Status & 0x2) != 0 || ee.HasOutstandingIntcDispatch;
        if (inHleIsr && t.HasFullSave && t.SavedGprFull != null)
        {
            // Still record that this thread is blocked in the ISR (for bookkeeping only).
            t.SavedSp = ee.GetGpr(29).Lo;
            LogThreadEvent("SaveOutIsr", _currentTid, t.SavedPc, t.SavedSp, fromSyscall ? "fromSyscall" : "cooperative");
            return;
        }

        t.SavedPc = resumePc;
        t.SavedGprFull ??= new ulong[32];
        for (int i = 0; i < 32; i++)
            t.SavedGprFull[i] = ee.GetGpr(i).Lo;
        // Resume PC may differ from current PC (syscall +4); keep that in SavedPc / slot 0 unused.
        t.HasFullSave = true;
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
            Console.Error.WriteLine($"[RESTORE1] cyc={CurrentCycle} wasStarted={t.Started} wasSleeping={t.Sleeping} savedPc=0x{t.SavedPc:X8} hasFull={t.HasFullSave}");

        // Force-preempted threads (MaybePreempt → SaveFullContext) may be resumed here via
        // SwitchToNext after another thread's WaitSema/Sleep — NOT only via RestoreFullContext.
        // Partial restore (callee-saved only) would resume at SavedPc mid-memset/memcpy with
        // garbage a0/a2/t* and self-corrupt code (Shaolin Monks 0x474814, 2026-07-29).
        if (t.HasFullSave && t.SavedGprFull != null && !t.FreshStart)
        {
            _currentTid = id;
            ulong fpc = t.SavedPc != 0 ? t.SavedPc : t.Entry;
            if (fpc == 0) return false;
            if (fromSyscall)
                ee.HleRedirectPc = fpc;
            else
                ee.PC = fpc;
            for (int i = 1; i < 32; i++)
                ee.SetGpr(i, new EmotionEngine.Gpr128 { Lo = t.SavedGprFull[i] });
            // Snapshot consumed — next leave must SaveFullContext again if preempted.
            // Apply waiter return ($v0) before clearing HasFullSave so DeleteSema/ReleaseWait codes stick.
            ApplyWaitReturnIfAny(ee, t);
            t.HasFullSave = false;
            t.Sleeping = false;
            t.Started = true;
            LogThreadEvent("SwitchToFull", id, fpc, t.SavedGprFull[29], fromSyscall ? "fromSyscall" : "cooperative");
            return true;
        }

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
        ApplyWaitReturnIfAny(ee, t);
        LogThreadEvent("SwitchTo", id, pc, t.SavedSp, fromSyscall ? "fromSyscall" : "cooperative");
        return true;
    }

    /// <summary>Patch $v0 when DeleteSema/ReleaseWait/etc. set a waiter return code.</summary>
    private static void ApplyWaitReturnIfAny(EmotionEngine ee, Thread t)
    {
        if (!t.HasWaitReturn) return;
        ulong v = unchecked((ulong)(long)t.WaitReturnCode);
        ee.SetGpr(2, new EmotionEngine.Gpr128 { Lo = v });
        if (t.HasFullSave && t.SavedGprFull != null && t.SavedGprFull.Length > 2)
            t.SavedGprFull[2] = v;
        t.HasWaitReturn = false;
    }

    /// <summary>Record waiter return code and optionally patch a full save's $v0 before resume.</summary>
    private static void SetWaitReturn(Thread t, int code)
    {
        t.HasWaitReturn = true;
        t.WaitReturnCode = code;
        if (t.HasFullSave && t.SavedGprFull != null && t.SavedGprFull.Length > 2)
            t.SavedGprFull[2] = unchecked((ulong)(long)code);
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
            // Self-wake only for temporary SleepThread/WaitSema — never for SuspendThread
            // nest (would undo Suspend the same cycle and recreate the ADX thrash loop).
            if (cur != null && cur.Sleeping && cur.Started && cur.SuspendCount == 0)
            {
                cur.Sleeping = false;
                cur.WaitSemaId = 0;
            }
            return false;
        }
        return RestoreContext(ee, next, fromSyscall);
    }

    /// <summary>Start thread and optionally switch to it immediately (first-run boost).</summary>
    /// <remarks>
    /// Classic resume is stub <c>PC+4</c> (<c>jr ra</c>) so the caller epilogue after the
    /// <c>jal</c> into the SCE kernel stub runs normally.
    ///
    /// Haven SLUS_205.17 WAVE-5 residual: StartThread switchNow → worker WaitSema yield
    /// restored main at stub <c>jr ra</c> but an HleRedirect / PC+4 interaction continued at
    /// the delay-slot nop and fell through into the next packed trampoline
    /// (<c>ExitThread</c>). A broad "always resume at <c>$ra</c>" plant fixed Haven but
    /// <b>broke God of War</b> at the first StartThread (tid2 @0x2947C8, ~274k cycles):
    /// main SavedPc pinned to the jal return, stack/ra epilogue desynced, <c>Started=false</c>,
    /// forever WaitSema thrash, cdvd=0 / gifP2=0 (wave-7 had cdvd=555 gifP2=962).
    ///
    /// WAVE-8b (GoW): keep classic <c>fromSyscall</c> PC+4 always. Haven's ExitThread
    /// fall-through is covered by restoring full SP/s-regs in
    /// <c>SonyKernelHle.InvokeRpcEndFunction</c> and title-side stall clear — not by
    /// rewriting every StartThread resume PC.
    /// </remarks>
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

    /// <summary>
    /// Switch to another runnable thread if one exists, without the SwitchToNext self-wake
    /// fallback. Used by EmotionEngine's sema-stall recovery when the current waiter is still
    /// Sleeping but drain (or another path) made a different thread ready.
    /// </summary>
    public bool TryYieldToOtherRunnable(EmotionEngine ee)
    {
        int next = FindNextRunnable(_currentTid);
        if (next == _currentTid) return false;
        SaveCurrentContext(ee, fromSyscall: false);
        return RestoreContext(ee, next, fromSyscall: false);
    }

    private uint _preemptQuantum = 0x10000; // ~65536 EE cycles per timeslice
    private ulong _cyclesSinceLastPreempt;

    /// <summary>
    /// Force the next <see cref="MaybePreempt"/> tick to rotate (Whiplash WaitSema soft-signal
    /// empty SIF poll — without this the worker re-enters WaitSema every ~60 cycles and never
    /// reaches the 64k quantum while main is mid stream-init / GOE Open).
    /// </summary>
    public void RequestImmediatePreempt() => _cyclesSinceLastPreempt = _preemptQuantum;

    /// <summary>Drop HasFullSave on the current thread (after eret restored user GPRs into EE).</summary>
    public void ClearFullSaveIfCurrent()
    {
        var t = FindThread(_currentTid);
        if (t != null) t.HasFullSave = false;
    }

    /// <summary>
    /// Publish the interrupted user GPR file (from INTC dispatch) onto the current thread so
    /// cooperative SwitchToNext from inside the ISR can full-restore, not partial-restore.
    /// </summary>
    public void CaptureInterruptedContext(EmotionEngine ee, ulong[] gprs)
    {
        var t = FindThread(_currentTid);
        if (t == null || gprs == null || gprs.Length < 32) return;
        t.SavedGprFull ??= new ulong[32];
        Array.Copy(gprs, t.SavedGprFull, 32);
        t.HasFullSave = true;
        t.SavedPc = ee.PC; // PC still points at interrupted instruction when this is called
        t.SavedSp = gprs[29];
        t.SavedGp = gprs[28];
        t.SavedRa = gprs[31];
        t.SavedS0 = gprs[16];
        t.SavedS1 = gprs[17];
        t.SavedS2 = gprs[18];
        t.SavedS3 = gprs[19];
        t.SavedS4 = gprs[20];
        t.SavedS5 = gprs[21];
        t.SavedS6 = gprs[22];
        t.SavedS7 = gprs[23];
        t.SavedS8 = gprs[30];
        t.SavedFp = gprs[30];
    }

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
        ApplyWaitReturnIfAny(ee, t);
        // Don't clear SuspendThread park on preempt-in
        if (t.SuspendCount == 0)
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
        // Force preemption stays ON by default (busy-loops like Midway ADX lock-wait at
        // 0x4145xx never call WaitSema — without timeslice SM freezes sifBytes~2k).
        // Pair with RR FindNextRunnable (DETPS2_PRIO_SCHED off) — priority+preempt combo
        // caused Exit@12.4M on SLUS_210.87. DETPS2_NO_PREEMPT=1 disables for A/B.
        if (string.Equals(Environment.GetEnvironmentVariable("DETPS2_NO_PREEMPT"), "1",
                StringComparison.Ordinal))
            return;

        _cyclesSinceLastPreempt++;
        if (_cyclesSinceLastPreempt < _preemptQuantum) return;
        _cyclesSinceLastPreempt = 0;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_PREEMPT") == "1")
            Console.Error.WriteLine($"[PREEMPT] tick threads={_threads.Count} cur={_currentTid} pc=0x{ee.PC:X8}");
        if (_threads.Count < 2) return; // common case: nothing else to switch to

        // Never force-preempt across an HLE INTC episode. SaveFullContext would overwrite
        // CaptureInterruptedContext's user GPR file with ISR scratch (v1=sema-id etc.), so
        // eret/SwitchToNext resumes a busy-poll with a garbage base register. Real hardware
        // keeps the user frame on the kernel stack for the whole exception episode.
        if ((ee.COP0_Status & 0x2) != 0 || ee.HasOutstandingIntcDispatch)
            return;

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
                // Do not clear Sleeping if SuspendThread is still nested — that turned
                // Suspend+WaitSemaVblank into "return every frame" and left main spinning
                // at the Suspend stub (MK 0x47FDD8, ~600 Suspends/150M with no Resume).
                if (t.SuspendCount == 0 && t.WaitSemaId == 0)
                    t.Sleeping = false;
            }
        }
        // ~1/60s ≈ 16667 µs — DelayThread alarm path (FUN_00002444) via VBlank ticks.
        TickDelays(16667);
    }

    public int CreateSema(int init, int max)
    {
        int id = _nextSema++;
        int m = max > 0 ? max : 1;
        int c = init < 0 ? 0 : (init > m ? m : init);
        _semas[id] = new Sema { Id = id, Count = c, MaxCount = m, InitCount = c };
        return id;
    }

    /// <summary>
    /// Materialize a semaphore at a specific id (Sony WaitSema race-tolerant auto-create).
    /// Plain <see cref="CreateSema"/> always allocates <c>_nextSema++</c>, which cannot
    /// satisfy a waiter that already holds a concrete id from a peer Create that HLE never
    /// observed. No-ops if the id already exists.
    /// </summary>
    public int EnsureSema(int id, int init = 0, int max = 1)
    {
        if (id <= 0) return -1;
        if (_semas.ContainsKey(id)) return id;
        int m = max > 0 ? max : 1;
        int c = init < 0 ? 0 : (init > m ? m : init);
        _semas[id] = new Sema { Id = id, Count = c, MaxCount = m, InitCount = c };
        if (id >= _nextSema)
            _nextSema = id + 1;
        return id;
    }

    /// <summary>
    /// BIOS THREADMAN DeleteSema (FUN_00003164): remove the object and wake every waiter.
    /// Real IOP writes waiter return <c>0xfffffe57</c> (KeWaitDelete); EE HLE stores that on
    /// the thread for $v0 patch on restore, and leaves Suspend nest intact.
    /// </summary>
    public int DeleteSema(int id)
    {
        if (!_semas.Remove(id)) return -1;
        foreach (var t in _threads)
        {
            if (t.Alive && t.Sleeping && t.WaitSemaId == id)
            {
                SetWaitReturn(t, KeWaitDelete);
                ClearSemaWait(t);
            }
        }
        return 0;
    }

    /// <summary>
    /// BIOS THREADMAN / EE ReleaseWaitThread (syscall 0x2D): force-release any wait
    /// (Sleep / WaitSema / ReceiveMbx / Delay / event flag / pool) with return
    /// <see cref="KeReleaseWait"/> (<c>0xfffffe5e</c>).
    /// </summary>
    public int ReleaseWaitThread(int id)
    {
        var t = FindThread(id == 0 ? _currentTid : id);
        if (t == null || !t.Alive) return -1;
        bool waiting = t.Sleeping || t.WaitVblank || t.WaitSemaId != 0 || t.WaitEfId != 0
            || t.WaitMbxId != 0 || t.WaitVplId != 0 || t.WaitFplId != 0 || t.DelayRemainingUs > 0;
        if (!waiting) return -1;
        SetWaitReturn(t, KeReleaseWait);
        ClearAllWaits(t);
        return 0;
    }

    /// <summary>Clear every cooperative wait reason (not Suspend nest).</summary>
    private void ClearAllWaits(Thread t)
    {
        t.WaitSemaId = 0;
        t.WaitEfId = 0;
        t.WaitMbxId = 0;
        t.WaitVplId = 0;
        t.WaitVplSize = 0;
        t.WaitFplId = 0;
        t.DelayRemainingUs = 0;
        t.WaitVblank = false;
        if (t.SuspendCount == 0)
            t.Sleeping = false;
    }

    /// <summary>Non-mutating existence check — unlike WaitSemaBlocking, does not consume a count.</summary>
    public bool SemaExists(int id) => _semas.ContainsKey(id);

    /// <summary>Live count for ReferSemaStatus / diagnostics; −1 if missing.</summary>
    public int GetSemaCount(int id) => _semas.TryGetValue(id, out var s) ? s.Count : -1;

    /// <summary>Create-time init_count (THREADMAN +0x28); −1 if missing.</summary>
    public int GetSemaInitCount(int id) => _semas.TryGetValue(id, out var s) ? s.InitCount : -1;

    /// <summary>Create-time max_count; −1 if missing.</summary>
    public int GetSemaMaxCount(int id) => _semas.TryGetValue(id, out var s) ? s.MaxCount : -1;

    /// <summary>Number of threads currently parked on this sema (THREADMAN +0x10 waiter count).</summary>
    public int CountSemaWaiters(int id)
    {
        int n = 0;
        foreach (var t in _threads)
            if (t.Alive && t.Sleeping && t.WaitSemaId == id) n++;
        return n;
    }

    /// <summary>
    /// Drop a WaitSema park without violating SuspendThread. Mirrors WakeupThread /
    /// OnVblank: Suspend nest keeps Sleeping=true so ReferThreadStatus still shows
    /// THS_SUSPEND (and FIND-NEXT does not schedule the peer).
    /// </summary>
    private static void ClearSemaWait(Thread t)
    {
        t.WaitSemaId = 0;
        if (t.SuspendCount == 0)
            t.Sleeping = false;
    }

    /// <summary>
    /// BIOS THREADMAN SignalSema (Ghidra FUN_0000328c / tools/bios-decomp/THREADMAN_ALL.txt):
    /// if any waiter is queued → wake exactly one (do not also bump count);
    /// else if count &lt; max → count++;
    /// else error (full). EE RPC uses CreateSema(init=0,max=1) then WaitSema;
    /// double-counting on wake made later WaitSema succeed without a real producer.
    /// </summary>
    public int SignalSema(int id)
    {
        if (!_semas.TryGetValue(id, out var s)) return -1;

        // Prefer waking a waiter (BIOS: wait queue non-empty → ready one thread).
        foreach (var t in _threads)
        {
            if (t.Alive && t.Sleeping && t.WaitSemaId == id)
            {
                ClearSemaWait(t);
                // No count++: the wake *is* the unit of signal consumption.
                return s.Count;
            }
        }

        if (s.Count < s.MaxCount)
        {
            s.Count++;
            return s.Count;
        }
        return -1; // KE_SEMA_OVF style — max already held, no waiter
    }

    /// <summary>
    /// Interrupt-context SignalSema (THREADMAN iSignalSema). Same count/wake rules as
    /// <see cref="SignalSema"/> — EE i-forms only differ in which context may call them.
    /// </summary>
    public int ISignalSema(int id) => SignalSema(id);

    /// <summary>
    /// BIOS THREADMAN PollSema: non-blocking WaitSema. Decrements count if available;
    /// returns negative without sleeping when empty. Distinct from WaitSemaBlocking, which
    /// parks the thread (and was incorrectly used for PollSema before).
    /// </summary>
    public int PollSema(int id)
    {
        if (!_semas.TryGetValue(id, out var s)) return -1;
        if (s.Count > 0)
        {
            s.Count--;
            return s.Count;
        }
        return -1; // KE_SEMA_ZERO — would block
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

    /// <summary>
    /// Set bits on an event flag and wake any parked WaitEventFlag threads whose condition
    /// is now satisfied. Mirrors THREADMAN iSetEventFlag wake semantics so IOP producers
    /// (e.g. <see cref="IopVblankHost"/> PCRTC pulse) release EE/IOP waiters without requiring
    /// the Sony syscall path to run first.
    /// </summary>
    public int SetEventFlag(int id, uint bits)
    {
        if (!_flags.TryGetValue(id, out var f)) return -1;
        f.Bits |= bits;
        foreach (var t in _threads)
        {
            if (!t.Alive || !t.Sleeping || t.WaitEfId != id) continue;
            if (!EventFlagSatisfied(id, t.WaitEfPattern, t.WaitEfMode)) continue;
            // Consume clear-on-exit before clearing WaitEfId so mode bit 0x10 works.
            t.WaitEfLastBits = ConsumeEventFlag(id, t.WaitEfPattern, t.WaitEfMode);
            t.WaitEfNeedsResultWrite = t.WaitEfResultAddr != 0;
            t.WaitEfId = 0;
            // Suspend nest keeps Sleeping (same rule as SignalSema / OnVblank).
            if (t.SuspendCount == 0)
                t.Sleeping = false;
        }
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

    // -------------------------------------------------------------------------
    // Message boxes (THREADMAN thmsgbx / FUN_000037c0…03fa4 — magic 0x7f04)
    // EE has no CreateMbx syscall (ps2sdk kernel.h); public KernelState API for IOP HLE /
    // host tests. Opaque message values are uint pointers (guest message packet addresses).
    // -------------------------------------------------------------------------

    public bool LastReceiveMbxBlocked { get; private set; }

    public int CreateMbx(uint attr = 0, uint option = 0)
    {
        int id = _nextMbx++;
        _mbxs[id] = new Mbx { Id = id, Attr = attr, Option = option };
        return id;
    }

    public int DeleteMbx(int id)
    {
        if (!_mbxs.Remove(id)) return -1;
        foreach (var t in _threads)
        {
            if (t.Alive && t.Sleeping && t.WaitMbxId == id)
            {
                SetWaitReturn(t, KeWaitDelete);
                t.WaitMbxId = 0;
                if (t.SuspendCount == 0) t.Sleeping = false;
            }
        }
        return 0;
    }

    /// <summary>
    /// SendMbx (FUN_00003a84): if a ReceiveMbx waiter exists, deliver to one and wake;
    /// else enqueue the message pointer.
    /// </summary>
    public int SendMbx(int id, uint msgPtr)
    {
        if (!_mbxs.TryGetValue(id, out var m)) return KeUnknownObject;
        foreach (var t in _threads)
        {
            if (t.Alive && t.Sleeping && t.WaitMbxId == id)
            {
                t.MbxReceivedMsg = msgPtr;
                t.WaitMbxId = 0;
                if (t.SuspendCount == 0) t.Sleeping = false;
                return 0;
            }
        }
        m.Messages.Enqueue(msgPtr);
        return 0;
    }

    /// <summary>
    /// ReceiveMbx (FUN_00003c40): dequeue or park. Returns 0 and sets <paramref name="msg"/>;
    /// on block returns −1 with <see cref="LastReceiveMbxBlocked"/> true (caller yields).
    /// </summary>
    public int ReceiveMbx(int id, out uint msg)
    {
        msg = 0;
        LastReceiveMbxBlocked = false;
        if (!_mbxs.TryGetValue(id, out var m)) return KeUnknownObject;
        if (m.Messages.Count > 0)
        {
            msg = m.Messages.Dequeue();
            return 0;
        }
        var t = FindThread(_currentTid);
        if (t == null) return -1;
        t.Sleeping = true;
        t.WaitMbxId = id;
        t.MbxReceivedMsg = 0;
        LastReceiveMbxBlocked = true;
        return -1;
    }

    /// <summary>PollMbx (FUN_00003de4): non-blocking; <see cref="KeMboxNomsg"/> when empty.</summary>
    public int PollMbx(int id, out uint msg)
    {
        msg = 0;
        if (!_mbxs.TryGetValue(id, out var m)) return KeUnknownObject;
        if (m.Messages.Count == 0) return KeMboxNomsg;
        msg = m.Messages.Dequeue();
        return 0;
    }

    /// <summary>Take message delivered to a woken ReceiveMbx waiter (after SendMbx rendezvous).</summary>
    public uint TakeMbxReceivedMsg(int threadId = 0)
    {
        var t = FindThread(threadId == 0 ? _currentTid : threadId);
        if (t == null) return 0;
        uint m = t.MbxReceivedMsg;
        t.MbxReceivedMsg = 0;
        return m;
    }

    public int ReferMbx(int id, out uint attr, out uint option, out int numMessages, out int numWaiters)
    {
        attr = option = 0;
        numMessages = numWaiters = 0;
        if (!_mbxs.TryGetValue(id, out var m)) return KeUnknownObject;
        attr = m.Attr;
        option = m.Option;
        numMessages = m.Messages.Count;
        foreach (var t in _threads)
            if (t.Alive && t.Sleeping && t.WaitMbxId == id) numWaiters++;
        return 0;
    }

    public bool MbxExists(int id) => _mbxs.ContainsKey(id);

    // -------------------------------------------------------------------------
    // Variable pools (THREADMAN thvpool / FUN_00004020…047b0 — magic 0x7f05)
    // Host freelist with synthetic pointer cookies (not mapped RDRAM).
    // -------------------------------------------------------------------------

    public bool LastAllocateVplBlocked { get; private set; }

    public int CreateVpl(int size, uint attr = 0, uint option = 0)
    {
        if (size <= 0) return -1;
        int id = _nextVpl++;
        uint basePtr = _nextPoolCookie;
        // Reserve a cookie region large enough for the pool (align 16)
        uint span = (uint)((size + 15) & ~15);
        _nextPoolCookie = basePtr + span + 0x1000;
        var v = new Vpl
        {
            Id = id,
            Attr = attr,
            Option = option,
            PoolSize = size,
            FreeBytes = size,
            BasePtr = basePtr
        };
        v.FreeBlocks.Add((basePtr, size));
        _vpls[id] = v;
        return id;
    }

    public int DeleteVpl(int id)
    {
        if (!_vpls.Remove(id)) return -1;
        foreach (var t in _threads)
        {
            if (t.Alive && t.Sleeping && t.WaitVplId == id)
            {
                SetWaitReturn(t, KeWaitDelete);
                t.WaitVplId = 0;
                t.WaitVplSize = 0;
                if (t.SuspendCount == 0) t.Sleeping = false;
            }
        }
        return 0;
    }

    private static int TryAllocVpl(Vpl v, int size, out uint ptr)
    {
        ptr = 0;
        if (size <= 0 || size > v.PoolSize) return -1;
        for (int i = 0; i < v.FreeBlocks.Count; i++)
        {
            var (fp, fs) = v.FreeBlocks[i];
            if (fs < size) continue;
            ptr = fp;
            v.FreeBlocks.RemoveAt(i);
            int rem = fs - size;
            if (rem > 0)
                v.FreeBlocks.Insert(i, (fp + (uint)size, rem));
            v.UsedBlocks.Add((ptr, size));
            v.FreeBytes -= size;
            return 0;
        }
        return KeNoMemory;
    }

    /// <summary>AllocateVpl (FUN_00004258): park when no free block (blocking).</summary>
    public int AllocateVpl(int id, int size, out uint ptr)
    {
        ptr = 0;
        LastAllocateVplBlocked = false;
        if (!_vpls.TryGetValue(id, out var v)) return KeUnknownObject;
        int r = TryAllocVpl(v, size, out ptr);
        if (r == 0) return 0;
        if (r == -1) return -1; // size invalid
        var t = FindThread(_currentTid);
        if (t == null) return KeNoMemory;
        t.Sleeping = true;
        t.WaitVplId = id;
        t.WaitVplSize = size;
        t.VplAllocatedPtr = 0;
        LastAllocateVplBlocked = true;
        return -1;
    }

    /// <summary>Non-blocking AllocateVpl (FUN_0000440c / poll path).</summary>
    public int PollAllocateVpl(int id, int size, out uint ptr)
    {
        ptr = 0;
        if (!_vpls.TryGetValue(id, out var v)) return KeUnknownObject;
        int r = TryAllocVpl(v, size, out ptr);
        return r == 0 ? 0 : (r == -1 ? -1 : KeNoMemory);
    }

    public int FreeVpl(int id, uint ptr)
    {
        if (!_vpls.TryGetValue(id, out var v)) return KeUnknownObject;
        int idx = -1;
        int size = 0;
        for (int i = 0; i < v.UsedBlocks.Count; i++)
        {
            if (v.UsedBlocks[i].Ptr == ptr)
            {
                idx = i;
                size = v.UsedBlocks[i].Size;
                break;
            }
        }
        if (idx < 0) return -1;
        v.UsedBlocks.RemoveAt(idx);
        // Coalesce into free list (simple insert + adjacent merge)
        v.FreeBlocks.Add((ptr, size));
        v.FreeBytes += size;
        CoalesceFree(v);

        // Wake one AllocateVpl waiter if a block can now satisfy them
        foreach (var t in _threads)
        {
            if (!t.Alive || !t.Sleeping || t.WaitVplId != id) continue;
            if (TryAllocVpl(v, t.WaitVplSize, out uint wptr) != 0) continue;
            t.VplAllocatedPtr = wptr;
            t.WaitVplId = 0;
            t.WaitVplSize = 0;
            if (t.SuspendCount == 0) t.Sleeping = false;
            break;
        }
        return 0;
    }

    private static void CoalesceFree(Vpl v)
    {
        if (v.FreeBlocks.Count < 2) return;
        v.FreeBlocks.Sort((a, b) => a.Ptr.CompareTo(b.Ptr));
        var merged = new List<(uint Ptr, int Size)>();
        var cur = v.FreeBlocks[0];
        for (int i = 1; i < v.FreeBlocks.Count; i++)
        {
            var n = v.FreeBlocks[i];
            if (cur.Ptr + (uint)cur.Size == n.Ptr)
                cur = (cur.Ptr, cur.Size + n.Size);
            else
            {
                merged.Add(cur);
                cur = n;
            }
        }
        merged.Add(cur);
        v.FreeBlocks.Clear();
        v.FreeBlocks.AddRange(merged);
    }

    public uint TakeVplAllocatedPtr(int threadId = 0)
    {
        var t = FindThread(threadId == 0 ? _currentTid : threadId);
        if (t == null) return 0;
        uint p = t.VplAllocatedPtr;
        t.VplAllocatedPtr = 0;
        return p;
    }

    public int ReferVpl(int id, out uint attr, out uint option, out int poolSize, out int freeSize, out int numWaiters)
    {
        attr = option = 0;
        poolSize = freeSize = numWaiters = 0;
        if (!_vpls.TryGetValue(id, out var v)) return KeUnknownObject;
        attr = v.Attr;
        option = v.Option;
        poolSize = v.PoolSize;
        freeSize = v.FreeBytes;
        foreach (var t in _threads)
            if (t.Alive && t.Sleeping && t.WaitVplId == id) numWaiters++;
        return 0;
    }

    public bool VplExists(int id) => _vpls.ContainsKey(id);

    // -------------------------------------------------------------------------
    // Fixed pools (THREADMAN thfpool / FUN_00004830… — magic 0x7f06)
    // -------------------------------------------------------------------------

    public bool LastAllocateFplBlocked { get; private set; }

    public int CreateFpl(int blockSize, int blockCount, uint attr = 0, uint option = 0)
    {
        if (blockSize <= 0 || blockCount <= 0) return -1;
        int aligned = (blockSize + 3) & ~3;
        int id = _nextFpl++;
        uint basePtr = _nextPoolCookie;
        uint span = (uint)(aligned * blockCount);
        _nextPoolCookie = basePtr + span + 0x1000;
        var f = new Fpl
        {
            Id = id,
            Attr = attr,
            Option = option,
            BlockSize = aligned,
            BlockCount = blockCount,
            BasePtr = basePtr
        };
        for (int i = 0; i < blockCount; i++)
            f.FreeBlocks.Enqueue(basePtr + (uint)(i * aligned));
        _fpls[id] = f;
        return id;
    }

    public int DeleteFpl(int id)
    {
        if (!_fpls.Remove(id)) return -1;
        foreach (var t in _threads)
        {
            if (t.Alive && t.Sleeping && t.WaitFplId == id)
            {
                SetWaitReturn(t, KeWaitDelete);
                t.WaitFplId = 0;
                if (t.SuspendCount == 0) t.Sleeping = false;
            }
        }
        return 0;
    }

    public int AllocateFpl(int id, out uint ptr)
    {
        ptr = 0;
        LastAllocateFplBlocked = false;
        if (!_fpls.TryGetValue(id, out var f)) return KeUnknownObject;
        if (f.FreeBlocks.Count > 0)
        {
            ptr = f.FreeBlocks.Dequeue();
            f.UsedBlocks.Add(ptr);
            return 0;
        }
        var t = FindThread(_currentTid);
        if (t == null) return KeNoMemory;
        t.Sleeping = true;
        t.WaitFplId = id;
        t.FplAllocatedPtr = 0;
        LastAllocateFplBlocked = true;
        return -1;
    }

    public int PollAllocateFpl(int id, out uint ptr)
    {
        ptr = 0;
        if (!_fpls.TryGetValue(id, out var f)) return KeUnknownObject;
        if (f.FreeBlocks.Count == 0) return KeNoMemory;
        ptr = f.FreeBlocks.Dequeue();
        f.UsedBlocks.Add(ptr);
        return 0;
    }

    public int FreeFpl(int id, uint ptr)
    {
        if (!_fpls.TryGetValue(id, out var f)) return KeUnknownObject;
        if (!f.UsedBlocks.Remove(ptr)) return -1;
        // Prefer waking a waiter over returning to free list first
        foreach (var t in _threads)
        {
            if (!t.Alive || !t.Sleeping || t.WaitFplId != id) continue;
            t.FplAllocatedPtr = ptr;
            t.WaitFplId = 0;
            f.UsedBlocks.Add(ptr);
            if (t.SuspendCount == 0) t.Sleeping = false;
            return 0;
        }
        f.FreeBlocks.Enqueue(ptr);
        return 0;
    }

    public uint TakeFplAllocatedPtr(int threadId = 0)
    {
        var t = FindThread(threadId == 0 ? _currentTid : threadId);
        if (t == null) return 0;
        uint p = t.FplAllocatedPtr;
        t.FplAllocatedPtr = 0;
        return p;
    }

    public int ReferFpl(int id, out uint attr, out uint option, out int blockSize, out int freeBlocks, out int numWaiters)
    {
        attr = option = 0;
        blockSize = freeBlocks = numWaiters = 0;
        if (!_fpls.TryGetValue(id, out var f)) return KeUnknownObject;
        attr = f.Attr;
        option = f.Option;
        blockSize = f.BlockSize;
        freeBlocks = f.FreeBlocks.Count;
        foreach (var t in _threads)
            if (t.Alive && t.Sleeping && t.WaitFplId == id) numWaiters++;
        return 0;
    }

    public bool FplExists(int id) => _fpls.ContainsKey(id);

    // -------------------------------------------------------------------------
    // DelayThread (THREADMAN FUN_00002444) — alarm-style park; not an EE syscall.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Park current thread for approximately <paramref name="usec"/> microseconds.
    /// Released by <see cref="TickDelays"/> (also called from <see cref="OnVblank"/> with ~16667 µs).
    /// </summary>
    public int DelayThread(int usec)
    {
        var t = FindThread(_currentTid);
        if (t == null) return -1;
        if (usec <= 0) return 0;
        t.Sleeping = true;
        t.DelayRemainingUs = usec;
        // Pure delay: not a WaitSema/Mbx park
        t.WaitSemaId = 0;
        return 0;
    }

    /// <summary>
    /// Advance DelayThread parks by <paramref name="usec"/> microseconds.
    /// Called from <see cref="OnVblank"/> (~16667 µs/frame). Returns woken count.
    /// </summary>
    public int TickDelays(int usec)
    {
        if (usec <= 0) return 0;
        int woken = 0;
        foreach (var t in _threads)
        {
            if (!t.Alive || t.DelayRemainingUs <= 0) continue;
            t.DelayRemainingUs -= usec;
            if (t.DelayRemainingUs > 0) continue;
            t.DelayRemainingUs = 0;
            if (t.SuspendCount == 0 && t.WaitSemaId == 0 && t.WaitEfId == 0
                && t.WaitMbxId == 0 && t.WaitVplId == 0 && t.WaitFplId == 0)
            {
                t.Sleeping = false;
                woken++;
            }
        }
        return woken;
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
