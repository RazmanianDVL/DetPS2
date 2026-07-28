using System;
using System.Collections.Generic;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// BIOS / kernel HLE (Phases 9–14).
/// Syscall in $v1; args $a0–$a3; return $v0.
/// </summary>
public sealed class BiosHle
{
    public const uint SysExit = 0x01;
    public const uint SysWrite = 0x02;
    public const uint SysGsClear = 0x20;
    public const uint SysGsDrawTest = 0x21;
    public const uint SysGsDrawTri = 0x22;
    public const uint SysGsDrawSprite = 0x23;
    public const uint SysGsSetCrt = 0x24;
    public const uint SysPadRead = 0x30;
    public const uint SysThreadCreate = 0x40;
    public const uint SysThreadSleep = 0x41;
    public const uint SysThreadDelete = 0x42;
    public const uint SysThreadStart = 0x43;
    public const uint SysThreadWakeup = 0x44;
    public const uint SysThreadRotate = 0x45;
    public const uint SysWaitVblank = 0x46;
    public const uint SysCreateSema = 0x47;
    public const uint SysSignalSema = 0x48;
    public const uint SysWaitSema = 0x49;
    public const uint SysDeleteSema = 0x4A;
    public const uint SysCreateEventFlag = 0x4B;
    public const uint SysSetEventFlag = 0x4C;
    public const uint SysClearEventFlag = 0x4D;
    public const uint SysPollEventFlag = 0x4E;
    public const uint SysFileOpen = 0x50;
    public const uint SysFileClose = 0x51;
    public const uint SysFileRead = 0x52;
    public const uint SysFileWrite = 0x53;
    public const uint SysLoadExec = 0x55;
    public const uint SysFlushCache = 0x64;
    public const uint SysGetTimer = 0x70;
    public const uint SysSifInit = 0x71;
    public const uint SysSifSetDma = 0x72;
    public const uint SysReferThreadStatus = 0x4F;
    public const uint SysGetThreadId = 0x56;
    public const uint SysIWakeupThread = 0x57;
    public const uint SysExitDeleteThread = 0x58;
    public const uint SysCreateSemaEx = 0x59;
    public const uint SysLoadIrx = 0x83;
    public const uint SysSifRpcCall = 0x80;
    public const uint SysLoadModule = 0x81;
    public const uint SysSifRpcSync = 0x82;
    // Phase 41 boot-spine safe HLE
    public const uint SysGsPutDrawEnv = BootBlockerFixes.SysGsPutDrawEnv;
    public const uint SysGsPutDisplayEnv = BootBlockerFixes.SysGsPutDisplayEnv;
    public const uint SysSifLoadModuleBuffer = BootBlockerFixes.SysSifLoadModuleBuffer;
    public const uint SysSifCheckStatModule = BootBlockerFixes.SysSifCheckStatModule;
    public const uint SysDeci2Call = BootBlockerFixes.SysDeci2Call;
    public const uint SysKSeg0 = BootBlockerFixes.SysKSeg0;

    private readonly Ps2System _system;
    private readonly Dictionary<int, string> _openFiles = new();
    private int _nextFd = 3;
    private readonly StringBuilder _console = new();
    private readonly KernelState _kernel = new();
    private SonyKernelHle? _sony;
    /// <summary>Sony commercial HLE (null until EnableSonyKernel).</summary>
    public SonyKernelHle? Sony => _sony;

    public KernelState Kernel => _kernel;
    public HleLevel Level { get; set; } = HleLevel.Standard;
    /// <summary>When true, dispatch the real Sony EE syscall table (commercial BIOS boots).</summary>
    public bool SonyKernelMode { get; set; }
    public ulong SyscallCount { get; private set; }
    public bool ExitRequested { get; private set; }
    public int ExitCode { get; private set; }
    public string ConsoleOutput => _console.ToString();
    public uint CrtMode { get; set; }

    public BiosHle(Ps2System system)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _kernel.Reset();
        _sony = new SonyKernelHle(system, _kernel);
    }

    public void Reset()
    {
        SyscallCount = 0;
        ExitRequested = false;
        ExitCode = 0;
        _console.Clear();
        _openFiles.Clear();
        _nextFd = 3;
        CrtMode = 0;
        SonyKernelMode = false;
        _kernel.Reset();
        _sony?.Reset();
    }

    /// <summary>Mark commercial mode after a real BIOS image is loaded.</summary>
    public void EnableSonyKernel()
    {
        SonyKernelMode = true;
        _sony ??= new SonyKernelHle(_system, _kernel);
        _sony.Reset();
    }

    public void RequestExit(int code)
    {
        ExitRequested = true;
        ExitCode = code;
    }

    public void OnVblank() => _kernel.OnVblank();

    public bool HandleSyscall(EmotionEngine ee)
    {
        SyscallCount++;
        uint num = (uint)ee.GetGpr(3).Lo;
        // Real PS2 SDKs commonly encode a syscall number negated (li v1,-N instead of li v1,N)
        // as the standard "fast syscall" convention (documented ps2sdk/libkernel behavior) --
        // same syscall, just dispatched without certain BIOS-side checks. The real BIOS negates
        // it back before dispatch; this HLE didn't, so every negative-encoded syscall silently
        // fell through as unhandled regardless of which one it was. Confirmed via Mortal Kombat:
        // Deception (SLUS_208.81, no game-specific quirks registered) issuing raw v1=0xFFFFFFAB/
        // 0xFFFFFFA8 (i.e. -0x55/-0x58) that never matched any positive case.
        if ((num & 0x80000000u) != 0) num = (uint)(-(int)num);
        uint a0 = (uint)ee.GetGpr(4).Lo;
        uint a1 = (uint)ee.GetGpr(5).Lo;
        uint a2 = (uint)ee.GetGpr(6).Lo;
        uint a3 = (uint)ee.GetGpr(7).Lo;

        // Commercial path: full Sony EE kernel ABI
        if (SonyKernelMode && _sony != null && _sony.TryHandle(ee, num, out long sonyResult))
        {
            ee.SetGpr(2, new EmotionEngine.Gpr128 { Lo = unchecked((ulong)sonyResult) });
            return true;
        }

        long result = 0;
        switch (num)
        {
            case SysExit:
                ExitRequested = true;
                ExitCode = (int)a0;
                break;

            case SysWrite:
                result = HleWrite(a0, a1, a2);
                break;

            case SysGsClear:
                _system.Gs.Clear(a0 == 0 ? 0xFF000000u : a0);
                break;

            case SysGsDrawTest:
                _system.Gs.RenderTestScene();
                result = (long)_system.Gs.PixelsWritten;
                break;

            case SysGsDrawTri:
                {
                    int x0 = (int)(a0 & 0xFFFF), y0 = (int)(a0 >> 16);
                    int x1 = (int)(a1 & 0xFFFF), y1 = (int)(a1 >> 16);
                    int x2 = (int)(a2 & 0xFFFF), y2 = (int)(a2 >> 16);
                    _system.Gs.WriteGsRegister(0x00, 0x03);
                    _system.Gs.DrawScreenTriangle(x0, y0, x1, y1, x2, y2, a3);
                    result = 1;
                }
                break;

            case SysGsDrawSprite:
                {
                    int x = (int)(a0 & 0xFFFF), y = (int)(a0 >> 16);
                    int w = (int)(a1 & 0xFFFF), h = (int)(a1 >> 16);
                    _system.Gs.DrawQuad(x, y, w, h, a2);
                    result = 1;
                }
                break;

            case SysGsSetCrt:
                CrtMode = a0;
                result = 0;
                break;

            case SysPadRead:
                result = _system.Pad.Buttons;
                break;

            case SysThreadCreate:
                result = _kernel.CreateThread(a0, a1, a2);
                break;

            case SysThreadSleep:
                result = _kernel.SleepThread();
                break;

            case SysThreadDelete:
                result = _kernel.DeleteThread((int)a0);
                break;

            case SysThreadStart:
                result = _kernel.StartThread((int)a0);
                break;

            case SysThreadWakeup:
                result = _kernel.WakeupThread((int)a0);
                break;

            case SysThreadRotate:
                result = _kernel.RotateThread();
                break;

            case SysWaitVblank:
                result = _kernel.WaitSemaVblank();
                break;

            case SysCreateSema:
                result = _kernel.CreateSema((int)a0, (int)a1);
                break;

            case SysSignalSema:
                result = _kernel.SignalSema((int)a0);
                break;

            case SysWaitSema:
                // Auto-signal so commercial WaitSema doesn't hang the boot path
                _kernel.SignalSema((int)a0);
                result = _kernel.WaitSema((int)a0);
                break;

            case SysDeleteSema:
                result = _kernel.DeleteSema((int)a0);
                break;

            case SysCreateEventFlag:
                result = _kernel.CreateEventFlag(a0);
                break;

            case SysSetEventFlag:
                result = _kernel.SetEventFlag((int)a0, a1);
                break;

            case SysClearEventFlag:
                result = _kernel.ClearEventFlag((int)a0, a1);
                break;

            case SysPollEventFlag:
                result = _kernel.PollEventFlag((int)a0);
                break;

            case SysReferThreadStatus:
                result = _kernel.ThreadCount > 0 ? 0 : -1;
                break;

            case SysGetThreadId:
                result = _kernel.CurrentThreadId;
                break;

            case SysIWakeupThread:
                result = _kernel.WakeupThread((int)a0);
                break;

            case SysExitDeleteThread:
                result = _kernel.DeleteThread(_kernel.CurrentThreadId);
                break;

            case SysCreateSemaEx:
                result = _kernel.CreateSema((int)a0, (int)a1);
                break;

            case SysSifInit:
                result = 0;
                break;

            case SysSifSetDma:
                // a0 = packet addr (simplified: run one SIF step)
                _system.Sif.Step(32);
                result = 1;
                break;

            case SysLoadIrx:
                // Dual meaning: homebrew LoadIrx (buffer+size) vs Sony FindAddress (0x83).
                // Heuristic: large a0 + nonzero size → IRX load; else FindAddress stub.
                if (a0 >= 0x10000 && a1 > 16 && a1 < 0x200000)
                {
                    byte[] elf = new byte[a1];
                    for (uint i = 0; i < a1; i++)
                        elf[i] = _system.Memory.Read8(a0 + i);
                    var lr = _system.IopModules.LoadIrx(elf, _system.Memory);
                    result = lr.Success && _system.IopModules.TryGetModule(lr.ModuleName, out int mid)
                        ? mid
                        : -1;
                }
                else
                {
                    // FindAddress: point at a tiny JR RA; NOP stub so JALR targets are safe
                    InstallKernelStub(0x00082000);
                    result = 0x00082000;
                }
                break;

            case SysFileOpen:
                {
                    // Prefer SIF RPC path when Standard+
                    if (Level >= HleLevel.Standard)
                    {
                        string path = ReadCString(a0, 256);
                        // stash path and open via RPC
                        uint pathAddr = 0x0000D000;
                        WriteCString(pathAddr, path);
                        result = (long)_system.CallRpc(SifRpcCmd.Open, pathAddr, 0);
                        if (result >= 3)
                            _openFiles[(int)result] = path;
                    }
                    else
                    {
                        string path = ReadCString(a0, 256);
                        int fd = _nextFd++;
                        _openFiles[fd] = path;
                        result = fd;
                    }
                }
                break;

            case SysFileClose:
                _openFiles.Remove((int)a0);
                if (Level >= HleLevel.Standard)
                    _system.CallRpc(SifRpcCmd.Close, 0, a0);
                result = 0;
                break;

            case SysFileRead:
                if (Level >= HleLevel.Standard)
                    result = (long)_system.CallRpc(SifRpcCmd.Read, a1, a2);
                else
                {
                    for (uint i = 0; i < a2; i++)
                        _system.Memory.Write8(a1 + i, 0);
                    result = a2;
                }
                break;

            case SysFileWrite:
                if (Level >= HleLevel.Standard)
                    result = (long)_system.CallRpc(SifRpcCmd.Write, a1, a2);
                else
                    result = a2;
                break;

            case SysLoadExec:
                // a0 = path or 0; a1 = optional; reload homebrew demo if path empty
                {
                    string path = ReadCString(a0, 256);
                    if (string.IsNullOrEmpty(path) || path.Contains("DEMO", StringComparison.OrdinalIgnoreCase))
                    {
                        var load = _system.LoadHomebrewGsDemo();
                        result = (long)load.Entry;
                    }
                    else
                        result = -1;
                }
                break;

            case SysFlushCache:
                result = 0;
                break;

            case SysGetTimer:
                result = (long)_system.MasterCycles;
                break;

            case SysSifRpcCall:
                _system.Sif.SubmitRpc(a0);
                _system.Sif.Step(16);
                result = _system.Memory.Read32(a0 + 12);
                break;

            case SysLoadModule:
                {
                    string name = ReadCString(a0, 64);
                    result = _system.IopModules.RegisterModule(name);
                }
                break;

            case SysSifRpcSync:
                _system.Sif.Step(64);
                result = (long)_system.Sif.RpcProcessed;
                break;

            case SysGsPutDrawEnv:
            case SysGsPutDisplayEnv:
                // Accept draw/display env pointers — no-op success (Phase 41)
                result = 0;
                break;

            case SysSifLoadModuleBuffer:
                // a0 = EE buffer ELF, a1 = size
                if (a1 > 0 && a1 < 0x200000)
                {
                    byte[] elf = new byte[a1];
                    for (uint i = 0; i < a1; i++)
                        elf[i] = _system.Memory.Read8(a0 + i);
                    var lr = _system.IopModules.LoadIrx(elf, _system.Memory);
                    result = lr.Success ? 0 : -1;
                }
                else result = -1;
                break;

            case SysSifCheckStatModule:
                result = 0; // finished
                break;

            case BootBlockerFixes.SysRFU060:
                // SetupThread (Sony EE kernel #60): CRT0 does move $sp, $v0 after this.
                // a0=gp, a1=stack base, a2=stack_size, a3=args. Return usable SP (top of stack).
                {
                    uint stackBase = a1;
                    uint stackSize = a2;
                    if (stackSize == 0) stackSize = 0x10000;
                    ulong spTop = (ulong)stackBase + stackSize;
                    if (stackBase >= 0x100000 && spTop <= SystemMemory.RDRAM_SIZE)
                        result = (long)(spTop & ~0xFUL);
                    else if (stackBase > 0x10000 && stackBase < SystemMemory.RDRAM_SIZE)
                        result = (long)(stackBase & ~0xFUL);
                    else
                        result = 0x01FF0000;
                }
                break;

            case BootBlockerFixes.SysRFU061:
            case BootBlockerFixes.SysEndOfHeap:
                result = 0;
                break;

            // Sony SIF helpers (numbers that don't collide with homebrew HLE ABI). Real numbers
            // per BootBlockerFixes.SonySifDmaStat's own doc comment — this whole block used to
            // have SetDma/DmaStat swapped and SetReg/GetReg off by one; fixed to match the same
            // confirmed ABI SonyKernelHle.cs uses (this path only runs for homebrew titles not
            // in Sony-kernel mode; SonyKernelHle.TryHandle already handles the real numbers
            // first for every commercial title). SonySifLoadModule (the old case here) is gone
            // — a real sceSifLoadModule is an RPC call to the IOP's loadfile service (see
            // RealSifRpc.cs), not a raw EE syscall; SysSifLoadModuleBuffer below is the real,
            // already-correct mechanism for the homebrew path.
            case BootBlockerFixes.SonySifDmaStat:
                result = -1; // complete
                break;
            case BootBlockerFixes.SonySifSetDma:
            case BootBlockerFixes.SonySifSetDChain:
                _system.Sif.Step(32);
                result = 1;
                break;
            case BootBlockerFixes.SonySifSetReg:
            case BootBlockerFixes.SonySifGetReg:
                result = 0;
                break;

            case SysDeci2Call:
            case SysKSeg0:
            case BootBlockerFixes.SysGetCop0:
            case BootBlockerFixes.SysSetCop0:
                result = 0;
                break;

            default:
                if (BootBlockerFixes.KnownSafeSyscalls.Contains(num))
                {
                    result = 0;
                    break;
                }
                result = -1;
                _system.Telemetry.UnknownSyscall(
                    _system.MasterCycles,
                    ee.PC,
                    num);
                break;
        }

        ee.SetGpr(2, new EmotionEngine.Gpr128 { Lo = unchecked((ulong)result) });
        return true;
    }

    private int HleWrite(uint fd, uint buf, uint len)
    {
        if (len > 0x10000) len = 0x10000;
        var sb = new StringBuilder((int)len);
        for (uint i = 0; i < len; i++)
        {
            byte b = _system.Memory.Read8(buf + i);
            if (b == 0) break;
            sb.Append((char)b);
        }
        if (fd <= 2)
            _console.Append(sb);
        return (int)len;
    }

    private string ReadCString(uint addr, int max)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < max; i++)
        {
            byte b = _system.Memory.Read8(addr + (uint)i);
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    private void WriteCString(uint addr, string s)
    {
        for (int i = 0; i < s.Length; i++)
            _system.Memory.Write8(addr + (uint)i, (byte)s[i]);
        _system.Memory.Write8(addr + (uint)s.Length, 0);
    }

    /// <summary>Plant <c>jr ra; nop</c> so FindAddress / export stubs are callable.</summary>
    private void InstallKernelStub(uint addr)
    {
        // jr ra  = 0x03E00008 ; nop = 0
        _system.Memory.Write32(addr, 0x03E00008u);
        _system.Memory.Write32(addr + 4, 0u);
        _system.Memory.Write32(addr + 8, 0x03E00008u);
        _system.Memory.Write32(addr + 12, 0u);
    }
}
