using System;
using System.Collections.Generic;
using System.IO;

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
    /// <summary>Per-cause ordered list of AddIntcHandler registrations. Real BIOS keeps a
    /// linked list and the ISR walks every entry; a single-slot dictionary silently dropped
    /// every registration but the last (Burnout 3: cause=2 registers 0x2370A0 then 0x1F1CE8
    /// then 0x22B830 — only the counter stub survived, so the VBlank thread-wakeup never ran).
    /// </summary>
    private readonly Dictionary<int, List<uint>> _intcHandlers = new();
    /// <summary>Next handler index to dispatch for each cause within the current interrupt
    /// episode (reset to 0 after the last handler of the chain runs).</summary>
    private readonly Dictionary<int, int> _intcNextIndex = new();
    private readonly Dictionary<int, uint> _vTlbRefillHandlers = new();
    private readonly Dictionary<int, uint> _vCommonHandlers = new();
    private readonly Dictionary<int, uint> _vInterruptHandlers = new();
    /// <summary>Stand-in for the BIOS default exception vector (real hardware: 0x80000180).</summary>
    private const uint DefaultExceptionHandlerSentinel = 0x80000180;
    private readonly uint[] _sifRegs = new uint[32];
    /// <summary>SifSetReg/SifGetReg with the high bit set (id | 0x80000000) is a distinct,
    /// software-defined "virtual register" namespace some SDKs build on top of real SIF
    /// registers (confirmed live in Burnout 3/MK: Deadly Alliance/MK: Deception's shared SDK SIF
    /// init routine — e.g. `sceSifSetReg(0xffffffff80000000, val)`) — not real SIF hardware
    /// register indices 0-31. Previously these silently no-op'd on write and always read 0
    /// (`a0 < _sifRegs.Length` fails for any 0x80000000+ id), since `a0` is a plain `uint` array
    /// index with no headroom for that marker bit. Kept separate from `_sifRegs` rather than
    /// merged in (even after masking off the marker bit) to avoid aliasing real hardware register
    /// semantics — e.g. `_sifRegs[3]` mirrors onto real SIF MMIO below, which a virtual id 3 must
    /// not touch.</summary>
    private readonly uint[] _sifVirtualRegs = new uint[32];
    private readonly Dictionary<uint, uint> _customSyscalls = new();
    private uint _gsImr = 0xFF00;
    private bool _stubsInstalled;
    /// <summary>sceSetVSyncFlag(u32* odd, u32* even) — EE pointers written each VBlank.
    /// Was a flat no-op; MK and others poll these words for frame pacing (syscall 0x73).</summary>
    private uint _vsyncFlagOdd;
    private uint _vsyncFlagEven;
    private uint _vsyncCount;
    /// <summary>EE kernel soft alarms (syscalls 0x18/0x19 / 0xFC/0xFE). Time unit is H-SYNC
    /// ticks per ps2sdk <c>kernel.h</c>. Fired from <see cref="OnVblankTick"/> by subtracting
    /// an approximate lines-per-frame budget (real BIOS uses INTC Timer3 / H-SYNC).</summary>
    private sealed class EeAlarm
    {
        public int Id;
        public int RemainingHsync;
        public int InitialTime;
        public uint Callback;
        public uint Common;
        public bool Active;
    }
    private const int MaxEeAlarms = 64;
    /// <summary>Approx NTSC visible+blanking lines per VBlank field used to advance alarms.</summary>
    private const int HsyncPerVblank = 262;
    private readonly EeAlarm?[] _alarms = new EeAlarm?[MaxEeAlarms];
    private int _nextAlarmId = 1;
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

    /// <summary>Count of pending real BIND/CALL/RDATA packets (generation-gated drain).</summary>
    public int RealRpcQueueCount => _system.Sif.RealRpcQueueCount;

    /// <summary>True when a queued real RPC packet's client sema matches <paramref name="semaId"/>.</summary>
    public bool RealRpcQueueMaySignalSema(int semaId) =>
        _system.Sif.QueueMaySignalSema(_system.Memory, semaId);

    private readonly Dictionary<int, uint> _dmacHandlers = new();

    /// <summary>Drains real (retail sifrpc.c) bind/call packets queued by HleSifCmdFromEe
    /// that are strictly older than <paramref name="currentGeneration"/> — called once per
    /// ambient scheduler tick (Ps2System.ISchedulable.Step) with that tick's own generation,
    /// so a response is never visible within the same EE instruction (or even the same
    /// scheduler tick) that submitted the request. Also called opportunistically from
    /// PerformSifSetDma itself (with the SAME current generation, so it still can't drain
    /// this tick's own fresh submissions) — a title whose own retry loop can issue many bind
    /// attempts within a single EE.Step() call needs older packets freed *during* that call,
    /// not just once at the end of the tick, or it can exhaust the real, small, fixed-size
    /// EE-side RPC packet pool before the ambient drain ever gets a turn (confirmed live,
    /// 2026-07-28 — Shaolin Monks' CDVD bind, sid=0x80000592, retried literally millions of
    /// times once the queue's answer was delayed by even one tick). See Sif.cs's
    /// _realRpcQueue doc comment for the full mechanism.
    /// <para>
    /// <b>WP-22 / LOADFILE:</b> today every dequeued packet is answered by
    /// <see cref="RealSifRpc.TryHandle"/> (C# HLE), including sid=<c>0x80000006</c>.
    /// Under <c>DETPS2_LITERAL_IRX=1</c> the EE→IOP DMA still lands the real packet in IOP RAM
    /// (live path); HLE remains the answerer until IOP LOADFILE.IRX can complete sifrpc.
    /// Set <see cref="PreferLiveLoadFileRpc"/> only when that live server exists — do not
    /// enable it for HLE=0 bisect. See <c>docs/irx/EE_LOADFILE.md</c>.
    /// </para>
    /// </summary>
    private bool _inRpcEndFuncInvoke;

    /// <summary>
    /// When true <b>and</b> <c>DETPS2_LITERAL_IRX=1</c>, LOADFILE-related real RPC packets are
    /// left for live IOP (scaffold for WP-22). Default <c>false</c>: always HLE via
    /// <see cref="RealSifRpc"/> so smokes and <c>LITERAL_IRX=0</c> bisect stay stable.
    /// Enabling without a runnable IOP LOADFILE server will starve client semas.
    /// </summary>
    public bool PreferLiveLoadFileRpc { get; set; }

    public void DrainRealRpcQueue(ulong currentGeneration)
    {
        // Nested Step() during end_function invoke re-enters drain; skip to avoid reentrancy.
        if (_inRpcEndFuncInvoke) return;

        bool literalIrx = IopExtendedBiosHost.IsLiteralIrxEnabled();
        bool preferLiveLf = literalIrx && PreferLiveLoadFileRpc;

        while (_system.Sif.TryDequeueRealRpc(currentGeneration, out uint addr))
        {
            // WP-22 scaffold: under LITERAL_IRX + PreferLiveLoadFileRpc, skip HLE for packets
            // that already live in IOP RAM after Sif1EeToIop so a future IOP LOADFILE server
            // can answer. Without a live server this path must stay off (default).
            if (preferLiveLf && RealSifRpc.IsRealRpcPacket(_system.Memory, addr))
            {
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine(
                        $"[LOADFILE] LITERAL_IRX PreferLiveLoadFileRpc: defer HLE pkt=0x{addr:X8} " +
                        $"(live IOP path; no C# TryHandle)");
                // Packet remains in IOP RAM from Sif1EeToIop; do not HLE-complete.
                continue;
            }

            if (_realRpc.TryHandle(_system.Memory, _kernel, _system.Cdvd, _system.Pad, _system.IopModules, addr))
                _system.Intc.Raise(Intc.InterruptSource.Sif);
        }

        // ps2sdk _request_end: after CALL, run end_function(end_param) then SignalSema.
        // CompleteRpcEnd already SignalSema'd and queued callbacks; invoke them now so
        // 989snd/libmc done-flags land before the waiter inspects them.
        while (_realRpc.TryDequeueEndFunc(out uint fn, out uint param))
            InvokeRpcEndFunction(fn, param);
    }

    /// <summary>
    /// Run EE <c>void end_function(void *end_param)</c> briefly. Saves/restores PC/a0/ra/<b>sp</b>
    /// and callee-saved s0–s7/fp.
    /// Fast path already wrote <c>*end_param=1</c> when param is in RDRAM.
    /// <para>
    /// Haven SLUS_205.17 live (WAVE-6): NUSOUND bulk end_function <c>0x211878</c> jals into a
    /// multi-thousand-insn body at <c>0x208818</c>. The 2048-step cap left the nested run
    /// mid-frame with a depressed <c>$sp</c>; only PC/a0/ra were restored, so the interrupted
    /// <c>sceSifCallRpc</c> epilogue did <c>ld ra,176(sp)</c> against the wrong frame → ra=0 →
    /// JREXIT → main Started=false. Always restore SP + s-regs so a truncated nested invoke
    /// cannot corrupt the caller's stack geometry.
    /// </para>
    /// </summary>
    private void InvokeRpcEndFunction(uint func, uint param)
    {
        if (func < 0x1000 || func >= SystemMemory.RDRAM_SIZE) return;
        var ee = _system.EE;
        if (ee == null) return;

        // Emulate common leaf: store 1 to *a0 / absolute then jr ra. Avoid full nested Step
        // when the body is the classic done-flag pattern (covers 989snd + most libmc ends).
        if (TryHleSimpleEndFunction(func, param))
            return;

        // Haven NUSOUND bulk: end_function @0x211878 is a thin wrapper that jals into a
        // multi-kinsn game body @0x208818 (state machine / bank bind). Nested Step of that
        // body either truncates (SP clobber — fixed below) or runs so long it starves the
        // CallRpc waiter and leaves main in a VIF helper leaf. Soft-success: the bulk
        // path already painted recv + bound DLL.DAT; let main continue past WaitSema.
        if (func == 0x00211878u || func == 0x211878u)
        {
            if (param != 0 && param < SystemMemory.RDRAM_SIZE)
            {
                try { _system.Memory.Write32(param, 1); } catch { /* ignore */ }
            }
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[RPC] end_function=0x{func:X8} Haven NUSOUND soft-OK (skip heavy body)");
            return;
        }

        ulong savedPc = ee.PC;
        var savedA0 = ee.GetGpr(4);
        var savedRa = ee.GetGpr(31);
        var savedSp = ee.GetGpr(29);
        // Callee-saved may be dirtied by a truncated nested body (Haven 0x208818).
        var savedS0 = ee.GetGpr(16);
        var savedS1 = ee.GetGpr(17);
        var savedS2 = ee.GetGpr(18);
        var savedS3 = ee.GetGpr(19);
        var savedS4 = ee.GetGpr(20);
        var savedS5 = ee.GetGpr(21);
        var savedS6 = ee.GetGpr(22);
        var savedS7 = ee.GetGpr(23);
        var savedFp = ee.GetGpr(30);
        const uint SentinelRa = 0xFFFFFFFC; // unmapped; jr ra stops the mini-run

        ee.SetGpr(4, new EmotionEngine.Gpr128 { Lo = param });
        ee.SetGpr(31, new EmotionEngine.Gpr128 { Lo = SentinelRa });
        ee.PC = func;
        ee.HleRedirectPc = null;

        // Nested end_function must not be no-op'd by WaitingVblank (Vexx CdSync).
        bool savedWaitingVblank = _kernel.WaitingVblank;
        _kernel.WaitingVblank = false;

        _inRpcEndFuncInvoke = true;
        try
        {
            // Haven NUSOUND end_function body is large; 16k steps still bounded.
            int limit = func is >= 0x00210000 and < 0x00220000 ? 16384 : 2048;
            for (int i = 0; i < limit; i++)
            {
                uint pc = (uint)(ee.PC & 0x1FFFFFFF);
                if (pc == (SentinelRa & 0x1FFFFFFF) || pc == 0) break;
                ee.Step(1);
            }
        }
        finally
        {
            _inRpcEndFuncInvoke = false;
            _kernel.WaitingVblank = savedWaitingVblank;
            ee.PC = savedPc;
            ee.SetGpr(4, savedA0);
            ee.SetGpr(31, savedRa);
            ee.SetGpr(29, savedSp);
            ee.SetGpr(16, savedS0);
            ee.SetGpr(17, savedS1);
            ee.SetGpr(18, savedS2);
            ee.SetGpr(19, savedS3);
            ee.SetGpr(20, savedS4);
            ee.SetGpr(21, savedS5);
            ee.SetGpr(22, savedS6);
            ee.SetGpr(23, savedS7);
            ee.SetGpr(30, savedFp);
        }
    }

    /// <summary>
    /// HLE common end_function bodies without nested EE Step:
    /// <c>*(int*)end_param = 1; return;</c> or store-1 to an absolute address via lui/addiu.
    /// </summary>
    private bool TryHleSimpleEndFunction(uint func, uint param)
    {
        var mem = _system.Memory;
        uint w0 = mem.Read32(func);
        uint w1 = mem.Read32(func + 4);
        uint w2 = mem.Read32(func + 8);
        uint w3 = mem.Read32(func + 12);

        // sw reg, 0(a0)  encoding: op=0x2B, base=a0=4, rt=?, imm=0
        // jr ra = 0x03E00008; nop = 0x00000000
        static bool IsJrRa(uint w) => w == 0x03E00008;
        static bool IsSwToA0(uint w, out int rt, out int imm)
        {
            rt = (int)((w >> 16) & 0x1F);
            imm = (short)(w & 0xFFFF);
            return ((w >> 26) == 0x2B) && (((w >> 21) & 0x1F) == 4);
        }
        static bool IsLiOrAddiuToReg(uint w, out int rt, out int imm)
        {
            // addiu rt, rs, imm — often rs=0 for li
            rt = (int)((w >> 16) & 0x1F);
            imm = (short)(w & 0xFFFF);
            return (w >> 26) == 0x09; // ADDIU
        }
        static bool IsLui(uint w, out int rt, out int imm)
        {
            rt = (int)((w >> 16) & 0x1F);
            imm = (short)(w & 0xFFFF);
            return (w >> 26) == 0x0F;
        }

        // Pattern: sw XX, imm(a0); jr ra; nop  — write 1 (or whatever) already done if param set
        if (IsSwToA0(w0, out _, out int off0) && IsJrRa(w1))
        {
            if (param != 0 && param < SystemMemory.RDRAM_SIZE - 4)
                mem.Write32(param + (uint)off0, 1);
            return true;
        }
        // Pattern: addiu r, zero, 1; sw r, 0(a0); jr ra
        if (IsLiOrAddiuToReg(w0, out int rLi, out int immLi) && immLi == 1
            && IsSwToA0(w1, out int rSw, out int off1) && rSw == rLi
            && IsJrRa(w2))
        {
            if (param != 0 && param < SystemMemory.RDRAM_SIZE - 4)
                mem.Write32(param + (uint)off1, 1);
            return true;
        }
        // Pattern: lui at, HI; ... sw reg, LO(at) for global flag — try first 4 words
        if (IsLui(w0, out int rAt, out int hi))
        {
            for (int i = 1; i < 4; i++)
            {
                uint w = i == 1 ? w1 : i == 2 ? w2 : w3;
                // sw rt, imm(at-base)
                if ((w >> 26) == 0x2B && ((int)((w >> 21) & 0x1F) == rAt))
                {
                    int imm = (short)(w & 0xFFFF);
                    uint addr = (uint)((hi << 16) + imm);
                    if (addr < SystemMemory.RDRAM_SIZE - 4)
                        mem.Write32(addr, 1);
                    return true;
                }
            }
        }

        // Unknown body — caller may nested-Step.
        return false;
    }

    /// <summary>Called from VBlank path — fulfills sceSetVSyncFlag pointers and advances
    /// EE soft alarms (H-SYNC budget per field).</summary>
    public void OnVblankTick()
    {
        _vsyncCount++;
        // Alternate odd/even field counter writes (real GS/PCRTC is more nuanced; this
        // unblocks software that waits for *any* change of the pointed words).
        uint odd = _vsyncCount | 1u;
        uint even = _vsyncCount & ~1u;
        if (_vsyncFlagOdd != 0 && _vsyncFlagOdd < SystemMemory.RDRAM_SIZE - 4)
            _system.Memory.Write32(_vsyncFlagOdd, odd);
        if (_vsyncFlagEven != 0 && _vsyncFlagEven < SystemMemory.RDRAM_SIZE - 4)
            _system.Memory.Write32(_vsyncFlagEven, even);

        TickEeAlarms(HsyncPerVblank);
    }

    /// <summary>Test / save-state helper: advance alarm timers by <paramref name="hsyncTicks"/>
    /// H-SYNC units without a full PCRTC VBlank (fires due callbacks immediately).</summary>
    public void TickEeAlarms(int hsyncTicks)
    {
        if (hsyncTicks <= 0) return;
        // Collect firings first so a callback that SetAlarm/ReleaseAlarm mid-fire is safe.
        Span<int> fireSlots = stackalloc int[MaxEeAlarms];
        int fireCount = 0;
        for (int i = 0; i < MaxEeAlarms; i++)
        {
            var a = _alarms[i];
            if (a == null || !a.Active) continue;
            a.RemainingHsync -= hsyncTicks;
            if (a.RemainingHsync <= 0)
                fireSlots[fireCount++] = i;
        }
        for (int f = 0; f < fireCount; f++)
        {
            int slot = fireSlots[f];
            var a = _alarms[slot];
            if (a == null || !a.Active) continue;
            a.Active = false;
            _alarms[slot] = null;
            InvokeAlarmCallback(a.Id, (uint)a.InitialTime, a.Callback, a.Common);
        }
    }

    /// <summary>Number of currently armed EE soft alarms (diagnostics / smokes).</summary>
    public int ActiveAlarmCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < MaxEeAlarms; i++)
                if (_alarms[i] is { Active: true }) n++;
            return n;
        }
    }

    /// <summary>ps2sdk SetAlarm(u16 time, cb, common) — returns alarm id, or negative on full.</summary>
    private int SetEeAlarm(uint timeHsync, uint callback, uint common)
    {
        // time is u16 on hardware; zero means "as soon as possible" (next tick).
        int time = (int)(timeHsync & 0xFFFF);
        if (time == 0) time = 1;
        if (callback == 0) return -1;

        for (int i = 0; i < MaxEeAlarms; i++)
        {
            if (_alarms[i] != null) continue;
            int id = _nextAlarmId++;
            if (_nextAlarmId <= 0) _nextAlarmId = 1;
            _alarms[i] = new EeAlarm
            {
                Id = id,
                RemainingHsync = time,
                InitialTime = time,
                Callback = callback,
                Common = common,
                Active = true
            };
            return id;
        }
        return -1; // table full (MAX_ALARMS = 64)
    }

    /// <summary>ps2sdk ReleaseAlarm(id) — returns remaining H-SYNC ticks, or negative if missing.</summary>
    private int ReleaseEeAlarm(int alarmId)
    {
        if (alarmId <= 0) return -1;
        for (int i = 0; i < MaxEeAlarms; i++)
        {
            var a = _alarms[i];
            if (a == null || !a.Active || a.Id != alarmId) continue;
            int rem = a.RemainingHsync > 0 ? a.RemainingHsync : 0;
            a.Active = false;
            _alarms[i] = null;
            return rem;
        }
        return -1;
    }

    /// <summary>
    /// Invoke <c>void cb(s32 id, u16 time, void *common)</c> briefly. Same save/restore
    /// pattern as RPC end_function invoke; callback must return (jr ra) within a few hundred
    /// instructions. Failures are swallowed — alarms must not crash the whole EE.
    /// </summary>
    private void InvokeAlarmCallback(int alarmId, uint time, uint callback, uint common)
    {
        if (callback < 0x1000 || callback >= SystemMemory.RDRAM_SIZE) return;
        var ee = _system.EE;
        if (ee == null) return;

        ulong savedPc = ee.PC;
        var savedA0 = ee.GetGpr(4);
        var savedA1 = ee.GetGpr(5);
        var savedA2 = ee.GetGpr(6);
        var savedRa = ee.GetGpr(31);
        const uint SentinelRa = 0xFFFFFFFC;

        ee.SetGpr(4, new EmotionEngine.Gpr128 { Lo = unchecked((uint)alarmId) });
        ee.SetGpr(5, new EmotionEngine.Gpr128 { Lo = time });
        ee.SetGpr(6, new EmotionEngine.Gpr128 { Lo = common });
        ee.SetGpr(31, new EmotionEngine.Gpr128 { Lo = SentinelRa });
        ee.PC = callback;
        ee.HleRedirectPc = null;

        bool savedWaitingVblank = _kernel.WaitingVblank;
        _kernel.WaitingVblank = false;

        // Re-use the RPC end-function reentrancy guard so nested Step cannot re-drain.
        bool prev = _inRpcEndFuncInvoke;
        _inRpcEndFuncInvoke = true;
        try
        {
            for (int i = 0; i < 512; i++)
            {
                uint pc = (uint)(ee.PC & 0x1FFFFFFF);
                if (pc == (SentinelRa & 0x1FFFFFFF) || pc == 0) break;
                ee.Step(1);
            }
        }
        finally
        {
            _kernel.WaitingVblank = savedWaitingVblank;
            _inRpcEndFuncInvoke = prev;
            ee.PC = savedPc;
            ee.SetGpr(4, savedA0);
            ee.SetGpr(5, savedA1);
            ee.SetGpr(6, savedA2);
            ee.SetGpr(31, savedRa);
        }
    }

    /// <summary>Peek the first registered AddIntcHandler for <paramref name="cause"/>
    /// without advancing the multi-handler dispatch cursor (diagnostics / save-state tests).</summary>
    public bool TryGetIntcHandler(int cause, out uint handlerAddr)
    {
        if (_intcHandlers.TryGetValue(cause, out var list) && list.Count > 0)
        {
            handlerAddr = list[0];
            return handlerAddr != 0;
        }
        handlerAddr = 0;
        return false;
    }

    /// <summary>
    /// Take the next AddIntcHandler for <paramref name="cause"/> in registration order.
    /// Real BIOS walks the whole list inside one ISR; we serialize one handler per exception
    /// episode. When <paramref name="moreRemain"/> is true the caller must leave the COP0
    /// edge latch armed (or re-Raise) so the next eret dispatches the following handler —
    /// same pattern as multi-channel DMAC owed-handler re-raise.
    /// </summary>
    public bool TryTakeNextIntcHandler(int cause, out uint handlerAddr, out bool moreRemain)
    {
        moreRemain = false;
        handlerAddr = 0;
        if (!_intcHandlers.TryGetValue(cause, out var list) || list.Count == 0)
            return false;

        int idx = _intcNextIndex.GetValueOrDefault(cause, 0);
        if (idx < 0 || idx >= list.Count)
        {
            _intcNextIndex[cause] = 0;
            return false;
        }

        handlerAddr = list[idx];
        int next = idx + 1;
        if (next >= list.Count)
        {
            _intcNextIndex[cause] = 0;
            moreRemain = false;
        }
        else
        {
            _intcNextIndex[cause] = next;
            moreRemain = true;
        }
        return handlerAddr != 0;
    }

    /// <summary>Same registration the AddIntcHandler syscall (case 0x10) performs — exposed
    /// directly for callers that already know the (cause, handler) pair without going through
    /// a syscall dispatch (e.g. save-state round-trip tests). Appends to the per-cause chain
    /// (does not replace), matching real linked-list semantics.</summary>
    public void RegisterIntcHandler(int cause, uint handlerAddr)
    {
        if (!_intcHandlers.TryGetValue(cause, out var list))
        {
            list = new List<uint>(4);
            _intcHandlers[cause] = list;
        }
        list.Add(handlerAddr);
    }

    /// <summary>Looks up a game-registered AddDmacHandler entry (keyed by DMA channel, e.g.
    /// DMA_CHANNEL_SIF0=5) — real hardware routes DMA-channel completion here, not through
    /// AddIntcHandler; e.g. ps2sdk's sceSifInitCmd installs _SifCmdIntHandler this way.</summary>
    public bool TryGetDmacHandler(int channel, out uint handlerAddr) =>
        _dmacHandlers.TryGetValue(channel, out handlerAddr);

    /// <summary>Same registration the AddDmacHandler syscall (case 0x12) performs — for tests
    /// and save-state round-trips that already know the (channel, handler) pair.</summary>
    public void RegisterDmacHandler(int channel, uint handlerAddr) =>
        _dmacHandlers[channel] = handlerAddr;

    /// <summary>
    /// Pick one pending DMAC channel that both has a sticky D_STAT completion bit (and is
    /// IRQ-enabled) and a registered <c>AddDmacHandler</c>. Used by the HLE DmaController
    /// (INTC source 14) dispatcher — real BIOS walks this table; we never installed that
    /// MIPS trampoline, so EmotionEngine must call handlers directly. Clears the chosen
    /// channel's status bit (handler still may W1C it — no-op) so the next completion can
    /// re-arm. Returns false when nothing is dispatchable.
    /// </summary>
    public bool TryTakePendingDmacHandler(out uint handlerAddr, out int channel)
    {
        var dmac = _system.Dmac;
        // Prefer sticky D_STAT completion bits (hardware-accurate path).
        for (int ch = 0; ch < 10; ch++)
        {
            if ((dmac.DStat & (1u << ch)) == 0) continue;
            if (!dmac.IsChannelIrqEnabled(ch)) continue;
            if (!_dmacHandlers.TryGetValue(ch, out handlerAddr) || handlerAddr == 0) continue;
            dmac.ClearChannelStatus(ch);
            // Also consume one owed-call credit so the soft queue doesn't double-fire.
            dmac.TryConsumeOwedHandlerCall(ch);
            dmac.NoteHandlerTake(ch, viaCis: true); // M5-a S1 telemetry only
            channel = ch;
            // If other channels still need service, re-raise so the next instruction after
            // this handler's eret dispatches them (EXL blocks nesting mid-handler).
            if (dmac.HasPendingChannelIrq() || HasAnyOwedDmacHandler(dmac))
                _system.Intc.Raise(Intc.InterruptSource.DmaController);
            return true;
        }
        // Fall back to owed-call queue: path-sync force-step can FinishChannel + Raise, then
        // the game W1C's D_STAT before EE dispatches (Burnout 3). The soft queue still owes
        // the AddDmacHandler invocation so flip pending-count can drain.
        for (int ch = 0; ch < 10; ch++)
        {
            if (!dmac.HasOwedHandlerCall(ch)) continue;
            if (!dmac.IsChannelIrqEnabled(ch)) continue;
            if (!_dmacHandlers.TryGetValue(ch, out handlerAddr) || handlerAddr == 0) continue;
            dmac.TryConsumeOwedHandlerCall(ch);
            dmac.NoteHandlerTake(ch, viaCis: false); // M5-a S1 telemetry only
            channel = ch;
            if (dmac.HasPendingChannelIrq() || HasAnyOwedDmacHandler(dmac))
                _system.Intc.Raise(Intc.InterruptSource.DmaController);
            return true;
        }
        handlerAddr = 0;
        channel = -1;
        return false;
    }

    private static bool HasAnyOwedDmacHandler(Dmac dmac)
    {
        for (int ch = 0; ch < 10; ch++)
            if (dmac.HasOwedHandlerCall(ch)) return true;
        return false;
    }

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
        // WP-25: LOADFILE MOD_LOAD needs host for StartLoadedModule after disc LoadIrx.
        _realRpc.BindHost(system);
    }

    public void Reset()
    {
        _intcHandlers.Clear();
        _intcNextIndex.Clear();
        _dmacHandlers.Clear();
        PreferLiveLoadFileRpc = false;
        Array.Clear(_sifRegs);
        Array.Clear(_sifVirtualRegs);
        _customSyscalls.Clear();
        _findCache.Clear();
        _midwayPairPlanted = false;
        _gsImr = 0xFF00;
        _stubsInstalled = false;
        _stubSlots = 0;
        _vsyncFlagOdd = 0;
        _vsyncFlagEven = 0;
        _vsyncCount = 0;
        Array.Clear(_alarms);
        _nextAlarmId = 1;
        Handled = Unknown = 0;
        SifDmaCalls = SifGetRegCalls = 0;
        _recentSyscallIdx = 0;
        Array.Clear(RecentSyscalls);
        _syscallHistogram.Clear();
        _realRpc.Reset();
        // SIFINIT/EESYNC/SIFCMD contracts: present post-IOPBTCONF handoff so commercial
        // sceSifInitCmd / sceSifInitRpc do not spin (docs/bios-ports/SIFINIT_EESYNC.md).
        PlantSifInitSyncContracts();
    }

    /// <summary>
    /// Present the EE-visible effects of a completed BIOS IOPBTCONF SIF stack
    /// (SIFMAN → SIFCMD → … → SIFINIT + EESYNC): SMFLAG ready bits, SUBADDR command buffer,
    /// SYSREG_RPCINIT already acknowledged, EE ready-slot table planted.
    /// </summary>
    public void PlantSifInitSyncContracts()
    {
        _system.Sif.PresentIopBootReady();
        // SIF_REG_SUBADDR (2): IOP SIFCMD receive buffer — sceSifInitCmd DMA dest.
        _sifRegs[Sif.SifRegSubAddr] = Sif.DefaultIopSifCmdBufAddr;
        // SIF_SYSREG_SUBADDR (0x80000000): software mirror used by SifIopReset / InitCmd first path.
        _sifVirtualRegs[0] = Sif.DefaultIopSifCmdBufAddr;
        // SIF_SYSREG_RPCINIT (0x80000002): non-zero → sceSifInitRpc skips INIT_CMD wait.
        _sifVirtualRegs[2] = 1;
        // Shadow SMFLAG software copy matches hardware presentation.
        _sifRegs[Sif.SifRegSmFlag] = _system.Sif.SmFlag;
        Sif.PlantEeSifReadySlots(_system.Memory);
    }

    /// <summary>Game-registered handler tables + SIF register state for SaveState.cs.
    /// _intcHandlers/_dmacHandlers are what let EmotionEngine dispatch a real interrupt
    /// straight into a game's own AddIntcHandler/AddDmacHandler callback instead of a
    /// synthesized no-op vector — without saving these, a load would resume with every
    /// future interrupt going nowhere, even though the game registered real handlers long
    /// before the save point (see this session's Shaolin Monks work: this dispatch path is
    /// load-bearing for real commercial titles, not an edge case).
    /// _findCache/_midwayPairPlanted are intentionally NOT saved — pure perf caches /
    /// one-time boot-assist scan state that's safe to recompute, not correctness-affecting.</summary>
    public void WriteState(BinaryWriter w)
    {
        // Flatten (cause, handler) pairs — multiple handlers may share a cause.
        int intcTotal = 0;
        foreach (var list in _intcHandlers.Values) intcTotal += list.Count;
        w.Write(intcTotal);
        foreach (var kv in _intcHandlers)
            foreach (uint h in kv.Value) { w.Write(kv.Key); w.Write(h); }
        w.Write(_dmacHandlers.Count);
        foreach (var kv in _dmacHandlers) { w.Write(kv.Key); w.Write(kv.Value); }
        for (int i = 0; i < _sifRegs.Length; i++) w.Write(_sifRegs[i]);
        for (int i = 0; i < _sifVirtualRegs.Length; i++) w.Write(_sifVirtualRegs[i]);
        w.Write(_customSyscalls.Count);
        foreach (var kv in _customSyscalls) { w.Write(kv.Key); w.Write(kv.Value); }
        w.Write(_gsImr);
        w.Write(_stubsInstalled);
        w.Write(_stubSlots);
        w.Write(_deci2Handlers.Length);
        foreach (var h in _deci2Handlers)
        {
            w.Write(h.HasValue);
            if (h.HasValue) { w.Write(h.Value.device); w.Write(h.Value.bufferAddr); }
        }
        w.Write(Handled); w.Write(Unknown);
        for (int i = 0; i < RecentSyscalls.Length; i++) w.Write(RecentSyscalls[i]);
        w.Write(_recentSyscallIdx);
        w.Write(SifDmaCalls); w.Write(SifGetRegCalls);
        w.Write(_syscallHistogram.Count);
        foreach (var kv in _syscallHistogram) { w.Write(kv.Key); w.Write(kv.Value); }
        w.Write(LastCreatedThreadEntry);
        w.Write(LastCreatedThreadStack);
        _realRpc.WriteState(w);
    }

    public void ReadState(BinaryReader r)
    {
        _intcHandlers.Clear();
        _intcNextIndex.Clear();
        int nInt = r.ReadInt32();
        for (int i = 0; i < nInt; i++)
        {
            int k = r.ReadInt32();
            uint v = r.ReadUInt32();
            RegisterIntcHandler(k, v);
        }
        _dmacHandlers.Clear();
        int nDma = r.ReadInt32();
        for (int i = 0; i < nDma; i++) { int k = r.ReadInt32(); uint v = r.ReadUInt32(); _dmacHandlers[k] = v; }
        for (int i = 0; i < _sifRegs.Length; i++) _sifRegs[i] = r.ReadUInt32();
        for (int i = 0; i < _sifVirtualRegs.Length; i++) _sifVirtualRegs[i] = r.ReadUInt32();
        _customSyscalls.Clear();
        int nCustom = r.ReadInt32();
        for (int i = 0; i < nCustom; i++) { uint k = r.ReadUInt32(); uint v = r.ReadUInt32(); _customSyscalls[k] = v; }
        _gsImr = r.ReadUInt32();
        _stubsInstalled = r.ReadBoolean();
        _stubSlots = r.ReadInt32();
        int deciLen = r.ReadInt32();
        for (int i = 0; i < deciLen && i < _deci2Handlers.Length; i++)
        {
            bool has = r.ReadBoolean();
            if (has) { uint dev = r.ReadUInt32(); uint buf = r.ReadUInt32(); _deci2Handlers[i] = (dev, buf); }
            else _deci2Handlers[i] = null;
        }
        Handled = r.ReadUInt64(); Unknown = r.ReadUInt64();
        for (int i = 0; i < RecentSyscalls.Length; i++) RecentSyscalls[i] = r.ReadUInt32();
        _recentSyscallIdx = r.ReadInt32();
        SifDmaCalls = r.ReadUInt64(); SifGetRegCalls = r.ReadUInt64();
        _syscallHistogram.Clear();
        int nHist = r.ReadInt32();
        for (int i = 0; i < nHist; i++) { uint k = r.ReadUInt32(); int v = r.ReadInt32(); _syscallHistogram[k] = v; }
        LastCreatedThreadEntry = r.ReadUInt32();
        LastCreatedThreadStack = r.ReadUInt32();
        _realRpc.ReadState(r);
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
            if (phys != 0 && phys < SystemMemory.RDRAM_SIZE)
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
            case 0x00: // RFU000_FullReset — soft accept (no full machine rebuild mid-title)
            case 0x01: // ResetEE(init_bitfield) — accept; peripherals already live under HLE
                result = 0;
                break;
            case 0x02: // SetGsCrt(interlace, mode, ffmd)
                _system.Hle.CrtMode = a1;
                // Ensure display looks "on" for present path
                _system.Gs.WritePrivileged64(0x12000000, 1); // PMODE EN1
                result = 0;
                break;
            case 0x03: // (unused / RFU) — intentional no-op
                result = 0;
                break;
            case 0x04: // Exit / KExit
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                    Console.Error.WriteLine(
                        $"[EE] Exit({a0}) pc=0x{ee.PC:X8} ra=0x{(uint)ee.GetGpr(31).Lo:X8} " +
                        $"cyc={_system.MasterCycles}");
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_EXIT") == "1")
                    Console.Error.WriteLine($"[EXIT-SYSCALL] code={a0} pc=0x{ee.PC:X8} ra=0x{ee.GetGpr(31).Lo:X8} sp=0x{ee.GetGpr(29).Lo:X8} tid={_kernel.CurrentThreadId} cyc={_system.MasterCycles}");
                _system.Hle.RequestExit((int)a0);
                result = 0;
                break;
            case 0x05: // ResumeIntrDispatch (ps2sdk arbitrary name) — intentional no-op
            case 0x08: // ResumeT3IntrDispatch (alarm update path) — intentional no-op under soft alarms
            case 0x09: // RFU009 — intentional no-op
                result = 0;
                break;
            case 0x06: // LoadExecPS2 — not fully supported
            case 0x07: // ExecPS2
                result = -1;
                break;

            // ---- INTC / DMAC enable ----
            case 0x0A: // AddSbusIntcHandler — no SBUS guest; accept id
            case 0x0B: // RemoveSbusIntcHandler
            case 0x0C: // Interrupt2Iop
                result = 0;
                break;
            case 0x0D: // SetVTLBRefillHandler(cause, handler) — return previous (or BIOS default)
                result = SetExceptionVectorHandler(_vTlbRefillHandlers, (int)a0, a1);
                break;
            case 0x0E: // SetVCommonHandler(cause, handler) — return previous (or BIOS default)
                result = SetExceptionVectorHandler(_vCommonHandlers, (int)a0, a1);
                break;
            case 0x0F: // SetVInterruptHandler(cause, handler) — return previous (or BIOS default)
                result = SetExceptionVectorHandler(_vInterruptHandlers, (int)a0, a1);
                break;
            case 0x10: // AddIntcHandler(cause, handler, next, arg, flag)
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_HANDLERS") == "1")
                    Console.Error.WriteLine($"[ADDINTC] cause={a0} handler=0x{a1:X8}");
                // Append to the per-cause chain (real BIOS linked list). Do NOT replace —
                // Burnout 3 registers three VBlankStart handlers; keeping only the last
                // left the VBlank thread-wakeup at 0x2370A0 dead and wedged boot on a
                // SleepThread flag poll at 0x23719x (flags @ gp-23820 never set).
                if (!_intcHandlers.TryGetValue((int)a0, out var intcList))
                {
                    intcList = new List<uint>(4);
                    _intcHandlers[(int)a0] = intcList;
                }
                intcList.Add(a1);
                result = intcList.Count - 1; // handler id within this cause
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
                // If this cause already fired (sticky STAT) and the no-handler path consumed
                // its COP0 latch before we owned it, re-arm so the newly registered handler
                // actually runs. God of War: VBlankStart raised at cyc≈250k, AddIntcHandler
                // cause=2 arrives after Timer2's registration already flipped TakeExceptions
                // and ClearCpuLatch'd the still-unhandled VBlank edge.
                if (a0 < 15)
                    _system.Intc.RearmCpuLatch((Intc.InterruptSource)(int)a0);
                break;
            case 0x11: // RemoveIntcHandler(cause, id) — clear the whole cause chain for now
                _intcHandlers.Remove((int)a0);
                _intcNextIndex.Remove((int)a0);
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
            case 0x14: // EnableIntc(cause) — OR the cause bit into INTC_MASK
            case 0x1A: // iEnableIntc after abs(-0x1a) — same mask OR
                if (a0 < 15)
                {
                    uint bit = 1u << (int)a0;
                    _system.Intc.SetMask(_system.Intc.Mask | bit);
                    _system.Intc.RearmCpuLatch((Intc.InterruptSource)(int)a0);
                }
                result = 1;
                break;
            case 0x15: // DisableIntc(cause)
            case 0x1B: // iDisableIntc after abs(-0x1b)
                if (a0 < 15)
                    _system.Intc.SetMask(_system.Intc.Mask & ~(1u << (int)a0));
                result = 1;
                break;
            case 0x16: // EnableDmac(channel) — arm D_STAT mask + INTC DmaController
            case 0x1C: // iEnableDmac after abs(-0x1c)
                if (a0 < 10)
                {
                    _system.Dmac.EnableChannelIrq((int)a0);
                    _system.Intc.SetMask(_system.Intc.Mask | (1u << (int)Intc.InterruptSource.DmaController));
                    // Same rationale as AddIntcHandler: once the game owns a DMA completion
                    // callback it must be allowed to take the IRQ (Burnout 3 path-sync drain).
                    _system.EE.TakeExceptions = true;
                    // If this channel (or any enabled channel) already completed before the
                    // mask was armed, Raise so the handler actually runs (sticky D_STAT).
                    if (_system.Dmac.HasPendingChannelIrq())
                        _system.Intc.Raise(Intc.InterruptSource.DmaController);
                    else
                        _system.Intc.RearmCpuLatch(Intc.InterruptSource.DmaController);
                }
                result = 1;
                break;
            case 0x17: // DisableDmac(channel)
            case 0x1D: // iDisableDmac after abs(-0x1d)
                if (a0 < 10)
                    _system.Dmac.DisableChannelIrq((int)a0);
                result = 1;
                break;
            // ---- Alarms (ps2sdk: time in H-SYNC ticks; public nums 0xFC/0xFE, internal 0x18/0x19;
            // i* after abs: 0x1E/0x1F internal, 0xFD/0xFF public) ----
            case 0x18: // _SetAlarm(time, cb, common)
            case 0x1E: // _iSetAlarm
            case 0xFC: // SetAlarm
            case 0xFD: // iSetAlarm
                result = SetEeAlarm(a0, a1, a2);
                break;
            case 0x19: // _ReleaseAlarm(id)
            case 0x1F: // _iReleaseAlarm
            case 0xFE: // ReleaseAlarm
            case 0xFF: // iReleaseAlarm
                result = ReleaseEeAlarm((int)a0);
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
            case 0x26: // iTerminateThread after abs(-0x26)
                result = _kernel.DeleteThread((int)a0);
                break;
            case 0x27: // DisableDispatchThread — not supported on retail EE (ps2sdk comment)
            case 0x28: // EnableDispatchThread — intentional no-op
                result = 0;
                break;
            case 0x29: // ChangeThreadPriority(tid, priority) — lower value = higher priority
            case 0x2A: // iChangeThreadPriority after abs(-0x2a)
                {
                    int oldPrio = _kernel.ChangeThreadPriority((int)a0, (int)a1);
                    result = oldPrio;
                    // Self-priority change may need a yield so a higher-prio peer runs first.
                    if (oldPrio >= 0 && ((int)a0 == 0 || (int)a0 == _kernel.CurrentThreadId))
                        _kernel.SwitchToNext(ee);
                }
                break;
            case 0x2B: // RotateThreadReadyQueue
            case 0x2C: // iRotateThreadReadyQueue after abs(-0x2c)
                _kernel.SwitchToNext(ee);
                result = 0;
                break;
            case 0x2D: // ReleaseWaitThread — THREADMAN KeReleaseWait (0xfffffe5e) on waiter
            case 0x2E: // iReleaseWaitThread after abs(-0x2e)
                result = _kernel.ReleaseWaitThread((int)a0);
                break;
            case 0x2F: // GetThreadId
                result = _kernel.CurrentThreadId;
                break;
            case 0x30: // ReferThreadStatus(id, ee_thread_status_t* out)
            case 0x31: // iReferThreadStatus — same semantics, interrupt-safe variant
                result = ReferThreadStatus((int)a0, a1);
                break;
            case 0x32: // SleepThread — switch to another runnable thread
                {
                    // THREADMAN: pending WakeupCount is consumed without parking. Only yield
                    // when we actually slept; the no-runnable fallback must not WakeupThread
                    // a still-awake self (that would bump WakeupCount per decomp FUN_000020e4's
                    // "not waiting" path and poison the next Sleep).
                    _kernel.SleepThread();
                    var selfAfter = _kernel.GetThread(_kernel.CurrentThreadId);
                    if (selfAfter != null && selfAfter.Sleeping)
                    {
                        if (!_kernel.SwitchToNext(ee))
                            _kernel.WakeupThread(_kernel.CurrentThreadId);
                    }
                    result = 0;
                }
                break;
            case 0x33: // WakeupThread
            case 0x34: // iWakeupThread — same semantics, interrupt-safe variant
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_WAKEUP") == "1")
                    Console.Error.WriteLine($"[WAKEUP] from tid={_kernel.CurrentThreadId} target={a0} cyc={_system.MasterCycles}");
                result = _kernel.WakeupThread((int)a0);
                break;
            case 0x35: // CancelWakeupThread — THREADMAN FUN_000022dc: return+clear wakeup count
            case 0x36: // iCancelWakeupThread after abs(-0x36)
                result = _kernel.CancelWakeupThread((int)a0);
                break;
            case 0x37: // SuspendThread(tid) — was a no-op stub; ADX thrash path
                {
                    int sid = (int)a0;
                    if (sid == 0) sid = _kernel.CurrentThreadId;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_SUSPEND") == "1")
                        Console.Error.WriteLine(
                            $"[SUSPEND] from=tid{_kernel.CurrentThreadId} target={sid} ra=0x{ee.GetGpr(31).Lo:X8} " +
                            $"pc=0x{ee.PC:X8} cyc={_system.MasterCycles}");
                    var target = _kernel.GetThread(sid);
                    // DORMANT pump worker only (entry 0x4147F8). One-shot waiters at
                    // 0x4145A8/0x414600/0x414708 intentionally ExitThread after flags are set;
                    // re-Starting them on every Suspend caused Start→see-flag→Exit thrash
                    // (live: Suspend target=3 from ra=0x414A9C forever).
                    if (target != null && target.Alive && !target.Started
                        && target.Entry == 0x004147F8u)
                    {
                        _kernel.StartAndMaybeSwitch(ee, sid, switchNow: false, arg: 0, fromSyscall: false);
                        target = _kernel.GetThread(sid);
                    }
                    // Suspending a still-DORMANT/missing thread: success, no full-EE freeze.
                    if (target == null || !target.Alive || !target.Started)
                    {
                        result = 0;
                        _kernel.SwitchToNext(ee);
                        break;
                    }
                    // Already suspended: success without re-nesting.
                    if ((target.SuspendCount > 0 || target.SoftSuspended) && sid != _kernel.CurrentThreadId)
                    {
                        result = 0;
                        _kernel.SwitchToNext(ee);
                        break;
                    }
                    result = _kernel.SuspendThread(sid);
                    // Self-suspend must yield or the caller spins forever.
                    if (sid == _kernel.CurrentThreadId)
                    {
                        if (!_kernel.SwitchToNext(ee))
                        {
                            // Deadlock break: main Suspend(self) with every peer Sleep/Suspend'd
                            // freezes the whole EE (RequestSemaStall). Before parking, wake pure
                            // SleepThread peers so the ADX pump (0x4147F8) can Resume main.
                            foreach (var peer in _kernel.AllThreads)
                            {
                                if (peer.Id == sid || !peer.Alive || !peer.Started) continue;
                                if (peer.WaitSemaId != 0) continue; // real WaitSema has its own producer
                                if (peer.SuspendCount > 0)
                                {
                                    while (peer.SuspendCount > 0)
                                        _kernel.ResumeThread(peer.Id);
                                }
                                else if (peer.Sleeping)
                                    _kernel.WakeupThread(peer.Id);
                            }
                            if (!_kernel.SwitchToNext(ee))
                            {
                                ee.RequestSemaStall();
                                _kernel.WaitSemaVblank();
                            }
                        }
                    }
                    else
                    {
                        // Suspending a peer: try to yield so the suspendee is off-CPU.
                        // Do NOT WaitSemaVblank if nobody else is runnable — that freezes the
                        // entire EE (including workers) until the next PCRTC edge, which with
                        // SoftSuspended peers made post-WAD boot crawl (~7k syscalls / 150M)
                        // while PC sat on the Suspend stub.
                        _kernel.SwitchToNext(ee);
                    }
                }
                break;
            case 0x38: // iSuspendThread
                result = _kernel.SuspendThread((int)a0 == 0 ? _kernel.CurrentThreadId : (int)a0);
                break;
            case 0x39: // ResumeThread
            case 0x3A: // iResumeThread
                result = _kernel.ResumeThread((int)a0 == 0 ? _kernel.CurrentThreadId : (int)a0);
                break;
            case 0x3B: // RFU059 (ps2sdk) — not JoinThread; EE has no JoinThread syscall.
                       // Retail returns a u8 status; HLE returns 0 (idle/no residual).
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
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine(
                        $"[RPC] CreateSema -> id={result} tid={_kernel.CurrentThreadId} pc=0x{ee.PC:X8} " +
                        $"ra=0x{ee.GetGpr(31).Lo:X8} init={_kernel.GetSemaInitCount((int)result)} max={_kernel.GetSemaMaxCount((int)result)}");
                break;
            case 0x41: // DeleteSema
                result = _kernel.DeleteSema((int)a0);
                break;
            case 0x42: // SignalSema
                {
                    // Real THREADMAN returns the semaphore id on success (not remaining count).
                    // libcdvd / SN ProDG check `SignalSema(id) == id`. DETPS2_SIGNALSEMA_COUNT=1
                    // returns remaining count for A/B diagnostics.
                    int sr = _kernel.SignalSema((int)a0);
                    if (sr < 0) result = sr;
                    else if (Environment.GetEnvironmentVariable("DETPS2_SIGNALSEMA_COUNT") == "1")
                        result = sr;
                    else result = (int)a0;
                }
                break;
            case 0x44: // WaitSema — block + yield to another thread when empty
                {
                    // Auto-create missing semas (titles sometimes Wait before Create races).
                    // Must be a non-mutating existence check — WaitSemaBlocking decrements the
                    // count as a side effect on success, so probing with it here would silently
                    // consume a legitimate signal (e.g. one our own synchronous SIF RPC handling
                    // just posted) before the real wait below ever sees it, forcing a spurious
                    // block on every semaphore that starts at count 1.
                    // EnsureSema(id) materializes the *requested* id — plain CreateSema returns
                    // a fresh _nextSema id and left WaitSemaBlocking(a0) still missing → fake
                    // success (wr=-1, LastWaitSemaBlocked=false → result 0).
                    if (a0 != 0 && !_kernel.SemaExists((int)a0))
                        _kernel.EnsureSema((int)a0, init: 0, max: 1);
                    int wr = _kernel.WaitSemaBlocking((int)a0);
                    if (_kernel.LastWaitSemaBlocked)
                    {
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                        {
                            Console.Error.WriteLine($"[RPC] WaitSema BLOCKED a0(sema)=0x{a0:X} tid={_kernel.CurrentThreadId} pc=0x{ee.PC:X8} ra=0x{ee.GetGpr(31).Lo:X8} sp=0x{ee.GetGpr(29).Lo:X8} gp=0x{ee.GetGpr(28).Lo:X8}");
                            foreach (var t in _kernel.AllThreads)
                                Console.Error.WriteLine(
                                    $"[RPC]   thread id={t.Id} alive={t.Alive} started={t.Started} sleeping={t.Sleeping} " +
                                    $"waitVblank={t.WaitVblank} suspend={t.SuspendCount} waitSemaId={t.WaitSemaId} priority={t.Priority}");
                        }
                        // Stall only when a *queued* real BIND/CALL/RDATA will SignalSema(this
                        // id) via CompleteRpcEnd. Queue depth alone is not enough: GoW/B3
                        // WaitSema(3) on the SIF-cmd poll mutex saw unrelated CDVD/PAD packets
                        // in the queue, RequestSemaStall'd, and froze the whole EE forever
                        // (wrong-sema deadlock — SEMA_STALL_YIELD is OFF for MK WAD). Matching
                        // client sema ⇒ real RPC_END path (SHARED). No match ⇒ yield/fabricate
                        // like a non-RPC wait (SIF-cmd poll / mutex re-lock).
                        // Do NOT TryYield when our packet is queued: that diverted MK's ADX/WAD
                        // path (cdvdSectors collapsed 198k→1).
                        if (_system.Sif.QueueMaySignalSema(_system.Memory, (int)a0))
                        {
                            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                                Console.Error.WriteLine($"[RPC] WaitSema STALLING for real completion sema=0x{a0:X} pc=0x{ee.PC:X8}");
                            ee.RequestSemaStall();
                        }
                        else if (_system.ActiveQuirk is WhiplashAssist)
                        {
                            // WHIP_SEMA_FIX_V3: soft-signal SN seq WaitSema + force timeslice end.
                            // V2 without RequestImmediatePreempt burned ~333k WaitSema/Wakeup pairs
                            // (stream-init / title Open) while main starved — px 3→0, syscalls~670k.
                            // Must NOT apply fabricate+preempt to all titles (GoW/Dec residual).
                            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                                Console.Error.WriteLine($"[RPC] WaitSema FABRICATING signal for sema=0x{a0:X} (WHIP_SEMA_FIX_V3)");
                            if (_kernel.ThreadCount < 2)
                                _kernel.WaitSemaVblank();
                            _kernel.SignalSema((int)a0);
                            _kernel.WakeupThread(_kernel.CurrentThreadId);
                            _kernel.RequestImmediatePreempt();
                        }
                        else if (!_kernel.TryYieldToOtherRunnable(ee))
                        {
                            // SHARED: yield to peer first; fabricate only when alone.
                            // Restores GoW MOD_LOAD/cdvd and Dec PADDATAEX residual past WaitSema(3).
                            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                                Console.Error.WriteLine($"[RPC] WaitSema FABRICATING signal for sema=0x{a0:X} (no matching RPC / no runnable thread)");
                            _kernel.WaitSemaVblank();
                            _kernel.SignalSema((int)a0);
                            _kernel.WakeupThread(_kernel.CurrentThreadId);
                        }
                        // On block (and on later wake): return the sema id as the success token
                        // so SN ProDG `WaitSema(id) == id` checks pass. Same rationale as SignalSema.
                        result = (int)a0;
                    }
                    else if (wr < 0)
                        result = wr; // missing sema / hard error
                    else
                        result = (int)a0; // acquired without sleep — return id, not remaining count
                }
                break;
            case 0x43: // iSignalSema — interrupt-safe SignalSema (Sony EE #67)
                {
                    int ir = _kernel.ISignalSema((int)a0);
                    result = ir < 0 ? ir : (int)a0;
                }
                break;
            case 0x45: // PollSema — non-blocking (never sleep); BIOS THREADMAN PollSema
            case 0x46: // iPollSema — same rules, interrupt context
                {
                    // DETPS2_POLLSEMA_COUNT=1: return remaining count (legacy). Default: return
                    // semaphore id on success — required by libcdvd _CdCheckSCmd/NCmd
                    // (PollSema(id)==id). Count return made DualInfo fail forever (GoW).
                    int pr = _kernel.PollSema((int)a0);
                    if (pr < 0)
                        result = pr;
                    else if (Environment.GetEnvironmentVariable("DETPS2_POLLSEMA_COUNT") == "1")
                        result = pr;
                    else
                        result = (int)a0;
                }
                break;
            case 0x47: // ReferSemaStatus
            case 0x48: // iReferSemaStatus — same fill, interrupt-safe variant
                result = ReferSemaStatus((int)a0, a1);
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
            case 0x51: // DeleteEventFlag — object delete not exposed on KernelState (residual);
                       // return success so callers that Create+Delete without Wait do not panic.
                result = 0;
                break;
            case 0x52: // SetEventFlag
            case 0x53: // iSetEventFlag
                result = _kernel.SetEventFlag((int)a0, a1);
                // Wake any threads parked in WaitEventFlag (case 0x56 below) whose condition is
                // now satisfied by this update — mirrors SignalSema's own "wake matching waiters"
                // step, since KernelState.SetEventFlag itself has no memory access to perform the
                // *result_ptr write real WaitEventFlag callers expect on wake.
                foreach (var t in _kernel.AllThreads)
                {
                    if (!t.Alive || !t.Sleeping || t.WaitEfId != (int)a0) continue;
                    if (!_kernel.EventFlagSatisfied(t.WaitEfId, t.WaitEfPattern, t.WaitEfMode)) continue;
                    uint bits = _kernel.ConsumeEventFlag(t.WaitEfId, t.WaitEfPattern, t.WaitEfMode);
                    if (t.WaitEfResultAddr != 0) _system.Memory.Write32(t.WaitEfResultAddr, bits);
                    t.WaitEfId = 0;
                    _kernel.WakeupThread(t.Id);
                }
                break;
            case 0x54: // ClearEventFlag
            case 0x55: // iClearEventFlag
                result = _kernel.ClearEventFlag((int)a0, a1);
                break;
            case 0x56: // WaitEventFlag(ef_id, pattern, mode, result_ptr) — real ps2sdk semantics:
                       // block until (bits & pattern) satisfies mode (OR = mode bit 0x01, AND =
                       // default; clear-on-exit = mode bit 0x10), writing the satisfying bits to
                       // *result_ptr. Previously ignored a1 (pattern)/a2 (mode)/a3 (result_ptr)
                       // entirely and returned the raw current bits as a status code with no
                       // blocking - a caller checking v0==0 for success against nonzero real bits
                       // would see a spurious "error" and retry immediately, forever, without ever
                       // yielding to another thread: a busy spin (confirmed via PC profiling as
                       // this session's remaining bottleneck after the interrupt-storm fix above).
                {
                    bool satisfied = _kernel.EventFlagSatisfied((int)a0, a1, a2);
                    if (satisfied)
                    {
                        uint bits = _kernel.ConsumeEventFlag((int)a0, a1, a2);
                        if (a3 != 0) _system.Memory.Write32(a3, bits);
                        result = 0;
                    }
                    else
                    {
                        _kernel.ParkOnEventFlag(_kernel.CurrentThreadId, (int)a0, a1, a2, a3);
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                            Console.Error.WriteLine($"[RPC] WaitEventFlag BLOCKED ef={a0} pattern=0x{a1:X} mode=0x{a2:X} pc=0x{ee.PC:X8}");
                        if (!_kernel.SwitchToNext(ee))
                        {
                            // Nobody else runnable: fabricate satisfaction like WaitSema's own
                            // fallback does, rather than deadlocking the whole system over one
                            // thread's wait.
                            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                                Console.Error.WriteLine($"[RPC] WaitEventFlag FABRICATING signal for ef={a0} (no runnable thread)");
                            _kernel.SetEventFlag((int)a0, a1);
                            uint bits = _kernel.ConsumeEventFlag((int)a0, a1, a2);
                            if (a3 != 0) _system.Memory.Write32(a3, bits);
                            var self = _kernel.GetThread(_kernel.CurrentThreadId);
                            if (self != null) { self.Sleeping = false; self.WaitEfId = 0; }
                            _kernel.WakeupThread(_kernel.CurrentThreadId);
                        }
                        result = 0; // pre-set for when this thread's context is restored on wake
                    }
                }
                break;
            case 0x57: // PollEventFlag (DetPS2 live ABI; ps2sdk 0x57 = GetTLBEntry — see EE_KERNEL_SYSCALLS.md)
            case 0x58: // iPollEventFlag
                result = (long)_kernel.PollEventFlag((int)a0);
                break;
            case 0x59: // ExpandScratchPad (ps2sdk) — no TLB/scratch remap; success 0
                result = 0;
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
            case 0x5C: // EnableIntcHandler — handlers always "enabled" once registered
            case 0x5D: // DisableIntcHandler — intentional no-op (chain kept)
            case 0x5E: // EnableDmacHandler
            case 0x5F: // DisableDmacHandler
                result = 0;
                break;
            case 0x60: // KSeg0
            case 0x61: // EnableCache
            case 0x62: // DisableCache
                result = 0;
                break;
            case 0x63: // GetCop0
            case 0x67: // iGetCop0 after abs(-0x67)
                result = (long)ee.ReadCop0Public((int)a0);
                break;
            case 0x64: // FlushCache
            case 0x66: // CpuConfig
            case 0x68: // iFlushCache after abs(-0x68)
            case 0x69: // RFU105 — intentional no-op
            case 0x6A: // iCpuConfig after abs(-0x6a)
                result = 0;
                break;

            // ---- SIF / timers / OSD ----
            case 0x6B: // SifStopDma
                result = 0;
                break;
            case 0x6C: // SetCPUTimerHandler — COP0 Compare timer not wired; accept registration
            case 0x6D: // SetCPUTimer — intentional no-op (no EE hard-timer fire path yet)
            case 0x6E: // SetOsdConfigParam2
            case 0x6F: // GetOsdConfigParam2
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
            case 0x72: // SetPgifHandler — PGIF path unused under HLE; intentional no-op
                result = 0;
                break;
            case 0x73: // SetVSyncFlag(u32* oddField, u32* evenField) — ps2sdk sceSetVSyncFlag
                // Stores EE RAM pointers; kernel writes field counters on each VBlank.
                // Flat no-op left MK calling this ~500k times / 200M with no progress.
                _vsyncFlagOdd = a0;
                _vsyncFlagEven = a1;
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
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_SIFSETDMA") == "1")
                    Console.Error.WriteLine($"[SIFSETDMA] a0=0x{a0:X8} a1={a1} result={result} tid={_kernel.CurrentThreadId} cyc={_system.MasterCycles}");
                break;
            case 0x78: // SifSetDChain
                result = 0;
                break;
            case 0x79: // SifSetReg(reg, val)
                if ((a0 & 0x80000000u) != 0)
                {
                    _sifVirtualRegs[(a0 & 0x1F)] = a1;
                }
                else if (a0 == Sif.SifRegSmFlag)
                {
                    // SMFLAG is write-1-to-clear (ps2sdk SifIopReset clears BOOTEND/SIFINIT/CMDINIT
                    // by writing the corresponding SIF_STAT_* bits).
                    _system.Sif.ClearSmFlagBits(a1);
                    _sifRegs[Sif.SifRegSmFlag] = _system.Sif.SmFlag;
                    _system.Sif.WriteRegister(0x30, _system.Sif.SmFlag);
                }
                else if (a0 < _sifRegs.Length)
                {
                    _sifRegs[a0] = a1;
                    // Mirror MSFLAG onto SIF MMIO (offset 0x20). Do not write MAINADDR
                    // through MsCom (that would enqueue a fake SBUS command).
                    if (a0 == Sif.SifRegMsFlag) _system.Sif.WriteRegister(0x20, a1);
                }
                result = 0;
                break;
            case 0x7A: // SifGetReg
                {
                    SifGetRegCalls++;
                    if (a0 == Sif.SifRegSmFlag)
                    {
                        // Deferred IOP reboot completion: real SifIopReset clears SMFLAG bits
                        // *after* RESET_CMD DMA; EESYNC re-posts BOOTEND once IOP reloads.
                        // Complete on first SMFLAG poll after those clears.
                        if (_system.Sif.TryCompletePendingIopReboot())
                            OnIopRebootCompleted();
                        result = _system.Sif.SmFlag;
                        _sifRegs[Sif.SifRegSmFlag] = _system.Sif.SmFlag;
                        break;
                    }
                    if (a0 == 5) // SUBRESET / legacy ready poll — treat as SMFLAG snapshot
                    {
                        if (_system.Sif.TryCompletePendingIopReboot())
                            OnIopRebootCompleted();
                        result = _system.Sif.SmFlag;
                        break;
                    }
                    if ((a0 & 0x80000000u) != 0) { result = _sifVirtualRegs[(a0 & 0x1F)]; break; }
                    // SUBADDR: ensure non-zero when IOP CMD layer is up (sceSifInitCmd path).
                    if (a0 == Sif.SifRegSubAddr && _sifRegs[Sif.SifRegSubAddr] == 0 && _system.Sif.CmdInitApplied)
                        _sifRegs[Sif.SifRegSubAddr] = Sif.DefaultIopSifCmdBufAddr;
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
        0x2B or 0x2F or 0x32 or 0x33 or 0x34 => true, // rotate/id/sleep/wakeup(+i)
        0x3C or 0x3D or 0x3E => true, // SetupThread/Heap
        // Semas: include iSignalSema (0x43) for SetAlarm callbacks (Vexx CdSync).
        0x40 or 0x41 or 0x42 or 0x43 or 0x44 or 0x45 or 0x46 => true,
        // EE soft alarms — must HLE even if title SetSyscall-hooks them.
        0x18 or 0x19 or 0x1E or 0x1F or 0xFC or 0xFD or 0xFE or 0xFF => true,
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
        bool trace = Environment.GetEnvironmentVariable("DETPS2_TRACE_SIFSETDMA") == "1";
        if (listAddr == 0 || count == 0)
        {
            if (trace) Console.Error.WriteLine($"[SIFSETDMA] EARLY-ZERO listAddr=0x{listAddr:X8} count={count} cyc={_system.MasterCycles}");
            return 0;
        }
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
            {
                _system.Sif.Sif1EeToIop(src, dest, size);
                // CRI DTX (and any future IOP consumer of EE→IOP bulk DMA) needs an IOP-side
                // completion signal after the payload lands. RealSifRpc tracks CRI DTX channels
                // created via sid=0x90000200 fno=2 and advances their EE work-buffer counter.
                // Pass EE src so SearchFile (and similar in-out RPCs) can write CdlFILE results
                // back to the EE send buffer (Play! modifies args in place).
                _realRpc.NotifyDtxEeToIopDma(_system.Memory, dest, size, src);
            }
        }

        for (uint i = 0; i < count; i++)
        {
            if (!eeToIop[(int)i] || sizes[(int)i] < 16 || sizes[(int)i] > 0x200000) continue;
            HleSifCmdFromEe(srcs[(int)i], sizes[(int)i]);
        }

        // Opportunistically free up any real RPC packets queued in an *earlier* generation
        // than this one — see DrainRealRpcQueue's own doc comment for why a title's own
        // tight bind-retry loop needs this mid-EE.Step() drain, not just the once-per-tick
        // ambient one, to avoid exhausting the real EE-side packet pool.
        // LOADFILE/FILEIO retail BIND/CALL go through this real-RPC path (generation-gated),
        // not the simplified Sif.Step queue below.
        DrainRealRpcQueue(_system.SchedulerGeneration);

        // M1-e: do not bulk-complete simplified HLE RPC mid-SifSetDma (audit ~line 1606).
        // Prefer only DrainRealRpcQueue above; ambient Sif.Step drains _rpcPacketAddrs next
        // slice. DETPS2_DISABLE_M1E_SYSCALL_SIF=1 restores legacy Step(64).
        _system.Sif.StepFromSyscall(64);
        // Mark SMFLAG that IOP saw the transfer (retail polls SIFINIT) — but not during a
        // pending IOP reboot, where EE deliberately clears SIFINIT/CMDINIT after this returns.
        if (!_system.Sif.IopRebootPending)
            _system.Sif.ApplySifInit();
        // Return a non-zero DMA id; -1 from SifDmaStat means complete
        return unchecked((int)(1 + (count & 0x7FFF)));
    }

    /// <summary>
    /// Minimal SIFCMD HLE: parse EE-built command packet and synthesize IOP-side
    /// completion so sceSif* / Midway cmd handlers can advance.
    /// Header (16 bytes): +0 sizes, +4 dest, +8 cid, +12 opt.
    /// <para>
    /// LOADFILE (sid 0x80000006) never appears as a raw SIFCMD cid here — retail uses
    /// sifrpc BIND/CALL packets (cid 0x80000009/0x0A) which are queued below and answered
    /// by <see cref="DrainRealRpcQueue"/> → <see cref="RealSifRpc"/> HLE, or (future WP-22
    /// under <c>DETPS2_LITERAL_IRX=1</c> + <see cref="PreferLiveLoadFileRpc"/>) by live IOP
    /// LOADFILE.IRX. EE→IOP DMA already copied the packet; see <c>docs/irx/EE_LOADFILE.md</c>.
    /// </para>
    /// </summary>
    private void HleSifCmdFromEe(uint eePacket, uint size)
    {
        // Real RPC bind/call (cid 0x80000009/0x8000000A) — the protocol retail-compiled
        // sifrpc.c actually speaks. On real hardware this can only ever be answered by the
        // IOP, a separate chip reachable solely over the narrow SIF bus — never instantly,
        // never within the same instruction that issued the request. Queue it for
        // DrainRealRpcQueue (called once per ambient scheduler tick, see Sif.cs's
        // _realRpcQueue doc comment) instead of handling it here.
        // Under DETPS2_LITERAL_IRX=1 the same queue is used: HLE answers until PreferLiveLoadFileRpc
        // is armed for a live IOP LOADFILE server (default off — does not break HLE=0 bisect).
        if (size >= 16 && RealSifRpc.IsRealRpcPacket(_system.Memory, eePacket))
        {
            _system.Sif.SubmitRealRpc(eePacket, _system.SchedulerGeneration);
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

        // System commands (Sony SIFCMD.IRX — BIOS FUN_000006c0 registers these).
        // CIDs: CHANGE_SADDR=0, SET_SREG=1, INIT_CMD=2, RESET_CMD=3 (sifcmd-common.h).
        switch (cid)
        {
            case 0x80000000: // CHANGE_SADDR / SIF_CMD_CHANGE_SADDR
                // EE publishes its receive buffer; IOP also tracks reverse. Store as MAINADDR-ish.
                if (size >= 0x14)
                {
                    uint buf = _system.Memory.Read32(eePacket + 0x10);
                    if (buf != 0) _sifRegs[Sif.SifRegMainAddr] = buf;
                }
                else if (dest != 0)
                    _sifRegs[Sif.SifRegMainAddr] = dest;
                // ProDG / retail cmd-handler SDKs register a DMAC-5 consumer that drains
                // IOP→EE packets from this buffer. After the EE publishes MAINADDR, IOP
                // SIFCMD typically posts a SET_SREG(SIF_SREG_RPCINIT, 1) style notify so
                // the EE handler can mark its ready-flag table (Burnout 3 @ 0x4E4140,
                // MK:DA @ 0x40C780, MK:Deception @ 0x5D8840 — shared SN ProDG pattern).
                DeliverIopSifCmdToEe(0x80000001, 0, 0, 1);
                break;
            case 0x80000001: // SET_SREG — packet: SifCmdSRegData_t { hdr(16), index, value }
                {
                    uint idx = size >= 0x18 ? _system.Memory.Read32(eePacket + 0x10) : opt;
                    uint val = size >= 0x18 ? _system.Memory.Read32(eePacket + 0x14) : dest;
                    uint reg = idx & 0x1F;
                    if (reg < _sifRegs.Length) _sifRegs[reg] = val;
                    if (reg < _sifVirtualRegs.Length) _sifVirtualRegs[reg] = val;
                    // SIF_SREG_RPCINIT (index 0): IOP ack that RPC init completed — also
                    // reflect into SIF_SYSREG_RPCINIT so sceSifInitRpc's GetReg path sees it.
                    if (reg == 0 && val != 0)
                        _sifVirtualRegs[2] = val;
                    // SMFLAG-style boot bits in value — OR into hardware SMFLAG (IOP→EE post).
                    if ((val & Sif.SifStatIopBootReady) != 0)
                        _system.Sif.WriteRegister(0x30, _system.Sif.ReadRegister(0x30) | (val & Sif.SifStatIopBootReady));
                    // Echo SET_SREG back to EE receive buffer so registered cmd handlers see it
                    // (real IOP SIFCMD reverse-path; required for ProDG flag-table SDKs).
                    DeliverIopSifCmdToEe(0x80000001, 0, idx, val);
                }
                break;
            case 0x80000002: // INIT_CMD — SIFCMD.IRX FUN_0000006c
                // opt==0: SIFCMD init → CMDINIT + publish SUBADDR
                // opt!=0: RPC init path → set SREG/SYSREG RPCINIT (sceSifInitRpc)
                _system.Sif.ApplySifInit();
                _system.Sif.ApplyCmdInit();
                if (_sifRegs[Sif.SifRegSubAddr] == 0)
                    _sifRegs[Sif.SifRegSubAddr] = Sif.DefaultIopSifCmdBufAddr;
                if (_sifVirtualRegs[0] == 0)
                    _sifVirtualRegs[0] = Sif.DefaultIopSifCmdBufAddr;
                if (opt != 0)
                {
                    // RPC init: equivalent of IOP SET_SREG(SIF_SREG_RPCINIT, 1)
                    _sifRegs[0] = 1;
                    _sifVirtualRegs[2] = 1;
                    DeliverIopSifCmdToEe(0x80000001, 0, 0, 1);
                }
                _sifRegs[Sif.SifRegSmFlag] = _system.Sif.SmFlag;
                break;
            case 0x80000003: // RESET_CMD — SifIopReset / REBOOT.IRX payload
                // SifCmdResetData_t (ps2sdk iopcontrol.c): header(16) + arglen + mode + arg[80].
                // Defer SMFLAG re-post: EE clears SIFINIT/CMDINIT *after* this DMA returns.
                // Completion (SIFINIT+CMDINIT+EESYNC BOOTEND) runs on next SMFLAG GetReg.
                {
                    int argLen = size >= 0x18 ? (int)_system.Memory.Read32(eePacket + 0x10) : 0;
                    int mode = size >= 0x18 ? (int)_system.Memory.Read32(eePacket + 0x14) : (int)opt;
                    if (argLen < 0) argLen = 0;
                    if (argLen > Sif.IopRebootArgMax) argLen = Sif.IopRebootArgMax;
                    string arg = "";
                    if (argLen > 0 && size >= 0x18u + (uint)argLen)
                    {
                        var sb = new System.Text.StringBuilder(argLen);
                        for (int i = 0; i < argLen; i++)
                        {
                            byte c = _system.Memory.Read8(eePacket + 0x18 + (uint)i);
                            if (c == 0) break;
                            if (c >= 0x20 && c < 0x7F) sb.Append((char)c);
                        }
                        arg = sb.ToString();
                    }
                    _system.Sif.MarkIopRebootPending(arg, mode, argLen);
                }
                _sifVirtualRegs[2] = 0; // SYSREG_RPCINIT cleared like SifIopReset
                _sifVirtualRegs[0] = 0; // SYSREG_SUBADDR cleared
                _sifRegs[Sif.SifRegSubAddr] = 0;
                break;
            case 0x80000008: // RPC_END arriving EE→IOP (unusual) — treat as free/ack
                break;
            default:
                break;
        }

        // EE SIF library effect of a successful IOP ack (normally _SifCmdIntHandler after
        // SIF0 DMA). sceSifInitRpc polls a ready-slot table — without this write the EE
        // spins forever even when SMFLAG already has CMDINIT|BOOTEND (BIOS HLE path).
        AcknowledgeEeSifCmdReady(cid);

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

    /// <summary>
    /// Deliver one IOP→EE SIFCMD packet into the EE receive buffer published via
    /// CHANGE_SADDR / SIF_REG_MAINADDR, then raise the SIF INTC source so a game-registered
    /// AddDmacHandler(SIF0) can drain it.
    /// <para>
    /// Packet layout matches real SIF0 16-byte header + payload (ps2sdk <c>SifCmdHeader_t</c>):
    /// word0 low 8 bits = total packet size in bytes (ProDG handlers <c>lbu</c> this as a
    /// length prefix, then copy that many bytes and clear it); +8 = cid; +16/+20 = optional
    /// SET_SREG index/value. Ground-truthed against PCSX2 SIF0 traces for Burnout 3
    /// (docs/DEVELOPER_GUIDE.md §7.13–7.14): without this write, the EE handler sees a zero
    /// length prefix at the buffer and never reaches the flag-table setter.
    /// </para>
    /// Generic BIOS HLE — no title PCs. Safe no-op when MAINADDR is unset or not in RDRAM.
    /// </summary>
    private void DeliverIopSifCmdToEe(uint cid, uint opt, uint word0, uint word1)
    {
        uint dest = _sifRegs[Sif.SifRegMainAddr];
        if (dest == 0) return;
        dest &= 0x1FFFFFFFu;
        if (dest < 0x1000 || dest + 0x30 >= SystemMemory.RDRAM_SIZE) return;

        // 24-byte packet: 16B header + 8B payload (index, value for SET_SREG).
        const uint pktSize = 0x18;
        _system.Memory.Write32(dest + 0x00, pktSize); // psize in low byte; dsize=0
        _system.Memory.Write32(dest + 0x04, 0);
        _system.Memory.Write32(dest + 0x08, cid);
        _system.Memory.Write32(dest + 0x0C, opt);
        _system.Memory.Write32(dest + 0x10, word0);
        _system.Memory.Write32(dest + 0x14, word1);
        // Pad to 48 bytes like real SIF0 DMA quanta (harmless zeros if handler only copies psize).
        _system.Memory.Write32(dest + 0x18, 0);
        _system.Memory.Write32(dest + 0x1C, 0);
        _system.Memory.Write32(dest + 0x20, 0);
        _system.Memory.Write32(dest + 0x24, 0);
        _system.Memory.Write32(dest + 0x28, 0);
        _system.Memory.Write32(dest + 0x2C, 0);

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine(
                $"[SIFCMD] IOP→EE dest=0x{dest:X8} cid=0x{cid:X8} w0=0x{word0:X8} w1=0x{word1:X8}");
    }

    /// <summary>
    /// Mark EE-side SIFCMD/RPC "queue registered / cmd ready" slots after an EE→IOP
    /// command is accepted. Real hardware: IOP replies over SIF0 → EE DMAC IRQ →
    /// <c>_SifCmdIntHandler</c> fills the handler table. HLE has no IOP R3000, so we
    /// apply the same EE memory side effects here (docs/BIOS_DISSECTION.md §3,
    /// docs/bios-ports/SIFINIT_EESYNC.md).
    /// </summary>
    private void AcknowledgeEeSifCmdReady(uint cid)
    {
        // Do not re-assert SMFLAG during a pending IOP reboot — EE intentionally cleared
        // bits and is waiting for EESYNC's deferred BOOTEND re-post via GetReg.
        if (!_system.Sif.IopRebootPending)
            _system.Sif.PresentIopBootReady();

        Sif.PlantEeSifReadySlots(_system.Memory);
        _sifRegs[Sif.SifRegSmFlag] = _system.Sif.SmFlag;

        // Wake one SIF-cmd / init waiter. Real IOP SIFCMD posts reverse DMA then the EE
        // handler SignalSema's the cmd-queue mutex (often id 3 — GoW/B3 sifrpc poll at
        // WaitSema trampoline). INIT family always woke; also wake on any successful
        // non-RESET cmd so WaitSema(3) is not stuck forever under empty fabricate thrash
        // (wave-3 SHARED — prefer real SIF completion over LOCAL SignalSema pulse).
        // RESET_CMD does not wake here (reboot completion does via GetReg path).
        if (cid != 0x80000003)
        {
            foreach (var t in _kernel.AllThreads)
            {
                if (t.Alive && t.Sleeping && t.WaitSemaId > 0 && t.WaitSemaId <= 16)
                {
                    _kernel.ISignalSema(t.WaitSemaId);
                    break; // one wake per ack, like SignalSema / RPC_END
                }
            }
        }
    }

    /// <summary>
    /// After deferred IOP reboot completes (REBOOT.IRX + SIFINIT + SIFCMD + EESYNC):
    /// re-publish SUBADDR / RPCINIT, re-install IOMAN default devices + STDIO/IGREETING
    /// contracts so a subsequent <c>sceSifInitRpc</c> sees a live post-IOPBTCONF IOP.
    /// </summary>
    private void OnIopRebootCompleted()
    {
        _sifRegs[Sif.SifRegSubAddr] = Sif.DefaultIopSifCmdBufAddr;
        _sifVirtualRegs[0] = Sif.DefaultIopSifCmdBufAddr;
        _sifVirtualRegs[2] = 1;
        _sifRegs[Sif.SifRegSmFlag] = _system.Sif.SmFlag;
        Sif.PlantEeSifReadySlots(_system.Memory);
        _system.Sif.WriteRegister(0x30, _system.Sif.SmFlag);

        // REBOOT.IRX / IOPBTCONF reload side effects (generic HLE — no title PCs):
        // re-present IOMAN device table, STDIO tty sink, IGREETING done flag.
        BiosBootHost.ApplyPostIopRebootContracts(_system);

        // PADMAN open-port table dies with the IOP image; clear so post-reboot OPEN works.
        // Pass reboot arg so LOADFILE GetVersion can return the IOPRP ASCII tag
        // (MK:DA "2430", shared SN ProDG gate — see RealSifRpc.ExtractIopRpVersionAscii).
        // This is UDNL-arg handoff into the HLE GetVersion store — not an EE RAM version plant.
        RealRpc.OnIopReboot(_system.Sif.LastIopRebootArg);

        // WP-22 prep (DETPS2_LITERAL_IRX=1): prefer GetVersion from the real RESET/UDNL arg
        // when present, so classic 0x00020000 does not fight disc IOPRP version gates.
        // LITERAL_IRX=0 / unset: leave PreferIopRpGetVersion alone (title assists / smokes).
        // No GameQuirk plants here — only surface the arg SifIopReset already captured.
        // M8-a B3 dual-suppress: DETPS2_M8A_B3_HOLD_PREFER_OFF=1 skips Prefer auto-set so
        // plant-quiet seats can hold Prefer false (Burnout3Assist also re-clears in Step).
        bool holdPreferOffB3 = Environment.GetEnvironmentVariable("DETPS2_M8A_B3_HOLD_PREFER_OFF") is "1"
            || string.Equals(Environment.GetEnvironmentVariable("DETPS2_M8A_B3_HOLD_PREFER_OFF"),
                "true", StringComparison.OrdinalIgnoreCase);
        if (IopExtendedBiosHost.IsLiteralIrxEnabled()
            && !string.IsNullOrEmpty(RealRpc.LastIopRpVersionAscii)
            && !holdPreferOffB3)
        {
            RealRpc.PreferIopRpGetVersion = true;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1"
                || Environment.GetEnvironmentVariable("DETPS2_TRACE_REBOOT") == "1")
                Console.Error.WriteLine(
                    $"[LOADFILE] LITERAL_IRX: PreferIopRpGetVersion=1 ioprp=\"{RealRpc.LastIopRpVersionAscii}\"");
        }
        else if (holdPreferOffB3
            && (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1"
                || Environment.GetEnvironmentVariable("DETPS2_TRACE_REBOOT") == "1"))
        {
            Console.Error.WriteLine(
                $"[LOADFILE] LITERAL_IRX: PreferIopRp auto-set skipped (DETPS2_M8A_B3_HOLD_PREFER_OFF) ioprp=\"{RealRpc.LastIopRpVersionAscii}\"");
        }

        // Wake one WaitSema sleeper if any (EESYNC post → EE SifIopSync consumer).
        foreach (var t in _kernel.AllThreads)
        {
            if (t.Alive && t.Sleeping && t.WaitSemaId > 0)
            {
                _kernel.ISignalSema(t.WaitSemaId);
                break;
            }
        }
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
        // THS_RUN=1 READY=2 WAIT=4 SUSPEND=8 DORMANT=0x10 — combinable bits.
        // Missing SUSPEND bit made callers that SuspendThread + poll status spin forever
        // (MK ADX: 123k× Suspend / 150M while status never showed 0x08).
        //
        // Never-started → DORMANT(0x10). Exited-after-run → DORMANT unless SoftSuspended
        // (logical park from SuspendThread on an exited peer — see KernelState.SoftSuspended).
        uint status;
        if (!t.Started)
        {
            status = t.SoftSuspended ? 0x08u : 0x10u;
        }
        else
        {
            status = 0;
            if (id == _kernel.CurrentThreadId) status |= 0x01u;
            else if (!t.Sleeping && t.SuspendCount == 0) status |= 0x02u;
            // WAIT only while actually parked (Sleeping/WaitVblank).
            if (t.Sleeping || t.WaitVblank) status |= 0x04u;
            if (t.SuspendCount > 0 || t.SoftSuspended) status |= 0x08u;
            if (status == 0) status = 0x02u;
        }
        if (statusAddr != 0)
        {
            _system.Memory.Write32(statusAddr + 0, status);
            _system.Memory.Write32(statusAddr + 4, t.Entry);
            _system.Memory.Write32(statusAddr + 8, t.Stack);
            _system.Memory.Write32(statusAddr + 12, t.StackSize);
            _system.Memory.Write32(statusAddr + 16, t.Gp);
            _system.Memory.Write32(statusAddr + 20, (uint)t.InitialPriority);
            _system.Memory.Write32(statusAddr + 24, (uint)t.Priority);
            _system.Memory.Write32(statusAddr + 28, 0); // attr
            _system.Memory.Write32(statusAddr + 32, 0); // option
            // ee_thread_status_t: waitType@0x24, waitId@0x28, wakeupCount@0x2C
            uint waitType = 0, waitId = 0;
            if (t.WaitSemaId != 0) { waitType = 2; waitId = (uint)t.WaitSemaId; }
            else if (t.WaitMbxId != 0) { waitType = 5; waitId = (uint)t.WaitMbxId; }
            else if (t.DelayRemainingUs > 0) { waitType = 2; /* DELAY */ }
            else if (t.Sleeping || t.WaitVblank) { waitType = 1; /* SLEEP */ }
            _system.Memory.Write32(statusAddr + 36, waitType);
            _system.Memory.Write32(statusAddr + 40, waitId);
            _system.Memory.Write32(statusAddr + 44, (uint)t.WakeupCount);
        }
        return 0;
    }

    private int CreateThreadFromStruct(uint addr)
    {
        // ps2sdk ee_thread_t:
        // +0 status, +4 func, +8 stack, +C stack_size, +10 gp, +14 initial_priority
        uint func = 0, stack = 0, stackSize = 0, gp = 0;
        int priority = 64;
        if (addr != 0)
        {
            func = _system.Memory.Read32(addr + 0x04);
            stack = _system.Memory.Read32(addr + 0x08);
            stackSize = _system.Memory.Read32(addr + 0x0C);
            gp = _system.Memory.Read32(addr + 0x10);
            priority = (int)_system.Memory.Read32(addr + 0x14);
            if (priority <= 0 || priority > 127) priority = 64;
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
                    priority = (int)_system.Memory.Read32(addr + 0x1C);
                    if (priority <= 0 || priority > 127) priority = 64;
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
        return _kernel.CreateThread(func, gp, sp, stackSize, priority);
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

    /// <summary>
    /// BIOS THREADMAN ReferSemaStatus (FUN_0000365c / FUN_000036a4). Fills ps2sdk
    /// <c>ee_sema_t</c>: count@+0, max_count@+4, init_count@+8, wait_threads@+C,
    /// attr@+10, option@+14. Decomp copies attr/option/init/max/count/numWaiters from
    /// the live sema object; attr/option are not tracked by HLE and are written 0.
    /// </summary>
    private int ReferSemaStatus(int id, uint statusAddr)
    {
        if (!_kernel.SemaExists(id)) return -1;
        if (statusAddr != 0)
        {
            _system.Memory.Write32(statusAddr + 0, (uint)_kernel.GetSemaCount(id));
            _system.Memory.Write32(statusAddr + 4, (uint)_kernel.GetSemaMaxCount(id));
            _system.Memory.Write32(statusAddr + 8, (uint)_kernel.GetSemaInitCount(id));
            _system.Memory.Write32(statusAddr + 12, (uint)_kernel.CountSemaWaiters(id));
            _system.Memory.Write32(statusAddr + 16, 0); // attr
            _system.Memory.Write32(statusAddr + 20, 0); // option
        }
        return 0;
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
    /// <summary>
    /// Install an EE exception-vector handler (SetVTLBRefill / SetVCommon / SetVInterrupt).
    /// Returns the previous handler, or a non-zero BIOS-default sentinel when none was
    /// registered yet — matching retail BIOS where the default exception vector is always live.
    /// Passing handler=0 clears the registration and still returns the previous value.
    /// </summary>
    private static long SetExceptionVectorHandler(Dictionary<int, uint> table, int cause, uint handler)
    {
        long previous = table.TryGetValue(cause, out uint prev) ? prev : DefaultExceptionHandlerSentinel;
        if (handler == 0)
            table.Remove(cause);
        else
            table[cause] = handler;
        return previous;
    }

}
