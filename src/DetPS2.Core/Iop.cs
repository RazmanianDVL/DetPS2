using System;

namespace DetPS2.Core;

/// <summary>
/// IOP R3000A interpreter (Phase 8 / IRX WP-05+06).
/// Delay slots, LO/HI, expanded loads/stores, minimal COP0, deterministic stepping.
/// Public <see cref="RunInstructions"/> is the preferred quantum API for IRX module exec.
/// </summary>
public sealed class Iop : ISchedulable
{
    /// <summary>R3000A general exception vector when Status.BEV=0 (KSEG0 → phys 0x80).</summary>
    public const uint VectorGeneral = 0x80000080u;
    /// <summary>R3000A general exception vector when Status.BEV=1 (BIOS).</summary>
    public const uint VectorGeneralBev = 0xBFC00180u;

    public Intc Intc { get; }

    public uint PC { get; set; } = 0xBFC00000;
    private readonly uint[] _gprs = new uint[32];

    public uint LO { get; private set; }
    public uint HI { get; private set; }

    public uint Cop0Status { get; set; }
    public uint Cop0Cause { get; set; }
    public uint Cop0Epc { get; set; }
    public uint Cop0BadVAddr { get; set; }

    public uint SifMbxFromEE { get; private set; }
    public uint SifMbxToEE { get; private set; }

    public bool Running { get; private set; } = true;
    /// <summary>Total instruction slots retired (delay slots count as their own slots).</summary>
    public ulong InstructionsExecuted { get; private set; }
    /// <summary>Number of times <see cref="EnterException"/> ran (SYSCALL/BREAK/…).</summary>
    public ulong ExceptionCount { get; private set; }
    /// <summary>Last COP0 Cause ExcCode (e.g. 8=SYSCALL, 9=BREAK).</summary>
    public uint LastExceptionCode { get; private set; }
    /// <summary>Code field from last SYSCALL/BREAK insn (bits 25:6), if any.</summary>
    public uint LastSyscallCode { get; private set; }

    public static readonly bool TracePc = Environment.GetEnvironmentVariable("DETPS2_TRACE_IOP") == "1";
    public static readonly ulong TracePcLimit =
        ulong.TryParse(Environment.GetEnvironmentVariable("DETPS2_TRACE_IOP_LIMIT"), out var lim) ? lim : 2000;

    /// <summary>Diagnostic-only: logs full call context (sp/ra/gp/a0-a2) every time any call
    /// instruction (J/JAL/JR/JALR) targets this physical IOP address. Opt-in via
    /// DETPS2_TRACE_IOP_CALLWATCH=0xHEXADDR — for tracing who calls a specific real function
    /// (and with what stack) when the static decompile shows no in-module caller.</summary>
    public static readonly uint? WatchCallTarget =
        uint.TryParse(Environment.GetEnvironmentVariable("DETPS2_TRACE_IOP_CALLWATCH"),
            System.Globalization.NumberStyles.HexNumber, null, out var wct) ? wct : (uint?)null;

    /// <summary>Diagnostic-only: once DETPS2_TRACE_IOP_CALLWATCH fires, trace every retired
    /// instruction for this many slots afterward (PC + opcode + v0/a0/ra/sp), so a call site with
    /// no clean, short return can be watched all the way through instead of just at entry/exit.
    /// Opt-in via DETPS2_TRACE_IOP_CALLWATCH_AFTER=N.</summary>
    public static readonly ulong WatchTraceAfterInsns =
        ulong.TryParse(Environment.GetEnvironmentVariable("DETPS2_TRACE_IOP_CALLWATCH_AFTER"), out var wta) ? wta : 0;

    private ulong _watchTraceRemaining;

    /// <summary>Diagnostic-only: the first time PC reaches this address, dumps full GPRs plus a
    /// stack window (sp-0x20..sp+0x40) to stderr, then disarms itself for the rest of the process.
    /// For catching a crash-site PC (e.g. a "jr ra" with a corrupted $ra) with full context on the
    /// very first hit, without knowing in advance which call chain reaches it.
    /// Opt-in via DETPS2_TRACE_IOP_ADDR_ONESHOT=0xHEXADDR.</summary>
    public static readonly uint? OneshotAddr =
        uint.TryParse(Environment.GetEnvironmentVariable("DETPS2_TRACE_IOP_ADDR_ONESHOT"),
            System.Globalization.NumberStyles.HexNumber, null, out var osa) ? osa : (uint?)null;
    private static bool _oneshotFired;

    // Always-on (cheap: fixed-size array, no I/O) ring buffer of the last N (pc,ra) pairs, dumped
    // only if OneshotAddr fires -- lets a single crash-site hit show its own approach path without
    // needing a separate, possibly-huge full trace run.
    private const int RingSize = 256;
    private readonly uint[] _ringPc = new uint[RingSize];
    private readonly uint[] _ringRa = new uint[RingSize];
    private int _ringPos;

    /// <summary>Diagnostic-only: prints current PC every 0x100000 (~1M) executed instructions --
    /// coarse enough to be cheap, fine enough to show whether a long-running stretch is a tight
    /// loop (PC barely moves between heartbeats) or genuine varied work. Opt-in via
    /// DETPS2_TRACE_IOP_HEARTBEAT=1.</summary>
    public static readonly bool TraceHeartbeat =
        Environment.GetEnvironmentVariable("DETPS2_TRACE_IOP_HEARTBEAT") == "1";

    private readonly SystemMemory _memory;
    private uint _branchTarget;
    private bool _pendingVectorJump;
    private uint _vectorTarget;
    private ulong _traceUnkMmioLogged;
    private ulong _traceUnkCopLogged;

    public Iop(Intc intc, SystemMemory memory)
    {
        Intc = intc ?? throw new ArgumentNullException(nameof(intc));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public void Reset()
    {
        PC = 0xBFC00000;
        Array.Clear(_gprs);
        LO = HI = 0;
        Cop0Status = 0;
        Cop0Cause = 0;
        Cop0Epc = 0;
        Cop0BadVAddr = 0;
        SifMbxFromEE = 0;
        SifMbxToEE = 0;
        Running = true;
        InstructionsExecuted = 0;
        ExceptionCount = 0;
        LastExceptionCode = 0;
        LastSyscallCode = 0;
        _branchTarget = 0;
        _pendingVectorJump = false;
        _vectorTarget = 0;
        _traceUnkMmioLogged = 0;
        _traceUnkCopLogged = 0;
    }

    /// <summary>Full IOP core state for SaveState.cs, including LO/HI (no public setters —
    /// the old SaveState.cs saved GPRs/PC directly via GetGpr/PC but never had a way to
    /// restore LO/HI, or the COP0 exception state added this session's real R3000A exception
    /// work — a load would silently resume mid-MULT/DIV or mid-exception-handler with wrong
    /// register state.</summary>
    public void WriteState(System.IO.BinaryWriter w)
    {
        w.Write(PC);
        for (int i = 0; i < 32; i++) w.Write(_gprs[i]);
        w.Write(LO); w.Write(HI);
        w.Write(Cop0Status); w.Write(Cop0Cause); w.Write(Cop0Epc); w.Write(Cop0BadVAddr);
        w.Write(SifMbxFromEE); w.Write(SifMbxToEE);
        w.Write(Running);
        w.Write(InstructionsExecuted);
        w.Write(ExceptionCount);
        w.Write(LastExceptionCode);
        w.Write(LastSyscallCode);
        w.Write(_branchTarget);
        w.Write(_pendingVectorJump);
        w.Write(_vectorTarget);
    }

    public void ReadState(System.IO.BinaryReader r)
    {
        PC = r.ReadUInt32();
        for (int i = 0; i < 32; i++) _gprs[i] = r.ReadUInt32();
        LO = r.ReadUInt32(); HI = r.ReadUInt32();
        Cop0Status = r.ReadUInt32(); Cop0Cause = r.ReadUInt32(); Cop0Epc = r.ReadUInt32(); Cop0BadVAddr = r.ReadUInt32();
        SifMbxFromEE = r.ReadUInt32(); SifMbxToEE = r.ReadUInt32();
        Running = r.ReadBoolean();
        InstructionsExecuted = r.ReadUInt64();
        ExceptionCount = r.ReadUInt64();
        LastExceptionCode = r.ReadUInt32();
        LastSyscallCode = r.ReadUInt32();
        _branchTarget = r.ReadUInt32();
        _pendingVectorJump = r.ReadBoolean();
        _vectorTarget = r.ReadUInt32();
    }

    public uint GetGpr(int index) => _gprs[index & 0x1F];
    public void SetGpr(int index, uint value)
    {
        if ((index & 0x1F) != 0) _gprs[index & 0x1F] = value;
    }

    public void WriteSifMailboxFromEE(uint value)
    {
        SifMbxFromEE = value;
        SifMbxToEE = ~value;
        Intc.Raise(Intc.InterruptSource.Sif);
    }

    public uint ReadSifMailboxToEE() => SifMbxToEE;

    public int Step(ulong maxCycles)
    {
        if (!Running || maxCycles == 0) return 0;

        int executed = 0;
        while ((ulong)executed < maxCycles && Running)
        {
            // Real R3000A raises an Address Error (AdEL) immediately when PC leaves mapped
            // memory, trapping into the real, now-installed exception handler chain (2026-08-03).
            // Without this, a derailed PC (from any earlier bug) walked forward forever through
            // IopRead32's "unmapped == 0 == NOP" fallback -- turning one real, recoverable fault
            // into an unbounded, silent runaway that burned the rest of a module's execution
            // budget doing nothing. Root-caused via a real SDRDRV.IRX crash trace, not guessed.
            if (!_memory.IsKnownIopAddress(PC))
            {
                if (TracePc && ExceptionCount <= TracePcLimit)
                    Console.Error.WriteLine($"[IOP-ADEL] fetch fault pc=0x{PC:X8} n={InstructionsExecuted}");
                EnterException(4, PC); // AdEL
                executed++;
                InstructionsExecuted++;
                if (_pendingVectorJump) { _pendingVectorJump = false; PC = _vectorTarget; }
                continue;
            }
            uint opcode = _memory.IopRead32(PC);
            if (TracePc && InstructionsExecuted < TracePcLimit)
                Console.Error.WriteLine($"[IOPTRACE] n={InstructionsExecuted} pc=0x{PC:X8} op=0x{opcode:X8}");
            if (TraceHeartbeat && (InstructionsExecuted & 0xFFFFF) == 0)
                Console.Error.WriteLine($"[IOP-HEARTBEAT] n={InstructionsExecuted} pc=0x{PC:X8}");
            if (OneshotAddr.HasValue && !_oneshotFired)
            {
                _ringPc[_ringPos] = PC;
                _ringRa[_ringPos] = _gprs[31];
                _ringPos = (_ringPos + 1) % RingSize;
            }
            if (_watchTraceRemaining > 0)
            {
                Console.Error.WriteLine(
                    $"[IOP-CALLWATCH-TRACE] n={InstructionsExecuted} pc=0x{PC:X8} op=0x{opcode:X8} " +
                    $"v0=0x{_gprs[2]:X8} a0=0x{_gprs[4]:X8} a1=0x{_gprs[5]:X8} ra=0x{_gprs[31]:X8} sp=0x{_gprs[29]:X8}");
                _watchTraceRemaining--;
            }
            if (OneshotAddr.HasValue && PC == OneshotAddr.Value && !_oneshotFired)
            {
                _oneshotFired = true;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[IOP-ADDR-ONESHOT] hit pc=0x{PC:X8} n={InstructionsExecuted} op=0x{opcode:X8}");
                for (int r = 0; r < 32; r++)
                    sb.Append($"r{r}=0x{_gprs[r]:X8} ").Append(r % 8 == 7 ? "\n" : "");
                sb.AppendLine();
                sb.AppendLine($"cop0status=0x{Cop0Status:X8} cop0cause=0x{Cop0Cause:X8} cop0epc=0x{Cop0Epc:X8}");
                uint sp = _gprs[29];
                sb.AppendLine("stack window:");
                for (uint off = unchecked((uint)-0x20); off != 0x44; off += 4)
                {
                    uint addr = sp + off;
                    uint val = _memory.IsKnownIopAddress(addr) ? _memory.IopRead32(addr) : 0xFFFFFFFF;
                    sb.AppendLine($"  [sp{(int)off:+0;-0}] = 0x{addr:X8} -> 0x{val:X8}");
                }
                sb.AppendLine($"approach path (last {RingSize} retired instructions, oldest first):");
                for (int i = 0; i < RingSize; i++)
                {
                    int idx = (_ringPos + i) % RingSize;
                    sb.AppendLine($"  pc=0x{_ringPc[idx]:X8} ra=0x{_ringRa[idx]:X8}");
                }
                Console.Error.WriteLine(sb.ToString());
            }
            bool tookBranch = ExecuteInstruction(opcode);
            executed++;
            InstructionsExecuted++;

            if (_pendingVectorJump)
            {
                // Real exception entry (SYSCALL/BREAK) — jumps straight to the vector, no
                // delay-slot fetch of its own (the exception replaces normal sequential
                // control flow at this point; whatever follows the faulting instruction in
                // memory is not executed until/unless the handler itself returns there).
                _pendingVectorJump = false;
                PC = _vectorTarget;
            }
            else if (tookBranch)
            {
                // Delay slot
                uint delay = _memory.IopRead32(PC + 4);
                ExecuteInstruction(delay);
                executed++;
                InstructionsExecuted++;
                PC = _branchTarget;
            }
            else
            {
                PC += 4;
            }
        }
        return executed;
    }

    /// <summary>
    /// Run up to <paramref name="count"/> instruction slots deterministically
    /// (delay slots count). Preferred IRX quantum API (WP-05). Same budget semantics as
    /// <see cref="Step"/>; returns how many slots were actually retired.
    /// </summary>
    public int RunInstructions(ulong count) => Step(count);

    private bool ExecuteInstruction(uint opcode)
    {
        uint primary = (opcode >> 26) & 0x3F;

        return primary switch
        {
            0x00 => ExecuteSpecial(opcode),
            0x01 => ExecuteRegimm(opcode),
            0x02 => J(opcode),
            0x03 => Jal(opcode),
            0x04 => BranchIf(_gprs[Rs(opcode)] == _gprs[Rt(opcode)], opcode),
            0x05 => BranchIf(_gprs[Rs(opcode)] != _gprs[Rt(opcode)], opcode),
            0x06 => BranchIf((int)_gprs[Rs(opcode)] <= 0, opcode), // BLEZ
            0x07 => BranchIf((int)_gprs[Rs(opcode)] > 0, opcode),  // BGTZ
            0x08 => ImmArith(opcode, (a, i) => a + (uint)i),       // ADDI
            0x09 => ImmArith(opcode, (a, i) => a + (uint)i),       // ADDIU
            0x0A => ImmArith(opcode, (a, i) => (uint)((int)a < i ? 1 : 0)), // SLTI
            0x0B => ImmArith(opcode, (a, i) => a < (uint)i ? 1u : 0u),      // SLTIU
            0x0C => ImmLogic(opcode, (a, i) => a & i),             // ANDI
            0x0D => ImmLogic(opcode, (a, i) => a | i),             // ORI
            0x0E => ImmLogic(opcode, (a, i) => a ^ i),             // XORI
            0x0F => Lui(opcode),
            0x10 => ExecuteCop0(opcode),
            // COP1/COP2: IOP R3000A has neither FPU nor COP2 — log + NOP for IRX diagnostics.
            0x11 => UnknownCop(1, opcode),
            0x12 => UnknownCop(2, opcode),
            0x13 => UnknownCop(3, opcode),
            0x20 => LoadStore8(opcode, store: false, signed: true),   // LB
            0x21 => LoadStore16(opcode, store: false, signed: true),  // LH
            0x23 => LoadWord(opcode),                                 // LW
            0x24 => LoadStore8(opcode, store: false, signed: false),  // LBU
            0x25 => LoadStore16(opcode, store: false, signed: false), // LHU
            0x28 => LoadStore8(opcode, store: true, signed: false),   // SB
            0x29 => LoadStore16(opcode, store: true, signed: false),  // SH
            0x2B => StoreWord(opcode),                                // SW
            _ => UnknownOpcode(primary, opcode)
        };
    }

    private bool UnknownOpcode(uint primary, uint opcode)
    {
        if (TracePc && _traceUnkCopLogged < TracePcLimit)
        {
            _traceUnkCopLogged++;
            Console.Error.WriteLine(
                $"[IOP-UNK-OP] pc=0x{PC:X8} primary=0x{primary:X2} op=0x{opcode:X8} n={InstructionsExecuted}");
        }
        return false;
    }

    private bool UnknownCop(int cop, uint opcode)
    {
        if (TracePc && _traceUnkCopLogged < TracePcLimit)
        {
            _traceUnkCopLogged++;
            Console.Error.WriteLine(
                $"[IOP-UNK-COP] pc=0x{PC:X8} cop={cop} op=0x{opcode:X8} n={InstructionsExecuted}");
        }
        return false;
    }

    private static uint Rs(uint op) => (op >> 21) & 0x1F;
    private static uint Rt(uint op) => (op >> 16) & 0x1F;
    private static uint Rd(uint op) => (op >> 11) & 0x1F;
    private static uint Sa(uint op) => (op >> 6) & 0x1F;
    private static short Imm16(uint op) => (short)(op & 0xFFFF);
    private static ushort ImmU16(uint op) => (ushort)(op & 0xFFFF);

    private bool BranchTo(uint target)
    {
        _branchTarget = target;
        return true;
    }

    private bool BranchIf(bool cond, uint opcode)
    {
        if (!cond) return false;
        int off = Imm16(opcode) << 2;
        return BranchTo((uint)(PC + 4 + off));
    }

    private bool J(uint opcode)
    {
        uint target = ((PC + 4) & 0xF0000000) | ((opcode & 0x03FFFFFF) << 2);
        if (WatchCallTarget.HasValue && target == WatchCallTarget.Value)
        {
            Console.Error.WriteLine(
                $"[IOP-CALLWATCH] J (no-link, ra inherited) to 0x{target:X8} from pc=0x{PC:X8} " +
                $"n={InstructionsExecuted} sp=0x{_gprs[29]:X8} ra=0x{_gprs[31]:X8} gp=0x{_gprs[28]:X8} " +
                $"a0=0x{_gprs[4]:X8} a1=0x{_gprs[5]:X8} a2=0x{_gprs[6]:X8}");
            _watchTraceRemaining = WatchTraceAfterInsns;
        }
        return BranchTo(target);
    }

    private bool Jal(uint opcode)
    {
        _gprs[31] = PC + 8;
        uint target = ((PC + 4) & 0xF0000000) | ((opcode & 0x03FFFFFF) << 2);
        if (WatchCallTarget.HasValue && target == WatchCallTarget.Value)
        {
            Console.Error.WriteLine(
                $"[IOP-CALLWATCH] JAL to 0x{target:X8} from pc=0x{PC:X8} n={InstructionsExecuted} " +
                $"sp=0x{_gprs[29]:X8} ra(after)=0x{_gprs[31]:X8} gp=0x{_gprs[28]:X8} " +
                $"a0=0x{_gprs[4]:X8} a1=0x{_gprs[5]:X8} a2=0x{_gprs[6]:X8}");
            _watchTraceRemaining = WatchTraceAfterInsns;
        }
        return BranchTo(target);
    }

    private bool ImmArith(uint opcode, Func<uint, int, uint> fn)
    {
        uint rt = Rt(opcode);
        if (rt != 0) _gprs[rt] = fn(_gprs[Rs(opcode)], Imm16(opcode));
        return false;
    }

    private bool ImmLogic(uint opcode, Func<uint, uint, uint> fn)
    {
        uint rt = Rt(opcode);
        if (rt != 0) _gprs[rt] = fn(_gprs[Rs(opcode)], ImmU16(opcode));
        return false;
    }

    private bool Lui(uint opcode)
    {
        uint rt = Rt(opcode);
        if (rt != 0) _gprs[rt] = (uint)ImmU16(opcode) << 16;
        return false;
    }

    private bool ExecuteSpecial(uint opcode)
    {
        uint fn = opcode & 0x3F;
        uint rs = Rs(opcode), rt = Rt(opcode), rd = Rd(opcode);
        int sa = (int)Sa(opcode);

        switch (fn)
        {
            case 0x00: if (rd != 0) _gprs[rd] = _gprs[rt] << sa; break; // SLL
            case 0x02: if (rd != 0) _gprs[rd] = _gprs[rt] >> sa; break; // SRL
            case 0x03: if (rd != 0) _gprs[rd] = (uint)((int)_gprs[rt] >> sa); break; // SRA
            case 0x04: if (rd != 0) _gprs[rd] = _gprs[rt] << (int)(_gprs[rs] & 0x1F); break; // SLLV
            case 0x06: if (rd != 0) _gprs[rd] = _gprs[rt] >> (int)(_gprs[rs] & 0x1F); break; // SRLV
            case 0x07: if (rd != 0) _gprs[rd] = (uint)((int)_gprs[rt] >> (int)(_gprs[rs] & 0x1F)); break; // SRAV
            case 0x08: // JR
                if (TracePc && _gprs[rs] < 0x1000)
                    Console.Error.WriteLine(
                        $"[IOP-BADJUMP] JR to 0x{_gprs[rs]:X8} from pc=0x{PC:X8} n={InstructionsExecuted} " +
                        $"rs=${rs} ra=0x{_gprs[31]:X8} v0=0x{_gprs[2]:X8} a0=0x{_gprs[4]:X8}");
                if (WatchCallTarget.HasValue && _gprs[rs] == WatchCallTarget.Value)
                {
                    Console.Error.WriteLine(
                        $"[IOP-CALLWATCH] JR to 0x{_gprs[rs]:X8} from pc=0x{PC:X8} n={InstructionsExecuted} " +
                        $"rs=${rs} sp=0x{_gprs[29]:X8} ra=0x{_gprs[31]:X8} gp=0x{_gprs[28]:X8} " +
                        $"a0=0x{_gprs[4]:X8} a1=0x{_gprs[5]:X8} a2=0x{_gprs[6]:X8}");
                    _watchTraceRemaining = WatchTraceAfterInsns;
                }
                return BranchTo(_gprs[rs]);
            case 0x09: // JALR
                uint ret = PC + 8;
                uint target = _gprs[rs];
                if (TracePc && target < 0x1000)
                    Console.Error.WriteLine(
                        $"[IOP-BADJUMP] JALR to 0x{target:X8} from pc=0x{PC:X8} n={InstructionsExecuted} " +
                        $"rs=${rs} ra=0x{_gprs[31]:X8} v0=0x{_gprs[2]:X8} a0=0x{_gprs[4]:X8}");
                if (WatchCallTarget.HasValue && target == WatchCallTarget.Value)
                {
                    Console.Error.WriteLine(
                        $"[IOP-CALLWATCH] JALR to 0x{target:X8} from pc=0x{PC:X8} n={InstructionsExecuted} " +
                        $"rs=${rs} sp=0x{_gprs[29]:X8} ra(before)=0x{_gprs[31]:X8} ret(after)=0x{ret:X8} gp=0x{_gprs[28]:X8} " +
                        $"a0=0x{_gprs[4]:X8} a1=0x{_gprs[5]:X8} a2=0x{_gprs[6]:X8}");
                    _watchTraceRemaining = WatchTraceAfterInsns;
                }
                if (rd != 0) _gprs[rd] = ret;
                return BranchTo(target);
            case 0x0C: // SYSCALL — real R3000A exception entry (ExcCode 8), not a halt.
                LastSyscallCode = (opcode >> 6) & 0xFFFFF;
                EnterException(8);
                break;
            case 0x0D: // BREAK (ExcCode 9)
                LastSyscallCode = (opcode >> 6) & 0xFFFFF;
                EnterException(9);
                break;
            case 0x10: if (rd != 0) _gprs[rd] = HI; break; // MFHI
            case 0x11: HI = _gprs[rs]; break; // MTHI
            case 0x12: if (rd != 0) _gprs[rd] = LO; break; // MFLO
            case 0x13: LO = _gprs[rs]; break; // MTLO
            case 0x18: // MULT
            {
                long r = (long)(int)_gprs[rs] * (int)_gprs[rt];
                LO = (uint)r;
                HI = (uint)(r >> 32);
                break;
            }
            case 0x19: // MULTU
            {
                ulong r = (ulong)_gprs[rs] * _gprs[rt];
                LO = (uint)r;
                HI = (uint)(r >> 32);
                break;
            }
            case 0x1A: // DIV
                if (_gprs[rt] != 0)
                {
                    LO = (uint)((int)_gprs[rs] / (int)_gprs[rt]);
                    HI = (uint)((int)_gprs[rs] % (int)_gprs[rt]);
                }
                break;
            case 0x1B: // DIVU
                if (_gprs[rt] != 0)
                {
                    LO = _gprs[rs] / _gprs[rt];
                    HI = _gprs[rs] % _gprs[rt];
                }
                break;
            case 0x20:
            case 0x21: if (rd != 0) _gprs[rd] = _gprs[rs] + _gprs[rt]; break;
            case 0x22:
            case 0x23: if (rd != 0) _gprs[rd] = _gprs[rs] - _gprs[rt]; break;
            case 0x24: if (rd != 0) _gprs[rd] = _gprs[rs] & _gprs[rt]; break;
            case 0x25: if (rd != 0) _gprs[rd] = _gprs[rs] | _gprs[rt]; break;
            case 0x26: if (rd != 0) _gprs[rd] = _gprs[rs] ^ _gprs[rt]; break;
            case 0x27: if (rd != 0) _gprs[rd] = ~(_gprs[rs] | _gprs[rt]); break; // NOR
            case 0x2A: if (rd != 0) _gprs[rd] = (int)_gprs[rs] < (int)_gprs[rt] ? 1u : 0u; break;
            case 0x2B: if (rd != 0) _gprs[rd] = _gprs[rs] < _gprs[rt] ? 1u : 0u; break;
        }
        return false;
    }

    private bool ExecuteRegimm(uint opcode)
    {
        uint rt = Rt(opcode);
        int val = (int)_gprs[Rs(opcode)];
        bool take = rt switch
        {
            0x00 => val < 0,  // BLTZ
            0x01 => val >= 0, // BGEZ
            0x10 => val < 0,  // BLTZAL
            0x11 => val >= 0, // BGEZAL
            _ => false
        };
        // MIPS link always writes $ra, whether or not the branch is taken.
        if (rt == 0x10 || rt == 0x11)
            _gprs[31] = PC + 8;
        return take && BranchIf(true, opcode);
    }

    private bool ExecuteCop0(uint opcode)
    {
        uint rs = Rs(opcode);
        uint rt = Rt(opcode);
        uint rd = Rd(opcode);
        switch (rs)
        {
            case 0x00: // MFC0
                if (rt != 0) _gprs[rt] = ReadCop0(rd);
                break;
            case 0x04: // MTC0
                WriteCop0(rd, _gprs[rt]);
                break;
            case 0x10: // COP0 CO — RFE is func=0x10
                // RFE: shift the KUp/IEp pair (bits 3:2) back down into KUc/IEc (bits 1:0).
                // R3000A has no EXL bit (later-MIPS/R5900) — 3-deep current/previous/old stack.
                if ((opcode & 0x3F) == 0x10)
                    Cop0Status = (Cop0Status & ~0xFu) | ((Cop0Status & 0x3Cu) >> 2);
                else if (TracePc && _traceUnkCopLogged < TracePcLimit)
                {
                    _traceUnkCopLogged++;
                    Console.Error.WriteLine(
                        $"[IOP-UNK-COP] pc=0x{PC:X8} cop=0 co_func=0x{opcode & 0x3F:X2} op=0x{opcode:X8}");
                }
                break;
            default:
                if (TracePc && _traceUnkCopLogged < TracePcLimit)
                {
                    _traceUnkCopLogged++;
                    Console.Error.WriteLine(
                        $"[IOP-UNK-COP] pc=0x{PC:X8} cop=0 rs=0x{rs:X2} op=0x{opcode:X8} n={InstructionsExecuted}");
                }
                break;
        }
        return false;
    }

    /// <summary>
    /// Real R3000A exception entry: EPC = faulting PC, Cause.ExcCode set, the KU/IE stack
    /// shifted left (current pair becomes previous, new current pair forced to kernel-mode/
    /// interrupts-disabled), PC redirected to the BEV-selected vector. This is what lets the
    /// real IOP BIOS ROM's own exception dispatcher (present in the same BIOS dump already
    /// used for the EE side — confirmed 0xBFC00000 maps to real BIOS_BASE=0x1FC00000 data,
    /// not a synthesized stub) actually run for real instead of the interpreter just halting
    /// on the first SYSCALL it hits. Simplification: EPC always = PC (no delay-slot/BD-bit
    /// tracking yet, since Step()'s delay-slot handling doesn't expose "is this instruction
    /// itself in a delay slot" to ExecuteInstruction) -- correct for the overwhelmingly common
    /// case of a syscall not in a delay slot, wrong in the rare case it is.
    /// </summary>
    private void EnterException(uint excCode, uint badVAddr = 0)
    {
        bool bev = (Cop0Status & (1u << 22)) != 0;
        Cop0Epc = PC;
        Cop0Cause = (Cop0Cause & ~0x7Cu) | ((excCode & 0x1Fu) << 2);
        if (excCode == 4 || excCode == 5) Cop0BadVAddr = badVAddr; // AdEL/AdES
        // Shift KU/IE stack: insert kernel-mode (KUc=0) + IE disabled (IEc=0) as new current.
        Cop0Status = (Cop0Status & ~0x3Fu) | ((Cop0Status & 0xFu) << 2);
        _vectorTarget = bev ? VectorGeneralBev : VectorGeneral;
        _pendingVectorJump = true;
        LastExceptionCode = excCode;
        ExceptionCount++;
        if (TracePc && ExceptionCount <= TracePcLimit)
        {
            Console.Error.WriteLine(
                $"[IOP-EXC] code={excCode} syscall=0x{LastSyscallCode:X} epc=0x{Cop0Epc:X8} " +
                $"vec=0x{_vectorTarget:X8} bev={(bev ? 1 : 0)} n={InstructionsExecuted} " +
                $"v0=0x{_gprs[2]:X8} v1=0x{_gprs[3]:X8} a0=0x{_gprs[4]:X8} a1=0x{_gprs[5]:X8} a2=0x{_gprs[6]:X8} a3=0x{_gprs[7]:X8} ra=0x{_gprs[31]:X8}");
        }
    }

    /// <summary>
    /// Install a minimal R3000 general-exception stub at the BEV=0 vector (phys 0x80):
    /// skip the faulting insn (EPC+4) and RFE/return. Enough for synthetic IRX <c>_start</c>
    /// and unit smokes that issue SYSCALL/BREAK without a full IOP BIOS handler (WP-06).
    /// Does not touch BEV=1 BIOS vector (ROM).
    /// </summary>
    public void InstallMinimalExceptionStub()
    {
        // k0 = $26. Classic R3000: mfc0 k0,EPC; addiu k0,k0,4; jr k0; rfe (delay slot).
        const uint k0 = 26;
        uint Mfc0(uint rt, uint rd) => (0x10u << 26) | (0x00u << 21) | (rt << 16) | (rd << 11);
        uint Addiu(uint rt, uint rs, short imm) =>
            (0x09u << 26) | (rs << 21) | (rt << 16) | (ushort)imm;
        uint Jr(uint rs) => (0x00u << 26) | (rs << 21) | 0x08u;
        const uint Rfe = (0x10u << 26) | (0x10u << 21) | 0x10u; // COP0 CO RFE

        uint[] stub =
        {
            Mfc0(k0, 14),      // mfc0 k0, $14 (EPC)
            Addiu(k0, k0, 4),  // skip SYSCALL/BREAK
            Jr(k0),            // jr k0
            Rfe                // rfe in delay slot
        };
        for (int i = 0; i < stub.Length; i++)
            _memory.IopWrite32(VectorGeneral + (uint)(i * 4), stub[i]);
    }

    private uint ReadCop0(uint reg) => reg switch
    {
        8 => Cop0BadVAddr,
        12 => Cop0Status,
        13 => Cop0Cause,
        14 => Cop0Epc,
        // PRId — R3000A identity. SIFMAN _start does `mfc0 v0,$15; slti v0,v0,16; bne early_exit`
        // and skips all SIF hardware init when PRId < 16. Returning 0 made SIFMAN a no-op
        // (modres=1 after 11 insns) so SIFCMD/SIFINIT hung waiting for an uninitialised SIF.
        // 0x1F matches common IOP R3000A PRId values used by PCSX2-class cores (≥16 → full init).
        15 => 0x0000001Fu,
        _ => 0
    };

    private void WriteCop0(uint reg, uint value)
    {
        switch (reg)
        {
            case 12: Cop0Status = value; break;
            case 13: Cop0Cause = value; break;
            case 14: Cop0Epc = value; break;
        }
    }

    private uint EffectiveAddress(uint opcode) => _gprs[Rs(opcode)] + (uint)Imm16(opcode);

    /// <summary>
    /// COP0 Status bit 16 (IsC) — Isolate Cache. When set, load/store touch only the data
    /// cache, not main memory. LOADCORE's <c>FlushDcache</c> (and siblings) set IsC then
    /// store-zero through the low 4 KiB to invalidate dcache lines; without honouring IsC
    /// those stores wipe the R3000 exception vectors at 0x80 and every subsequent SYSCALL
    /// falls into a nop sled (INTRMANP/THREADMAN/FILEIO budget-storm root cause).
    /// </summary>
    private bool CacheIsolated => (Cop0Status & (1u << 16)) != 0;

    /// <summary>
    /// IOP bus regions currently backed by SystemMemory.Iop*: RAM, BIOS window, SIF mailbox.
    /// Everything else is "unknown MMIO" for WP-05 diagnostics (real peripherals land here later).
    /// </summary>
    private static bool IsKnownIopMap(uint addr)
    {
        uint p = addr & 0x1FFFFFFFu;
        if (p < (uint)SystemMemory.IOP_RAM_SIZE) return true;
        if (p >= SystemMemory.IOP_SIF_BASE && p < SystemMemory.IOP_SIF_BASE + SystemMemory.IOP_SIF_SIZE)
            return true;
        if (p >= SystemMemory.IOP_IO_BASE && p < SystemMemory.IOP_IO_BASE + SystemMemory.IOP_IO_SIZE)
            return true;
        if (p >= SystemMemory.BIOS_BASE && p < SystemMemory.BIOS_BASE + (uint)SystemMemory.BIOS_SIZE)
            return true;
        return false;
    }

    private void TraceUnknownMmio(string op, uint addr, uint value = 0)
    {
        if (!TracePc || _traceUnkMmioLogged >= TracePcLimit) return;
        _traceUnkMmioLogged++;
        Console.Error.WriteLine(
            $"[IOP-UNK-MMIO] {op} addr=0x{addr:X8} val=0x{value:X8} pc=0x{PC:X8} n={InstructionsExecuted}");
    }

    private uint MemRead32(uint addr)
    {
        // Isolated cache: real R3000 returns dcache contents; we have no dcache model, so 0.
        // Instruction fetch still uses IopRead32(PC) directly — only load ops go through here.
        if (CacheIsolated) return 0;
        if (!IsKnownIopMap(addr)) TraceUnknownMmio("R32", addr);
        return _memory.IopRead32(addr);
    }

    private void MemWrite32(uint addr, uint value)
    {
        // Isolated cache: discard store (FlushDcache invalidation must not touch IOP RAM).
        if (CacheIsolated) return;
        if (!IsKnownIopMap(addr)) TraceUnknownMmio("W32", addr, value);
        _memory.IopWrite32(addr, value);
    }

    private byte MemRead8(uint addr)
    {
        if (CacheIsolated) return 0;
        if (!IsKnownIopMap(addr)) TraceUnknownMmio("R8", addr);
        return _memory.IopRead8(addr);
    }

    private void MemWrite8(uint addr, byte value)
    {
        if (CacheIsolated) return;
        if (!IsKnownIopMap(addr)) TraceUnknownMmio("W8", addr, value);
        _memory.IopWrite8(addr, value);
    }

    private bool LoadWord(uint opcode)
    {
        uint rt = Rt(opcode);
        uint addr = EffectiveAddress(opcode);
        if (rt != 0) _gprs[rt] = MemRead32(addr);
        return false;
    }

    private bool StoreWord(uint opcode)
    {
        MemWrite32(EffectiveAddress(opcode), _gprs[Rt(opcode)]);
        return false;
    }

    private bool LoadStore8(uint opcode, bool store, bool signed)
    {
        uint addr = EffectiveAddress(opcode);
        uint rt = Rt(opcode);
        if (store)
        {
            MemWrite8(addr, (byte)_gprs[rt]);
        }
        else if (rt != 0)
        {
            byte b = MemRead8(addr);
            _gprs[rt] = signed ? (uint)(sbyte)b : b;
        }
        return false;
    }

    private bool LoadStore16(uint opcode, bool store, bool signed)
    {
        uint addr = EffectiveAddress(opcode);
        uint rt = Rt(opcode);
        if (store)
        {
            MemWrite8(addr, (byte)_gprs[rt]);
            MemWrite8(addr + 1, (byte)(_gprs[rt] >> 8));
        }
        else if (rt != 0)
        {
            ushort h = (ushort)(MemRead8(addr) | (MemRead8(addr + 1) << 8));
            _gprs[rt] = signed ? (uint)(short)h : h;
        }
        return false;
    }

    public void Stop() => Running = false;

    /// <summary>Assemble helper: write words into IOP-visible memory and set PC.</summary>
    public void LoadProgram(uint address, ReadOnlySpan<uint> words)
    {
        for (int i = 0; i < words.Length; i++)
            _memory.IopWrite32(address + (uint)(i * 4), words[i]);
        PC = address;
        Running = true;
        InstructionsExecuted = 0;
        ExceptionCount = 0;
        LastExceptionCode = 0;
        LastSyscallCode = 0;
    }
}
