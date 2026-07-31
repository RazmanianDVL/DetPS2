using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Subsystem Interface (Phase 8 + 13 RPC) — <b>SIF bridge</b> for IRX-first (WP-19/T4).
/// DMA SIF0/SIF1 + SBUS mailbox + RPC queues. See <c>docs/irx/SIF_BRIDGE.md</c>.
/// <para>
/// SIFMAN/SIFCMD should run as real IRX on the IOP; this class is the shared DMA/mailbox
/// engine those modules (or temporary HLE stand-ins) drive. Paths that pure-HLE without IOP
/// exec are debt — demote via WP-20/WP-49. IRX is the product, not an optional mode.
/// </para>
/// </summary>
public sealed class Sif : ISchedulable
{
    public enum DmaDirection
    {
        IopToEe = 0,
        EeToIop = 1
    }

    /// <summary>
    /// True unless emergency HLE bisect (<c>DETPS2_FORCE_HLE_IOP=1</c> / <c>DETPS2_LITERAL_IRX=0</c>).
    /// </summary>
    public static bool LiteralIrxMode => IopModuleHost.IsLiteralIrxEnabled;

    /// <summary>Optional log of pure-HLE SIF paths when <c>DETPS2_TRACE_SIF_HLE=1</c>.</summary>
    public static bool TraceSifHleBypass =>
        Environment.GetEnvironmentVariable("DETPS2_TRACE_SIF_HLE") == "1";

    private static void NoteHleBypass(string site)
    {
        if (!TraceSifHleBypass) return;
        Console.Error.WriteLine($"[SIF-HLE] bypass site={site} literalIrx={LiteralIrxMode}");
    }

    private readonly SystemMemory _memory;
    private readonly Intc? _intc;
    private readonly Queue<uint> _cmdQueue = new();
    private readonly Queue<uint> _rpcPacketAddrs = new();

    /// <summary>
    /// Real, retail-compiled sifrpc.c bind/call packets (cid 0x80000009/0x8000000A), queued
    /// for the IOP's own scheduler tick to drain instead of being answered synchronously
    /// inside the EE's own SifSetDma syscall handler. On real hardware the EE and IOP are
    /// separate chips joined only by a narrow 32-bit SIF bus (confirmed against the real
    /// SCPH-30000-series service manual block diagram, 2026-07-28) — CDVD/SPU2/pad/memcard
    /// are wired to the IOP's own sub-bus, physically unreachable from the EE except by
    /// relay through the IOP. Answering synchronously within the same EE instruction that
    /// issued the request collapses that relay to a single function call, so anything
    /// depending on genuine cross-chip latency (a real async completion, not just "the
    /// current value") could never be modeled correctly — the root cause behind a whole
    /// class of bugs this project kept re-finding under different names (see
    /// DEVELOPER_GUIDE.md's Shaolin Monks / Burnout 3 write-ups). Drained by
    /// SonyKernelHle.DrainRealRpcQueue, called once per ambient scheduler tick (Ps2System's
    /// ISchedulable.Step) — never from inside PerformSifSetDma itself — so a response is
    /// never visible until at least the next scheduler slice after the request was issued.
    ///
    /// Each entry is tagged with the scheduler "generation" (Ps2System's own tick counter,
    /// NOT MasterCycles — MasterCycles only advances once per whole RunFor slice, so it can't
    /// distinguish "this tick" from "an earlier tick" the way a per-Step()-call counter can)
    /// it was submitted in, so TryDequeueRealRpc can refuse to hand back an entry from the
    /// *current* generation — preserving "never answered within the same instruction that
    /// issued it" — while still draining anything older whenever it's called, not just once
    /// per generation. That distinction matters: a title whose own retry loop can issue many
    /// bind attempts within a single scheduler slice (confirmed live, 2026-07-28 — Shaolin
    /// Monks' sceSifBindRpc retrying its CDVD bind, sid=0x80000592, millions of times) will
    /// exhaust the real, small, fixed-size EE-side RPC packet pool if packets from *earlier*
    /// generations aren't freed before the retry loop gets another turn — draining strictly
    /// once per generation isn't enough on its own if PerformSifSetDma is also given a chance
    /// to opportunistically drain older entries mid-slice (see its own call site).
    /// </summary>
    private readonly Queue<(uint addr, ulong generation)> _realRpcQueue = new();

    private IopModuleHost? _modules;
    private PadInput? _pad;
    private Cdvd? _cdvd;

    public bool DmaBusy { get; private set; }
    public uint LastCommand { get; private set; }
    public uint Status { get; private set; }
    public ulong CommandsProcessed { get; private set; }
    public ulong BytesTransferred { get; private set; }
    public ulong RpcProcessed { get; private set; }

    public uint MsCom { get; private set; }
    public uint SmCom { get; private set; }
    public uint MsFlag { get; private set; }
    public uint SmFlag { get; private set; }

    /// <summary>
    /// Real ps2sdk (ee/kernel/include/sifdma.h) bit values for SIF_REG_SMFLAG, the register
    /// real EE library code polls to learn the IOP's boot progress: sceSifInitCmd's own real
    /// source (ee/kernel/src/sifcmd.c) literally reads
    /// "while (!(sceSifGetReg(SIF_REG_SMFLAG) & SIF_STAT_CMDINIT))" before doing anything else.
    /// This emulator's IOP core (Iop.cs) doesn't model real IOP-side hardware registers or
    /// firmware execution faithfully enough for a real IOP kernel to ever set these for real
    /// (see MaybeUnblockStarvedSema's doc comment and DEVELOPER_GUIDE.md 2026-07-26 for how that
    /// was confirmed) — so, like a real BIOS handing off to a game only once its own boot is
    /// actually complete, present these as already set from boot: SIFINIT (0x10000, basic SIF
    /// up), CMDINIT (0x20000, cmd/rpc layer up), BOOTEND (0x40000, full IOP boot done).
    /// </summary>
    public const uint SifStatSifInit = 0x10000;
    public const uint SifStatCmdInit = 0x20000;
    public const uint SifStatBootEnd = 0x40000;

    /// <summary>All three SMFLAG boot-progress bits (SIFMAN+SIFCMD+EESYNC handoff complete).</summary>
    public const uint SifStatIopBootReady = SifStatSifInit | SifStatCmdInit | SifStatBootEnd;

    // ps2sdk sifdma.h SIF_REG_* indices (physical SBUS mailbox regs via SifGetReg/SifSetReg).
    public const uint SifRegMainAddr = 1;
    public const uint SifRegSubAddr = 2;
    public const uint SifRegMsFlag = 3;
    public const uint SifRegSmFlag = 4;

    // ps2sdk sifdma.h software sysregs (SIF_REG_ID_SYSTEM | n).
    public const uint SifSysregSubAddr = 0x80000000;
    public const uint SifSysregMainAddr = 0x80000001;
    public const uint SifSysregRpcInit = 0x80000002;

    /// <summary>
    /// IOP-side SIFCMD receive buffer (SIF_REG_SUBADDR). Real SIFCMD.IRX publishes this after
    /// init; EE <c>sceSifInitCmd</c> reads it as the DMA destination for EE→IOP command packets.
    /// HLE places it in high IOP RAM (physical), away from early module load ranges.
    /// </summary>
    public const uint DefaultIopSifCmdBufAddr = 0x0001F000;

    /// <summary>
    /// Common EE BSS base used by several retail/sceSifInitRpc builds as the RPC "queue ready"
    /// / cmd-handler-slot table (polled as non-zero words). Confirmed live against Midway titles
    /// at 0x00778800; planted generically so pure-BIOS HLE matches the post-handshake state
    /// without game PCs. Not a title assist — the same table shape appears across SDK variants.
    /// </summary>
    public const uint EeSifReadySlotBase = 0x00778800;
    public const int EeSifReadySlotCount = 8;

    /// <summary>Last RPC result written (for tests).</summary>
    public uint LastRpcResult { get; private set; }

    /// <summary>
    /// IOP reboot in flight: EE <c>SifIopReset</c> clears SMFLAG bits + SYSREG after sending
    /// RESET_CMD; real IOP reloads SIFMAN→SIFCMD→…→SIFINIT and EESYNC re-posts BOOTEND.
    /// HLE defers re-post until the next SMFLAG GetReg (after EE clears) — see
    /// <see cref="MarkIopRebootPending"/> / <see cref="TryCompletePendingIopReboot"/>.
    /// </summary>
    public bool IopRebootPending { get; private set; }

    /// <summary>Monotonic IOP reboot generation (RESET_CMD completions). For diagnostics/smokes.</summary>
    public ulong IopRebootGeneration { get; private set; }

    /// <summary>
    /// Last RESET_CMD arg string captured from the EE reset packet (ps2sdk
    /// <c>SifCmdResetData_t.arg</c>). Empty string = default IOPBTCONF reload
    /// (real <c>SifIopReset("", 0)</c>). Non-empty often starts with
    /// <c>rom0:UDNL …</c> (SifIopReboot path).
    /// </summary>
    public string LastIopRebootArg { get; private set; } = "";

    /// <summary>Last RESET_CMD mode field (<c>SifCmdResetData_t.mode</c>).</summary>
    public int LastIopRebootMode { get; private set; }

    /// <summary>Last RESET_CMD arglen field (bytes of arg copied by EE).</summary>
    public int LastIopRebootArgLen { get; private set; }

    /// <summary>ps2sdk RESET_ARG_MAX — max arg chars in SifCmdResetData_t.</summary>
    public const int IopRebootArgMax = 80;

    /// <summary>SIFINIT applied (SMFLAG bit SIFINIT). Idempotent — decomp "Skip SIF init".</summary>
    public bool SifInitApplied => (SmFlag & SifStatSifInit) != 0;

    /// <summary>SIFCMD layer up (SMFLAG CMDINIT).</summary>
    public bool CmdInitApplied => (SmFlag & SifStatCmdInit) != 0;

    /// <summary>EESYNC SyncEE posted BOOTEND.</summary>
    public bool BootEndPosted => (SmFlag & SifStatBootEnd) != 0;

    public Sif(SystemMemory memory, Intc? intc = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _intc = intc;
    }

    public void BindServices(IopModuleHost modules, PadInput pad, Cdvd cdvd)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _pad = pad ?? throw new ArgumentNullException(nameof(pad));
        _cdvd = cdvd ?? throw new ArgumentNullException(nameof(cdvd));
    }

    public void Reset()
    {
        DmaBusy = false;
        LastCommand = 0;
        Status = 0;
        CommandsProcessed = 0;
        BytesTransferred = 0;
        RpcProcessed = 0;
        LastRpcResult = 0;
        MsCom = SmCom = MsFlag = 0;
        IopRebootPending = false;
        IopRebootGeneration = 0;
        LastIopRebootArg = "";
        LastIopRebootMode = 0;
        LastIopRebootArgLen = 0;
        // BIOS handoff: SIFMAN SIFINIT + SIFCMD CMDINIT + EESYNC BOOTEND already complete.
        SmFlag = SifStatIopBootReady;
        _cmdQueue.Clear();
        _rpcPacketAddrs.Clear();
        _realRpcQueue.Clear();
    }

    // ---- SIFINIT / EESYNC / SIFCMD boot contracts (decomp + ps2sdk) ----

    /// <summary>
    /// SIFINIT.IRX / SIFMAN init effect: set SIF_STAT_SIFINIT. Decomp string "Skip SIF init" —
    /// if already set, no-op (returns false).
    /// </summary>
    public bool ApplySifInit()
    {
        if ((SmFlag & SifStatSifInit) != 0)
            return false;
        SmFlag |= SifStatSifInit;
        // EE side of the same handshake: SIFMAN sceSifInit polls MSFLAG bit 0x10000
        // (SIF_STAT_SIFINIT on the EE→IOP mailbox). Without this, sequential
        // StartLoadedModule parks forever in GetMsFlag (WP-10 residual).
        PresentEeSifHandshake();
        return true;
    }

    /// <summary>
    /// Plant EE→IOP MSFLAG SIFINIT so retail SIFMAN <c>sceSifInit</c> can leave its
    /// <c>while (!(GetMsFlag() &amp; 0x10000))</c> poll during cold IRX start (no live EE
    /// SifSetReg yet). Safe/idempotent.
    /// </summary>
    public void PresentEeSifHandshake()
    {
        MsFlag |= SifStatSifInit;
    }

    /// <summary>
    /// SIFCMD.IRX init effect (IOP side FUN_0000006c opt==0 / FUN_000016d0): set CMDINIT.
    /// </summary>
    public bool ApplyCmdInit()
    {
        if ((SmFlag & SifStatCmdInit) != 0)
            return false;
        SmFlag |= SifStatCmdInit;
        return true;
    }

    /// <summary>
    /// EESYNC.IRX export SyncEE (decomp FUN_0000007c): posts SIF_STAT_BOOTEND (0x40000) via
    /// sifman so EE <c>SifIopSync</c> / boot waiters observe full IOP bring-up.
    /// </summary>
    public void PostBootEnd()
    {
        SmFlag |= SifStatBootEnd;
    }

    /// <summary>
    /// Full IOPBTCONF SIF stack handoff after SIFMAN→SIFCMD→…→SIFINIT (+ EESYNC): all three
    /// SMFLAG bits set. Idempotent.
    /// <para>
    /// <b>LITERAL_IRX HLE bypass</b> when called without executing those IRX modules — presents
    /// the EE-visible *effect* of a completed IOP boot. Under literal mode, prefer
    /// <see cref="ApplySifInit"/> / <see cref="ApplyCmdInit"/> / <see cref="PostBootEnd"/>
    /// driven by real module start (or keep this only as bisect fallback).
    /// </para>
    /// </summary>
    public void PresentIopBootReady()
    {
        if (LiteralIrxMode)
            NoteHleBypass("PresentIopBootReady");
        SmFlag |= SifStatIopBootReady;
    }

    /// <summary>
    /// SMFLAG write-1-to-clear (ps2sdk <c>SifIopReset</c> / real SBUS): each 1-bit in
    /// <paramref name="bits"/> clears the corresponding SMFLAG bit.
    /// </summary>
    public void ClearSmFlagBits(uint bits)
    {
        SmFlag &= ~bits;
    }

    /// <summary>
    /// EE sent SIF_CMD_RESET_CMD. Defer re-post until after EE clears SIFINIT/CMDINIT/BOOTEND
    /// and SYSREG (real SifIopReset order: SetDma then SetReg clears). Next SMFLAG poll
    /// completes the sequence (SIFINIT + CMDINIT + EESYNC BOOTEND).
    /// </summary>
    public void MarkIopRebootPending() => MarkIopRebootPending(null, 0, 0);

    /// <summary>
    /// RESET_CMD with full <c>SifCmdResetData_t</c> payload (REBOOT.IRX / IOP-side helper
    /// contract). Captures arg/mode for diagnostics and post-reboot path selection.
    /// Empty/null <paramref name="arg"/> = default IOPBTCONF (commercial cold-boot equivalent).
    /// </summary>
    public void MarkIopRebootPending(string? arg, int mode, int argLen = -1)
    {
        IopRebootPending = true;
        // Mirror pre-reboot clear of BOOTEND if EE hadn't already (SifIopReset clears it first).
        SmFlag &= ~SifStatBootEnd;
        LastIopRebootArg = arg ?? "";
        if (LastIopRebootArg.Length > IopRebootArgMax)
            LastIopRebootArg = LastIopRebootArg[..IopRebootArgMax];
        LastIopRebootMode = mode;
        LastIopRebootArgLen = argLen >= 0 ? argLen : LastIopRebootArg.Length;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_REBOOT") == "1")
            Console.Error.WriteLine(
                $"[REBOOT] pending arglen={LastIopRebootArgLen} mode={LastIopRebootMode} " +
                $"arg=\"{LastIopRebootArg}\"");
    }

    /// <summary>
    /// If a RESET_CMD reboot is pending, re-apply SIFINIT→CMDINIT→EESYNC BOOTEND (as real
    /// IOPBTCONF reload + REBOOT.IRX + EESYNC SyncEE would). Returns true when a pending
    /// reboot completed.
    /// </summary>
    public bool TryCompletePendingIopReboot()
    {
        if (!IopRebootPending)
            return false;
        IopRebootPending = false;
        IopRebootGeneration++;
        // SIFMAN re-init + SIFCMD re-init + EESYNC SyncEE post (REBOOT completes bring-up).
        SmFlag |= SifStatIopBootReady;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_REBOOT") == "1")
            Console.Error.WriteLine(
                $"[REBOOT] complete gen={IopRebootGeneration} smflag=0x{SmFlag:X} " +
                $"arg=\"{LastIopRebootArg}\"");
        return true;
    }

    /// <summary>
    /// Plant EE-side SIF "queue ready" slots so <c>sceSifInitRpc</c>-style polls succeed under
    /// pure BIOS HLE (no IOP R3000 to run the real SIF0→_SifCmdIntHandler path).
    /// <para>
    /// <b>LITERAL_IRX HLE bypass:</b> under <see cref="LiteralIrxMode"/> this is debt until
    /// executing SIFCMD + SIF0 DMA fills the same table. Do not add title-specific bases here;
    /// WP-20 should shrink call sites. Trace: <c>DETPS2_TRACE_SIF_HLE=1</c>.
    /// </para>
    /// </summary>
    public static void PlantEeSifReadySlots(SystemMemory mem, uint baseAddr = EeSifReadySlotBase, int count = EeSifReadySlotCount)
    {
        // LITERAL_IRX HLE bypass — optional branch stub (always plants today; log when tracing).
        if (LiteralIrxMode)
            NoteHleBypass("PlantEeSifReadySlots");
        if (mem == null) return;
        if (count <= 0) count = EeSifReadySlotCount;
        for (uint i = 0; i < (uint)count; i++)
            mem.Write32(baseAddr + i * 4, 1);
    }

    /// <summary>True when SMFLAG has the full SIFINIT|CMDINIT|BOOTEND handoff.</summary>
    public bool IsIopBootReady => (SmFlag & SifStatIopBootReady) == SifStatIopBootReady;

    public void SendCommand(uint command)
    {
        LastCommand = command;
        Status |= 0x2;
        _cmdQueue.Enqueue(command);
        MsCom = command;
        MsFlag |= 1;
        _intc?.Raise(Intc.InterruptSource.Sif);
    }

    /// <summary>Queue a real (retail sifrpc.c) bind/call packet for later IOP-side
    /// processing, tagged with the scheduler generation it was submitted in — see the
    /// field's own doc comment for why this must not be drained synchronously from within
    /// the EE's own SifSetDma syscall handler, and why "later" means "any generation after
    /// this one", not strictly "only the very next one".</summary>
    public void SubmitRealRpc(uint eePacketAddr, ulong generation)
    {
        _realRpcQueue.Enqueue((eePacketAddr, generation));
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPCQUEUE") == "1")
            Console.Error.WriteLine($"[RPCQUEUE] submit addr=0x{eePacketAddr:X8} gen={generation} depth={_realRpcQueue.Count}");
    }

    /// <summary>Dequeues the oldest real RPC packet, but only if it's from a strictly
    /// earlier generation than <paramref name="currentGeneration"/> — refuses to hand back
    /// something submitted in the same generation as the caller's own current one, so a
    /// request is never answered within the same instruction (or even the same scheduler
    /// tick) that issued it.</summary>
    public bool TryDequeueRealRpc(ulong currentGeneration, out uint eePacketAddr)
    {
        if (_realRpcQueue.Count == 0 || _realRpcQueue.Peek().generation >= currentGeneration)
        {
            eePacketAddr = 0;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPCQUEUE") == "1" && _realRpcQueue.Count > 0)
                Console.Error.WriteLine($"[RPCQUEUE] refused: peekGen={_realRpcQueue.Peek().generation} currentGen={currentGeneration} depth={_realRpcQueue.Count}");
            return false;
        }
        eePacketAddr = _realRpcQueue.Dequeue().addr;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPCQUEUE") == "1")
            Console.Error.WriteLine($"[RPCQUEUE] drained addr=0x{eePacketAddr:X8} currentGen={currentGeneration} depthAfter={_realRpcQueue.Count}");
        return true;
    }

    public int RealRpcQueueCount => _realRpcQueue.Count;

    /// <summary>
    /// True when some queued real BIND/CALL/RDATA packet's client
    /// <c>SifRpcClientData_t.hdr.sema_id</c> matches <paramref name="semaId"/>.
    /// <para>
    /// Used by WaitSema to decide whether to <c>RequestSemaStall</c> for real
    /// <c>CompleteRpcEnd</c>/<c>iSignalSema</c> completion vs yield/fabricate.
    /// Without this check, a WaitSema on the SIF-cmd poll mutex (e.g. GoW/B3
    /// WaitSema(3) at the sifrpc trampoline) freezes the whole EE whenever any
    /// unrelated BIND/CALL is in the queue — the stall only clears when *our*
    /// sema is signaled, which those packets never do (wrong-sema deadlock,
    /// 2026-07-30). SEMA_STALL_YIELD is not required when we only stall for a
    /// matching client.
    /// </para>
    /// </summary>
    public bool QueueMaySignalSema(SystemMemory mem, int semaId)
    {
        if (semaId < 0 || _realRpcQueue.Count == 0) return false;
        foreach (var (addr, _) in _realRpcQueue)
        {
            // EE sifrpc often posts packets via the uncached phys window (0x20000000|pa).
            // Bounds must use physical — raw 0x20xxxxxx fails RDRAM_SIZE and skips every
            // entry (BO2 WAVE 4: 0 STALL / all FABRICATE → half-updated CallRpc thrash).
            uint phys = addr & 0x1FFFFFFFu;
            if (phys == 0 || phys >= (uint)SystemMemory.RDRAM_SIZE - 0x30u) continue;
            uint cid = mem.Read32(phys + 8);
            // BIND/CALL: client at +28; RDATA: client at +0x1c (see RealSifRpc).
            uint cdPtr = cid == RealSifRpc.CidRpcRdata
                ? mem.Read32(phys + 0x1c)
                : mem.Read32(phys + 28);
            cdPtr &= 0x1FFFFFFFu;
            if (cdPtr == 0 || cdPtr >= (uint)SystemMemory.RDRAM_SIZE - 0x10u) continue;
            int pktSema = unchecked((int)mem.Read32(cdPtr + 8)); // hdr.sema_id
            if (pktSema == semaId)
                return true;
        }
        return false;
    }

    /// <summary>Queue an EE RPC packet address for IOP-side processing.</summary>
    public void SubmitRpc(uint packetEeAddr)
    {
        _rpcPacketAddrs.Enqueue(packetEeAddr);
        Status |= 0x8; // RPC pending
        MsFlag |= 2;
        _intc?.Raise(Intc.InterruptSource.Sif);
    }

    public bool TryDequeueCommand(out uint command)
    {
        if (_cmdQueue.Count == 0)
        {
            command = 0;
            return false;
        }
        command = _cmdQueue.Dequeue();
        CommandsProcessed++;
        return true;
    }

    public int CommandQueueCount => _cmdQueue.Count;
    public int RpcQueueCount => _rpcPacketAddrs.Count;

    public void DoDmaTransfer(uint eeAddr, uint iopAddr, uint size, DmaDirection direction = DmaDirection.EeToIop)
    {
        if (size == 0) return;

        DmaBusy = true;
        Status |= 0x1;

        uint iopPhys = NormalizeIopAddr(iopAddr);
        if (TransferLog.Enabled)
        {
            bool eeToIop = direction == DmaDirection.EeToIop;
            TransferLog.Log(eeToIop ? "SIF:EE->IOP" : "SIF:IOP->EE",
                eeToIop ? eeAddr : iopPhys, eeToIop ? iopPhys : eeAddr, size);
        }

        for (uint i = 0; i < size; i++)
        {
            if (direction == DmaDirection.EeToIop)
            {
                byte b = _memory.Read8(eeAddr + i);
                _memory.Write8(iopPhys + i, b);
            }
            else
            {
                byte b = _memory.Read8(iopPhys + i);
                _memory.Write8(eeAddr + i, b);
            }
        }

        BytesTransferred += size;
        DmaBusy = false;
        Status &= ~0x1u;
        Status |= 0x4;
        _intc?.Raise(Intc.InterruptSource.Sif);
    }

    public void DoDmaTransfer(uint eeAddr, uint iopAddr, uint size) =>
        DoDmaTransfer(eeAddr, iopAddr, size, DmaDirection.EeToIop);

    public void Sif0IopToEe(uint iopAddr, uint eeAddr, uint size) =>
        DoDmaTransfer(eeAddr, iopAddr, size, DmaDirection.IopToEe);

    public void Sif1EeToIop(uint eeAddr, uint iopAddr, uint size) =>
        DoDmaTransfer(eeAddr, iopAddr, size, DmaDirection.EeToIop);

    private static uint NormalizeIopAddr(uint iopAddr)
    {
        if (iopAddr < SystemMemory.IOP_RAM_SIZE)
            return SystemMemory.IOP_RAM_BASE + iopAddr;
        return iopAddr;
    }

    public void WriteSmCom(uint value)
    {
        SmCom = value;
        SmFlag |= 1;
        _intc?.Raise(Intc.InterruptSource.Sif);
    }

    /// <summary>
    /// EE posts MSFLAG bits visible to the IOP via the shared mailbox (EE
    /// <c>0x1000F220</c> / IOP <c>0x1D000020</c>). Used by smokes and by kernel
    /// <c>SifSetReg(SIF_REG_MSFLAG)</c> mirroring.
    /// </summary>
    public void EePostMsFlag(uint value)
    {
        MsFlag = value;
    }

    /// <summary>
    /// IOP → EE mailbox reply path for future executing SIFMAN (and WP-19 smokes).
    /// Sets SMCOM, optionally ORs SMFLAG status bits, raises SIF INTC so the EE can observe
    /// the reverse mailbox without requiring a full SIF0 DMA.
    /// <para>
    /// Bulk reply data still uses <see cref="Sif0IopToEe"/>. Command-packet replies use
    /// <c>SonyKernelHle.DeliverIopSifCmdToEe</c> (HLE) until SIFCMD IRX owns that path.
    /// </para>
    /// </summary>
    public void IopPostMailboxReply(uint smCom, uint smFlagOrBits = 0)
    {
        SmCom = smCom;
        if (smFlagOrBits != 0)
            SmFlag |= smFlagOrBits;
        SmFlag |= 1; // "message pending" style bit (matches WriteSmCom)
        Status |= 0x4; // transfer/reply visible
        _intc?.Raise(Intc.InterruptSource.Sif);
    }

    public uint GetStatus() => Status;

    public uint ReadRegister(uint address)
    {
        return (address & 0xFF) switch
        {
            0x00 => MsCom,
            0x10 => SmCom,
            0x20 => MsFlag,
            0x30 => SmFlag,
            0x40 => Status,
            0x50 => LastRpcResult,
            _ => 0
        };
    }

    public void WriteRegister(uint address, uint value)
    {
        switch (address & 0xFF)
        {
            case 0x00:
                // EE→IOP MSCOM (also used if EE MMIO posts a command word).
                SendCommand(value);
                break;
            case 0x10:
                // IOP→EE SMCOM reply (IOP window WriteRegister / WriteSmCom).
                WriteSmCom(value);
                break;
            case 0x20:
                // EE→IOP MSFLAG — must be readable by IOP IopRead32(0x1D000020).
                MsFlag = value;
                break;
            case 0x30:
                // IOP→EE SMFLAG raw write (SIFMAN posts SIFINIT etc.). EE path uses W1C
                // via SifSetReg, not this full assign.
                SmFlag = value;
                break;
            case 0x40:
                Status = value;
                break;
            case 0x60:
                // Write packet address to submit simplified RPC via MMIO
                SubmitRpc(value);
                break;
        }
    }

    /// <summary>
    /// Process pending <b>simplified</b> Phase-13 RPC packets (deterministic, no host I/O).
    /// <para>
    /// <b>LITERAL_IRX HLE bypass:</b> dispatches <see cref="SifRpcPacket"/> through
    /// <c>IopModuleHost.Dispatch</c> — does <b>not</b> execute SIFMAN/SIFCMD/service IRX.
    /// Retail BIND/CALL use <see cref="SubmitRealRpc"/> + <c>RealSifRpc</c> (also HLE services).
    /// Under <see cref="LiteralIrxMode"/> keep this for homebrew/bisect only; WP-20 routes
    /// sifcmd through live IOP. Optional stub: log via <c>DETPS2_TRACE_SIF_HLE=1</c>.
    /// </para>
    /// </summary>
    public int Step(ulong maxCycles)
    {
        if (_modules == null || _pad == null || _cdvd == null)
            return 0;

        if (_rpcPacketAddrs.Count > 0 && LiteralIrxMode)
            NoteHleBypass("Sif.Step pure-HLE SifRpcPacket→IopModuleHost");

        int n = 0;
        while (_rpcPacketAddrs.Count > 0 && n < 16)
        {
            uint addr = _rpcPacketAddrs.Dequeue();
            var pkt = SifRpcPacket.Read(_memory, addr);
            var done = _modules.Dispatch(pkt, _memory, _pad, _cdvd);
            done.Write(_memory, addr);
            LastRpcResult = done.Result;
            // Reply path: result visible on SMCOM + SMFLAG for EE pollers.
            IopPostMailboxReply(done.Result, 4);
            Status |= 0x10; // RPC complete
            Status &= ~0x8u;
            RpcProcessed++;
            n++;
        }

        return n > 0 ? n : 0;
    }
}
