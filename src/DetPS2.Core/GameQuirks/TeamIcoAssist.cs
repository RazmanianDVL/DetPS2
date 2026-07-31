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
/// <b>Haven residual (#21 — first FILEIO / game-data open):</b> after the IRX stack the EE
/// burns ≥150M cycles in multi-precision soft-float at <c>0x00351xxx–0x00352xxx</c>
/// (multi-prec body <c>0x00352660</c>, callers <c>0x00345C30</c> / <c>0x00343328</c>,
/// outerRa live <c>0x00345Dxx</c>; libm-class poly / range-reduce on doubles — EE has no
/// hardware double FPU). Rodata doubles at <c>0x00614640+</c> (π etc.) are live post-decompress.
/// INTC_STAT already sticky bit2|3 (<c>0xC</c>); Vb poll at <c>0x00331650</c> is secondary.
/// No <c>sceSifBindRpc(FILEIO 0x80000001)</c>, no <c>DLL.DAT</c>/<c>DATA/</c> path string in
/// RDRAM. Title-local epi/double-pop thrash escapes re-enter more soft-float and do not unlock
/// FILEIO — next work is shared EE soft-float fidelity or a proven outer-game resume (PINE),
/// not more stack invent. Haven-only: keep VBlankStart sticky + repair poll base if mid-function
/// re-home. Disc first assets: root <c>DLL.DAT</c> (~1.1 MiB), <c>DATA/</c> (NuFile / TT).
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

    private int _vbPulses;
    private int _vbBaseRepairs;
    private int _lateLogPulses;
    private ulong _lastLogCyc;
    private ulong _lastVbPulseCyc;

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
        _lateLogPulses = 0;
        _lastLogCyc = 0;
        _lastVbPulseCyc = 0;
    }

    public void OnDiscMounted(Ps2System sys)
    {
        Reset();
        if (sys.Hle?.Sony?.RealRpc != null)
            sys.Hle.Sony.RealRpc.PreferIopRpGetVersion = true;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            || Environment.GetEnvironmentVariable("DETPS2_TRACE_TEAMICO") == "1")
            Console.Error.WriteLine(
                $"[TEAMICO] OnDiscMounted: PreferIopRpGetVersion serial={_serial}"
                + (_isHaven ? " havenVbAssist=on" : ""));
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
                + $"intcStat=0x{sys.Intc.Stat:X} vbPulse={_vbPulses} vbFix={_vbBaseRepairs}");
        }
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
