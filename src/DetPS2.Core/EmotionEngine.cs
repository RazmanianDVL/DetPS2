using System;
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
        bool causeIp = (COP0_Cause & ((1u << 10) | (1u << 15))) != 0;
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
                EnterException(GetExceptionVector(general: true), causeExcCode: 0); // Int
                executed++;
                continue;
            }

            if (_hle != null && _hle.ExitRequested)
                break;

            ulong cyc = _cycleSource?.Invoke() ?? 0;
            if (_debugger != null && _debugger.ShouldHaltBefore(PC, cyc))
                break;

            if (_cacheModelEnabled)
                NoteICache(PC);

            uint opcode = _memory.Read32(PC);
            _tracer?.LogInstruction(cyc, PC, opcode);
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

        if (rt == 0 && (long)GetGpr(rs).Lo < 0) { _delaySlotTarget = target; return true; }
        if (rt == 1 && (long)GetGpr(rs).Lo >= 0) { _delaySlotTarget = target; return true; }
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
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = _memory.Read32(addr) });
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
    /// MMI subset (SPECIAL2 / 0x1C) — common parallel integer ops used by games.
    /// Operates on 128-bit GPR Lo/Hi as 2×64 or 4×32 lanes.
    /// </summary>
    private void ExecuteMmi(uint opcode)
    {
        uint function = opcode & 0x3F;
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        uint rd = (opcode >> 11) & 0x1F;
        if (rd == 0) return;

        var a = GetGpr(rs);
        var b = GetGpr(rt);

        switch (function)
        {
            case 0x12: // PAND
                SetGpr(rd, new Gpr128 { Lo = a.Lo & b.Lo, Hi = a.Hi & b.Hi });
                break;
            case 0x13: // POR
                SetGpr(rd, new Gpr128 { Lo = a.Lo | b.Lo, Hi = a.Hi | b.Hi });
                break;
            case 0x14: // PXOR
                SetGpr(rd, new Gpr128 { Lo = a.Lo ^ b.Lo, Hi = a.Hi ^ b.Hi });
                break;
            case 0x16: // PNOR
                SetGpr(rd, new Gpr128 { Lo = ~(a.Lo | b.Lo), Hi = ~(a.Hi | b.Hi) });
                break;
            case 0x08: // PADDW — 4×32 add
            {
                uint a0 = (uint)a.Lo, a1 = (uint)(a.Lo >> 32), a2 = (uint)a.Hi, a3 = (uint)(a.Hi >> 32);
                uint b0 = (uint)b.Lo, b1 = (uint)(b.Lo >> 32), b2 = (uint)b.Hi, b3 = (uint)(b.Hi >> 32);
                ulong lo = (uint)(a0 + b0) | ((ulong)(uint)(a1 + b1) << 32);
                ulong hi = (uint)(a2 + b2) | ((ulong)(uint)(a3 + b3) << 32);
                SetGpr(rd, new Gpr128 { Lo = lo, Hi = hi });
                break;
            }
            case 0x09: // PSUBW
            {
                uint a0 = (uint)a.Lo, a1 = (uint)(a.Lo >> 32), a2 = (uint)a.Hi, a3 = (uint)(a.Hi >> 32);
                uint b0 = (uint)b.Lo, b1 = (uint)(b.Lo >> 32), b2 = (uint)b.Hi, b3 = (uint)(b.Hi >> 32);
                ulong lo = (uint)(a0 - b0) | ((ulong)(uint)(a1 - b1) << 32);
                ulong hi = (uint)(a2 - b2) | ((ulong)(uint)(a3 - b3) << 32);
                SetGpr(rd, new Gpr128 { Lo = lo, Hi = hi });
                break;
            }
            case 0x28: // PEXTLW simplified — pack low 32s
                SetGpr(rd, new Gpr128
                {
                    Lo = (a.Lo & 0xFFFFFFFF) | ((b.Lo & 0xFFFFFFFF) << 32),
                    Hi = ((a.Lo >> 32) & 0xFFFFFFFF) | (((b.Lo >> 32) & 0xFFFFFFFF) << 32)
                });
                break;
            case 0x29: // PEXTUW simplified
                SetGpr(rd, new Gpr128
                {
                    Lo = (a.Hi & 0xFFFFFFFF) | ((b.Hi & 0xFFFFFFFF) << 32),
                    Hi = ((a.Hi >> 32) & 0xFFFFFFFF) | (((b.Hi >> 32) & 0xFFFFFFFF) << 32)
                });
                break;
            case 0x1B: // PCPYLD — copy lo/hi mixed
                SetGpr(rd, new Gpr128 { Lo = b.Lo, Hi = a.Lo });
                break;
            case 0x1E: // PCPYUD
                SetGpr(rd, new Gpr128 { Lo = b.Hi, Hi = a.Hi });
                break;
            case 0x18: // PADDB — 16×8 add (simplified on bytes of lo/hi)
            {
                ulong lo = 0, hi = 0;
                for (int i = 0; i < 8; i++)
                {
                    int s = i * 8;
                    lo |= (ulong)(byte)(((a.Lo >> s) & 0xFF) + ((b.Lo >> s) & 0xFF)) << s;
                    hi |= (ulong)(byte)(((a.Hi >> s) & 0xFF) + ((b.Hi >> s) & 0xFF)) << s;
                }
                SetGpr(rd, new Gpr128 { Lo = lo, Hi = hi });
                break;
            }
            case 0x0A: // PMAXW simplified as max of 32-bit lanes
            {
                uint a0 = (uint)a.Lo, a1 = (uint)(a.Lo >> 32), a2 = (uint)a.Hi, a3 = (uint)(a.Hi >> 32);
                uint b0 = (uint)b.Lo, b1 = (uint)(b.Lo >> 32), b2 = (uint)b.Hi, b3 = (uint)(b.Hi >> 32);
                uint r0 = (int)a0 > (int)b0 ? a0 : b0;
                uint r1 = (int)a1 > (int)b1 ? a1 : b1;
                uint r2 = (int)a2 > (int)b2 ? a2 : b2;
                uint r3 = (int)a3 > (int)b3 ? a3 : b3;
                SetGpr(rd, new Gpr128 { Lo = r0 | ((ulong)r1 << 32), Hi = r2 | ((ulong)r3 << 32) });
                break;
            }
            case 0x0B: // PMINW
            {
                uint a0 = (uint)a.Lo, a1 = (uint)(a.Lo >> 32), a2 = (uint)a.Hi, a3 = (uint)(a.Hi >> 32);
                uint b0 = (uint)b.Lo, b1 = (uint)(b.Lo >> 32), b2 = (uint)b.Hi, b3 = (uint)(b.Hi >> 32);
                uint r0 = (int)a0 < (int)b0 ? a0 : b0;
                uint r1 = (int)a1 < (int)b1 ? a1 : b1;
                uint r2 = (int)a2 < (int)b2 ? a2 : b2;
                uint r3 = (int)a3 < (int)b3 ? a3 : b3;
                SetGpr(rd, new Gpr128 { Lo = r0 | ((ulong)r1 << 32), Hi = r2 | ((ulong)r3 << 32) });
                break;
            }
            default:
                _telemetry?.UnknownOpcode(CurrentCycle(), PC, opcode | 0x1C000000u);
                break;
        }
    }

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
                    default: break;
                }
                break;
            default:
                break;
        }
        return false;
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

    // Unaligned load/store simplified: behave like aligned LW/SW for now (homebrew rarely relies on LWL edge cases in tests)
    private void ExecuteLwl(uint opcode) => ExecuteLw(opcode);
    private void ExecuteLwr(uint opcode) => ExecuteLw(opcode);
    private void ExecuteSwl(uint opcode) => ExecuteSw(opcode);
    private void ExecuteSwr(uint opcode) => ExecuteSw(opcode);
    private void ExecuteSdl(uint opcode) => ExecuteSd(opcode);
    private void ExecuteSdr(uint opcode) => ExecuteSd(opcode);
}