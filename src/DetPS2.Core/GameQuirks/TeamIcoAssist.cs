using System;

namespace DetPS2.Core;

/// <summary>
/// IOPRP ASCII LOADFILE GetVersion policy (no memory plants).
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

    public TeamIcoAssist(string serial, string displayName)
    {
        _serial = serial ?? throw new ArgumentNullException(nameof(serial));
        _displayName = displayName ?? serial;
    }

    public string Serial => _serial;
    public string DisplayName => _displayName;

    public void Reset() { }

    public void OnDiscMounted(Ps2System sys)
    {
        if (sys.Hle?.Sony?.RealRpc != null)
            sys.Hle.Sony.RealRpc.PreferIopRpGetVersion = true;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine($"[TEAMICO] OnDiscMounted: PreferIopRpGetVersion serial={_serial}");
    }

    public void Step(Ps2System sys) => _ = sys;

    public void OnHostPresent(Ps2System sys) => _ = sys;
}
