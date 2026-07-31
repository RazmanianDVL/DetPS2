using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// C# HLE for BIOS / IOPRP <b>LIBSD</b> (Sound Device Library) — export table + functional
/// core contracts used by retail audio stacks (SDRDRV, MSL, game IRX).
///
/// <para><b>Authority:</b> ps2sdk <c>iop/sound/libsd</c> <c>exports.tab</c> +
/// <c>libsd-common.h</c> / <c>libsd.h</c>; SCPH70008 ROMDIR entry; Ghidra module string
/// <c>libsd</c>. Not a full SPU2 mixer reimplementation — key-on/param/addr path through
/// <see cref="Spu2"/> host hooks.</para>
///
/// <para>Export ordinals (DECLARE_EXPORT_TABLE libsd 1.4):
/// 0 _start, 1 _retonly, 2 sceSdQuit, 3 _retonly, 4 sceSdInit, 5 sceSdSetParam,
/// 6 sceSdGetParam, 7 sceSdSetSwitch, 8 sceSdGetSwitch, 9 sceSdSetAddr, 10 sceSdGetAddr,
/// 11–12 CoreAttr, 13–14 Note/Pitch, 15–16 ProcBatch, 17–20 Trans*, 21–22 callbacks,
/// 23–25 effect, 26–27 intr handlers. Version 1.5 adds StopTrans/CleanEffect/EffectMode
/// (ordinals 30–33 via imports only — planted as stubs if requested).</para>
/// </summary>
public sealed class IopLibSdHost
{
    public const string LibName = "libsd";
    public const byte VersionMajor = 1;
    public const byte VersionMinor = 4;

    /// <summary>ps2sdk exports.tab ends at ordinal 27 (28 entries).</summary>
    public const int ExportCount = 28;

    // Named ordinals (ps2sdk I_sceSd* DECLARE_IMPORT)
    public const int OrdStart = 0;
    public const int OrdRetOnly1 = 1;
    public const int OrdQuit = 2;
    public const int OrdRetOnly2 = 3;
    public const int OrdInit = 4;
    public const int OrdSetParam = 5;
    public const int OrdGetParam = 6;
    public const int OrdSetSwitch = 7;
    public const int OrdGetSwitch = 8;
    public const int OrdSetAddr = 9;
    public const int OrdGetAddr = 10;
    public const int OrdSetCoreAttr = 11;
    public const int OrdGetCoreAttr = 12;
    public const int OrdNote2Pitch = 13;
    public const int OrdPitch2Note = 14;
    public const int OrdProcBatch = 15;
    public const int OrdProcBatchEx = 16;
    public const int OrdVoiceTrans = 17;
    public const int OrdBlockTrans = 18;
    public const int OrdVoiceTransStatus = 19;
    public const int OrdBlockTransStatus = 20;
    public const int OrdSetTransCallback = 21;
    public const int OrdSetIrqCallback = 22;
    public const int OrdSetEffectAttr = 23;
    public const int OrdGetEffectAttr = 24;
    public const int OrdClearEffectWorkArea = 25;
    public const int OrdSetTransIntrHandler = 26;
    public const int OrdSetSpu2IntrHandler = 27;

    // Entry high-byte selectors (libsd-common.h)
    public const int VParamVoll = 0x00;
    public const int VParamVolr = 0x01;
    public const int VParamPitch = 0x02;
    public const int VParamAdsr1 = 0x03;
    public const int VParamAdsr2 = 0x04;
    public const int VParamEnvx = 0x05;
    public const int ParamMvoll = 0x09;
    public const int ParamMvolr = 0x0A;

    public const int SwitchPmon = 0x13;
    public const int SwitchNon = 0x14;
    public const int SwitchKon = 0x15;
    public const int SwitchKoff = 0x16;
    public const int SwitchEndx = 0x17;
    public const int SwitchVmixl = 0x18;
    public const int SwitchVmixr = 0x1A;

    public const int AddrEsa = 0x1C;
    public const int AddrEea = 0x1D;
    public const int AddrTsa = 0x1E;
    public const int AddrIrqa = 0x1F;
    public const int VAddrSsa = 0x20;
    public const int VAddrLsax = 0x21;
    public const int VAddrNax = 0x22;

    public const int TransWrite = 0;
    public const int TransRead = 1;
    public const int TransStop = 2;

    // Plant region: after IopExtendedBiosHost (0x6000..0x8800). Use a private slice.
    public const uint StubRegionPhys = 0x00008800;
    public const uint StubRegionSize = 0x00000800; // 2 KiB

    private bool _installed;
    private bool _inited;
    private int _initFlag;
    private readonly ushort[] _coreAttr = new ushort[2];
    private readonly uint[] _coreSwitch = new uint[8]; // KON/KOFF/ENDX/VMIX* snapshots per core-ish
    private readonly uint[] _coreAddr = new uint[8]; // ESA/EEA/TSA/IRQA per core
    private readonly uint[] _voiceSsa = new uint[48]; // 2 cores × 24 (host shadow; Spu2 has 24)
    private readonly uint[] _voiceLsax = new uint[48];
    private readonly ushort[] _masterVol = new ushort[4]; // MVOLL/R core0/1
    private Spu2? _spu2;
    private ulong _initCalls;
    private ulong _keyOnOps;
    private ulong _keyOffOps;
    private ulong _setParamOps;
    private ulong _voiceTransOps;
    private uint[] _exports = Array.Empty<uint>();
    private int _voiceTransBusy; // 0 = idle

    public bool Installed => _installed;
    public bool Initialized => _inited;
    public int InitFlag => _initFlag;
    public ulong InitCalls => _initCalls;
    public ulong KeyOnOps => _keyOnOps;
    public ulong KeyOffOps => _keyOffOps;
    public ulong SetParamOps => _setParamOps;
    public ulong VoiceTransOps => _voiceTransOps;
    public IReadOnlyList<uint> Exports => _exports;

    public void Reset()
    {
        _installed = false;
        _inited = false;
        _initFlag = 0;
        Array.Clear(_coreAttr);
        Array.Clear(_coreSwitch);
        Array.Clear(_coreAddr);
        Array.Clear(_voiceSsa);
        Array.Clear(_voiceLsax);
        Array.Clear(_masterVol);
        _spu2 = null;
        _initCalls = _keyOnOps = _keyOffOps = _setParamOps = _voiceTransOps = 0;
        _exports = Array.Empty<uint>();
        _voiceTransBusy = 0;
    }

    /// <summary>
    /// Plant export table + register <c>libsd</c> for LinkImports. Called from
    /// <see cref="IopExtendedBiosHost.Install"/> (owns LIBSD name) or directly.
    /// </summary>
    public void Install(Ps2System sys)
    {
        if (sys == null) return;
        var mem = sys.Memory;
        var modules = sys.IopModules;
        _spu2 = sys.Spu2;

        uint cursor = StubRegionPhys;
        uint stubJrRa = cursor;
        mem.Write32(cursor, 0x03E00008u); // jr ra
        mem.Write32(cursor + 4, 0x00000000u);
        cursor += 8;

        _exports = new uint[ExportCount];
        for (int i = 0; i < ExportCount; i++)
            _exports[i] = stubJrRa;

        cursor = PlantExportTable(mem, cursor, LibName, VersionMajor, VersionMinor, _exports);
        modules.RegisterExportLibrary(new IrxLoader.ExportTable
        {
            Name = LibName,
            VersionMajor = VersionMajor,
            VersionMinor = VersionMinor,
            Exports = _exports
        });
        modules.RegisterModule("LIBSD", systemResident: true);
        modules.RegisterModule("rom0:LIBSD", systemResident: true);

        _installed = true;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[BIOS] IopLibSdHost installed exports={ExportCount} cursor=0x{cursor:X}");
    }

    /// <summary>sceSdInit(flag) — soft-init cores; flag bits ignored for HLE success.</summary>
    public int SdInit(int flag)
    {
        _initFlag = flag;
        _inited = true;
        _initCalls++;
        _voiceTransBusy = 0;
        // Default core attr: SPU2 on, unmuted
        _coreAttr[0] = unchecked((ushort)(1 << 15)); // SD_SPU2_ON
        _coreAttr[1] = unchecked((ushort)(1 << 15));
        _masterVol[0] = _masterVol[1] = _masterVol[2] = _masterVol[3] = 0x3FFF;
        _spu2?.WriteRegister(Spu2.PhysBase + 0x1A0, 0); // touch enable path
        if (_spu2 != null)
        {
            // Ensure Spu2 is live for subsequent key-on
            // Writing 0 key-on mask does not start voices; Enabled stays false until KON.
        }
        return 0; // success
    }

    /// <summary>sceSdQuit — mark uninit; does not wipe Spu2 RAM (matches soft quit).</summary>
    public int SdQuit()
    {
        _inited = false;
        return 0;
    }

    /// <summary>sceSdSetParam(entry, value).</summary>
    public void SdSetParam(ushort entry, ushort value)
    {
        _setParamOps++;
        int kind = (entry >> 8) & 0x7F;
        int core = entry & 1;
        int voice = (entry & 0xFF) >> 1;

        if (kind <= VParamEnvx)
        {
            int vIdx = MapVoice(core, voice);
            _spu2?.HostSetVoiceParam(vIdx, kind, value);
            return;
        }

        // Master volumes (bit 0x80 set in SD_PARAM_MVOLL etc.)
        switch (kind)
        {
            case ParamMvoll:
                _masterVol[core * 2] = value;
                break;
            case ParamMvolr:
                _masterVol[core * 2 + 1] = value;
                break;
        }
    }

    /// <summary>sceSdGetParam(entry).</summary>
    public ushort SdGetParam(ushort entry)
    {
        int kind = (entry >> 8) & 0x7F;
        int core = entry & 1;
        int voice = (entry & 0xFF) >> 1;

        if (kind <= VParamEnvx)
        {
            int vIdx = MapVoice(core, voice);
            return (ushort)(_spu2?.HostGetVoiceParam(vIdx, kind) ?? 0);
        }

        return kind switch
        {
            ParamMvoll => _masterVol[core * 2],
            ParamMvolr => _masterVol[core * 2 + 1],
            _ => 0
        };
    }

    /// <summary>sceSdSetSwitch(entry, value) — KON/KOFF drive Spu2 key-on/off.</summary>
    public void SdSetSwitch(ushort entry, uint value)
    {
        int kind = (entry >> 8) & 0x7F;
        int core = entry & 1;
        int slot = core * 4 + Math.Min(kind - SwitchPmon, 3);
        if (slot >= 0 && slot < _coreSwitch.Length)
            _coreSwitch[slot] = value;

        switch (kind)
        {
            case SwitchKon:
                _keyOnOps++;
                // 24-bit voice mask → Spu2 low 16 + high 8
                _spu2?.HostKeyOnMask(value & 0xFFFFu, 0);
                _spu2?.HostKeyOnMask((value >> 16) & 0xFFu, 16);
                break;
            case SwitchKoff:
                _keyOffOps++;
                _spu2?.HostKeyOffMask(value & 0xFFFFu, 0);
                _spu2?.HostKeyOffMask((value >> 16) & 0xFFu, 16);
                break;
            // PMON/NON/ENDX/VMIX*: store only (mixer residual)
        }
    }

    /// <summary>sceSdGetSwitch(entry).</summary>
    public uint SdGetSwitch(ushort entry)
    {
        int kind = (entry >> 8) & 0x7F;
        int core = entry & 1;
        int slot = core * 4 + Math.Min(kind - SwitchPmon, 3);
        if (slot >= 0 && slot < _coreSwitch.Length)
            return _coreSwitch[slot];
        return 0;
    }

    /// <summary>sceSdSetAddr(entry, value).</summary>
    public void SdSetAddr(ushort entry, uint value)
    {
        int kind = (entry >> 8) & 0x7F;
        int core = entry & 1;
        int voice = (entry & 0xFF) >> 1;
        int vIdx = MapVoice(core, voice);
        int flat = core * 24 + Math.Clamp(voice, 0, 23);

        switch (kind)
        {
            case VAddrSsa:
                if ((uint)flat < _voiceSsa.Length) _voiceSsa[flat] = value;
                _spu2?.HostSetVoiceSsa(vIdx, value);
                break;
            case VAddrLsax:
                if ((uint)flat < _voiceLsax.Length) _voiceLsax[flat] = value;
                break;
            case AddrEsa:
                _coreAddr[core * 4 + 0] = value;
                break;
            case AddrEea:
                _coreAddr[core * 4 + 1] = value;
                break;
            case AddrTsa:
                _coreAddr[core * 4 + 2] = value;
                break;
            case AddrIrqa:
                _coreAddr[core * 4 + 3] = value;
                break;
        }
    }

    /// <summary>sceSdGetAddr(entry).</summary>
    public uint SdGetAddr(ushort entry)
    {
        int kind = (entry >> 8) & 0x7F;
        int core = entry & 1;
        int voice = (entry & 0xFF) >> 1;
        int vIdx = MapVoice(core, voice);
        int flat = core * 24 + Math.Clamp(voice, 0, 23);

        return kind switch
        {
            VAddrSsa => (uint)flat < _voiceSsa.Length
                ? _voiceSsa[flat]
                : (_spu2?.HostGetVoiceSsa(vIdx) ?? 0),
            VAddrLsax => (uint)flat < _voiceLsax.Length ? _voiceLsax[flat] : 0,
            AddrEsa => _coreAddr[core * 4 + 0],
            AddrEea => _coreAddr[core * 4 + 1],
            AddrTsa => _coreAddr[core * 4 + 2],
            AddrIrqa => _coreAddr[core * 4 + 3],
            _ => 0
        };
    }

    public void SdSetCoreAttr(ushort entry, ushort value)
    {
        int core = entry & 1;
        _coreAttr[core] = value;
    }

    public ushort SdGetCoreAttr(ushort entry) => _coreAttr[entry & 1];

    /// <summary>Approximate sceSdNote2Pitch (equal-tempered from center note).</summary>
    public ushort SdNote2Pitch(ushort centerNote, ushort centerFine, ushort note, short fine)
    {
        // pitch @ 0x1000 = unity at center. Semi-tones: 2^(n/12).
        double semis = (note - centerNote) + (fine - (short)centerFine) / 128.0;
        double ratio = Math.Pow(2.0, semis / 12.0);
        int pitch = (int)Math.Clamp(0x1000 * ratio, 1, 0x3FFF);
        return (ushort)pitch;
    }

    /// <summary>Inverse of <see cref="SdNote2Pitch"/> (coarse).</summary>
    public ushort SdPitch2Note(ushort centerNote, ushort centerFine, ushort pitch)
    {
        if (pitch == 0) return centerNote;
        double semis = 12.0 * Math.Log(pitch / (double)0x1000) / Math.Log(2.0);
        int note = (int)Math.Round(centerNote + semis);
        return (ushort)Math.Clamp(note, 0, 0x7F);
    }

    /// <summary>
    /// sceSdVoiceTrans — IOP→SPU write (mode WRITE) of <paramref name="size"/> bytes from
    /// EE/IOP mem at <paramref name="iopAddr"/> into SPU RAM at <paramref name="spuAddr"/>.
    /// </summary>
    public int SdVoiceTrans(SystemMemory? mem, short chan, ushort mode, uint iopAddr, uint spuAddr, uint size)
    {
        _ = chan;
        _voiceTransOps++;
        if ((mode & 3) == TransStop)
        {
            _voiceTransBusy = 0;
            return 0;
        }
        if (mem == null || size == 0 || _spu2 == null)
        {
            _voiceTransBusy = 0;
            return 0;
        }

        if ((mode & 3) == TransWrite || (mode & 3) == 0)
        {
            size = Math.Min(size, 0x10000u);
            var buf = new byte[size];
            for (uint i = 0; i < size; i++)
                buf[i] = mem.Read8(iopAddr + i);
            int n = _spu2.HostWriteSpuRam(spuAddr, buf);
            _voiceTransBusy = 0; // complete immediately (HLE)
            return n;
        }

        if ((mode & 3) == TransRead)
        {
            size = Math.Min(size, 0x10000u);
            var buf = new byte[size];
            int n = _spu2.HostReadSpuRam(spuAddr, buf);
            for (int i = 0; i < n; i++)
                mem.Write8(iopAddr + (uint)i, buf[i]);
            _voiceTransBusy = 0;
            return n;
        }

        return 0;
    }

    /// <summary>sceSdVoiceTransStatus — 0 busy, 1 complete (HLE always complete after call).</summary>
    public uint SdVoiceTransStatus(short channel, short flag)
    {
        _ = channel;
        _ = flag;
        return _voiceTransBusy == 0 ? 1u : 0u;
    }

    public int SdBlockTrans(short chan, ushort mode, uint iopAddr, uint size) =>
        // BlockTrans shares VoiceTrans path for WRITE/READ HLE.
        SdVoiceTrans(null, chan, mode, iopAddr, 0, size) >= 0 ? 0 : -1;

    public uint SdBlockTransStatus(short channel, short flag) => SdVoiceTransStatus(channel, flag);

    public int SdClearEffectWorkArea(int core, int channel, int effectMode)
    {
        _ = core; _ = channel; _ = effectMode;
        return 0;
    }

    public int SdSetEffectAttr(int core, int mode, short depthL, short depthR, int delay, int feedback)
    {
        _ = core; _ = mode; _ = depthL; _ = depthR; _ = delay; _ = feedback;
        if (_spu2 != null && mode != 0)
        {
            _spu2.ReverbEnabled = true;
            if (delay > 0) _spu2.ReverbDelaySamples = Math.Clamp(delay, 1, 8000);
            if (feedback > 0) _spu2.ReverbFeedback = Math.Clamp(feedback, 0, 99);
        }
        else if (_spu2 != null && mode == 0)
            _spu2.ReverbEnabled = false;
        return 0;
    }

    /// <summary>Build <c>SD_VOICE(core,v) | (param&lt;&lt;8)</c> entry for tests / host callers.</summary>
    public static ushort MakeVoiceEntry(int core, int voice, int paramKind) =>
        (ushort)(((paramKind & 0x7F) << 8) | ((voice & 0x7F) << 1) | (core & 1));

    public static ushort MakeSwitchEntry(int core, int switchKind) =>
        (ushort)(((switchKind & 0x7F) << 8) | (core & 1));

    public static ushort MakeAddrEntry(int core, int voice, int addrKind) =>
        (ushort)(((addrKind & 0x7F) << 8) | ((voice & 0x7F) << 1) | (core & 1));

    /// <summary>
    /// Our Spu2 model is 24 voices (one core). Core1 voices alias 0..23 for HLE link success.
    /// </summary>
    private static int MapVoice(int core, int voice)
    {
        _ = core;
        return Math.Clamp(voice, 0, Spu2.VoiceCount - 1);
    }

    private static uint PlantExportTable(SystemMemory mem, uint at, string name,
        byte verMaj, byte verMin, uint[] exports)
    {
        mem.Write32(at + 0x00, IrxLoader.ExportTableMagic);
        mem.Write32(at + 0x04, 0);
        mem.Write8(at + 0x08, verMin);
        mem.Write8(at + 0x09, verMaj);
        mem.Write8(at + 0x0A, 0);
        mem.Write8(at + 0x0B, 0);
        for (int i = 0; i < 8; i++)
            mem.Write8(at + 0x0C + (uint)i, i < name.Length ? (byte)name[i] : (byte)0);
        uint p = at + 0x14;
        for (int i = 0; i < exports.Length; i++, p += 4)
            mem.Write32(p, exports[i]);
        mem.Write32(p, 0);
        return p + 4;
    }
}
