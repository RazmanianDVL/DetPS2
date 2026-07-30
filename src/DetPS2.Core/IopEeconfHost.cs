using System;

namespace DetPS2.Core;

/// <summary>
/// C# HLE for BIOS <b>EECONF</b> — optional IOPBTCONF module (after HEAPLIB) that finishes
/// EE/peripheral configuration during IOP bring-up.
///
/// <para>Authority (no in-tree Ghidra dump / EECONF.bin yet):
/// <list type="bullet">
/// <item>ps2homebrew / Woon Yung OSD init notes: EECONF initializes peripherals, MAC address,
/// SPEED (DEV9) capabilities, and manipulates ROM version data (DECKARD collab on late models).</item>
/// <item>Every IOP reboot: EECONF opens the MECHACON EEPROM PS1-config block and zero-fills it
/// (browser PS1 disc-speed/texture options do not survive IOP reboot).</item>
/// <item>IOPBTCONF placement: after HEAPLIB, before THREADMAN; <see cref="BiosBootHost"/> lists
/// it with <c>RequiredForCommercialFastPath = false</c> but still registers the name.</item>
/// </list>
/// </para>
///
/// <para>No EE SIF RPC sid — pure IOP-side init. DetPS2 presents the <b>post-init contracts</b>
/// other modules and EE-side probes expect (configured flag, MAC, SPEED caps, cleared PS1
/// NVRAM block, ROM version string plant) without modeling mechacon serial or DECKARD.</para>
/// </summary>
public sealed class IopEeconfHost
{
    /// <summary>Size of the PS1 configuration EEPROM block EECONF zero-fills on each IOP boot.</summary>
    public const int Ps1ConfigBlockBytes = 64;

    /// <summary>Default synthetic Ethernet MAC (locally administered unicast) when none bound.</summary>
    public static readonly byte[] DefaultMac = { 0x02, 0x00, 0x00, 0x00, 0x00, 0x01 };

    // SPEED / DEV9 capability bits (HLE surface — not a full SMAP driver).
    public const uint SpeedCapPresent = 1u << 0;
    public const uint SpeedCap100M = 1u << 1;
    public const uint SpeedCapFullDuplex = 1u << 2;
    public const uint SpeedCapHdd = 1u << 3;

    /// <summary>Default SPEED caps: adaptor present, 100M, FD (no HDD unless virtual HDD enabled later).</summary>
    public const uint DefaultSpeedCaps = SpeedCapPresent | SpeedCap100M | SpeedCapFullDuplex;

    /// <summary>Default ROM version plant for EE probes (SCPH-class string, not a title string).</summary>
    public const string DefaultRomVersion = "0160EC20040614";

    private readonly byte[] _mac = new byte[6];
    private readonly byte[] _ps1Config = new byte[Ps1ConfigBlockBytes];
    private uint _speedCaps;
    private string _romVersion = DefaultRomVersion;
    private bool _initialized;
    private bool _ps1BlockCleared;
    private bool _peripheralsReady;
    private ulong _initCount;
    private ulong _ps1ClearCount;

    public bool Initialized => _initialized;
    public bool PeripheralsReady => _peripheralsReady;
    public bool Ps1ConfigBlockCleared => _ps1BlockCleared;
    public uint SpeedCaps => _speedCaps;
    public string RomVersion => _romVersion;
    public ulong InitCount => _initCount;
    public ulong Ps1ClearCount => _ps1ClearCount;

    public IopEeconfHost() => Reset();

    public void Reset()
    {
        Array.Clear(_mac);
        Array.Clear(_ps1Config);
        // Leave residual garbage in PS1 block until ApplyBiosInit so smokes can observe zero-fill.
        for (int i = 0; i < _ps1Config.Length; i++)
            _ps1Config[i] = 0xA5;
        _speedCaps = 0;
        _romVersion = DefaultRomVersion;
        _initialized = false;
        _ps1BlockCleared = false;
        _peripheralsReady = false;
        _initCount = 0;
        _ps1ClearCount = 0;
    }

    /// <summary>
    /// Run the EECONF IOPBTCONF entry path: clear PS1 EEPROM config block, plant MAC + SPEED
    /// caps + ROM version, mark peripherals ready. Idempotent for a single boot; each call
    /// re-clears the PS1 block (matches "every IOP reboot" retail behavior).
    /// </summary>
    public void ApplyBiosInit()
    {
        ClearPs1ConfigBlock();
        if (_mac[0] == 0 && _mac[1] == 0 && _mac[2] == 0 &&
            _mac[3] == 0 && _mac[4] == 0 && _mac[5] == 0)
            Array.Copy(DefaultMac, _mac, 6);
        if (_speedCaps == 0)
            _speedCaps = DefaultSpeedCaps;
        if (string.IsNullOrEmpty(_romVersion))
            _romVersion = DefaultRomVersion;
        _peripheralsReady = true;
        _initialized = true;
        _initCount++;
    }

    /// <summary>Zero-fill the MECHACON PS1 configuration block (retail EECONF every IOP boot).</summary>
    public void ClearPs1ConfigBlock()
    {
        Array.Clear(_ps1Config);
        _ps1BlockCleared = true;
        _ps1ClearCount++;
    }

    /// <summary>Bind a 6-byte MAC (null/short → default). Call before or after init.</summary>
    public void SetMac(ReadOnlySpan<byte> mac)
    {
        if (mac.Length < 6)
        {
            Array.Copy(DefaultMac, _mac, 6);
            return;
        }
        mac.Slice(0, 6).CopyTo(_mac);
    }

    public void GetMac(Span<byte> dest)
    {
        if (dest.Length < 6) return;
        _mac.AsSpan().CopyTo(dest);
    }

    public byte[] GetMacCopy()
    {
        var a = new byte[6];
        Array.Copy(_mac, a, 6);
        return a;
    }

    public void SetSpeedCaps(uint caps) => _speedCaps = caps;

    public void SetRomVersion(string? version) =>
        _romVersion = string.IsNullOrEmpty(version) ? DefaultRomVersion : version;

    /// <summary>Read one byte of the PS1 config block (post-clear all zeros).</summary>
    public byte ReadPs1Config(int offset)
    {
        if ((uint)offset >= Ps1ConfigBlockBytes) return 0;
        return _ps1Config[offset];
    }

    /// <summary>
    /// True when EECONF has completed the contracts other modules expect after IOPBTCONF:
    /// initialized, peripherals ready, PS1 block cleared, MAC non-zero, SPEED present bit set.
    /// </summary>
    public bool ContractsReady =>
        _initialized &&
        _peripheralsReady &&
        _ps1BlockCleared &&
        (_speedCaps & SpeedCapPresent) != 0 &&
        (_mac[0] | _mac[1] | _mac[2] | _mac[3] | _mac[4] | _mac[5]) != 0;
}
