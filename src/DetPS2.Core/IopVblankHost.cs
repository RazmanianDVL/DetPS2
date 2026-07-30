using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// C# HLE of BIOS <c>VBLANK.IRX</c> (<c>Vblank_service</c> v1.1) — IOP callback lists and
/// event-flag pulses, separate from EE <see cref="Intc"/> cause 2 / PCRTC.
///
/// Authority: Ghidra decomp of SCPH70008 VBLANK (<c>tools/bios-decomp/VBLANK_ALL.txt</c>),
/// <c>docs/BIOS_DISSECTION.md</c> §5, and the open-source recreation in ps2sdk
/// <c>iop/system/vblank/src/vblank.c</c> (SCE SDK 1.3.4-based, matches decomp exports).
///
/// Export table <c>vblank</c> 1.1:
/// <list type="bullet">
/// <item>[4] WaitVblankStart — WaitEventFlag(ef, bit 1, OR)</item>
/// <item>[5] WaitVblankEnd — WaitEventFlag(ef, bit 4, OR)</item>
/// <item>[6] WaitVblank — WaitEventFlag(ef, bit 2, OR)</item>
/// <item>[7] WaitNonVblank — WaitEventFlag(ef, bit 8, OR)</item>
/// <item>[8] RegisterVblankHandler(which, priority, cb, arg) — <c>FUN_00000164</c></item>
/// <item>[9] ReleaseVblankHandler(which, cb) — <c>FUN_000002ac</c></item>
/// </list>
///
/// IRQ path (real module registers INTRMAN handlers for IOP IRQ 0 / 11):
/// start-list dispatch → base handler sets START|VBLANK then clears residual to START;
/// end-list dispatch → base handler sets END|NON then residual END.
/// Callback returning 0 auto-releases the node (decomp <c>FUN_00000374</c>/<c>0042c</c>).
/// HLE does not execute R3000 callbacks; permanent handlers stay until Unregister.
/// CDVDFSV / FILEIO / retail drivers WaitEventFlag on this service's flag bits.
/// </summary>
public sealed class IopVblankHost
{
    public const int WhichStart = 0;
    public const int WhichEnd = 1;

    /// <summary>Max nodes in the shared free pool (real <c>list_items[16]</c>).</summary>
    public const int MaxHandlers = 16;

    // Real EF bits (ps2sdk vblank.c + decomp FUN_000004b4 / FUN_000004fc / Wait exports):
    /// <summary>EF_VBLANK_START — pulsed on IOP vblank start (IRQ 0).</summary>
    public const uint EvfBitStart = 0x0001;
    /// <summary>EF_VBLANK — "in vblank" combined bit, pulsed with start.</summary>
    public const uint EvfBitVblank = 0x0002;
    /// <summary>EF_VBLANK_END — pulsed on IOP vblank end (IRQ 11 EVBLANK).</summary>
    public const uint EvfBitEnd = 0x0004;
    /// <summary>EF_NON_VBLANK — pulsed with end.</summary>
    public const uint EvfBitNonVblank = 0x0008;

    // KE_* from common/include/kerr.h (same constants appear in VBLANK decomp returns):
    public const int ResultOk = 0;
    public const int ResultIllegalContext = unchecked((int)0xFFFFFF9C); // KE_ILLEGAL_CONTEXT -100
    public const int ResultFoundHandler = unchecked((int)0xFFFFFF98);   // KE_FOUND_HANDLER -104
    public const int ResultNotFoundHandler = unchecked((int)0xFFFFFF97); // KE_NOTFOUND_HANDLER -105
    public const int ResultNoMemory = unchecked((int)0xFFFFFE70);       // KE_NO_MEMORY -400

    private sealed class Node
    {
        public int Priority;
        public uint Callback; // IOP pointer (opaque to HLE — not executed as R3000)
        public uint Arg;
        /// <summary>How many times this node was visited by DispatchStart/End.</summary>
        public ulong Invocations;
    }

    private readonly List<Node> _start = new();
    private readonly List<Node> _end = new();
    private int _efId;
    private bool _efCreated;
    private ulong _startDispatches;
    private ulong _endDispatches;
    private ulong _frameCount;
    private ulong _callbackInvocations;
    private bool _intrContext;

    public ulong StartDispatches => _startDispatches;
    public ulong EndDispatches => _endDispatches;
    public ulong FrameCount => _frameCount;
    public ulong CallbackInvocations => _callbackInvocations;
    public int HandlerCount => _start.Count + _end.Count;
    public int StartHandlerCount => _start.Count;
    public int EndHandlerCount => _end.Count;
    /// <summary>THREADMAN event-flag id owned by this service (0 until EnsureEventFlag).</summary>
    public int EventFlagId => _efId;
    /// <summary>Free-pool remaining capacity (shared across start/end lists).</summary>
    public int FreeSlots => MaxHandlers - HandlerCount;

    /// <summary>
    /// Simulated QueryIntrContext: when true, Register/Release return KE_ILLEGAL_CONTEXT
    /// (real VBLANK rejects non-thread context). Tests / future IOP IRQ entry can toggle.
    /// </summary>
    public bool InterruptContext
    {
        get => _intrContext;
        set => _intrContext = value;
    }

    public void Reset()
    {
        _start.Clear();
        _end.Clear();
        _efId = 0;
        _efCreated = false;
        _startDispatches = _endDispatches = 0;
        _frameCount = 0;
        _callbackInvocations = 0;
        _intrContext = false;
    }

    /// <summary>Ensure the IOP vblank event flag exists (lazy create on first register/dispatch).</summary>
    public int EnsureEventFlag(KernelState kernel)
    {
        if (_efCreated && _efId != 0) return _efId;
        _efId = kernel.CreateEventFlag(0);
        _efCreated = true;
        return _efId;
    }

    /// <summary>
    /// Register a vblank handler. Matches BIOS <c>FUN_00000164</c> / ps2sdk
    /// <c>RegisterVblankHandler</c>: reject interrupt context, reject duplicate callback on
    /// the selected list, reject when the 16-slot free pool is empty, insert by priority
    /// (lower value = earlier; equal priorities keep registration order).
    /// </summary>
    public int Register(int which, int priority, uint callback, uint arg, KernelState? kernel = null)
    {
        if (_intrContext) return ResultIllegalContext;
        if (callback == 0) return ResultIllegalContext; // no valid cb (attr-ish)
        if (HandlerCount >= MaxHandlers) return ResultNoMemory;

        var list = which == WhichEnd ? _end : _start;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Callback == callback)
                return ResultFoundHandler;
        }

        // Walk until first node with priority > new (ps2sdk: priority < item->priority → break).
        int insert = list.Count;
        for (int i = 0; i < list.Count; i++)
        {
            if (priority < list[i].Priority)
            {
                insert = i;
                break;
            }
        }
        list.Insert(insert, new Node { Priority = priority, Callback = callback, Arg = arg });
        if (kernel != null) EnsureEventFlag(kernel);
        return ResultOk;
    }

    /// <summary>ReleaseVblankHandler — <c>FUN_000002ac</c>.</summary>
    public int Unregister(int which, uint callback)
    {
        if (_intrContext) return ResultIllegalContext;
        var list = which == WhichEnd ? _end : _start;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Callback == callback)
            {
                list.RemoveAt(i);
                return ResultOk;
            }
        }
        return ResultNotFoundHandler;
    }

    /// <summary>
    /// IOP vblank start-list dispatch + base-handler event-flag pulse.
    /// Real: <c>irq_vblank_interrupt_handler</c> (<c>FUN_00000374</c>) then
    /// <c>vblank_handler_base_beginning</c> (<c>FUN_000004b4</c>).
    /// </summary>
    public void DispatchStart(KernelState kernel)
    {
        _startDispatches++;
        _frameCount++;
        EnsureEventFlag(kernel);

        // Walk start list (HLE: count invocations; permanent handlers stay).
        InvokeList(_start);

        // Base beginning: iSetEventFlag(START); iSetEventFlag(VBLANK); iClearEventFlag(~(START|NON)).
        // ClearEventFlag(ef, bits) does bits &= ~bits — so Clear(~9) keeps only START|NON residual.
        // After sets of 1|2, residual is START (1) for pollers / next Wait.
        kernel.SetEventFlag(_efId, EvfBitStart);
        kernel.SetEventFlag(_efId, EvfBitVblank);
        kernel.ClearEventFlag(_efId, unchecked((uint)~(EvfBitStart | EvfBitNonVblank)));
    }

    /// <summary>
    /// IOP vblank end-list dispatch + base-handler event-flag pulse.
    /// Real: <c>irq_evblank_interrupt_handler</c> (<c>FUN_0000042c</c>) then
    /// <c>vblank_handler_base_end</c> (<c>FUN_000004fc</c>).
    /// </summary>
    public void DispatchEnd(KernelState kernel)
    {
        _endDispatches++;
        EnsureEventFlag(kernel);

        InvokeList(_end);

        // Base end: iSetEventFlag(END); iSetEventFlag(NON); iClearEventFlag(~(VBLANK|END)).
        // Residual after clear is END (4).
        kernel.SetEventFlag(_efId, EvfBitEnd);
        kernel.SetEventFlag(_efId, EvfBitNonVblank);
        kernel.ClearEventFlag(_efId, unchecked((uint)~(EvfBitVblank | EvfBitEnd)));
    }

    /// <summary>Full start+end pulse (one EE VBlank / PCRTC edge). Real boot ties both IRQs to the beam.</summary>
    public void OnEeVblank(KernelState kernel)
    {
        DispatchStart(kernel);
        DispatchEnd(kernel);
    }

    /// <summary>True if WaitVblankStart would return immediately (EF bit 1 set, OR mode).</summary>
    public bool WouldWaitStartSucceed(KernelState kernel) =>
        _efCreated && kernel.EventFlagSatisfied(_efId, EvfBitStart, mode: 1);

    /// <summary>True if WaitVblankEnd would return immediately (EF bit 4 set, OR mode).</summary>
    public bool WouldWaitEndSucceed(KernelState kernel) =>
        _efCreated && kernel.EventFlagSatisfied(_efId, EvfBitEnd, mode: 1);

    /// <summary>Priority of registered callback on the given list, or -1 if absent (diagnostics).</summary>
    public int GetPriority(int which, uint callback)
    {
        var list = which == WhichEnd ? _end : _start;
        for (int i = 0; i < list.Count; i++)
            if (list[i].Callback == callback) return list[i].Priority;
        return -1;
    }

    /// <summary>Callback pointer at list index (start list if which==0), or 0 if OOB.</summary>
    public uint GetCallbackAt(int which, int index)
    {
        var list = which == WhichEnd ? _end : _start;
        if ((uint)index >= (uint)list.Count) return 0;
        return list[index].Callback;
    }

    private void InvokeList(List<Node> list)
    {
        // Snapshot next pointers by index — real code unlinks return-0 nodes mid-walk.
        // HLE never auto-unlinks (callback return value unknown without R3000 exec).
        for (int i = 0; i < list.Count; i++)
        {
            list[i].Invocations++;
            _callbackInvocations++;
        }
    }
}
