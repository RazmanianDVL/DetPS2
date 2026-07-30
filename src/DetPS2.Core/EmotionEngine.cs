using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DetPS2.Core;

/// <summary>
/// Emotion Engine (R5900) — Phase 12: COP0, ERET, exception vectors, more loads.
/// </summary>
public sealed class EmotionEngine : ISchedulable
{
    // COP0 register numbers (MIPS R5900 subset)
    public const int Cop0Index = 0;
    public const int Cop0Random = 1;
    public const int Cop0EntryLo0 = 2;
    public const int Cop0EntryLo1 = 3;
    public const int Cop0Context = 4;
    public const int Cop0PageMask = 5;
    public const int Cop0Wired = 6;
    public const int Cop0BadVAddr = 8;
    public const int Cop0Count = 9;
    public const int Cop0EntryHi = 10;
    public const int Cop0Compare = 11;
    public const int Cop0Status = 12;
    public const int Cop0Cause = 13;
    public const int Cop0Epc = 14;
    public const int Cop0PrId = 15;
    public const int Cop0Config = 16;
    public const int Cop0ErrorEpc = 30;

    private readonly SystemMemory _memory;
    private Vu0? _vu0;
    private Intc? _intc;
    private BiosHle? _hle;
    private bool _takeExceptions;
    /// <summary>Diagnostic-only (DETPS2_TRACE_IRQLOOP): counts consecutive Step() loop iterations
    /// that re-enter the interrupt-dispatch branch without ever reaching real instruction
    /// execution in between — used to detect a genuine unacknowledged-interrupt re-entry loop.</summary>
    private ulong _irqLoopStreak;
    /// <summary>Set when a thread implicitly exits via `jr ra` with ra==0 (see the JR case below)
    /// but SwitchToNext finds no other runnable thread. Real hardware has nothing meaningful left
    /// to execute in that state; without a genuine stall here, the interpreter previously fell
    /// through and started executing whatever raw bytes happened to sit at the delay slot and
    /// beyond as if they were real instructions, producing nonsense register values (e.g. a small
    /// integer where a pointer was expected) and eventually a spurious, unintended syscall —
    /// confirmed via Mortal Kombat: Shaolin Monks (see docs/DEVELOPER_GUIDE.md, the
    /// `[MSGBUF-A0] a0=0xB ra=0` / `[ABORT-CALLER] ra=0` traces). Checked at the top of Step();
    /// cleared automatically the moment SwitchToNext finds a real thread to run.</summary>
    private bool _pendingThreadStall;
    /// <summary>Break open-bus rescue thrash when the same non-progressing target is re-chosen.</summary>
    private uint _openBusLastResume;
    private int _openBusSameTargetHits;

    /// <summary>Set by SonyKernelHle's WaitSema handler when a thread genuinely blocks on a
    /// semaphore AND there is a real, already-queued SIF RPC (Sif.RealRpcQueueCount &gt; 0) that
    /// will eventually resolve it via DrainRealRpcQueue -> RealSifRpc.TryHandle -> SignalSema.
    /// Unlike _pendingThreadStall's retry loop, this does NOT call SwitchToNext each cycle —
    /// SwitchToNext's own "wake ourselves if nothing else runnable" fallback (KernelHle.cs)
    /// would immediately undo WaitSemaBlocking's Sleeping=true/WaitSemaId=id bookkeeping before
    /// the real async response ever had a chance to arrive, which is exactly the fabrication bug
    /// this field exists to avoid (see DEVELOPER_GUIDE.md, 2026-07-28). Instead this just polls
    /// whether the current thread is still Sleeping — cleared the moment the real SignalSema
    /// call (fired for real, not fabricated) wakes it.</summary>
    private bool _pendingSemaStall;
    private bool _preferHleSyscalls = true;
    private int _cop2StallRemaining;
    private bool _cacheModelEnabled;
    private ulong _cacheLineHits;
    private ulong _cacheLineMisses;
    private ulong _lastCacheLine = ulong.MaxValue;
    private Debugger? _debugger;
    private Tracer? _tracer;
    private Telemetry? _telemetry;
    private Func<ulong>? _cycleSource;
    private uint _cop0Count;
    private uint _cop0Compare;
    private uint _cop0BadVAddr;
    private uint _cop0ErrorEpc;
    private uint _cop0Config = 0x00010400; // plausible stub
    private const uint Cop0PrIdValue = 0x00002E20; // R5900-ish

    public ulong PC { get; set; } = 0xBFC00000;
    /// <summary>When set by HLE during SYSCALL, EE jumps here next (no delay slot).</summary>
    public ulong? HleRedirectPc { get; set; }

    public struct Gpr128
    {
        public ulong Lo;
        public ulong Hi;

        public override string ToString() => $"0x{Hi:X16}_{Lo:X16}";
    }

    private readonly Gpr128[] _gprs = new Gpr128[32];

    public ulong LO { get; set; }
    public ulong HI { get; set; }

    /// <summary>Optional mid-slice hook (e.g. CRI HLE stubs). Invoked every 64 EE instructions
    /// when set; keep the handler cheap — it is on the hot path.</summary>
    public Action<EmotionEngine>? MidInstructionHook { get; set; }

    /// <summary>Diagnostic-only: logs v0/v1/ra to stderr every time PC hits this address.
    /// Opt-in via blocker-trace --pcbreak=ADDR; null (default) costs one branch per Step().</summary>
    public static uint? PcBreakGpr;
    /// <summary>End of an inclusive PC range for PcBreakGpr (opt-in via --pcbreak=START:END) —
    /// dumps registers at EVERY instruction in the range, not just one address, so a loop's
    /// register state can be seen evolving instruction-by-instruction instead of only at one
    /// fixed point per iteration. Built specifically because --trace-chrono shows opcodes with
    /// no register state and single-address --pcbreak shows register state at only one PC per
    /// iteration — neither alone is enough to resolve a step-by-step register-flow question (see
    /// DEVELOPER_GUIDE.md §7.4, the material-string self-overlapping-copy investigation).
    /// Null (default) means single-address mode, matching the original behavior exactly.</summary>
    public static uint? PcBreakEnd;

    /// <summary>Diagnostic-only: logs every time the JR/JALR "ignore jumps into the low vector
    /// page" guard fires (i.e. an uninitialized/garbage function pointer would otherwise have
    /// sent PC into the BIOS vector page, so the jump is silently dropped and execution falls
    /// through into whatever code happens to sit immediately after the jr/jalr instead). Opt-in
    /// via DETPS2_TRACE_JRGUARD=1 — used to find the true root cause of a corrupted `ra`/function
    /// pointer, since the guard itself only hides the symptom (a would-be crash) rather than
    /// explaining why the register was bad in the first place.</summary>
    public static readonly bool TraceJrGuard = Environment.GetEnvironmentVariable("DETPS2_TRACE_JRGUARD") == "1";

    /// <summary>MMI "pipeline 1" HI/LO — real R5900 HI/LO are 128-bit registers; regular
    /// MULT/DIV/MADD use the lower 64 bits (HI/LO above), MULT1/DIV1/MADD1/MFHI1/MTHI1/
    /// MFLO1/MTLO1 use this independent upper-64-bit lane.</summary>
    public ulong LO1 { get; set; }
    public ulong HI1 { get; set; }

    /// <summary>Real R5900 "SA" register — a byte-granular shift amount set by the REGIMM
    /// extensions MTSAB (rt=0x18) / MTSAH (rt=0x19) and consumed by QFSRV (a 256-bit funnel
    /// shift used by real compiled code for unaligned quadword-granularity memory copies, the
    /// same class of problem LWL/LWR/SWL/SWR/LDL/LDR/SDL/SDR solve at word/dword granularity —
    /// see docs/DEVELOPER_GUIDE.md §7.6 for that fix). Stored in BITS (not bytes) to match the
    /// real hardware register directly: MTSAB shifts its computed 0-15 value left by 3 (byte
    /// granularity, ×8), MTSAH shifts its computed 0-7 value left by 4 (halfword granularity,
    /// ×16) — both land in the same underlying bit-unit register. Semantics for both this field
    /// and QFSRV verified byte-for-byte against the Play! PS2 emulator's own JIT implementation
    /// (github.com/jpd002/Play-, Source/ee/MA_EE.cpp's MTSAB/MTSAH/QFSRV) and its CodeGen test
    /// suite (github.com/jpd002/Play--CodeGen, tests/MdTest.cpp's MD_Srl256 cases), not guessed.</summary>
    private uint _sa;

    public uint COP0_Status { get; set; }
    public uint COP0_Cause { get; set; }
    public ulong COP0_EPC { get; set; }
    public uint COP0_Count => _cop0Count;
    public uint COP0_Compare => _cop0Compare;
    public uint COP0_BadVAddr => _cop0BadVAddr;

    /// <summary>True when INTC has unmasked pending IRQs and COP0 Status IE/EIE allow them.</summary>
    public bool InterruptPending { get; private set; }
    public ulong ExceptionCount { get; private set; }
    public ulong EretCount { get; private set; }

    private bool _inDelaySlot;
    private ulong _delaySlotTarget;
    /// <summary>LIFO of $ra values clobbered by TryDispatchRegisteredIntcHandler's
    /// return-through-eret trick (it points $ra at the exception vector so the handler's own
    /// `jr ra` epilogue reaches `eret`, without an explicit dispatcher). That overwrite used to
    /// discard whatever $ra held for the code that got interrupted — since interrupts can land
    /// at any instruction boundary, including mid-call-chain with a live, not-yet-saved $ra,
    /// this silently corrupted arbitrary in-flight return addresses (confirmed: MK Shaolin
    /// Monks' CRT0 static-constructor walker returned into garbage because of exactly this).
    /// Push the real value before clobbering; ExecuteEret pops and restores it, so this is
    /// invisible to any code that wasn't relying on the synthesized dispatch's own return path.
    ///
    /// Extended (2026-07-27, MK Shaolin Monks): saving only $ra wasn't enough. Real hardware
    /// interrupts are transparent to every register — the interrupted code never "called" the
    /// ISR, so it had no chance to save its own caller-saved registers ($v0/$v1/$a0-$a3/$t0-$t9)
    /// the way a normal function call's caller would. On real hardware this is safe because the
    /// BIOS-level exception dispatcher (a hand-written asm trampoline) saves all 32 GPRs to the
    /// kernel exception frame before ever calling into the registered C-level handler, and
    /// restores them all before `eret`. TryDispatchRegisteredIntcHandler jumps straight into that
    /// C-level handler with no such save/restore, so any register it uses as scratch — which is
    /// nearly all of them, since ordinary C calling convention lets a callee clobber caller-saved
    /// regs freely — permanently corrupts the interrupted code's in-flight state. Confirmed
    /// concretely: a `sceSifSetDma` syscall's return value (in $v0) was overwritten to 0 by a
    /// registered SIF interrupt handler firing in the few cycles between the syscall completing
    /// and its caller reading $v0, making a successful DMA submission look like a failure and
    /// permanently halting the game's own real fatal-error path. Now saves/restores the full
    /// GPR file (same approach as KernelHle's SaveFullContext/RestoreFullContext for thread
    /// preemption, which already gets this right) instead of just $ra.</summary>
    private readonly Stack<ulong[]> _savedGprAcrossIntcDispatch = new();
    private bool _branchWasLikely;

    // COP1 FPU (Phase 25) — 32 single regs, Det policy
    private readonly float[] _fpr = new float[32];
    private uint _fcr31;

    public EmotionEngine(SystemMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public void SetVu0(Vu0 vu0)
    {
        _vu0 = vu0 ?? throw new ArgumentNullException(nameof(vu0));
    }

    public void SetIntc(Intc intc)
    {
        _intc = intc ?? throw new ArgumentNullException(nameof(intc));
        _intc.SetNotify(SyncInterruptsFromIntc);
    }

    public void SetHle(BiosHle hle) => _hle = hle;
    public void SetDebugger(Debugger dbg) => _debugger = dbg;
    public void SetTracer(Tracer tracer) => _tracer = tracer;
    public void SetTelemetry(Telemetry? telemetry) => _telemetry = telemetry;
    public void SetCycleSource(Func<ulong> source) => _cycleSource = source;

    /// <summary>See _pendingSemaStall's doc comment. Called by SonyKernelHle's WaitSema handler
    /// instead of fabricating an instant signal, when a real SIF RPC is already queued to resolve
    /// this wait for real.</summary>
    public void RequestSemaStall() => _pendingSemaStall = true;

    /// <summary>When true, pending IRQs vector through EnterException.</summary>
    public bool TakeExceptions
    {
        get => _takeExceptions;
        set => _takeExceptions = value;
    }

    /// <summary>When true (default), SYSCALL uses BiosHle; when false, vectors as ExcCode 8.</summary>
    public bool PreferHleSyscalls
    {
        get => _preferHleSyscalls;
        set => _preferHleSyscalls = value;
    }

    /// <summary>BEV bit (Status bit 22): bootstrap exception vectors in ROM window.</summary>
    public bool BootExceptionVectors => (COP0_Status & (1u << 22)) != 0;

    /// <summary>Optional crude I-cache line model (64B lines) for timing stubs.</summary>
    public bool CacheModelEnabled
    {
        get => _cacheModelEnabled;
        set => _cacheModelEnabled = value;
    }

    public ulong CacheLineHits => _cacheLineHits;
    public ulong CacheLineMisses => _cacheLineMisses;
    public int Cop2StallRemaining => _cop2StallRemaining;

    public void Reset()
    {
        PC = 0xBFC00000;
        Array.Clear(_gprs);
        LO = HI = 0;
        COP0_Status = COP0_Cause = 0;
        COP0_EPC = 0;
        _cop0Count = _cop0Compare = _cop0BadVAddr = _cop0ErrorEpc = 0;
        InterruptPending = false;
        ExceptionCount = EretCount = 0;
        _inDelaySlot = false;
        _delaySlotTarget = 0;
        _cop2StallRemaining = 0;
        _cacheLineHits = _cacheLineMisses = 0;
        _lastCacheLine = ulong.MaxValue;
        _preferHleSyscalls = true;
        _branchWasLikely = false;
        Array.Clear(_fpr);
        _fcr31 = 0;
    }

    public float GetFpr(int i) => _fpr[i & 31];
    public void SetFpr(int i, float v) => _fpr[i & 31] = DeterministicFloat.Canonicalize(v);

    /// <summary>Full EE core state for SaveState.cs. The prior version saved PC/GPRs/LO/HI/
    /// three COP0 fields directly in SaveState.cs and stopped there — missing LO1/HI1 (the
    /// MMI 128-bit-multiply extended registers), every FPU register, the rest of COP0 (Count/
    /// Compare/BadVAddr/ErrorEpc/Config), and critically the branch-delay-slot state
    /// (_inDelaySlot/_delaySlotTarget/_branchWasLikely) and the interrupt-dispatch register
    /// save stack (_savedGprAcrossIntcDispatch — this session's own interrupt-corruption fix;
    /// without it, a save taken while inside a dispatched interrupt handler would resume with
    /// the wrong "return to" register state once the handler returns). Also covers the
    /// WaitSema genuine-stall flags (_pendingThreadStall/_pendingSemaStall) this session added
    /// — a save taken mid-stall needs to resume stalled, not silently start executing.
    /// Deliberately NOT saved: cache-model hit/miss counters and _irqLoopStreak (pure
    /// diagnostics, safe to reset) and _sa (single-instruction scratch, never live across
    /// instruction boundaries).</summary>
    public void WriteState(System.IO.BinaryWriter w)
    {
        w.Write(PC);
        for (int i = 0; i < 32; i++) { w.Write(_gprs[i].Lo); w.Write(_gprs[i].Hi); }
        w.Write(LO); w.Write(HI);
        w.Write(LO1); w.Write(HI1);
        w.Write(COP0_Status); w.Write(COP0_Cause); w.Write(COP0_EPC);
        w.Write(_cop0Count); w.Write(_cop0Compare); w.Write(_cop0BadVAddr); w.Write(_cop0ErrorEpc); w.Write(_cop0Config);
        for (int i = 0; i < 32; i++) w.Write(_fpr[i]);
        w.Write(_fcr31);
        w.Write(InterruptPending);
        w.Write(ExceptionCount); w.Write(EretCount);
        w.Write(_inDelaySlot);
        w.Write(_delaySlotTarget);
        w.Write(_branchWasLikely);
        w.Write(_takeExceptions);
        w.Write(_pendingThreadStall);
        w.Write(_pendingSemaStall);
        w.Write(_preferHleSyscalls);
        w.Write(_cop2StallRemaining);
        w.Write(HleRedirectPc.HasValue);
        if (HleRedirectPc.HasValue) w.Write(HleRedirectPc.Value);

        w.Write(_savedGprAcrossIntcDispatch.Count);
        // Stack<T> enumerates top-to-bottom; reverse so ReadState can Push in the same order
        // and end up with an identical top-of-stack.
        var frames = _savedGprAcrossIntcDispatch.ToArray();
        for (int i = frames.Length - 1; i >= 0; i--)
            foreach (var v in frames[i]) w.Write(v);
    }

    public void ReadState(System.IO.BinaryReader r)
    {
        PC = r.ReadUInt64();
        for (int i = 0; i < 32; i++) _gprs[i] = new Gpr128 { Lo = r.ReadUInt64(), Hi = r.ReadUInt64() };
        LO = r.ReadUInt64(); HI = r.ReadUInt64();
        LO1 = r.ReadUInt64(); HI1 = r.ReadUInt64();
        COP0_Status = r.ReadUInt32(); COP0_Cause = r.ReadUInt32(); COP0_EPC = r.ReadUInt64();
        _cop0Count = r.ReadUInt32(); _cop0Compare = r.ReadUInt32(); _cop0BadVAddr = r.ReadUInt32();
        _cop0ErrorEpc = r.ReadUInt32(); _cop0Config = r.ReadUInt32();
        for (int i = 0; i < 32; i++) _fpr[i] = r.ReadSingle();
        _fcr31 = r.ReadUInt32();
        InterruptPending = r.ReadBoolean();
        ExceptionCount = r.ReadUInt64(); EretCount = r.ReadUInt64();
        _inDelaySlot = r.ReadBoolean();
        _delaySlotTarget = r.ReadUInt64();
        _branchWasLikely = r.ReadBoolean();
        _takeExceptions = r.ReadBoolean();
        _pendingThreadStall = r.ReadBoolean();
        _pendingSemaStall = r.ReadBoolean();
        _preferHleSyscalls = r.ReadBoolean();
        _cop2StallRemaining = r.ReadInt32();
        bool hasRedirect = r.ReadBoolean();
        HleRedirectPc = hasRedirect ? r.ReadUInt64() : (ulong?)null;

        _savedGprAcrossIntcDispatch.Clear();
        int frameCount = r.ReadInt32();
        for (int i = 0; i < frameCount; i++)
        {
            var frame = new ulong[32];
            for (int j = 0; j < 32; j++) frame[j] = r.ReadUInt64();
            _savedGprAcrossIntcDispatch.Push(frame);
        }
    }

    /// <summary>
    /// Mirror INTC pending into COP0 Cause IP bits.
    /// Status bit0 = IE, bit16 = EIE (simplified: either enables recognition).
    /// EXL (bit 1) and ERL (bit 2) block delivery.
    /// </summary>
    public void SyncInterruptsFromIntc()
    {
        if (_intc == null)
        {
            InterruptPending = false;
            return;
        }

        uint pending = _intc.GetPendingInterrupts();
        if (pending != 0)
            COP0_Cause |= 1u << 10; // IP2 summary
        else
            COP0_Cause &= ~(1u << 10);

        // Compare interrupt (software timer on COP0)
        if (_cop0Compare != 0 && _cop0Count >= _cop0Compare)
            COP0_Cause |= 1u << 15;
        else if (_cop0Compare == 0 || _cop0Count < _cop0Compare)
            COP0_Cause &= ~(1u << 15);

        bool ie = (COP0_Status & 1) != 0 || (COP0_Status & (1u << 16)) != 0;
        // EXL | ERL block delivery on real R5900. Our HLE also refuses nested
        // TryDispatchRegisteredIntcHandler while an outer frame is still live
        // (_savedGprAcrossIntcDispatch non-empty), even if software cleared EXL mid-handler
        // via an ERL critical section (God of War 0x00299820). Real BIOS dispatchers save
        // EPC on a private stack and can re-enter; we only have one COP0_EPC + a LIFO GPR
        // frame stack that must fully unwind before another handler is entered. Without
        // this, post-ERL EXL=0 windows nested SIF/VBlank dispatches unbounded (eretStack
        // 3000+ observed @ cyc≈34M) until SP/ra are garbage and PC lands in BSS.
        bool blocked = (COP0_Status & 0x6) != 0
            || _savedGprAcrossIntcDispatch.Count > 0;
        // Real MIPS gates each Cause.IPx bit (8-15) by the matching Status.IMx bit at the
        // same position — software (e.g. a busy-poll on INTC_STAT with IM left at 0 while it
        // hasn't set up a dispatcher yet) can leave IE=1 with all IM bits clear specifically
        // to keep interrupts from being taken while it observes STAT by hand. Without this,
        // we'd take a phantom exception (and ack the STAT bit as a side effect) out from
        // under exactly that kind of legitimate polling loop.
        bool causeIp = (COP0_Cause & COP0_Status & 0xFF00u) != 0;
        InterruptPending = causeIp && ie && !blocked;
    }

    public bool HasCop0Interrupt => InterruptPending;

    /// <summary>Public COP0 read for kernel HLE (GetCop0 syscall).</summary>
    public uint ReadCop0Public(int reg) => ReadCop0(reg);

    public uint ReadCop0(int reg) => reg switch
    {
        Cop0BadVAddr => _cop0BadVAddr,
        Cop0Count => _cop0Count,
        Cop0Compare => _cop0Compare,
        Cop0Status => COP0_Status,
        Cop0Cause => COP0_Cause,
        Cop0Epc => (uint)COP0_EPC,
        Cop0PrId => Cop0PrIdValue,
        Cop0Config => _cop0Config,
        Cop0ErrorEpc => _cop0ErrorEpc,
        _ => 0
    };

    public void WriteCop0(int reg, uint value)
    {
        switch (reg)
        {
            case Cop0BadVAddr: _cop0BadVAddr = value; break;
            case Cop0Count: _cop0Count = value; break;
            case Cop0Compare:
                _cop0Compare = value;
                COP0_Cause &= ~(1u << 15); // clear timer IP on compare write
                break;
            case Cop0Status:
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_COP0STATUS") == "1" && value != COP0_Status)
                    Console.Error.WriteLine($"[COP0STATUS] pc=0x{PC:X8} old=0x{COP0_Status:X8} new=0x{value:X8} cyc={CurrentCycle()}");
                COP0_Status = value;
                break;
            case Cop0Cause: COP0_Cause = (COP0_Cause & 0xB000FF00) | (value & ~0xB000FF00u); break; // keep RO-ish bits simple
            case Cop0Epc: COP0_EPC = value; break;
            case Cop0Config: _cop0Config = value; break;
            case Cop0ErrorEpc: _cop0ErrorEpc = value; break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Gpr128 GetGpr(int index) => _gprs[index & 0x1F];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Gpr128 GetGpr(uint index) => _gprs[(int)index & 0x1F];

    /// <summary>Diagnostic-only (DETPS2_TRACE_REGWRITE=1, DETPS2_TRACE_REGWRITE_IDX=N to pick the
    /// register, default 4=a0): logs every write to one specific GPR anywhere in the program,
    /// with the writing PC. General-purpose — built to trace a specific corrupted register back
    /// to its source instruction when a manual --trace-window binary search becomes impractical
    /// (the register hadn't been touched for over 13,000 cycles before the point it was found
    /// corrupted, i.e. its true origin is far outside any reasonably-sized trace window).</summary>
    public static readonly bool TraceRegWrite = Environment.GetEnvironmentVariable("DETPS2_TRACE_REGWRITE") == "1";
    public static readonly int TraceRegWriteIdx =
        int.TryParse(Environment.GetEnvironmentVariable("DETPS2_TRACE_REGWRITE_IDX"), out var _tprIdx) ? _tprIdx : 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetGpr(int index, Gpr128 value)
    {
        int reg = index & 0x1F;
        if (reg != 0)
        {
            if (TraceRegWrite && reg == TraceRegWriteIdx && value.Lo != _gprs[reg].Lo)
                Console.Error.WriteLine($"[REGWRITE] pc=0x{PC:X8} reg={reg} old=0x{_gprs[reg].Lo:X16} new=0x{value.Lo:X16} cyc={CurrentCycle()}");
            _gprs[reg] = value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetGpr(uint index, Gpr128 value) => SetGpr((int)index, value);

    /// <summary>Phase 51: JIT-friendly Lo access (matches ADDIU/ADDU SetGpr Lo-only writes).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong JitGetLo(int index) => _gprs[index & 0x1F].Lo;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void JitSetLo(int index, ulong value)
    {
        int reg = index & 0x1F;
        if (reg != 0)
        {
            _gprs[reg].Lo = value;
            _gprs[reg].Hi = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void JitAddCount(int n)
    {
        if (n > 0) _cop0Count += (uint)n;
    }

    /// <summary>
    /// Executes up to maxCycles instructions.
    /// Properly handles branch delay slots.
    /// Returns the number of cycles actually consumed.
    /// </summary>
    public int Step(ulong maxCycles)
    {
        if (maxCycles == 0) return 0;

        SyncInterruptsFromIntc();

        int executed = 0;

        while ((ulong)executed < maxCycles)
        {
            // COP0 Count advances with executed EE cycles (deterministic)
            _cop0Count++;

            // Diagnostic-cycle stamp, computed and applied unconditionally on every loop
            // iteration, so --trace-threads and other cycle-keyed diagnostics stay accurate
            // across stretches dominated by early-continue branches below (interrupt dispatch,
            // VBlank wait, COP2 stall) that never reach the "normal" instruction-fetch path
            // further down. Previously this was only stamped there, so a run cycling through
            // (say) repeated interrupt dispatch showed the SAME stale cycle number in
            // --trace-threads output for thousands of real iterations — looking exactly like a
            // frozen livelock (ForcePreempt ping-ponging at one "frozen" cyc value) when real
            // time was actually advancing normally underneath it. Confirmed as a real diagnostic
            // accuracy bug, not a genuine emulation issue, while investigating Shaolin Monks
            // (2026-07-27 — see DEVELOPER_GUIDE.md's IRQLOOP/interrupt-storm entries, where this
            // exact artifact briefly looked like a second livelock before DETPS2_TRACE_IRQLOOP
            // showed cyc genuinely advancing underneath the stale --trace-threads timestamps).
            ulong cyc = _cycleSource?.Invoke() ?? 0;
            if (KernelState.TraceThreads) KernelState.CurrentCycle = cyc;
            if (TransferLog.Enabled) TransferLog.CurrentCycle = cyc;
            Intc.CurrentCycleForTrace = cyc; // always — STAT hold windows depend on it

            if ((executed & 0x3F) == 0)
                SyncInterruptsFromIntc();

            // CRI HLE etc.: poll every instruction only while PC is in known hot ranges
            // (cvFs / ADXF open / synthetic stubs). Full every-instr dispatch was too slow;
            // open windows are short so we still need finer than 64-cycle sampling.
            if (MidInstructionHook != null)
            {
                uint ppc = (uint)(PC & 0x1FFFFFFF);
                if ((ppc >= 0x0041D000 && ppc <= 0x0041D800)
                    || (ppc >= 0x00417B00 && ppc <= 0x00418200)
                    || (ppc >= 0x01FD4000 && ppc < 0x01FD4100))
                    MidInstructionHook.Invoke(this);
            }

            // VBlank HLE wait (Phase 14): stall EE while kernel waits for VBlank
            if (_hle != null && _hle.Kernel.WaitingVblank)
            {
                executed++;
                continue;
            }

            // See _pendingThreadStall's doc comment: a thread implicitly exited via jr ra (ra==0)
            // with nothing else runnable, so there is genuinely nothing correct left to execute.
            // Keep retrying SwitchToNext every cycle (cheap — O(thread count)) until another
            // thread becomes runnable, instead of falling through into raw memory.
            if (_pendingThreadStall)
            {
                if (_hle != null && _hle.Kernel.SwitchToNext(this))
                {
                    _pendingThreadStall = false;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_STALLCLEAR") == "1")
                        Console.Error.WriteLine($"[STALLCLEAR] cyc={CurrentCycle()} newPc=0x{PC:X8} tid={_hle.Kernel.CurrentThreadId}");
                }
                executed++;
                continue;
            }

            // See _pendingSemaStall's doc comment: genuinely wait for a real, already-queued SIF
            // RPC to resolve this semaphore via SignalSema, instead of calling SwitchToNext (whose
            // own "wake ourselves" fallback would undo the Sleeping state before the real response
            // ever arrives) or fabricating a fake signal.
            // Also: if drain woke a *different* waiter (or another thread was always runnable),
            // clear the stall and SwitchToNext — otherwise one wrong-sema WaitSema freezes the EE
            // forever after the RPC that was never going to signal it completes.
            if (_pendingSemaStall)
            {
                var stalledThread = _hle?.Kernel.GetThread(_hle.Kernel.CurrentThreadId);
                if (stalledThread == null || !stalledThread.Sleeping)
                {
                    _pendingSemaStall = false;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_STALLCLEAR") == "1")
                        Console.Error.WriteLine($"[STALLCLEAR-SEMA] cyc={CurrentCycle()} pc=0x{PC:X8} tid={_hle?.Kernel.CurrentThreadId}");
                }
                else if (Environment.GetEnvironmentVariable("DETPS2_SEMA_STALL_YIELD") == "1"
                         && _hle != null && _hle.Kernel.TryYieldToOtherRunnable(this))
                {
                    // Opt-in only. Yielding out of a WaitSema stall when another thread is
                    // runnable kills MK Shaolin Monks WAD (cdvdSectors collapses 198k→1).
                    // GoW DualInfo works with pure stall + PollSema-id; leave yield off by default.
                    _pendingSemaStall = false;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_STALLCLEAR") == "1")
                        Console.Error.WriteLine($"[STALLCLEAR-SEMA-YIELD] cyc={CurrentCycle()} pc=0x{PC:X8} tid={_hle.Kernel.CurrentThreadId}");
                }
                else
                {
                    executed++;
                    continue;
                }
            }

            // Kernel thread preemption (see KernelState.MaybePreempt): real hardware
            // timeslices threads via a periodic timer tick even if they never yield
            // voluntarily (e.g. a bind-retry loop with a local software delay and no
            // syscalls at all) — without this, such a thread starves every other thread
            // forever under our otherwise purely-cooperative scheduler. No-ops (cheap) in
            // the overwhelmingly common single-thread-of-interest case.
            _hle?.Kernel.MaybePreempt(this);

            // COP2 interlock stall (Phase 10)
            if (_cop2StallRemaining > 0 || (_vu0 != null && _vu0.IsCop2Interlocked))
            {
                if (_vu0 != null && _vu0.IsCop2Interlocked)
                    _vu0.Step(1);
                if (_cop2StallRemaining > 0)
                    _cop2StallRemaining--;
                executed++;
                continue;
            }

            // Exception vectoring for external / compare IRQs
            if (_takeExceptions && InterruptPending)
            {
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_IRQLOOP") == "1")
                {
                    _irqLoopStreak++;
                    if (_irqLoopStreak % 1000 == 1)
                        Console.Error.WriteLine($"[IRQLOOP] streak={_irqLoopStreak} pc=0x{PC:X8} pending=0x{_intc?.GetPendingInterrupts():X4} status=0x{COP0_Status:X8} cause=0x{COP0_Cause:X8} cyc={CurrentCycle()}");
                }
                if (!TryDispatchRegisteredIntcHandler())
                    EnterException(GetExceptionVector(general: true), causeExcCode: 0); // Int
                executed++;
                continue;
            }
            _irqLoopStreak = 0;

            if (_hle != null && _hle.ExitRequested)
                break;

            if (_debugger != null && _debugger.ShouldHaltBefore(PC, cyc))
                break;

            // Real MIPS traps a misaligned instruction fetch (AdEL) rather than reading garbage.
            // Without this, a wild jump (corrupted register/stack) silently free-runs through
            // whatever bytes happen to sit at an unaligned PC — often unmapped MMIO returning 0
            // (NOP), which just carries the runaway further instead of failing fast where the
            // actual bug is visible.
            if ((PC & 0x3) != 0)
            {
                _cop0BadVAddr = (uint)PC;
                EnterException(GetExceptionVector(general: true), causeExcCode: 4); // AdEL
                executed++;
                continue;
            }

            if (_cacheModelEnabled)
                NoteICache(PC);

            // Diagnostic-only (DETPS2_TRACE_MSGBUF, temporary): read the formatted error-message
            // string just before it's NUL-terminated at Shaolin Monks' fatal-exit call site
            // (0x004767F0: sb zero,0(v1)), to identify what assertion is actually failing there.
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_MSGBUF") == "1" && (PC & 0x1FFFFFFF) == 0x004767F0)
            {
                ulong bufAddr = GetGpr(3).Lo; // v1
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 512; i++)
                {
                    byte b = _memory.Read8((uint)(bufAddr + (ulong)i));
                    if (b == 0) break;
                    sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
                }
                Console.Error.WriteLine($"[MSGBUF] v1=0x{bufAddr:X8} cyc={CurrentCycle()} msg=\"{sb}\"");
            }
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_MSGBUF") == "1" && (PC & 0x1FFFFFFF) == 0x004767B8)
            {
                ulong a1v = GetGpr(5).Lo;
                var fsb = new System.Text.StringBuilder();
                for (int i = 0; i < 300 && a1v + (ulong)i < 0x2000000; i++)
                {
                    byte b = _memory.Read8((uint)(a1v + (ulong)i));
                    if (b == 0) break;
                    fsb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
                }
                int tid = _hle?.Kernel.CurrentThreadId ?? -1;
                uint tidEntry = tid >= 0 ? (_hle?.Kernel.GetThread(tid)?.Entry ?? 0) : 0;
                Console.Error.WriteLine($"[MSGBUF-A0] a0=0x{GetGpr(4).Lo:X16} a1=0x{a1v:X8} a2=0x{GetGpr(6).Lo:X8} cyc={CurrentCycle()} ra={GetGpr(31).Lo:X8} tid={tid} tidEntry=0x{tidEntry:X8} a1text=\"{fsb}\"");
            }
            // Diagnostic-only (DETPS2_TRACE_MSGBUF, temporary): log every entry into the
            // vsnprintf-style formatter (0x00475BA8) with its buffer (a0) and format-string (a1)
            // arguments, reading a1 as text where it looks plausible — the format string itself
            // should reveal what assertion/message the game is actually building, without needing
            // to fully resolve the formatter's own internal NULL-deref mechanics.
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_MSGBUF") == "1" && (PC & 0x1FFFFFFF) == 0x00475BA8)
            {
                ulong a0 = GetGpr(4).Lo, a1 = GetGpr(5).Lo, a2 = GetGpr(6).Lo, ra = GetGpr(31).Lo;
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 200 && a1 + (ulong)i < 0x2000000; i++)
                {
                    byte b = _memory.Read8((uint)(a1 + (ulong)i));
                    if (b == 0) break;
                    sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
                }
                Console.Error.WriteLine($"[FMTENTRY] a0(buf)=0x{a0:X16} a1(fmt)=0x{a1:X8} a2=0x{a2:X8} ra=0x{ra:X8} cyc={CurrentCycle()} fmt=\"{sb}\"");
            }

            // Diagnostic-only (DETPS2_TRACE_EXIT, temporary): log $ra at entry to the hardcoded
            // abort()-style wrapper (0x00476808: unconditionally builds a0=1 and calls into
            // 0x0011C2B0, which issues the Exit syscall without returning). The [EXIT-SYSCALL]
            // trace in SonyKernelHle.cs always reports ra=0x00476818 regardless of caller, since
            // that's the return address the wrapper's own internal jal sets right before the
            // syscall fires — it can never identify who called the wrapper itself. Capturing ra
            // here, before it's overwritten, is the only way to find the real caller.
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_EXIT") == "1" && (PC & 0x1FFFFFFF) == 0x00476808)
                Console.Error.WriteLine($"[ABORT-CALLER] ra=0x{GetGpr(31).Lo:X8} cyc={CurrentCycle()}");

            // Diagnostic-only (DETPS2_TRACE_EXIT, temporary): found via Ghidra's own reference
            // analysis (2026-07-27, see docs/DEVELOPER_GUIDE.md) -- the real mechanism behind the
            // ra=0 signature at [ABORT-CALLER]. 0x00476808 is reached from a mutable global
            // function-pointer slot (0x004EC8F8, PTR_FUN_004ec8f8) whose default/initial value IS
            // the abort path (0x002010F8, itself a plain `FUN_00476808(); return;` stub) -- called
            // through via two small dispatch functions, 0x00201108 and 0x00202E40, whose own
            // bodies are pure tail calls (the pointer's target, then return) with nothing else in
            // between. A pure tail call is a plain `j`/`jr`, not `jal` -- it never touches $ra --
            // so $ra survives unchanged all the way from FUN_00201108/FUN_00202E40's own entry
            // down through the pointer call and into 0x00476808, explaining why ra=0 shows up
            // there even though every step in between is a real, findable function. Unlike
            // 0x00476808 itself, THESE two functions are reached via genuine `jal`s (confirmed via
            // Ghidra's reference manager, not just byte-pattern scanword matching) from 14-22
            // distinct call sites each -- logging $ra here is the only way to find out which one.
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_EXIT") == "1" && (PC & 0x1FFFFFFF) == 0x00201108)
                Console.Error.WriteLine($"[PANIC-DISPATCH-A] ra=0x{GetGpr(31).Lo:X8} sp=0x{GetGpr(29).Lo:X8} cyc={CurrentCycle()}");
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_EXIT") == "1" && (PC & 0x1FFFFFFF) == 0x00202E40)
                Console.Error.WriteLine($"[PANIC-DISPATCH-B] ra=0x{GetGpr(31).Lo:X8} sp=0x{GetGpr(29).Lo:X8} cyc={CurrentCycle()}");
            // Re-check (2026-07-27, after this session's other EE fixes changed the execution
            // path leading up to the same crash cycle): does the one confirmed tail-jump (j, not
            // jal -- preserves whatever $ra already held) call site to 0x00476808 found much
            // earlier this investigation, inside the lookup-or-die function at 0x00204430, fire
            // now? Log entry to the function itself (0x00204430) and the exact tail-jump
            // instruction (0x0020448C) separately.
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_EXIT") == "1" && (PC & 0x1FFFFFFF) == 0x00204430)
                Console.Error.WriteLine($"[LOOKUP-ENTRY] a0=0x{GetGpr(4).Lo:X8} ra=0x{GetGpr(31).Lo:X8} cyc={CurrentCycle()}");
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_EXIT") == "1" && (PC & 0x1FFFFFFF) == 0x0020448C)
                Console.Error.WriteLine($"[LOOKUP-TAILJUMP] ra=0x{GetGpr(31).Lo:X8} cyc={CurrentCycle()}");

            // Shaolin Monks: guard a NULL-buffer dereference inside the vsnprintf-style
            // formatter's own count-check (0x00475D24: bgtzl v0,+6, in the function entered at
            // 0x00475BA8). Traced precisely (2026-07-27, see DEVELOPER_GUIDE.md): the formatter
            // is legitimately called with a0=0 (buf=NULL) as part of normal nested format-
            // specifier processing (confirmed via a clean --trace-window capture — no wild jump,
            // no register corruption, ra/a0/a1 all genuinely 0 from a real, deterministic call
            // chain). Its own s2 (=a0, the buffer) is then dereferenced via MEM[s2+4] without a
            // NULL guard; on a system where address 0-8 is genuinely blank, that read is 0 and
            // this branch-likely correctly doesn't fire, safely skipping the buggy delay slot
            // (MEM[s2+0], which corrupts a0 for a much later, unrelated formatter call, cascading
            // into a real Exit(1) about 695,000 cycles afterward). Real R5900 hardware has actual
            // TLB-Refill vector code at physical address 0 too (KernelBootstrap.cs installs it
            // there for the same architectural reason), so this specific NULL-buffer code path
            // may simply never be reached on a real console for reasons not yet understood upstream
            // of this point — matched here at the exact crash site instead, gated on both the PC
            // and the literal opcode bytes (bgtzl v0,+6) to keep the false-positive risk for any
            // other title landing on this same physical address at essentially zero.
            if ((PC & 0x1FFFFFFF) == 0x00475D24 && _memory.Read32(PC) == 0x5C400006 && GetGpr(18).Lo == 0)
                SetGpr(2, new Gpr128 { Lo = 0 }); // v0 = 0, so bgtzl correctly doesn't take the branch

            // Past-RDRAM open-bus fetch (e.g. MK post-WAD 0x024F0C64 — 32MiB RDRAM ends at
            // 0x02000000; Read32 returns 0 → perpetual nop). Recover immediately. Generic:
            // never hardcode title addresses (MK 0x4145A8 yanked BO2 off its own boot path).
            // Resume targets MUST pass IsLikelyEeCode — plain non-zero rejects fail on string
            // data (0x00520040 "port"/path) and thrash open-bus forever (2026-07-30).
            {
                uint pcPhysFetch = (uint)(PC & 0x1FFFFFFFUL);
                if (pcPhysFetch >= (uint)SystemMemory.RDRAM_SIZE)
                {
                    ulong resumePc = 0;
                    // Prefer another runnable thread only if its saved PC is real code.
                    if (_hle != null)
                    {
                        int cur = _hle.Kernel.CurrentThreadId;
                        int next = _hle.Kernel.FindNextRunnable(cur);
                        if (next != cur)
                        {
                            var nt = _hle.Kernel.GetThread(next);
                            uint nPhys = nt != null
                                ? (uint)((nt.SavedPc != 0 ? nt.SavedPc : nt.Entry) & 0x1FFFFFFFUL)
                                : 0;
                            if (_memory.IsLikelyEeCode(nPhys)
                                && _hle.Kernel.TryYieldToOtherRunnable(this))
                            {
                                executed++;
                                continue;
                            }
                        }
                    }
                    // Stack return candidates (prefer over raw $ra — $ra often holds data ptrs).
                    uint spPhys = (uint)(GetGpr(29).Lo & 0x1FFFFFFFUL);
                    if (spPhys is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE)
                    {
                        for (uint off = 0; off <= 0x80; off += 4)
                        {
                            uint cand = _memory.Read32(spPhys + off);
                            if (_memory.IsLikelyEeCode(cand))
                            {
                                resumePc = cand;
                                break;
                            }
                        }
                    }
                    if (resumePc == 0 && _memory.IsLikelyEeCode(GetGpr(31).Lo))
                        resumePc = GetGpr(31).Lo & 0x1FFFFFFFUL;
                    if (resumePc == 0 && _memory.IsLikelyEeCode(COP0_EPC))
                        resumePc = COP0_EPC & 0x1FFFFFFFUL;
                    // Prefer Midway main / CRT0 over ADX pump — forcing pump PC from open-bus
                    // mid-boot (pre-WAD) corrupts streaming state. Pump is resumed by its own
                    // thread scheduler, not by open-bus re-home.
                    if (resumePc == 0 && _memory.Read32(0x00212F70) == 0x27BDFEE0)
                        resumePc = 0x00212F70UL;
                    if (resumePc == 0 && _memory.IsLikelyEeCode(0x0011C200UL))
                        resumePc = 0x0011C200UL;
                    if (resumePc == 0)
                        resumePc = 0x00100008UL; // ELF entry fallback

                    // Same bad resume re-chosen every time (target immediately jumps back past
                    // RDRAM): escalate to main/CRT0 after a few hits so we stop thrashing
                    // millions of open-bus cycles (was 0x520040 ↔ 0x6403800 for entire post-WAD).
                    uint resumeU = (uint)(resumePc & 0x1FFFFFFFUL);
                    if (resumeU == _openBusLastResume)
                    {
                        _openBusSameTargetHits++;
                        if (_openBusSameTargetHits >= 4)
                        {
                            if (_memory.Read32(0x00212F70) == 0x27BDFEE0)
                                resumePc = 0x00212F70UL;
                            else if (_memory.IsLikelyEeCode(0x0011C200UL))
                                resumePc = 0x0011C200UL;
                            else
                                resumePc = 0x00100008UL;
                            resumeU = (uint)(resumePc & 0x1FFFFFFFUL);
                            _openBusSameTargetHits = 0;
                        }
                    }
                    else
                    {
                        _openBusLastResume = resumeU;
                        _openBusSameTargetHits = 0;
                    }

                    COP0_Status &= ~0x6u;
                    PC = resumePc;
                    // Rate-limit: every ~1M instr, not every 64k (stderr flood killed long runs).
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                        && (executed & 0xFFFFF) == 0)
                        Console.Error.WriteLine(
                            $"[BIOS] EE open-bus rescue 0x{pcPhysFetch:X8} -> 0x{resumeU:X8} cyc={cyc}");
                    executed++;
                    continue;
                }
            }

            uint opcode = _memory.Read32(PC);
            _tracer?.LogInstruction(cyc, PC, opcode);
            if (SystemMemory.WatchAddr.HasValue || SystemMemory.TrackLastWriter)
            {
                SystemMemory.CurrentPcForWatch = (ulong)PC;
                SystemMemory.CurrentCycleForWriterLog = cyc;
            }
            // KernelState.CurrentCycle/TransferLog.CurrentCycle/Intc.CurrentCycleForTrace are now
            // stamped unconditionally at the top of the loop (see comment there) — only the
            // PC pairing for TransferLog is real-instruction-specific and belongs here.
            if (TransferLog.Enabled) TransferLog.CurrentPc = (ulong)PC;
            if (PcProfiler.Enabled) PcProfiler.Sample((ulong)PC);
            if (PcBreakGpr.HasValue && PC >= PcBreakGpr.Value && PC <= (PcBreakEnd ?? PcBreakGpr.Value))
                Console.Error.WriteLine($"[PCBREAK] pc=0x{PC:X8} op=0x{opcode:X8} v0=0x{GetGpr(2).Lo:X} v1=0x{GetGpr(3).Lo:X} a0=0x{GetGpr(4).Lo:X} a1=0x{GetGpr(5).Lo:X} a2=0x{GetGpr(6).Lo:X} a3=0x{GetGpr(7).Lo:X} " +
                    $"t0=0x{GetGpr(8).Lo:X} t1=0x{GetGpr(9).Lo:X} t2=0x{GetGpr(10).Lo:X} " +
                    $"s0=0x{GetGpr(16).Lo:X} s1=0x{GetGpr(17).Lo:X} s2=0x{GetGpr(18).Lo:X} s3=0x{GetGpr(19).Lo:X} s4=0x{GetGpr(20).Lo:X} s5=0x{GetGpr(21).Lo:X} s6=0x{GetGpr(22).Lo:X} s7=0x{GetGpr(23).Lo:X} sp=0x{GetGpr(29).Lo:X} ra=0x{GetGpr(31).Lo:X} " +
                    $"COP0_Status=0x{COP0_Status:X8} COP0_Cause=0x{COP0_Cause:X8} EPC=0x{COP0_EPC:X8} ErrorEPC=0x{_cop0ErrorEpc:X8} eretStack={_savedGprAcrossIntcDispatch.Count} InterruptPending={InterruptPending} takeExceptions={_takeExceptions} cyc={cyc}");
            _branchWasLikely = false;
            HleRedirectPc = null;
            bool tookBranch = ExecuteInstruction(opcode);
            executed++;

            if (HleRedirectPc.HasValue)
            {
                // Syscall SetSyscall hook: jump to handler without delay-slot semantics
                PC = HleRedirectPc.Value;
                HleRedirectPc = null;
            }
            else if (tookBranch)
            {
                if (_cacheModelEnabled)
                    NoteICache(PC + 4);
                // CurrentPcForWatch/WatchAddr still hold the branch's own PC from above (set
                // before ExecuteInstruction(opcode) ran) — without refreshing it here, any store
                // performed by the delay-slot instruction itself gets mis-attributed in
                // SystemMemory.LastWriterLog/WatchHits to the branch instruction's address
                // instead of the delay slot's, since a branch/jump can never itself write memory.
                if (SystemMemory.WatchAddr.HasValue || SystemMemory.TrackLastWriter)
                    SystemMemory.CurrentPcForWatch = (ulong)(PC + 4);
                uint delayOpcode = _memory.Read32(PC + 4);
                ExecuteInstruction(delayOpcode);
                PC = _delaySlotTarget;
                _inDelaySlot = false;
                executed++;
            }
            else if (_branchWasLikely)
            {
                // Likely branch not taken: nullify delay slot (PC += 8)
                PC += 8;
            }
            else
            {
                PC += 4;
            }
            _branchWasLikely = false;

            _debugger?.AfterInstruction(PC, cyc);
            if (_debugger != null && _debugger.Halted)
                break;

            if ((ulong)executed >= maxCycles)
                break;
        }

        return executed;
    }

    private void NoteICache(ulong pc)
    {
        ulong line = pc >> 6; // 64-byte lines
        if (line == _lastCacheLine)
            _cacheLineHits++;
        else
        {
            _cacheLineMisses++;
            _lastCacheLine = line;
            // miss penalty absorbed as extra cycle accounting opportunity (no wall clock)
        }
    }

    public ulong GetExceptionVector(bool general) =>
        BootExceptionVectors
            ? (general ? 0xBFC00380UL : 0xBFC00200UL)
            : (general ? 0x80000180UL : 0x80000000UL);

    private void EnterException(ulong vector, uint causeExcCode)
    {
        // Real MIPS: EPC (and Cause.BD, which only describes what EPC points at) are captured
        // once per exception "episode" — if Status.EXL is already set, we're taking a *nested*
        // exception while still inside an earlier, not-yet-eret'd handler, and the hardware does
        // NOT touch EPC again, since doing so would overwrite the outer exception's real return
        // address with nothing left to restore it. Software is expected to detect EXL-already-set
        // in its own handler and cope (real BIOS/kernel code does this; we don't emulate a BIOS
        // exception dispatcher, so this only matters for our synthesized/direct vectoring path).
        // Flagged 2026-07-27 while exhausting the Exit(1)/ra=0 investigation (DEVELOPER_GUIDE.md
        // §7.9) as a real, unconfirmed gap: an AdEL (misaligned-PC) exception below bypasses the
        // usual InterruptPending/blocked gate entirely, so a wild jump landing on a misaligned
        // address WHILE already mid-exception (EXL=1) could previously clobber the outer
        // exception's EPC here, corrupting its eventual eret target.
        // Real MIPS nests on Status.EXL. Our HLE also keeps an outstanding frame in
        // _savedGprAcrossIntcDispatch for every TryDispatchRegisteredIntcHandler episode.
        // Games (God of War SCUS_973.99 helper @ 0x00299820) deliberately clear EXL while
        // inside a registered handler via an ERL critical section (ori EXL|ERL; xori EXL;
        // mtc0; …; mtc0 ErrorEPC; eret). After that eret, COP0 shows EXL=0 but the HLE frame
        // is still live — a subsequent interrupt must NOT recapture EPC, or the outer vector
        // eret returns into the middle of the handler with restored *user* GPRs (stack
        // clobber → ra=0 → jr-ra-exit stall at 0x00290040). Treat an outstanding dispatch
        // frame as nested for EPC purposes even when software cleared EXL.
        bool nested = (COP0_Status & 0x2) != 0
            || _savedGprAcrossIntcDispatch.Count > 0;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_NESTED_EXC") == "1" && nested)
            Console.Error.WriteLine($"[NESTED-EXC] cyc={CurrentCycle()} pc=0x{PC:X8} excCode={causeExcCode} epc(unchanged)=0x{COP0_EPC:X8} eretStack={_savedGprAcrossIntcDispatch.Count}");
        if (!nested)
        {
            COP0_EPC = PC;
            if (_inDelaySlot) COP0_Cause |= 1u << 31;
            else COP0_Cause &= ~(1u << 31);
        }
        COP0_Cause = (COP0_Cause & ~0x7Cu) | ((causeExcCode & 0x1F) << 2);
        COP0_Status |= 0x2; // EXL
        InterruptPending = false;
        ExceptionCount++;
        // Int (0): use 0x80000200 common vector when not BEV (Phase 9/12 compat)
        if (causeExcCode == 0 && !BootExceptionVectors)
            PC = 0x80000200UL;
        else
            PC = vector;
    }

    public void RaiseException(uint excCode, ulong? vector = null) =>
        EnterException(vector ?? GetExceptionVector(general: true), excCode);

    /// <summary>
    /// Our synthesized interrupt vector is a bare eret stub — real hardware instead runs a
    /// BIOS dispatcher that walks the table AddIntcHandler builds and calls each registered
    /// handler for the pending INTC source(s). Rather than hand-write that dispatcher in MIPS,
    /// do the equivalent here: if the game has registered a handler for a currently pending
    /// (and unmasked) source, take the exception exactly as EnterException would (EPC/Cause/EXL),
    /// then redirect PC straight to that handler instead of the vector. a0=cause matches the
    /// real `s32 (*handler_func)(s32 cause)` signature; ra is pointed at the vector itself
    /// (already just `eret`), so the handler's own `jr ra` epilogue restores EPC and clears EXL
    /// exactly like the real ISR return path would.
    /// </summary>
    private bool TryDispatchRegisteredIntcHandler()
    {
        var sony = _hle?.Sony;
        if (sony == null || _intc == null) return false;

        uint pending = _intc.GetPendingInterrupts();
        for (int src = 0; src < 15; src++)
        {
            if ((pending & (1u << src)) == 0) continue;

            // Multi-handler chain: real BIOS walks every AddIntcHandler for this cause.
            // TryTakeNext advances the cursor; moreIntcRemain means leave the COP0 latch
            // armed so the next eret dispatches the following registration.
            bool moreIntcRemain = false;
            bool found = sony.TryTakeNextIntcHandler(src, out uint handlerAddr, out moreIntcRemain)
                         && handlerAddr != 0;
            int handlerArg = src;

            // Real hardware routes SIF0 DMA-channel completion (our INTC "Sif" summary bit)
            // to whatever the game registered via AddDmacHandler(DMA_CHANNEL_SIF0=5, ...) —
            // e.g. ps2sdk's sceSifInitCmd installs _SifCmdIntHandler this way — not through
            // AddIntcHandler. Fall back to the DMAC table for that channel when there's no
            // direct INTC handler for this source.
            // Traced (2026-07-27): dispatching here does NOT itself acknowledge the INTC "Sif"
            // bit — by design, for a real AddIntcHandler registration, since real software-owned
            // handlers ack INTC_STAT themselves as part of doing real work. But our HLE raises
            // this bit (Sif.cs/Iop.cs/SonyKernelHle.cs, several call sites) whenever SIF DMA
            // activity happens, without necessarily also populating whatever real in-memory
            // queue/flag data the game's registered handler inspects to decide there's real work
            // to do. When the handler finds nothing (a real, legitimate outcome from its own
            // perspective — e.g. ps2sdk's _SifCmdIntHandler checking an empty queue) it takes its
            // own early-exit path and never reaches the ack write buried in the "real work"
            // branch — so the bit stays pending and this dispatch fires again on the very next
            // eligible instruction, forever: a genuine interrupt storm (confirmed via
            // DETPS2_TRACE_INTC_DISPATCH re-firing every ~64 cycles with zero forward progress in
            // between) that starves everything else, not a one-off harmless spin. Real DMAC
            // channel completion is hardware-acknowledged (the DMAC's own STR/completion state
            // clears itself once the transfer is done — unlike INTC sources, which need explicit
            // software ack), so acknowledging here for the DMAC-channel-5 fallback path
            // specifically (not the direct AddIntcHandler case, which may have different, correct
            // semantics already) matches real hardware and breaks the storm.
            bool viaDmacFallback = false;
            if (!found && src == (int)Intc.InterruptSource.Sif &&
                sony.TryGetDmacHandler(5, out uint dmacHandlerAddr) && dmacHandlerAddr != 0)
            {
                handlerAddr = dmacHandlerAddr;
                handlerArg = 5; // DMA_CHANNEL_SIF0
                found = true;
                viaDmacFallback = true;
            }

            // INTC source 14 (DmaController) is the summary bit for every DMAC channel
            // completion. Real BIOS walks AddDmacHandler's per-channel table; we never
            // installed that trampoline. Without this, Burnout 3's VIF1/GIF handler at
            // 0x001F1778 (registered via AddDmacHandler ch1/ch2 + EnableDmac) never ran on
            // completion — only the software a0=-1 poll path did, which early-outs while
            // the pending-count byte is non-zero, so the GS flip-queue (gp-24120/gp-24116)
            // never drained and main spun forever at 0x001F24E0.
            if (!found && src == (int)Intc.InterruptSource.DmaController &&
                sony.TryTakePendingDmacHandler(out uint chHandler, out int chNum))
            {
                handlerAddr = chHandler;
                handlerArg = chNum;
                found = true;
                viaDmacFallback = true;
            }

            if (!found) continue;
            if (viaDmacFallback)
            {
                _intc.Acknowledge((Intc.InterruptSource)src);
                // Acknowledge is a no-op while the STAT hold window is active (busy-poller
                // assist). Without also clearing the COP0 latch, viaDmacFallback storms
                // for the entire hold (2M cycles) — denser after multi-handler eret sync.
                if (_intc.IsPending((Intc.InterruptSource)src))
                    _intc.ClearCpuLatch((Intc.InterruptSource)src);
            }

            // Traced (2026-07-27, Mortal Kombat: Shaolin Monks): the same interrupt-storm class
            // documented above for the SIF/DMAC-fallback case also hits Timer0-3, but via a
            // different mechanism. A real, directly-registered Timer0 handler (confirmed via
            // Ghidra decompilation to be short and complete normally every single time — two
            // calls, then return) still left the game trapped for 26+ million cycles: dispatched
            // every ~64 executed instructions (this method's own caller polls that often), with
            // the interrupted code's registers frozen bit-for-bit identical across dozens of
            // consecutive dispatches -- zero measurable progress the entire time. Unlike SIF's
            // "handler checks a queue, finds nothing, legitimately takes an early-exit path that
            // never reaches its own ack" scenario, a periodic timer-tick ISR has no such "nothing
            // to do" case -- it unconditionally does its short, fixed amount of work every time it
            // runs, so there's no legitimate reason for repeated dispatch to mean anything other
            // than "the real ack write this handler should have reached (or that a real BIOS-level
            // ISR wrapper we don't emulate would have done first) never happened." Acknowledging
            // here for Timer sources specifically -- not a blanket change to every "found=true"
            // dispatch, which the comment above explains would be wrong for SIF -- matches real
            // hardware's actual behavior for a periodic timer tick and breaks the storm.
            if (src is (int)Intc.InterruptSource.Timer0 or (int)Intc.InterruptSource.Timer1
                    or (int)Intc.InterruptSource.Timer2 or (int)Intc.InterruptSource.Timer3)
            {
                _intc.Acknowledge((Intc.InterruptSource)src);
                // Defense in depth (Vexx 2026-07-30): if Acknowledge is ever a no-op (legacy
                // StatHold on non-VBlank, or future hold changes), still drop the COP0 latch so
                // eret cannot re-enter the same Timer ISR every instruction. Matches the
                // viaDmacFallback ClearCpuLatch fallback above.
                if (_intc.IsPending((Intc.InterruptSource)src))
                    _intc.ClearCpuLatch((Intc.InterruptSource)src);
            }

            // Consume COP0 edge latch for every dispatched source. STAT can stay sticky (pollers
            // / software write-1-clear); without clearing the latch, eret immediately re-enters
            // the same handler forever and leaves EXL effectively stuck (Shaolin Monks DI spin
            // at 0x485FE4 with Status EXL=1 for 80M+ cycles after preemption fix, 2026-07-29).
            //
            // Consume COP0 edge latch, then re-Raise when more AddIntcHandler registrations
            // remain so the next eret dispatches the next handler (real BIOS walks the whole
            // list in one ISR; we serialize one handler per episode).
            // Burnout 3 VBlankStart: 0x2370A0 → 0x1F1CE8 → 0x22B830.
            if (!viaDmacFallback
                && src is not ((int)Intc.InterruptSource.Timer0 or (int)Intc.InterruptSource.Timer1
                    or (int)Intc.InterruptSource.Timer2 or (int)Intc.InterruptSource.Timer3))
            {
                _intc.ClearCpuLatch((Intc.InterruptSource)src);
                if (moreIntcRemain)
                    _intc.Raise((Intc.InterruptSource)src);
            }

            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_INTC_DISPATCH") == "1")
                Console.Error.WriteLine($"[INTC_DISPATCH] cyc={CurrentCycle()} src={src} handler=0x{handlerAddr:X8} fromPc=0x{PC:X8} savedRa=0x{GetGpr(31).Lo:X8} sp=0x{GetGpr(29).Lo:X8} stackDepthBeforePush={_savedGprAcrossIntcDispatch.Count} a0=0x{GetGpr(4).Lo:X8} a1=0x{GetGpr(5).Lo:X8} a2=0x{GetGpr(6).Lo:X8} t0=0x{GetGpr(8).Lo:X8} t1=0x{GetGpr(9).Lo:X8} v0=0x{GetGpr(2).Lo:X8} v1=0x{GetGpr(3).Lo:X8}");
            // Snapshot interrupted GPRs BEFORE EnterException/a0 clobber. Also publish to the
            // thread's SaveFullContext so a handler that WaitSema/SwitchToNext mid-ISR cannot
            // partial-restore this thread with ISR garbage (v1=0x44 instead of 0x1000F000 on
            // MKSM INTC poll, 2026-07-29).
            //
            // Push the frame AFTER EnterException so the nested-EPC check
            // (`_savedGprAcrossIntcDispatch.Count > 0`) only sees *prior* outstanding
            // dispatches — not the frame we are creating for this one. Pushing first made
            // every dispatch look nested and froze EPC at the first exception forever
            // (God of War VBlank wait at 0x0021FF24 never resumed to the right PC).
            var savedGpr = new ulong[32];
            for (int i = 0; i < 32; i++)
                savedGpr[i] = GetGpr(i).Lo;
            _hle?.Kernel.CaptureInterruptedContext(this, savedGpr);
            EnterException(GetExceptionVector(general: true), causeExcCode: 0);
            _savedGprAcrossIntcDispatch.Push(savedGpr);
            PC = handlerAddr;
            SetGpr(4, new Gpr128 { Lo = (ulong)(uint)handlerArg }); // a0 = cause
            SetGpr(31, new Gpr128 { Lo = KernelBootstrap.Kseg0Interrupt }); // ra = vector's eret
            return true;
        }

        // No game/kernel-registered handler owns any currently pending source.
        //
        // Real INTC_STAT is sticky until software write-1-clear — taking a COP0 exception
        // does not clear STAT. Our synthesized vector is bare eret, so if we leave the
        // COP0 latch armed, we re-enter every instruction (storm). Auto-acking STAT fixed
        // the storm but stole sticky VBlank from busy-pollers (Shaolin Monks CRT0 at
        // 0x00480330: clear bit2, EI, spin on INTC_STAT — never sees the bit again).
        //
        // Structural fix (Intc.CpuLatched): clear only the COP0 edge latch here so bare-eret
        // does not storm; leave STAT sticky for MMIO poll / real write-1-clear. Next Pcrtc
        // Raise after software clears STAT re-arms the edge. VBlankStart/End keep STAT;
        // other sources still full-Acknowledge (they are not typically busy-polled sticky
        // the same way, and leaving them sticky without a handler was also a storm source).
        if (pending != 0)
        {
            for (int src = 0; src < 15; src++)
            {
                if ((pending & (1u << src)) == 0) continue;
                var source = (Intc.InterruptSource)src;
                if (source is Intc.InterruptSource.VBlankStart or Intc.InterruptSource.VBlankEnd)
                    _intc.ClearCpuLatch(source);
                else
                    _intc.Acknowledge(source);
            }
        }
        return false;
    }

    /// <summary>
    /// The JR/JALR "ignore jumps into the low vector page" guard masks off the kernel-segment
    /// prefix before comparing against 0x10000 -- deliberately, so it catches BOTH a raw
    /// near-zero garbage pointer (0x0, 0x400, ...) and the KSEG0-mirrored equivalent (0x80000000,
    /// 0x80000400, ...) uniformly, since genuinely uninitialized memory can read as either shape.
    /// But that same masking also catches the real, intentional KSEG0 exception vectors
    /// (0x80000000/0x80000180/0x80000200 -- see KernelBootstrap's own constants) whenever code
    /// legitimately jumps there, e.g. TryDispatchRegisteredIntcHandler deliberately points a
    /// dispatched handler's `ra` at 0x80000200 so its own `jr ra` epilogue lands back on the
    /// vector's `eret`. Without this exclusion, THAT jump gets silently swallowed by the same
    /// guard it was relying on: the handler's `jr ra` becomes a no-op, `eret` never runs, EXL
    /// never clears, and `_savedGprAcrossIntcDispatch`'s pushed frame never gets popped -- COP0
    /// stays permanently "mid-exception" and every later `jr`/`jalr` elsewhere in the program
    /// keeps landing back in the vector page too (since InterruptPending is now permanently
    /// blocked by the stuck EXL, so no *new* exception ever re-establishes a fresh EPC/ra
    /// either). Confirmed via Mortal Kombat: Shaolin Monks: this exact sequence explained a
    /// stale `ra=0x80000200` observed 38M cycles after the dispatch that set it, at the real,
    /// legitimate exit(1) call this session's SifBindRpc investigation ultimately traced to (see
    /// docs/DEVELOPER_GUIDE.md's "next wall" entry). These three addresses are OUR OWN
    /// synthesized vector locations (KernelBootstrap.InstallExceptionVectors), not something a
    /// game could coincidentally produce as garbage, so excluding exactly them (rather than
    /// broadly loosening the guard) keeps its original uninitialized-pointer protection intact.
    /// </summary>
    private static bool IsLegitimateVectorTarget(ulong t) =>
        t is KernelBootstrap.Kseg0Tlb or KernelBootstrap.Kseg0Common or KernelBootstrap.Kseg0Interrupt;

    private bool ExecuteInstruction(uint opcode)
    {
        uint primary = (opcode >> 26) & 0x3F;

        switch (primary)
        {
            case 0x00: return ExecuteSpecial(opcode);
            case 0x01: return ExecuteRegimm(opcode);
            case 0x02: return ExecuteJ(opcode);
            case 0x03: return ExecuteJal(opcode);
            case 0x04: return ExecuteBeq(opcode);
            case 0x05:
                // Boot assist: giant software fill/delay loops (BNE + ADDIU -1) can be
                // 2^32+ iterations when a length has garbage high bits — snap to done.
                MaybeFastForwardCountdown(opcode);
                return ExecuteBne(opcode);
            case 0x06: return ExecuteBlez(opcode);
            case 0x07: return ExecuteBgtz(opcode);
            // Likely branches (Phase 25): nullify delay if not taken
            case 0x14: return ExecuteBeqLikely(opcode);
            case 0x15: return ExecuteBneLikely(opcode);
            case 0x16: return ExecuteBlezLikely(opcode);
            case 0x17: return ExecuteBgtzLikely(opcode);
            case 0x08: ExecuteAddi(opcode); break;
            case 0x09: ExecuteAddiu(opcode); break;
            case 0x0A: ExecuteSlti(opcode); break;
            case 0x0B: ExecuteSltiu(opcode); break;
            case 0x0C: ExecuteAndi(opcode); break;
            case 0x0D: ExecuteOri(opcode); break;
            case 0x0E: ExecuteXori(opcode); break;
            case 0x0F: ExecuteLui(opcode); break;
            // MIPS III 64-bit immediates (retail CRT0 / Midway)
            case 0x18: ExecuteDaddi(opcode); break;  // DADDI
            case 0x19: ExecuteDaddiu(opcode); break; // DADDIU

            case 0x10: return ExecuteCop0(opcode);
            case 0x11: return ExecuteCop1(opcode);
            case 0x12: return ExecuteCop2(opcode);
            case 0x1A: ExecuteLdl(opcode); break; // LDL simplified (see LWL/SDL note)
            case 0x1B: ExecuteLdr(opcode); break; // LDR simplified
            case 0x1C: ExecuteMmi(opcode); break;
            case 0x1E: ExecuteLq(opcode); break; // LQ
            case 0x1F: ExecuteSq(opcode); break; // SQ
            case 0x2F: break; // CACHE nop
            case 0x33: break; // PREF nop (Phase 41)
            case 0x36: ExecuteLqc2(opcode); break; // LQC2
            case 0x3E: ExecuteSqc2(opcode); break; // SQC2

            case 0x20: ExecuteLb(opcode); break;
            case 0x21: ExecuteLh(opcode); break;
            case 0x22: ExecuteLwl(opcode); break;
            case 0x23: ExecuteLw(opcode); break;
            case 0x24: ExecuteLbu(opcode); break;
            case 0x25: ExecuteLhu(opcode); break;
            case 0x26: ExecuteLwr(opcode); break;
            case 0x27: ExecuteLwu(opcode); break; // LWU (was wrongly mapped to LD)
            case 0x28: ExecuteSb(opcode); break;
            case 0x29: ExecuteSh(opcode); break;
            case 0x2A: ExecuteSwl(opcode); break;
            case 0x2B: ExecuteSw(opcode); break;
            case 0x2C: ExecuteSdl(opcode); break; // SDL simplified
            case 0x2D: ExecuteSdr(opcode); break; // SDR simplified
            case 0x2E: ExecuteSwr(opcode); break;
            // COP1 memory ops — retail math / Midway uses these heavily
            case 0x31: ExecuteLwc1(opcode); break; // LWC1
            case 0x35: ExecuteLdc1(opcode); break; // LDC1
            case 0x39: ExecuteSwc1(opcode); break; // SWC1
            case 0x3D: ExecuteSdc1(opcode); break; // SDC1
            // 0x30–0x37: LD is 0x37; 0x38–0x3F: SD is 0x3F (R5900 / MIPS III)
            case 0x37: ExecuteLd(opcode); break;  // LD  (was wrongly mapped to SD)
            case 0x3F: ExecuteSd(opcode); break;  // SD  (was missing → UnknownOpcode 0xFFxx)

            default:
                _telemetry?.UnknownOpcode(CurrentCycle(), PC, opcode);
                break;
        }

        return false;
    }

    private ulong CurrentCycle() => _cycleSource?.Invoke() ?? 0;

    // ==================== SPECIAL ====================
    private bool ExecuteSpecial(uint opcode)
    {
        uint function = opcode & 0x3F;
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        uint rd = (opcode >> 11) & 0x1F;
        uint sa = (opcode >> 6) & 0x1F;

        switch (function)
        {
            // SLL/SRL/SRA(V) are 32-bit MIPS ops: truncate rt to its low 32 bits, shift, then
            // sign-extend the 32-bit RESULT into the 64-bit register — same class of bug as
            // LUI/LW (see their comments). The old code shifted the full 64-bit register value
            // directly with no truncation or result sign-extension, which is wrong whenever
            // rt's upper 32 bits aren't a clean sign-extension of its low 32 (e.g. after any
            // 64-bit D-op) and/or the 32-bit shift result has bit 31 set.
            case 0x00: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = unchecked((ulong)(long)((int)GetGpr(rt).Lo << (int)sa)) }); break; // SLL
            case 0x01: break; // MOVCI / reserved — nop (seen in retail data-as-code fallthrough)
            case 0x02: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = unchecked((ulong)(long)(int)((uint)GetGpr(rt).Lo >> (int)sa)) }); break; // SRL
            case 0x03: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = unchecked((ulong)(long)((int)GetGpr(rt).Lo >> (int)sa)) }); break; // SRA

            case 0x04: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = unchecked((ulong)(long)((int)GetGpr(rt).Lo << (int)(GetGpr(rs).Lo & 0x1F))) }); break; // SLLV
            case 0x06: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = unchecked((ulong)(long)(int)((uint)GetGpr(rt).Lo >> (int)(GetGpr(rs).Lo & 0x1F))) }); break; // SRLV
            case 0x07: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = unchecked((ulong)(long)((int)GetGpr(rt).Lo >> (int)(GetGpr(rs).Lo & 0x1F))) }); break; // SRAV

            case 0x08: // JR — ignore jumps into low/vector page (uninitialized fptrs)
                {
                    ulong t = GetGpr(rs).Lo;
                    // `jr ra` (rs=31) with a target of EXACTLY 0 is not garbage to mask away
                    // silently -- it's KernelState.RestoreContext's own documented convention
                    // (see its "$ra = 0 so ExitThread path is clean" comment): a freshly-started
                    // thread's ra is deliberately seeded to 0 so that a thread function naturally
                    // returning (instead of calling ExitThread itself) can be detected as an
                    // implicit exit. That detection was never actually wired up anywhere -- the
                    // return just silently fell through into whatever code follows in memory,
                    // including, confirmed via Mortal Kombat: Shaolin Monks, an entire table of
                    // syscall trampolines each firing a real, unintended syscall as a side effect
                    // (see docs/DEVELOPER_GUIDE.md's SifBindRpc-investigation follow-ups). Honor
                    // the documented convention for real instead of masking its symptom.
                    //
                    // Previously excluded thread 1 here (CurrentThreadId != 1), on the theory that
                    // its ra=0 could only ever be the raw, never-overwritten CPU boot-state default
                    // (KernelHle.cs creates thread 1 synthetically -- Started=true from
                    // construction, Entry=0 -- so it never goes through a real StartThread/
                    // RestoreContext cycle, the only thing that deliberately seeds ra=0 as an exit
                    // signal). That exclusion was itself wrong: live-traced (2026-07-27, Mortal
                    // Kombat: Shaolin Monks) a REAL, later occurrence of `jr ra` with ra==0 on
                    // thread 1 at cyc≈28.5M -- deep into real execution, nowhere near boot, loaded
                    // straight from a saved stack slot by a completely ordinary function epilogue.
                    // With the exclusion in place, that fell through (see the JR-guard below) into
                    // whatever code happened to follow in memory -- confirmed via
                    // DETPS2_TRACE_JRGUARD to cascade through 40,000+ instructions including an
                    // entire table of unrelated syscall trampolines, each firing a real, unintended
                    // syscall, before coincidentally landing on the game's own fatal-error path and
                    // triggering an `Exit(1)` that had nothing to do with any real error condition
                    // -- a symptom of this exact garbage-execution bug class, not a real game panic.
                    // Removing the exclusion avoids all of that: DETPS2_TRACE_JRGUARD shows zero
                    // fallthrough events across the same run, and DETPS2_TRACE_JREXIT shows the
                    // ra==0 case at cyc=1,350,000 (the same point that motivated the original
                    // exclusion -- see docs/DEVELOPER_GUIDE.md) now genuinely stalls, then recovers
                    // and reaches real, clean code well past where the old exclusion's replacement
                    // theory assumed it would freeze forever. Verified via the full 9-title
                    // `user-media.json`: identical results for every other title, so this wasn't
                    // covering for some other thread-1-specific need elsewhere.
                    if (rs == 31 && t == 0 && _hle != null)
                    {
                        int exitingTid = _hle.Kernel.CurrentThreadId;
                        _hle.Kernel.ExitCurrentThread();
                        bool switched = _hle.Kernel.SwitchToNext(this);
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_JREXIT") == "1")
                            Console.Error.WriteLine($"[JREXIT] pc=0x{PC:X8} tid={exitingTid} switched={switched} newPc=0x{(switched ? HleRedirectPc ?? 0 : 0):X8} cyc={CurrentCycle()}");
                        // If no other thread is runnable, do NOT fall through and execute whatever
                        // raw bytes happen to sit at this jr's delay slot and beyond as if they
                        // were real instructions (see _pendingThreadStall's doc comment for the
                        // real crash this caused). Genuinely stall instead — Step()'s top-of-loop
                        // check keeps retrying SwitchToNext every cycle until something elsewhere
                        // (IOP/SIF progress, a timer, etc.) makes another thread runnable.
                        if (!switched)
                            _pendingThreadStall = true;
                        return false;
                    }
                    // Guard entire low 64KB (vectors + trap + recovery), not just 4KB
                    if ((t & 0x1FFFFFFFUL) < 0x10000UL && !IsLegitimateVectorTarget(t))
                    {
                        if (TraceJrGuard)
                            Console.Error.WriteLine($"[JRGUARD] pc=0x{PC:X8} rs={rs} target=0x{t:X16} -> falls through instead of jumping");
                        break; // nop: stay sequential
                    }
                    _delaySlotTarget = t;
                    return true;
                }
            case 0x09: // JALR
                {
                    ulong t = GetGpr(rs).Lo;
                    if (rd != 0) SetGpr(rd, new Gpr128 { Lo = PC + 8 });
                    if ((t & 0x1FFFFFFFUL) < 0x10000UL && !IsLegitimateVectorTarget(t))
                    {
                        if (TraceJrGuard)
                            Console.Error.WriteLine($"[JRGUARD] pc=0x{PC:X8} rs={rs} target=0x{t:X16} -> falls through instead of jumping (jalr)");
                        break;
                    }
                    _delaySlotTarget = t;
                    return true;
                }

            case 0x0A: if (GetGpr(rt).Lo == 0 && rd != 0) SetGpr(rd, GetGpr(rs)); break;
            case 0x0B: if (GetGpr(rt).Lo != 0 && rd != 0) SetGpr(rd, GetGpr(rs)); break;

            case 0x0C: HandleSyscall(opcode); break;
            case 0x0D: HandleBreak(opcode); break;
            case 0x0E: break; // SYNC / TEQ-ish family — treat unused SPECIAL as nop when not TEQ
            case 0x0F: break; // SYNC nop
            case 0x30: // TGE
            case 0x31: // TGEU
            case 0x32: // TLT
            case 0x33: // TLTU
            case 0x34: // TEQ
            case 0x35: // — reserved
            case 0x36: // TNE
            case 0x05: // — reserved / rare
            case 0x1C: // — reserved / rare
                // Conditional traps / unused SPECIAL: nop under HLE-friendly path
                break;

            case 0x10: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = HI }); break;
            case 0x11: HI = GetGpr(rs).Lo; break;
            case 0x12: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = LO }); break;
            case 0x13: LO = GetGpr(rs).Lo; break;

            // MULT/MULTU/DIV/DIVU: real MIPS sign-extends BOTH LO and HI as independent 32-bit
            // halves — including MULTU/DIVU, despite the multiply/divide itself being unsigned
            // (a well-known R-series quirk). The old code zero-extended via `(uint)` casts, so
            // e.g. a negative 32-bit quotient/product half read back as a huge positive 64-bit
            // value instead — same bug class as SLL/SRL/ADD/SUB above.
            case 0x18: // MULT (signed 32)
                {
                    int a = (int)(uint)GetGpr(rs).Lo; int b = (int)(uint)GetGpr(rt).Lo;
                    long res = (long)a * b;
                    LO = unchecked((ulong)(long)(int)res);
                    HI = unchecked((ulong)(res >> 32)); // arithmetic shift already yields the sign-extended high half
                    // R5900 extension: MULT rd,rs,rt (rd != 0) ALSO writes the low-32 sign-extended
                    // product to a regular GPR, not just HI/LO — compilers emit this constantly to
                    // avoid a separate mflo. MULT1 (below) already did this; base MULT didn't, which
                    // left rd holding a stale value wherever code relied on the 3-operand form
                    // (e.g. array-index scaling: "mult t0,t0,v0" then immediately using t0).
                    if (rd != 0) SetGpr(rd, new Gpr128 { Lo = LO });
                }
                break;
            case 0x19: // MULTU (unsigned 32) — Phase 20 accuracy
                {
                    uint a = (uint)GetGpr(rs).Lo; uint b = (uint)GetGpr(rt).Lo;
                    ulong res = (ulong)a * b;
                    LO = unchecked((ulong)(long)(int)(uint)res);
                    HI = unchecked((ulong)(long)(int)(uint)(res >> 32));
                    if (rd != 0) SetGpr(rd, new Gpr128 { Lo = LO }); // same R5900 3-operand extension as MULT
                }
                break;

            case 0x1A: // DIV (signed)
                {
                    int a = (int)(uint)GetGpr(rs).Lo; int b = (int)(uint)GetGpr(rt).Lo;
                    if (b != 0) { LO = unchecked((ulong)(long)(a / b)); HI = unchecked((ulong)(long)(a % b)); }
                }
                break;
            case 0x1B: // DIVU (unsigned) — Phase 20 accuracy
                {
                    uint a = (uint)GetGpr(rs).Lo; uint b = (uint)GetGpr(rt).Lo;
                    if (b != 0) { LO = unchecked((ulong)(long)(int)(a / b)); HI = unchecked((ulong)(long)(int)(a % b)); }
                }
                break;

            case 0x38: // DSLL
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rt).Lo << (int)sa }); break;
            case 0x3A: // DSRL
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rt).Lo >> (int)sa }); break;
            case 0x3B: // DSRA
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = (ulong)((long)GetGpr(rt).Lo >> (int)sa) }); break;
            case 0x3C: // DSLL32
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rt).Lo << (int)(sa + 32) }); break;
            case 0x3E: // DSRL32
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rt).Lo >> (int)(sa + 32) }); break;
            case 0x3F: // DSRA32
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = (ulong)((long)GetGpr(rt).Lo >> (int)(sa + 32)) }); break;

            // ADD/ADDU are 32-bit ops: truncate both operands to 32 bits, add, sign-extend the
            // 32-bit result — NOT a full 64-bit add of the raw register values. The old shared
            // code with DADD/DADDU did a 64-bit add for all four, which silently diverges from
            // real hardware any time the true 32-bit sum crosses the sign boundary (e.g.
            // 0x7FFFFFFF+1), which is routine in real compiled loop counters/pointer math — the
            // same bug class as SLL/SRL/MULT/DIV above, just far more common since ADD/ADDU are
            // among the most-used instructions in any compiled MIPS binary.
            case 0x20: case 0x21: // ADD / ADDU (32-bit; HLE ignores overflow trap on ADD)
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = unchecked((ulong)(long)(int)((uint)GetGpr(rs).Lo + (uint)GetGpr(rt).Lo)) });
                break;
            case 0x2C: // DADD (64-bit) — was UnknownSpecial:0x2C storm on retail titles
            case 0x2D: // DADDU
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rs).Lo + GetGpr(rt).Lo });
                break;
            case 0x22: case 0x23: // SUB / SUBU (32-bit — same truncate/sign-extend rule as ADD/ADDU)
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = unchecked((ulong)(long)(int)((uint)GetGpr(rs).Lo - (uint)GetGpr(rt).Lo)) });
                break;
            case 0x2E: // DSUB
            case 0x2F: // DSUBU
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rs).Lo - GetGpr(rt).Lo });
                break;
            case 0x27: // NOR
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = ~(GetGpr(rs).Lo | GetGpr(rt).Lo) }); break;
            case 0x2A: // SLT
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = (long)GetGpr(rs).Lo < (long)GetGpr(rt).Lo ? 1UL : 0UL }); break;
            case 0x2B: // SLTU
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rs).Lo < GetGpr(rt).Lo ? 1UL : 0UL }); break;
            case 0x14: // DSLLV
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rt).Lo << (int)(GetGpr(rs).Lo & 0x3F) }); break;
            case 0x16: // DSRLV
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rt).Lo >> (int)(GetGpr(rs).Lo & 0x3F) }); break;
            case 0x17: // DSRAV
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = (ulong)((long)GetGpr(rt).Lo >> (int)(GetGpr(rs).Lo & 0x3F)) }); break;
            case 0x28: // MFSA (R5900) — shift amount register; expose as 0 until full pipeline model
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = 0 });
                break;
            case 0x29: // MTSA — accept writes as nop for now
                break;
            case 0x24: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rs).Lo & GetGpr(rt).Lo }); break;
            case 0x25: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rs).Lo | GetGpr(rt).Lo }); break;
            case 0x26: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rs).Lo ^ GetGpr(rt).Lo }); break;

            default:
                _telemetry?.UnknownSpecial(CurrentCycle(), PC, opcode);
                break;
        }

        return false;
    }

    private bool ExecuteRegimm(uint opcode)
    {
        uint rt = (opcode >> 16) & 0x1F;
        uint rs = (opcode >> 21) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong target = PC + 4 + (ulong)((int)offset << 2);

        // rt selects: BLTZ=0x00 BGEZ=0x01 BLTZL=0x02 BGEZL=0x03
        //             BLTZAL=0x10 BGEZAL=0x11 BLTZALL=0x12 BGEZALL=0x13
        // bit0=GE(vs LT), bit1=likely, bit4=link. Only these 8 rt values are real
        // REGIMM branches (0x08-0x0E are trap-on-condition, not branches — not handled).
        if (rt <= 0x03 || (rt >= 0x10 && rt <= 0x13))
        {
            bool ge = (rt & 1) != 0;
            bool likely = (rt & 2) != 0;
            bool link = (rt & 0x10) != 0;
            bool cond = ge ? (long)GetGpr(rs).Lo >= 0 : (long)GetGpr(rs).Lo < 0;

            // $ra is set unconditionally on real MIPS, whether or not the branch is taken.
            if (link) SetGpr(31, new Gpr128 { Lo = PC + 8 });
            if (likely) _branchWasLikely = true;

            if (cond) { _delaySlotTarget = target; return true; }
            return false;
        }

        // MTSAB/MTSAH — real R5900 REGIMM extensions (not branches) that set the SA register
        // QFSRV consumes. Semantics verified against Play!'s CMA_EE::MTSAB/MTSAH (see _sa's own
        // doc comment): SA = ((GPR[rs] & mask) XOR (imm & mask)) << shift, byte-granular for
        // MTSAB (mask=0xF, shift=3) and halfword-granular for MTSAH (mask=0x7, shift=4) — both
        // land in the same underlying bit-unit SA register QFSRV reads directly.
        if (rt == 0x18) // MTSAB
        {
            uint imm = opcode & 0xFFFFu;
            _sa = (((uint)GetGpr(rs).Lo & 0xFu) ^ (imm & 0xFu)) << 3;
            return false;
        }
        if (rt == 0x19) // MTSAH
        {
            uint imm = opcode & 0xFFFFu;
            _sa = (((uint)GetGpr(rs).Lo & 0x7u) ^ (imm & 0x7u)) << 4;
            return false;
        }
        return false;
    }

    private bool ExecuteJ(uint opcode) { uint t = opcode & 0x03FFFFFF; _delaySlotTarget = (PC & 0xF0000000UL) | (t << 2); return true; }
    private bool ExecuteJal(uint opcode) { SetGpr(31, new Gpr128 { Lo = PC + 8 }); uint t = opcode & 0x03FFFFFF; _delaySlotTarget = (PC & 0xF0000000UL) | (t << 2); return true; }

    private bool ExecuteBeq(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; uint rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        if (GetGpr(rs).Lo == GetGpr(rt).Lo) { _delaySlotTarget = PC + 4 + (ulong)((int)off << 2); return true; }
        return false;
    }

    private bool ExecuteBne(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; uint rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        if (GetGpr(rs).Lo != GetGpr(rt).Lo) { _delaySlotTarget = PC + 4 + (ulong)((int)off << 2); return true; }
        return false;
    }

    /// <summary>
    /// Detect BNE rs,rt,-N with N small (tight loop) and |rs-rt| enormous — common
    /// memset/delay when length was sign-extended wrong. Snap rs to rt to finish.
    /// </summary>
    private void MaybeFastForwardCountdown(uint bneOpcode)
    {
        short off = (short)(bneOpcode & 0xFFFF);
        if (off >= -16 && off < 0)
        {
            uint rs = (bneOpcode >> 21) & 0x1F;
            uint rt = (bneOpcode >> 16) & 0x1F;
            ulong a = GetGpr(rs).Lo;
            ulong b = GetGpr(rt).Lo;
            // strcpy/strlen null checks commonly do `sll v0, byte, 24; bne v0, zero, loop`.
            // The character lives only in bits 31–24 so |v0-0| looks "enormous" and the
            // old heuristic snapped v0→0 after the first non-null byte — truncating every
            // unaligned strcpy to a single character (live: GAMEDATA.WAD became "G", so
            // ADXF open failed and MKSM never loaded the WAD past the logo). Never snap a
            // value that is a pure high-byte mask.
            static bool LooksLikeShiftedByte(ulong x) => x != 0 && (x & 0x00FFFFFFUL) == 0;
            if (LooksLikeShiftedByte(a) || LooksLikeShiftedByte(b))
                return;
            // memcpy/memset unaligned tails commonly do:
            //   addiu a2, a2, -1; lbu; sb; bne a2, -1, loop
            // with the sentinel held as 0xFFFFFFFFFFFFFFFF. |len - (-1)| is always
            // enormous for any small positive remaining count, so the old dist>50k
            // snap forced len→-1 after the first byte — every unaligned memcpy became
            // 1 byte (Blood Omen 2: "cdrom0:" → "c", SifIopReset "rom0:UDNL c").
            // Refuse the snap when one side is the all-ones sentinel and the other is a
            // modest remaining count (real copy tails). Huge Midway spins that count
            // toward -1 from tens of millions still snap via the dist check below.
            const ulong modestCopyTail = 1_048_576UL; // 1 MiB
            if ((a == ulong.MaxValue && b < modestCopyTail) ||
                (b == ulong.MaxValue && a < modestCopyTail))
                return;
            // Distance in 64-bit space
            ulong dist = a > b ? a - b : b - a;
            // Software delay loops (e.g. Midway spin counting to 0/-1) burn tens of millions
            // of EE cycles; snap earlier so commercial boot reaches graph init.
            if (dist > 50_000UL)
            {
                SetGpr(rs, new Gpr128 { Lo = b, Hi = GetGpr(rt).Hi });
            }
        }
    }

    private bool ExecuteBlez(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; short off = (short)(opcode & 0xFFFF);
        if ((long)GetGpr(rs).Lo <= 0) { _delaySlotTarget = PC + 4 + (ulong)((int)off << 2); return true; }
        return false;
    }

    private bool ExecuteBgtz(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; short off = (short)(opcode & 0xFFFF);
        if ((long)GetGpr(rs).Lo > 0) { _delaySlotTarget = PC + 4 + (ulong)((int)off << 2); return true; }
        return false;
    }

    private void ExecuteAddi(uint opcode) => ExecuteAddiu(opcode);

    private void ExecuteAddiu(uint opcode)
    {
        // ADDIU is a 32-bit op: truncate rs to its low 32 bits, add the sign-extended
        // immediate as a 32-bit add, then sign-extend the 32-bit RESULT to 64 bits — not a
        // raw 64-bit add of rs's full register value (that's DADDIU's job). The old code did
        // exactly that raw 64-bit add, which is silently wrong whenever the true 32-bit sum
        // crosses the sign boundary (e.g. computing a small negative offset via
        // `addiu rt,rs,-N`) — the same bug class as LUI/LW/ADD/SLL (see their comments), and
        // likely the highest-impact instance of it given how common ADDIU is in compiled code.
        uint rs = (opcode >> 21) & 0x1F; uint rt = (opcode >> 16) & 0x1F;
        short imm = (short)(opcode & 0xFFFF);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = unchecked((ulong)(long)(int)((uint)GetGpr(rs).Lo + (uint)(int)imm)) });
    }

    private void ExecuteDaddi(uint opcode) => ExecuteDaddiu(opcode);

    private void ExecuteDaddiu(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; uint rt = (opcode >> 16) & 0x1F;
        short imm = (short)(opcode & 0xFFFF);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = GetGpr(rs).Lo + (ulong)(long)imm });
    }

    private void ExecuteLwc1(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; uint ft = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)(int)off;
        uint bits = _memory.Read32(addr);
        _fpr[ft] = DeterministicFloat.Canonicalize(BitConverter.UInt32BitsToSingle(bits));
    }

    private void ExecuteSwc1(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; uint ft = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)(int)off;
        uint bits = BitConverter.SingleToUInt32Bits(_fpr[ft]);
        _memory.Write32(addr, bits);
    }

    private void ExecuteLdc1(uint opcode)
    {
        // EE FPU is single-precision; treat LDC1 as two consecutive S loads
        uint rs = (opcode >> 21) & 0x1F; uint ft = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)(int)off;
        uint lo = _memory.Read32(addr);
        uint hi = _memory.Read32(addr + 4);
        _fpr[ft] = DeterministicFloat.Canonicalize(BitConverter.UInt32BitsToSingle(lo));
        if (ft + 1 < _fpr.Length)
            _fpr[ft + 1] = DeterministicFloat.Canonicalize(BitConverter.UInt32BitsToSingle(hi));
    }

    private void ExecuteSdc1(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; uint ft = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)(int)off;
        _memory.Write32(addr, BitConverter.SingleToUInt32Bits(_fpr[ft]));
        if (ft + 1 < _fpr.Length)
            _memory.Write32(addr + 4, BitConverter.SingleToUInt32Bits(_fpr[ft + 1]));
    }

    private void ExecuteSlti(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; uint rt = (opcode >> 16) & 0x1F;
        short imm = (short)(opcode & 0xFFFF);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = ((long)GetGpr(rs).Lo < imm) ? 1UL : 0UL });
    }

    private void ExecuteSltiu(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; uint rt = (opcode >> 16) & 0x1F;
        short imm = (short)(opcode & 0xFFFF);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = (GetGpr(rs).Lo < (ulong)imm) ? 1UL : 0UL });
    }

    private void ExecuteOri(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; uint rt = (opcode >> 16) & 0x1F;
        ushort imm = (ushort)(opcode & 0xFFFF);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = GetGpr(rs).Lo | imm });
    }

    private void ExecuteXori(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; uint rt = (opcode >> 16) & 0x1F;
        ushort imm = (ushort)(opcode & 0xFFFF);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = GetGpr(rs).Lo ^ imm });
    }

    private void ExecuteAndi(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; uint rt = (opcode >> 16) & 0x1F;
        ushort imm = (ushort)(opcode & 0xFFFF);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = GetGpr(rs).Lo & imm });
    }

    private void ExecuteLui(uint opcode)
    {
        // Real MIPS64/R5900 LUI sign-extends its 32-bit result (imm<<16) to 64 bits — this
        // used to zero-extend instead, so `lui rt,0xFFFF` produced 0x00000000FFFF0000
        // instead of the correct 0xFFFFFFFFFFFF0000. The extremely common `lui rt,0xFFFF;
        // ori rt,rt,0xFFFF` idiom for loading a -1 (or any other negative 32-bit) constant
        // therefore produced 0x00000000FFFFFFFF instead of the true 64-bit -1
        // (0xFFFFFFFFFFFFFFFF), silently breaking any 64-bit compare/branch against a
        // properly sign-extended value built via addiu (which already sign-extends
        // correctly). Confirmed as a real, firing bug: this exact mismatch made a loop-exit
        // check inside MK Shaolin Monks' own compiled memset() fail to match, causing one
        // extra byte to be written past the intended range and corrupting an adjacent
        // stack slot — see DEVELOPER_GUIDE.md #7.4 for the full trace.
        uint rt = (opcode >> 16) & 0x1F; ushort imm = (ushort)(opcode & 0xFFFF);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = unchecked((ulong)(long)(int)((uint)imm << 16)) });
    }

    private void ExecuteLb(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; uint rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)off;
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = (ulong)(sbyte)_memory.Read8(addr) });
    }

    private void ExecuteLbu(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; uint rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)off;
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = _memory.Read8(addr) });
    }

    private void ExecuteLh(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; uint rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)off;
        short val = (short)(_memory.Read8(addr) | (_memory.Read8(addr + 1) << 8));
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = (ulong)val });
    }

    private void ExecuteLhu(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; uint rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)off;
        ushort val = (ushort)(_memory.Read8(addr) | (_memory.Read8(addr + 1) << 8));
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = val });
    }

    private void ExecuteLw(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; uint rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)off;
        // LW sign-extends into the 64-bit GPR (that's exactly what distinguishes it from
        // LWU, which zero-extends) — a plain uint->ulong assignment here is a zero-extend,
        // silently turning any loaded value with the high bit set (e.g. -1, or any negative
        // 32-bit int/loop-bound-sentinel) into a huge positive 64-bit number instead. Found
        // by hand-tracing a real infinite loop: `slt v0,s0,a1` with a1 loaded as 0xFFFFFFFF
        // via LW should compare as "positive < -1" (false, loop should end) but was instead
        // comparing "positive < +4294967295" (true, forever) because of this bug.
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = (ulong)(long)(int)_memory.Read32(addr) });
    }

    private void ExecuteSb(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; uint rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        _memory.Write8(GetGpr(rs).Lo + (ulong)off, (byte)GetGpr(rt).Lo);
    }

    private void ExecuteSh(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; uint rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)off;
        ushort v = (ushort)GetGpr(rt).Lo;
        _memory.Write8(addr, (byte)v); _memory.Write8(addr + 1, (byte)(v >> 8));
    }

    private void ExecuteSw(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F; uint rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        _memory.Write32(GetGpr(rs).Lo + (ulong)off, (uint)GetGpr(rt).Lo);
    }

    private bool ExecuteCop2(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        uint rd = (opcode >> 11) & 0x1F;
        uint sa = (opcode >> 6) & 0x1F;
        uint function = opcode & 0x3F;

        switch (rs)
        {
            case 0x00:
                if (rt != 0) SetGpr(rt, new Gpr128 { Lo = 0 });
                break;
            case 0x02:
                break;
            case 0x04:
                if (_vu0 != null && rt < 32)
                {
                    var reg = _vu0.GetVfRegister(rd);
                    SetGpr(rt, new Gpr128 { Lo = (ulong)BitConverter.SingleToInt32Bits(reg.X) });
                }
                break;
            case 0x06:
                if (_vu0 != null && rd < 32)
                {
                    Gpr128 gpr = GetGpr(rt);
                    float val = BitConverter.Int32BitsToSingle((int)gpr.Lo);
                    _vu0.SetVfRegister(rd, new VectorUnit.VuReg128 { X = val, Y = 0, Z = 0, W = 1 });
                }
                break;
            case 0x10: // COP2 special / VU op
            case 0x11:
            case 0x12:
            case 0x13:
            case 0x14:
            case 0x15:
            case 0x16:
            case 0x17:
                if (_vu0 != null)
                {
                    int cost = _vu0.ExecuteVuInstruction(function, rs, rt, rd, sa);
                    _cop2StallRemaining = Math.Max(_cop2StallRemaining, cost);
                }
                break;
            default:
                if (_vu0 != null && function != 0)
                {
                    int cost = _vu0.ExecuteVuInstruction(function, rs, rt, rd, sa);
                    _cop2StallRemaining = Math.Max(_cop2StallRemaining, cost);
                }
                break;
        }

        return false;
    }

    /// <summary>
    /// MMI (SPECIAL2 / 0x1C) — parallel integer SIMD ops over 128-bit GPRs.
    ///
    /// Real R5900 encoding is a TWO-field dispatch: bits[5:0] ("func") only
    /// narrows to one of {8, 9, 0x28, 0x29} for the whole 128-bit arithmetic/
    /// logic family; bits[10:6] ("sa") then selects the actual instruction
    /// within that family. An earlier version of this method treated bits[5:0]
    /// alone as the complete opcode, which happened to work for a couple of
    /// entries by coincidence (PADDW) but was outright wrong for others —
    /// PSUBW was listening on func=9, a slot the real ISA shares between
    /// PMFHI/PMADDW/PCPYLD/PEXTUH/etc. depending on sa, so it would have
    /// misfired on any of those instead of PSUBW.
    ///
    /// Verified against ps2dev-community opcode tables (github.com/wasaylor/
    /// r5900-opcodes). Two (sa,func) slots had internally contradictory
    /// documentation even in that source (PAND vs. a claimed "PEXTUW" both at
    /// sa=18/func=9; a PEXEH-adjacent slot vs. "PEXTUB" both at sa=26/func=9) —
    /// rather than guess, PAND is implemented at the contested slot (completes
    /// the AND/OR/XOR/NOR family, which two independent, uncontested slots —
    /// POR and PNOR — already confirm exists at neighboring sa values) and the
    /// "U-extract" 32/8-bit variants some sources allege share it are left
    /// unhandled (still telemetry-visible) rather than silently wrong.
    /// </summary>
    private void ExecuteMmi(uint opcode)
    {
        uint func = opcode & 0x3F;
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        uint rd = (opcode >> 11) & 0x1F;
        uint sa = (opcode >> 6) & 0x1F;

        if (func is 0x08 or 0x09 or 0x28 or 0x29)
        {
            ExecuteMmiFamily(sa, func, rs, rt, rd);
            return;
        }

        // Real tbl_MMI[64] (verified against PCSX2's R5900OpcodeTables.cpp/MMI.cpp — the
        // func field directly indexes a 64-entry table; only 8/9/0x28/0x29 delegate to the
        // MMI0-3 sub-tables above, everything else here is a direct top-level instruction).
        switch (func)
        {
            case 0x00: // MADD — pipeline-0 32x32+64->64 accumulate (shares LO/HI with MULT/DIV)
            {
                long acc = unchecked((long)((uint)LO | ((ulong)(uint)HI << 32)));
                long temp = acc + (long)(int)GetGpr(rs).Lo * (int)GetGpr(rt).Lo;
                // Same sign-extension rule as MULT/DIV (see their comments): each 32-bit half
                // is sign-extended independently, not zero-extended.
                LO = unchecked((ulong)(long)(int)temp);
                HI = unchecked((ulong)(temp >> 32));
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = LO });
                break;
            }
            case 0x01: // MADDU
            {
                ulong acc = (uint)LO | ((ulong)(uint)HI << 32);
                ulong tempu = unchecked(acc + (ulong)(uint)GetGpr(rs).Lo * (uint)GetGpr(rt).Lo);
                LO = unchecked((ulong)(long)(int)(uint)tempu);
                HI = unchecked((ulong)(long)(int)(uint)(tempu >> 32));
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = LO });
                break;
            }
            case 0x04: // PLZCW — leading zero/one run length per 32-bit lane (sign bit excluded)
            {
                if (rd == 0) break;
                var aw = ExtractW(GetGpr(rs));
                var r = new uint[4];
                for (int i = 0; i < 4; i++) r[i] = (uint)PlzcwLane(aw[i]);
                SetGpr(rd, PackW(r));
                break;
            }
            case 0x10: // MFHI1
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = HI1 });
                break;
            case 0x11: // MTHI1
                HI1 = GetGpr(rs).Lo;
                break;
            case 0x12: // MFLO1
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = LO1 });
                break;
            case 0x13: // MTLO1
                LO1 = GetGpr(rs).Lo;
                break;
            case 0x18: // MULT1
            {
                long res = (long)(int)GetGpr(rs).Lo * (int)GetGpr(rt).Lo;
                // Same sign-extension rule as base-pipeline MULT — each 32-bit half is
                // sign-extended independently (this "1"-pipeline family was missed by the
                // earlier sign-extension audit, which only covered the base pipeline).
                LO1 = unchecked((ulong)(long)(int)res);
                HI1 = unchecked((ulong)(res >> 32));
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = LO1 });
                break;
            }
            case 0x19: // MULTU1
            {
                ulong res = (ulong)(uint)GetGpr(rs).Lo * (uint)GetGpr(rt).Lo;
                LO1 = unchecked((ulong)(long)(int)(uint)res);
                HI1 = unchecked((ulong)(long)(int)(uint)(res >> 32));
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = LO1 });
                break;
            }
            case 0x1A: // DIV1
            {
                int a = (int)(uint)GetGpr(rs).Lo, b = (int)(uint)GetGpr(rt).Lo;
                if ((uint)a == 0x80000000u && b == -1) { LO1 = unchecked((ulong)(long)(int)0x80000000u); HI1 = 0; }
                else if (b != 0) { LO1 = unchecked((ulong)(long)(a / b)); HI1 = unchecked((ulong)(long)(a % b)); }
                else { LO1 = unchecked((ulong)(long)(a < 0 ? 1 : -1)); HI1 = unchecked((ulong)(long)a); }
                break;
            }
            case 0x1B: // DIVU1
            {
                uint a = (uint)GetGpr(rs).Lo, b = (uint)GetGpr(rt).Lo;
                if (b != 0) { LO1 = unchecked((ulong)(long)(int)(a / b)); HI1 = unchecked((ulong)(long)(int)(a % b)); }
                else { LO1 = unchecked((ulong)(long)(int)(uint)(-1)); HI1 = unchecked((ulong)(long)(int)a); }
                break;
            }
            case 0x20: // MADD1
            {
                long acc = unchecked((long)((uint)LO1 | ((ulong)(uint)HI1 << 32)));
                long temp = acc + (long)(int)GetGpr(rs).Lo * (int)GetGpr(rt).Lo;
                LO1 = unchecked((ulong)(long)(int)temp);
                HI1 = unchecked((ulong)(temp >> 32));
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = LO1 });
                break;
            }
            case 0x21: // MADDU1
            {
                ulong acc = (uint)LO1 | ((ulong)(uint)HI1 << 32);
                ulong tempu = unchecked(acc + (ulong)(uint)GetGpr(rs).Lo * (uint)GetGpr(rt).Lo);
                LO1 = unchecked((ulong)(long)(int)(uint)tempu);
                HI1 = unchecked((ulong)(long)(int)(uint)(tempu >> 32));
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = LO1 });
                break;
            }

            // ---- Shift-immediate families (PSLLH/PSRLH/PSRAH over 8 halfwords,
            // PSLLW/PSRLW/PSRAW over 4 words) — sa is the shift amount here, not a sub-opcode.
            case 0x34: // PSLLH
                if (rd != 0) { var h = HalfOp1(GetGpr(rt), static (x, s) => unchecked((ushort)(x << (int)(s & 0xF))), sa); SetGpr(rd, PackH(h)); }
                break;
            case 0x36: // PSRLH
                if (rd != 0) { var h = HalfOp1(GetGpr(rt), static (x, s) => (ushort)(x >> (int)(s & 0xF)), sa); SetGpr(rd, PackH(h)); }
                break;
            case 0x37: // PSRAH
                if (rd != 0) { var h = HalfOp1(GetGpr(rt), static (x, s) => (ushort)((short)x >> (int)(s & 0xF)), sa); SetGpr(rd, PackH(h)); }
                break;
            case 0x3C: // PSLLW
                if (rd != 0) { var w = WordOp1(GetGpr(rt), static (x, s) => x << (int)s, sa); SetGpr(rd, PackW(w)); }
                break;
            case 0x3E: // PSRLW
                if (rd != 0) { var w = WordOp1(GetGpr(rt), static (x, s) => x >> (int)s, sa); SetGpr(rd, PackW(w)); }
                break;
            case 0x3F: // PSRAW
                if (rd != 0) { var w = WordOp1(GetGpr(rt), static (x, s) => (uint)((int)x >> (int)s), sa); SetGpr(rd, PackW(w)); }
                break;

            default:
                _telemetry?.UnknownOpcode(CurrentCycle(), PC, opcode | 0x1C000000u);
                break;
        }
    }

    private static uint[] WordOp1(Gpr128 t, Func<uint, uint, uint> op, uint sa)
    {
        var tw = ExtractW(t);
        var r = new uint[4];
        for (int i = 0; i < 4; i++) r[i] = op(tw[i], sa);
        return r;
    }

    private static ushort[] HalfOp1(Gpr128 t, Func<ushort, uint, ushort> op, uint sa)
    {
        var th = ExtractH(t);
        var r = new ushort[8];
        for (int i = 0; i < 8; i++) r[i] = op(th[i], sa);
        return r;
    }

    private void ExecuteMmiFamily(uint sa, uint func, uint rs, uint rt, uint rd)
    {
        // PMTHI/PMTLO (MMI3, sa=8/9) write only to HI/HI1 or LO/LO1 — real encoding leaves
        // rd unused (0), so they must run before the rd==0 guard below that every other
        // (rd-producing) entry in this table relies on.
        if (func == 0x29 && sa is 8 or 9)
        {
            var src = GetGpr(rs);
            if (sa == 8) { HI = src.Lo; HI1 = src.Hi; } // PMTHI
            else { LO = src.Lo; LO1 = src.Hi; }         // PMTLO
            return;
        }

        if (rd == 0) return;
        var a = GetGpr(rs);
        var b = GetGpr(rt);
        uint key = (sa << 6) | func;

        switch (key)
        {
            // ---- word lanes (4x32) ----
            case (0u << 6) | 0x08: SetGpr(rd, PackW(WordOp(a, b, static (x, y) => unchecked(x + y)))); break; // PADDW
            case (1u << 6) | 0x08: SetGpr(rd, PackW(WordOp(a, b, static (x, y) => unchecked(x - y)))); break; // PSUBW
            case (2u << 6) | 0x28: SetGpr(rd, PackW(WordOp(a, b, static (x, y) => x == y ? 0xFFFFFFFFu : 0u))); break; // PCEQW
            case (2u << 6) | 0x08: SetGpr(rd, PackW(WordOp(a, b, static (x, y) => (int)x > (int)y ? 0xFFFFFFFFu : 0u))); break; // PCGTW
            case (3u << 6) | 0x08: SetGpr(rd, PackW(WordOp(a, b, static (x, y) => (int)x > (int)y ? x : y))); break; // PMAXW
            case (3u << 6) | 0x28: SetGpr(rd, PackW(WordOp(a, b, static (x, y) => (int)x < (int)y ? x : y))); break; // PMINW
            case (16u << 6) | 0x08: SetGpr(rd, PackW(WordOp(a, b, static (x, y) => (uint)SatS32((long)(int)x + (int)y)))); break; // PADDSW
            case (17u << 6) | 0x08: SetGpr(rd, PackW(WordOp(a, b, static (x, y) => (uint)SatS32((long)(int)x - (int)y)))); break; // PSUBSW
            case (16u << 6) | 0x28: SetGpr(rd, PackW(WordOp(a, b, static (x, y) => SatU32((long)x + y)))); break; // PADDUW
            case (17u << 6) | 0x28: SetGpr(rd, PackW(WordOp(a, b, static (x, y) => SatU32((long)x - y)))); break; // PSUBUW

            // ---- halfword lanes (8x16) ----
            case (4u << 6) | 0x08: SetGpr(rd, PackH(HalfOp(a, b, static (x, y) => unchecked((ushort)(x + y))))); break; // PADDH
            case (5u << 6) | 0x08: SetGpr(rd, PackH(HalfOp(a, b, static (x, y) => unchecked((ushort)(x - y))))); break; // PSUBH
            case (6u << 6) | 0x28: SetGpr(rd, PackH(HalfOp(a, b, static (x, y) => (ushort)(x == y ? 0xFFFF : 0)))); break; // PCEQH
            case (6u << 6) | 0x08: SetGpr(rd, PackH(HalfOp(a, b, static (x, y) => (ushort)((short)x > (short)y ? 0xFFFF : 0)))); break; // PCGTH
            case (7u << 6) | 0x08: SetGpr(rd, PackH(HalfOp(a, b, static (x, y) => (short)x > (short)y ? x : y))); break; // PMAXH
            case (7u << 6) | 0x28: SetGpr(rd, PackH(HalfOp(a, b, static (x, y) => (short)x < (short)y ? x : y))); break; // PMINH
            case (20u << 6) | 0x08: SetGpr(rd, PackH(HalfOp(a, b, static (x, y) => (ushort)SatS16((short)x + (short)y)))); break; // PADDSH
            case (21u << 6) | 0x08: SetGpr(rd, PackH(HalfOp(a, b, static (x, y) => (ushort)SatS16((short)x - (short)y)))); break; // PSUBSH
            case (20u << 6) | 0x28: SetGpr(rd, PackH(HalfOp(a, b, static (x, y) => SatU16(x + y)))); break; // PADDUH
            case (21u << 6) | 0x28: SetGpr(rd, PackH(HalfOp(a, b, static (x, y) => SatU16(x - y)))); break; // PSUBUH

            // ---- byte lanes (16x8) ----
            case (8u << 6) | 0x08: SetGpr(rd, PackB(ByteOp(a, b, static (x, y) => unchecked((byte)(x + y))))); break; // PADDB
            case (9u << 6) | 0x08: SetGpr(rd, PackB(ByteOp(a, b, static (x, y) => unchecked((byte)(x - y))))); break; // PSUBB
            case (10u << 6) | 0x28: SetGpr(rd, PackB(ByteOp(a, b, static (x, y) => (byte)(x == y ? 0xFF : 0)))); break; // PCEQB
            case (10u << 6) | 0x08: SetGpr(rd, PackB(ByteOp(a, b, static (x, y) => (byte)((sbyte)x > (sbyte)y ? 0xFF : 0)))); break; // PCGTB
            case (24u << 6) | 0x08: SetGpr(rd, PackB(ByteOp(a, b, static (x, y) => (byte)SatS8((sbyte)x + (sbyte)y)))); break; // PADDSB
            case (25u << 6) | 0x08: SetGpr(rd, PackB(ByteOp(a, b, static (x, y) => (byte)SatS8((sbyte)x - (sbyte)y)))); break; // PSUBSB
            case (24u << 6) | 0x28: SetGpr(rd, PackB(ByteOp(a, b, static (x, y) => SatU8(x + y)))); break; // PADDUB
            case (25u << 6) | 0x28: SetGpr(rd, PackB(ByteOp(a, b, static (x, y) => SatU8(x - y)))); break; // PSUBUB

            // ---- logical (fixes the earlier func-only dispatch, which collided with other real slots) ----
            case (18u << 6) | 0x09: SetGpr(rd, new Gpr128 { Lo = a.Lo & b.Lo, Hi = a.Hi & b.Hi }); break; // PAND
            case (18u << 6) | 0x29: SetGpr(rd, new Gpr128 { Lo = a.Lo | b.Lo, Hi = a.Hi | b.Hi }); break; // POR
            case (19u << 6) | 0x09: SetGpr(rd, new Gpr128 { Lo = a.Lo ^ b.Lo, Hi = a.Hi ^ b.Hi }); break; // PXOR
            case (19u << 6) | 0x29: SetGpr(rd, new Gpr128 { Lo = ~(a.Lo | b.Lo), Hi = ~(a.Hi | b.Hi) }); break; // PNOR

            // ---- extract-low interleave (high confidence; "U"/extract-high variants omitted, see note above) ----
            case (18u << 6) | 0x08: // PEXTLW — interleave low 32-bit lanes: a0,b0,a1,b1
            {
                var aw = ExtractW(a); var bw = ExtractW(b);
                SetGpr(rd, PackW(new[] { aw[0], bw[0], aw[1], bw[1] }));
                break;
            }
            // PPACW/PPACH/PPACB/PEXT5/PPAC5 — MMI0 pack family. Verified against PCSX2's
            // pcsx2/MMI.cpp (interpreter). God of War (SCUS_973.99) hits PPACW early in
            // CRT0 (UnknownOpcode 0x9C0004C8 = key (sa=19)<<6|func=0x08) four times by
            // cyc=5M and stalls there if unimplemented.
            case (19u << 6) | 0x08: // PPACW — pack even words: rd={rt[0],rt[2],rs[0],rs[2]}
            {
                var aw = ExtractW(a); var bw = ExtractW(b);
                SetGpr(rd, PackW(new[] { bw[0], bw[2], aw[0], aw[2] }));
                break;
            }
            case (22u << 6) | 0x08: // PEXTLH
            {
                var ah = ExtractH(a); var bh = ExtractH(b);
                SetGpr(rd, PackH(new[] { ah[0], bh[0], ah[1], bh[1], ah[2], bh[2], ah[3], bh[3] }));
                break;
            }
            case (23u << 6) | 0x08: // PPACH — pack even halfwords of each word lane
            {
                var ah = ExtractH(a); var bh = ExtractH(b);
                SetGpr(rd, PackH(new[] { bh[0], bh[2], bh[4], bh[6], ah[0], ah[2], ah[4], ah[6] }));
                break;
            }
            case (26u << 6) | 0x08: // PEXTLB
            {
                var ab = ExtractB(a); var bb = ExtractB(b);
                var r = new byte[16];
                for (int i = 0; i < 8; i++) { r[i * 2] = ab[i]; r[i * 2 + 1] = bb[i]; }
                SetGpr(rd, PackB(r));
                break;
            }
            case (27u << 6) | 0x08: // PPACB — pack even bytes of each halfword lane
            {
                var ab = ExtractB(a); var bb = ExtractB(b);
                var r = new byte[16];
                for (int i = 0; i < 8; i++) r[i] = bb[i * 2];
                for (int i = 0; i < 8; i++) r[8 + i] = ab[i * 2];
                SetGpr(rd, PackB(r));
                break;
            }
            case (30u << 6) | 0x08: // PEXT5 — expand 15-bit 5:5:5:1 color in each word of Rt
            {
                // PCSX2: ((c&0x1F)<<3)|((c&0x3E0)<<6)|((c&0x7C00)<<9)|((c&0x8000)<<16)
                var tw = ExtractW(b);
                var r = new uint[4];
                for (int i = 0; i < 4; i++)
                {
                    uint c = tw[i];
                    r[i] = ((c & 0x0000001Fu) << 3)
                         | ((c & 0x000003E0u) << 6)
                         | ((c & 0x00007C00u) << 9)
                         | ((c & 0x00008000u) << 16);
                }
                SetGpr(rd, PackW(r));
                break;
            }
            case (31u << 6) | 0x08: // PPAC5 — pack 32-bit channel words back to 15-bit 5:5:5:1
            {
                // PCSX2: ((c>>3)&0x1F)|((c>>6)&0x3E0)|((c>>9)&0x7C00)|((c>>16)&0x8000)
                var tw = ExtractW(b);
                var r = new uint[4];
                for (int i = 0; i < 4; i++)
                {
                    uint c = tw[i];
                    r[i] = ((c >> 3) & 0x0000001Fu)
                         | ((c >> 6) & 0x000003E0u)
                         | ((c >> 9) & 0x00007C00u)
                         | ((c >> 16) & 0x00008000u);
                }
                SetGpr(rd, PackW(r));
                break;
            }

            // ---- 64-bit copy-mix ----
            case (14u << 6) | 0x09: SetGpr(rd, new Gpr128 { Lo = b.Lo, Hi = a.Lo }); break; // PCPYLD
            // EXPERIMENT (2026-07-26): was Lo=b.Hi,Hi=a.Hi (mechanical "UD mirrors LD" symmetry
            // with PCPYLD above, i.e. rd.Lo=rt.Hi/rd.Hi=rs.Hi). Testing rd.Lo=rs.Hi/rd.Hi=rt.Hi
            // instead against a real, precisely-traced failure: the real library strcpy's
            // hasless-zero-detection sequence (`psubb/pnor/pand/pand` -> v0, then
            // `pcpyud a0,v0,t1` -> `or v1,v0,a0` -> `bne v1,zero,...`) needs a0.Lo to receive
            // v0.Hi (the upper-half zero-detection result) so a scalar bne (which per real R5900
            // semantics only ever examines the low 64 bits of a GPR) can see a zero byte that
            // landed in the upper half of the 16-byte chunk. With the old assignment, a0.Lo got
            // t1.Hi (raw source data) instead, which is generally not zero even when the byte
            // data legitimately contains a null terminator there — so bne never fires, the loop
            // never finds the terminator, and it walks off the end of the buffer indefinitely.
            // Confirmed via `pcpyud a0, v0, t1` (rs=v0, rt=t1): with the old mapping,
            // a0.Lo=t1.Hi=0 (irrelevant raw data) and a0.Hi=v0.Hi=0x8080808080808080 (the real
            // detection signal, stuck where bne can't see it); with this mapping,
            // a0.Lo=v0.Hi=0x8080808080808080 lands exactly where bne needs it.
            case (14u << 6) | 0x29: SetGpr(rd, new Gpr128 { Lo = a.Hi, Hi = b.Hi }); break; // PCPYUD

            // ---- MMI1 (func=0x28) remaining entries ----
            case (1u << 6) | 0x28: // PABSW — operates on Rt, per-word signed abs with 0x80000000 clamp
            {
                var tw = ExtractW(b);
                var r = new uint[4];
                for (int i = 0; i < 4; i++)
                    r[i] = tw[i] == 0x80000000u ? 0x7FFFFFFFu : (int)tw[i] < 0 ? unchecked((uint)(-(int)tw[i])) : tw[i];
                SetGpr(rd, PackW(r));
                break;
            }
            case (4u << 6) | 0x28: // PADSBH — low 4 halfwords = Rs-Rt, high 4 = Rs+Rt
            {
                var ah = ExtractH(a); var bh = ExtractH(b);
                var r = new ushort[8];
                for (int i = 0; i < 4; i++) r[i] = unchecked((ushort)(ah[i] - bh[i]));
                for (int i = 4; i < 8; i++) r[i] = unchecked((ushort)(ah[i] + bh[i]));
                SetGpr(rd, PackH(r));
                break;
            }
            case (5u << 6) | 0x28: // PABSH — operates on Rt, per-halfword signed abs with 0x8000 clamp
            {
                var th = ExtractH(b);
                var r = new ushort[8];
                for (int i = 0; i < 8; i++)
                    r[i] = th[i] == 0x8000 ? (ushort)0x7FFF : (short)th[i] < 0 ? unchecked((ushort)(-(short)th[i])) : th[i];
                SetGpr(rd, PackH(r));
                break;
            }
            case (18u << 6) | 0x28: // PEXTUW — interleave UPPER 32-bit lanes: rt[2],rs[2],rt[3],rs[3]
            {
                var aw = ExtractW(a); var bw = ExtractW(b);
                SetGpr(rd, PackW(new[] { bw[2], aw[2], bw[3], aw[3] }));
                break;
            }
            case (22u << 6) | 0x28: // PEXTUH — interleave UPPER 4 halfword lanes: rt[4..7] with rs[4..7]
            {
                var ah = ExtractH(a); var bh = ExtractH(b);
                SetGpr(rd, PackH(new[] { bh[4], ah[4], bh[5], ah[5], bh[6], ah[6], bh[7], ah[7] }));
                break;
            }
            case (26u << 6) | 0x28: // PEXTUB — interleave UPPER 8 byte lanes: rt[8..15] with rs[8..15]
            {
                var ab = ExtractB(a); var bb = ExtractB(b);
                var r = new byte[16];
                for (int i = 0; i < 8; i++) { r[i * 2] = bb[8 + i]; r[i * 2 + 1] = ab[8 + i]; }
                SetGpr(rd, PackB(r));
                break;
            }
            case (27u << 6) | 0x28: // QFSRV — 256-bit funnel shift right by _sa bits (set via MTSAB/MTSAH)
            {
                // combined = {rt (bytes 0-15), rs (bytes 16-31)} — this concatenation order (rt
                // first/low, rs second/high) and the byte-shift semantics are both verified
                // against Play!'s CodeGen test suite (MdTest.cpp's MD_Srl256 cases), not guessed
                // — see _sa's own doc comment.
                var ab = ExtractB(a); var bb = ExtractB(b);
                var combined = new byte[32];
                Array.Copy(bb, 0, combined, 0, 16);
                Array.Copy(ab, 0, combined, 16, 16);
                int byteShift = (int)(_sa >> 3);
                var r = new byte[16];
                Array.Copy(combined, byteShift, r, 0, 16);
                SetGpr(rd, PackB(r));
                break;
            }

            // ---- MMI2 (func=0x09) remaining entries ----
            case (8u << 6) | 0x09: SetGpr(rd, new Gpr128 { Lo = HI, Hi = HI1 }); break; // PMFHI — full 128-bit HI:HI1
            case (9u << 6) | 0x09: SetGpr(rd, new Gpr128 { Lo = LO, Hi = LO1 }); break; // PMFLO — full 128-bit LO:LO1
            case (10u << 6) | 0x09: // PINTH
            {
                var ah = ExtractH(a); var bh = ExtractH(b);
                SetGpr(rd, PackH(new[] { bh[0], ah[4], bh[1], ah[5], bh[2], ah[6], bh[3], ah[7] }));
                break;
            }
            case (26u << 6) | 0x09: // PEXEH — swap Rt's US[0]<->US[2] and US[4]<->US[6] within each half
            {
                var th = ExtractH(b);
                SetGpr(rd, PackH(new[] { th[2], th[1], th[0], th[3], th[6], th[5], th[4], th[7] }));
                break;
            }
            case (27u << 6) | 0x09: // PREVH — reverse Rt's halfwords within each 64-bit half
            {
                var th = ExtractH(b);
                SetGpr(rd, PackH(new[] { th[3], th[2], th[1], th[0], th[7], th[6], th[5], th[4] }));
                break;
            }
            case (30u << 6) | 0x09: // PEXEW — swap Rt's UL[0]<->UL[2]
            {
                var tw = ExtractW(b);
                SetGpr(rd, PackW(new[] { tw[2], tw[1], tw[0], tw[3] }));
                break;
            }
            case (31u << 6) | 0x09: // PROT3W — rotate Rt's lower 3 words left
            {
                var tw = ExtractW(b);
                SetGpr(rd, PackW(new[] { tw[1], tw[2], tw[0], tw[3] }));
                break;
            }

            // ---- MMI3 (func=0x29) remaining entries ----
            case (10u << 6) | 0x29: // PINTEH
            {
                var ah = ExtractH(a); var bh = ExtractH(b);
                SetGpr(rd, PackH(new[] { bh[0], ah[0], bh[2], ah[2], bh[4], ah[4], bh[6], ah[6] }));
                break;
            }
            case (26u << 6) | 0x29: // PEXCH — swap Rt's US[1]<->US[2] and US[5]<->US[6] within each half
            {
                var th = ExtractH(b);
                SetGpr(rd, PackH(new[] { th[0], th[2], th[1], th[3], th[4], th[6], th[5], th[7] }));
                break;
            }
            case (27u << 6) | 0x29: // PCPYH — broadcast Rt.US[0] to lanes 0-3, Rt.US[4] to lanes 4-7
            {
                var th = ExtractH(b);
                SetGpr(rd, PackH(new[] { th[0], th[0], th[0], th[0], th[4], th[4], th[4], th[4] }));
                break;
            }
            case (30u << 6) | 0x29: // PEXCW — swap Rt's UL[1]<->UL[2]
            {
                var tw = ExtractW(b);
                SetGpr(rd, PackW(new[] { tw[0], tw[2], tw[1], tw[3] }));
                break;
            }

            default:
                _telemetry?.UnknownOpcode(CurrentCycle(), PC, key | 0x1C000000u | 0x80000000u);
                break;
        }
    }

    private static int PlzcwLane(uint v)
    {
        uint sign = (v >> 31) & 1;
        int count = 0;
        for (int b = 30; b >= 0; b--)
        {
            if (((v >> b) & 1) != sign) break;
            count++;
        }
        return count;
    }

    private static uint[] ExtractW(Gpr128 v) => new[] { (uint)v.Lo, (uint)(v.Lo >> 32), (uint)v.Hi, (uint)(v.Hi >> 32) };
    private static Gpr128 PackW(uint[] w) => new() { Lo = w[0] | ((ulong)w[1] << 32), Hi = w[2] | ((ulong)w[3] << 32) };

    private static ushort[] ExtractH(Gpr128 v)
    {
        var h = new ushort[8];
        for (int i = 0; i < 4; i++) h[i] = (ushort)(v.Lo >> (i * 16));
        for (int i = 0; i < 4; i++) h[4 + i] = (ushort)(v.Hi >> (i * 16));
        return h;
    }
    private static Gpr128 PackH(ushort[] h)
    {
        ulong lo = 0, hi = 0;
        for (int i = 0; i < 4; i++) lo |= (ulong)h[i] << (i * 16);
        for (int i = 0; i < 4; i++) hi |= (ulong)h[4 + i] << (i * 16);
        return new Gpr128 { Lo = lo, Hi = hi };
    }

    private static byte[] ExtractB(Gpr128 v)
    {
        var b = new byte[16];
        for (int i = 0; i < 8; i++) b[i] = (byte)(v.Lo >> (i * 8));
        for (int i = 0; i < 8; i++) b[8 + i] = (byte)(v.Hi >> (i * 8));
        return b;
    }
    private static Gpr128 PackB(byte[] b)
    {
        ulong lo = 0, hi = 0;
        for (int i = 0; i < 8; i++) lo |= (ulong)b[i] << (i * 8);
        for (int i = 0; i < 8; i++) hi |= (ulong)b[8 + i] << (i * 8);
        return new Gpr128 { Lo = lo, Hi = hi };
    }

    private static uint[] WordOp(Gpr128 a, Gpr128 b, Func<uint, uint, uint> op)
    {
        var aw = ExtractW(a); var bw = ExtractW(b);
        var r = new uint[4];
        for (int i = 0; i < 4; i++) r[i] = op(aw[i], bw[i]);
        return r;
    }
    private static ushort[] HalfOp(Gpr128 a, Gpr128 b, Func<ushort, ushort, ushort> op)
    {
        var ah = ExtractH(a); var bh = ExtractH(b);
        var r = new ushort[8];
        for (int i = 0; i < 8; i++) r[i] = op(ah[i], bh[i]);
        return r;
    }
    private static byte[] ByteOp(Gpr128 a, Gpr128 b, Func<byte, byte, byte> op)
    {
        var ab = ExtractB(a); var bb = ExtractB(b);
        var r = new byte[16];
        for (int i = 0; i < 16; i++) r[i] = op(ab[i], bb[i]);
        return r;
    }

    private static int SatS32(long v) => v > int.MaxValue ? int.MaxValue : v < int.MinValue ? int.MinValue : (int)v;
    private static uint SatU32(long v) => v > uint.MaxValue ? uint.MaxValue : v < 0 ? 0u : (uint)v;
    private static short SatS16(int v) => v > short.MaxValue ? short.MaxValue : v < short.MinValue ? short.MinValue : (short)v;
    private static ushort SatU16(int v) => v > ushort.MaxValue ? ushort.MaxValue : v < 0 ? (ushort)0 : (ushort)v;
    private static sbyte SatS8(int v) => v > sbyte.MaxValue ? sbyte.MaxValue : v < sbyte.MinValue ? sbyte.MinValue : (sbyte)v;
    private static byte SatU8(int v) => v > byte.MaxValue ? byte.MaxValue : v < 0 ? (byte)0 : (byte)v;

    private void ExecuteLq(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F, rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = (GetGpr(rs).Lo + (ulong)(int)off) & ~0xFUL;
        if (rt == 0) return;
        ulong lo = _memory.Read32(addr) | ((ulong)_memory.Read32(addr + 4) << 32);
        ulong hi = _memory.Read32(addr + 8) | ((ulong)_memory.Read32(addr + 12) << 32);
        SetGpr(rt, new Gpr128 { Lo = lo, Hi = hi });
    }

    private void ExecuteSq(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F, rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = (GetGpr(rs).Lo + (ulong)(int)off) & ~0xFUL;
        var v = GetGpr(rt);
        _memory.Write32(addr, (uint)v.Lo);
        _memory.Write32(addr + 4, (uint)(v.Lo >> 32));
        _memory.Write32(addr + 8, (uint)v.Hi);
        _memory.Write32(addr + 12, (uint)(v.Hi >> 32));
    }

    private void ExecuteLqc2(uint opcode)
    {
        if (_vu0 == null) return;
        uint rs = (opcode >> 21) & 0x1F, rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)(int)off;
        float x = BitConverter.Int32BitsToSingle((int)_memory.Read32(addr));
        float y = BitConverter.Int32BitsToSingle((int)_memory.Read32(addr + 4));
        float z = BitConverter.Int32BitsToSingle((int)_memory.Read32(addr + 8));
        float w = BitConverter.Int32BitsToSingle((int)_memory.Read32(addr + 12));
        _vu0.SetVfRegister(rt, new VectorUnit.VuReg128 { X = x, Y = y, Z = z, W = w });
    }

    private void ExecuteSqc2(uint opcode)
    {
        if (_vu0 == null) return;
        uint rs = (opcode >> 21) & 0x1F, rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)(int)off;
        var r = _vu0.GetVfRegister(rt);
        _memory.Write32(addr, (uint)BitConverter.SingleToInt32Bits(r.X));
        _memory.Write32(addr + 4, (uint)BitConverter.SingleToInt32Bits(r.Y));
        _memory.Write32(addr + 8, (uint)BitConverter.SingleToInt32Bits(r.Z));
        _memory.Write32(addr + 12, (uint)BitConverter.SingleToInt32Bits(r.W));
    }

    private bool ExecuteBeqLikely(uint opcode)
    {
        _branchWasLikely = true;
        return ExecuteBeq(opcode);
    }
    private bool ExecuteBneLikely(uint opcode)
    {
        _branchWasLikely = true;
        return ExecuteBne(opcode);
    }
    private bool ExecuteBlezLikely(uint opcode)
    {
        _branchWasLikely = true;
        return ExecuteBlez(opcode);
    }
    private bool ExecuteBgtzLikely(uint opcode)
    {
        _branchWasLikely = true;
        return ExecuteBgtz(opcode);
    }

    private bool ExecuteCop1(uint opcode)
    {
        uint fmt = (opcode >> 21) & 0x1F;
        uint ft = (opcode >> 16) & 0x1F;
        uint fs = (opcode >> 11) & 0x1F;
        uint fd = (opcode >> 6) & 0x1F;
        uint func = opcode & 0x3F;

        switch (fmt)
        {
            case 0x00: // MFC1
                // MIPS64 sign-extends the 32-bit FPU value's bit pattern into the 64-bit GPR.
                // Every negative float has IEEE754 bit 31 (the sign bit) set, so this was
                // zero-extending in a genuinely common case, not an edge case — same bug class
                // as LUI/LW/ADD/SLL/MFC0 (see their comments).
                if (ft != 0)
                    SetGpr(ft, new Gpr128 { Lo = unchecked((ulong)(long)(int)BitConverter.SingleToUInt32Bits(_fpr[fs])) });
                break;
            case 0x04: // MTC1
                _fpr[fs] = DeterministicFloat.Canonicalize(BitConverter.UInt32BitsToSingle((uint)GetGpr(ft).Lo));
                break;
            case 0x02: // CFC1
                if (ft != 0)
                    SetGpr(ft, new Gpr128 { Lo = fs == 31 ? _fcr31 : 0 });
                break;
            case 0x06: // CTC1
                if (fs == 31) _fcr31 = (uint)GetGpr(ft).Lo;
                break;
            case 0x08: // BC1F/BC1T/BC1FL/BC1TL — FPU condition branch
                return ExecuteBc1(opcode);
            case 0x14: // W (word) format — source operand is an int stored in the FPR
                if (func == 0x20) // CVT.S.W
                {
                    int iv = (int)BitConverter.SingleToUInt32Bits(_fpr[fs]);
                    _fpr[fd] = DeterministicFloat.Canonicalize((float)iv);
                }
                break;
            case 0x10: // S (single)
                switch (func)
                {
                    case 0x00: _fpr[fd] = DeterministicFloat.Canonicalize(_fpr[fs] + _fpr[ft]); break; // ADD.S
                    case 0x01: _fpr[fd] = DeterministicFloat.Canonicalize(_fpr[fs] - _fpr[ft]); break; // SUB.S
                    case 0x02: _fpr[fd] = DeterministicFloat.Canonicalize(_fpr[fs] * _fpr[ft]); break; // MUL.S
                    case 0x03: _fpr[fd] = _fpr[ft] != 0 ? DeterministicFloat.Canonicalize(_fpr[fs] / _fpr[ft]) : DeterministicFloat.FromBits(0x7F800000); break;
                    case 0x06: _fpr[fd] = DeterministicFloat.Canonicalize(_fpr[fs]); break; // MOV.S
                    case 0x07: _fpr[fd] = DeterministicFloat.Canonicalize(-_fpr[fs]); break; // NEG.S
                    case 0x05: _fpr[fd] = DeterministicFloat.Canonicalize(MathF.Abs(_fpr[fs])); break; // ABS.S
                    case 0x04: _fpr[fd] = DeterministicFloat.Canonicalize(MathF.Sqrt(MathF.Max(0, _fpr[fs]))); break; // SQRT.S
                    case 0x24: // CVT.W.S — result is an int, stored raw (not canonicalized as float)
                        {
                            float f = _fpr[fs];
                            int iv = float.IsNaN(f) ? int.MaxValue
                                : f >= 2147483647f ? int.MaxValue
                                : f <= -2147483648f ? int.MinValue
                                : (int)f; // C# cast truncates toward zero, matching the common MIPS RM default
                            _fpr[fd] = BitConverter.UInt32BitsToSingle((uint)iv);
                        }
                        break;
                    default:
                        // C.cond.S comparisons (func 0x30-0x3F): low 3 bits select the IEEE
                        // predicate; bit 3 (quiet vs signaling NaN exception) is irrelevant
                        // here since we don't model FPU exception trapping.
                        if ((func & 0x30) == 0x30)
                            ExecuteCondS(func, _fpr[fs], _fpr[ft]);
                        break;
                }
                break;
            default:
                break;
        }
        return false;
    }

    private bool ExecuteBc1(uint opcode)
    {
        uint rt = (opcode >> 16) & 0x1F;
        bool tf = (rt & 1) != 0;     // 0=BC1F, 1=BC1T
        bool likely = (rt & 2) != 0; // BC1FL/BC1TL nullify delay slot if not taken
        short off = (short)(opcode & 0xFFFF);
        bool cond = (_fcr31 & (1u << 23)) != 0;
        if (likely) _branchWasLikely = true;
        if (cond == tf)
        {
            _delaySlotTarget = PC + 4 + (ulong)((int)off << 2);
            return true;
        }
        return false;
    }

    private void ExecuteCondS(uint func, float a, float b)
    {
        bool unordered = float.IsNaN(a) || float.IsNaN(b);
        bool cond = (func & 0x7) switch
        {
            0x0 => false,                            // F
            0x1 => unordered,                         // UN
            0x2 => !unordered && a == b,               // EQ
            0x3 => unordered || a == b,                // UEQ
            0x4 => !unordered && a < b,                 // OLT
            0x5 => unordered || a < b,                  // ULT
            0x6 => !unordered && a <= b,                // OLE
            0x7 => unordered || a <= b,                 // ULE
            _ => false
        };
        if (cond) _fcr31 |= 1u << 23; else _fcr31 &= ~(1u << 23);
    }

    private void HandleSyscall(uint opcode)
    {
        if (_preferHleSyscalls && _hle != null)
        {
            _hle.HandleSyscall(this);
            return;
        }
        // Architectural SYSCALL exception (ExcCode 8)
        EnterException(GetExceptionVector(general: true), causeExcCode: 8);
    }

    private void HandleBreak(uint opcode)
    {
        if (_preferHleSyscalls)
            return; // treat as nop under HLE-friendly mode
        EnterException(GetExceptionVector(general: true), causeExcCode: 9);
    }

    private bool ExecuteCop0(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        uint rd = (opcode >> 11) & 0x1F;
        uint function = opcode & 0x3F;

        switch (rs)
        {
            case 0x00: // MFC0
                // MIPS64 sign-extends the 32-bit COP0 value into the 64-bit GPR — matters a
                // lot in practice since KSEG0/KSEG1 addresses (0x80000000+, i.e. essentially
                // all kernel/BIOS code and every exception vector) have bit 31 set, so reading
                // EPC/BadVAddr etc. after an exception hit this constantly. Same bug class as
                // LUI/LW/ADD/SLL (see their comments) — the old code zero-extended instead.
                if (rt != 0)
                    SetGpr(rt, new Gpr128 { Lo = unchecked((ulong)(long)(int)ReadCop0((int)rd)) });
                break;
            case 0x04: // MTC0
                WriteCop0((int)rd, (uint)GetGpr(rt).Lo);
                break;
            case 0x10: // CO
                if (function == 0x18) // ERET
                    ExecuteEret();
                else if (function == 0x38) // EI — set EIE (Status bit 16)
                    COP0_Status |= 1u << 16;
                else if (function == 0x39) // DI — clear EIE (Status bit 16)
                    COP0_Status &= ~(1u << 16);
                break;
            default:
                break;
        }
        return false;
    }

    private void ExecuteEret()
    {
        // No delay slot; Step() always PC+=4 after non-branch → preload target-4.
        //
        // Two distinct hardware uses of eret (R5900 COP0):
        //   1. ERL path — Status.ERL set: clear ERL, PC = ErrorEPC. Games use this as a
        //      software "return-with-Status-change" for DI/critical sections that write
        //      uncached hardware (e.g. God of War helper at 0x00299820: mtc0 Status with
        //      ERL, sw to 0xB0001xxx, mtc0 ra ErrorEPC, eret). This is NOT an interrupt
        //      return and must not touch the HLE INTC GPR save stack.
        //   2. EXL path — normal exception/interrupt return: clear EXL, PC = EPC. Our
        //      TryDispatchRegisteredIntcHandler snapshots user GPRs before redirecting into
        //      a registered handler; only THIS path restores them (handler jr ra → vector
        //      eret). Popping on the ERL path while eretStack>0 (handler mid-flight called
        //      an ERL critical section) restored user GPRs into the ISR, zeroed ErrorEPC
        //      via corrupted ra, then wedged forever at the eret itself (PC=EPC=eret).
        //      Traced God of War SCUS_973.99 @ cyc≈17.05M, 2026-07-30.
        ulong target;
        bool erlPath = (COP0_Status & 0x4) != 0;
        if (erlPath)
        {
            COP0_Status &= ~0x4u;
            target = _cop0ErrorEpc;
        }
        else
        {
            COP0_Status &= ~0x2u;
            target = COP0_EPC;
        }
        InterruptPending = false;
        EretCount++;
        PC = target - 4;
        // Restore HLE interrupt-dispatch GPRs only on the real exception-return (EXL) path.
        if (!erlPath && _savedGprAcrossIntcDispatch.Count > 0)
        {
            ulong[] savedGpr = _savedGprAcrossIntcDispatch.Pop();
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_INTC_DISPATCH") == "1")
                Console.Error.WriteLine($"[ERET-POP] cyc={CurrentCycle()} poppedRa=0x{savedGpr[31]:X8} poppedV0=0x{savedGpr[2]:X8} stackDepthAfterPop={_savedGprAcrossIntcDispatch.Count} newPc=0x{PC:X8}");
            for (int i = 1; i < 32; i++) // skip $zero
                SetGpr(i, new Gpr128 { Lo = savedGpr[i] });
            // User GPRs are live in the EE again; drop the thread's interrupt snapshot so a
            // later SwitchToNext cannot resurrect this (now stale) full save.
            _hle?.Kernel.ClearFullSaveIfCurrent();
        }
        // Re-evaluate COP0 delivery when a latched source is still waiting (multi-handler
        // AddIntcHandler chain re-Raise, multi-channel DMAC re-Raise). eret forced
        // InterruptPending=false above; without a sync the next handler waits up to 64
        // instructions. Only sync when something is actually latched — unconditional sync
        // densified SIF storms during the STAT hold window.
        if (!erlPath && _intc != null && _intc.AnyPending)
            SyncInterruptsFromIntc();
    }

    private void ExecuteLd(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F, rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)(int)off;
        if (rt == 0) return;
        uint lo = _memory.Read32(addr);
        uint hi = _memory.Read32(addr + 4);
        SetGpr(rt, new Gpr128 { Lo = lo | ((ulong)hi << 32), Hi = 0 });
    }

    private void ExecuteSd(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F, rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)(int)off;
        ulong v = GetGpr(rt).Lo;
        _memory.Write32(addr, (uint)v);
        _memory.Write32(addr + 4, (uint)(v >> 32));
    }

    /// <summary>LWU — load word unsigned into 64-bit GPR (zero-extend).</summary>
    private void ExecuteLwu(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F, rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)(int)off;
        if (rt == 0) return;
        uint w = _memory.Read32(addr);
        SetGpr(rt, new Gpr128 { Lo = w, Hi = 0 });
    }

    // Real unaligned load/store (word and doubleword "left"/"right" pairs). These were
    // previously aliased straight to the full aligned LW/SW/LD/SD ("behave like aligned for
    // now") -- silently WRONG for any unaligned address, since e.g. `sdl v1,15(sp)` would
    // perform a full 8-byte store starting AT offset 15 instead of a partial store confined to
    // its own aligned word, happily stomping whatever sits at offset 16-22 (a live saved `ra`
    // slot, another local, anything). Root-caused via Mortal Kombat: Shaolin Monks
    // (SLUS_210.87): a `ldl/ldr` + `sdl/sdr` unaligned-copy idiom at 0x0024C828-0x0024C848
    // (immediately followed by `ld ra,16(sp)`) was corrupting its own function's saved return
    // address, causing a masked `jr ra` (see EE.TraceJrGuard/DETPS2_TRACE_JRGUARD) to silently
    // fall through into a wholly unrelated function with garbage argument registers -- the
    // apparent "uninitialized voice pointer" a much earlier investigation chased for hours was
    // actually this: real, correct game code executing with corrupted inputs because ITS OWN
    // caller's return address had already been destroyed one level up. See
    // docs/DEVELOPER_GUIDE.md's "cyc~96.2M crash" investigation for the full trace.
    //
    // Real MIPS little-endian LWL/LWR/SWL/SWR/LDL/LDR/SDL/SDR semantics (per the MIPS64 ISA
    // manual's ReverseEndian-adjusted pseudocode, verified here against the standard paired-use
    // identity `Xxl rt,(N-1)(base); Xxr rt,0(base)` == a full unaligned N-byte access at `base`,
    // for base at every alignment 0..N-1): let `b = (vAddr & (N-1)) XOR (N-1)` (the byte-lane
    // adjustment big-endian-terminology "left/right" needs on a little-endian target) and
    // `alignedAddr = vAddr & ~(N-1)`. LWL/SWL affect the TOP `(N-b)` bytes of the register
    // (indices b..N-1), sourced from/written to `mem[alignedAddr + (j-b)]` for each byte index
    // j in that range. LWR/SWR affect the BOTTOM `(b+1)` bytes (indices 0..b), at
    // `mem[alignedAddr + (j+N-1-b)]`. Implemented as explicit byte loops rather than a
    // shift+mask formula -- slower, but eliminates all shift-count edge-case risk (e.g. C#
    // masking a shift-by-32 down to shift-by-0) for an instruction family that's cheap to get
    // subtly wrong and expensive to debug once it is.
    private void ExecuteLwl(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F, rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong vAddr = GetGpr(rs).Lo + (ulong)(int)off;
        ulong alignedAddr = vAddr & ~3UL;
        int b = (int)(vAddr & 3) ^ 3;
        uint word = rt != 0 ? (uint)GetGpr(rt).Lo : 0;
        for (int j = b; j <= 3; j++)
        {
            uint val = _memory.Read8(alignedAddr + (ulong)(j - b));
            word = (word & ~(0xFFu << (8 * j))) | (val << (8 * j));
        }
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = unchecked((ulong)(long)(int)word) });
    }

    private void ExecuteLwr(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F, rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong vAddr = GetGpr(rs).Lo + (ulong)(int)off;
        ulong alignedAddr = vAddr & ~3UL;
        int b = (int)(vAddr & 3) ^ 3;
        uint word = rt != 0 ? (uint)GetGpr(rt).Lo : 0;
        for (int j = 0; j <= b; j++)
        {
            uint val = _memory.Read8(alignedAddr + (ulong)(j + 3 - b));
            word = (word & ~(0xFFu << (8 * j))) | (val << (8 * j));
        }
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = unchecked((ulong)(long)(int)word) });
    }

    private void ExecuteSwl(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F, rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong vAddr = GetGpr(rs).Lo + (ulong)(int)off;
        ulong alignedAddr = vAddr & ~3UL;
        int b = (int)(vAddr & 3) ^ 3;
        uint word = (uint)GetGpr(rt).Lo;
        for (int j = b; j <= 3; j++)
            _memory.Write8(alignedAddr + (ulong)(j - b), (byte)(word >> (8 * j)));
    }

    private void ExecuteSwr(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F, rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong vAddr = GetGpr(rs).Lo + (ulong)(int)off;
        ulong alignedAddr = vAddr & ~3UL;
        int b = (int)(vAddr & 3) ^ 3;
        uint word = (uint)GetGpr(rt).Lo;
        for (int j = 0; j <= b; j++)
            _memory.Write8(alignedAddr + (ulong)(j + 3 - b), (byte)(word >> (8 * j)));
    }

    private void ExecuteLdl(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F, rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong vAddr = GetGpr(rs).Lo + (ulong)(int)off;
        ulong alignedAddr = vAddr & ~7UL;
        int b = (int)(vAddr & 7) ^ 7;
        ulong dw = rt != 0 ? GetGpr(rt).Lo : 0;
        for (int j = b; j <= 7; j++)
        {
            ulong val = _memory.Read8(alignedAddr + (ulong)(j - b));
            dw = (dw & ~(0xFFUL << (8 * j))) | (val << (8 * j));
        }
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = dw });
    }

    private void ExecuteLdr(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F, rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong vAddr = GetGpr(rs).Lo + (ulong)(int)off;
        ulong alignedAddr = vAddr & ~7UL;
        int b = (int)(vAddr & 7) ^ 7;
        ulong dw = rt != 0 ? GetGpr(rt).Lo : 0;
        for (int j = 0; j <= b; j++)
        {
            ulong val = _memory.Read8(alignedAddr + (ulong)(j + 7 - b));
            dw = (dw & ~(0xFFUL << (8 * j))) | (val << (8 * j));
        }
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = dw });
    }

    private void ExecuteSdl(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F, rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong vAddr = GetGpr(rs).Lo + (ulong)(int)off;
        ulong alignedAddr = vAddr & ~7UL;
        int b = (int)(vAddr & 7) ^ 7;
        ulong dw = GetGpr(rt).Lo;
        for (int j = b; j <= 7; j++)
            _memory.Write8(alignedAddr + (ulong)(j - b), (byte)(dw >> (8 * j)));
    }

    private void ExecuteSdr(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F, rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong vAddr = GetGpr(rs).Lo + (ulong)(int)off;
        ulong alignedAddr = vAddr & ~7UL;
        int b = (int)(vAddr & 7) ^ 7;
        ulong dw = GetGpr(rt).Lo;
        for (int j = 0; j <= b; j++)
            _memory.Write8(alignedAddr + (ulong)(j + 7 - b), (byte)(dw >> (8 * j)));
    }
}