using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// C# HLE of BIOS EXCEPMAN.IRX ("Exception Manager") — real per-exception-code, priority-ordered
/// handler registration. Ghidra-decompiled in full (tools/bios-decomp/EXCEPMAN_ALL.txt, 14 real
/// functions, 2026-07-29): a real R3000A CPU exception (syscall, address error, TLB miss, etc. —
/// 16 real exception codes) dispatches through a registered handler *chain*, not a single fixed
/// vector — modules install their own handler via RegisterExceptionHandler/
/// RegisterPriorityExceptionHandler, and EXCEPMAN rebuilds the real dispatch chain
/// (FUN_0000038c) every time the registry changes.
///
/// Distinct from <see cref="IopSystemHost"/>'s INTRMAN registry: INTRMAN is for asynchronous
/// hardware interrupts, EXCEPMAN is for synchronous CPU exceptions (traps) — a real, meaningful
/// architectural distinction confirmed by these being two genuinely separate real BIOS modules.
///
/// Bookkeeping only, matching this project's existing IOP HLE modules — DetPS2 does not execute
/// real R3000A BIOS module code, so there is nothing yet that would actually *dispatch* through
/// this chain; this exists so that if/when real IOP execution lands, a module's real
/// RegisterExceptionHandler call succeeds/fails/orders exactly as it would on real hardware,
/// rather than needing this discovered and ground-truthed again from scratch at that point.
/// </summary>
public sealed class IopExcepManHost
{
    public const int ExceptionCodeCount = 16; // real: 0..0xf, confirmed via FUN_00000134's `param_1 < 0x10` bound

    // Real result codes read directly off the decompile (RegisterExceptionHandler/
    // RegisterPriorityExceptionHandler/ReleaseExceptionHandler's own return values):
    public const int ResultOk = 0;
    public const int ResultAlreadyRegistered = unchecked((int)0xFFFFFFCC); // -52, FUN_00000134/FUN_00000210: *param_3 != 0 on entry
    public const int ResultNotFound = unchecked((int)0xFFFFFFCD);         // -51, FUN_00000264/FUN_0000030c: handler not in chain
    public const int ResultInvalidExCode = unchecked((int)0xFFFFFFCE);    // -50, FUN_00000134/FUN_00000264: excCode >= 0x10

    private sealed class Handler
    {
        public int Priority;
        public uint Callback;
    }

    private readonly List<Handler>[] _chains = new List<Handler>[ExceptionCodeCount];
    private ulong _rebuilds;

    public ulong RebuildCount => _rebuilds;
    public int HandlerCount(int excCode) => IsValidExCode(excCode) ? _chains[excCode].Count : 0;

    public IopExcepManHost() => Reset();

    public void Reset()
    {
        for (int i = 0; i < ExceptionCodeCount; i++)
            _chains[i] = new List<Handler>();
        _rebuilds = 0;
    }

    private static bool IsValidExCode(int excCode) => (uint)excCode < ExceptionCodeCount;

    /// <summary>RegisterExceptionHandler(excCode, handler) — real BIOS fixes priority=2 for this
    /// simple form (FUN_00000110 always calls FUN_00000134 with a hardcoded priority of 2).</summary>
    public int RegisterExceptionHandler(int excCode, uint handler) =>
        RegisterPriorityExceptionHandler(excCode, 2, handler);

    /// <summary>RegisterPriorityExceptionHandler(excCode, priority, handler) — real FUN_00000134,
    /// transliterated: reject out-of-range excCode, reject a handler already carrying non-zero
    /// state (this project doesn't model the real caller-owned control-block struct, so this
    /// specific rejection path is unreachable here — every call is treated as a fresh
    /// registration), then insert into the excCode's chain ordered by priority (lower value =
    /// dispatched first — real chain-walk breaks on the first existing entry whose own priority
    /// is >= the new one, inserting before it) and rebuild the dispatch chain.</summary>
    public int RegisterPriorityExceptionHandler(int excCode, int priority, uint handler)
    {
        if (!IsValidExCode(excCode)) return ResultInvalidExCode;
        var chain = _chains[excCode];
        int insertAt = chain.Count;
        for (int i = 0; i < chain.Count; i++)
        {
            if (priority <= chain[i].Priority) { insertAt = i; break; }
        }
        chain.Insert(insertAt, new Handler { Priority = priority, Callback = handler });
        Rebuild();
        return ResultOk;
    }

    /// <summary>ReleaseExceptionHandler(excCode, handler) — real FUN_00000264.</summary>
    public int ReleaseExceptionHandler(int excCode, uint handler)
    {
        if (!IsValidExCode(excCode)) return ResultInvalidExCode;
        var chain = _chains[excCode];
        int idx = chain.FindIndex(h => h.Callback == handler);
        if (idx < 0) return ResultNotFound;
        chain.RemoveAt(idx);
        Rebuild();
        return ResultOk;
    }

    /// <summary>Real FUN_0000038c: rebuilds the compiled dispatch table from every exception
    /// code's current handler chain. Nothing in this project executes real R3000A code that
    /// would read this table yet, so this only advances a counter for observability/testing.</summary>
    private void Rebuild() => _rebuilds++;
}
