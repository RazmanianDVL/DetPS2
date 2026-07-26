using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Diagnostic-only: a chronological log of every bulk data transfer in the system — DMAC channel
/// starts, SIF0/SIF1 EE&lt;-&gt;IOP transfers, and GIF Path3 (EE-&gt;GS) receives. Complements
/// SystemMemory.LastWriterLog, which covers individual CPU store instructions but not the DMA
/// engine's own bulk moves (a DMA transfer's payload bytes are copied by the channel's own
/// implementation, not by repeated Write32/Write8 calls from EE code, so they were previously
/// invisible to any "who wrote this" query even with --track-writers on).
///
/// Built 2026-07-26 in response to a direct request for maximally comprehensive logging — "every
/// single byte transmitted gives us a log of where it was trying to go." DMA/SIF/GIF-Path3 are the
/// actual transmission mechanisms on real PS2 hardware (EE RAM &lt;-&gt; IOP RAM, EE RAM -&gt; GS,
/// etc.), so this is the literal answer to that: every channel start, every cross-domain transfer,
/// and every GS-bound Path3 receive, each stamped with cycle/pc/source/dest/size.
///
/// Off by default (a list append per transfer is cheap, but still not free); opt-in via
/// blocker-trace --track-transfers.
/// </summary>
public static class TransferLog
{
    public static bool Enabled;
    public static ulong CurrentCycle;
    public static ulong CurrentPc;

    public readonly record struct Event(ulong Cycle, ulong Pc, string Kind, uint Source, uint Dest, uint Size, string Detail);
    public static readonly List<Event> Events = new();

    public static void Log(string kind, uint source, uint dest, uint size, string detail = "")
    {
        if (!Enabled) return;
        Events.Add(new Event(CurrentCycle, CurrentPc, kind, source, dest, size, detail));
    }

    public static void Reset() => Events.Clear();
}
