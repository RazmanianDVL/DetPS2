using System;

namespace DetPS2.Core;

/// <summary>
/// IOPRP ASCII LOADFILE GetVersion policy (no memory plants for SotC/Ico).
/// Used by Team ICO first-party titles and other retail discs that memcmp GetVersion
/// against the post-UDNL 4-char IOPRP tag (e.g. <c>"3000"</c>, <c>"2500"</c>).
///
/// <para>
/// <b>Shadow of the Colossus (SCUS_974.72)</b> reboots IOP with
/// <c>rom0:UDNL cdrom0:\IOPRP300.IMG;1</c>, then the EE LOADFILE client memcmp's the
/// GetVersion (fno=0xFF) 4-byte reply against rodata <c>"3000"</c> at <c>0x0013227C</c>
/// before any <c>sceSifLoadModule("cdrom0:\MODULES\SIO2MAN.IRX;1")</c>. With the default
/// classic reply <c>0x00020000</c> the gate returns <c>0xFFFEFFFC</c>, the game hangs in
/// an intentional error nop-sled at <c>0x001035B0</c>, and generic nop-sled rescue can
/// re-home mid VBlank busy-poll (<c>0x00111DA0</c>) with a garbage <c>v1</c>.
/// </para>
///
/// <para>
/// <b>Haven: Call of the King (SLUS_205.17)</b> reboots with
/// <c>rom0:UDNL cdrom0:\SYS250\IOPRP250.IMG;1</c> (shared tag <c>"2500"</c>). Without
/// <see cref="RealSifRpc.PreferIopRpGetVersion"/>, GetVersion stays classic
/// <c>0x00020000</c> and the title <c>Exit(0)</c> before any post-reboot MOD_LOAD / FILEIO.
/// PreferIopRp lands live SYS250 IRX + pad/MC/SD (px=3, cdvd=77 @100M, binds=12/calls=16).
/// </para>
///
/// <para>
/// <b>Haven boot geometry:</b> retail ELF is a single high-VA PT_LOAD at <c>0x01000000</c>
/// (entry <c>0x01000008</c>, ~2.5 MiB packed). Diagnose @20M still sits in the CRT0 bit-stream
/// decompress loop at <c>0x010003F0</c> (syscalls=0) — that is cycle budget, not a TLB/map miss:
/// RDRAM is 32 MiB and <c>TranslateAddress</c> identity-maps kuseg. Decompress finishes ~80–85M;
/// @100M: PC soft-float band, px=3, gifP3=2, full SYS250 (binds=12, cdvd=77).
/// </para>
///
/// <para>
/// <b>Haven residual (#21 — title surface / FILEIO·DLL.DAT):</b> after the IRX stack the EE
/// enters a sin/cos LUT fill at <c>0x0010CCD8</c> —
/// <c>for (i=0..N) table[i] = (float)sin((double)(i * k))</c> with <c>k≈π/16384</c>
/// (<c>0x39490FDB</c>). Each iteration calls soft-double f32→f64 (<c>0x00353A28</c>),
/// sin (<c>0x003432F0</c>), f64→f32 (<c>0x00352E30</c>); the sin poly lives at
/// <c>0x00345C30</c> / mul body <c>0x00352660</c> (band <c>0x00351xxx–0x00352xxx</c>).
/// Interpreter soft-float costs 10k–100k cycles/sin → 100–250M cycles with no
/// <c>DLL.DAT</c>/<c>FILEIO</c> string. Wave-2: register those entries on
/// <see cref="SoftFloatBridge"/> (shared host IEEE). Wave-3: clear software VIF1 busy
/// (<c>*(0x39C0C4)</c>) when the wait at <c>0x188AE0</c> spins while CHCR.STR is clear /
/// channel idle, and credit VIF1 DMA IRQ so the real handler can advance; NUSOUND2
/// (sid <c>0x00012345</c>, not Midway MSL.IRX) bulk fno=0 is handled in
/// <see cref="RealSifRpc"/>. Wave-4: NUSOUND bulk partial recv echo + <b>real-bind</b>
/// root <c>DLL.DAT</c> (~1.1 MiB SN module image) into RDRAM at <c>0x00800000</c>
/// (live residual <c>$ra=0x8925CC</c> / PC high band matched file+base); Soft-GS already
/// paints logo clear (px≈286720 gifP3=68) — next is chrome beyond clear / title surface.
/// Haven-only still: VBlankStart sticky + poll-base repair. Disc: <c>DLL.DAT</c>, <c>DATA/</c>.
/// </para>
///
/// <para>
/// Enabling <see cref="RealSifRpc.PreferIopRpGetVersion"/> reuses the shared
/// OnIopReboot ASCII tag path — no title-local memory plant. Same class as
/// <see cref="GodOfWarAssist"/> / <see cref="VexxAssist"/> version policy; no Midway plants.
/// </para>
/// </summary>
public sealed class TeamIcoAssist : IGameQuirkModule
{
    private readonly string _serial;
    private readonly string _displayName;
    private readonly bool _isHaven;

    // Haven INTC_STAT VBlankStart busy-poll (disasm residual top PC 0x331650).
    private const uint HavenVbPollA = 0x00331650;
    private const uint HavenVbPollAEnd = 0x00331668;
    private const uint HavenVbPollB = 0x003316F0;
    private const uint HavenVbPollBEnd = 0x0033170C;

    // Haven VIF1 software-busy wait (disasm 0x188AD8: jal 0x1883C8; 0x188AE0: bne v0,0).
    // Callee returns *(0x39C0C4); set when a VIF1 chain is kicked (CHCR=0x1C5), cleared by
    // the DMA completion path. When STR is already clear / channel idle, clear busy and
    // credit VIF1 IRQ so the real handler can run (same class as B3/DA owed-handler assist).
    private const uint HavenVifWaitSpin = 0x00188AE0;
    private const uint HavenVifWaitJal = 0x00188AD8;
    private const uint HavenVifBusyFlag = 0x0039C0C4;
    private const uint HavenVifPendingFlag = 0x0039C0DC;

    private int _vbPulses;
    private int _vbBaseRepairs;
    private int _vifBusyClears;
    private int _lateLogPulses;
    private ulong _lastLogCyc;
    private ulong _lastVbPulseCyc;
    private ulong _lastVifBusyCyc;

    public TeamIcoAssist(string serial, string displayName)
    {
        _serial = serial ?? throw new ArgumentNullException(nameof(serial));
        _displayName = displayName ?? serial;
        _isHaven = string.Equals(_serial, "SLUS_205.17", StringComparison.OrdinalIgnoreCase);
    }

    public string Serial => _serial;
    public string DisplayName => _displayName;

    public void Reset()
    {
        _vbPulses = 0;
        _vbBaseRepairs = 0;
        _vifBusyClears = 0;
        _lateLogPulses = 0;
        _lastLogCyc = 0;
        _lastVbPulseCyc = 0;
        _lastVifBusyCyc = 0;
        if (_isHaven)
            SoftFloatBridge.Reset();
    }

    public void OnDiscMounted(Ps2System sys)
    {
        Reset();
        if (sys.Hle?.Sony?.RealRpc != null)
            sys.Hle.Sony.RealRpc.PreferIopRpGetVersion = true;
        if (_isHaven)
            RegisterHavenSoftFloat();
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            || Environment.GetEnvironmentVariable("DETPS2_TRACE_TEAMICO") == "1")
            Console.Error.WriteLine(
                $"[TEAMICO] OnDiscMounted: PreferIopRpGetVersion serial={_serial}"
                + (_isHaven ? $" havenVbAssist=on softFloatEntries={SoftFloatBridge.EntryCount}" : ""));
    }

    /// <summary>
    /// Haven post-decompress soft-double library (live @90M). Shared
    /// <see cref="SoftFloatBridge"/> evaluates IEEE on host so the sin LUT fill at
    /// <c>0x0010CCD8</c> can finish and reach first game-data FILEIO.
    /// </summary>
    private static void RegisterHavenSoftFloat()
    {
        SoftFloatBridge.RegisterMany(new (uint, SoftFloatBridge.Op)[]
        {
            // Core multi-precision arithmetic (sin/cos poly body)
            (0x00352660u, SoftFloatBridge.Op.DMul),
            (0x003525A0u, SoftFloatBridge.Op.DAdd),
            (0x003525F8u, SoftFloatBridge.Op.DSub),
            // libm
            (0x003432F0u, SoftFloatBridge.Op.DSin),
            (0x00342EB0u, SoftFloatBridge.Op.DCos),
            // float↔double bridges used by the 0x10CCD8 LUT fill
            (0x00353A28u, SoftFloatBridge.Op.F32ToF64),
            (0x00352E30u, SoftFloatBridge.Op.F64ToF32),
        });
    }

    public void OnHostPresent(Ps2System sys)
    {
        if (_isHaven && sys.Hle?.Sony?.RealRpc is { Binds: >= 10 })
            PulseHavenVblank(sys, force: false);
    }

    public void Step(Ps2System sys)
    {
        if (!_isHaven) return;

        var rpc = sys.Hle?.Sony?.RealRpc;
        if (rpc == null || rpc.Binds < 10) return;

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFu);
        ulong cyc = sys.Scheduler.MasterCycles;

        bool inPollA = pc is >= HavenVbPollA and < HavenVbPollAEnd;
        bool inPollB = pc is >= HavenVbPollB and < HavenVbPollBEnd;
        if (inPollA || inPollB)
        {
            // Mid-function re-home can leave v1 != INTC_STAT (same class as
            // Ps2System.TryRepairIntcStatPollBase).
            uint v1 = (uint)(sys.EE.GetGpr(3).Lo & 0xFFFFFFFFu);
            if (v1 != Intc.AddrStat)
            {
                sys.EE.SetGpr(3, new EmotionEngine.Gpr128 { Lo = Intc.AddrStat });
                _vbBaseRepairs++;
            }
            PulseHavenVblank(sys, force: true);
        }
        else if (rpc.FileIoOps == 0 && (cyc - _lastVbPulseCyc) > 500_000UL)
        {
            PulseHavenVblank(sys, force: false);
        }

        // VIF1 software-busy spin @0x188AE0 (post soft-float residual).
        bool inVifWait = pc is >= HavenVifWaitJal and <= HavenVifWaitSpin + 4
            || (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFu) is >= HavenVifWaitJal and <= HavenVifWaitSpin + 4;
        if (inVifWait)
            MaybeClearHavenVifBusy(sys, cyc);

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_TEAMICO") == "1"
            && (cyc - _lastLogCyc) > 5_000_000UL
            && _lateLogPulses < 8)
        {
            _lastLogCyc = cyc;
            _lateLogPulses++;
            uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFu);
            Console.Error.WriteLine(
                $"[TEAMICO-HAVEN] residual #{_lateLogPulses} cyc={cyc} pc=0x{pc:X8} ra=0x{ra:X8} "
                + $"binds={rpc.Binds} calls={rpc.Calls} fioOps={rpc.FileIoOps} "
                + $"intcStat=0x{sys.Intc.Stat:X} vbPulse={_vbPulses} vbFix={_vbBaseRepairs} "
                + $"vifBusyClr={_vifBusyClears}");
        }
    }

    /// <summary>
    /// When the wait-for-VIF1-idle loop at <c>0x188AE0</c> is live and the software busy
    /// flag is stuck while VIF1 CHCR.STR is clear / DMAC channel idle, clear the flag and
    /// credit the VIF1 AddDmacHandler path so completion side-effects can run.
    /// </summary>
    private void MaybeClearHavenVifBusy(Ps2System sys, ulong cyc)
    {
        if (_vifBusyClears >= 64) return;
        if ((cyc - _lastVifBusyCyc) < 50_000UL) return;

        uint busy = sys.Memory.Read32(HavenVifBusyFlag);
        uint pending = sys.Memory.Read32(HavenVifPendingFlag);
        if (busy == 0 && pending == 0) return;

        bool vifActive = sys.Dmac.IsActive(Dmac.Channel.VIF1);
        uint chcr = sys.Dmac.ReadRegister(0x10009000);
        bool str = (chcr & 0x100) != 0;
        if (vifActive || str) return;

        if (busy != 0)
            sys.Memory.Write32(HavenVifBusyFlag, 0);
        if (pending != 0)
            sys.Memory.Write32(HavenVifPendingFlag, 0);

        try
        {
            sys.Dmac.EnableChannelIrq((int)Dmac.Channel.VIF1);
            sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.VIF1, 1);
        }
        catch { /* ignore */ }

        _vifBusyClears++;
        _lastVifBusyCyc = cyc;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_TEAMICO") == "1"
            && _vifBusyClears <= 16)
            Console.Error.WriteLine(
                $"[TEAMICO-HAVEN] VIF1 busy clear n={_vifBusyClears} chcr=0x{chcr:X} "
                + $"busyWas=0x{busy:X} pendWas=0x{pending:X} cyc={cyc}");
    }

    private void PulseHavenVblank(Ps2System sys, bool force)
    {
        ulong cyc = sys.Scheduler.MasterCycles;
        if (!force && (cyc - _lastVbPulseCyc) < 200_000UL) return;
        Intc.CurrentCycleForTrace = cyc;
        if (sys.Intc.IsRaised(Intc.InterruptSource.VBlankStart))
            sys.Intc.RearmCpuLatch(Intc.InterruptSource.VBlankStart);
        else
            sys.Intc.Raise(Intc.InterruptSource.VBlankStart);
        _vbPulses++;
        _lastVbPulseCyc = cyc;
    }
}
