using System;

namespace DetPS2.Core;

/// <summary>
/// C# HLE for BIOS <b>SSBUSC</b> (SSBUS controller) — IOPBTCONF early module that programs
/// Shared Subsystem Bus chip-select windows (base + delay/config) for BOOTROM, DVDROM, SPU,
/// CD/DVD, Exp1/Exp2, SPU2, and DEV9.
///
/// <para>Authority: ps2sdk <c>iop/system/ssbusc</c> (<c>ssbusc.h</c> / <c>ssbusc.c</c>) recreation
/// of the retail export library <c>ssbusc</c> v1.1 — <c>SetDelay</c>/<c>GetDelay</c>/
/// <c>SetBaseAddress</c>/<c>GetBaseAddress</c> + common-delay field helpers. Register map and
/// delay bit-fields from Wisi's SSBUSC research (cited in ps2sdk header). No
/// <c>SSBUSC_ALL.txt</c> Ghidra dump in-tree yet.</para>
///
/// <para>Contract other modules expect after init: device windows are configured (non-zero
/// delay for every wired device; known base addresses for BOOTROM-class windows). DetPS2 does
/// not model cycle-accurate PIO/DMA wait-states — this host is the service surface IRX would
/// call via <c>ssbusc</c> imports, plus the post-IOPBTCONF defaults commercial bring-up leaves.</para>
///
/// Distinct from EE <c>0x1000F100</c> SBUS mailbox window (SIF) and from SIFMAN's SBUS DMA
/// programming — SSBUSC is the IOP peripheral chip-select controller at <c>0xBF8010xx</c>/
/// <c>0xBF8014xx</c>.
/// </summary>
public sealed class IopSsbuscHost
{
    /// <summary>Number of SSBUSC device slots (ps2sdk enum SSBUSC_DEV 0..12).</summary>
    public const int DeviceCount = 13;

    // Device IDs (ps2sdk SSBUSC_DEV)
    public const int Dev0 = 0;       // Exp1 / full map influence
    public const int DevDvdRom = 1;  // DVD ROM (rom1/erom)
    public const int DevBootRom = 2; // BOOT ROM (rom0)
    public const int Dev3 = 3;
    public const int DevSpu = 4;
    public const int DevCdvd = 5;
    public const int Dev6 = 6;
    public const int Dev7 = 7;
    public const int DevExp2 = 8;
    public const int DevSpu2 = 9;
    public const int DevDev9I = 10;  // DEV9 I/O window
    public const int DevDev9M = 11;  // DEV9 memory window
    public const int DevDev9C = 12;  // DEV9 controller

    public const int ResultOk = 0;
    /// <summary>Invalid device id or no register for that slot (ps2sdk returns -1).</summary>
    public const int ResultInvalid = -1;

    // Physical register addresses (documentation / telemetry; HLE stores values in arrays).
    public static readonly uint[] DelayRegAddr =
    {
        0xBF801008, // 0 Exp1
        0xBF80100C, // 1 DVDROM
        0xBF801010, // 2 BOOTROM
        0,          // 3 —
        0xBF801014, // 4 SPU
        0xBF801018, // 5 CDVD
        0,          // 6 —
        0,          // 7 —
        0xBF80101C, // 8 Exp2
        0xBF801414, // 9 SPU2
        0xBF801418, // 10 DEV9I
        0xBF80141C, // 11 DEV9M
        0xBF801420, // 12 DEV9C
    };

    public static readonly uint[] BaseRegAddr =
    {
        0xBF801000, // 0 Exp1
        0xBF801400, // 1 DVDROM
        0,          // 2 BOOTROM (fixed decode)
        0,          // 3 —
        0xBF801404, // 4 SPU
        0xBF801408, // 5 CDVD
        0,          // 6 —
        0,          // 7 —
        0xBF801004, // 8 Exp2
        0xBF80140C, // 9 SPU2
        0,          // 10 DEV9I
        0xBF801410, // 11 DEV9M
        0,          // 12 DEV9C
    };

    public const uint CommonDelayRegAddr = 0xBF801020;

    // Retail-ish post-BIOS defaults (timing not cycle-accurate; windows must be non-zero/valid).
    // Values match common PS2 boot / ps2tek-era tables used by CDVDMAN/SPU/DEV9 bring-up.
    private static readonly uint[] DefaultDelay =
    {
        0x0013243F, // 0 Exp1
        0x0013243F, // 1 DVDROM
        0x0013243F, // 2 BOOTROM
        0,          // 3
        0x200931E1, // 4 SPU
        0x00020843, // 5 CDVD
        0,          // 6
        0,          // 7
        0x00070777, // 8 Exp2
        0x200931E1, // 9 SPU2
        0x00001685, // 10 DEV9I
        0x00101485, // 11 DEV9M
        0x00001685, // 12 DEV9C
    };

    private static readonly uint[] DefaultBase =
    {
        0x1F000000, // 0 Exp1
        0x1E000000, // 1 DVDROM (rom1 window class)
        0,          // 2 BOOTROM fixed
        0,          // 3
        0x1F801C00, // 4 SPU
        0x1F402000, // 5 CDVD
        0,          // 6
        0,          // 7
        0x1F802000, // 8 Exp2
        0x1F900000, // 9 SPU2
        0,          // 10 DEV9I
        0x10000000, // 11 DEV9M (expansion memory class)
        0,          // 12 DEV9C
    };

    /// <summary>Common delay default: recovery/hold/float/strobe mid-range (Wisi field layout).</summary>
    public const uint DefaultCommonDelay = 0x00003331;

    private readonly uint[] _delay = new uint[DeviceCount];
    private readonly uint[] _base = new uint[DeviceCount];
    private uint _commonDelay;
    private bool _configured;
    private ulong _setDelayCount;
    private ulong _setBaseCount;
    private ulong _setCommonCount;
    private ulong _applyDefaultsCount;

    public bool Configured => _configured;
    public uint CommonDelay => _commonDelay;
    public ulong SetDelayCount => _setDelayCount;
    public ulong SetBaseCount => _setBaseCount;
    public ulong SetCommonCount => _setCommonCount;
    public ulong ApplyDefaultsCount => _applyDefaultsCount;

    /// <summary>How many devices have a wired delay register (non-null in ps2sdk table).</summary>
    public int WiredDelayDevices
    {
        get
        {
            int n = 0;
            for (int i = 0; i < DeviceCount; i++)
                if (DelayRegAddr[i] != 0) n++;
            return n;
        }
    }

    public IopSsbuscHost() => Reset();

    public void Reset()
    {
        Array.Clear(_delay);
        Array.Clear(_base);
        _commonDelay = 0;
        _configured = false;
        _setDelayCount = _setBaseCount = _setCommonCount = 0;
        _applyDefaultsCount = 0;
    }

    /// <summary>
    /// Present post-IOPBTCONF SSBUSC state: plant retail-class base/delay for every wired
    /// device. Idempotent; safe to call again after IOP reboot (same as re-loading SSBUSC.IRX).
    /// </summary>
    public void ApplyBiosDefaults()
    {
        for (int i = 0; i < DeviceCount; i++)
        {
            if (DelayRegAddr[i] != 0)
                _delay[i] = DefaultDelay[i];
            if (BaseRegAddr[i] != 0)
                _base[i] = DefaultBase[i];
        }
        _commonDelay = DefaultCommonDelay;
        _configured = true;
        _applyDefaultsCount++;
    }

    private static bool ValidDevice(int device) => (uint)device < DeviceCount;

    /// <summary>ps2sdk SetDelay — write delay/config; returns value or -1.</summary>
    public int SetDelay(int device, uint value)
    {
        if (!ValidDevice(device) || DelayRegAddr[device] == 0)
            return ResultInvalid;
        _delay[device] = value;
        _setDelayCount++;
        _configured = true;
        return unchecked((int)value);
    }

    /// <summary>ps2sdk GetDelay — read delay/config; returns value or -1.</summary>
    public int GetDelay(int device)
    {
        if (!ValidDevice(device) || DelayRegAddr[device] == 0)
            return ResultInvalid;
        return unchecked((int)_delay[device]);
    }

    /// <summary>ps2sdk SetBaseAddress — returns value or -1.</summary>
    public int SetBaseAddress(int device, uint value)
    {
        if (!ValidDevice(device) || BaseRegAddr[device] == 0)
            return ResultInvalid;
        _base[device] = value;
        _setBaseCount++;
        _configured = true;
        return unchecked((int)value);
    }

    /// <summary>ps2sdk GetBaseAddress — returns value or -1.</summary>
    public int GetBaseAddress(int device)
    {
        if (!ValidDevice(device) || BaseRegAddr[device] == 0)
            return ResultInvalid;
        return unchecked((int)_base[device]);
    }

    public int SetCommonDelay(uint value)
    {
        _commonDelay = value;
        _setCommonCount++;
        _configured = true;
        return unchecked((int)value);
    }

    public int GetCommonDelay() => unchecked((int)_commonDelay);

    public int SetRecoveryTime(uint value)
    {
        _commonDelay = (_commonDelay & ~0xFu) | (value & 0xF);
        _setCommonCount++;
        return unchecked((int)_commonDelay);
    }

    public int GetRecoveryTime() => (int)(_commonDelay & 0xF);

    public int SetHoldTime(uint value)
    {
        _commonDelay = (_commonDelay & ~0xF0u) | ((value << 4) & 0xF0);
        _setCommonCount++;
        return unchecked((int)_commonDelay);
    }

    public int GetHoldTime() => (int)((_commonDelay >> 4) & 0xF);

    public int SetFloatTime(uint value)
    {
        _commonDelay = (_commonDelay & ~0xF00u) | ((value << 8) & 0xF00);
        _setCommonCount++;
        return unchecked((int)_commonDelay);
    }

    public int GetFloatTime() => (int)((_commonDelay >> 8) & 0xF);

    public int SetStrobeTime(uint value)
    {
        _commonDelay = (_commonDelay & ~0xF000u) | ((value << 12) & 0xF000);
        _setCommonCount++;
        return unchecked((int)_commonDelay);
    }

    public int GetStrobeTime() => (int)((_commonDelay >> 12) & 0xF);

    /// <summary>
    /// True when a device has both a usable delay (and base when the slot has a base reg)
    /// after <see cref="ApplyBiosDefaults"/> — contract for "window ready".
    /// </summary>
    public bool IsWindowReady(int device)
    {
        if (!ValidDevice(device) || DelayRegAddr[device] == 0)
            return false;
        if (_delay[device] == 0)
            return false;
        if (BaseRegAddr[device] != 0 && _base[device] == 0)
            return false;
        return true;
    }

    /// <summary>Decode range field (bits 20:16) from delay: 2^n bytes.</summary>
    public static int DecodeRangeBytes(uint delay)
    {
        int n = (int)((delay >> 16) & 0x1F);
        if (n > 27) n = 27;
        return 1 << n;
    }
}
