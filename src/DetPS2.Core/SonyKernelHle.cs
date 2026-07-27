using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Sony EE kernel syscall ABI (psdevwiki / ps2sdk syscallnr.h).
/// Used for commercial titles when a real BIOS is present.
/// Numbers intentionally differ from DetPS2 homebrew HLE helpers.
/// </summary>
public sealed class SonyKernelHle
{
    private readonly Ps2System _system;
    private readonly KernelState _kernel;
    private readonly Dictionary<int, uint> _intcHandlers = new();
    private readonly Dictionary<int, uint> _dmacHandlers = new();
    private readonly uint[] _sifRegs = new uint[32];
    private readonly Dictionary<uint, uint> _customSyscalls = new();
    private uint _gsImr = 0xFF00;
    private bool _stubsInstalled;
    private const uint StubBase = 0x00081000;
    // Top of usable RDRAM for heap purposes — leaves room below the top-of-RAM stack
    // region real hardware reserves. Shared by SetupHeap (0x3D) and EndOfHeap (0x3E)
    // so both syscalls agree on where the heap ends.
    private const uint HeapTop = 0x01FFF000;
    private int _stubSlots;
    private readonly RealSifRpc _realRpc = new();
    /// <summary>Deci2Open handler slots: (device, bufferAddr) per allocated id, or null if free.
    /// Matches Play!'s Deci2HandlerList — a small fixed pool is realistic (real games open one
    /// or two DECI2 channels, e.g. stdout/stderr, at most).</summary>
    private readonly (uint device, uint bufferAddr)?[] _deci2Handlers = new (uint, uint)?[8];
    public RealSifRpc RealRpc => _realRpc;

    /// <summary>Looks up a game-registered AddIntcHandler entry so EmotionEngine can
    /// dispatch directly to it instead of the synthesized (no-op) interrupt vector.</summary>
    public bool TryGetIntcHandler(int cause, out uint handlerAddr) =>
        _intcHandlers.TryGetValue(cause, out handlerAddr);

    /// <summary>Looks up a game-registered AddDmacHandler entry (keyed by DMA channel, e.g.
    /// DMA_CHANNEL_SIF0=5) — real hardware routes DMA-channel completion here, not through
    /// AddIntcHandler; e.g. ps2sdk's sceSifInitCmd installs _SifCmdIntHandler this way.</summary>
    public bool TryGetDmacHandler(int channel, out uint handlerAddr) =>
        _dmacHandlers.TryGetValue(channel, out handlerAddr);

    public ulong Handled { get; private set; }
    public ulong Unknown { get; private set; }
    /// <summary>Last few Sony syscall numbers (ring) for boot diagnostics.</summary>
    public uint[] RecentSyscalls { get; } = new uint[32];
    private int _recentSyscallIdx;
    public ulong SifDmaCalls { get; private set; }
    public ulong SifGetRegCalls { get; private set; }
    private readonly Dictionary<uint, int> _syscallHistogram = new();
    public IReadOnlyDictionary<uint, int> SyscallHistogram => _syscallHistogram;

    public SonyKernelHle(Ps2System system, KernelState kernel)
    {
        _system = system;
        _kernel = kernel;
    }

    public void Reset()
    {
        _intcHandlers.Clear();
        _dmacHandlers.Clear();
        Array.Clear(_sifRegs);
        _customSyscalls.Clear();
        _findCache.Clear();
        _midwayPairPlanted = false;
        _gsImr = 0xFF00;
        _stubsInstalled = false;
        _stubSlots = 0;
        Handled = Unknown = 0;
        SifDmaCalls = SifGetRegCalls = 0;
        _recentSyscallIdx = 0;
        Array.Clear(RecentSyscalls);
        _syscallHistogram.Clear();
        _realRpc.Reset();
    }

    public bool TryHandle(EmotionEngine ee, uint num, out long result)
    {
        // Negative numbers = i* interrupt-safe variants; treat same as positive
        int signed = unchecked((int)num);
        if (signed < 0)
            num = (uint)(-signed);

        RecentSyscalls[_recentSyscallIdx++ & 31] = num;
        _syscallHistogram[num] = _syscallHistogram.GetValueOrDefault(num) + 1;

        uint a0 = (uint)ee.GetGpr(4).Lo;
        uint a1 = (uint)ee.GetGpr(5).Lo;
        uint a2 = (uint)ee.GetGpr(6).Lo;
        uint a3 = (uint)ee.GetGpr(7).Lo;

        // Note: SetSyscall hooks are recorded but not live-redirected yet.
        // Live redirect needs careful interaction with game handlers; HLE covers 0x5A Copy etc.

        result = 0;
        bool handled = true;

        // Live-redirect game-installed syscalls (SetSyscall). Skip numbers we must HLE
        // for boot survival (thread/sema/cache/SIF ready). Redirect the rest so Midway
        // custom handlers (often graph / file / RPC glue) actually run.
        if (_customSyscalls.TryGetValue(num, out uint hook) && hook != 0 && !IsHleForcedSyscall(num))
        {
            uint phys = hook & 0x1FFFFFFFu;
            if (phys >= 0x100000u && phys < SystemMemory.RDRAM_SIZE)
            {
                // Midway FindAddress hook: plant CRT0 success patch before entering it
                if (num == 0x83)
                    ForcePlantMidwayPair();
                ee.HleRedirectPc = hook;
                result = 0;
                Handled++;
                return true;
            }
        }

        switch (num)
        {
            case 0x00: // RFU000 FullReset
            case 0x01: // ResetEE
                result = 0;
                break;
            case 0x02: // SetGsCrt(interlace, mode, ffmd)
                _system.Hle.CrtMode = a1;
                // Ensure display looks "on" for present path
                _system.Gs.WritePrivileged64(0x12000000, 1); // PMODE EN1
                result = 0;
                break;
            case 0x03:
                result = 0;
                break;
            case 0x04: // Exit
                _system.Hle.RequestExit((int)a0);
                result = 0;
                break;
            case 0x05:
            case 0x08:
            case 0x09:
                result = 0;
                break;
            case 0x06: // LoadExecPS2 — not fully supported
            case 0x07: // ExecPS2
                result = -1;
                break;

            // ---- INTC / DMAC enable ----
            case 0x0A: // AddSbusIntcHandler
            case 0x0B: // RemoveSbusIntcHandler
            case 0x0C: // Interrupt2Iop
            case 0x0D:
            case 0x0E:
            case 0x0F:
                result = 0;
                break;
            case 0x10: // AddIntcHandler(cause, handler, next, arg, flag)
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_HANDLERS") == "1")
                    Console.Error.WriteLine($"[ADDINTC] cause={a0} handler=0x{a1:X8}");
                _intcHandlers[(int)a0] = a1;
                result = (int)a0; // handler id
                // KernelBootstrap deliberately leaves EE.TakeExceptions off after fast-boot
                // ("without a full ISR that ACKs INTC, VBlank would storm the EE... games
                // that install their own handlers via AddIntcHandler can enable later") but
                // never actually flips it back on anywhere — the real, general fix belongs
                // here: once the game has installed its own handler for a cause, it owns
                // acknowledging that interrupt, so it's safe (and necessary — this is the
                // only thing that lets any IRQ-driven wait, e.g. real SIF_CMD_INIT_CMD
                // handshakes, ever resolve instead of spinning forever) to start taking
                // exceptions.
                _system.EE.TakeExceptions = true;
                break;
            case 0x11: // RemoveIntcHandler
                _intcHandlers.Remove((int)a0);
                result = 0;
                break;
            case 0x12: // AddDmacHandler
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_HANDLERS") == "1")
                    Console.Error.WriteLine($"[ADDDMAC] channel={a0} handler=0x{a1:X8}");
                _dmacHandlers[(int)a0] = a1;
                result = (int)a0;
                break;
            case 0x13:
                _dmacHandlers.Remove((int)a0);
                result = 0;
                break;
            case 0x14: // EnableIntc
            case 0x15: // DisableIntc
            case 0x16: // EnableDmac
            case 0x17: // DisableDmac
                result = 1;
                break;
            case 0x18: // SetAlarm
            case 0x19: // ReleaseAlarm
            case 0xFC:
            case 0xFE:
                result = 0;
                break;

            // ---- Threads (Sony) ----
            case 0x20: // CreateThread(ee_thread_t*)
                result = CreateThreadFromStruct(a0);
                // Do not auto-start: Midway's worker needs globals filled first.
                // StartThread (if called) or a late commercial assist will start it.
                break;
            case 0x21: // DeleteThread
                result = _kernel.DeleteThread((int)a0);
                break;
            case 0x22: // StartThread(tid, arg)
                result = _kernel.StartAndMaybeSwitch(ee, (int)a0, switchNow: true, arg: a1);
                break;
            case 0x23: // ExitThread
            case 0x24: // ExitDeleteThread
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_EXIT") == "1")
                    Console.Error.WriteLine($"[EXIT] tid={_kernel.CurrentThreadId} pc=0x{ee.PC:X8} ra=0x{ee.GetGpr(31).Lo:X8}");
                _kernel.ExitCurrentThread(); // mark done, permanently — see its own doc comment
                _kernel.SwitchToNext(ee);
                result = 0;
                break;
            case 0x25: // TerminateThread
                result = _kernel.DeleteThread((int)a0);
                break;
            case 0x27: // DisableDispatchThread
            case 0x28: // EnableDispatchThread
                result = 0;
                break;
            case 0x29: // ChangeThreadPriority
                result = 0;
                break;
            case 0x2B: // RotateThreadReadyQueue
                _kernel.SwitchToNext(ee);
                result = 0;
                break;
            case 0x2D: // ReleaseWaitThread
                result = _kernel.WakeupThread((int)a0);
                break;
            case 0x2F: // GetThreadId
                result = _kernel.CurrentThreadId;
                break;
            case 0x30: // ReferThreadStatus(id, ee_thread_status_t* out)
                result = ReferThreadStatus((int)a0, a1);
                break;
            case 0x32: // SleepThread — switch to another runnable thread
                _kernel.SleepThread();
                if (!_kernel.SwitchToNext(ee))
                {
                    // No other runnable thread: self-wake so boot never deadlocks
                    _kernel.WakeupThread(_kernel.CurrentThreadId);
                }
                result = 0;
                break;
            case 0x33: // WakeupThread
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_WAKEUP") == "1")
                    Console.Error.WriteLine($"[WAKEUP] from tid={_kernel.CurrentThreadId} target={a0} cyc={_system.MasterCycles}");
                result = _kernel.WakeupThread((int)a0);
                break;
            case 0x35: // CancelWakeupThread
            case 0x37: // SuspendThread
            case 0x39: // ResumeThread
            case 0x3B: // JoinThread
                result = 0;
                break;
            case 0x3C: // SetupThread
                result = SetupThread(a0, a1, a2, a3);
                break;
            case 0x3D: // SetupHeap
                // NOTE: tried returning a real heap-end pointer here (matching EndOfHeap) on
                // the theory that a null return corrupts newlib's malloc bookkeeping — tested
                // empirically against the cyc~1,381,616 stack-corruption repro (see #7.4) and
                // it made no difference (identical failure, same cycle, same PC), so the
                // return value doesn't appear to be what's consumed here. Reverted to the
                // known, unverified-but-harmless prior behavior rather than keep an unproven
                // guess; left this note so the theory isn't silently retried later.
                result = 0;
                break;
            case 0x3E: // EndOfHeap — return top of usable RDRAM (titles poll this)
                result = HeapTop;
                break;

            // ---- Semaphores (Sony: a0 = ee_sema_t*) ----
            case 0x40: // CreateSema
                result = CreateSemaFromStruct(a0);
                break;
            case 0x41: // DeleteSema
                result = _kernel.DeleteSema((int)a0);
                break;
            case 0x42: // SignalSema
                result = _kernel.SignalSema((int)a0);
                break;
            case 0x44: // WaitSema — block + yield to another thread when empty
                {
                    // Auto-create missing semas (titles sometimes Wait before Create races).
                    // Must be a non-mutating existence check — WaitSemaBlocking decrements the
                    // count as a side effect on success, so probing with it here would silently
                    // consume a legitimate signal (e.g. one our own synchronous SIF RPC handling
                    // just posted) before the real wait below ever sees it, forcing a spurious
                    // block on every semaphore that starts at count 1.
                    if (a0 != 0 && !_kernel.SemaExists((int)a0))
                    {
                        _kernel.CreateSema(0, 1);
                        // fall through with new id only if a0 was out of range — use given id map
                    }
                    int wr = _kernel.WaitSemaBlocking((int)a0);
                    if (_kernel.LastWaitSemaBlocked)
                    {
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                            Console.Error.WriteLine($"[RPC] WaitSema BLOCKED a0(sema)=0x{a0:X} pc=0x{ee.PC:X8}");
                        if (!_kernel.SwitchToNext(ee))
                        {
                            // Nobody else runnable: park on VBlank instead of busy-spin.
                            // Next PCRTC VBlank wakes us so the frame loop can progress.
                            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                                Console.Error.WriteLine($"[RPC] WaitSema FABRICATING signal for sema=0x{a0:X} (no runnable thread)");
                            _kernel.WaitSemaVblank();
                            _kernel.SignalSema((int)a0);
                            _kernel.WakeupThread(_kernel.CurrentThreadId);
                            wr = 0;
                        }
                        result = wr < 0 ? 0 : wr;
                    }
                    else result = wr < 0 ? 0 : wr;
                }
                break;
            case 0x45: // PollSema — non-blocking (never sleep)
                {
                    int pr = _kernel.WaitSemaBlocking((int)a0);
                    if (_kernel.LastWaitSemaBlocked)
                    {
                        var ct = _kernel.GetThread(_kernel.CurrentThreadId);
                        if (ct != null) { ct.Sleeping = false; ct.WaitSemaId = 0; }
                        result = -1;
                    }
                    else result = pr < 0 ? -1 : pr;
                }
                break;
            case 0x47: // ReferSemaStatus
                result = 0;
                break;

            // ---- OSD / GS params ----
            case 0x4A: // SetOsdConfigParam
            case 0x4B: // GetOsdConfigParam
            case 0x4C: // GetGsHParam
            case 0x4D: // GetGsVParam
            case 0x4E: // SetGsHParam
            case 0x4F: // SetGsVParam
                result = 0;
                break;

            // ---- Event flags ----
            case 0x50: // CreateEventFlag
                result = _kernel.CreateEventFlag(a0);
                break;
            case 0x51:
                result = 0;
                break;
            case 0x52: // SetEventFlag
            case 0x53: // iSetEventFlag
                result = _kernel.SetEventFlag((int)a0, a1);
                break;
            case 0x54: // ClearEventFlag
            case 0x55: // iClearEventFlag
                result = _kernel.ClearEventFlag((int)a0, a1);
                break;
            case 0x56: // WaitEventFlag — succeed immediately
            case 0x57: // PollEventFlag
            case 0x58: // iPollEventFlag
                result = (long)_kernel.PollEventFlag((int)a0);
                break;

            // ---- Cache / COP0 / KSeg ----
            case 0x5A: // Copy (or game-hooked via SetSyscall — handled above)
                // Default: memcpy(a0 dest, a1 src, a2 len) style best-effort
                if (a2 > 0 && a2 < 0x100000)
                {
                    for (uint i = 0; i < a2; i++)
                        _system.Memory.Write8(a0 + i, _system.Memory.Read8(a1 + i));
                }
                result = a0;
                break;
            case 0x5B: // GetEntryAddress
                result = AllocStub();
                break;
            case 0x5C: // EnableIntcHandler
            case 0x5D: // DisableIntcHandler
            case 0x5E:
            case 0x5F:
                result = 0;
                break;
            case 0x60: // KSeg0
            case 0x61: // EnableCache
            case 0x62: // DisableCache
                result = 0;
                break;
            case 0x63: // GetCop0
                result = (long)ee.ReadCop0Public((int)a0);
                break;
            case 0x64: // FlushCache
            case 0x66: // CpuConfig
                result = 0;
                break;

            // ---- SIF ----
            case 0x6B: // SifStopDma
                result = 0;
                break;
            case 0x6C: // SetCPUTimerHandler
            case 0x6D: // SetCPUTimer
            case 0x6E:
            case 0x6F:
                result = 0;
                break;
            case 0x70: // GsGetIMR
                result = _gsImr;
                break;
            case 0x71: // GsPutIMR
                _gsImr = a0;
                _system.Gs.WritePrivileged64(0x12001010, a0);
                result = 0;
                break;
            case 0x72: // SetPgifHandler
            case 0x73: // SetVSyncFlag
                result = 0;
                break;
            case 0x74: // SetSyscall(num, addr)
                // Return previous handler (0 if none) — games check this
                result = _customSyscalls.TryGetValue(a0, out uint prev) ? prev : 0;
                if (a1 != 0)
                    _customSyscalls[a0] = a1;
                else
                    _customSyscalls.Remove(a0);
                break;
            case 0x75: // print
                result = 0;
                break;
            case 0x76: // SifDmaStat — -1 = completed / idle
                result = -1;
                break;
            case 0x77: // SifSetDma(SifDmaTransfer_t* sdd, int count)
                SifDmaCalls++;
                result = PerformSifSetDma(a0, a1);
                break;
            case 0x78: // SifSetDChain
                result = 0;
                break;
            case 0x79: // SifSetReg(reg, val)
                if (a0 < _sifRegs.Length) _sifRegs[a0] = a1;
                // Mirror MSFLAG onto SIF MMIO (offset 0x20). Do not write MAINADDR
                // through MsCom (that would enqueue a fake SBUS command).
                if (a0 == 3) _system.Sif.WriteRegister(0x20, a1);
                result = 0;
                break;
            case 0x7A: // SifGetReg
                {
                    SifGetRegCalls++;
                    // Always report IOP alive for commercial fast-boot:
                    // SIF_STAT_SIFINIT|CMDINIT|BOOTEND = 0x70000 on SMFLAG (reg 4)
                    const uint IopReady = 0x10000u | 0x20000u | 0x40000u;
                    if (a0 == 4)
                    {
                        result = IopReady | (_sifRegs.Length > 4 ? _sifRegs[4] : 0);
                        break;
                    }
                    if (a0 == 5) // SUBRESET / legacy ready poll
                    {
                        result = IopReady;
                        break;
                    }
                    if (a0 < _sifRegs.Length) result = _sifRegs[a0];
                    else result = 0;
                }
                break;
            case 0x7B: // ExecOSD
                result = -1;
                break;
            case 0x7C: // Deci2Call(function=a0, param=a1) — real sub-dispatch, per ps2sdk/Play!'s
                       // CPS2OS::sc_Deci2Call. Previously always returned 0 regardless of function
                       // or param, which never touches the caller-supplied DECI2BUFFER struct's
                       // status fields — a game whose debug-output retry loop polls that struct for
                       // "link ready" (Deci2Poll) or "send complete" (Deci2Send's status0) would
                       // never see it change and retry indefinitely. Confirmed exactly this: traced
                       // an ~197,000-call storm (each recomputing a CRC over a ~10-byte outgoing
                       // debug packet) back to this stub. DECI2BUFFER layout (0x14 bytes:
                       // unknown0@0, status0@4, unknown1@8, status1@0xC, dataAddr@0x10) and
                       // DECI2SEND layout (size@0, data@0xC) confirmed against Play!'s PS2OS.cpp.
                result = Deci2Call(a0, a1);
                break;
            case 0x7D: // PSMode
                result = 0;
                break;
            case 0x7E: // MachineType
                result = 0; // consumer
                break;
            case 0x7F: // GetMemorySize
                result = SystemMemory.RDRAM_SIZE;
                break;
            case 0x80: // GetGsDxDyOffset
                result = 0;
                break;
            case 0x82: // InitTLB
                result = 0;
                break;
            case 0x83: // FindAddress — commercial code uses (start, end, needle) memory scan
                result = FindAddressScan(a0, a1, a2);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_FINDADDR") == "1")
                    Console.Error.WriteLine($"[FINDADDR] start=0x{a0:X8} end=0x{a1:X8} needle=0x{a2:X8} -> 0x{result:X8} pc=0x{ee.PC:X8} ra=0x{ee.GetGpr(31).Lo:X8}");
                break;
            case 0x85: // SetMemoryMode
            case 0x86: // GetMemoryMode
                result = 0;
                break;
            case 0x87: // ExecPSX
                result = -1;
                break;

            default:
                handled = false;
                Unknown++;
                break;
        }

        if (handled) Handled++;
        return handled;
    }

    /// <summary>
    /// Syscalls we always HLE even if the title installed a SetSyscall hook.
    /// Keeps cooperative threading / SIF ready bits / WaitSema alive under commercial boot.
    /// </summary>
    private static bool IsHleForcedSyscall(uint num) => num switch
    {
        0x20 or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 => true, // threads create/start/exit
        0x2B or 0x2F or 0x32 or 0x33 => true, // rotate/id/sleep/wakeup
        0x3C or 0x3D or 0x3E => true, // SetupThread/Heap
        0x40 or 0x41 or 0x42 or 0x44 or 0x45 => true, // semas
        0x64 => true, // FlushCache
        0x74 => true, // SetSyscall itself
        0x76 or 0x77 or 0x79 or 0x7A => true, // SIF dma/reg (need ready bits)
        0x83 => true, // FindAddress — HLE scan + Midway CRT0 plant (game hook loops)
        _ => false
    };

    public IReadOnlyDictionary<uint, uint> CustomSyscalls => _customSyscalls;

    /// <summary>
    /// Perform SifSetDma transfers. Layout (SifDmaTransfer_t, 16 bytes each):
    /// +0 src, +4 dest, +8 size, +12 attr. Attr bit0: 0=EE→IOP, 1=IOP→EE (common SDK).
    /// After EE→IOP, run lightweight SIFCMD HLE so retail boot gets IOP replies.
    /// Two passes: raw copies first, then SIFCMD interpretation — a real RPC call's
    /// argument buffer is often sent as a second descriptor in the SAME batch as the
    /// call packet, so it must already be in place before we read it back.
    /// </summary>
    private long PerformSifSetDma(uint listAddr, uint count)
    {
        if (listAddr == 0 || count == 0) return 0;
        if (count > 32) count = 32; // safety

        Span<uint> srcs = stackalloc uint[32];
        Span<uint> sizes = stackalloc uint[32];
        Span<bool> eeToIop = stackalloc bool[32];

        for (uint i = 0; i < count; i++)
        {
            uint baseAddr = listAddr + i * 16;
            uint src = _system.Memory.Read32(baseAddr);
            uint dest = _system.Memory.Read32(baseAddr + 4);
            uint size = _system.Memory.Read32(baseAddr + 8);
            uint attr = _system.Memory.Read32(baseAddr + 12);
            srcs[(int)i] = src;
            sizes[(int)i] = size;
            eeToIop[(int)i] = (attr & 1) == 0;
            if (size == 0 || size > 0x200000) continue;
            if ((attr & 1) != 0)
                _system.Sif.Sif0IopToEe(src, dest, size);
            else
                _system.Sif.Sif1EeToIop(src, dest, size);
        }

        for (uint i = 0; i < count; i++)
        {
            if (!eeToIop[(int)i] || sizes[(int)i] < 16 || sizes[(int)i] > 0x200000) continue;
            HleSifCmdFromEe(srcs[(int)i], sizes[(int)i]);
        }

        _system.Sif.Step(64);
        // Mark SMFLAG that IOP saw the transfer (retail polls this)
        _system.Sif.WriteRegister(0x30, _system.Sif.ReadRegister(0x30) | 0x10000u);
        // Return a non-zero DMA id; -1 from SifDmaStat means complete
        return unchecked((int)(1 + (count & 0x7FFF)));
    }

    /// <summary>
    /// Minimal SIFCMD HLE: parse EE-built command packet and synthesize IOP-side
    /// completion so sceSif* / Midway cmd handlers can advance.
    /// Header (16 bytes): +0 sizes, +4 dest, +8 cid, +12 opt.
    /// </summary>
    private void HleSifCmdFromEe(uint eePacket, uint size)
    {
        // Real RPC bind/call (cid 0x80000009/0x8000000A) — the protocol retail-compiled
        // sifrpc.c actually speaks. Handled fully (response written + semaphore signaled),
        // so nothing else in this method should run for it.
        if (size >= 16 && _realRpc.TryHandle(_system.Memory, _kernel, _system.Cdvd, _system.Pad, eePacket))
        {
            _system.Intc.Raise(Intc.InterruptSource.Sif);
            return;
        }

        uint word0 = _system.Memory.Read32(eePacket);
        uint dest = _system.Memory.Read32(eePacket + 4);
        uint cid = _system.Memory.Read32(eePacket + 8);
        uint opt = _system.Memory.Read32(eePacket + 12);
        uint psize = word0 & 0xFF;
        uint dsize = word0 >> 8;

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine($"[SIFCMD] cid=0x{cid:X8} dest=0x{dest:X8} opt=0x{opt:X8} psize={psize} dsize={dsize} eePacket=0x{eePacket:X8}");

        // System commands (Sony)
        switch (cid)
        {
            case 0x80000000: // CHANGE_SADDR
            case 0x80000001: // SET_SREG
            case 0x80000002: // INIT_CMD
            case 0x80000003: // RESET_CMD
                // Acknowledge by setting IOP "boot end / cmd init" style flags
                if (cid < _sifRegs.Length)
                    _sifRegs[cid & 0x1F] = opt | 1;
                break;
            default:
                break;
        }

        // Midway / custom: if dest looks like EE buffer, write a success result dword
        if (dest >= 0x100000 && (dest & 0x1FFFFFFFu) < SystemMemory.RDRAM_SIZE)
        {
            // Common pattern: result code at dest, optional payload
            _system.Memory.Write32(dest, 0); // success
            if (dsize >= 4 && dsize < 0x10000)
            {
                // For RPC-like packets, mark completed in first result field
                _system.Memory.Write32(dest + 4, 1);
            }
        }

        // If packet embeds a path-looking string, try to satisfy FILEIO open via HLE
        if (size >= 32 && size < 0x800)
        {
            // Scan for ASCII path after header
            for (uint off = 16; off + 4 < size && off < 128; off++)
            {
                byte c = _system.Memory.Read8(eePacket + off);
                if (c is (byte)'c' or (byte)'C' or (byte)'/' or (byte)'\\' or (byte)'h' or (byte)'H')
                {
                    // Likely host/cdrom path — signal SIF IRQ for waiter
                    break;
                }
            }
        }

        _system.Intc.Raise(Intc.InterruptSource.Sif);
        _ = psize;
        _ = dsize;
        _ = cid;
    }

    private long SetupThread(uint gp, uint stack, uint stackSize, uint args)
    {
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_SETUPTHREAD") == "1")
            Console.Error.WriteLine($"[SETUPTHREAD] gp=0x{gp:X8} stack=0x{stack:X8} stackSize=0x{stackSize:X8} args=0x{args:X8}");
        if (stackSize == 0) stackSize = 0x10000;
        ulong spTop = (ulong)stack + stackSize;
        if (stack >= 0x100000 && spTop <= SystemMemory.RDRAM_SIZE)
            return (long)(spTop & ~0xFUL);
        if (stack > 0x10000 && stack < SystemMemory.RDRAM_SIZE)
            return (long)(stack & ~0xFUL);
        return 0x01FF0000;
    }

    public uint LastCreatedThreadEntry { get; private set; }
    public uint LastCreatedThreadStack { get; private set; }

    // ps2sdk ee_thread_status_t (36B): +0 status, +4 func, +8 stack, +C stack_size,
    // +10 gp_reg, +14 initial_priority, +18 current_priority, +1C attr, +20 option.
    // Real status bitmask (ee/kernel/include/kernel.h): RUN=0x01 READY=0x02 WAIT=0x04
    // SUSPEND=0x08 DORMANT=0x10. Confirmed load-bearing: MK Shaolin Monks' own boot
    // creates a worker thread (entry deep in the SIF-RPC library, likely the SIF command
    // dispatch thread sceSifInitRpc sets up) then immediately calls ReferThreadStatus on
    // it expecting DORMANT (0x10, "created but not started") before it will call
    // StartThread — since this syscall used to be a no-op stub, that check always read
    // stack garbage, took the game's own defensive error path, and StartThread was never
    // called at all, permanently starving whatever that thread was meant to set up.
    private int ReferThreadStatus(int id, uint statusAddr)
    {
        var t = _kernel.GetThread(id);
        if (t == null) return -1;
        uint status = !t.Started ? 0x10u
            : id == _kernel.CurrentThreadId ? 0x01u
            : t.Sleeping ? 0x04u
            : 0x02u;
        if (statusAddr != 0)
        {
            _system.Memory.Write32(statusAddr + 0, status);
            _system.Memory.Write32(statusAddr + 4, t.Entry);
            _system.Memory.Write32(statusAddr + 8, t.Stack);
            _system.Memory.Write32(statusAddr + 12, t.StackSize);
            _system.Memory.Write32(statusAddr + 16, t.Gp);
            _system.Memory.Write32(statusAddr + 20, 0);
            _system.Memory.Write32(statusAddr + 24, 0);
            _system.Memory.Write32(statusAddr + 28, 0);
            _system.Memory.Write32(statusAddr + 32, 0);
        }
        return 0;
    }

    private int CreateThreadFromStruct(uint addr)
    {
        // ps2sdk ee_thread_t:
        // +0 status, +4 func, +8 stack, +C stack_size, +10 gp, +14 initial_priority
        uint func = 0, stack = 0, stackSize = 0, gp = 0;
        if (addr != 0)
        {
            func = _system.Memory.Read32(addr + 0x04);
            stack = _system.Memory.Read32(addr + 0x08);
            stackSize = _system.Memory.Read32(addr + 0x0C);
            gp = _system.Memory.Read32(addr + 0x10);
            // Sanity: func must look like EE code in RDRAM
            if (func < 0x100000 || (func & 0x1FFFFFFFu) >= SystemMemory.RDRAM_SIZE)
            {
                // Older wrong layout fallback: +0C func
                uint alt = _system.Memory.Read32(addr + 0x0C);
                if (alt >= 0x100000 && (alt & 0x1FFFFFFFu) < SystemMemory.RDRAM_SIZE)
                {
                    func = alt;
                    stack = _system.Memory.Read32(addr + 0x10);
                    stackSize = _system.Memory.Read32(addr + 0x14);
                    gp = _system.Memory.Read32(addr + 0x18);
                }
            }
        }
        if (func == 0) func = addr;
        // SP = top of stack (MIPS grows down)
        uint sp = stack;
        if (stackSize > 0 && stackSize < 0x400000 && stack != 0)
            sp = (stack + stackSize) & ~0xFu;
        else if (stack != 0)
            sp = stack & ~0xFu;
        LastCreatedThreadEntry = func;
        LastCreatedThreadStack = sp;
        return _kernel.CreateThread(func, gp, sp, stackSize);
    }

    private int CreateSemaFromStruct(uint addr)
    {
        // ee_sema_t: count@0, max_count@4, init_count@8, option@12
        int init = 1, max = 1;
        if (addr != 0)
        {
            // SDK layouts vary; common: init_count at +8, max at +4
            max = (int)_system.Memory.Read32(addr + 4);
            init = (int)_system.Memory.Read32(addr + 8);
            if (max <= 0) max = (int)_system.Memory.Read32(addr); // alternate
            if (init < 0) init = 0;
            if (max <= 0) max = 1;
            if (init > max) init = max;
        }
        return _kernel.CreateSema(init, max);
    }

    /// <summary>Real Deci2Call sub-dispatch (function/param convention and struct layouts
    /// confirmed against Play!'s CPS2OS::sc_Deci2Call). Debug-link semantics only — no genuine
    /// devkit is attached, so Send/kPuts just surface the text (opt-in trace) rather than
    /// transmitting anywhere; the important part is Poll/Send always updating the caller's
    /// status fields so a real retry loop sees success and stops retrying.</summary>
    private int Deci2Call(uint function, uint param)
    {
        bool trace = Environment.GetEnvironmentVariable("DETPS2_TRACE_DECI2") == "1";
        switch (function)
        {
            case 0x01: // Deci2Open(param->{device, bufferAddr}) -> handler id
            {
                uint device = _system.Memory.Read32(param + 0x00);
                uint bufferAddr = _system.Memory.Read32(param + 0x04);
                for (int i = 0; i < _deci2Handlers.Length; i++)
                {
                    if (_deci2Handlers[i].HasValue) continue;
                    _deci2Handlers[i] = (device, bufferAddr);
                    if (trace) Console.Error.WriteLine($"[DECI2] Open id={i} device=0x{device:X8} bufferAddr=0x{bufferAddr:X8}");
                    return i;
                }
                return -1; // no free slot
            }
            case 0x03: // Deci2Send(param->{id}) — id's buffer->dataAddr points at a DECI2SEND
            {
                uint id = _system.Memory.Read32(param + 0x00);
                if (id >= (uint)_deci2Handlers.Length || !_deci2Handlers[id].HasValue) return 0;
                uint bufferAddr = _deci2Handlers[id]!.Value.bufferAddr;
                uint dataAddr = _system.Memory.Read32(bufferAddr + 0x10);
                if (dataAddr != 0)
                {
                    uint size = _system.Memory.Read32(dataAddr + 0x00);
                    if (trace && size >= 0x0C)
                    {
                        int len = (int)(size - 0x0C);
                        var bytes = new byte[Math.Min(len, 256)];
                        for (int i = 0; i < bytes.Length; i++) bytes[i] = _system.Memory.Read8(dataAddr + 0x0C + (uint)i);
                        Console.Error.WriteLine($"[DECI2] Send id={id} dataAddr=0x{dataAddr:X8} size=0x{size:X}: {System.Text.Encoding.ASCII.GetString(bytes)}");
                    }
                    _system.Memory.Write32(bufferAddr + 0x04, 0); // status0 = 0 (sent)
                }
                else
                {
                    _system.Memory.Write32(bufferAddr + 0x04, unchecked((uint)-1)); // status0 = error
                }
                return 0;
            }
            case 0x04: // Deci2Poll(param->{id}) — always report "not busy" (status1=0), return 1
            {
                uint id = _system.Memory.Read32(param + 0x00);
                if (id < (uint)_deci2Handlers.Length && _deci2Handlers[id].HasValue)
                    _system.Memory.Write32(_deci2Handlers[id]!.Value.bufferAddr + 0x0C, 0);
                return 1;
            }
            case 0x10: // kPuts(param->{stringAddr})
            {
                uint stringAddr = _system.Memory.Read32(param + 0x00);
                if (trace)
                {
                    var sb = new System.Text.StringBuilder();
                    for (uint i = 0; i < 256; i++)
                    {
                        byte b = _system.Memory.Read8(stringAddr + i);
                        if (b == 0) break;
                        sb.Append((char)b);
                    }
                    Console.Error.WriteLine($"[DECI2] kPuts: {sb}");
                }
                return 0;
            }
            default:
                if (trace) Console.Error.WriteLine($"[DECI2] unknown function=0x{function:X8}");
                return 0;
        }
    }

    private void EnsureStubs()
    {
        if (_stubsInstalled) return;
        _stubsInstalled = true;
        for (uint i = 0; i < 64; i++)
        {
            uint a = StubBase + i * 16;
            _system.Memory.Write32(a, 0x03E00008u);
            _system.Memory.Write32(a + 4, 0u);
            _system.Memory.Write32(a + 8, 0x03E00008u);
            _system.Memory.Write32(a + 12, 0u);
        }
        _system.Memory.Write32(StubBase + 0x400, 0x24020000u);
        _system.Memory.Write32(StubBase + 0x404, 0x03E00008u);
        _system.Memory.Write32(StubBase + 0x408, 0u);
    }

    private uint AllocStub()
    {
        EnsureStubs();
        int slot = _stubSlots++ % 64;
        return StubBase + (uint)(slot * 16);
    }

    /// <summary>
    /// Games (e.g. Midway) call syscall 0x83 as FindAddress(start, end, needle):
    /// scan memory for a 32-bit word equal to needle and return its address.
    /// </summary>
    private long FindAddressScan(uint start, uint end, uint needle)
    {
        if (needle == 0 && (end == 0 || end == start))
        {
            EnsureStubs();
            return AllocStub();
        }

        // Cache key includes `start`, not just `needle` -- a title enumerating multiple
        // occurrences of the same needle (e.g. "find the next export after the one I just
        // processed") calls this repeatedly with the same needle but an advancing `start`
        // (typically the previous hit + a few bytes). Caching by needle alone made every such
        // call after the first return the same stale first-ever hit forever, regardless of
        // `start` — an infinite loop for any title using this enumerate-forward idiom (confirmed
        // via DETPS2_TRACE_FINDADDR against Mortal Kombat: Deception, SLUS_208.81: 226,976 calls
        // in a 5M-cycle window, same start=/end=/needle=/result= every time, start already past
        // the returned hit). Caching by (needle, start) keeps the original fast-path behavior for
        // the common "poll the same start/needle until the answer changes" idiom (unaffected: the
        // key is identical every retry) while letting an advancing `start` produce a fresh scan.
        ulong key = ((ulong)needle << 32) | start;
        if (_findCache.TryGetValue(key, out uint cached))
            return cached;

        uint vbase = (start & 0xE0000000u) != 0 ? (start & 0xE0000000u) : 0x80000000u;
        uint physCap = (uint)Math.Min(SystemMemory.RDRAM_SIZE, 0x01000000);
        // Honor `start` as the real scan lower bound (previously always scanned from physical 0,
        // silently ignoring `start` for anything but computing `vbase` — the other half of the
        // enumerate-forward bug above: even a fresh, uncached scan would re-find the same first
        // occurrence instead of the next one). Clamp a `start` above our fixed scan ceiling back
        // to 0 rather than skip the scan outright, so a title whose runtime addresses this
        // scanner doesn't otherwise understand still gets a best-effort answer instead of none.
        uint physStart = start & 0x1FFFFFFFu;
        if (physStart >= physCap) physStart = 0;

        long hit = ScanPhysRange(physStart, physCap, needle, vbase);
        _findCache[key] = (uint)hit;

        // Midway-style pair fixup: export tables often need (addrA - 524) == (addrB - 360).
        // When we know both pointers, plant a synthetic slot so commercial init loops exit.
        MaybePlantMidwayPair(vbase);

        return _findCache.TryGetValue(key, out cached) ? cached : hit;
    }

    private readonly Dictionary<ulong, uint> _findCache = new();
    private bool _midwayPairPlanted;

    private void MaybePlantMidwayPair(uint vbase) => ForcePlantMidwayPair();

    /// <summary>
    /// Midway (Shaolin Monks) CRT0 scans for two code pointers then checks
    /// (addrA - 524) == (addrB - 360). Static .data layout doesn't satisfy that.
    /// Patch the tight retry loop so init can continue to graph setup.
    /// </summary>
    private void ForcePlantMidwayPair()
    {
        if (_midwayPairPlanted) return;
        // Only plant if the characteristic CRT0 instructions are present
        uint at = _system.Memory.Read32(0x00486194);
        uint at2 = _system.Memory.Read32(0x004861D8);
        // BEQ/BNE primary opcode nibble in top 6 bits = 0x04 or 0x05
        uint p1 = (at >> 26) & 0x3F;
        uint p2 = (at2 >> 26) & 0x3F;
        if (p1 is not (0x04 or 0x05) && p2 is not (0x04 or 0x05))
            return;

        // 0x486194: BEQ r17,r16,+20  → BEQ r0,r0,+20 (always take "success" path)
        _system.Memory.Write32(0x00486194, 0x10000014u);
        // 0x4861D8: BNE r17,r16,-15 → NOP (don't restart scan)
        _system.Memory.Write32(0x004861D8, 0);
        _midwayPairPlanted = true;
    }

    private long ScanPhysRange(uint physS, uint physE, uint needle, uint vbase)
    {
        if (physE < physS)
            (physS, physE) = (physE, physS);
        if (physS >= SystemMemory.RDRAM_SIZE)
            return 0;
        if (physE > SystemMemory.RDRAM_SIZE)
            physE = (uint)SystemMemory.RDRAM_SIZE;

        for (uint p = physS & ~3u; p + 3 < physE; p += 4)
        {
            if (_system.Memory.Read32(p) == needle)
                return (long)(vbase | p);
        }
        return 0;
    }
}
