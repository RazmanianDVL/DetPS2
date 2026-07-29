using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// C# HLE of BIOS <c>VBLANK.IRX</c> (<c>Vblank_service</c>) — IOP callback lists and
/// event-flag wakes, separate from EE <see cref="Intc"/> cause 2 / PCRTC.
///
/// Source: Ghidra decomp of SCPH70008 VBLANK (tools/bios-decomp/VBLANK_ALL.txt,
/// docs/BIOS_DISSECTION.md §5):
/// <list type="bullet">
/// <item><c>FUN_00000164</c> Register(which, priority, callback, arg) — which 0/1 = start/end list</item>
/// <item><c>FUN_000002ac</c> Unregister by callback pointer</item>
/// <item><c>FUN_00000374</c> / <c>FUN_0000042c</c> Dispatch start/end lists</item>
/// <item><c>FUN_00000544</c> Signal event flag for waiters</item>
/// </list>
/// CDVDFSV / FILEIO / retail drivers <c>WaitEventFlag</c> on this service's flag bits.
/// </summary>
public sealed class IopVblankHost
{
    public const int WhichStart = 0;
    public const int WhichEnd = 1;

    /// <summary>Event-flag bit set on every IOP vblank start (thevent-style waiter wake).</summary>
    public const uint EvfBitStart = 0x0001;
    /// <summary>Event-flag bit set on every IOP vblank end.</summary>
    public const uint EvfBitEnd = 0x0002;

    private sealed class Node
    {
        public int Priority;
        public uint Callback; // IOP pointer (opaque to HLE — not executed as R3000)
        public uint Arg;
    }

    private readonly List<Node> _start = new();
    private readonly List<Node> _end = new();
    private int _efId;
    private bool _efCreated;
    private ulong _startDispatches;
    private ulong _endDispatches;

    public ulong StartDispatches => _startDispatches;
    public ulong EndDispatches => _endDispatches;
    public int HandlerCount => _start.Count + _end.Count;
    /// <summary>THREADMAN event-flag id owned by this service (0 until EnsureEventFlag).</summary>
    public int EventFlagId => _efId;

    public void Reset()
    {
        _start.Clear();
        _end.Clear();
        _efId = 0;
        _efCreated = false;
        _startDispatches = _endDispatches = 0;
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
    /// Register a vblank handler. Matches BIOS: reject duplicate callback, insert by priority
    /// (lower value = earlier). Callback is stored but not executed as R3000 code — HLE
    /// signals the service event flag so WaitEventFlag paths complete.
    /// </summary>
    public int Register(int which, int priority, uint callback, uint arg, KernelState? kernel = null)
    {
        if (callback == 0) return unchecked((int)0xFFFFFF9C); // KE_ILLEGAL_ATTR-ish
        var list = which == WhichEnd ? _end : _start;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Callback == callback)
                return unchecked((int)0xFFFFFF98); // already registered
        }
        int insert = list.Count;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Priority > priority)
            {
                insert = i;
                break;
            }
        }
        list.Insert(insert, new Node { Priority = priority, Callback = callback, Arg = arg });
        if (kernel != null) EnsureEventFlag(kernel);
        return 0;
    }

    public int Unregister(int which, uint callback)
    {
        var list = which == WhichEnd ? _end : _start;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Callback == callback)
            {
                list.RemoveAt(i);
                return 0;
            }
        }
        return unchecked((int)0xFFFFFF97); // not found
    }

    /// <summary>
    /// IOP vblank start-list dispatch + event flag. Called from EE PCRTC VBlank edge so
    /// IOP-side waiters advance in lockstep with display timing (BIOS ties IOP IRQ 1/2 to
    /// the same beam).
    /// </summary>
    public void DispatchStart(KernelState kernel)
    {
        _startDispatches++;
        EnsureEventFlag(kernel);
        kernel.SetEventFlag(_efId, EvfBitStart);
        // HLE: callbacks are not R3000-executed; registered count is observable for diagnostics.
        _ = _start.Count;
    }

    public void DispatchEnd(KernelState kernel)
    {
        _endDispatches++;
        EnsureEventFlag(kernel);
        kernel.SetEventFlag(_efId, EvfBitEnd);
        _ = _end.Count;
    }

    /// <summary>Full start+end pulse (one EE VBlank).</summary>
    public void OnEeVblank(KernelState kernel)
    {
        DispatchStart(kernel);
        DispatchEnd(kernel);
    }
}
