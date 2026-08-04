using System;

namespace DetPS2.Core;

/// <summary>
/// ps2sdk-style thread status bits for IOP cooperative multi-context (THREADMAN contract).
/// Combinable: WAIT|SUSPEND = 0x0C. Slice-1 scaffolding only — not a full ready-queue port.
/// </summary>
[Flags]
public enum IopThreadStatus : byte
{
    None = 0,
    /// <summary>THS_RUN — currently executing on the R3000 live register file.</summary>
    Run = 0x01,
    /// <summary>THS_READY — runnable, waiting for a switch.</summary>
    Ready = 0x02,
    /// <summary>THS_WAIT — SleepThread / WaitSema / etc. (THREADMAN later).</summary>
    Wait = 0x04,
    /// <summary>THS_SUSPEND — suspend nest (THREADMAN later).</summary>
    Suspend = 0x08,
    /// <summary>THS_DORMANT — never started or exited.</summary>
    Dormant = 0x10,
}

/// <summary>
/// Saved IOP R3000 register context for one cooperative thread.
/// Live decode loop still uses <see cref="Iop"/>'s active PC/GPR arrays; switch = save active → load target.
/// See docs/IOP_MULTITHREAD_AND_REAL_RPC.md §2.
/// </summary>
public sealed class IopThreadContext
{
    public int Id { get; internal set; }
    public IopThreadStatus Status { get; set; }
    /// <summary>Resume PC after yield / switch.</summary>
    public uint PC;
    /// <summary>Full 32 GPRs; r0 is forced zero on restore.</summary>
    public readonly uint[] Gprs = new uint[32];
    public uint HI;
    public uint LO;
    /// <summary>Initial top-of-stack (SP at create). Stacks grow downward on R3000.</summary>
    public uint StackTop;
    /// <summary>Reserved stack size in bytes (convention for unique SP allocation).</summary>
    public uint StackSize;
    /// <summary>True when this slot holds a live context (boot thread 0 is always live when table exists).</summary>
    public bool InUse;

    /// <summary>$sp = Gprs[29]. Explicit accessor for stack-pointer convention docs.</summary>
    public uint Sp
    {
        get => Gprs[29];
        set => Gprs[29] = value;
    }
}

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

    /// <summary>
    /// Env <c>DETPS2_IOP_THREADS=1</c> enables multi-context save/restore scaffolding.
    /// Unset / 0 (default): single flat register file — byte-identical to pre-scaffolding tip.
    /// </summary>
    public static readonly bool MultiThreadEnvEnabled =
        Environment.GetEnvironmentVariable("DETPS2_IOP_THREADS") == "1";

    /// <summary>Dense thread table size (boot = id 0). Full THREADMAN ready queues are later.</summary>
    public const int MaxIopThreadSlots = 32;

    /// <summary>
    /// Base of reserved secondary-stack region (IOP phys). Slot N uses
    /// <c>[base + N*size, base + (N+1)*size)</c> with SP = top. Below THREADMAN entry slots
    /// (0x1D0000) and RealSifRpc scratch (0x1E0000); not a heap-safe proof — THREADMAN later.
    /// </summary>
    public const uint ThreadStackRegionBase = 0x001C0000u;
    public const uint ThreadStackSlotSize = 0x2000u;

    /// <summary>
    /// C1.2: dedicated module-entry stack arena (IOP phys) for <c>PrepareModuleEntry</c> binds
    /// when multi-thread is on. Region <c>[0x1B0000, 0x1C0000)</c> = 8 × 8 KiB, immediately
    /// below <see cref="ThreadStackRegionBase"/>. Opt-in only — flag-off path never touches this.
    /// Remaining work: real THREADMAN heap-backed stacks; free slots on UnloadModule.
    /// </summary>
    public const uint ModuleEntryStackArenaBase = 0x001B0000u;
    public const int MaxModuleEntryStacks = 8;

    public Intc Intc { get; }

    public uint PC { get; set; } = 0xBFC00000;
    private readonly uint[] _gprs = new uint[32];

    public uint LO { get; private set; }
    public uint HI { get; private set; }

    // --- C1 multi-thread context scaffolding (DETPS2_IOP_THREADS) ---
    // When disabled: _threads stays null, no allocations, Step path untouched.
    // When enabled: table of IopThreadContext; live PC/_gprs/HI/LO are the current thread.
    // C1.3: explicit YieldToReady / ParkAndYieldToReady hooks (WaitSema/SleepThread-shaped).
    // C1.4: dedicated RealSifRpc mid-quantum dispatch context (see TryEnterRealRpcDispatch).
    // Full THREADMAN ready-queues / wait-object ids: later (IOP_MULTITHREAD_AND_REAL_RPC.md).
    private bool _multiThreadEnabled = MultiThreadEnvEnabled;
    private IopThreadContext[]? _threads;
    private int _currentThreadId;
    private int _nextThreadSlot = 1;
    /// <summary>Reusable secondary slot for <see cref="TryEnterRealRpcDispatch"/> (−1 = none).</summary>
    private int _rpcDispatchThreadId = -1;

    /// <summary>
    /// RealSifRpc mid-quantum handler scratch stack top (IOP phys). Matches
    /// <c>RealSifRpc.TryDispatchRealRegisteredRpc</c>; below THREADMAN entry slots, above
    /// module-entry arena when multi-thread is on.
    /// </summary>
    public const uint RealRpcDispatchStackTop = 0x001E0000u;

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
    /// <summary>Subset of <see cref="ExceptionCount"/> that were hardware-interrupt entries
    /// (excCode==0), i.e. real INTC line takes -- diagnostic for telling "spending its whole
    /// quantum servicing one interrupt per instruction" apart from "doing real work".</summary>
    public ulong InterruptExceptionCount { get; private set; }
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
    /// <summary>Ignore OneshotAddr hits before this global instruction count -- avoids catching
    /// an earlier, unrelated legitimate pass through the same PC value before the quantum of
    /// interest even starts. Opt-in via DETPS2_TRACE_IOP_ADDR_ONESHOT_AFTER_N=decimal.</summary>
    public static readonly ulong OneshotMinInsn =
        ulong.TryParse(Environment.GetEnvironmentVariable("DETPS2_TRACE_IOP_ADDR_ONESHOT_AFTER_N"), out var omn) ? omn : 0;
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

    /// <summary>Diagnostic-only: fires the first time $sp (r29) transitions TO this exact value
    /// (not merely equals it -- catches the actual assignment/restore, not every instruction
    /// while it stays there). Opt-in via DETPS2_TRACE_IOP_SP_BECOMES=0xHEXVALUE.</summary>
    public static readonly uint? WatchSpBecomes =
        uint.TryParse(Environment.GetEnvironmentVariable("DETPS2_TRACE_IOP_SP_BECOMES"),
            System.Globalization.NumberStyles.HexNumber, null, out var wsb) ? wsb : (uint?)null;
    private uint _lastSp;
    private bool _spWatchFired;

    /// <summary>Diagnostic-only: logs sp/ra/a3 whenever PC hits one of a comma-separated list of
    /// addresses, up to a small cap per address -- for comparing register state across two or more
    /// specific instructions (e.g. a call site vs. its epilogue) without a full trace dump.
    /// Opt-in via DETPS2_TRACE_IOP_PC_LOG=0xHEXADDR,0xHEXADDR,...</summary>
    private static readonly HashSet<uint>? PcLogAddrs = ParsePcLogAddrs();
    private readonly Dictionary<uint, int> _pcLogHits = new();
    private const int PcLogCapPerAddr = 6;

    private static HashSet<uint>? ParsePcLogAddrs()
    {
        var s = Environment.GetEnvironmentVariable("DETPS2_TRACE_IOP_PC_LOG");
        if (string.IsNullOrEmpty(s)) return null;
        var set = new HashSet<uint>();
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = part.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            if (uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v))
                set.Add(v);
        }
        return set.Count > 0 ? set : null;
    }

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

    /// <summary>
    /// Real MIPS R3000 hardware reset clears Status (IEc/KUc/IM all zero) -- a genuine IOP reset
    /// (EELOAD's IOPRP/UDNL handoff) resets the CPU's interrupt-enable state along with its
    /// peripherals, so a freshly-reloaded module's _start always begins with interrupts
    /// genuinely disabled, exactly like real hardware, instead of inheriting whatever Status/
    /// Cause a *previous*, unrelated module's synthetic quantum happened to leave behind (which
    /// let a stale pending interrupt fire on THREADMAN's very first instruction, before it ever
    /// reached its own real init code). Deliberately does not touch PC/GPRs/PC -- the module
    /// loader (SifRpc.PrepareModuleEntry) owns those per-module. Pair with
    /// SystemMemory.ResetIopInterruptControllerForIopReset.
    /// </summary>
    public void ResetInterruptStateForIopReset()
    {
        Cop0Status = 0;
        Cop0Cause = 0;
        _pendingVectorJump = false;
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
        ResetThreadTable();
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
            // Real R3000A hardware interrupt delivery (2026-08-03). The whole real IOP INTC
            // (VBLANK, timers, DMA, SIF, ...) wires its combined output to a single external
            // interrupt input -- COP0 hardware interrupt 2 (Status/Cause bit 10, IM2/IP2), the
            // standard PS1/PS2 IOP convention. Checked at every instruction boundary (never
            // mid-delay-slot, since delay slots execute inline within one loop iteration here),
            // matching real timing. Before this, Iop.Step never checked for a pending interrupt
            // at all (only SYSCALL/BREAK/AdEL) -- so even though real EXCEPMAN/INTRMAN/THREADMAN/
            // VBLANK.IRX genuinely install a real exception vector and real interrupt handlers
            // (confirmed running for real elsewhere this session), no real hardware event could
            // ever reach them, starving THREADMAN's own real interrupt-driven scheduler of the
            // only thing that lets it ever switch to a newly-started worker thread. Root-caused
            // via IOPFILE.IRX's real _start returning cleanly (having created+started a worker)
            // without that worker ever getting CPU time again, not guessed.
            if (_memory.IopInterruptLineAsserted)
                Cop0Cause |= 0x400u;
            else
                Cop0Cause &= ~0x400u;
            if ((Cop0Status & 1u) != 0 && (Cop0Status & 0x400u) != 0 && (Cop0Cause & 0x400u) != 0)
            {
                if (TracePc && ExceptionCount <= TracePcLimit)
                    Console.Error.WriteLine($"[IOP-IRQ] hw interrupt pc=0x{PC:X8} n={InstructionsExecuted}");
                EnterException(0);
                InterruptExceptionCount++;
                executed++;
                InstructionsExecuted++;
                if (_pendingVectorJump) { _pendingVectorJump = false; PC = _vectorTarget; }
                continue;
            }
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
            if (WatchSpBecomes.HasValue && !_spWatchFired &&
                _gprs[29] == WatchSpBecomes.Value && _lastSp != WatchSpBecomes.Value)
            {
                _spWatchFired = true;
                Console.Error.WriteLine(
                    $"[IOP-SP-BECOMES] sp transitioned to 0x{_gprs[29]:X8} at pc=0x{PC:X8} " +
                    $"n={InstructionsExecuted} ra=0x{_gprs[31]:X8} v0=0x{_gprs[2]:X8} lastSp=0x{_lastSp:X8}");
            }
            _lastSp = _gprs[29];
            if (PcLogAddrs != null && PcLogAddrs.Contains(PC))
            {
                _pcLogHits.TryGetValue(PC, out var hitCount);
                if (hitCount < PcLogCapPerAddr)
                {
                    _pcLogHits[PC] = hitCount + 1;
                    Console.Error.WriteLine(
                        $"[IOP-PC-LOG] pc=0x{PC:X8} hit#{hitCount} n={InstructionsExecuted} " +
                        $"sp=0x{_gprs[29]:X8} ra=0x{_gprs[31]:X8} a3=0x{_gprs[7]:X8} v0=0x{_gprs[2]:X8} s0=0x{_gprs[16]:X8}");
                }
            }
            if ((OneshotAddr.HasValue && !_oneshotFired) || WatchWriteAddr.HasValue ||
                (WatchWriteRange.HasValue && _watchRangeHits == 0))
            {
                _ringPc[_ringPos] = PC;
                _ringRa[_ringPos] = _gprs[31];
                _ringPos = (_ringPos + 1) % RingSize;
            }
            if (_watchTraceRemaining > 0)
            {
                Console.Error.WriteLine(
                    $"[IOP-CALLWATCH-TRACE] n={InstructionsExecuted} pc=0x{PC:X8} op=0x{opcode:X8} " +
                    $"v0=0x{_gprs[2]:X8} v1=0x{_gprs[3]:X8} a0=0x{_gprs[4]:X8} a1=0x{_gprs[5]:X8} a2=0x{_gprs[6]:X8} " +
                    $"t0=0x{_gprs[8]:X8} t1=0x{_gprs[9]:X8} s0=0x{_gprs[16]:X8} " +
                    $"ra=0x{_gprs[31]:X8} sp=0x{_gprs[29]:X8}");
                _watchTraceRemaining--;
            }
            if (OneshotAddr.HasValue && PC == OneshotAddr.Value && !_oneshotFired &&
                InstructionsExecuted >= OneshotMinInsn)
            {
                _oneshotFired = true;
                _watchTraceRemaining = WatchTraceAfterInsns; // also trace forward from a PC-value hit
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
        if (!IsKnownIopMap(addr))
        {
            TraceUnknownMmio("R32", addr);
            RaiseDataAddressFault(4, addr); // AdEL
        }
        return _memory.IopRead32(addr);
    }

    private void MemWrite32(uint addr, uint value)
    {
        // Isolated cache: discard store (FlushDcache invalidation must not touch IOP RAM).
        if (CacheIsolated) return;
        if (!IsKnownIopMap(addr))
        {
            TraceUnknownMmio("W32", addr, value);
            RaiseDataAddressFault(5, addr); // AdES
        }
        _memory.IopWrite32(addr, value);
    }

    private byte MemRead8(uint addr)
    {
        if (CacheIsolated) return 0;
        if (!IsKnownIopMap(addr))
        {
            TraceUnknownMmio("R8", addr);
            RaiseDataAddressFault(4, addr); // AdEL
        }
        return _memory.IopRead8(addr);
    }

    private void MemWrite8(uint addr, byte value)
    {
        if (CacheIsolated) return;
        if (!IsKnownIopMap(addr))
        {
            TraceUnknownMmio("W8", addr, value);
            RaiseDataAddressFault(5, addr); // AdES
        }
        _memory.IopWrite8(addr, value);
    }

    /// <summary>
    /// Real R3000A raises an Address Error on a data load/store to an address outside every
    /// known-mapped region too, not just on instruction fetch (2026-08-03; extends the fetch-side
    /// AdEL fix from earlier the same day — see <see cref="Step"/>'s own doc comment). Root-caused
    /// via a real crash: a worker thread's real stack pointer drifted 0x28 bytes past the top of
    /// 2 MiB IOP RAM (0x200000), and the offending `lw ra,...(sp)` silently read back 0 from
    /// SystemMemory's out-of-range fallback instead of faulting — turning a real, detectable
    /// stack error into a mysterious "$ra corrupted" crash several instructions later. Only fires
    /// once per faulting instruction (idempotent if called again before the vector jump lands —
    /// harmless, matches how a real pipeline would also not double-fault the same access).
    /// </summary>
    private void RaiseDataAddressFault(uint excCode, uint badVAddr)
    {
        if (_pendingVectorJump) return; // already faulting this instruction (e.g. re-entrant read/modify/write)
        EnterException(excCode, badVAddr);
    }

    private bool LoadWord(uint opcode)
    {
        uint rt = Rt(opcode);
        uint addr = EffectiveAddress(opcode);
        if (rt != 0) _gprs[rt] = MemRead32(addr);
        return false;
    }

    /// <summary>Diagnostic-only: log any store of a value near/past the top of IOP RAM (a
    /// suspicious computed stack pointer). Opt-in via DETPS2_TRACE_IOP_NEARTOP=1 — cheap,
    /// independent of the full-instruction DETPS2_TRACE_IOP trace.</summary>
    public static readonly bool TraceNearTop = Environment.GetEnvironmentVariable("DETPS2_TRACE_IOP_NEARTOP") == "1";
    public static readonly uint? WatchWriteAddr =
        uint.TryParse(Environment.GetEnvironmentVariable("DETPS2_TRACE_IOP_WATCH_WRITE"),
            System.Globalization.NumberStyles.HexNumber, null, out var wwa) ? wwa : (uint?)null;
    public static readonly (uint start, uint end)? WatchWriteRange = ParseWatchRange();
    private static (uint, uint)? ParseWatchRange()
    {
        var s = Environment.GetEnvironmentVariable("DETPS2_TRACE_IOP_WATCH_RANGE");
        if (string.IsNullOrEmpty(s)) return null;
        var parts = s.Split(':');
        if (parts.Length != 2) return null;
        if (!uint.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var a)) return null;
        if (!uint.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var b)) return null;
        return (a, b);
    }
    private static ulong _watchRangeHits;

    private bool StoreWord(uint opcode)
    {
        uint addr = EffectiveAddress(opcode);
        uint val = _gprs[Rt(opcode)];
        if (TraceNearTop && val >= 0x001FFE00u && val <= 0x00200200u)
            Console.Error.WriteLine(
                $"[IOP-NEARTOP-SW] storing near-top-of-RAM value 0x{val:X8} at addr=0x{addr:X8} " +
                $"pc=0x{PC:X8} n={InstructionsExecuted} ra=0x{_gprs[31]:X8}");
        if (WatchWriteAddr.HasValue && addr == WatchWriteAddr.Value)
        {
            Console.Error.WriteLine(
                $"[IOP-WATCH-WRITE] sw val=0x{val:X8} to addr=0x{addr:X8} pc=0x{PC:X8} " +
                $"n={InstructionsExecuted} ra=0x{_gprs[31]:X8} sp=0x{_gprs[29]:X8} " +
                $"rt=${Rt(opcode)} rs=${Rs(opcode)}");
            Console.Error.WriteLine("[IOP-WATCH-WRITE] approach path (last 256 retired, oldest first):");
            for (int k = 0; k < RingSize; k++)
            {
                int idx = (_ringPos + k) % RingSize;
                Console.Error.WriteLine($"  pc=0x{_ringPc[idx]:X8} ra=0x{_ringRa[idx]:X8}");
            }
        }
        if (WatchWriteRange.HasValue && addr >= WatchWriteRange.Value.start && addr < WatchWriteRange.Value.end)
        {
            bool isFirst = _watchRangeHits == 0;
            _watchRangeHits++;
            if (_watchRangeHits <= 20)
                Console.Error.WriteLine(
                    $"[IOP-WATCH-RANGE] sw val=0x{val:X8} to addr=0x{addr:X8} pc=0x{PC:X8} " +
                    $"n={InstructionsExecuted} ra=0x{_gprs[31]:X8} sp=0x{_gprs[29]:X8} " +
                    $"rt=${Rt(opcode)} rs=${Rs(opcode)}");
            if (isFirst)
            {
                Console.Error.WriteLine("[IOP-WATCH-RANGE] approach path to FIRST hit (last 256 retired, oldest first):");
                for (int k = 0; k < RingSize; k++)
                {
                    int idx = (_ringPos + k) % RingSize;
                    Console.Error.WriteLine($"  pc=0x{_ringPc[idx]:X8} ra=0x{_ringRa[idx]:X8}");
                }
            }
        }
        MemWrite32(addr, val);
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

    // -------------------------------------------------------------------------
    // C1 multi-thread context scaffolding (DETPS2_IOP_THREADS)
    // -------------------------------------------------------------------------

    /// <summary>
    /// True when multi-context save/restore is active for this IOP instance.
    /// Default follows <see cref="MultiThreadEnvEnabled"/>; tests may call
    /// <see cref="EnableMultiThreadScaffolding"/>.
    /// </summary>
    public bool MultiThreadEnabled => _multiThreadEnabled;

    /// <summary>Current cooperative thread id (always 0 when multi-thread is off).</summary>
    public int CurrentThreadId => _multiThreadEnabled ? _currentThreadId : 0;

    /// <summary>Number of in-use contexts (1 when multi-thread off = the single flat GPR set).</summary>
    public int ThreadCount
    {
        get
        {
            if (!_multiThreadEnabled || _threads == null) return 1;
            int n = 0;
            for (int i = 0; i < _threads.Length; i++)
                if (_threads[i].InUse) n++;
            return n;
        }
    }

    /// <summary>
    /// Enable multi-context scaffolding after construction (tests / diagnostics).
    /// Product path: set <c>DETPS2_IOP_THREADS=1</c> before process start.
    /// C1.3 yield hooks become live; automatic Step-path RR is not enabled (explicit only).
    /// </summary>
    public void EnableMultiThreadScaffolding()
    {
        _multiThreadEnabled = true;
        EnsureThreadTable();
    }

    /// <summary>
    /// Create a secondary context with a unique SP in the reserved stack region.
    /// Returns thread id, or -1 if multi-thread is off / table full.
    /// Dual-context stub for slice 1; full THREADMAN CreateThread later.
    /// </summary>
    public int CreateSecondaryContext(uint entryPc)
    {
        if (!_multiThreadEnabled) return -1;
        EnsureThreadTable();
        // Find free slot ≥ 1; stack top = base + (slot+1)*size (grows down into the slot).
        for (int id = 1; id < MaxIopThreadSlots; id++)
        {
            if (_threads![id].InUse) continue;
            uint stackTop = ThreadStackRegionBase + (uint)(id + 1) * ThreadStackSlotSize;
            // Prefer dedicated module-entry arena for low slot ids (C1.2): first
            // MaxModuleEntryStacks secondaries land in [ModuleEntryStackArenaBase, ThreadStackRegionBase).
            if (id <= MaxModuleEntryStacks)
                stackTop = ModuleEntryStackArenaBase + (uint)id * ThreadStackSlotSize;
            return InitThreadSlot(id, entryPc, stackTop, ThreadStackSlotSize, IopThreadStatus.Ready);
        }
        return -1;
    }

    /// <summary>
    /// Create a thread context with an explicit stack top (caller owns uniqueness).
    /// Returns id or -1 when disabled / full. Does not switch to the new context.
    /// </summary>
    public int CreateThreadContext(uint entryPc, uint stackTop, uint stackSize = ThreadStackSlotSize)
    {
        if (!_multiThreadEnabled) return -1;
        EnsureThreadTable();
        for (int id = 1; id < MaxIopThreadSlots; id++)
        {
            if (_threads![id].InUse) continue;
            return InitThreadSlot(id, entryPc, stackTop, stackSize, IopThreadStatus.Ready);
        }
        return -1;
    }

    /// <summary>
    /// C1.2: bind or re-arm a secondary context for a module <c>_start</c> with a unique stack.
    /// Reuses <paramref name="existingTid"/> when still in-use; otherwise allocates a new slot
    /// with SP from the module-entry stack arena (then ThreadStackRegion). Optionally switches
    /// to the bound context so live PC/SP match the entry. Returns thread id, or -1 if off/full.
    /// </summary>
    public int BindModuleEntryContext(int existingTid, uint entryPc, bool switchTo, out uint stackTop)
    {
        stackTop = 0;
        if (!_multiThreadEnabled) return -1;
        EnsureThreadTable();

        int tid = existingTid;
        if (tid < 1 || tid >= MaxIopThreadSlots || !_threads![tid].InUse)
        {
            tid = CreateSecondaryContext(entryPc);
            if (tid < 1) return -1;
        }
        else
        {
            // Re-arm: keep unique SP, reset PC/GPRs for a fresh _start (caller sets a0/gp/ra).
            var t = _threads![tid];
            stackTop = t.StackTop != 0 ? t.StackTop : ModuleEntryStackArenaBase + (uint)tid * ThreadStackSlotSize;
            Array.Clear(t.Gprs);
            t.PC = entryPc;
            t.Gprs[29] = stackTop;
            t.Gprs[30] = stackTop;
            t.HI = t.LO = 0;
            t.Status = IopThreadStatus.Ready;
            if (tid == _currentThreadId)
            {
                // Live set must match re-arm when already on this context.
                PC = entryPc;
                for (int i = 0; i < 32; i++) _gprs[i] = 0;
                _gprs[29] = stackTop;
                _gprs[30] = stackTop;
                HI = LO = 0;
            }
        }

        if (!TryGetThreadContext(tid, out var ctx) || ctx == null)
            return -1;
        stackTop = ctx.StackTop;
        if (switchTo && tid != _currentThreadId)
            SwitchToThread(tid);
        return tid;
    }

    /// <summary>
    /// Unique stack top from the module-entry arena (rotating). Used only when the thread
    /// table is full but multi-thread is on — still avoids sharing DefaultModuleStack.
    /// </summary>
    public uint NextModuleEntryStackTop(ref int slotCounter)
    {
        int slot = slotCounter++ % MaxModuleEntryStacks;
        // Tops at base+(slot+1)*size → [0x1B2000 .. 0x1C0000]
        return ModuleEntryStackArenaBase + (uint)(slot + 1) * ThreadStackSlotSize;
    }

    /// <summary>
    /// Save live PC/GPRs/HI/LO into the current context, load <paramref name="tid"/> onto the live set.
    /// No-op success if already on <paramref name="tid"/>. Returns false if multi-thread off or bad id.
    /// COP0 / branch-pending state is not switched (shared exception state for slice 1).
    /// </summary>
    public bool SwitchToThread(int tid)
    {
        if (!_multiThreadEnabled || _threads == null) return false;
        if ((uint)tid >= (uint)MaxIopThreadSlots || !_threads[tid].InUse) return false;
        if (tid == _currentThreadId) return true;

        SaveLiveToContext(_threads[_currentThreadId]);
        if (_threads[_currentThreadId].Status == IopThreadStatus.Run)
            _threads[_currentThreadId].Status = IopThreadStatus.Ready;

        LoadContextToLive(_threads[tid]);
        _threads[tid].Status = IopThreadStatus.Run;
        _currentThreadId = tid;
        return true;
    }

    /// <summary>Look up a context by id. False when multi-thread off or slot unused.</summary>
    public bool TryGetThreadContext(int tid, out IopThreadContext? ctx)
    {
        ctx = null;
        if (!_multiThreadEnabled || _threads == null) return false;
        if ((uint)tid >= (uint)MaxIopThreadSlots || !_threads[tid].InUse) return false;
        // Keep current slot's saved view coherent for readers that inspect without switching.
        if (tid == _currentThreadId)
            SaveLiveToContext(_threads[tid]);
        ctx = _threads[tid];
        return true;
    }

    // -------------------------------------------------------------------------
    // C1.3 yield hooks (DETPS2_IOP_THREADS) — WaitSema / SleepThread-shaped
    // -------------------------------------------------------------------------

    /// <summary>
    /// Round-robin search for a runnable peer (READY, or stray RUN on a non-current slot).
    /// Starts after <paramref name="afterId"/> (default: current). Returns −1 when multi-thread
    /// is off or no peer is runnable. Does not modify state.
    /// </summary>
    public int FindNextReadyThread(int afterId = -1)
    {
        if (!_multiThreadEnabled || _threads == null) return -1;
        int start = afterId >= 0 ? afterId : _currentThreadId;
        for (int i = 1; i <= MaxIopThreadSlots; i++)
        {
            int id = (start + i) % MaxIopThreadSlots;
            if (!_threads[id].InUse || id == _currentThreadId) continue;
            var st = _threads[id].Status;
            if (st == IopThreadStatus.Ready || st == IopThreadStatus.Run)
                return id;
        }
        return -1;
    }

    /// <summary>
    /// Cooperative yield: leave current as READY and switch to the next READY peer (RR).
    /// No-op (false) when multi-thread is off or no other READY exists — zero flag-off impact.
    /// Does not model THREADMAN priorities.
    /// </summary>
    public bool YieldToReady()
    {
        if (!_multiThreadEnabled || _threads == null) return false;
        int next = FindNextReadyThread(_currentThreadId);
        if (next < 0) return false;
        return SwitchToThread(next);
    }

    /// <summary>
    /// WaitSema / SleepThread-shaped park: mark current WAIT, switch to a READY peer if any.
    /// When a peer exists: returns true and runs that peer. When alone: marks current WAIT,
    /// remains on the live set, returns false (caller is parked with no one to run).
    /// Flag off: always false, no status writes.
    /// Sema counts / wait-object ids are not modeled (C1.4+ / real THREADMAN).
    /// </summary>
    public bool ParkAndYieldToReady()
    {
        if (!_multiThreadEnabled || _threads == null) return false;
        EnsureThreadTable();

        int next = FindNextReadyThread(_currentThreadId);
        SaveLiveToContext(_threads![_currentThreadId]);
        _threads[_currentThreadId].Status = IopThreadStatus.Wait;

        if (next < 0)
            return false;

        LoadContextToLive(_threads[next]);
        _threads[next].Status = IopThreadStatus.Run;
        _currentThreadId = next;
        return true;
    }

    /// <summary>
    /// WaitSema-shaped alias for <see cref="ParkAndYieldToReady"/> (C1.3).
    /// Host / future IOP thsema HLE can call this when a wait would block under multi-thread.
    /// </summary>
    public bool WaitSemaYieldHook() => ParkAndYieldToReady();

    /// <summary>
    /// SleepThread-shaped alias for <see cref="ParkAndYieldToReady"/> (C1.3).
    /// Same park semantics as WaitSema for this slice (no wakeup-count yet).
    /// </summary>
    public bool SleepThreadYieldHook() => ParkAndYieldToReady();

    /// <summary>
    /// Wake a parked context: WAIT (or DORMANT) → READY. No switch. Returns false when
    /// multi-thread is off or the slot is unused. Current thread is left RUN if <paramref name="tid"/>
    /// is current.
    /// </summary>
    public bool ReadyThread(int tid)
    {
        if (!_multiThreadEnabled || _threads == null) return false;
        if ((uint)tid >= (uint)MaxIopThreadSlots || !_threads[tid].InUse) return false;
        if (tid == _currentThreadId)
        {
            _threads[tid].Status = IopThreadStatus.Run;
            return true;
        }
        _threads[tid].Status = IopThreadStatus.Ready;
        return true;
    }

    /// <summary>
    /// Status of a context (None when multi-thread off / unused). Current slot is synced first.
    /// </summary>
    public IopThreadStatus GetThreadStatus(int tid)
    {
        if (!_multiThreadEnabled || _threads == null) return IopThreadStatus.None;
        if ((uint)tid >= (uint)MaxIopThreadSlots || !_threads[tid].InUse) return IopThreadStatus.None;
        if (tid == _currentThreadId)
            SaveLiveToContext(_threads[tid]);
        return _threads[tid].Status;
    }

    // -------------------------------------------------------------------------
    // C1.4 real SIF RPC dispatch compose (DETPS2_IOP_THREADS + live registry)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Dedicated RealSifRpc dispatch thread id when multi-thread is on (−1 when unused / off).
    /// Diagnostics / smokes only.
    /// </summary>
    public int RpcDispatchThreadId =>
        _multiThreadEnabled && _rpcDispatchThreadId >= 1 ? _rpcDispatchThreadId : -1;

    /// <summary>
    /// C1.4: switch onto a dedicated mid-quantum RealSifRpc dispatch context so handler
    /// <see cref="Step"/> quanta do not clobber the caller's multi-thread slot (parent
    /// GPRs/PC stay in the thread table via <see cref="SwitchToThread"/>).
    /// <para>
    /// Returns true when switched — caller must pair with <see cref="LeaveRealRpcDispatch"/>.
    /// Flag-off, table-full, or already-on-dispatch: false (caller uses classic in-place
    /// GPR save/restore; zero behavior change vs pre-C1.4 single-context path).
    /// </para>
    /// </summary>
    public bool TryEnterRealRpcDispatch(out int previousThreadId, uint scratchStackTop = RealRpcDispatchStackTop)
    {
        previousThreadId = _currentThreadId;
        if (!_multiThreadEnabled) return false;
        EnsureThreadTable();
        previousThreadId = _currentThreadId;

        int tid = _rpcDispatchThreadId;
        if (tid < 1 || tid >= MaxIopThreadSlots || !_threads![tid].InUse)
        {
            tid = CreateThreadContext(0, scratchStackTop, ThreadStackSlotSize);
            if (tid < 1) return false;
            _rpcDispatchThreadId = tid;
        }
        else
        {
            // Re-arm: clean GPRs + SP for a fresh SifRpcFunc_t call.
            var t = _threads![tid];
            Array.Clear(t.Gprs);
            t.PC = 0;
            t.Gprs[29] = scratchStackTop;
            t.Gprs[30] = scratchStackTop;
            t.HI = t.LO = 0;
            t.StackTop = scratchStackTop;
            t.StackSize = ThreadStackSlotSize;
            t.Status = IopThreadStatus.Ready;
            if (tid == _currentThreadId)
            {
                for (int i = 0; i < 32; i++) _gprs[i] = 0;
                _gprs[29] = scratchStackTop;
                _gprs[30] = scratchStackTop;
                HI = LO = 0;
                PC = 0;
            }
        }

        // Same slot as caller → classic in-place path (no nested switch needed).
        if (tid == previousThreadId) return false;
        return SwitchToThread(tid);
    }

    /// <summary>
    /// C1.4: restore the thread that was current before <see cref="TryEnterRealRpcDispatch"/>.
    /// No-op when multi-thread is off or <paramref name="previousThreadId"/> is already current.
    /// Captures reply GPRs from the dispatch context <b>before</b> calling this.
    /// </summary>
    public void LeaveRealRpcDispatch(int previousThreadId)
    {
        if (!_multiThreadEnabled || _threads == null) return;
        if ((uint)previousThreadId >= (uint)MaxIopThreadSlots || !_threads[previousThreadId].InUse)
            return;
        if (previousThreadId == _currentThreadId) return;
        SwitchToThread(previousThreadId);
        // Dispatch slot is Ready after SwitchToThread demotes the left context.
        if (_rpcDispatchThreadId >= 1 &&
            (uint)_rpcDispatchThreadId < (uint)MaxIopThreadSlots &&
            _threads[_rpcDispatchThreadId].InUse &&
            _rpcDispatchThreadId != _currentThreadId)
        {
            _threads[_rpcDispatchThreadId].Status = IopThreadStatus.Ready;
        }
    }

    private void EnsureThreadTable()
    {
        if (_threads != null) return;
        _threads = new IopThreadContext[MaxIopThreadSlots];
        for (int i = 0; i < MaxIopThreadSlots; i++)
            _threads[i] = new IopThreadContext { Id = i };
        // Boot context 0 = current live single-context state.
        var boot = _threads[0];
        boot.InUse = true;
        boot.Status = IopThreadStatus.Run;
        SaveLiveToContext(boot);
        // Match IopModuleHost.DefaultModuleStack (0x1F0000) when live SP is still zero.
        boot.StackTop = boot.Sp != 0 ? boot.Sp : 0x001F0000u;
        boot.StackSize = ThreadStackSlotSize;
        _currentThreadId = 0;
        _nextThreadSlot = 1;
    }

    private void ResetThreadTable()
    {
        _currentThreadId = 0;
        _nextThreadSlot = 1;
        _rpcDispatchThreadId = -1;
        if (!_multiThreadEnabled)
        {
            // Drop any table so a later Enable re-captures live state; no alloc when still off.
            _threads = null;
            return;
        }
        if (_threads == null)
        {
            // Lazy: only allocate if still enabled (env was set at start).
            // Avoid ctor-path alloc until first Create/Switch when env-enabled but unused.
            return;
        }
        for (int i = 0; i < _threads.Length; i++)
        {
            var t = _threads[i];
            t.InUse = false;
            t.Status = IopThreadStatus.Dormant;
            t.PC = 0;
            Array.Clear(t.Gprs);
            t.HI = t.LO = 0;
            t.StackTop = 0;
            t.StackSize = 0;
        }
        var boot = _threads[0];
        boot.InUse = true;
        boot.Status = IopThreadStatus.Run;
        SaveLiveToContext(boot);
        boot.StackTop = 0x001F0000u; // IopModuleHost.DefaultModuleStack
        boot.StackSize = ThreadStackSlotSize;
    }

    private int InitThreadSlot(int id, uint entryPc, uint stackTop, uint stackSize, IopThreadStatus status)
    {
        var t = _threads![id];
        t.InUse = true;
        t.Status = status;
        t.PC = entryPc;
        Array.Clear(t.Gprs);
        t.Gprs[29] = stackTop; // $sp
        t.Gprs[30] = stackTop; // $fp convention (matches PrepareModuleEntry)
        t.HI = 0;
        t.LO = 0;
        t.StackTop = stackTop;
        t.StackSize = stackSize;
        if (id >= _nextThreadSlot) _nextThreadSlot = id + 1;
        return id;
    }

    private void SaveLiveToContext(IopThreadContext t)
    {
        t.PC = PC;
        for (int i = 0; i < 32; i++)
            t.Gprs[i] = _gprs[i];
        t.Gprs[0] = 0;
        t.HI = HI;
        t.LO = LO;
    }

    private void LoadContextToLive(IopThreadContext t)
    {
        PC = t.PC;
        for (int i = 0; i < 32; i++)
            _gprs[i] = t.Gprs[i];
        _gprs[0] = 0;
        HI = t.HI;
        LO = t.LO;
    }
}
