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

    /// <summary>Diagnostic-only: logs v0/v1/ra to stderr every time PC hits this address.
    /// Opt-in via blocker-trace --pcbreak=ADDR; null (default) costs one branch per Step().</summary>
    public static uint? PcBreakGpr;

    /// <summary>MMI "pipeline 1" HI/LO — real R5900 HI/LO are 128-bit registers; regular
    /// MULT/DIV/MADD use the lower 64 bits (HI/LO above), MULT1/DIV1/MADD1/MFHI1/MTHI1/
    /// MFLO1/MTLO1 use this independent upper-64-bit lane.</summary>
    public ulong LO1 { get; set; }
    public ulong HI1 { get; set; }

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
    /// invisible to any code that wasn't relying on the synthesized dispatch's own return path.</summary>
    private readonly Stack<ulong> _savedRaAcrossIntcDispatch = new();
    /// <summary>Phase 25: when true and branch not taken, skip delay slot (likely branches).</summary>
    private bool _nullifyDelayIfNotTaken;
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
        _nullifyDelayIfNotTaken = false;
        _branchWasLikely = false;
        Array.Clear(_fpr);
        _fcr31 = 0;
    }

    public float GetFpr(int i) => _fpr[i & 31];
    public void SetFpr(int i, float v) => _fpr[i & 31] = DeterministicFloat.Canonicalize(v);

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
        bool blocked = (COP0_Status & 0x6) != 0; // EXL | ERL
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
            case Cop0Status: COP0_Status = value; break;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetGpr(int index, Gpr128 value)
    {
        int reg = index & 0x1F;
        if (reg != 0)
            _gprs[reg] = value;
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

            if ((executed & 0x3F) == 0)
                SyncInterruptsFromIntc();

            // VBlank HLE wait (Phase 14): stall EE while kernel waits for VBlank
            if (_hle != null && _hle.Kernel.WaitingVblank)
            {
                executed++;
                continue;
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
                if (!TryDispatchRegisteredIntcHandler())
                    EnterException(GetExceptionVector(general: true), causeExcCode: 0); // Int
                executed++;
                continue;
            }

            if (_hle != null && _hle.ExitRequested)
                break;

            ulong cyc = _cycleSource?.Invoke() ?? 0;
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

            uint opcode = _memory.Read32(PC);
            _tracer?.LogInstruction(cyc, PC, opcode);
            if (SystemMemory.WatchAddr.HasValue) SystemMemory.CurrentPcForWatch = (ulong)PC;
            if (PcBreakGpr.HasValue && PC == PcBreakGpr.Value)
                Console.Error.WriteLine($"[PCBREAK] pc=0x{PC:X8} v0=0x{GetGpr(2).Lo:X} v1=0x{GetGpr(3).Lo:X} a0=0x{GetGpr(4).Lo:X} a1=0x{GetGpr(5).Lo:X} a2=0x{GetGpr(6).Lo:X} " +
                    $"s0=0x{GetGpr(16).Lo:X} s1=0x{GetGpr(17).Lo:X} s2=0x{GetGpr(18).Lo:X} s3=0x{GetGpr(19).Lo:X} sp=0x{GetGpr(29).Lo:X} ra=0x{GetGpr(31).Lo:X} " +
                    $"COP0_Status=0x{COP0_Status:X8} COP0_Cause=0x{COP0_Cause:X8} InterruptPending={InterruptPending} takeExceptions={_takeExceptions} cyc={cyc}");
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
        COP0_EPC = PC;
        COP0_Cause = (COP0_Cause & ~0x7Cu) | ((causeExcCode & 0x1F) << 2);
        if (_inDelaySlot) COP0_Cause |= 1u << 31;
        else COP0_Cause &= ~(1u << 31);
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

            bool found = sony.TryGetIntcHandler(src, out uint handlerAddr) && handlerAddr != 0;
            int handlerArg = src;

            // Real hardware routes SIF0 DMA-channel completion (our INTC "Sif" summary bit)
            // to whatever the game registered via AddDmacHandler(DMA_CHANNEL_SIF0=5, ...) —
            // e.g. ps2sdk's sceSifInitCmd installs _SifCmdIntHandler this way — not through
            // AddIntcHandler. Fall back to the DMAC table for that channel when there's no
            // direct INTC handler for this source.
            if (!found && src == (int)Intc.InterruptSource.Sif &&
                sony.TryGetDmacHandler(5, out uint dmacHandlerAddr) && dmacHandlerAddr != 0)
            {
                handlerAddr = dmacHandlerAddr;
                handlerArg = 5; // DMA_CHANNEL_SIF0
                found = true;
            }

            if (!found) continue;

            EnterException(GetExceptionVector(general: true), causeExcCode: 0);
            PC = handlerAddr;
            SetGpr(4, new Gpr128 { Lo = (ulong)(uint)handlerArg }); // a0 = cause
            _savedRaAcrossIntcDispatch.Push(GetGpr(31).Lo);
            SetGpr(31, new Gpr128 { Lo = KernelBootstrap.Kseg0Interrupt }); // ra = vector's eret
            return true;
        }

        // No game/kernel-registered handler owns any currently pending source. Real BIOS
        // always acknowledges INTC internally (e.g. a default OS-tick handler) even before
        // user code installs its own handler; our synthesized vector is a bare eret with no
        // such bookkeeping, so leaving these unacked would re-raise the same interrupt on
        // the very next instruction fetch forever, pinning PC in place. Ack them exactly
        // like a minimal no-op default ISR would — but NOT VBlankStart: Pcrtc.Step()
        // deliberately leaves that bit sticky ("games poll/ACK via INTC_STAT write-1-clear")
        // specifically so code can busy-poll INTC_STAT directly with COP0 interrupts masked
        // off, exactly as real hardware requires; auto-acking it here on behalf of some
        // unrelated co-pending source would steal the event out from under that poll.
        if (pending != 0)
            for (int src = 0; src < 15; src++)
                if ((pending & (1u << src)) != 0 && src != (int)Intc.InterruptSource.VBlankStart)
                    _intc.Acknowledge((Intc.InterruptSource)src);
        return false;
    }

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
            case 0x00: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rt).Lo << (int)sa }); break;
            case 0x01: break; // MOVCI / reserved — nop (seen in retail data-as-code fallthrough)
            case 0x02: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rt).Lo >> (int)sa }); break;
            case 0x03: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = (ulong)((long)GetGpr(rt).Lo >> (int)sa) }); break;

            case 0x04: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rt).Lo << (int)(GetGpr(rs).Lo & 0x1F) }); break;
            case 0x06: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rt).Lo >> (int)(GetGpr(rs).Lo & 0x1F) }); break;
            case 0x07: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = (ulong)((long)GetGpr(rt).Lo >> (int)(GetGpr(rs).Lo & 0x1F)) }); break;

            case 0x08: // JR — ignore jumps into low/vector page (uninitialized fptrs)
                {
                    ulong t = GetGpr(rs).Lo;
                    // Guard entire low 64KB (vectors + trap + recovery), not just 4KB
                    if ((t & 0x1FFFFFFFUL) < 0x10000UL)
                        break; // nop: stay sequential
                    _delaySlotTarget = t;
                    return true;
                }
            case 0x09: // JALR
                {
                    ulong t = GetGpr(rs).Lo;
                    if (rd != 0) SetGpr(rd, new Gpr128 { Lo = PC + 8 });
                    if ((t & 0x1FFFFFFFUL) < 0x10000UL)
                        break;
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

            case 0x18: // MULT (signed 32)
                {
                    int a = (int)(uint)GetGpr(rs).Lo; int b = (int)(uint)GetGpr(rt).Lo;
                    long res = (long)a * b;
                    LO = (ulong)(uint)res;
                    HI = (ulong)(uint)(res >> 32);
                }
                break;
            case 0x19: // MULTU (unsigned 32) — Phase 20 accuracy
                {
                    ulong a = GetGpr(rs).Lo & 0xFFFFFFFFUL;
                    ulong b = GetGpr(rt).Lo & 0xFFFFFFFFUL;
                    ulong res = a * b;
                    LO = res & 0xFFFFFFFFUL;
                    HI = res >> 32;
                }
                break;

            case 0x1A: // DIV (signed)
                {
                    int a = (int)(uint)GetGpr(rs).Lo; int b = (int)(uint)GetGpr(rt).Lo;
                    if (b != 0) { LO = (ulong)(uint)(a / b); HI = (ulong)(uint)(a % b); }
                }
                break;
            case 0x1B: // DIVU (unsigned) — Phase 20 accuracy
                {
                    uint a = (uint)GetGpr(rs).Lo; uint b = (uint)GetGpr(rt).Lo;
                    if (b != 0) { LO = a / b; HI = a % b; }
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

            case 0x20: case 0x21: // ADD / ADDU (32-bit; HLE ignores overflow trap on ADD)
            case 0x2C: // DADD (64-bit) — was UnknownSpecial:0x2C storm on retail titles
            case 0x2D: // DADDU
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rs).Lo + GetGpr(rt).Lo });
                break;
            case 0x22: case 0x23: // SUB / SUBU
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
            // Distance in 64-bit space
            ulong dist = a > b ? a - b : b - a;
            // Software delay loops (e.g. Midway spin counting to -1) burn tens of millions
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
        uint rs = (opcode >> 21) & 0x1F; uint rt = (opcode >> 16) & 0x1F;
        short imm = (short)(opcode & 0xFFFF);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = GetGpr(rs).Lo + (ulong)imm });
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
        uint rt = (opcode >> 16) & 0x1F; ushort imm = (ushort)(opcode & 0xFFFF);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = (ulong)imm << 16 });
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
                LO = (ulong)(uint)temp;
                HI = (ulong)(uint)(temp >> 32);
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = LO });
                break;
            }
            case 0x01: // MADDU
            {
                ulong acc = (uint)LO | ((ulong)(uint)HI << 32);
                ulong tempu = unchecked(acc + (ulong)(uint)GetGpr(rs).Lo * (uint)GetGpr(rt).Lo);
                LO = (ulong)(uint)tempu;
                HI = (ulong)(uint)(tempu >> 32);
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
                LO1 = (ulong)(uint)res;
                HI1 = (ulong)(uint)(res >> 32);
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = LO1 });
                break;
            }
            case 0x19: // MULTU1
            {
                ulong res = (ulong)(uint)GetGpr(rs).Lo * (uint)GetGpr(rt).Lo;
                LO1 = (ulong)(uint)res;
                HI1 = (ulong)(uint)(res >> 32);
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = LO1 });
                break;
            }
            case 0x1A: // DIV1
            {
                int a = (int)(uint)GetGpr(rs).Lo, b = (int)(uint)GetGpr(rt).Lo;
                if ((uint)a == 0x80000000u && b == -1) { LO1 = unchecked((ulong)(uint)0x80000000u); HI1 = 0; }
                else if (b != 0) { LO1 = (ulong)(uint)(a / b); HI1 = (ulong)(uint)(a % b); }
                else { LO1 = (ulong)(uint)(a < 0 ? 1 : -1); HI1 = (ulong)(uint)a; }
                break;
            }
            case 0x1B: // DIVU1
            {
                uint a = (uint)GetGpr(rs).Lo, b = (uint)GetGpr(rt).Lo;
                if (b != 0) { LO1 = (ulong)(uint)(a / b); HI1 = (ulong)(uint)(a % b); }
                else { LO1 = unchecked((ulong)(uint)(-1)); HI1 = (ulong)(uint)(int)a; }
                break;
            }
            case 0x20: // MADD1
            {
                long acc = unchecked((long)((uint)LO1 | ((ulong)(uint)HI1 << 32)));
                long temp = acc + (long)(int)GetGpr(rs).Lo * (int)GetGpr(rt).Lo;
                LO1 = (ulong)(uint)temp;
                HI1 = (ulong)(uint)(temp >> 32);
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = LO1 });
                break;
            }
            case 0x21: // MADDU1
            {
                ulong acc = (uint)LO1 | ((ulong)(uint)HI1 << 32);
                ulong tempu = unchecked(acc + (ulong)(uint)GetGpr(rs).Lo * (uint)GetGpr(rt).Lo);
                LO1 = (ulong)(uint)tempu;
                HI1 = (ulong)(uint)(tempu >> 32);
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
            case (22u << 6) | 0x08: // PEXTLH
            {
                var ah = ExtractH(a); var bh = ExtractH(b);
                SetGpr(rd, PackH(new[] { ah[0], bh[0], ah[1], bh[1], ah[2], bh[2], ah[3], bh[3] }));
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

            // ---- 64-bit copy-mix ----
            case (14u << 6) | 0x09: SetGpr(rd, new Gpr128 { Lo = b.Lo, Hi = a.Lo }); break; // PCPYLD
            case (14u << 6) | 0x29: SetGpr(rd, new Gpr128 { Lo = b.Hi, Hi = a.Hi }); break; // PCPYUD

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
                if (ft != 0)
                    SetGpr(ft, new Gpr128 { Lo = BitConverter.SingleToUInt32Bits(_fpr[fs]) });
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
                if (rt != 0)
                    SetGpr(rt, new Gpr128 { Lo = ReadCop0((int)rd) });
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
        ulong target;
        if ((COP0_Status & 0x4) != 0)
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
        if (_savedRaAcrossIntcDispatch.Count > 0)
            SetGpr(31, new Gpr128 { Lo = _savedRaAcrossIntcDispatch.Pop() });
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

    // Unaligned load/store simplified: behave like aligned LW/SW/LD/SD for now
    // (LDL/LDR added after real-boot telemetry showed R5900-compiled retail code hits them)
    private void ExecuteLwl(uint opcode) => ExecuteLw(opcode);
    private void ExecuteLwr(uint opcode) => ExecuteLw(opcode);
    private void ExecuteSwl(uint opcode) => ExecuteSw(opcode);
    private void ExecuteSwr(uint opcode) => ExecuteSw(opcode);
    private void ExecuteSdl(uint opcode) => ExecuteSd(opcode);
    private void ExecuteSdr(uint opcode) => ExecuteSd(opcode);
    private void ExecuteLdl(uint opcode) => ExecuteLd(opcode);
    private void ExecuteLdr(uint opcode) => ExecuteLd(opcode);
}