using System;
using System.Collections.Generic;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Real Sony SIF RPC wire-protocol HLE (bind + call), layered under the existing
/// SifSetDma (EE syscall 0x77) intercept in SonyKernelHle. This is distinct from
/// the synthetic 16-byte "DetPS2 RPC ABI" in SifRpc.cs (still used by our own HLE
/// syscalls) — retail-compiled ps2sdk sifrpc.c code speaks THIS protocol over real
/// SIF DMA, so unmodified commercial binaries only get serviced through here.
///
/// Packet shapes and cid values verified against ps2sdk source (github.com/ps2dev/ps2sdk):
///   common/include/sifcmd-common.h, sifrpc-common.h; ee/kernel/src/sifrpc.c;
///   ee/rpc/cdvd/src/{scmd,ncmd}.c; ee/rpc/pad/src/libpad.c; ee/rpc/memorycard/src/libmc.c
///
/// Design: this is a transport-level shim, not real IOP CPU emulation. The real
/// EE-side sifrpc.c library already creates and waits on a semaphore
/// (SifRpcClientData_t.hdr.sema_id) for both bind and call — we don't need to
/// fake that wait/wake mechanism, just read the (already valid, real) semaphore
/// id the game wrote and signal it via the existing, real KernelState semaphore
/// implementation once we've written our response.
/// </summary>
public sealed class RealSifRpc
{
    public const uint CidRpcEnd = 0x80000008;
    public const uint CidRpcBind = 0x80000009;
    public const uint CidRpcCall = 0x8000000A;
    public const uint CidRpcRdata = 0x8000000C;
    /// <summary>SIFCMD INIT / system command family (BIOS SIFCMD.IRX registers 0x80000001).</summary>
    public const uint CidSifInit = 0x80000000;
    public const uint CidSifSetSreg = 0x80000001;

    /// <summary>Host for LOADFILE MOD_LOAD StartLoadedModule (WP-25/31). Bound from SonyKernelHle.</summary>
    private Ps2System? _host;
    /// <summary>Wire host so disc MOD_LOAD can run real IRX _start after LoadIrx.</summary>
    public void BindHost(Ps2System system) => _host = system;

    // Known real service ids (sid) bound by retail libcdvd/libpad/libmc/fileio.
    // CDVDFSV registration ground-truthed against decompiled FUN_000044ac / FUN_0000457c
    // (tools/bios-decomp/CDVDFSV_ALL.txt): 0x592 init, 0x593 SCMD, 0x595 NCMD, 0x597 SearchFile,
    // 0x59a DiskReady. IOPRP 2.8+ CDVDFSV (disc IOP/IOPRP280.IMG, Burnout 3) also registers
    // 0x59c with the *same* DiskReady handler + buffer as 0x59a (MIPS: lui/ori 0x8000059C →
    // handler vaddr 0x30D8 identical to 0x59a). CDVDMANIA RPS list confirms the sixth sid.
    public const uint SidCdScmd = 0x80000593; // sync CDVD status calls (FUN_000041b8)
    public const uint SidCdNcmd = 0x80000595; // async CDVD read/seek — the real disc-data path
    /// <summary>NEW PADMAN primary (disc/later modules). ps2sdk PAD_BIND_RPC_ID1_NEW.</summary>
    public const uint SidPad1 = 0x80000100;
    /// <summary>NEW PADMAN secondary. ps2sdk PAD_BIND_RPC_ID2_NEW.</summary>
    public const uint SidPad2 = 0x80000101;
    /// <summary>
    /// rom0:PADMAN primary (BIOS "Pad driver. (99/11/22)"). Ghidra FUN_000066b0 registers
    /// <c>sceSifRegisterRpc(..., 0x8000010f, FUN_0000655c, ...)</c>. ps2sdk PAD_BIND_RPC_ID1_OLD.
    /// </summary>
    public const uint SidPadOld1 = 0x8000010F;
    /// <summary>
    /// rom0:PADMAN "Extend Service" — registered but FUN_00006744 always rejects.
    /// ps2sdk PAD_BIND_RPC_ID2_OLD; EE libpad still must bind successfully.
    /// </summary>
    public const uint SidPadOld2 = 0x8000011F;
    public const uint SidMcServ = 0x80000400;
    /// <summary>
    /// BIOS FILEIO.IRX / retail <c>sceOpen</c> RPC service (ps2tek + ps2sdk fileio:
    /// <c>sceSifBindRpc(&amp;cd, 0x80000001, 0)</c>). Distinct from SIFCMD <see cref="CidSifSetSreg"/>
    /// which shares the same numeric value in the *command-id* namespace, not RPC sid space.
    /// </summary>
    public const uint SidFileIo = 0x80000001;
    /// <summary>
    /// SotC (SCUS_974.72) binds this after <c>PL2303.IRX</c> / <c>USBD.IRX</c> load.
    /// Soft-HLE: bind completes; calls return 0. Not required for STARTUP.XFF path.
    /// </summary>
    public const uint SidPl2303Usb = 0x80000220;
    /// <summary>CDVDFSV <c>sceCdInit</c> service (FUN_00000204 registered at 0x80000592).</summary>
    public const uint SidCdBase = 0x80000592;
    /// <summary>CDVDFSV <c>sceCdSearchFile</c> (FUN_000002f0 registered at 0x80000597).</summary>
    public const uint SidCdSearchFile = 0x80000597;
    /// <summary>CDVDFSV blocking DiskReady (FUN_000032d8 / IOPRP2.8 0x30D8 registered at 0x8000059a).</summary>
    public const uint SidCdDiskReady = 0x8000059a;
    /// <summary>
    /// CDVDFSV DiskReady twin (IOPRP 2.8+ / XCDVDFSV). Same handler as <see cref="SidCdDiskReady"/>
    /// (handler vaddr 0x30D8, buffer 0x63B8). Newer EE libcdvd (post-CdInit version probe) binds
    /// this sid instead of 0x59a — Burnout 3 live: bind+call fno=0 after SCMD MMODE.
    /// </summary>
    public const uint SidCdDiskReady2 = 0x8000059C;

    // Real ps2sdk IOP-heap allocator RPC service (ee/kernel/src/iopheap.c: SifInitIopHeap
    // binds sid=0x80000003 unconditionally). Confirmed via a real boot: MK Shaolin Monks'
    // PS2RNA_Init calls SifAllocIopHeap(size), whose panic string ("Failed, sceSifAllocIopHeap
    // (%d) in PS2RNA_Init") is what's printed when this call's reply isn't a real allocated
    // address. Unlike the other "unknown service -> return 1" fallbacks, this one MUST return
    // an actual usable IOP RAM pointer in recvbuf, since the caller does `(void*)arg.addr`
    // with no further success/failure check on the value itself.
    public const uint SidSysmem = 0x80000003;

    // Midway/Surreal's proprietary "SNDF_Driver" (SNDFI.IRX, v2.24b, built 2005-07-22) —
    // extracted from the real disc image and disassembled (R3000A) since no public
    // documentation of this protocol exists anywhere. sid is the module's own name read
    // literally from its .iopmod header ("SNDF_Driver" -> "SNDF" as a big-endian-in-register
    // fourCC, matching sceSifRegisterRpc's actual call args at file offset 0xBAD8 in the
    // extracted IRX). The RPC callback (real vaddr 0xBAE0) dispatches on (fno & 0xFF00);
    // fno=0x1300 (the only value this title's boot path calls) reached a handler (real vaddr
    // 0xB718) that returns 0 on success, -1 only if the first packed dword (arg buf word 0)
    // is zero — a plain C "0 == success" contract, NOT this file's usual "return 1" idiom.
    public const uint SidSndf = 0x534E4446;
    private const uint SndfInitAudio = 0x1300;

    /// <summary>
    /// SN Systems ProDG residual debug/RPC service seen on Crystal Dynamics titles
    /// (Blood Omen 2 binds <c>0x00534E03</c> after the SN Debugger extension check).
    /// No public IOP IRX; treat bind+call as 0-success so boot can continue without a T10000.
    /// </summary>
    public const uint SidSnProdg = 0x00534E03;

    // SNDFI.IRX's second registered service, "SFSV" as the same fourCC-in-register convention
    // as SidSndf (registered at real vaddr 0xC2C8, same module/.iopmod as SNDF_Driver). Bound
    // early but not called until real forward progress unblocks the code path that reaches it
    // (confirmed live: 2026-07-28, only reached after the LOADFILE/sid=0x80000006 fix below let
    // the boot sequence's reference-counted init gate get its first real decrements). Only
    // fno=0x8000 observed so far; not independently disassembled the way SNDF's fno=0x1300 was,
    // but same module/toolchain ("MW MIPS C Compiler") as SNDF_Driver and SDRDRV, both confirmed
    // to use a 0-for-success convention -- applying that same convention here rather than this
    // file's generic "unknown service -> 1" fallback, since the latter comes from a different,
    // unrelated toolchain assumption that's already been shown wrong for this module's siblings.
    public const uint SidSfsv = 0x53465356;

    // CRI Middleware's ADX audio codec IOP driver (CRI_ADXI.IRX) — extracted from the disc
    // and disassembled; sid read directly out of its .data section (a runtime variable, not
    // a compile-time constant, unlike Midway's own SNDF/SFSV services) at the call site for
    // sceSifRegisterRpc (real vaddr 0x8AC). See the echo-handling comment in HandleCall for
    // why this service's fno!=2/3 calls are special-cased there rather than in Dispatch.
    public const uint SidCriAdx = 0x90000200;

    // Midway/Surreal's raw SPU2 register RPC driver (SDRDRV.IRX) — extracted and
    // disassembled; sid confirmed via its literal sceSifRegisterRpc call args (real vaddr
    // 0x5BC: lui a1,0x8000 / ori a1,a1,0x701). Its callback (real vaddr 0x628) dispatches on
    // (fno & 0xFFF0) across ~17 groups matching a 24-voice, 0x10-byte-stride register block
    // (observed calls: fno=0x8000/0x8010 = voice 0/1 setup) — a full raw-register pass-
    // through we haven't individually mapped. Same toolchain as SNDF_Driver ("MW MIPS C
    // Compiler" per both modules' .comment sections) — 0 for success is that toolchain's
    // convention here too, confirmed independently for SNDF's fno=0x1300 handler.
    public const uint SidSdReg = 0x80000701;
    // EE iopheap RPC fnos (ps2sdk ee/kernel/src/iopheap.c + iopheap-common.h).
    // IOP-side backend is SYSMEM AllocSysMemory/FreeSysMemory (sysmem.h / SYSMEM.bin
    // export table sysmem v1.1). Not the same as SIFCMD CID 0x80000003 (RESET_CMD).
    private const uint SysmemAlloc = 1; // SifAllocIopHeap(size) -> addr (NULL/0 on fail)
    private const uint SysmemFree = 2;  // SifFreeIopHeap(addr) -> 0 ok / -1 fail
    private const uint SysmemLoad = 3;  // SifLoadIopHeap(path, addr) -> result
    /// <summary>Real AllocSysMemory page quanta: <c>(size + 255) &amp; ~255</c> (see ps2sdk sysmem.c).</summary>
    private const uint SysmemPageSize = 256;

    // Real ps2sdk IOP module loader RPC service (ee/kernel/src/loadfile.c: SifLoadFileInit
    // binds sid=0x80000006 unconditionally; verified against the real ps2sdk source,
    // github.com/ps2dev/ps2sdk). This is the service SifLoadModule/SifLoadModuleBuffer/
    // SifExecModuleBuffer all ultimately call through -- the real, general mechanism for "load
    // and start an IRX module", as opposed to the disc-file preload path
    // (Ps2System.PreloadIopModulesFromDisc) which only covers files already sitting under IOP/
    // on the mounted ISO. Games that bundle their own IRX modules inside EE-side data (loaded via
    // SifExecModuleBuffer, which DMAs the bytes to an IOP heap allocation via SidSysmem/
    // SysmemAlloc first, then calls this service's LF_F_MOD_BUF_LOAD) were completely unhandled --
    // this sid fell through to the generic "unknown service" fallback, which returns a bare `1`
    // and never populates the real 8-byte { result, modres } reply struct real callers read.
    // Function numbers and arg struct layout (common/include/loadfile-common.h) confirmed
    // directly against real ps2sdk source + BIOS LOADFILE.IRX Ghidra table at DAT_00001bc8
    // (tools/bios-decomp/LOADFILE_ALL.txt): fno0 MOD_LOAD, 1 ELF_LOAD, 2 SET_ADDR, 3 GET_ADDR,
    // 4 MG_MOD_LOAD, 5 MG_ELF_LOAD. Later fnos (6+) are XLOADFILE / modern extensions.
    public const uint SidLoadFile = 0x80000006;
    private const uint LfModLoad = 0;              // LF_F_MOD_LOAD — _lf_module_load_arg
    private const uint LfElfLoad = 1;              // LF_F_ELF_LOAD — _lf_elf_load_arg → {epc,gp}
    private const uint LfSetAddr = 2;              // LF_F_SET_ADDR — _lf_iop_val_arg
    private const uint LfGetAddr = 3;              // LF_F_GET_ADDR — _lf_iop_val_arg
    private const uint LfMgModLoad = 4;            // LF_F_MG_MOD_LOAD (encrypted path load)
    private const uint LfMgElfLoad = 5;            // LF_F_MG_ELF_LOAD
    private const uint LfModBufLoad = 6;           // LF_F_MOD_BUF_LOAD — _lf_module_buffer_load_arg
    private const uint LfModStop = 7;              // LF_F_MOD_STOP
    private const uint LfModUnload = 8;            // LF_F_MOD_UNLOAD
    private const uint LfSearchModByName = 9;      // LF_F_SEARCH_MOD_BY_NAME
    private const uint LfSearchModByAddress = 10;  // LF_F_SEARCH_MOD_BY_ADDRESS
    private const uint LfGetVersion = 0xFF;        // LF_F_GET_VERSION
    private const int LfPathMax = 252;
    private const int LfArgMax = 252;
    // ps2lib_err.h LF_* (negative of E_LF_* / E_IOP_*) — match BIOS LOADFILE reply codes
    // (decomp FUN_00000150 / FUN_00000240 / FUN_000010dc: 0xffffff37 / 0xffffff35 / 0xffffff34 / 0xfffffe70).
    public const int LfErrNotIrx = -201;       // E_LF_NOT_IRX
    public const int LfErrFileNotFound = -203; // E_LF_FILE_NOT_FOUND
    public const int LfErrFileIo = -204;       // E_LF_FILE_IO_ERROR
    public const int LfErrNoMemory = -400;     // E_IOP_NO_MEMORY
    // Real IOP RAM module bytes are self-describing (ELF/IRX section headers carry their own
    // sizes) -- copying a generous upper bound and letting IrxLoader.Load parse only what it
    // actually needs avoids having to duplicate ELF-header size computation here. Real driver
    // IRX modules on this title's disc top out around 100KB (THREADMAN, the largest BIOS-
    // resident kernel module, is 36KB -- see RomdirExtractor's Phase 2 findings); 512KB comfortably
    // covers any real game-bundled module without reading past the 2MB IOP RAM window.
    private const int LfModuleCopyCap = 0x80000;

    /// <summary>Count of LOADFILE RPC calls handled (smoke / diagnostics).</summary>
    public ulong LoadFileOps { get; private set; }

    /// <summary>
    /// When true, PADMAN GetModVer returns major=4 (0x0400) for MK:DA / XPADMAN gates.
    /// Default false → major=3 (0x0300) which Shaolin Monks (SLUS_210.87) needs for a live
    /// post-reboot spine (see PadRpcCmdGetModVer). Set from a title quirk if required.
    /// </summary>
    public bool PadModVerMajor4 { get; set; }

    /// <summary>
    /// When true, LOADFILE GetVersion returns the IOPRP/DNAS ASCII tag after reboot
    /// (DA/BO2/B3 gates). Default false keeps classic 0x00020000 for Shaolin Monks spine.
    /// </summary>
    public bool PreferIopRpGetVersion { get; set; }

    /// <summary>
    /// When true, FILEIO stays on SN ProDG / Midway layouts (eeReply* mirror, open returns fd,
    /// immediate read completion). Suppresses Play! FILEIO-2200 arming even when IOPRP≥3000
    /// digits match SotC. Midway (Deception/DA/Arm) ships IOPRP300 + SN FILEIO — false 2200
    /// arming left GAMER.OVL as open/lseek/close with only a 3-byte magic probe (no full 384 B
    /// read), so the MWo3 stub never settled and member <c>.ssf</c> opens never started.
    /// </summary>
    public bool PreferSnFileIo { get; set; }

    /// <summary>
    /// Last IOPRP/DNAS image version tag derived from <c>SifIopReset</c> arg
    /// (e.g. <c>"2430"</c> for <c>IOPRP243.IMG</c>). Empty until a RESET_CMD with an
    /// image name completes, or a title calls <see cref="SetIopRpVersionAscii"/> when
    /// HLE never captured a UDNL arg (GoW empty <c>SifIopReset</c> left GetVersion at
    /// classic 0x00020000 while EE still expected <c>"3000"</c>). Used by
    /// <c>LF_F_GET_VERSION</c> so SN ProDG / Midway LOADFILE clients that strcmp the
    /// 4-byte reply against the expected IOPRP digits advance past the post-reboot gate.
    /// </summary>
    private string _lastIopRpVersionAscii = "";

    /// <summary>Current IOPRP/DNAS GetVersion ASCII tag (may be empty).</summary>
    public string LastIopRpVersionAscii => _lastIopRpVersionAscii;

    /// <summary>
    /// Successful BO2 pack-resident FILEIO/IOPFILE opens (KAIN.IMP / .ETP / ASSETS/…).
    /// Honest signal that post-RKV entity I/O started — prefer this over faked CODE/MAINMENU
    /// sector notes when gating title assists or scoreboard post-asset heuristics.
    /// </summary>
    public int Bo2PackResidentOpens { get; private set; }

    /// <summary>
    /// Set the LOADFILE/FILEIO GetVersion IOPRP ASCII tag without a full reboot surface clear.
    /// Accepts a 4-char tag (<c>"3000"</c>) or a UDNL/RESET arg containing <c>IOPRPxxx</c>/<c>DNASxxx</c>.
    /// Prefer this over <see cref="OnIopReboot"/> when only the version cell is missing.
    /// </summary>
    public void SetIopRpVersionAscii(string fourOrImgArg)
    {
        if (string.IsNullOrEmpty(fourOrImgArg)) return;
        // Bare 3–4 digit tag (e.g. "3000", "2800", "2340").
        if (fourOrImgArg.Length is >= 3 and <= 4)
        {
            bool allDigits = true;
            for (int i = 0; i < fourOrImgArg.Length; i++)
            {
                char c = fourOrImgArg[i];
                if (c is < '0' or > '9') { allDigits = false; break; }
            }
            if (allDigits)
            {
                string digits = fourOrImgArg;
                while (digits.Length < 4) digits += "0";
                _lastIopRpVersionAscii = digits[..4];
                return;
            }
        }
        string ver = ExtractIopRpVersionAscii(fourOrImgArg);
        if (ver.Length > 0)
            _lastIopRpVersionAscii = ver;
    }

    // IOP heap window for SidSysmem HLE. Sits above IopModuleHost IRX placement
    // (IrxLoader.DefaultLoadBase … ~0x180000) and below bind-scratch (0x1F0000).
    // Real SYSMEM manages nearly all free IOP RAM via 256-byte page freelists; this is a
    // contract-level allocator (first-fit free list + bump) that matches EE iopheap RPC
    // shapes without executing the R3000 SYSMEM.IRX.
    private const uint IopHeapBase = 0x180000;
    private const uint IopHeapLimit = 0x1F0000; // == ScratchBase; keep numeric to avoid forward-ref noise
    private uint _iopHeapNext = IopHeapBase;
    /// <summary>Live allocations: IOP physical base → page-aligned size.</summary>
    private readonly Dictionary<uint, uint> _iopHeapLive = new();
    /// <summary>Freed holes awaiting reuse (first-fit). Coalesced on free.</summary>
    private readonly List<(uint Phys, uint Size)> _iopHeapHoles = new();

    // CD_SCMD function numbers — ground-truthed against the real decompiled CDVDFSV.IRX SCMD
    // dispatcher (Ghidra FUN_000041b8, tools/bios-decomp/CDVDFSV_ALL.txt); see HandleCdScmd for
    // the rest of the 25 real case numbers and their per-case ground truth.
    private const uint ScmdReadClock = 0x01;
    private const uint ScmdWriteClock = 0x02;
    private const uint ScmdGetDiskType = 0x03;
    private const uint ScmdGetError = 0x04;
    private const uint ScmdTrayReq = 0x05;
    private const uint ScmdStatus = 0x0C;
    private const uint ScmdBreak = 0x16;

    // CD_NCMD function numbers — ps2sdk ee/rpc/cdvd/src/ncmd.c enum CD_NCMD_CMDS matches
    // decompiled FUN_00003f3c cases 1–0xe exactly (0xf = READCHAIN is XCDVDFSV-only).
    private const uint NcmdRead = 0x01;
    private const uint NcmdCddaRead = 0x02;
    private const uint NcmdDvdRead = 0x03;
    private const uint NcmdGetToc = 0x04;
    private const uint NcmdSeek = 0x05;
    private const uint NcmdStandby = 0x06;
    private const uint NcmdStop = 0x07;
    private const uint NcmdPause = 0x08;
    private const uint NcmdStream = 0x09;
    private const uint NcmdCddaStream = 0x0A;
    private const uint NcmdReadKey = 0x0B;
    private const uint NcmdApplyNCmd = 0x0C;
    private const uint NcmdReadIopMem = 0x0D;
    private const uint NcmdDiskReady = 0x0E; // was wrongly 0x0F (= READCHAIN X-only)
    private const uint NcmdReadChain = 0x0F;

    // FILEIO function numbers (ps2sdk common/include/fileio-common.h)
    private const uint FioOpen = 0;
    private const uint FioClose = 1;
    private const uint FioRead = 2;
    private const uint FioWrite = 3;
    private const uint FioLseek = 4;
    private const uint FioIoctl = 5;
    private const uint FioRemove = 6;
    private const uint FioMkdir = 7;
    private const uint FioRmdir = 8;
    private const uint FioDopen = 9;
    private const uint FioDclose = 10;
    private const uint FioDread = 11;
    private const uint FioGetstat = 12;
    private const uint FioChstat = 13;
    private const uint FioFormat = 14;
    private const uint FioAddDrv = 15; // FIO_F_ADDDRV — EE rarely uses; IOP AddDrv is primary
    private const uint FioDelDrv = 16; // FIO_F_DELDRV
    /// <summary>Some retail clients probe FILEIO with fno=0xFF like LOADFILE GetVersion.</summary>
    private const uint FioGetVersion = 0xFF;

    // Reserved scratch region: top 64KB of the 2MB IOP RAM, used only for the
    // opaque cd->buf/cd->cbuf handles we hand back at bind time.
    private const uint ScratchBase = 0x1F0000;
    private const uint ScratchSlotSize = 0x400;
    private const int ScratchSlots = 64;

    private readonly Dictionary<uint, uint> _cdToSid = new();    // cd struct EE addr -> bound sid
    private readonly Dictionary<uint, uint> _cdToArgBuf = new(); // cd struct EE addr -> absolute arg-buffer addr
    /// <summary>
    /// FILEIO SN-wrapper: DMA send is <c>{seq, eeArgs*, …}</c>; real <c>_fio_*_arg</c> lives in EE
    /// stack. By the time async CALL drains, stack may be zeroed. Snapshot eeArgs at EE→IOP DMA.
    /// Key = IOP argBuf (EE-mapped), value = 16B payload from *eeArgs at DMA time.
    /// </summary>
    private readonly Dictionary<uint, byte[]> _fioEeArgSnap = new();
    /// <summary>Last successful FILEIO open fd (SN wrapper omits fd on lseek/write/read).</summary>
    private int _fioLastFd = -1;
    /// <summary>
    /// FILEIO module ≥2200 (IOPRP2.2+/Play! <c>CFileIoHandler2200</c>): EE result buffer
    /// pointers registered by fno=255 Init. Replies are written here and the command
    /// <c>semaphoreId</c> is signaled (Play sends SIFCMD <c>0x80000011</c>; we collapse to
    /// <see cref="KernelState.ISignalSema"/> + filled reply).
    /// </summary>
    private uint _fio2200ResultPtr0;
    private uint _fio2200ResultPtr1;
    /// <summary>True after a 2200-shaped Init or Getstat/Open packet was observed.</summary>
    private bool _fio2200Armed;
    /// <summary>
    /// Play! FileIoHandler2200 delays READ replies by one frame so SotC can reschedule EE
    /// threads. Hold the filled READREPLY until <see cref="ProcessPendingFileIoReplies"/>.
    /// </summary>
    private bool _fio2200ReadPending;
    private uint _fio2200ReadSema;
    private uint _fio2200ReadResult;
    private uint _fio2200ReadCmdResultPtr;
    private uint _fio2200ReadCmdResultSize;
    private int _nextSlot;

    /// <summary>
    /// Active CRI DTX channels created via fno=2. Real CRI_ADXI.IRX owns matching IOP-side
    /// state and, after each EE→IOP SifSetDma of channel work, advances a completion counter
    /// in the EE work buffer so the EE DTX poll loop can clear its busy flag.
    /// Without that write-back the EE hard-spins with busy=1 forever (live-traced on Shaolin
    /// Monks: slot+1 stays 1, progress==counter, clear-path condition progress &lt; counter
    /// never holds). Tracked here so PerformSifSetDma can HLE the missing IOP completion.
    /// </summary>
    private readonly List<DtxChannel> _dtxChannels = new();

    private struct DtxChannel
    {
        public uint Handle;
        public uint Eewk;   // EE-side work buffer (may have 0x20000000 uncached bit)
        public uint IopWk;  // IOP RAM buffer the EE DMAs into
        public uint WkLen;
        /// <summary>Last counter value we wrote; used to avoid re-completing a still-pending unit.</summary>
        public uint LastCompleted;
    }

    public ulong Binds { get; private set; }
    public ulong Calls { get; private set; }
    public ulong RdataOps { get; private set; }
    public ulong FileIoOps { get; private set; }
    /// <summary>Count of SYSMEM / iopheap (sid=0x80000003) RPC calls handled.</summary>
    public ulong SysmemOps { get; private set; }
    public ulong UnknownServiceCalls { get; private set; }
    public ulong UnknownBindSids { get; private set; }
    private readonly List<uint> _unknownSidsSeen = new();
    public IReadOnlyList<uint> UnknownSidsSeen => _unknownSidsSeen;

    /// <summary>IOP physical bump watermark for SidSysmem (diagnostics / smokes).</summary>
    public uint IopHeapBump => _iopHeapNext;
    /// <summary>Number of live SifAllocIopHeap blocks (diagnostics / smokes).</summary>
    public int IopHeapLiveCount => _iopHeapLive.Count;

    public void Reset()
    {
        _iopFileFds.Clear();
        _iopFileStreamToFd.Clear();
        _iopFileStreamSize.Clear();
        _iopFileStreamPos.Clear();
        _iopFileAcquires = 0;
        _goeArchiveMounted = false;
        _bo2CodeBg2Warmed = false;
        _bo2PackIndexBuilt = false;
        _bo2PackMembers.Clear();
        _bo2PackBytes.Clear();
        Bo2PackResidentOpens = 0;
        _goeArchiveFd = -1;
        _goeArchiveSize = 0;
        _goeArchiveDiscByteOffset = 0;
        _rkvToc.Clear();
        _rkvTocCount = 0;
        _mkdaPakMounted = false;
        _mkdaPakFd = -1;
        _mkdaPakDiscByteOffset = 0;
        _mkdaPakSize = 0;
        _mkdaPakTocCount = 0;
        _mkdaPakToc.Clear();
        _cdToSid.Clear();
        _cdToArgBuf.Clear();
        _fioEeArgSnap.Clear();
        _fioLastFd = -1;
        _fio2200ResultPtr0 = 0;
        _fio2200ResultPtr1 = 0;
        _fio2200Armed = false;
        _fio2200ReadPending = false;
        _pendingEndFuncs.Clear();
        _dtxChannels.Clear();
        _mwFileHandles.Clear();
        _mwFileNextHandle = 1;
        _mwFileInited = false;
        _nextSlot = 0;
        ResetIopHeap();
        Binds = Calls = RdataOps = FileIoOps = SysmemOps = UnknownServiceCalls = UnknownBindSids = 0;
        _unknownSidsSeen.Clear();
        _padAreas.Clear();
        _padAreasGhost.Clear();
        _padFrame = 0;
        _lastIopRpVersionAscii = "";
        ResetGtfsState();
    }

    /// <summary>
    /// Post-<c>SifIopReset</c> surface: real PADMAN dies with the IOP image, so open-port
    /// bookkeeping must drop. EE clients re-OPEN after rebind; leaving stale keys made
    /// rom0-style OPEN return 0 ("already open") across REBOOT gen≥2 (MK IOPRP300 reload).
    /// Client bind maps are kept — EE still rebinds, and wiping them mid-flight loses argBufs.
    /// </summary>
    /// <remarks>
    /// Live MK (SLUS_210.87) and other IOPRP-reload titles often keep EE-side pad buffer
    /// pointers and never re-OPEN after gen≥2 — padGetState then polls a frozen buffer and
    /// menu accept never sees CROSS/START. Snapshot open areas into
    /// <see cref="_padAreasGhost"/> so <see cref="TickPadDma"/> can keep refreshing those
    /// DMA surfaces until a real OPEN arrives (active map is still cleared so re-OPEN works).
    /// </remarks>
    /// <param name="rebootArg">
    /// Optional RESET_CMD arg string (e.g. <c>rom0:UDNL cdrom0:\MODULE\IOPRP243.IMG;1</c>).
    /// When present, updates the LOADFILE GetVersion ASCII tag so SN/Midway clients that
    /// compare the 4-byte reply against the IOPRP digits (MK:DA "2430", BO2 "2340", B3 "2800")
    /// see a real UDNL-style handoff instead of the bare LOADFILE 0x00020000 placeholder.
    /// </param>
    public void OnIopReboot(string? rebootArg = null)
    {
        // Snapshot for ghost DMA refresh; clear active map so re-OPEN can succeed.
        if (_padAreas.Count > 0)
        {
            _padAreasGhost.Clear();
            foreach (var kv in _padAreas)
                _padAreasGhost[kv.Key] = kv.Value;
        }
        _padAreas.Clear();
        // FILEIO-2200 result buffers die with the IOP image; EE re-Inits after rebind.
        _fio2200ResultPtr0 = 0;
        _fio2200ResultPtr1 = 0;
        _fio2200Armed = false;
        _fio2200ReadPending = false;
        if (!string.IsNullOrEmpty(rebootArg))
        {
            string ver = ExtractIopRpVersionAscii(rebootArg);
            if (ver.Length > 0)
                _lastIopRpVersionAscii = ver;
        }
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_REBOOT") == "1"
            || Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[RPC] OnIopReboot: cleared pad open areas; ghost={_padAreasGhost.Count} " +
                $"ioprpVer=\"{_lastIopRpVersionAscii}\" arg=\"{rebootArg}\"");
    }

    /// <summary>
    /// Derive the 4-char IOPRP version tag SN/Midway LOADFILE clients compare after
    /// <c>LF_F_GET_VERSION</c>. <c>IOPRP243.IMG</c> → <c>"2430"</c>, <c>IOPRP234</c> →
    /// <c>"2340"</c>, <c>DNAS280.IMG</c> / <c>IOPRP280</c> → <c>"2800"</c>. Empty if no match.
    /// </summary>
    public static string ExtractIopRpVersionAscii(string rebootArg)
    {
        if (string.IsNullOrEmpty(rebootArg)) return "";
        // Prefer IOPRPxxx; also accept DNASxxx (Burnout 3 DNAS280 path).
        int idx = rebootArg.IndexOf("IOPRP", StringComparison.OrdinalIgnoreCase);
        int digitStart;
        if (idx >= 0)
            digitStart = idx + 5;
        else
        {
            idx = rebootArg.IndexOf("DNAS", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";
            digitStart = idx + 4;
        }
        var digits = new System.Text.StringBuilder(4);
        for (int i = digitStart; i < rebootArg.Length && digits.Length < 4; i++)
        {
            char c = rebootArg[i];
            if (c is >= '0' and <= '9') digits.Append(c);
            else break;
        }
        if (digits.Length == 0) return "";
        // 3-digit image codes (243/234/280) become 4-char tags with trailing '0'.
        while (digits.Length < 4) digits.Append('0');
        return digits.ToString(0, 4);
    }

    /// <summary>Pack 4 ASCII chars little-endian into a LOADFILE GetVersion reply dword.</summary>
    public static int PackAsciiVersion(string four)
    {
        if (string.IsNullOrEmpty(four) || four.Length < 4) return 0x00020000;
        return unchecked((int)(
            (uint)(byte)four[0] |
            ((uint)(byte)four[1] << 8) |
            ((uint)(byte)four[2] << 16) |
            ((uint)(byte)four[3] << 24)));
    }

    private void ResetIopHeap()
    {
        _iopHeapNext = IopHeapBase;
        _iopHeapLive.Clear();
        _iopHeapHoles.Clear();
    }

    /// <summary>In-flight bind state for SaveState.cs — _cdToSid/_cdToArgBuf track which
    /// service each EE-side "cd" struct is bound to and which scratch slot answers its calls;
    /// without this a save/load between a real sceSifBindRpc and its first sceSifCallRpc would
    /// resume with the binding forgotten, and the next call would look unbound.</summary>
    public void WriteState(BinaryWriter w)
    {
        w.Write(_cdToSid.Count);
        foreach (var kv in _cdToSid) { w.Write(kv.Key); w.Write(kv.Value); }
        w.Write(_cdToArgBuf.Count);
        foreach (var kv in _cdToArgBuf) { w.Write(kv.Key); w.Write(kv.Value); }
        w.Write(_nextSlot);
        w.Write(_iopHeapNext);
        w.Write(Binds); w.Write(Calls); w.Write(UnknownServiceCalls); w.Write(UnknownBindSids);
        w.Write(_unknownSidsSeen.Count);
        foreach (var sid in _unknownSidsSeen) w.Write(sid);
        // SYSMEM freelist (appended so older readers that stop after unknown-sids still parse
        // the prefix; new writers always emit these fields).
        w.Write(SysmemOps);
        w.Write(_iopHeapLive.Count);
        foreach (var kv in _iopHeapLive) { w.Write(kv.Key); w.Write(kv.Value); }
        w.Write(_iopHeapHoles.Count);
        foreach (var h in _iopHeapHoles) { w.Write(h.Phys); w.Write(h.Size); }
    }

    public void ReadState(BinaryReader r)
    {
        _cdToSid.Clear();
        int n1 = r.ReadInt32();
        for (int i = 0; i < n1; i++) { uint k = r.ReadUInt32(); uint v = r.ReadUInt32(); _cdToSid[k] = v; }
        _cdToArgBuf.Clear();
        _fioEeArgSnap.Clear();
        int n2 = r.ReadInt32();
        for (int i = 0; i < n2; i++) { uint k = r.ReadUInt32(); uint v = r.ReadUInt32(); _cdToArgBuf[k] = v; }
        _nextSlot = r.ReadInt32();
        _iopHeapNext = r.ReadUInt32();
        Binds = r.ReadUInt64(); Calls = r.ReadUInt64(); UnknownServiceCalls = r.ReadUInt64(); UnknownBindSids = r.ReadUInt64();
        _unknownSidsSeen.Clear();
        int n3 = r.ReadInt32();
        for (int i = 0; i < n3; i++) _unknownSidsSeen.Add(r.ReadUInt32());
        _iopHeapLive.Clear();
        _iopHeapHoles.Clear();
        SysmemOps = 0;
        // Optional SYSMEM freelist tail (absent in pre-SYSMEM-deepen snapshots).
        if (r.BaseStream.Position < r.BaseStream.Length)
        {
            SysmemOps = r.ReadUInt64();
            int nl = r.ReadInt32();
            for (int i = 0; i < nl; i++) { uint k = r.ReadUInt32(); uint v = r.ReadUInt32(); _iopHeapLive[k] = v; }
            int nh = r.ReadInt32();
            for (int i = 0; i < nh; i++) _iopHeapHoles.Add((r.ReadUInt32(), r.ReadUInt32()));
        }
    }

    /// <summary>Recognizes and handles a real RPC bind/call/rdata packet. Returns false for
    /// anything else (caller falls back to existing system-cid / heuristic handling).</summary>
    public bool TryHandle(SystemMemory mem, KernelState kernel, Cdvd cdvd, PadInput pad, IopModuleHost iopModules, uint pktAddr)
    {
        uint cid = mem.Read32(pktAddr + 8);
        switch (cid)
        {
            case CidRpcBind: HandleBind(mem, kernel, pktAddr); return true;
            case CidRpcCall: HandleCall(mem, kernel, cdvd, pad, iopModules, pktAddr); return true;
            case CidRpcRdata: HandleRdata(mem, kernel, pktAddr); return true;
            default: return false;
        }
    }

    /// <summary>Cheap peek (no state mutation) so the caller can decide whether to queue
    /// this packet for real, IOP-tick-scheduled handling without processing it early.</summary>
    public static bool IsRealRpcPacket(SystemMemory mem, uint pktAddr)
    {
        uint cid = mem.Read32(pktAddr + 8);
        return cid == CidRpcBind || cid == CidRpcCall || cid == CidRpcRdata;
    }

    private uint AssignSlot()
    {
        uint off = ScratchBase + (uint)(_nextSlot % ScratchSlots) * ScratchSlotSize;
        _nextSlot++;
        return SystemMemory.IOP_RAM_BASE + off;
    }

    /// <summary>
    /// Called after an EE→IOP SifSetDma completes. If the destination lands in a tracked
    /// CRI DTX IOP workspace, advance that channel's EE completion counter so the EE-side
    /// DTX poll (progress &lt; counter) can clear its busy flag. Real CRI_ADXI does the
    /// equivalent from its IOP DTX thread after consuming the DMA'd payload.
    ///
    /// Only advances when the counter still equals what the EE just issued (counter has not
    /// already been pushed ahead of progress). Unconditional +1 on every matching DMA caused
    /// an infinite issue/complete thrash (sifBytes climbing unbounded, boot never left DTX).
    /// </summary>
    /// <summary>
    /// After every EE→IOP SifSetDma: if dest is a bound RPC argBuf and the payload looks like the
    /// SN FILEIO wrapper (<c>seq + eeArgs*</c>), snapshot <c>*eeArgs</c> (16B) so later async CALL
    /// can decode lseek/read/write even if the EE stack was cleared.
    /// </summary>
    public void NotifyRpcArgDma(SystemMemory mem, uint iopDest, uint size)
    {
        if (size < 8 || _cdToArgBuf.Count == 0) return;
        uint destMap = iopDest;
        if (destMap < SystemMemory.IOP_RAM_SIZE)
            destMap = SystemMemory.IOP_RAM_BASE + destMap;
        // Match any known argBuf (exact or within scratch slot).
        uint matched = 0;
        foreach (var kv in _cdToArgBuf)
        {
            uint ab = kv.Value;
            if (destMap == ab || (destMap >= ab && destMap < ab + ScratchSlotSize))
            {
                matched = ab;
                break;
            }
        }
        if (matched == 0) return;

        uint w1 = mem.Read32(matched + 4);
        if (!IsEeRamPointer(w1)) return;
        uint ee = w1 & 0x1FFFFFFFu;
        // Snapshot 16 bytes of the pointed EE struct (covers open-mode head / lseek / read).
        var snap = new byte[16];
        for (int i = 0; i < 16; i++)
            snap[i] = mem.Read8(ee + (uint)i);
        _fioEeArgSnap[matched] = snap;
        // Also expand into the IOP arg buffer after a fixed header so decoders can read inline:
        // keep words 0..4 as wrapper, write snap at +0x20 if slot is large enough and empty of path.
        // (Open already has path @+0x14; don't clobber. Only materialize when send was small.)
        if (size <= 64)
        {
            for (int i = 0; i < 16; i++)
                mem.Write8(matched + 0x20 + (uint)i, snap[i]);
        }
    }

    public void NotifyDtxEeToIopDma(SystemMemory mem, uint iopDest, uint size)
    {
        NotifyRpcArgDma(mem, iopDest, size);
        if (_dtxChannels.Count == 0 || size == 0) return;
        // Dest may be IOP-physical (0x000000-0x1FFFFF) or EE-visible IOP window (0x1C000000+).
        uint destPhys = iopDest & 0x1FFFFFu;
        for (int i = 0; i < _dtxChannels.Count; i++)
        {
            var ch = _dtxChannels[i];
            uint wkPhys = ch.IopWk & 0x1FFFFFu;
            uint wkLen = ch.WkLen != 0 ? ch.WkLen : 0x1000u;
            // Match if DMA dest overlaps the channel's IOP workspace (or its synthetic handle slot).
            uint handlePhys = ch.Handle & 0x1FFFFFu;
            bool hit = (destPhys >= wkPhys && destPhys < wkPhys + wkLen)
                       || (destPhys >= handlePhys && destPhys < handlePhys + ScratchSlotSize);
            if (!hit) continue;

            // EE DTX create (FUN_0041e988) points the live counter at
            //   uncached = (eewk + (wklen - 64)) | 0x20000000
            //   counter  = uncached + 0x3C
            // i.e. phys counter = eewk + wklen - 64 + 0x3C.
            uint eewk = ch.Eewk & 0x1FFFFFFFu;
            if (eewk == 0) continue;
            uint counterAddr = wkLen >= 64 ? eewk + (wkLen - 64) + 0x3Cu : eewk + 0x3Cu;
            uint completed = mem.Read32(counterAddr);
            // EE issue path: counter = progress; SifSetDma. Ack with counter = progress+1.
            // If we already pushed past `completed`, EE has not issued a new unit yet.
            if (ch.LastCompleted > completed)
                return;
            // Unbounded acks make the EE DTX issue path spin (sifBytes climb without bound)
            // because real IOP ring-buffer consumption / flow-control is not modelled. Budget
            // enough units for SJX_Init + early stream setup without infinite DMA thrash.
            const uint maxAutoComplete = 32;
            if (ch.LastCompleted >= maxAutoComplete)
                return;
            uint next = completed + 1;
            mem.Write32(counterAddr, next);
            ch.LastCompleted = next;
            _dtxChannels[i] = ch;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[RPC] DTX_COMPLETE eewk=0x{ch.Eewk:X8} counter=0x{counterAddr:X8} iopDest=0x{iopDest:X8} " +
                    $"completed {completed}->{next} size={size}");
            return;
        }
    }

    private void HandleBind(SystemMemory mem, KernelState kernel, uint pktAddr)
    {
        // SifRpcBindPkt_t (36B): +0 sifcmd(16) +16 rec_id +20 pkt_addr +24 rpc_id +28 cd(ptr) +32 sid
        uint cdPtr = mem.Read32(pktAddr + 28);
        uint sid = mem.Read32(pktAddr + 32);
        Binds++;
        if (cdPtr == 0)
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[RPC] HandleBind sid=0x{sid:X8} cdPtr=NULL (dropped, no reply) eePC=0x{SystemMemory.CurrentPcForWatch:X8}");
            return;
        }

        uint argBuf = AssignSlot();
        uint ctrlBuf = AssignSlot();
        _cdToSid[cdPtr] = sid;
        _cdToArgBuf[cdPtr] = argBuf;

        if (sid != SidCdScmd && sid != SidCdNcmd && sid != SidPad1 && sid != SidPad2
            && sid != SidPadOld1 && sid != SidPadOld2
            && sid != SidMcServ && sid != SidCdBase && sid != SidCdSearchFile && sid != SidCdDiskReady
            && sid != SidCdDiskReady2
            && sid != SidSysmem && sid != SidSndf && sid != SidSnProdg && sid != SidCriAdx && sid != SidSdReg
            && sid != SidLoadFile && sid != SidSfsv && sid != SidFileIo
            && sid != SidDbcMan && sid != Sid989Snd && sid != Sid989Snd2
            && sid != SidMsl && sid != SidMslMfl
            && sid != SidPl2303Usb // SotC binds after PL2303.IRX; soft-HLE, no unknown
            && !IsIopFileSid(sid)
            && sid != SidLgDev
            && !IsDbcManSibling(sid)
            && !IsBurnout3GtfsSid(sid)
            && !IsMwFileSid(sid))
        {
            UnknownBindSids++;
            _unknownSidsSeen.Add(sid);
        }

        // SifRpcClientData_t (40B): +8 hdr.sema_id, +20 buf, +24 cbuf, +36 server.
        // buf/cbuf/server are opaque handles the client only ever echoes back to us
        // in later Call packets — content just needs to round-trip, not mean anything.
        mem.Write32(cdPtr + 20, argBuf);
        mem.Write32(cdPtr + 24, ctrlBuf);
        mem.Write32(cdPtr + 36, sid);

        // BIOS SIFCMD.IRX FUN_00000c48 (Ghidra, tools/bios-decomp/SIFCMD_ALL.txt):
        // after filling server fields, IOP does SendCmd(SIF_CMD_RPC_END=0x80000008, reply, 0x40).
        // EE sifrpc.c's RPC_END handler then: SignalSema(cd->hdr.sema_id) + rpc_packet_free().
        // We collapse that IOP→EE DMA into the same two side-effects (docs/BIOS_DISSECTION.md §3).
        CompleteRpcEnd(mem, kernel, pktAddr, cdPtr);

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine($"[RPC] HandleBind sid=0x{sid:X8} cdPtr=0x{cdPtr:X8} semaId={mem.Read32(cdPtr + 8)} argBuf=0x{argBuf:X8} eePC=0x{SystemMemory.CurrentPcForWatch:X8}");
    }

    /// <summary>
    /// Pending EE <c>end_function(end_param)</c> callbacks from CALL completion.
    /// ps2sdk <c>_request_end</c> invokes these BEFORE SignalSema; 989snd and others
    /// poll a flag set only by end_function ("Sif says RPC isn't busy, but we still
    /// don't have returns from the IOP!" when skipped).
    /// </summary>
    private readonly Queue<(uint Func, uint Param)> _pendingEndFuncs = new();

    /// <summary>Drain one CALL end_function for EE invocation. Returns false if empty.</summary>
    public bool TryDequeueEndFunc(out uint func, out uint param)
    {
        if (_pendingEndFuncs.Count == 0) { func = 0; param = 0; return false; }
        (func, param) = _pendingEndFuncs.Dequeue();
        return true;
    }

    /// <summary>
    /// EE-side effects of IOP <c>SIF_CMD_RPC_END</c> (0x80000008).
    /// <para>
    /// Producers (BIOS SIFCMD.IRX, Ghidra): BIND <c>FUN_00000c48</c>, CALL completion
    /// <c>FUN_000013a4</c>, and RDATA <c>FUN_00000a68</c> all end in
    /// <c>SendCmd(0x80000008, reply, 0x40, …)</c>.
    /// Consumer (ps2sdk <c>ee/kernel/src/sifrpc.c</c> <c>_request_end</c>): for BIND, copy
    /// server/buf/cbuf into the client; for CALL run <c>end_function(end_param)</c>; always
    /// <c>iSignalSema(cd->hdr.sema_id)</c> if <c>sema_id &gt;= 0</c>;
    /// <c>rpc_packet_free(cd->hdr.pkt_addr)</c>; clear <c>cd->hdr.pkt_addr</c>.
    /// </para>
    /// We do not DMA a real IOP→EE packet; we apply the same EE state transitions so
    /// <c>WaitSema</c> + <c>sceSifCheckStatRpc</c> see a completed transfer.
    /// </summary>
    /// <param name="isCall">True for CALL/RDATA completion (run end_function); false for BIND.</param>
    private void CompleteRpcEnd(SystemMemory mem, KernelState kernel, uint pktAddr, uint cdPtr, bool isCall = false)
    {
        // Free the EE packet pool slot first (rpc_packet_free on the request packet).
        // PACKET_F_ALLOC = 0x01 in rec_id; rpc_id at +24. Also clear SIFCMD.cid so
        // sceSifCheckStatRpc sees a finished slot (not still BIND/CALL/RDATA).
        if (pktAddr != 0)
        {
            uint recId = mem.Read32(pktAddr + 16);
            mem.Write32(pktAddr + 16, recId & ~1u);
            mem.Write32(pktAddr + 24, 0);
            // SifCmdHeader_t.cid at +8 — BIOS reply stamps RPC_END then EE frees; we
            // collapse to "done" by writing RPC_END then clearing alloc.
            mem.Write32(pktAddr + 8, CidRpcEnd);
        }

        if (cdPtr == 0) return;

        // SifRpcClientData_t (ps2sdk sifrpc-common.h):
        //   hdr +0 pkt_addr, +4 rpc_id, +8 sema_id, +12 mode
        //   +16 command, +20 buf, +24 cbuf, +28 end_function, +32 end_param, +36 server
        // BIND path already wrote +20 buf, +24 cbuf, +36 server (HandleBind).
        mem.Write32(cdPtr + 0, 0); // hdr.pkt_addr = NULL after free

        // CALL: queue end_function before SignalSema (same order as _request_end).
        if (isCall)
        {
            uint endFunc = mem.Read32(cdPtr + 28);
            uint endParam = mem.Read32(cdPtr + 32);
            if (endFunc != 0)
            {
                // Do NOT unconditionally write *end_param=1. Simple done-flag
                // end_functions still plant 1 via TryHleSimpleEndFunction.
                // CDVDFSV-style treat end_param as transfer descriptor (Vexx CdRead).
                _pendingEndFuncs.Enqueue((endFunc, endParam));
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine($"[RPC] end_function=0x{endFunc:X8} end_param=0x{endParam:X8}");
            }
        }

        int semaId = unchecked((int)mem.Read32(cdPtr + 8));
        // ps2sdk: if (cd->hdr.sema_id >= 0) iSignalSema — NOWAIT paths leave -1.
        if (semaId >= 0)
            kernel.ISignalSema(semaId);
    }

    /// <summary>
    /// BIOS SIFCMD RDATA handler (<c>FUN_00000a68</c>): DMA size bytes between EE/IOP
    /// addresses from the request packet, then <c>SendCmd(RPC_END)</c>.
    /// EE uses this for <c>sceSifGetOtherData</c> / buffer pulls off IOP RAM.
    /// Packet layout (after 16B SifCmdHeader): +0x10 rec_id … +0x1c client,
    /// +0x20 src, +0x24 dest, +0x28 size (Ghidra FUN_00000a68 → FUN_00000524 args).
    /// </summary>
    private void HandleRdata(SystemMemory mem, KernelState kernel, uint pktAddr)
    {
        RdataOps++;
        uint src = mem.Read32(pktAddr + 0x20);
        uint dest = mem.Read32(pktAddr + 0x24);
        uint size = mem.Read32(pktAddr + 0x28);
        // Cap to avoid pathological packets; real IOP DMA is also size-limited per transfer.
        if (size > 0 && size <= 0x100000 && src != 0 && dest != 0)
            CopyMemoryWindow(mem, src, dest, size);

        // RDATA completion uses the client pointer at +0x1c when present (same as BIND/CALL).
        uint cdPtr = mem.Read32(pktAddr + 0x1c);
        CompleteRpcEnd(mem, kernel, pktAddr, cdPtr);

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine(
                $"[RPC] HandleRdata src=0x{src:X8} dest=0x{dest:X8} size={size} cd=0x{cdPtr:X8}");
    }

    /// <summary>
    /// RDATA DMA: IOP source → EE destination (sceSifGetOtherData direction).
    /// Src may be bare IOP physical (0–2MB); dest is EE RDRAM (must not remap low EE
    /// addresses into the IOP window — RDRAM and IOP both use 0–2MB physical ranges).
    /// </summary>
    private static void CopyMemoryWindow(SystemMemory mem, uint src, uint dest, uint size)
    {
        uint s = NormalizeIopAddr(src);
        uint d = dest & 0x1FFFFFFFu; // EE phys / uncached strip only
        for (uint i = 0; i < size; i++)
            mem.Write8(d + i, mem.Read8(s + i));
    }

    /// <summary>Map bare IOP physical or KSEG IOP pointer into the EE IOP RAM window.</summary>
    private static uint NormalizeIopAddr(uint addr)
    {
        uint p = addr & 0x1FFFFFFFu;
        // Already in EE-visible IOP window
        if (p >= SystemMemory.IOP_RAM_BASE && p < SystemMemory.IOP_RAM_BASE + (uint)SystemMemory.IOP_RAM_SIZE)
            return p;
        // Bare IOP physical (0x000000–0x1FFFFF) — RDATA src convention
        if (p < (uint)SystemMemory.IOP_RAM_SIZE)
            return SystemMemory.IOP_RAM_BASE + p;
        return p;
    }

    private void HandleCall(SystemMemory mem, KernelState kernel, Cdvd cdvd, PadInput pad, IopModuleHost iopModules, uint pktAddr)
    {
        // SifRpcCallPkt_t (56B): +0 sifcmd(16) +16 rec_id +20 pkt_addr +24 rpc_id +28 cd(ptr)
        //   +32 rpc_number +36 send_size +40 recvbuf(ptr) +44 recv_size +48 rmode +52 sd(ptr)
        uint cdPtr = mem.Read32(pktAddr + 28);
        uint rpcNumber = mem.Read32(pktAddr + 32);
        uint sendSize = mem.Read32(pktAddr + 36);
        uint recvBuf = mem.Read32(pktAddr + 40);
        uint recvSize = mem.Read32(pktAddr + 44);
        Calls++;

        uint sid = _cdToSid.TryGetValue(cdPtr, out var s) ? s : 0;
        uint argBuf = _cdToArgBuf.TryGetValue(cdPtr, out var a) ? a : 0;

        // CRI Middleware ADX (CRI_ADXI.IRX, sid=0x90000200).
        // Live-traced (2026-07-29) on Shaolin Monks SJX_Init:
        //   bind sid=0x90000200 → client 0x53DDF0
        //   call fno=2 send=16B (id,eewk,iopwk,wklen) recv=4B → DTX_Create
        // The EE wrapper FUN_0041e830 returns *(u32*)recvBuf as the DTX handle; 0 is fatal
        // ("E0100302: SJX_Init can't create DTX" / "DTX_Create: can't create DTX of server").
        // Previous HLE *echoed* the send buffer into recv — so fno=2 returned id (often 0)
        // and SJX_Init hard-spun forever at 0x426BA8. Real fno 2/3 create server-side state
        // and return a non-zero handle/status; we allocate a synthetic IOP-side handle.
        // Other fno values still echo (real callback returns the caller's own buffer).
        if (sid == SidCriAdx)
        {
            if (rpcNumber is 2 or 3)
            {
                // DTX create (2) / related setup (3): non-zero handle in first recv dword.
                // AssignSlot returns EE-mappable IOP RAM so later calls can round-trip it.
                // Send layout (live-traced FUN_0041e830): +0 id, +4 eewk, +8 iopwk, +12 wklen.
                uint handle = AssignSlot();
                uint eewk = argBuf != 0 && sendSize >= 8 ? mem.Read32(argBuf + 4) : 0;
                uint iopWk = argBuf != 0 && sendSize >= 12 ? mem.Read32(argBuf + 8) : 0;
                uint wkLen = argBuf != 0 && sendSize >= 16 ? mem.Read32(argBuf + 12) : 0;
                // Prefer our synthetic handle as the IOP workspace when the caller passed 0 /
                // garbage; otherwise keep the caller's iopwk so later SifSetDma dest matches.
                if (iopWk == 0) iopWk = handle;
                if (rpcNumber == 2 && eewk != 0)
                {
                    _dtxChannels.Add(new DtxChannel
                    {
                        Handle = handle,
                        Eewk = eewk,
                        IopWk = iopWk,
                        WkLen = wkLen != 0 ? wkLen : 0x880,
                        LastCompleted = 0
                    });
                }
                if (recvBuf != 0 && recvSize >= 4)
                    mem.Write32(recvBuf, handle);
                // Real driver also stores the result into the request buffer's first word.
                if (argBuf != 0 && sendSize >= 4)
                    mem.Write32(argBuf, handle);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine(
                        $"[RPC] HandleCall sid=0x{sid:X8} fno=0x{rpcNumber:X} DTX_CREATE handle=0x{handle:X8} " +
                        $"eewk=0x{eewk:X8} iopwk=0x{iopWk:X8} recvBuf=0x{recvBuf:X8} eePC=0x{SystemMemory.CurrentPcForWatch:X8}");
            }
            else if (recvBuf != 0 && argBuf != 0)
            {
                uint echoLen = Math.Min(sendSize, recvSize);
                for (uint i = 0; i < echoLen; i++)
                    mem.Write8(recvBuf + i, mem.Read8(argBuf + i));
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine($"[RPC] HandleCall sid=0x{sid:X8} fno=0x{rpcNumber:X} recvBuf=0x{recvBuf:X8} ECHO({echoLen}B) eePC=0x{SystemMemory.CurrentPcForWatch:X8}");
            }
            CompleteRpcEnd(mem, kernel, pktAddr, cdPtr, isCall: true);
            return;
        }

        // LOADFILE (sid=0x80000006) — real BIOS LOADFILE.IRX registers this sid at init
        // (Ghidra FUN_000000c8). Replies with a { result, modres } pair.
        if (sid == SidLoadFile)
        {
            HandleLoadFile(mem, iopModules, cdvd, rpcNumber, argBuf, recvBuf);
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[RPC] HandleCall sid=0x{sid:X8} fno=0x{rpcNumber:X} LOADFILE recvBuf=0x{recvBuf:X8} eePC=0x{SystemMemory.CurrentPcForWatch:X8}");
            CompleteRpcEnd(mem, kernel, pktAddr, cdPtr, isCall: true);
            return;
        }

        // CD_SCMD (sid=0x80000593) — real BIOS CDVDFSV.IRX SCMD dispatcher (Ghidra FUN_000041b8,
        // tools/bios-decomp/CDVDFSV_ALL.txt). Most of these 25 real function numbers reply with
        // a packed multi-word struct (result + real hardware output data), not a single int —
        // see HandleCdScmd's own doc comment for the exact per-case ground-truth.
        if (sid == SidCdScmd)
        {
            HandleCdScmd(mem, cdvd, rpcNumber, argBuf, recvBuf);
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[RPC] HandleCall sid=CD_SCMD fno=0x{rpcNumber:X} recvBuf=0x{recvBuf:X8} eePC=0x{SystemMemory.CurrentPcForWatch:X8}");
            CompleteRpcEnd(mem, kernel, pktAddr, cdPtr, isCall: true);
            return;
        }

        // CD_NCMD (sid=0x80000595) — FUN_00003f3c. Multi-word replies for some cases (GetToc,
        // ReadKey); single result word for Seek/Standby/Stop/Pause/DiskReady. Handled here so
        // payload words past result aren't clobbered by the generic "write result only" path.
        if (sid == SidCdNcmd)
        {
            HandleCdNcmd(mem, cdvd, rpcNumber, argBuf, recvBuf);
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[RPC] HandleCall sid=CD_NCMD fno=0x{rpcNumber:X} recvBuf=0x{recvBuf:X8} eePC=0x{SystemMemory.CurrentPcForWatch:X8}");
            CompleteRpcEnd(mem, kernel, pktAddr, cdPtr, isCall: true);
            return;
        }

        // CD SearchFile (sid=0x80000597) — FUN_000002f0 "search file name %s".
        if (sid == SidCdSearchFile)
        {
            int sf = HandleCdSearchFile(mem, cdvd, argBuf, recvBuf);
            if (recvBuf != 0) mem.Write32(recvBuf, unchecked((uint)sf));
            CompleteRpcEnd(mem, kernel, pktAddr, cdPtr, isCall: true);
            return;
        }

        // CD DiskReady wait service (sid=0x8000059a / 0x8000059C) — FUN_000032d8 / IOPRP2.8 0x30D8.
        // Both SIDs share the same IOP handler; 0x59C is the post-2.8 twin used by newer libcdvd.
        if (sid == SidCdDiskReady || sid == SidCdDiskReady2)
        {
            int dr = cdvd.DiskReady();
            if (recvBuf != 0) mem.Write32(recvBuf, unchecked((uint)dr));
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[RPC] HandleCall sid=CD_DISKREADY(0x{sid:X8}) fno=0x{rpcNumber:X} result={dr} recvBuf=0x{recvBuf:X8}");
            CompleteRpcEnd(mem, kernel, pktAddr, cdPtr, isCall: true);
            return;
        }

        // FILEIO (sid=0x80000001) — BIOS FILEIO.IRX / sceOpen family.
        // Also XFILEIO / IOPRP≥2200 EE client (same sid; Play! CFileIoHandler2200 layouts).
        if (sid == SidFileIo)
        {
            int fioResult = HandleFileIo(mem, kernel, iopModules, pad, cdvd, rpcNumber, argBuf, sendSize, recvBuf, recvSize);
            if (recvBuf != 0 && recvSize >= 4)
                mem.Write32(recvBuf, unchecked((uint)fioResult));
            // SN ProDG (Midway Deception, BO2): send+4 is eeReply* the EE reads after CallRpc
            // (sometimes distinct from packet recvBuf). Mirror the int result there.
            // Skip when FILEIO-2200 is armed — +4 is COMMANDHEADER.resultPtr, not SN eeReply*.
            if (!_fio2200Armed && LooksLikeSnFioWrapper(mem, argBuf, sendSize) && sendSize >= 8)
            {
                uint eeReply = mem.Read32(argBuf + 4) & 0x1FFFFFFFu;
                if (eeReply >= 0x100000 && eeReply + 4 <= SystemMemory.RDRAM_SIZE)
                    mem.Write32(eeReply, unchecked((uint)fioResult));
            }
            FileIoOps++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[RPC] HandleCall sid=FILEIO fno={rpcNumber} result={fioResult}");
            CompleteRpcEnd(mem, kernel, pktAddr, cdPtr, isCall: true);
            return;
        }

        // SYSMEM / iopheap (sid=0x80000003) — SifAllocIopHeap / Free / Load (ps2sdk iopheap.c).
        // Dedicated path so Load can read disc bytes via IopModuleHost.
        if (sid == SidSysmem)
        {
            int sm = HandleSysmem(mem, iopModules, rpcNumber, argBuf, recvBuf);
            if (recvBuf != 0 && recvSize >= 4)
                mem.Write32(recvBuf, unchecked((uint)sm));
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[RPC] HandleCall sid=SYSMEM fno={rpcNumber} result=0x{unchecked((uint)sm):X8}");
            CompleteRpcEnd(mem, kernel, pktAddr, cdPtr, isCall: true);
            return;
        }

        // 989snd (sid=0x00123456/7) — multi-word 0xFFFFFFFF completion sentinels in recv.
        // Must NOT fall through to the generic single-word result write (that clobbered +0
        // with 0 and triggered the "Sif says RPC isn't busy, but we still don't have
        // returns from the IOP!" DECI2 storm on God of War).
        if (sid == Sid989Snd || sid == Sid989Snd2)
        {
            int r989 = Handle989Snd(mem, rpcNumber, argBuf, recvBuf, recvSize);
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[RPC] HandleCall sid=989snd(0x{sid:X8}) fno=0x{rpcNumber:X} recvBuf=0x{recvBuf:X8} " +
                    $"recvSize={recvSize} result=0x{unchecked((uint)r989):X8}");
            CompleteRpcEnd(mem, kernel, pktAddr, cdPtr, isCall: true);
            return;
        }

        // IOPFILE / GOE_FSRV (Blood Omen 2 + Whiplash) — low SIDs including 0x31/0x40.
        // HandleIopFile writes the multi-word GOE reply (status/filesize/scefd/iStream)
        // into recvBuf — do NOT clobber with a single int afterwards.
        if (IsIopFileSid(sid))
        {
            int ir = HandleIopFile(mem, iopModules, cdvd, sid, rpcNumber, argBuf, sendSize, recvBuf, recvSize);
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[RPC] HandleCall sid=IOPFILE(0x{sid:X8}) fno=0x{rpcNumber:X} result={ir} " +
                    $"recvBuf=0x{recvBuf:X8} send={sendSize}");
            CompleteRpcEnd(mem, kernel, pktAddr, cdPtr, isCall: true);
            return;
        }

        // libdbc extension SIDs (siblings of dbcman 0x80001300) — bind-only on GoW so far,
        // but accept calls with the same version-shaped reply so pad path cannot wedge.
        if (IsDbcManSibling(sid))
        {
            int db = HandleDbcMan(mem, rpcNumber, argBuf, recvBuf);
            if (recvBuf != 0 && recvSize >= 4)
                mem.Write32(recvBuf, unchecked((uint)db));
            CompleteRpcEnd(mem, kernel, pktAddr, cdPtr, isCall: true);
            return;
        }

        // Logitech lgDevice (LGDEVW.IRX wheel / LGKBM) — Burnout 3 lgDeviceInit.
        // Live: bind sid=0x046D046D, call fno=12, expects *(recv+4)==0x010B1B00
        // (Version 1.11.027 Wheel). Wrong/zero version → assert sink at 0x443A90.
        if (sid == SidLgDev)
        {
            int lg = HandleLgDev(mem, rpcNumber, argBuf, sendSize, recvBuf, recvSize);
            if (recvBuf != 0 && recvSize >= 4 && mem.Read32(recvBuf) == 0 && lg != 0)
                mem.Write32(recvBuf, unchecked((uint)lg));
            CompleteRpcEnd(mem, kernel, pktAddr, cdPtr, isCall: true);
            return;
        }

        // Criterion GTFS / B3 aux after GTFSCDVD.IRX — real Iso TOC / STAGEHED open so
        // cdvd rises past IRX-only 425 and EE stream tables get non-zero sizes.
        if (IsBurnout3GtfsSid(sid))
        {
            int gr = HandleGtfs(mem, cdvd, iopModules, sid, rpcNumber, argBuf, sendSize, recvBuf, recvSize);
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[RPC] HandleCall sid=GTFS(0x{sid:X8}) fno=0x{rpcNumber:X} result={gr} " +
                    $"recvBuf=0x{recvBuf:X8} send={sendSize} arg=0x{argBuf:X8}");
            CompleteRpcEnd(mem, kernel, pktAddr, cdPtr, isCall: true);
            return;
        }

        // Midway MWFILEFR.IRX (MK: Deception / Deadly Alliance / shared Midway FS).
        // sids 0x000F0001 (main) + 0x000F0002 (aux fno 0xC8). Bridge to FILEIO/ISO.
        if (IsMwFileSid(sid))
        {
            int mr = HandleMwFile(mem, iopModules, cdvd, sid, rpcNumber, argBuf, sendSize, recvBuf, recvSize);
            if (recvBuf != 0 && recvSize >= 4)
                mem.Write32(recvBuf, unchecked((uint)mr));
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[RPC] HandleCall sid=MWFILE(0x{sid:X8}) fno=0x{rpcNumber:X} result={mr} " +
                    $"recvBuf=0x{recvBuf:X8} send={sendSize} arg=0x{argBuf:X8}");
            CompleteRpcEnd(mem, kernel, pktAddr, cdPtr, isCall: true);
            return;
        }

        // Midway MSL.IRX (0x00012345 sound) + MFL file link (0x00012347 open/read).
        // Soft DADA alone is not enough: DA queues cdrom0:\MKDA.PAK opens on the EE
        // request ring and completes them via MFL CallRpc. Bridge file fnos to FILEIO.
        if (IsMslFamilySid(sid))
        {
            int ms = HandleMsl(mem, iopModules, cdvd, sid, rpcNumber, argBuf, sendSize, recvBuf, recvSize);
            if (recvBuf != 0 && recvSize >= 4 && mem.Read32(recvBuf) == 0)
                mem.Write32(recvBuf, unchecked((uint)ms));
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[RPC] HandleCall sid=MSL(0x{sid:X8}) fno=0x{rpcNumber:X} result={ms} " +
                    $"recvBuf=0x{recvBuf:X8} send={sendSize} arg=0x{argBuf:X8}");
            CompleteRpcEnd(mem, kernel, pktAddr, cdPtr, isCall: true);
            return;
        }

        // PL2303 / USB serial (SotC binds 0x80000220 after PL2303.IRX) — soft success.
        if (sid == SidPl2303Usb)
        {
            if (recvBuf != 0 && recvSize >= 4)
                mem.Write32(recvBuf, 0);
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[RPC] HandleCall sid=PL2303(0x{sid:X8}) fno=0x{rpcNumber:X} result=0");
            CompleteRpcEnd(mem, kernel, pktAddr, cdPtr, isCall: true);
            return;
        }

        int result = Dispatch(mem, cdvd, pad, iopModules, sid, rpcNumber, argBuf, recvBuf);

        if (recvBuf != 0)
            mem.Write32(recvBuf, unchecked((uint)result));
        // sceCdInit recv is a multi-word CdInitPkt — version fields past result matter for
        // dual-layer / mechacon paths (libcdvd initVersionCdvdman/fsv).
        if (sid == SidCdBase)
            WriteCdInitPacket(mem, recvBuf, recvSize);

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine($"[RPC] HandleCall sid=0x{sid:X8} fno=0x{rpcNumber:X} recvBuf=0x{recvBuf:X8} result={result} eePC=0x{SystemMemory.CurrentPcForWatch:X8}");

        // BIOS SIFCMD: CALL completion always ends in SendCmd(RPC_END=0x80000008) (Ghidra
        // FUN_000013a4). EE handler = SignalSema + rpc_packet_free — CompleteRpcEnd.
        CompleteRpcEnd(mem, kernel, pktAddr, cdPtr, isCall: true);
    }


    /// <summary>
    /// BIOS FILEIO RPC (sid=0x80000001). Function numbers match ps2sdk fileio-common.h.
    /// Backed by <see cref="IopModuleHost"/> ISO-aware open/read/stat/dir so commercial
    /// <c>sceOpen("cdrom0:...")</c> returns real disc bytes and directory probes work.
    /// Also accepts IOPRP≥2200 / Play! <c>CFileIoHandler2200</c> command packets
    /// (<c>COMMANDHEADER</c> + payload) used by Shadow of the Colossus after IOPRP300.
    /// </summary>
    private int HandleFileIo(SystemMemory mem, KernelState kernel, IopModuleHost iopModules, PadInput pad, Cdvd cdvd,
        uint fno, uint argBuf, uint sendSize, uint recvBuf, uint recvSize)
    {
        _ = pad; _ = recvSize;
        switch (fno)
        {
            case FioOpen:
            {
                // Canonical ps2sdk: struct _fio_open_arg { int mode; char name[FIO_PATH_MAX]; }
                // Some retail / SN ProDG clients send { int mode; char *name; } (8B) where +4 is
                // an EE path pointer — treating those 4 pointer bytes as an inline C string
                // yields garbage like "ðûþ" (LE of 0x00FEFBF0) and open → ENOENT (Blood Omen 2).
                // FILEIO-2200: COMMANDHEADER(12) + flags + somePtr + fileName[256] (path @+20).
                // Prefer SN ProDG residual layout (BO2/B3/Midway) — never auto-arm 2200 from
                // SN packets (SotC IOPRP300 arms via Init fno=255 with dual result pointers).
                // PreferSnFileIo (MidwayFamilyAssist): hard-block 2200 even when IOPRP=3000.
                int mode;
                string path;
                uint openSema = 0;
                if (!PreferSnFileIo
                    && !LooksLikeSnFioWrapper(mem, argBuf, sendSize)
                    && TryDecodeFio2200Open(mem, argBuf, sendSize, out openSema, out mode, out path))
                {
                    // Only arm when already Init-armed or header looks strictly 2200
                    // (resultPtr0 already set, or resultSize in reply range).
                    if (_fio2200ResultPtr0 != 0 || openSema is > 0 and < 0x1000)
                        _fio2200Armed = true;
                    else
                        openSema = 0; // decode path only; keep classic return-fd ABI
                }
                else
                    DecodeFioOpenArgs(mem, argBuf, sendSize, out mode, out path);
                path = AliasMidwayPakPath(path);
                int openRes = iopModules.FileOpen(path, mode);
                // PS2.RKV virtual open for archive-only audio paths (Blood Omen 2).
                if (openRes < 0)
                {
                    EnsureGoeArchiveMounted(iopModules, cdvd);
                    int rkvFd = TryOpenFromRkv(iopModules, path, out uint rkvSz);
                    if (rkvFd >= 0)
                    {
                        openRes = rkvFd;
                        if (rkvSz > 0)
                            cdvd.NoteHostReadSectors((int)Math.Min((rkvSz + 2047) / 2048, 256));
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                            Console.Error.WriteLine(
                                $"[FILEIO] open RKV path=\"{path}\" fd={rkvFd} size={rkvSz}");
                    }
                }
                // Midway MKDA.PAK members (Deception / Deadly Alliance shared art archive).
                if (openRes < 0)
                {
                    EnsureMkdaPakMounted(iopModules, cdvd);
                    int pakFd = TryOpenFromMkdaPak(iopModules, path, out uint pakSz);
                    if (pakFd >= 0)
                    {
                        openRes = pakFd;
                        if (pakSz > 0)
                            cdvd.NoteHostReadSectors((int)Math.Min((pakSz + 2047) / 2048, 256));
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                            Console.Error.WriteLine(
                                $"[FILEIO] open MKDA.PAK path=\"{path}\" fd={pakFd} size={pakSz}");
                    }
                }
                // Prefer real PRECODE/CODE/MAINMENU .BG2 disc payloads (ISO 8.3 aliases).
                if (openRes < 0)
                {
                    int bg2 = TryOpenBo2RealBg2(iopModules, cdvd, path);
                    if (bg2 >= 0) openRes = bg2;
                }
                // Pack-resident assets (KAIN.IMP / .ETP / ASSETS/*) live inside CODE/PRECODE
                // goefile bigfiles — not as ISO 8.3 leaves. Resolve via goefile path index.
                // Sector credit comes from the generic openedSz path below.
                if (openRes < 0)
                {
                    int packFd = TryOpenBo2PackResident(iopModules, cdvd, path, out uint packSz);
                    if (packFd >= 0)
                    {
                        openRes = packFd;
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                            Console.Error.WriteLine(
                                $"[FILEIO] open PACK path=\"{path}\" fd={packFd} size={packSz}");
                    }
                }
                // Soft stub ONLY for non-payload probes. Never empty-stub .BG2 / MAINMENU /
                // PRECODE / CODE / .IMP / .ETP — empty goefile/package bytes stall parsers and
                // block "Starting code big file" / title menu (live: KAIN.IMP stub vs ENOENT).
                if (openRes < 0 && LooksLikeBo2SoftProbeStub(path))
                {
                    openRes = iopModules.FileOpenMemoryStub(path, Array.Empty<byte>());
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                        Console.Error.WriteLine($"[FILEIO] open STUB path=\"{path}\" fd={openRes}");
                }
                if (openRes >= 0)
                    _fioLastFd = openRes;
                // Preload of disc-backed open (FileOpen may load ≤16MiB into host memory) is real
                // ISO traffic; count once at open so blocker-trace sees cdvdSectors before first read.
                uint openedSz = 0;
                if (openRes >= 0 && LooksLikeDiscPath(path)
                    && iopModules.TryGetOpenFileSize(openRes, out openedSz) && openedSz > 0)
                    cdvd.NoteHostReadSectors((int)((openedSz + 2047) / 2048));
                // SN ProDG FILEIO client: recv / eeReply often carry { result, size } so the EE
                // can size the next read without a working SEEK_END decode (BO2 ENGLISH.DIR path).
                if (openRes >= 0 && openedSz == 0)
                    iopModules.TryGetOpenFileSize(openRes, out openedSz);
                if (openRes >= 0 && openedSz > 0)
                    WriteSnFioOpenSize(mem, argBuf, sendSize, recvBuf, recvSize, openedSz);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine(
                        $"[FILEIO] open path=\"{path}\" mode=0x{mode:X} result={openRes} size={openedSz} argBuf=0x{argBuf:X8} send={sendSize} fio2200={_fio2200Armed}");
                // FILEIO-2200: Play returns 1 from Invoke and posts GENERICREPLY to resultPtr0
                // + signals command.semaphoreId. When armed, always use that ABI — returning the
                // raw fd (esp. fd=0) as CallRpc result is read as "RPC fail" by 2200 clients, so
                // SotC never issues READ after STARTUP.XFF open (live fleet: open×2, no read).
                // Recover sema from COMMANDHEADER if decode left it 0 (hybrid classic path).
                if (_fio2200Armed)
                {
                    if (openSema == 0 && argBuf != 0 && sendSize >= 4)
                        openSema = mem.Read32(argBuf);
                    WriteFio2200GenericReply(mem, kernel, openSema, FioOpen, unchecked((uint)openRes));
                    return 1;
                }
                return openRes;
            }
            case FioGetVersion:
                // SN ProDG / Midway FILEIO clients (MK: Deception, DA, BO2/B3) probe fno=0xFF
                // and strcmp the 4-byte reply against the post-UDNL IOPRP digits ("3000"/
                // "2800"/…). Returning the bare LOADFILE 2.0 token 0x00020000 overwrites a
                // previously correct LOADFILE GetVersion cell and makes sceOpen return
                // 0xFFFEFFFC forever (live Deci2: "Failed overlay load: <cdrom0:\GAMER.OVL;1>").
                // Same PreferIopRpGetVersion gate as LOADFILE: SM needs classic 0x00020000.
                //
                // FILEIO-2200 Init (Play! CFileIoHandler2200 method 255): args[0]/args[1] are
                // EE reply-buffer pointers used by later Getstat/Open replies. Capture them so
                // SotC (IOPRP300 → FILEIO module ≥2200) can receive GETSTATREPLY.
                //
                // Do NOT arm from IOPRP version digits alone: PreferIopRp plants "2340" for
                // BO2 IOPRP234 and "3000" for SotC IOPRP300 — 2340 ≥ 2200 would falsely arm
                // FILEIO-2200 for SN ProDG clients (Blood Omen 2), which still use classic
                // recv/eeReply results. Arm only when BOTH args are EE pointers (true Init)
                // AND IOPRP ≥ 3000 (SotC-class), or when a later Open/Getstat is strictly 2200.
                // Midway PreferSnFileIo also ships IOPRP300 digits but must stay SN FILEIO.
                if (PreferSnFileIo)
                {
                    _fio2200Armed = false;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                        Console.Error.WriteLine("[FILEIO] GetVersion PreferSnFileIo — FILEIO-2200 disarmed");
                }
                else if (argBuf != 0 && sendSize >= 8)
                {
                    uint rp0 = mem.Read32(argBuf);
                    uint rp1 = mem.Read32(argBuf + 4);
                    // SN GetVersion: {seq, eeReply*} — seq is not an EE result buffer pointer.
                    if (IsEeRamPointer(rp0) && IsEeRamPointer(rp1))
                    {
                        _fio2200ResultPtr0 = rp0 & 0x1FFFFFFFu;
                        _fio2200ResultPtr1 = rp1 & 0x1FFFFFFFu;
                        if (PreferIopRpGetVersion && TryParseIopRpVersionNumber(out int iopVer) && iopVer >= 3000)
                            _fio2200Armed = true;
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                            Console.Error.WriteLine(
                                $"[FILEIO] Init/GetVersion resultPtr0=0x{_fio2200ResultPtr0:X8} resultPtr1=0x{_fio2200ResultPtr1:X8} armed={_fio2200Armed}");
                    }
                    else if (IsEeRamPointer(rp0) && !IsEeRamPointer(rp1)
                             && Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    {
                        // Single EE pointer — likely SN eeReply*, not 2200 Init dual buffers.
                        Console.Error.WriteLine(
                            $"[FILEIO] GetVersion SN-shaped rp0=0x{rp0:X8} rp1=0x{rp1:X8} (not arming 2200)");
                    }
                }
                if (PreferIopRpGetVersion && !string.IsNullOrEmpty(_lastIopRpVersionAscii))
                    return PackAsciiVersion(_lastIopRpVersionAscii);
                return 0x00020000;
            case FioClose:
            {
                // SN wrapper (send≈20): {seq, eeReply*, 4, …} — fd omitted; use last open.
                // FILEIO-2200 CLOSECOMMAND: header(12) + fd.
                int fd;
                uint closeSema = 0;
                if (_fio2200Armed && argBuf != 0 && sendSize >= 16
                    && mem.Read32(argBuf + 12) <= 15)
                {
                    closeSema = mem.Read32(argBuf);
                    fd = (int)mem.Read32(argBuf + 12);
                }
                else
                    fd = DecodeSnFioFd(mem, argBuf, sendSize);
                int cr = iopModules.FileClose(fd);
                if (fd == _fioLastFd) _fioLastFd = -1;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine($"[FILEIO] close fd={fd} result={cr} send={sendSize}");
                if (_fio2200Armed && closeSema != 0)
                {
                    WriteFio2200GenericReply(mem, kernel, closeSema, FioClose, unchecked((uint)cr));
                    return 1;
                }
                return cr;
            }
            case FioRead:
            {
                // FILEIO-2200 READCOMMAND: header(12)+fd+buffer+size (Play!).
                // Classic/SN: DecodeSnFioRwArgs.
                int fd;
                uint buf, size;
                uint readSema = 0, readCmdResultPtr = 0, readCmdResultSize = 0;
                bool read2200 = false;
                if (_fio2200Armed && TryDecodeFio2200Read(mem, argBuf, sendSize,
                        out readSema, out readCmdResultPtr, out readCmdResultSize,
                        out fd, out buf, out size))
                {
                    read2200 = true;
                }
                else
                {
                    DecodeSnFioRwArgs(mem, argBuf, sendSize, recvBuf, recvSize,
                        out fd, out buf, out size);
                }
                buf &= 0x1FFFFFFFu;
                // SN wrapper sometimes leaves size=0 in the DMA packet while eeArgs was
                // cleared (Blood Omen 2 ENGLISH.DIR read). Fall back to remaining file
                // bytes so a valid open+read still delivers content.
                if (size == 0 && fd >= 0 && iopModules.TryGetOpenFileSize(fd, out uint fullSz))
                {
                    int pos = iopModules.FileSeek(fd, 0, 1); // SEEK_CUR → current pos
                    if (pos < 0) pos = 0;
                    // FileSeek SEEK_CUR with off=0 returns current offset on our host.
                    uint remain = fullSz > (uint)pos ? fullSz - (uint)pos : 0;
                    // Cap single RPC read to 1 MiB (SN often streams in chunks).
                    size = Math.Min(remain, 0x100000u);
                    // Rewind? SEEK_CUR 0 already didn't move. Re-seek to pos for safety.
                    iopModules.FileSeek(fd, pos, 0); // SEEK_SET
                }
                bool streamed = iopModules.OpenFileIsStreamed(fd);
                int nRead = iopModules.FileRead(mem, fd, buf, size);
                if (nRead > 0 && streamed)
                    cdvd.NoteHostReadSectors((nRead + 2047) / 2048);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine(
                        $"[FILEIO] read fd={fd} buf=0x{buf:X8} size={size} result={nRead} send={sendSize} fio2200={read2200}");
                // Play! delays READ reply one frame (SotC relies on EE reschedule).
                if (read2200)
                {
                    _fio2200ReadPending = true;
                    _fio2200ReadSema = readSema;
                    _fio2200ReadResult = unchecked((uint)nRead);
                    _fio2200ReadCmdResultPtr = readCmdResultPtr;
                    _fio2200ReadCmdResultSize = readCmdResultSize;
                    return 1;
                }
                return nRead;
            }
            case FioWrite:
            {
                // Live BO2 write send=48:
                //   +0 seq  +4 eeReply*  +8 4  +12 0  +16 buf*  +20 size  +24 0  …
                // FILEIO-2200 WRITECOMMAND: header(12)+fd+buffer+size (+unaligned…).
                int fd;
                uint buf, size;
                uint writeSema = 0;
                bool write2200 = false;
                if (_fio2200Armed && TryDecodeFio2200Read(mem, argBuf, sendSize,
                        out writeSema, out _, out _, out fd, out buf, out size))
                {
                    // Same 24B prefix as READCOMMAND (Play WRITECOMMAND shares fd/buf/size).
                    write2200 = true;
                }
                else
                    DecodeSnFioRwArgs(mem, argBuf, sendSize, 0, 0, out fd, out buf, out size);
                buf &= 0x1FFFFFFFu;
                int nw = iopModules.FileWrite(mem, fd, buf, size);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine(
                        $"[FILEIO] write fd={fd} buf=0x{buf:X8} size={size} result={nw} send={sendSize} fio2200={write2200}");
                if (write2200)
                {
                    WriteFio2200GenericReply(mem, kernel, writeSema, FioWrite, unchecked((uint)nw));
                    return 1;
                }
                return nw;
            }
            case FioLseek:
            {
                // Live BO2 lseek send=28:
                //   +0 seq  +4 eeReply*  +8 4  +12 0  +16 0  +20 offset  +24 whence
                // fd omitted — use last open (or word0 if it is a bare ps2sdk packet).
                // FILEIO-2200 SEEKCOMMAND: header(12)+fd+offset+whence.
                int fd, off, whence;
                uint seekSema = 0;
                bool seek2200 = false;
                if (_fio2200Armed && argBuf != 0 && sendSize >= 24
                    && mem.Read32(argBuf + 12) <= 15)
                {
                    seekSema = mem.Read32(argBuf);
                    fd = (int)mem.Read32(argBuf + 12);
                    off = (int)mem.Read32(argBuf + 16);
                    whence = (int)mem.Read32(argBuf + 20);
                    seek2200 = true;
                }
                else
                    DecodeSnFioLseekArgs(mem, argBuf, sendSize, out fd, out off, out whence);
                int sr = iopModules.FileSeek(fd, off, whence);
                // SN eeReply* mirror is done in HandleCall for all FILEIO fnos.
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine(
                        $"[FILEIO] lseek fd={fd} off={off} whence={whence} result={sr} send={sendSize} fio2200={seek2200}");
                if (seek2200)
                {
                    WriteFio2200GenericReply(mem, kernel, seekSema, FioLseek, unchecked((uint)sr));
                    return 1;
                }
                return sr;
            }
            case FioGetstat:
            {
                // Classic ps2sdk: struct _fio_getstat_arg { io_stat_t *buf; char name[256]; }
                // FILEIO-2200 (Play! GETSTATCOMMAND): COMMANDHEADER(sema,resultPtr,resultSize)
                //   + statBuffer + fileName[256]  — path @+16, not @+4.
                // SotC live: classic decode saw pointer LE garbage at +4 → ENOENT thrash.
                string path = "";
                uint statAddr = 0;
                uint cmdSema = 0;
                uint cmdResultPtr = 0;
                uint cmdResultSize = 0;
                bool is2200 = TryDecodeFio2200Getstat(mem, argBuf, sendSize,
                    out cmdSema, out cmdResultPtr, out cmdResultSize, out statAddr, out path);
                if (is2200)
                {
                    _fio2200Armed = true;
                    if (_fio2200ResultPtr0 == 0 && IsEeRamPointer(cmdResultPtr))
                        _fio2200ResultPtr0 = cmdResultPtr & 0x1FFFFFFFu;
                }
                else if (argBuf != 0)
                {
                    uint p0 = mem.Read32(argBuf);
                    uint p1 = sendSize >= 8 ? mem.Read32(argBuf + 4) : 0;
                    if (sendSize >= 8)
                    {
                        string nameAt4 = ReadCString(mem, argBuf + 4, 256);
                        if (LooksLikeFsPath(nameAt4))
                        {
                            path = nameAt4;
                            if (IsEeRamPointer(p0))
                                statAddr = p0 & 0x1FFFFFFFu;
                        }
                        else if (IsEeRamPointer(p1) && !LooksLikeFsPath(nameAt4))
                        {
                            // Pointer-form name at +4; stat buf at +0 (or recv).
                            path = ReadCString(mem, p1 & 0x1FFFFFFFu, 256);
                            if (IsEeRamPointer(p0))
                                statAddr = p0 & 0x1FFFFFFFu;
                        }
                        else if (IsEeRamPointer(p0) && !LooksLikeFsPath(ReadCString(mem, argBuf, 4)))
                        {
                            // Legacy: path pointer at +0, stat at +4.
                            path = ReadCString(mem, p0 & 0x1FFFFFFFu, 256);
                            statAddr = IsEeRamPointer(p1) ? (p1 & 0x1FFFFFFFu) : 0;
                        }
                        else
                        {
                            // Last resort: scan for device path in the send blob (same class as open).
                            uint scanLimit = sendSize > 0 ? Math.Min(sendSize, 512u) : 64u;
                            if (TryFindDevicePathInBuffer(mem, argBuf, scanLimit, out string found))
                                path = found;
                            else
                                path = nameAt4.Length > 0 ? nameAt4 : ReadCString(mem, argBuf, 256);
                            if (IsEeRamPointer(p0))
                                statAddr = p0 & 0x1FFFFFFFu;
                        }
                    }
                    else
                    {
                        path = ReadCString(mem, argBuf, 256);
                    }
                }
                if (statAddr == 0) statAddr = recvBuf;
                int gs = iopModules.FileGetStat(mem, path, statAddr);
                // Do NOT alias truncated probes (KERNEL.X / STARTUP.) to .XFF — real IOP
                // returns ENOENT and SotC continues to open the full name. Forcing success
                // caused getstat thrash and blocked KERNEL.XFF open (live 100c).
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine(
                        $"[FILEIO] getstat path=\"{path}\" result={gs} stat=0x{statAddr:X8} " +
                        $"fio2200={is2200} sema={cmdSema} arg=0x{argBuf:X8} send={sendSize}");
                if (is2200 || (_fio2200Armed && cmdSema != 0))
                {
                    WriteFio2200GetstatReply(mem, kernel, cmdSema, cmdResultPtr, cmdResultSize,
                        unchecked((uint)gs), statAddr);
                    // Play! InvokeGetStat returns 1; real result is in GETSTATREPLY.
                    return 1;
                }
                return gs;
            }
            case FioChstat:
                // struct _fio_chstat_arg — ISO/host HLE has no mutable attributes.
                return 0;
            case FioRemove:
            {
                // path inline at arg (union { char path[256]; int result; })
                string path = argBuf != 0 ? ReadCString(mem, argBuf, 256) : "";
                return iopModules.FileRemove(path);
            }
            case FioMkdir:
            case FioRmdir:
            {
                // Path-only ops; read-only ISO — refuse mutating disc paths, allow probe success.
                string path = argBuf != 0 ? ReadCString(mem, argBuf, 256) : "";
                if (iopModules.DiscVolume != null &&
                    path.StartsWith("cdrom", StringComparison.OrdinalIgnoreCase))
                    return IopModuleHost.IoManErrnoNoEntry;
                return 0;
            }
            case FioDopen:
            {
                // path inline at arg (union { char name[256]; int result; })
                string path = argBuf != 0 ? ReadCString(mem, argBuf, 256) : "";
                return iopModules.DirOpen(path);
            }
            case FioDclose:
            {
                int dfd = argBuf != 0 ? (int)mem.Read32(argBuf) : -1;
                return iopModules.DirClose(dfd);
            }
            case FioDread:
            {
                // struct _fio_dread_arg { union { int fd; int result; } p; io_dirent_t *buf; }
                int dfd = argBuf != 0 ? (int)mem.Read32(argBuf) : -1;
                uint dirent = argBuf != 0 && sendSize >= 8 ? mem.Read32(argBuf + 4) : recvBuf;
                return iopModules.DirRead(mem, dfd, dirent);
            }
            case FioIoctl:
            case FioFormat:
                return 0;
            case FioAddDrv:
            {
                // EE-side rare path: device name string in arg (HLE registers name only).
                // Real drivers call IOP AddDrv directly; FILEIO fno 15/16 exist in the header.
                string name = argBuf != 0 ? ReadCString(mem, argBuf, 64) : "";
                return iopModules.AddDrv(name);
            }
            case FioDelDrv:
            {
                string name = argBuf != 0 ? ReadCString(mem, argBuf, 64) : "";
                return iopModules.DelDrv(name);
            }
            default:
                // Real FILEIO logs "sce_fileio: unrecognized code %x". Live Burnout 3 calls
                // fno=23 after GTFS/LGDEV (result was -22 EINVAL → IOP reboot thrash). Soft-
                // success high/extended XFILEIO-style fnos so boot can open game assets;
                // keep classic 0..16 as failure when truly unmapped above.
                if (fno is >= 17 and <= 64)
                {
                    if (recvBuf != 0 && recvSize >= 4)
                        mem.Write32(recvBuf, 0);
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                        Console.Error.WriteLine(
                            $"[FILEIO] soft-success unknown fno={fno} (XFILEIO-class) arg=0x{argBuf:X8}");
                    return 0;
                }
                return IopModuleHost.IoManErrnoInvalid;
        }
    }

    private static uint ResolvePathArg(SystemMemory mem, uint argBuf, uint sendSize)
    {
        if (argBuf == 0) return 0;
        uint maybe = mem.Read32(argBuf);
        if (maybe >= 0x10000 && maybe < SystemMemory.RDRAM_SIZE) return maybe;
        _ = sendSize;
        return argBuf;
    }

    /// <summary>EE RDRAM pointer (phys, KSEG0, or uncached) suitable for path/stat buffers.</summary>
    private static bool IsEeRamPointer(uint p)
    {
        if (p == 0) return false;
        uint phys = p & 0x1FFFFFFFu;
        return phys >= 0x1000u && phys < (uint)SystemMemory.RDRAM_SIZE;
    }

    /// <summary>True when <paramref name="s"/> looks like a PS2 filesystem path, not pointer garbage.</summary>
    private static bool LooksLikeFsPath(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 256) return false;
        // High-bit / control bytes (except tab) → not a path (classic LE pointer-as-string).
        foreach (char c in s)
        {
            if (c < 0x20 && c != '\t') return false;
            if (c > 0x7E) return false;
        }
        // Need a device colon, path separator, extension, or alnum basename.
        if (s.IndexOf(':') >= 0 || s.IndexOf('\\') >= 0 || s.IndexOf('/') >= 0 || s.IndexOf('.') >= 0)
            return true;
        foreach (char c in s)
            if (char.IsLetterOrDigit(c)) return true;
        return false;
    }

    private static bool LooksLikeDiscPath(string path) =>
        !string.IsNullOrEmpty(path) &&
        (path.StartsWith("cdrom", StringComparison.OrdinalIgnoreCase)
         || path.StartsWith("cdrom0", StringComparison.OrdinalIgnoreCase)
         || (!path.Contains(':') && LooksLikeFsPath(path)));

    /// <summary>Parse IOPRP/DNAS ASCII tag (e.g. <c>"3000"</c>) to int; false if non-numeric.</summary>
    private bool TryParseIopRpVersionNumber(out int version)
    {
        version = 0;
        if (string.IsNullOrEmpty(_lastIopRpVersionAscii)) return false;
        // Tag may be "3000", "2200", or "3000...." — take leading digits.
        int i = 0;
        while (i < _lastIopRpVersionAscii.Length && char.IsDigit(_lastIopRpVersionAscii[i])) i++;
        if (i == 0) return false;
        return int.TryParse(_lastIopRpVersionAscii.AsSpan(0, i), out version);
    }

    /// <summary>
    /// Play! <c>GETSTATCOMMAND</c> (FileIoHandler2200):
    /// <c>COMMANDHEADER{sema,resultPtr,resultSize}</c> + <c>statBuffer</c> + <c>fileName[256]</c>.
    /// Path lives at +16; classic ps2sdk puts the path at +4.
    /// Live SotC may DMA a short send (&lt;272) — clamp path read to remaining send bytes so
    /// ReadCString cannot run into adjacent scratch ("STARTUP.XFF" → "STARTUP.ldsys…").
    /// </summary>
    private static bool TryDecodeFio2200Getstat(SystemMemory mem, uint argBuf, uint sendSize,
        out uint semaId, out uint cmdResultPtr, out uint cmdResultSize, out uint statBuffer, out string path)
    {
        semaId = 0;
        cmdResultPtr = 0;
        cmdResultSize = 0;
        statBuffer = 0;
        path = "";
        if (argBuf == 0 || sendSize < 20) return false;

        // Classic wins if name@+4 is a real device path (ps2sdk _fio_getstat_arg).
        int maxAt4 = (int)Math.Min(256u, sendSize > 4 ? sendSize - 4 : 0);
        string nameAt4 = maxAt4 > 0 ? ReadCString(mem, argBuf + 4, maxAt4) : "";
        if (LooksLikeFsPath(nameAt4) && (nameAt4.IndexOf(':') >= 0 || nameAt4.IndexOf('.') >= 0))
            return false;

        int maxAt16 = (int)Math.Min(256u, sendSize > 16 ? sendSize - 16 : 0);
        string nameAt16 = maxAt16 > 0 ? ReadCString(mem, argBuf + 16, maxAt16) : "";
        nameAt16 = SanitizeFioPath(nameAt16);
        if (!LooksLikeFsPath(nameAt16)) return false;
        // Prefer device-looking paths for 2200; bare names still accepted if send is large.
        if (nameAt16.IndexOf(':') < 0 && nameAt16.IndexOf('.') < 0 && sendSize < 32)
            return false;

        semaId = mem.Read32(argBuf);
        cmdResultPtr = mem.Read32(argBuf + 4);
        cmdResultSize = mem.Read32(argBuf + 8);
        statBuffer = mem.Read32(argBuf + 12);
        path = nameAt16;

        // statBuffer should be EE RAM; if not, still accept when path looks solid (recv fallback).
        if (IsEeRamPointer(statBuffer))
            statBuffer &= 0x1FFFFFFFu;
        else
            statBuffer = 0;
        return true;
    }

    /// <summary>
    /// Trim IOP/EE path noise: stop at first control char, collapse trailing garbage after
    /// a valid <c>;1</c> version, and reject merged string-table bleed.
    /// </summary>
    private static string SanitizeFioPath(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        // Stop at first control (except nothing — already filtered by ReadCString often).
        int end = s.Length;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < 0x20 || c > 0x7E) { end = i; break; }
        }
        s = s[..end];
        // "cdrom0:\STARTUP.XFF;1" — if ";1" present, cut after version digit run.
        int semi = s.IndexOf(';');
        if (semi >= 0)
        {
            int j = semi + 1;
            while (j < s.Length && char.IsDigit(s[j])) j++;
            s = s[..j];
        }
        // Truncated "cdrom0:\STARTUP." without extension — common short-DMA bleed; do not invent.
        return s.Trim();
    }

    /// <summary>
    /// Play! <c>OPENCOMMAND</c>: header(12) + flags + somePtr + fileName[256] (path @+20).
    /// Must <b>not</b> match SN ProDG residual path@+20 (BO2/B3/Midway): that packet has
    /// <c>{seq, eeReply*, 4, …, path@+0x14}</c>. Mis-classifying SN as 2200 returns CallRpc=1
    /// (Play GENERICREPLY) instead of the host fd, skips SN eeReply mirror, and stalls the
    /// Manager State → FILEIO KAIN.IMP / .BG2 Open transition (live BO2 @ thrash 0x5387xx).
    /// </summary>
    private static bool TryDecodeFio2200Open(SystemMemory mem, uint argBuf, uint sendSize,
        out uint semaId, out int mode, out string path)
    {
        semaId = 0;
        mode = 0;
        path = "";
        if (argBuf == 0 || sendSize < 24) return false;

        // SN ProDG wrapper: never 2200 (Play! COMMANDHEADER.resultSize is not 4).
        if (LooksLikeSnFioWrapper(mem, argBuf, sendSize))
            return false;

        // Classic/SN: mode@+0, path often @+4. If path@+4 is real, not 2200.
        string nameAt4 = ReadCString(mem, argBuf + 4, 256);
        if (LooksLikeFsPath(nameAt4) && (nameAt4.IndexOf(':') >= 0 || nameAt4.IndexOf('.') >= 0))
            return false;

        string nameAt20 = ReadCString(mem, argBuf + 20, 256);
        if (!LooksLikeFsPath(nameAt20) || (nameAt20.IndexOf(':') < 0 && nameAt20.IndexOf('.') < 0))
            return false;

        // 2200 COMMANDHEADER: semaphoreId, resultPtr (EE), resultSize (reply buf size).
        // SN residual also has path@+20 with eeReply* @+4 — reject non-reply-size @+8.
        uint w0 = mem.Read32(argBuf);
        uint w1 = mem.Read32(argBuf + 4);
        uint w2 = mem.Read32(argBuf + 8);
        if (!IsEeRamPointer(w1))
            return false;
        // resultSize: Play uses small reply sizes (0x10..0x80). Reject SN field==4 and junk.
        if (w2 is < 0x10 or > 0x100)
            return false;
        // semaphoreId is a small EE kernel id — SN seq cookies climb past 0x100 quickly but
        // early ones can be low; resultSize gate above is the main SN filter.
        if (w0 > 0x10000)
            return false;

        semaId = w0;
        mode = (int)mem.Read32(argBuf + 12); // flags
        path = nameAt20;
        return true;
    }

    /// <summary>
    /// Write Play! <c>GETSTATREPLY</c> to the Init result buffer and signal command sema.
    /// Layout: REPLYHEADER(16) + result + dstPtr + io_stat_t(40) = 64 bytes.
    /// Also ensures <paramref name="statAddr"/> already holds the stat (caller FileGetStat).
    /// </summary>
    private void WriteFio2200GetstatReply(SystemMemory mem, KernelState kernel,
        uint semaId, uint cmdResultPtr, uint cmdResultSize, uint result, uint statAddr)
    {
        uint replyDest = _fio2200ResultPtr0;
        if (replyDest == 0 && IsEeRamPointer(cmdResultPtr))
            replyDest = cmdResultPtr & 0x1FFFFFFFu;
        if (replyDest != 0 && replyDest + 64 <= (uint)SystemMemory.RDRAM_SIZE)
        {
            // REPLYHEADER
            mem.Write32(replyDest + 0, semaId);
            mem.Write32(replyDest + 4, FioGetstat); // commandId
            mem.Write32(replyDest + 8, cmdResultPtr);
            mem.Write32(replyDest + 12, cmdResultSize);
            mem.Write32(replyDest + 16, result);
            mem.Write32(replyDest + 20, statAddr); // dstPtr
            // Copy io_stat_t (40B) from statAddr into reply +24 when available.
            if (statAddr != 0 && statAddr + 40 <= (uint)SystemMemory.RDRAM_SIZE)
            {
                for (uint i = 0; i < 40; i += 4)
                    mem.Write32(replyDest + 24 + i, mem.Read32(statAddr + i));
            }
        }
        // Collapse Play SIFCMD 0x80000011: signal the command semaphore so EE leaves WaitSema.
        if (semaId != 0 && semaId < 0x10000)
            kernel.ISignalSema((int)semaId);
    }

    /// <summary>Play! GENERICREPLY (header + result + 3 pad words) + signal command sema.</summary>
    private void WriteFio2200GenericReply(SystemMemory mem, KernelState kernel,
        uint semaId, uint commandId, uint result)
    {
        uint replyDest = _fio2200ResultPtr0;
        if (replyDest != 0 && replyDest + 32 <= (uint)SystemMemory.RDRAM_SIZE)
        {
            mem.Write32(replyDest + 0, semaId);
            mem.Write32(replyDest + 4, commandId);
            mem.Write32(replyDest + 8, 0);
            mem.Write32(replyDest + 12, 0);
            mem.Write32(replyDest + 16, result);
            mem.Write32(replyDest + 20, 0);
            mem.Write32(replyDest + 24, 0);
            mem.Write32(replyDest + 28, 0);
        }
        if (semaId != 0 && semaId < 0x10000)
            kernel.ISignalSema((int)semaId);
    }

    /// <summary>
    /// Play! <c>READCOMMAND</c>: header(12) + fd + buffer + size. Armed only when FILEIO-2200
    /// is active so classic/SN decode stays the default.
    /// </summary>
    private static bool TryDecodeFio2200Read(SystemMemory mem, uint argBuf, uint sendSize,
        out uint semaId, out uint cmdResultPtr, out uint cmdResultSize,
        out int fd, out uint buf, out uint size)
    {
        semaId = 0;
        cmdResultPtr = 0;
        cmdResultSize = 0;
        fd = -1;
        buf = 0;
        size = 0;
        if (argBuf == 0 || sendSize < 24) return false;
        semaId = mem.Read32(argBuf);
        cmdResultPtr = mem.Read32(argBuf + 4);
        cmdResultSize = mem.Read32(argBuf + 8);
        fd = (int)mem.Read32(argBuf + 12);
        buf = mem.Read32(argBuf + 16);
        size = mem.Read32(argBuf + 20);
        // fd must be a plausible IOMAN slot; buffer an EE pointer.
        if (fd < 0 || fd > 15) return false;
        if (!IsEeRamPointer(buf) && buf != 0) return false;
        return true;
    }

    /// <summary>
    /// Deliver deferred FILEIO-2200 READ replies (Play! one-frame delay). Called from
    /// <see cref="BiosHle.OnVblank"/> so SotC EE threads can reschedule between CallRpc and
    /// the command-sema wake.
    /// </summary>
    public void ProcessPendingFileIoReplies(SystemMemory mem, KernelState kernel)
    {
        if (!_fio2200ReadPending) return;
        _fio2200ReadPending = false;
        WriteFio2200GenericReply(mem, kernel, _fio2200ReadSema, FioRead, _fio2200ReadResult);
        // Also stamp cmd resultPtr fields into the reply header for EE clients that check them.
        uint replyDest = _fio2200ResultPtr0;
        if (replyDest != 0 && replyDest + 16 <= (uint)SystemMemory.RDRAM_SIZE)
        {
            mem.Write32(replyDest + 8, _fio2200ReadCmdResultPtr);
            mem.Write32(replyDest + 12, _fio2200ReadCmdResultSize);
        }
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine(
                $"[FILEIO] delayed-read-reply result={_fio2200ReadResult} sema={_fio2200ReadSema}");
    }



    /// <summary>
    /// Decode FILEIO OPEN send buffer.
    /// <list type="bullet">
    /// <item>ps2sdk canonical: <c>{ int mode; char name[256] }</c> (name @+4)</item>
    /// <item>Compact: <c>{ int mode; char *name }</c> (pointer @+4, small send)</item>
    /// <item>Extended retail (Blood Omen 2 / SN): mode @+0, stale stack ptr @+4, extra words,
    /// then a real inline path @+20 (DMA copy survives async RPC; stack pointer does not).
    /// Live hex: <c>26 00 00 00 F0 FB FE 01 … 63 64 72 6F…</c> →
    /// <c>cdrom0:\GOGAMES\BO2\GAME.ERG;1</c></item>
    /// </list>
    /// </summary>
    private static void DecodeFioOpenArgs(SystemMemory mem, uint argBuf, uint sendSize,
        out int mode, out string path)
    {
        mode = 0;
        path = "";
        if (argBuf == 0) return;

        uint w0 = mem.Read32(argBuf);
        uint w1 = sendSize >= 8 ? mem.Read32(argBuf + 4) : 0;
        mode = (int)w0;

        // SN ProDG wrapper: { seq, eeReply*, 4, mode@+0xC, path@+0x14 }.
        // Word0 is a sequence cookie (often 0x2D…), NOT FIO open mode — Whiplash GAME.INI
        // live: seq=0x2D, mode@+0xC=1 (O_RDONLY), path@+0x14 = cdrom0:\WHIPLASH\GAME.INI;1.
        if (LooksLikeSnFioWrapper(mem, argBuf, sendSize) && sendSize >= 16)
        {
            mode = (int)mem.Read32(argBuf + 12);
            string snPath = ReadCString(mem, argBuf + 0x14, 256);
            if (LooksLikeFsPath(snPath))
            {
                path = snPath;
                return;
            }
            // Fall through to scanners if path not at +0x14.
        }

        // 1) Canonical inline name @+4.
        if (sendSize >= 8)
        {
            string inlineAt4 = ReadCString(mem, argBuf + 4, 256);
            if (LooksLikeFsPath(inlineAt4))
            {
                path = inlineAt4;
                return;
            }
        }

        // 2) Compact / pointer form: path* @+4 → EE RAM (only if still a live path string).
        if (IsEeRamPointer(w1))
        {
            string viaPtr = ReadCString(mem, w1 & 0x1FFFFFFFu, 256);
            if (LooksLikeFsPath(viaPtr))
            {
                path = viaPtr;
                return;
            }
        }

        // 3) Scan common header paddings for an embedded device path (cdrom0:, host:, mc0:, …).
        //    Blood Omen 2 places the path at +0x14 after mode/ptr/extra words.
        uint scanLimit = sendSize > 0 ? Math.Min(sendSize, 512u) : 64u;
        uint[] preferOffs = { 0x14, 0x10, 0x18, 0x0C, 0x08, 0x20, 0x04, 0x00 };
        foreach (uint off in preferOffs)
        {
            if (off + 4 > scanLimit) continue;
            string s = ReadCString(mem, argBuf + off, 256);
            if (LooksLikeFsPath(s) && (s.IndexOf(':') >= 0 || s.IndexOf('.') >= 0))
            {
                path = s;
                // mode stays w0 unless w0 itself looked like a path start
                if (off == 0) mode = 0;
                // SN wrapper: prefer real mode @+0xC over seq cookie in w0
                if (LooksLikeSnFioWrapper(mem, argBuf, sendSize) && sendSize >= 16)
                    mode = (int)mem.Read32(argBuf + 12);
                return;
            }
        }

        // 4) Byte-scan for "cdrom" / "host" / "mc0" / "rom0" device prefixes in the send blob.
        if (TryFindDevicePathInBuffer(mem, argBuf, scanLimit, out string found))
        {
            path = found;
            return;
        }

        // 5) mode word was a path pointer.
        if (IsEeRamPointer(w0))
        {
            string viaMode = ReadCString(mem, w0 & 0x1FFFFFFFu, 256);
            if (LooksLikeFsPath(viaMode))
            {
                path = viaMode;
                mode = sendSize >= 8 ? (int)w1 : 0;
                return;
            }
        }

        // 6) Path-first legacy whole buffer.
        string inlineAt0 = ReadCString(mem, argBuf, 256);
        if (LooksLikeFsPath(inlineAt0))
        {
            path = inlineAt0;
            mode = 0;
            return;
        }

        // Last resort (smokes with synthetic non-device names).
        if (sendSize >= 8)
        {
            path = ReadCString(mem, argBuf + 4, 256);
            if (path.Length == 0)
                path = inlineAt0;
        }
        else
            path = inlineAt0;
    }

    /// <summary>
    /// Blood Omen 2 / SN FILEIO client wraps every call as:
    /// <c>{ u32 seq; void *eeArgs; u32 field; … [optional inline path @+0x14] }</c>.
    /// Canonical ps2sdk puts the real <c>_fio_*_arg</c> at <c>eeArgs</c>. Word0 is a sequence
    /// id (0x26, 0x2B, 0x2D…), NOT an fd — treating it as fd caused EBADF on every lseek/read.
    /// When <c>eeArgs</c> still points at live EE stack, use it; else fall back to inline words.
    /// </summary>
    /// <summary>
    /// True when send buffer looks like the SN/Blood Omen FILEIO wrapper:
    /// word0 = sequence cookie (often &gt;15), word1 = EE reply pointer, word2 = 4.
    /// </summary>
    private static bool LooksLikeSnFioWrapper(SystemMemory mem, uint argBuf, uint sendSize)
    {
        if (argBuf == 0 || sendSize < 20) return false;
        uint w0 = mem.Read32(argBuf);
        uint w1 = mem.Read32(argBuf + 4);
        uint w2 = mem.Read32(argBuf + 8);
        return w2 == 4 && IsEeRamPointer(w1) && w0 > 15;
    }

    private int DecodeSnFioFd(SystemMemory mem, uint argBuf, uint sendSize)
    {
        if (argBuf == 0) return _fioLastFd;
        // Canonical ps2sdk: fd @+0
        if (!LooksLikeSnFioWrapper(mem, argBuf, sendSize))
        {
            int w0 = (int)mem.Read32(argBuf);
            if (w0 >= 0 && w0 <= 15) return w0;
        }
        return _fioLastFd >= 0 ? _fioLastFd : (int)mem.Read32(argBuf);
    }

    private void DecodeSnFioRwArgs(SystemMemory mem, uint argBuf, uint sendSize,
        uint recvBuf, uint recvSize, out int fd, out uint buf, out uint size)
    {
        fd = -1; buf = recvBuf; size = recvSize;
        if (argBuf == 0) return;

        if (LooksLikeSnFioWrapper(mem, argBuf, sendSize) && sendSize >= 24)
        {
            // SN: +16 buf*, +20 size; fd = last open
            fd = _fioLastFd >= 0 ? _fioLastFd : 0;
            buf = mem.Read32(argBuf + 16);
            size = mem.Read32(argBuf + 20);
            if (buf == 0) buf = recvBuf;
            // Cap absurd sizes (packet also carries 0x7FFFFFFF sentinel later)
            if (size > 0x1000000) size = 0;
            return;
        }

        // Canonical _fio_read_arg / _fio_write_arg
        fd = (int)mem.Read32(argBuf);
        buf = sendSize >= 8 ? mem.Read32(argBuf + 4) : recvBuf;
        size = sendSize >= 12 ? mem.Read32(argBuf + 8) : recvSize;
        if (buf == 0) buf = recvBuf;
    }

    private void DecodeSnFioLseekArgs(SystemMemory mem, uint argBuf, uint sendSize,
        out int fd, out int off, out int whence)
    {
        fd = -1; off = 0; whence = 0;
        if (argBuf == 0) return;

        if (LooksLikeSnFioWrapper(mem, argBuf, sendSize) && sendSize >= 28)
        {
            // Two SN ProDG lseek packings exist:
            //   BO2 (Crystal Dynamics): +20 offset, +24 whence, +16 usually 0
            //   Midway (Deception SLUS_208.81 @ 0x111600): +16 offset, +20 whence, +24 slot
            // Live Deception: SEEK_END packs +16=0 +20=2 +24=slot; BO2 SEEK_END is +20=0 +24=2.
            fd = _fioLastFd >= 0 ? _fioLastFd : 0;
            int o16 = (int)mem.Read32(argBuf + 16);
            int w20 = (int)mem.Read32(argBuf + 20);
            int w24 = (int)mem.Read32(argBuf + 24);
            bool w20IsWhence = w20 is >= 0 and <= 2;
            bool w24IsWhence = w24 is >= 0 and <= 2;
            bool midway;
            if (w20IsWhence && !w24IsWhence)
                midway = true; // slot id > 2 at +24
            else if (!w20IsWhence && w24IsWhence)
                midway = false; // large offset at +20, whence at +24
            else if (w20IsWhence && w24IsWhence)
            {
                // Ambiguous small fields. Midway SEEK_END uses whence@+20==2; BO2 SEEK_END
                // uses offset@+20==0 + whence@+24==2. Midway SEEK_SET uses whence@+20==0
                // with slot@+24!=0; BO2 SEEK_SET uses whence@+24==0.
                if (w20 == 2)
                    midway = true;
                else if (w24 == 2 && w20 == 0)
                    midway = false;
                else if (w20 == 0 && w24 != 0)
                    midway = true; // Midway SEEK_SET (slot at +24)
                else
                    midway = o16 != 0; // non-zero +16 only Midway stores offset there
            }
            else
                midway = false;

            if (midway)
            {
                off = o16;
                whence = w20;
            }
            else
            {
                off = w20;
                whence = w24;
            }
            if (whence < 0 || whence > 2) whence = 0;
            return;
        }

        fd = (int)mem.Read32(argBuf);
        off = sendSize >= 8 ? (int)mem.Read32(argBuf + 4) : 0;
        whence = sendSize >= 12 ? (int)mem.Read32(argBuf + 8) : 0;
    }

    /// <summary>Scan a DMA send buffer for a NUL-terminated path starting at a known device prefix.</summary>
    private static bool TryFindDevicePathInBuffer(SystemMemory mem, uint argBuf, uint limit, out string path)
    {
        path = "";
        if (limit < 6) return false;
        // Common PS2 device prefixes (lowercase compare on ASCII).
        ReadOnlySpan<string> prefixes = ["cdrom0:", "cdrom:", "host0:", "host:", "mc0:", "mc1:", "rom0:", "rom:", "hdd0:", "mass:"];
        for (uint i = 0; i + 6 < limit; i++)
        {
            // Fast reject: path must start with a letter.
            byte b0 = mem.Read8(argBuf + i);
            if (b0 is < (byte)'A' or > (byte)'z') continue;
            string cand = ReadCString(mem, argBuf + i, 256);
            if (!LooksLikeFsPath(cand) || cand.Length < 6) continue;
            foreach (string pfx in prefixes)
            {
                if (cand.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
                {
                    path = cand;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Real LOADFILE service (sid=0x80000006). Dispatch + reply shapes ground-truthed against
    /// BIOS LOADFILE.IRX (Ghidra <c>FUN_000004c4</c> table + handlers) and ps2sdk
    /// <c>loadfile-common.h</c> / <c>ee/kernel/src/loadfile.c</c>.
    /// <para>
    /// Module-load family replies with 8 bytes <c>{ result, modres }</c>.
    /// ELF-load family replies with <c>t_ExecData</c>-shaped <c>{ epc, gp, sp, dummy }</c>
    /// (client treats epc==0 as load miss).
    /// GET/SET_ADDR reply with 4-byte result. Unhandled fnos return negative (not silent success).
    /// </para>
    /// </summary>
    private void HandleLoadFile(SystemMemory mem, IopModuleHost iopModules, Cdvd cdvd, uint fno, uint argBuf, uint recvBuf)
    {
        LoadFileOps++;
        int result = -1;
        int modres = 0;
        // ELF path may write a full t_ExecData (16B); module path writes 8B.
        bool elfReply = false;
        uint epc = 0, gp = 0, sp = 0;

        switch (fno)
        {
            case LfModLoad:
            case LfMgModLoad:
            {
                // struct _lf_module_load_arg { union{arg_len,result} p; int modres; char path[252]; char args[252]; }
                // decomp FUN_00000150 / FUN_000001fc (MG shares path load; SecrDiskBootFile when bytes present)
                string path = argBuf != 0 ? ReadCString(mem, argBuf + 8, LfPathMax) : "";
                bool mg = fno == LfMgModLoad;
                result = LoadModuleByPath(mem, iopModules, cdvd, path, out modres, magicGate: mg);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine(
                        $"[LOADFILE] {(mg ? "MG_MOD_LOAD" : "MOD_LOAD")} path=\"{path}\" result={result} modres={modres}");
                break;
            }
            case LfElfLoad:
            case LfMgElfLoad:
            {
                // struct _lf_elf_load_arg { u32 epc; u32 gp; char path[252]; char secname[252]; }
                // decomp FUN_00000240 / FUN_000002fc — success fills epc/gp; open fail → -203;
                // load fail → epc=0 so client returns -SCE_ELOADMISS.
                // MG_ELF_LOAD (FUN_000002fc) shares plain path when payload is ELF; encrypted → miss.
                elfReply = true;
                string path = argBuf != 0 ? ReadCString(mem, argBuf + 8, LfPathMax) : "";
                // secname at +8+252 = +260; used by encrypted/part loaders — HLE treats "all"/empty same.
                string secname = argBuf != 0 ? ReadCString(mem, argBuf + 8 + (uint)LfPathMax, LfArgMax) : "";
                bool mgElf = fno == LfMgElfLoad;
                result = LoadElfByPath(mem, iopModules, path, secname, out epc, out gp, magicGate: mgElf);
                if (result >= 0)
                {
                    // Success: epc/gp are the payload (decomp DAT_00001e80/1e84); result word is epc.
                    // Client checks arg.epc != 0, not a separate status int.
                    result = unchecked((int)epc);
                    modres = unchecked((int)gp);
                }
                break;
            }
            case LfSetAddr:
            {
                // struct _lf_iop_val_arg { union{iop_addr,result}; int type; union{b,s,l} val; }
                // decomp FUN_00000420 — always returns 0 after write
                result = IopSetVal(mem, argBuf);
                break;
            }
            case LfGetAddr:
            {
                // decomp FUN_00000364 — returns the value read as result
                result = IopGetVal(mem, argBuf);
                break;
            }
            case LfModBufLoad:
            {
                // struct _lf_module_buffer_load_arg { union{ptr,result}; union{arg_len,modres}; ... }
                uint ptr = argBuf != 0 ? mem.Read32(argBuf) : 0;
                result = TryLoadModuleFromMemory(mem, iopModules, ptr, null);
                // HLE does not execute module _start; modres stays 0 (real start return value).
                modres = 0;
                break;
            }
            case LfModStop:
            {
                // _lf_module_stop_arg: id @+0; reply {result, modres} — result is id on success
                int id = argBuf != 0 ? (int)mem.Read32(argBuf) : -1;
                result = iopModules.StopModule(id, out modres);
                break;
            }
            case LfModUnload:
            {
                // union _lf_module_unload_arg { int id; int result; } — 4-byte reply is id on success
                int id = argBuf != 0 ? (int)mem.Read32(argBuf) : -1;
                result = iopModules.UnloadModule(id); // id on success, or ModloadErr*
                break;
            }
            case LfSearchModByName:
            {
                // struct _lf_search_module_by_name_arg { int id; int dummy1; char name[252]; ... }
                // Client reads 4-byte id (recv size 4); name is at +8.
                string name = argBuf != 0 ? ReadCString(mem, argBuf + 8, LfPathMax) : "";
                result = iopModules.TryGetModule(StripDevicePrefix(name), out int foundId) ? foundId : -1;
                break;
            }
            case LfSearchModByAddress:
            {
                // struct _lf_search_module_by_address_arg { union{ptr, id} p; }
                uint ptr = argBuf != 0 ? mem.Read32(argBuf) : 0;
                result = iopModules.TryFindModuleByAddress(ptr, out int mid) ? mid : -1;
                break;
            }
            case LfGetVersion:
                // Confirmed called live (not via public ps2sdk wrapper).
                // After SifIopReset(IOPRPxxx/DNASxxx), SN ProDG / Midway clients (MK:DA @
                // 0x113778, BO2, B3) copy the 4-byte reply into a BSS cell and strcmp it
                // against the expected IOPRP digit string ("2430"/"2340"/"2800") or "....".
                // Real UDNL would surface the image version; without that handoff HLE used
                // to return a bare LOADFILE 0x00020000 placeholder and the gate returned
                // 0xFFFEFFFC forever (cdvdSectors stuck at 0) on those titles.
                // Shaolin Monks (SLUS_210.87) A/B (2026-07-30): always-IOPRP-ASCII path
                // changes post-reboot RPC cadence vs pre-merge spine. Prefer classic
                // 0x00020000 unless <see cref="PreferIopRpGetVersion"/> is set (DA/BO2/B3).
                result = PreferIopRpGetVersion && !string.IsNullOrEmpty(_lastIopRpVersionAscii)
                    ? PackAsciiVersion(_lastIopRpVersionAscii)
                    : 0x00020000;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine(
                        $"[LOADFILE] GET_VERSION result=0x{unchecked((uint)result):X8} " +
                        $"ioprp=\"{_lastIopRpVersionAscii}\" preferIopRp={PreferIopRpGetVersion}");
                break;

            default:
                // Unmapped LOADFILE fnos: honest failure (negative id), not silent success.
                result = -1;
                break;
        }

        if (recvBuf == 0)
            return;

        if (elfReply)
        {
            // t_ExecData: epc, gp, sp, dummy — client uses sizeof(t_ExecData)=16 recv size.
            // On error codes (negative result with epc==0 path), still write result in word0.
            if (result < 0 && epc == 0)
            {
                mem.Write32(recvBuf, unchecked((uint)result));
                mem.Write32(recvBuf + 4, 0);
                mem.Write32(recvBuf + 8, 0);
                mem.Write32(recvBuf + 12, 0);
            }
            else
            {
                mem.Write32(recvBuf, epc != 0 ? epc : unchecked((uint)result));
                mem.Write32(recvBuf + 4, gp);
                mem.Write32(recvBuf + 8, sp);
                mem.Write32(recvBuf + 12, 0);
            }
        }
        else if (fno is LfSearchModByName or LfSearchModByAddress or LfGetAddr or LfSetAddr
                 or LfModUnload or LfGetVersion)
        {
            // 4-byte result-only replies (ps2sdk recv size 4 for these).
            mem.Write32(recvBuf, unchecked((uint)result));
        }
        else
        {
            // Module load family: { result, modres }
            mem.Write32(recvBuf, unchecked((uint)result));
            mem.Write32(recvBuf + 4, unchecked((uint)modres));
        }
    }

    /// <summary>
    /// LF_F_MOD_LOAD / LF_F_MG_MOD_LOAD path load (decomp FUN_00000150 / FUN_000001fc).
    /// MG shares the plain path loader; when disc bytes are present SECRMAN classifies
    /// plain ELF vs encrypted (encrypted → clear <see cref="LfErrNotIrx"/>, no fake secrets).
    /// IOPRP/DNAS <c>.IMG</c> containers are parsed (ROMDIR + IOPBTCONF + LoadIrx when ELF).
    /// </summary>
    private int LoadModuleByPath(SystemMemory mem, IopModuleHost iopModules, Cdvd? cdvd, string path,
        out int modres, bool magicGate = false)
    {
        modres = 0;
        if (string.IsNullOrWhiteSpace(path))
            return LfErrNotIrx; // decomp path-check fail → 0xffffff37

        // MODLOAD IsIllegalBootDevice / LOADFILE FUN_00000150: mc/hd/net/dev → 0xFFFFFF37
        if (IopModuleHost.IsIllegalBootDevice(path))
            return IopModuleHost.ModloadErrIllegal;

        string name = StripDevicePrefix(path);
        // "cdrom0:\;1" / empty leaf — path-combine left only the device. Map to the next
        // common boot IRX that is not yet registered so titles (BO2) can advance the
        // module-load table without a perfect short-name expander.
        if (string.IsNullOrEmpty(name) || name is "\\" or "/" or ";1" or "\\;1" or "/;1"
            || name.Trim('\\', '/', ' ', '\0').Length == 0)
        {
            // Path-combine produced device-only. Prefer returning an already-registered
            // boot IRX id (PADMAN etc. are pre-registered by BiosBootHost) so the title's
            // load table advances; inventing a new name would miss the HLE sid bindings.
            foreach (var candidate in new[]
                     {
                         "PADMAN", "LIBSD", "SDRDRV", "IOPFILE", "IOPMEM", "IOPSND",
                         "SIO2MAN", "MCMAN", "MCSERV", "CDVDMAN", "CDVDFSV", "FILEIO"
                     })
            {
                if (iopModules.TryGetModule(candidate, out int cid))
                {
                    modres = 0;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                        Console.Error.WriteLine($"[LOADFILE] empty path → existing \"{candidate}\" id={cid}");
                    return cid;
                }
            }
            return LfErrNotIrx;
        }

        // Basename without directory / version for registry + disc lookup.
        // Handles "\\GOE\\BIN\\FOO.IRX;1", "IOP/FOO.IRX", "FOO.IRX;1".
        string baseName = name.Replace('\\', '/');
        int slash = baseName.LastIndexOf('/');
        if (slash >= 0) baseName = baseName[(slash + 1)..];
        // Drop trailing empty segments from paths like "cdrom0:\;1" (seen when a title's
        // module-name append races an empty prefix under HLE path combine).
        while (baseName.Length > 0 && (baseName[0] == ';' || baseName[0] == '\\' || baseName[0] == '/'))
            baseName = baseName[1..];
        string modKey = baseName;
        int dot = modKey.LastIndexOf('.');
        if (dot > 0) modKey = modKey[..dot];
        int semi = modKey.IndexOf(';');
        if (semi >= 0) modKey = modKey[..semi];

        // Empty basename after strip (e.g. "cdrom0:\;1") — not a real IRX request.
        // Soft-success so titles that hit a bad slot in a load list can advance rather
        // than spin forever on bltz retry of the same empty path.
        if (string.IsNullOrWhiteSpace(modKey))
        {
            modres = 0;
            return iopModules.RegisterModule("_empty_path");
        }

        if (iopModules.TryGetModule(name, out int existingId) ||
            iopModules.TryGetModule(baseName, out existingId) ||
            iopModules.TryGetModule(modKey, out existingId))
        {
            // Proprietary disc IRX: try _start if image present (HLE-owned skipped in helper).
            modres = TryStartLoadedModule(iopModules, existingId);
            return existingId;
        }

        // Prefer real IRX bytes from mounted disc (cdrom0:IOP/FOO.IRX, root FOO.IRX, etc.)
        byte[]? discElf = iopModules.ReadDiscFileBytes(path)
                          ?? iopModules.ReadDiscFileBytes(baseName)
                          ?? iopModules.ReadDiscFileBytes(modKey + ".IRX");
        if (discElf != null)
        {
            // Disc-backed IRX/IMG reads are real ISO traffic (Burnout 3 IOP/* load list).
            cdvd?.NoteHostReadSectors((discElf.Length + 2047) / 2048);

            // IOPRP/DNAS *.IMG — ROMDIR-in-IMG container (not a single IRX).
            // Parse IOPBTCONF / ROMDIR and LoadIrx extractable ELFs (UDNL-class apply).
            if (discElf.Length >= 32 &&
                (modKey.StartsWith("IOPRP", StringComparison.OrdinalIgnoreCase) ||
                 modKey.StartsWith("DNAS", StringComparison.OrdinalIgnoreCase) ||
                 baseName.EndsWith(".IMG", StringComparison.OrdinalIgnoreCase) ||
                 IopExtendedBiosHost.TryParseIopRpContainer(discElf, out _)))
            {
                // MG on an image is still a container apply (not MagicGate IRX body).
                IopExtendedBiosHost.ApplyIopRpImageBytes(iopModules, mem, discElf, modKey, out _);
                modres = 0;
                return iopModules.RegisterModule(modKey);
            }

            // MG_MOD_LOAD: SECRMAN classify — plain ELF passthrough; encrypted → clear fail.
            if (magicGate)
            {
                int secr = IopExtendedBiosHost.ClassifySecrBoot(discElf);
                if (secr == IopExtendedBiosHost.SecrErrCannotDecrypt)
                {
                    // SecrDiskBootFile: "Cannot decrypt" — no MagicGate secrets in HLE.
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                        Console.Error.WriteLine(
                            $"[LOADFILE] MG_MOD_LOAD encrypted/non-ELF reject path=\"{path}\" len={discElf.Length}");
                    return LfErrNotIrx;
                }
                // SecrOk / missing handled below via plain load.
            }

            if (discElf.Length < 52 || discElf[0] != 0x7F || discElf[1] != (byte)'E')
                return LfErrNotIrx;
            try
            {
                var lr = iopModules.LoadIrx(discElf, mem, modKey);
                if (lr.Success && iopModules.TryGetModule(lr.ModuleName, out int mid))
                {
                    // WP-25/31: real R3000 _start for proprietary disc IRX (shared).
                    modres = TryStartLoadedModule(iopModules, mid);
                    return mid;
                }
                // Also try by requested key (LoadIrx nameOverride may uppercase).
                if (lr.Success && iopModules.TryGetModule(modKey, out mid))
                {
                    modres = TryStartLoadedModule(iopModules, mid);
                    return mid;
                }
                return LfErrNotIrx;
            }
            catch
            {
                return LfErrFileIo;
            }
        }

        // No disc bytes: HLE register presence for rom0:/host: probes and BiosBootHost names.
        // Distinct from a proven open failure on a mounted cdrom path with a real ISO.
        // MG with no bytes cannot decrypt — same soft rom0 path as plain for missing host probes;
        // mounted cdrom miss stays -203.
        if (iopModules.DiscVolume != null &&
            path.StartsWith("cdrom", StringComparison.OrdinalIgnoreCase))
        {
            // Soft-register well-known names so a missing file still does not infinite-spin
            // titles that only probe presence after a partial IOPRP handoff.
            if (modKey.Length >= 3 &&
                (modKey.StartsWith("IOPRP", StringComparison.OrdinalIgnoreCase) ||
                 modKey is "SIO2MAN" or "PADMAN" or "MCMAN" or "MCSERV" or "LIBSD" or "SDRDRV"
                     or "IOPFILE" or "IOPMEM" or "IOPSND" or "FILEIO" or "LOADFILE"))
            {
                modres = 0;
                return iopModules.RegisterModule(modKey);
            }
            return LfErrFileNotFound;
        }

        return iopModules.RegisterModule(modKey.Length > 0 ? modKey : name);
    }

    /// <summary>
    /// Stack/SCE modules still answered by C# HLE — incomplete R3000 _start clobbers coexistence.
    /// Disc proprietary IRX (GTFSCDVD, LGDEVW, PL2303, 989nomid, B3ROUTE, …) still run.
    /// Force all: DETPS2_LOADFILE_START_ALL=1. Disable all: DETPS2_LOADFILE_START_IRX=0.
    /// </summary>
    private static readonly HashSet<string> LoadFileHleOwnedSkipStart = new(StringComparer.OrdinalIgnoreCase)
    {
        "SYSMEM", "LOADCORE", "HEAPLIB", "EXCEPMAN", "INTRMAN", "INTRMANP", "INTRMANS",
        "TIMEMAN", "TIMEMANI", "TIMEMANS", "SSBUSC", "EECONF",
        "THREADMAN", "VBLANK", "VBLANK_A", "VBLANK_B",
        "IOMAN", "MODLOAD", "ROMDRV", "STDIO", "SYSCLIB", "IGREETING",
        "SIFMAN", "SIFCMD", "SIFINIT", "EESYNC", "REBOOT",
        "FILEIO", "LOADFILE", "CDVDMAN", "CDVDFSV",
        "MCMAN", "MCSERV", "PADMAN", "SIO2MAN", "LIBSD",
    };

    /// <summary>Run R3000 _start for proprietary disc IRX; return LOADFILE modres (WP-25/31).</summary>
    private int TryStartLoadedModule(IopModuleHost iopModules, int mid)
    {
        if (mid < 0) return 0;
        if (!iopModules.TryGetIrx(mid, out var irx) || !irx.HasImage || irx.Entry == 0)
            return irx?.LastModRes ?? 0;
        if (irx.EntryExecuted && irx.LastEntryInstructions > 0)
            return irx.LastModRes;
        if (_host == null || !IopModuleHost.IsLiteralIrxEnabled)
            return irx.LastModRes;
        if (string.Equals(Environment.GetEnvironmentVariable("DETPS2_LOADFILE_START_IRX"), "0", StringComparison.Ordinal))
            return irx.LastModRes;
        bool startAll = string.Equals(Environment.GetEnvironmentVariable("DETPS2_LOADFILE_START_ALL"), "1", StringComparison.Ordinal);
        if (!startAll && LoadFileHleOwnedSkipStart.Contains(irx.Name))
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1"
                || Environment.GetEnvironmentVariable("DETPS2_TRACE_LITERAL_IRX") == "1")
                Console.Error.WriteLine($"[LOADFILE] StartLoadedModule SKIP hle-owned name={irx.Name} id={mid}");
            return irx.LastModRes;
        }
        // 50k left MC2_D/DS2U_D/989NOMID mid-_start on GoW IOP_MOD list. 100k gives more
        // room without multi-150k host stalls. DETPS2_LOADFILE_START_INSNS overrides.
        ulong maxInsn = 100_000;
        string? maxEnv = Environment.GetEnvironmentVariable("DETPS2_LOADFILE_START_INSNS");
        if (!string.IsNullOrEmpty(maxEnv) && ulong.TryParse(maxEnv, out ulong envMax) && envMax > 0)
            maxInsn = envMax;
        if (string.Equals(irx.Name, "INTRMANP", StringComparison.OrdinalIgnoreCase))
            _host.Memory.IopWrite32(0xBF801450, SystemMemory.IopIoIntrmanConfigDefault);
        else if (string.Equals(irx.Name, "SIFMAN", StringComparison.OrdinalIgnoreCase))
            _host.Memory.IopWrite32(0xBF801450, 0);
        var run = iopModules.StartLoadedModule(_host, mid, maxInsn);
        int replyModres;
        if (run.ReturnedToSentinel)
            replyModres = run.ModRes;
        else if (run.Success)
        {
            replyModres = IopModuleHost.ModuleResidentEnd;
            irx.LastModRes = replyModres;
        }
        else
            replyModres = irx.LastModRes;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1"
            || Environment.GetEnvironmentVariable("DETPS2_TRACE_LITERAL_IRX") == "1")
            Console.Error.WriteLine(
                $"[LOADFILE] StartLoadedModule name={irx.Name} id={mid} ok={run.Success} " +
                $"insns={run.InstructionsExecuted} modres={replyModres} (v0={run.ModRes} ret={run.ReturnedToSentinel}) msg={run.Message}");
        return replyModres;
    }


    /// <summary>LF_F_ELF_LOAD / LF_F_MG_ELF_LOAD — load EE ELF, return epc/gp (decomp FUN_00000240).</summary>
    /// <summary>
    /// LF_F_ELF_LOAD / LF_F_MG_ELF_LOAD — load EE ELF, return epc/gp (decomp FUN_00000240 / FUN_000002fc).
    /// MG shares plain path when payload is ELF; encrypted non-ELF → epc=0 load miss (no MG secrets).
    /// </summary>
    private int LoadElfByPath(SystemMemory mem, IopModuleHost iopModules, string path, string secname,
        out uint epc, out uint gp, bool magicGate = false)
    {
        _ = secname;
        epc = 0;
        gp = 0;
        if (string.IsNullOrWhiteSpace(path))
            return LfErrFileNotFound; // Cannot openfile shape

        byte[]? elfBytes = iopModules.ReadDiscFileBytes(path);
        if (elfBytes == null || elfBytes.Length < 52)
        {
            // No disc file — cannot invent EE entry points; epc=0 means load miss to the client.
            // Path open failure on real LOADFILE → -203; decomp also uses -204 for some path checks.
            if (iopModules.DiscVolume != null &&
                path.StartsWith("cdrom", StringComparison.OrdinalIgnoreCase))
                return LfErrFileNotFound;
            return 0; // epc stays 0 → client -SCE_ELOADMISS
        }

        if (magicGate)
        {
            int secr = IopExtendedBiosHost.ClassifySecrBoot(elfBytes);
            if (secr == IopExtendedBiosHost.SecrErrCannotDecrypt)
            {
                // FUN_000002fc on decrypt fail → epc=0 (client -SCE_ELOADMISS)
                epc = 0;
                gp = 0;
                return 0;
            }
        }

        if (elfBytes[0] != 0x7F || elfBytes[1] != (byte)'E' || elfBytes[2] != (byte)'L' || elfBytes[3] != (byte)'F')
            return LfErrNotIrx;

        try
        {
            var lr = ElfLoader.LoadElfDetailed(elfBytes, mem);
            epc = (uint)lr.Entry;
            gp = (uint)lr.Gp;
            // decomp sets DAT_00001e88 = 0 (sp) on success; we leave sp=0.
            return epc != 0 ? 0 : 0; // success path with epc may still be 0 for truncated fixtures
        }
        catch
        {
            epc = 0;
            gp = 0;
            return 0; // load fail → epc=0 (FUN_00000240)
        }
    }

    private static int IopSetVal(SystemMemory mem, uint argBuf)
    {
        if (argBuf == 0) return 0;
        uint iopAddr = mem.Read32(argBuf);
        int type = (int)mem.Read32(argBuf + 4);
        uint eeAddr = ResolveIopPointer(iopAddr);
        // val union at +8
        switch (type)
        {
            case 0: // LF_VAL_BYTE
                mem.Write8(eeAddr, mem.Read8(argBuf + 8));
                break;
            case 1: // LF_VAL_SHORT
            {
                byte lo = mem.Read8(argBuf + 8);
                byte hi = mem.Read8(argBuf + 9);
                mem.Write8(eeAddr, lo);
                mem.Write8(eeAddr + 1, hi);
                break;
            }
            case 2: // LF_VAL_LONG
                mem.Write32(eeAddr, mem.Read32(argBuf + 8));
                break;
        }
        return 0; // decomp FUN_00000420 always DAT_00001e80 = 0
    }

    private static int IopGetVal(SystemMemory mem, uint argBuf)
    {
        if (argBuf == 0) return 0;
        uint iopAddr = mem.Read32(argBuf);
        int type = (int)mem.Read32(argBuf + 4);
        uint eeAddr = ResolveIopPointer(iopAddr);
        return type switch
        {
            0 => mem.Read8(eeAddr),
            1 => mem.Read8(eeAddr) | (mem.Read8(eeAddr + 1) << 8),
            2 => unchecked((int)mem.Read32(eeAddr)),
            _ => 0
        };
    }

    /// <summary>Map an IOP-side pointer (physical, KSEG, or EE-mapped 0x1Cxxxxxx) to EE bus for
    /// <see cref="SystemMemory"/> access.</summary>
    private static uint ResolveIopPointer(uint addr)
    {
        if (addr >= SystemMemory.IOP_RAM_BASE &&
            addr < SystemMemory.IOP_RAM_BASE + SystemMemory.IOP_RAM_SIZE)
            return addr;
        uint phys = addr & 0x1FFFFFFFu;
        if (phys < SystemMemory.IOP_RAM_SIZE)
            return SystemMemory.IOP_RAM_BASE + phys;
        return addr;
    }

    /// <summary>Copies a generous window of real module bytes out of IOP RAM starting at
    /// <paramref name="ptr"/> and loads it through the existing, Phase-1/2-verified
    /// IrxLoader/IopModuleHost pipeline. Returns a positive module id on success, or a
    /// LOADFILE-shaped negative error (bad pointer / not IRX / load failure).</summary>
    private int TryLoadModuleFromMemory(SystemMemory mem, IopModuleHost iopModules, uint ptr, string? nameOverride)
    {
        // Accept EE-mapped IOP window or raw IOP physical (SifExecModuleBuffer uses SifAllocIopHeap
        // addresses which are IOP physical with 0x1C000000 EE map in this HLE).
        uint resolved = ResolveIopPointer(ptr);
        if (resolved < SystemMemory.IOP_RAM_BASE) return LfErrNotIrx;
        uint offset = resolved - SystemMemory.IOP_RAM_BASE;
        if (offset >= SystemMemory.IOP_RAM_SIZE) return LfErrNotIrx;
        int len = Math.Min(LfModuleCopyCap, SystemMemory.IOP_RAM_SIZE - (int)offset);
        var span = mem.GetIopRamSpan().Slice((int)offset, len);
        if (len < 4 || span[0] != 0x7F || span[1] != (byte)'E')
            return LfErrNotIrx;
        byte[] elf = span.ToArray();
        try
        {
            var r = iopModules.LoadIrx(elf, mem, nameOverride);
            return r.Success ? (iopModules.TryGetModule(r.ModuleName, out int id) ? id : 1) : LfErrNotIrx;
        }
        catch
        {
            return LfErrNotIrx;
        }
    }

    private static string ReadCString(SystemMemory mem, uint addr, int maxLen)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < maxLen; i++)
        {
            byte b = mem.Read8(addr + (uint)i);
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    /// <summary>Strips a PS2 device prefix ("cdrom0:", "rom0:", "host0:", etc.) and any
    /// trailing ";version" suffix, so the remaining path component reaches IopModuleHost's own
    /// NormalizeName (which only strips slashes/extension, not the colon-delimited device
    /// scheme) as a plain, comparable module name.</summary>
    private static string StripDevicePrefix(string path)
    {
        int colon = path.IndexOf(':');
        if (colon >= 0) path = path[(colon + 1)..];
        int semi = path.IndexOf(';');
        if (semi >= 0) path = path[..semi];
        return path;
    }

    /// <summary>
    /// BIOS/ps2sdk iopheap RPC (sid=<see cref="SidSysmem"/> = 0x80000003).
    /// Client: <c>ee/kernel/src/iopheap.c</c> (<c>SifAllocIopHeap</c>/<c>SifFreeIopHeap</c>/<c>SifLoadIopHeap</c>).
    /// IOP backend contract: SYSMEM <c>AllocSysMemory(ALLOC_FIRST, size, NULL)</c> /
    /// <c>FreeSysMemory</c> (256-byte pages, NULL on OOM, Free returns 0/-1).
    /// </summary>
    private int HandleSysmem(SystemMemory mem, IopModuleHost? iopModules, uint fno, uint argBuf, uint recvBuf)
    {
        SysmemOps++;
        switch (fno)
        {
            case SysmemAlloc:
            {
                // arg: int size (4B). Reply: u32 addr (EE-mapped IOP window) or 0 = NULL.
                uint reqSize = argBuf != 0 ? mem.Read32(argBuf) : 0;
                uint addr = AllocIopHeap(reqSize);
                // Some clients overwrite their arg union; recv path also gets the value from
                // HandleCall. Mirror into recvBuf when present so dual-buffer callers agree.
                if (recvBuf != 0) mem.Write32(recvBuf, addr);
                if (argBuf != 0) mem.Write32(argBuf, addr);
                return unchecked((int)addr);
            }
            case SysmemFree:
            {
                // arg: void *addr. Reply: int result (0 ok, -1 fail).
                uint addr = argBuf != 0 ? mem.Read32(argBuf) : 0;
                int rc = FreeIopHeap(addr);
                if (recvBuf != 0) mem.Write32(recvBuf, unchecked((uint)rc));
                if (argBuf != 0) mem.Write32(argBuf, unchecked((uint)rc));
                return rc;
            }
            case SysmemLoad:
            {
                // struct _iop_load_heap_arg { union { void *addr; int result; } p; char path[252]; }
                // sceSifCallRpc send=sizeof(arg), recv=4 → result overwrites p.
                int rc = LoadIopHeap(mem, iopModules, argBuf);
                if (recvBuf != 0) mem.Write32(recvBuf, unchecked((uint)rc));
                if (argBuf != 0) mem.Write32(argBuf, unchecked((uint)rc));
                return rc;
            }
            default:
                // Unknown fno: soft-success 1 (generic DetPS2 unknown-service convention).
                return 1;
        }
    }

    /// <summary>
    /// Page-align size the way real AllocSysMemory does: <c>((size+255)&gt;&gt;8)&lt;&lt;8</c>.
    /// Size 0 → 0 (NULL). First-fit holes, then bump within [IopHeapBase, IopHeapLimit).
    /// Returns EE-mapped IOP address (0x1Cxxxxxx) so EE code can DMA via existing helpers;
    /// <see cref="ResolveIopPointer"/> / MOD_BUF_LOAD also accept bare IOP physical.
    /// </summary>
    private uint AllocIopHeap(uint reqSize)
    {
        // Real: v3 = (size + 255) >> 8; if (!v3) return 0;
        uint pages = (reqSize + (SysmemPageSize - 1)) / SysmemPageSize;
        if (pages == 0) return 0;
        uint aligned = pages * SysmemPageSize;

        // First-fit among freed holes.
        for (int i = 0; i < _iopHeapHoles.Count; i++)
        {
            var hole = _iopHeapHoles[i];
            if (hole.Size < aligned) continue;
            uint phys = hole.Phys;
            uint rem = hole.Size - aligned;
            _iopHeapHoles.RemoveAt(i);
            if (rem > 0)
                _iopHeapHoles.Insert(i, (phys + aligned, rem));
            _iopHeapLive[phys] = aligned;
            return SystemMemory.IOP_RAM_BASE + phys;
        }

        // Bump from high watermark.
        uint addr = _iopHeapNext;
        if (addr + aligned > IopHeapLimit || addr + aligned < addr)
            return 0; // OOM → NULL
        _iopHeapNext = addr + aligned;
        _iopHeapLive[addr] = aligned;
        return SystemMemory.IOP_RAM_BASE + addr;
    }

    /// <summary>
    /// Free a prior <see cref="AllocIopHeap"/> block. Matches FreeSysMemory: 0 success, -1 if
    /// not page-aligned / not a live block / double-free.
    /// </summary>
    private int FreeIopHeap(uint addr)
    {
        if (addr == 0) return -1;
        uint phys = ToIopHeapPhys(addr);
        if ((phys & (SysmemPageSize - 1)) != 0) return -1;
        if (!_iopHeapLive.TryGetValue(phys, out uint size)) return -1;
        _iopHeapLive.Remove(phys);
        InsertHoleCoalesced(phys, size);
        return 0;
    }

    private void InsertHoleCoalesced(uint phys, uint size)
    {
        // Insert sorted by phys, merge with neighbours.
        int i = 0;
        while (i < _iopHeapHoles.Count && _iopHeapHoles[i].Phys < phys) i++;
        _iopHeapHoles.Insert(i, (phys, size));
        // Merge with previous.
        if (i > 0)
        {
            var prev = _iopHeapHoles[i - 1];
            if (prev.Phys + prev.Size == phys)
            {
                _iopHeapHoles[i - 1] = (prev.Phys, prev.Size + size);
                _iopHeapHoles.RemoveAt(i);
                i--;
                phys = _iopHeapHoles[i].Phys;
                size = _iopHeapHoles[i].Size;
            }
        }
        // Merge with next.
        if (i + 1 < _iopHeapHoles.Count)
        {
            var next = _iopHeapHoles[i + 1];
            if (phys + size == next.Phys)
            {
                _iopHeapHoles[i] = (phys, size + next.Size);
                _iopHeapHoles.RemoveAt(i + 1);
            }
        }
        // Retract bump watermark if the top hole ends at _iopHeapNext.
        if (_iopHeapHoles.Count > 0)
        {
            var last = _iopHeapHoles[^1];
            if (last.Phys + last.Size == _iopHeapNext)
            {
                _iopHeapNext = last.Phys;
                _iopHeapHoles.RemoveAt(_iopHeapHoles.Count - 1);
            }
        }
    }

    private static uint ToIopHeapPhys(uint addr)
    {
        if (addr >= SystemMemory.IOP_RAM_BASE &&
            addr < SystemMemory.IOP_RAM_BASE + (uint)SystemMemory.IOP_RAM_SIZE)
            return addr - SystemMemory.IOP_RAM_BASE;
        return addr & 0x1FFFFFu;
    }

    /// <summary>
    /// SifLoadIopHeap: copy disc/host file bytes into a previously allocated IOP heap buffer.
    /// Result 0 = success; negative = fail. Soft-0 when no disc is bound (boot without media).
    /// </summary>
    private int LoadIopHeap(SystemMemory mem, IopModuleHost? iopModules, uint argBuf)
    {
        if (argBuf == 0) return -1;
        uint dest = mem.Read32(argBuf); // p.addr
        if (dest == 0) return -1;

        // path at +4, max LIH_PATH_MAX = 252
        string path = ReadCString(mem, argBuf + 4, 252);
        if (string.IsNullOrEmpty(path)) return -1;

        if (iopModules == null)
            return 0; // no host → soft success (Dispatch fallback)

        byte[]? data = iopModules.ReadDiscFileBytes(path);
        if (data == null)
        {
            // Missing on mounted disc for cdrom paths → FILE_NOT_FOUND-shaped error.
            if (iopModules.DiscVolume != null &&
                path.StartsWith("cdrom", StringComparison.OrdinalIgnoreCase))
                return LfErrFileNotFound;
            // rom0:/host0:/no disc: soft success so callers that only check "loaded something"
            // do not panic during incomplete BIOS media paths.
            return 0;
        }

        uint eeDest = ResolveIopPointer(dest);
        // Cap write to remaining IOP RAM and to the live block size when known.
        uint phys = ToIopHeapPhys(dest);
        int maxWrite = SystemMemory.IOP_RAM_SIZE - (int)phys;
        if (maxWrite <= 0) return -1;
        if (_iopHeapLive.TryGetValue(phys, out uint blockSize) && blockSize < (uint)maxWrite)
            maxWrite = (int)blockSize;
        int n = Math.Min(data.Length, maxWrite);
        for (int i = 0; i < n; i++)
            mem.Write8(eeDest + (uint)i, data[i]);
        return 0;
    }

    private int Dispatch(SystemMemory mem, Cdvd cdvd, PadInput pad, IopModuleHost iopModules, uint sid, uint fno, uint argBuf, uint recvBuf)
    {
        switch (sid)
        {
            case SidSysmem:
                return HandleSysmem(mem, null, fno, argBuf, recvBuf);

            case SidCdBase:
                // CDVDFSV FUN_00000204 "sceCdInit call" — only registered fno path; mode is
                // *param_2 (arg word 0). Always ends with success marker DAT_000051b0 = 1.
                return HandleCdInit(cdvd, argBuf, mem);

            case SidCdSearchFile:
                return HandleCdSearchFile(mem, cdvd, argBuf, recvBuf);

            case SidCdDiskReady:
            case SidCdDiskReady2:
                return cdvd.DiskReady();

            // NCMD is handled in HandleCall via HandleCdNcmd (multi-word replies). Fallthrough
            // kept only if Dispatch is invoked directly in tests.
            case SidCdNcmd:
                return HandleCdNcmdResult(mem, cdvd, fno, argBuf, recvBuf);

            case SidPad1:
            case SidPad2:
                // NEW PADMAN (0x80000100/101): buffer.command is PAD_RPCCMD_*_NEW (0x01, 0x06..0x12).
                // libpad always uses rpc_number=1; real command is arg word 0.
                return HandlePad(mem, pad, fno, argBuf, recvBuf, oldStyle: false);

            case SidPadOld1:
                // rom0:PADMAN (0x8000010f): buffer.command is PAD_RPCCMD_*_OLD (0x8000010x).
                // Ghidra FUN_0000655c — see docs/bios-ports/PADMAN.md.
                return HandlePad(mem, pad, fno, argBuf, recvBuf, oldStyle: true);

            case SidPadOld2:
                // FUN_00006744: "Extend Service : this service is not support\n" — return buffer as-is.
                // Bind must succeed so padInit's second-client wait completes; calls are no-ops.
                return 0;

            case SidMcServ:
                // libmc MCSERV (sid=0x80000400). Function numbers from ps2sdk libmc:
                // 0x00 init, 0x01 getInfo, 0x02 open, 0x03 close, 0x04 seek, 0x05 read,
                // 0x06 write, 0x07 flush, 0x0A format, 0x0C delete, 0x0D getDir, …
                return HandleMcServ(mem, iopModules, fno, argBuf, recvBuf);

            case SidSndf:
                switch (fno & 0xFF00)
                {
                    case SndfInitAudio:
                    {
                        // Real handler: return -1 only if arg word 0 is literally zero,
                        // else 0 (success). We don't independently know what the game sent
                        // as word 0, but a shipped title's own init call sending 0 there
                        // (and thus intentionally failing its own audio init) is not a
                        // realistic scenario, so treat it as present/nonzero.
                        uint w0 = argBuf != 0 ? mem.Read32(argBuf) : 1;
                        return w0 == 0 ? -1 : 0;
                    }
                    default:
                        // Other SNDF_Driver commands (0xFE00/0xFD00/0xFC00/0xFB00/0x1B00/
                        // 0x1700/0xF900/0x0E00/0x0A00/0x0800/0x0700/0x1E00/0x0300/0x0200/
                        // 0x1600/0x0100 per the real dispatch table at real vaddr 0xBB80)
                        // aren't independently traced yet. 0 matches this module's own
                        // observed success convention better than this file's usual 1.
                        return 0;
                }

            case SidSdReg:
                return 0; // see SidSdReg's declaration comment

            case SidSfsv:
                return 0; // see SidSfsv's declaration comment

            case SidSnProdg:
                // SN ProDG debug RPC: accept all fnos as success (0). No T10000 present.
                return 0;

            case SidDbcMan:
                // dbcman.irx (libdbc). fno often equals sid|0x63 style version/init.
                // libdbc prints "Module version mismatch [libdbc.a = %d.%02x, dbcman.irx = %d.%02x]"
                // when IRX major/minor don't match the static lib — write 3.10 (3, 0x10).
                return HandleDbcMan(mem, fno, argBuf, recvBuf);

            case SidLgDev:
                return HandleLgDev(mem, fno, argBuf, sendSize: 0, recvBuf, recvSize: 0x240);

            case Sid989Snd:
            case Sid989Snd2:
                // Prefer dedicated HandleCall path (recvSize known). Dispatch fallback
                // still paints the 0xFFFFFFFF sentinel shape for unit tests.
                return Handle989Snd(mem, fno, argBuf, recvBuf, recvSize: 12);

            case SidGtfsStg:
            case SidB3Aux:
                return HandleGtfs(mem, cdvd, iopModules, sid, fno, argBuf, sendSize: 0, recvBuf, recvSize: 0x40);

            case SidMwFileMain:
            case SidMwFileAux:
                return HandleMwFile(mem, iopModules, cdvd, sid, fno, argBuf, sendSize: 0, recvBuf, recvSize: 4);

            case SidMsl:
            case SidMslMfl:
                return HandleMsl(mem, iopModules, cdvd, sid, fno, argBuf, sendSize: 0, recvBuf, recvSize: 4);

            default:
                if (IsBurnout3GtfsSid(sid))
                    return HandleGtfs(mem, cdvd, iopModules, sid, fno, argBuf, sendSize: 0, recvBuf, recvSize: 0x40);
                if (IsMwFileSid(sid))
                    return HandleMwFile(mem, iopModules, cdvd, sid, fno, argBuf, sendSize: 0, recvBuf, recvSize: 4);
                if (IsMslFamilySid(sid))
                    return HandleMsl(mem, iopModules, cdvd, sid, fno, argBuf, sendSize: 0, recvBuf, recvSize: 4);
                // Prefer 0 (common IOP "OK") over 1. Callers that treat non-zero as success
                // still pass with our specialized handlers above; 989snd treats 1 as fail.
                UnknownServiceCalls++;
                return 0;
        }
    }

    /// <summary>
    /// Criterion GTFS / Burnout 3 stage RPC HLE (GTFSCDVD.IRX / sid <c>"STG\\0"</c>).
    /// Live B3: fno=1 recv@0x4E2730 send=48, fno=3 recv@0x66E080 send=64, fno=5 send=16
    /// with dest in w0 (<c>0x0067D880</c>) + handle in w1. Soft-only / 256KiB-cap left the
    /// front-end incomplete; multi-chunk DMA of the full TXD is required for menu workers.
    /// </summary>
    private static int _gtfsStageHedFd = -1;
    private static uint _gtfsStageHedSize;
    private static int _gtfsFrontendFd = -1;
    private static uint _gtfsFrontendSize;
    private static bool _gtfsTocOpened;
    private static int _gtfsLastPathFd = -1;
    private static uint _gtfsLastPathSize;
    private static uint _gtfsReadOffset; // multi-chunk cursor into last opened path
    private static uint _gtfsLastDmaDest;
    private static uint _gtfsTotalDmaBytes;

    /// <summary>Reset GTFS host open state (new disc / new Ps2System).</summary>
    public static void ResetGtfsState()
    {
        _gtfsStageHedFd = -1;
        _gtfsStageHedSize = 0;
        _gtfsFrontendFd = -1;
        _gtfsFrontendSize = 0;
        _gtfsTocOpened = false;
        _gtfsLastPathFd = -1;
        _gtfsLastPathSize = 0;
        _gtfsReadOffset = 0;
        _gtfsLastDmaDest = 0;
        _gtfsTotalDmaBytes = 0;
    }

    private static int HandleGtfs(SystemMemory mem, Cdvd cdvd, IopModuleHost iopModules,
        uint sid, uint fno, uint argBuf, uint sendSize, uint recvBuf, uint recvSize)
    {
        _ = sid;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1" && argBuf != 0 && sendSize > 0)
        {
            uint n = Math.Min(sendSize, 64u);
            var sb = new System.Text.StringBuilder(160);
            sb.Append($"[GTFS] fno=0x{fno:X} send={sendSize} arg=0x{argBuf:X8}:");
            for (uint o = 0; o + 4 <= n; o += 4)
                sb.Append($" {mem.Read32(argBuf + o):X8}");
            string probe = ReadCString(mem, argBuf, 96);
            if (LooksLikeFsPath(probe))
                sb.Append($" path=\"{probe}\"");
            if (sendSize >= 8)
            {
                uint p4 = mem.Read32(argBuf + 4);
                if (IsEeRamPointer(p4))
                {
                    string p = ReadCString(mem, p4, 96);
                    if (LooksLikeFsPath(p)) sb.Append($" path*4=\"{p}\"");
                }
                else
                {
                    string p = ReadCString(mem, argBuf + 4, 96);
                    if (LooksLikeFsPath(p)) sb.Append($" path+4=\"{p}\"");
                }
            }
            Console.Error.WriteLine(sb.ToString());
        }

        // SidB3Aux (0x00150276) residual post-LGDEV: soft-OK only, never DMA into stack dests.
        bool isStg = sid == SidGtfsStg || sid == 0x53465447u;
        if (!isStg)
        {
            if (recvBuf != 0)
            {
                uint limit = recvSize > 0 ? Math.Min(recvSize, 0x40u) : 0x40u;
                for (uint o = 0; o + 4 <= limit; o += 4)
                    mem.Write32(recvBuf + o, 0);
            }
            return 0;
        }

        EnsureGtfsStageAssets(iopModules, cdvd);

        uint openedSize = 0;
        int openedFd = TryGtfsPathOpenOrRead(mem, iopModules, cdvd, fno, argBuf, sendSize,
            recvBuf, recvSize, out openedSize);

        // fno=4 (live B3 thrash after Global open): close / status. Soft-OK with cursor
        // so EE does not spin fno=3↔4 without advancing; do not DMA.
        if (fno == 4)
        {
            uint sz4 = openedSize != 0 ? openedSize
                : (_gtfsLastPathSize != 0 ? _gtfsLastPathSize
                    : (_gtfsFrontendSize != 0 ? _gtfsFrontendSize : _gtfsStageHedSize));
            uint h4 = openedFd >= 0 ? (uint)(1 + (openedFd & 0xFF))
                : (_gtfsLastPathFd >= 0 ? (uint)(1 + (_gtfsLastPathFd & 0xFF)) : 1u);
            if (recvBuf != 0)
            {
                uint limit = recvSize > 0 ? Math.Min(recvSize, 0x40u) : 0x20u;
                for (uint o = 0; o + 4 <= limit; o += 4)
                    mem.Write32(recvBuf + o, 0);
                mem.Write32(recvBuf, 0);
                if (limit >= 8) mem.Write32(recvBuf + 4, h4);
                if (limit >= 12) mem.Write32(recvBuf + 8, sz4);
                if (limit >= 16) mem.Write32(recvBuf + 12, _gtfsReadOffset);
                if (limit >= 20) mem.Write32(recvBuf + 16, _gtfsTotalDmaBytes);
            }
            return 0;
        }

        // fno=5 multi-layout / multi-chunk full TXD (wave-1 256KiB + w0==0 gate blocked live B3).
        uint dmaBytes = 0;
        if (fno == 5 && argBuf != 0 && sendSize >= 8)
            dmaBytes = TryGtfsFno5Dma(mem, cdvd, iopModules, argBuf, sendSize, openedFd, openedSize);

        uint assetSize = openedSize != 0 ? openedSize
            : (_gtfsLastPathSize != 0 ? _gtfsLastPathSize
                : (_gtfsStageHedSize != 0 ? _gtfsStageHedSize
                    : (_gtfsFrontendSize != 0 ? _gtfsFrontendSize : 0x10000u)));
        uint handle = 1;
        if (openedFd >= 0)
            handle = (uint)(1 + (openedFd & 0xFF));
        else if (_gtfsLastPathFd >= 0)
            handle = (uint)(1 + (_gtfsLastPathFd & 0xFF));
        else if (_gtfsStageHedFd >= 0)
            handle = (uint)(1 + (_gtfsStageHedFd & 0xFF));

        if (recvBuf != 0)
        {
            uint limit = recvSize > 0 ? Math.Min(recvSize, 0x100u) : 0x40u;
            for (uint o = 0; o + 4 <= limit; o += 4)
                mem.Write32(recvBuf + o, 0);
            mem.Write32(recvBuf, 0);
            if (limit >= 8)
                mem.Write32(recvBuf + 4, handle);
            if (limit >= 12)
                mem.Write32(recvBuf + 8, assetSize);
            if (limit >= 16)
                mem.Write32(recvBuf + 12, dmaBytes != 0 ? dmaBytes : 1u);
            if (limit >= 20)
                mem.Write32(recvBuf + 16, (assetSize + 2047) / 2048);
            if ((fno == 1 || fno == 3) && limit >= 8)
            {
                mem.Write32(recvBuf, 0);
                mem.Write32(recvBuf + 4, handle);
                if (limit >= 12)
                    mem.Write32(recvBuf + 8, assetSize);
            }
            if (fno == 5 && limit >= 12)
            {
                mem.Write32(recvBuf, 0);
                mem.Write32(recvBuf + 4, handle);
                mem.Write32(recvBuf + 8, dmaBytes != 0 ? dmaBytes : assetSize);
                if (limit >= 16)
                    mem.Write32(recvBuf + 12, _gtfsReadOffset);
                if (limit >= 20)
                    mem.Write32(recvBuf + 16, _gtfsTotalDmaBytes);
            }
        }
        return 0;
    }

    /// <summary>
    /// fno=5 multi-layout DMA. Transfers remaining file bytes in ≤2MiB host chunks until
    /// full TXD is in EE (or dest+size exhausts). Advances <see cref="_gtfsReadOffset"/>.
    /// </summary>
    private static uint TryGtfsFno5Dma(SystemMemory mem, Cdvd cdvd, IopModuleHost iopModules,
        uint argBuf, uint sendSize, int openedFd, uint openedSize)
    {
        uint w0 = mem.Read32(argBuf + 0);
        uint w1 = sendSize >= 8 ? mem.Read32(argBuf + 4) : 0;
        uint w2 = sendSize >= 12 ? mem.Read32(argBuf + 8) : 0;
        uint w3 = sendSize >= 16 ? mem.Read32(argBuf + 12) : 0;

        uint dest = 0, size = 0, offset = 0;
        // Layout B (live): dest in w0, handle in w1, optional size/offset in w2/w3.
        // Live B3 fno=5 after Global open: w=[0, handle, dest, pathResidue] where w3 is
        // ASCII "txd\0" (0x00647874) — never treat printable path residue as size/offset.
        if (IsEeRamPointer(w0) && (w0 & 0x1FFFFFFFu) < 0x01E00000u)
        {
            dest = w0 & 0x1FFFFFFFu;
            if (w2 is > 0 and <= 0x02000000u && !IsEeRamPointer(w2) && !LooksLikeAsciiResidue(w2))
                size = w2;
            else if (w3 is > 0 and <= 0x02000000u && !IsEeRamPointer(w3) && !LooksLikeAsciiResidue(w3))
                size = w3;
            if (w2 < 0x01000000u && w2 != size && !IsEeRamPointer(w2) && w2 > 0x100u
                && !LooksLikeAsciiResidue(w2))
                offset = w2;
            if (w3 < 0x01000000u && w3 != size && !IsEeRamPointer(w3) && w3 > 0x100u
                && offset == 0 && !LooksLikeAsciiResidue(w3))
                offset = w3;
        }
        else if (w0 == 0 && IsEeRamPointer(w2) && (w2 & 0x1FFFFFFFu) < 0x01E00000u)
        {
            dest = w2 & 0x1FFFFFFFu;
            if (w3 is > 0 and <= 0x02000000u && !LooksLikeAsciiResidue(w3))
                size = w3;
        }
        else if (w0 < 0x01000000u && w1 is > 0 and <= 0x02000000u
                 && IsEeRamPointer(w2) && (w2 & 0x1FFFFFFFu) < 0x01E00000u
                 && !LooksLikeAsciiResidue(w1))
        {
            offset = w0;
            size = w1;
            dest = w2 & 0x1FFFFFFFu;
        }
        else if (IsEeRamPointer(w0) && w1 is > 0x100 and <= 0x02000000u
                 && !IsEeRamPointer(w1) && !LooksLikeAsciiResidue(w1))
        {
            dest = w0 & 0x1FFFFFFFu;
            size = w1;
            if (w2 < 0x01000000u && !IsEeRamPointer(w2) && !LooksLikeAsciiResidue(w2))
                offset = w2;
        }

        if (dest == 0 || dest >= 0x01E00000u)
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[GTFS] fno=5 skip bad dest w=[{w0:X8} {w1:X8} {w2:X8} {w3:X8}]");
            return 0;
        }

        int fd = _gtfsLastPathFd >= 0 ? _gtfsLastPathFd
            : (openedFd >= 0 ? openedFd
                : (_gtfsFrontendFd >= 0 ? _gtfsFrontendFd : _gtfsStageHedFd));
        uint maxSz = _gtfsLastPathSize != 0 ? _gtfsLastPathSize
            : (openedSize != 0 ? openedSize
                : (_gtfsFrontendSize != 0 ? _gtfsFrontendSize
                    : (_gtfsStageHedSize != 0 ? _gtfsStageHedSize : 0x10000u)));
        if (fd < 0 || maxSz == 0)
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[GTFS] fno=5 no file fd={fd} maxSz={maxSz} lastFd={_gtfsLastPathFd} " +
                    $"fe={_gtfsFrontendFd} hed={_gtfsStageHedFd} " +
                    $"w=[{w0:X8} {w1:X8} {w2:X8} {w3:X8}]");
            return 0;
        }

        // After full Global.txd DMA, EE may fno=5 a new dest for FRONTEND without re-open.
        // SHARED: if cursor exhausted on last path and dest is a fresh EE buffer, arm FRONTEND.
        if (_gtfsFrontendFd >= 0 && _gtfsFrontendSize > 0
            && _gtfsReadOffset >= maxSz && maxSz > 0
            && dest != _gtfsLastDmaDest
            && dest != (_gtfsLastDmaDest + _gtfsReadOffset)
            && fd != _gtfsFrontendFd)
        {
            fd = _gtfsFrontendFd;
            maxSz = _gtfsFrontendSize;
            _gtfsLastPathFd = _gtfsFrontendFd;
            _gtfsLastPathSize = _gtfsFrontendSize;
            _gtfsReadOffset = 0;
            offset = 0;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[GTFS] fno=5 arm FRONTEND stream after full prior TXD dest=0x{dest:X8} " +
                    $"feSize={_gtfsFrontendSize}");
        }

        if (offset == 0 && _gtfsReadOffset > 0 && _gtfsReadOffset < maxSz
            && (_gtfsLastDmaDest == 0 || dest == _gtfsLastDmaDest
                || dest == _gtfsLastDmaDest + _gtfsReadOffset))
            offset = _gtfsReadOffset;
        if (offset == 0 && dest == _gtfsLastDmaDest && _gtfsReadOffset > 0)
            offset = _gtfsReadOffset;

        uint remaining = maxSz > offset ? maxSz - offset : 0;
        if (remaining == 0) return 0;
        // size==0 → remaining (full rest of TXD / FRONTEND multi-chunk).
        uint want = size != 0 ? Math.Min(size, remaining) : remaining;
        want = Math.Min(want, remaining);
        want = Math.Min(want, (uint)SystemMemory.RDRAM_SIZE - dest);
        if (want == 0) return 0;

        uint total = GtfsDmaChunks(mem, cdvd, iopModules, fd, dest, offset, want);
        if (total > 0)
        {
            _gtfsLastDmaDest = dest;
            _gtfsReadOffset = offset + total;
            _gtfsTotalDmaBytes += total;
            // After completing a non-FRONTEND file (live: full Global.txd), proactively rebind
            // last-path to FRONTEND so the next EE fno=3/5 (or pathless fno=5 with fresh dest)
            // streams FRONTEND without a stale Global cursor. SHARED second-path arm (B3 w9).
            if (_gtfsReadOffset >= maxSz && _gtfsFrontendFd >= 0
                && fd != _gtfsFrontendFd
                && (maxSz == _gtfsLastPathSize || maxSz > 0x10000u))
            {
                _gtfsLastPathFd = _gtfsFrontendFd;
                _gtfsLastPathSize = _gtfsFrontendSize;
                _gtfsReadOffset = 0;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine(
                        $"[GTFS] fno=5 arm FRONTEND stream after full prior TXD dest=0x{dest:X8} " +
                        $"priorSize={maxSz} feSize={_gtfsFrontendSize} totalDma={_gtfsTotalDmaBytes}");
            }
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[GTFS] fno=5 DMA fd={fd} -> 0x{dest:X8} off=0x{offset:X} n={total} " +
                    $"file={maxSz} cursor=0x{_gtfsReadOffset:X} totalDma={_gtfsTotalDmaBytes} " +
                    $"w=[{w0:X8} {w1:X8} {w2:X8} {w3:X8}]");
        }
        return total;
    }

    /// <summary>
    /// True when a word looks like leftover C-string path bytes (e.g. live fno=5 w3=
    /// <c>0x00647874</c> = "txd\\0") rather than a size/offset.
    /// </summary>
    private static bool LooksLikeAsciiResidue(uint w)
    {
        int printable = 0;
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)((w >> (8 * i)) & 0xFF);
            if (b == 0) continue;
            if (b is >= 0x20 and <= 0x7E) printable++;
            else return false;
        }
        return printable >= 3;
    }

    /// <summary>
    /// Host-read into EE in ≤2MiB loops so one fno=5 can complete a multi-MB TXD.
    /// </summary>
    private static uint GtfsDmaChunks(SystemMemory mem, Cdvd cdvd, IopModuleHost iopModules,
        int fd, uint dest, uint fileOff, uint want)
    {
        uint done = 0;
        const uint HostChunk = 2u * 1024 * 1024;
        while (done < want)
        {
            uint chunk = Math.Min(HostChunk, want - done);
            if (!iopModules.TryReadOpenFileBytes(fd, (int)(fileOff + done), (int)chunk, out byte[]? data)
                || data == null || data.Length == 0)
                break;
            for (int i = 0; i < data.Length; i++)
                mem.Write8(dest + done + (uint)i, data[i]);
            done += (uint)data.Length;
            if (data.Length < (int)chunk) break;
        }
        if (done > 0)
            cdvd.NoteHostReadSectors((int)((done + 2047) / 2048));
        return done;
    }

    /// <summary>
    /// Open <c>DATA/STAGEHED.BIN</c> + <c>DATA/FRONTEND.TXD</c> once via FILEIO host.
    /// Counts real ISO sectors toward <see cref="Cdvd.SectorsRead"/> so telemetry leaves IRX-only 425.
    /// </summary>
    private static void EnsureGtfsStageAssets(IopModuleHost iopModules, Cdvd cdvd)
    {
        if (_gtfsTocOpened) return;
        _gtfsTocOpened = true;

        // STAGEHED — stage/header TOC used by Criterion front-end boot.
        foreach (string p in new[]
                 {
                     @"cdrom0:\DATA\STAGEHED.BIN;1",
                     @"cdrom0:\DATA\STAGEHED.BIN",
                     "DATA/STAGEHED.BIN",
                     "STAGEHED.BIN",
                 })
        {
            int fd = iopModules.FileOpen(p);
            if (fd < 0) continue;
            _gtfsStageHedFd = fd;
            if (iopModules.TryGetOpenFileSize(fd, out uint sz) && sz > 0)
            {
                _gtfsStageHedSize = sz;
                cdvd.NoteHostReadSectors((int)Math.Min((sz + 2047) / 2048, 4096));
            }
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[GTFS] open STAGEHED path=\"{p}\" fd={fd} size={_gtfsStageHedSize}");
            break;
        }

        // FRONTEND.TXD — main menu textures (large; count sectors, stream don't preload full).
        foreach (string p in new[]
                 {
                     @"cdrom0:\DATA\FRONTEND.TXD;1",
                     @"cdrom0:\DATA\FRONTEND.TXD",
                     "DATA/FRONTEND.TXD",
                     "FRONTEND.TXD",
                 })
        {
            int fd = iopModules.FileOpen(p);
            if (fd < 0) continue;
            _gtfsFrontendFd = fd;
            if (iopModules.TryGetOpenFileSize(fd, out uint sz) && sz > 0)
            {
                _gtfsFrontendSize = sz;
                // Count a slice at open so cdvd advances without loading 8MB into RAM twice.
                cdvd.NoteHostReadSectors((int)Math.Min((sz + 2047) / 2048, 512));
            }
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[GTFS] open FRONTEND path=\"{p}\" fd={fd} size={_gtfsFrontendSize}");
            break;
        }

        // HEADUS.BIN — localized menu strings (ONLINE/CRASH/RACE …).
        foreach (string p in new[]
                 {
                     @"cdrom0:\DATA\HEADUS.BIN;1",
                     @"cdrom0:\DATA\HEADUS.BIN",
                     "DATA/HEADUS.BIN",
                 })
        {
            int fd = iopModules.FileOpen(p);
            if (fd < 0) continue;
            if (iopModules.TryGetOpenFileSize(fd, out uint sz) && sz > 0)
                cdvd.NoteHostReadSectors((int)Math.Min((sz + 2047) / 2048, 64));
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[GTFS] open HEADUS path=\"{p}\" fd={fd} size={sz}");
            break;
        }
    }

    /// <summary>
    /// If send buffer encodes a disc path (+ optional EE dest), open/read through FILEIO host.
    /// Live B3 fno=3: inline <c>Data\Global.txd</c> at arg+0 (no dest — status/size in recv).
    /// Returns fd (≥0) and size via <paramref name="openedSize"/>.
    /// </summary>
    private static int TryGtfsPathOpenOrRead(SystemMemory mem, IopModuleHost iopModules, Cdvd cdvd,
        uint fno, uint argBuf, uint sendSize, uint recvBuf, uint recvSize, out uint openedSize)
    {
        openedSize = 0;
        if (argBuf == 0 || sendSize < 4) return -1;

        string path = "";
        string p0 = ReadCString(mem, argBuf, 96);
        if (LooksLikeFsPath(p0) && (p0.Contains('.') || p0.Contains(':') || p0.Contains('\\') || p0.Contains('/')))
            path = p0;
        if (path.Length == 0 && sendSize >= 8)
        {
            uint maybePtr = mem.Read32(argBuf + 4);
            if (IsEeRamPointer(maybePtr))
            {
                string pp = ReadCString(mem, maybePtr, 96);
                if (LooksLikeFsPath(pp)) path = pp;
            }
            else
            {
                string p4 = ReadCString(mem, argBuf + 4, 96);
                if (LooksLikeFsPath(p4) && p4.Length >= 4) path = p4;
            }
        }
        // Live B3: after fno=1 multi-open (STAGEHED/FRONTEND/HEADUS), fno=3 often carries
        // inline "Data\Global.txd". When EE leaves the send buffer zero (post residual thrash),
        // still open Criterion's shared TXD so fno=5 can full-DMA Global then arm FRONTEND
        // (deliver residual→STG path). SHARED, path-only — no title plant.
        if (path.Length == 0 && fno == 3 && sendSize >= 16
            && _gtfsFrontendFd >= 0 && _gtfsFrontendSize > 0
            && !(_gtfsLastPathSize > 0 && _gtfsLastPathSize is > 0x10000 and < 0x200000
                 && _gtfsLastPathFd != _gtfsFrontendFd && _gtfsLastPathFd != _gtfsStageHedFd))
        {
            path = @"Data\Global.txd";
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    "[GTFS] fno=3 empty path → default Data\\Global.txd (Criterion shared TXD)");
        }
        if (path.Length == 0) return -1;

        // Dest + size heuristics: scan words for EE pointer + plausible size.
        uint dest = 0, size = 0;
        uint scan = Math.Min(sendSize, 48u);
        for (uint o = 0; o + 8 <= scan; o += 4)
        {
            uint a = mem.Read32(argBuf + o);
            uint b = mem.Read32(argBuf + o + 4);
            // Skip if 'a' is part of an ASCII path (high bytes printable).
            bool aLooksAscii = (a & 0xFF) is >= 0x20 and <= 0x7E
                               && ((a >> 8) & 0xFF) is >= 0x20 and <= 0x7E;
            if (aLooksAscii) continue;
            if (IsEeRamPointer(a) && b is > 0 and <= 0x01000000)
            {
                dest = a & 0x1FFFFFFFu;
                size = b;
                break;
            }
            if (IsEeRamPointer(b) && a is > 0 and <= 0x01000000 && a < 0x00100000)
            {
                size = a;
                dest = b & 0x1FFFFFFFu;
                break;
            }
        }

        // Resolve Criterion relative paths: "Data\Global.txd" → cdrom0:\DATA\GLOBAL.TXD
        string resolved = path;
        if (!path.StartsWith("cdrom", StringComparison.OrdinalIgnoreCase))
        {
            string norm = path.Replace('/', '\\').TrimStart('\\');
            resolved = @"cdrom0:\" + norm;
            if (!resolved.Contains(';'))
                resolved += ";1";
        }

        int fd = iopModules.FileOpen(resolved);
        if (fd < 0)
            fd = iopModules.FileOpen(path);
        if (fd < 0)
        {
            // Try upper-case DATA\ name (ISO 9660).
            string up = resolved.ToUpperInvariant();
            fd = iopModules.FileOpen(up);
        }
        if (fd < 0 && path.Contains('.'))
        {
            // Bare "Global.txd" / "Data\Global.txd" → DATA\GLOBAL.TXD variants.
            string baseName = path.Replace('/', '\\');
            int slash = baseName.LastIndexOf('\\');
            string leaf = slash >= 0 ? baseName[(slash + 1)..] : baseName;
            foreach (string cand in new[]
                     {
                         $@"cdrom0:\DATA\{leaf.ToUpperInvariant()};1",
                         $@"cdrom0:\DATA\{leaf};1",
                         $@"cdrom0:\{leaf.ToUpperInvariant()};1",
                     })
            {
                fd = iopModules.FileOpen(cand);
                if (fd >= 0) { resolved = cand; break; }
            }
        }
        if (fd < 0)
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[GTFS] open FAIL path=\"{path}\" resolved=\"{resolved}\" fno=0x{fno:X}");
            return -1;
        }

        uint fsz = 0;
        if (iopModules.TryGetOpenFileSize(fd, out fsz) && fsz > 0)
        {
            // Same path re-open keeps multi-chunk cursor (FRONTEND multi-call stream).
            bool same = _gtfsLastPathFd == fd && _gtfsLastPathSize == fsz;
            openedSize = fsz;
            _gtfsLastPathFd = fd;
            _gtfsLastPathSize = fsz;
            if (!same)
            {
                _gtfsReadOffset = 0;
                _gtfsTotalDmaBytes = 0;
            }
            // Track FRONTEND / STAGEHED by name so fno=5 can fall back correctly.
            string upPath = (resolved.Length > 0 ? resolved : path).ToUpperInvariant();
            if (upPath.Contains("FRONTEND") && (_gtfsFrontendFd < 0 || _gtfsFrontendSize != fsz))
            {
                _gtfsFrontendFd = fd;
                _gtfsFrontendSize = fsz;
            }
            if (upPath.Contains("STAGEHED") && (_gtfsStageHedFd < 0 || _gtfsStageHedSize != fsz))
            {
                _gtfsStageHedFd = fd;
                _gtfsStageHedSize = fsz;
            }
            // Credit open once per new path (DMA credits separately).
            if (!same)
                cdvd.NoteHostReadSectors((int)Math.Min((fsz + 2047) / 2048, 2048));
        }

        if (dest != 0 && size != 0 && dest < (uint)SystemMemory.RDRAM_SIZE)
        {
            uint want = Math.Min(size, fsz != 0 ? fsz : size);
            want = Math.Min(want, (uint)SystemMemory.RDRAM_SIZE - dest);
            // FRONTEND.TXD is multi-MB — allow full remaining via multi-chunk helper.
            uint total = GtfsDmaChunks(mem, cdvd, iopModules, fd, dest, 0, want);
            if (total > 0)
            {
                _gtfsLastDmaDest = dest;
                _gtfsReadOffset = total;
                _gtfsTotalDmaBytes += total;
                if (recvBuf != 0 && recvSize >= 8)
                {
                    mem.Write32(recvBuf, 0);
                    mem.Write32(recvBuf + 4, total);
                }
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine(
                        $"[GTFS] read path=\"{path}\" -> 0x{dest:X8} n={total} fno=0x{fno:X}");
            }
        }
        else if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
        {
            Console.Error.WriteLine(
                $"[GTFS] open path=\"{path}\" fd={fd} size={fsz} fno=0x{fno:X}");
        }
        return fd;
    }

    /// <summary>dbcman.irx RPC (sid=0x80001300). Version must match libdbc 3.10.</summary>
    public const uint SidDbcMan = 0x80001300;
    /// <summary>
    /// Logitech lgDevice EE client SID (Burnout 3 <c>lgDeviceInit</c> at <c>0x443xxx</c>).
    /// Disc modules: LGDEVW.IRX / LGKBM.IRX / LGAUD.IRX. Not a SCE SID.
    /// </summary>
    public const uint SidLgDev = 0x046D046D;
    /// <summary>
    /// LGDEVW.IRX "Version 1.11.027 (Wheel)" packed as EE expects at recv+4 after fno=12:
    /// major=1, minor=11 (0x0B), build=27 (0x1B) → <c>0x010B1B00</c>.
    /// </summary>
    public const uint LgDevVersion_1_11_027 = 0x010B1B00;
    /// <summary>989snd primary RPC (classic placeholder sid used by 989 middleware).</summary>
    public const uint Sid989Snd = 0x00123456;
    /// <summary>989snd secondary / stream RPC.</summary>
    public const uint Sid989Snd2 = 0x00123457;
    /// <summary>
    /// Midway MSL.IRX (Modular Sound Library) — DA live bind after LIBSD/SDRDRV/MSL load
    /// (sid=<c>0x00012345</c>, init fno=<c>0xDADA</c>). Distinct from 989snd 0x00123456.
    /// Disc: MODULES/MSL.IRX "MSL IOP driver version 1.7.4". Shared DA (and family titles
    /// that ship MSL rather than 989snd).
    /// </summary>
    public const uint SidMsl = 0x00012345;
    /// <summary>
    /// Midway MFL (MSL File Link) IOP↔EE file RPC — DA EE binds sid=<c>0x00012347</c>
    /// from <c>mflrpc.c</c> after MSL sound sid 0x12345. Carries open/read/close for
    /// <c>cdrom0:\MKDA.PAK</c> and member streams. Soft 0xDADA alone leaves this unbound
    /// and gameart.ssf wait never reaches status==4.
    /// </summary>
    public const uint SidMslMfl = 0x00012347;

    private static bool IsMslFamilySid(uint sid) => sid == SidMsl || sid == SidMslMfl;

    // Crystal Dynamics / SN "GOE_FSRV" IOPFILE.IRX services (Blood Omen 2 + Whiplash).
    // Disc module registers low SIDs — BO2 live bind order 0x30, 0x20, 0x21, 0x29
    // (iopfile.irx data words at file+0xC70/0xE44). Whiplash IOPFILE.IRX binds 0x31 + 0x40
    // (2026-07-30 live unknownBindSids). EE client uses these for archive / bigfile
    // streaming (PS2.RKV) after GAME.INI / GAME.ERG config.
    public const uint SidIopFile20 = 0x00000020;
    public const uint SidIopFile21 = 0x00000021;
    public const uint SidIopFile29 = 0x00000029;
    public const uint SidIopFile30 = 0x00000030;
    /// <summary>Whiplash (SLUS_206.84) IOPFILE primary stream SID (live bind).</summary>
    public const uint SidIopFile31 = 0x00000031;
    /// <summary>Whiplash (SLUS_206.84) IOPFILE secondary / control SID (live bind).</summary>
    public const uint SidIopFile40 = 0x00000040;

    private static bool IsIopFileSid(uint sid) =>
        sid is SidIopFile20 or SidIopFile21 or SidIopFile29 or SidIopFile30
            or SidIopFile31 or SidIopFile40;

    /// <summary>
    /// libdbc extension SIDs (DS2O / dualshock siblings of dbcman 0x80001300).
    /// Burnout 3 also binds <c>0x8000131B</c> next to <c>0x8000131C</c> after GTFSCDVD load.
    /// </summary>
    private static bool IsDbcManSibling(uint sid) =>
        sid is 0x8000131B or 0x8000131C or 0x8000131E or 0x8000131F;

    /// <summary>
    /// Criterion GTFS / stage RPC (Burnout 3 live: bind+call after GTFSCDVD.IRX load).
    /// FourCC-style sid <c>"STG\\0"</c> = <c>0x00475453</c>; fno 1/3 observed with non-null recv.
    /// Soft-success so boot can leave IRX-only and open game assets via FILEIO/NCMD.
    /// </summary>
    public const uint SidGtfsStg = 0x00475453;
    /// <summary>Burnout 3 residual service after LGDEV (live bind+call fno=sid, recv=0).</summary>
    public const uint SidB3Aux = 0x00150276;

    private static bool IsBurnout3GtfsSid(uint sid) =>
        sid == SidGtfsStg || sid == SidB3Aux || sid == 0x53465447u /* "GTFS" fourCC */;

    // -------------------------------------------------------------------------
    // Midway MWFILEFR.IRX — proprietary FS RPC used by MK: Deception (SLUS_208.81),
    // MK: Deadly Alliance, and other Midway titles after DNAS300/IOPRP300 GetVersion.
    //
    // Ground-truthed from disc MODULES/MWFILEFR.IRX (R3000, MW MIPS C Compiler 2.4.1):
    //   sceSifRegisterRpc(sd, 0x000F0002, handler@0xA08, buf)  — aux (fno 0xC8 only)
    //   sceSifRegisterRpc(sd, 0x000F0001, handler@0xA40, buf)  — main jump table fno 0..13
    // EE client (SLUS_208.81 @ 0x3D6144): CallRpc fno=1 send=20 recv=4  = MWF_RPC_INIT
    // Real INIT success returns 0 (result cell @ module+0x61C0); EE continues on *recv==0.
    // Open/read/write/close map onto IOMAN/FILEIO over host/cdrom0/atfile devices.
    // HLE bridges those to Iso FILEIO so titles leave IRX-only boot and open OVL/assets.
    // -------------------------------------------------------------------------
    /// <summary>MWFILEFR.IRX primary service (EE→IOP file commands).</summary>
    public const uint SidMwFileMain = 0x000F0001;
    /// <summary>MWFILEFR.IRX auxiliary service (fno 0xC8 buffer-release / reverse path).</summary>
    public const uint SidMwFileAux = 0x000F0002;
    /// <summary>EE-side reverse RPC server id (IOP binds to this; not an IOP service).</summary>
    public const uint SidMwFileEeServer = 0x000F1002;

    private static bool IsMwFileSid(uint sid) =>
        sid is SidMwFileMain or SidMwFileAux;

    // MWFILE main jump-table fnos (IRX handler @ 0xA40, table @ module+0x5A20).
    private const uint MwFnoInit = 1;
    private const uint MwFnoShutdown = 2;
    private const uint MwFnoClose = 4;
    private const uint MwFnoOpenPath = 5;   // path @ send+12
    private const uint MwFnoOpen2 = 6;
    private const uint MwFnoRead = 7;
    private const uint MwFnoFlush = 8;
    private const uint MwFnoWrite = 9;
    private const uint MwFnoSeek = 10;
    private const uint MwFnoConfig = 11;
    private const uint MwFnoNop = 12;
    private const uint MwFnoStat = 13;
    private const uint MwFnoAuxRelease = 0xC8; // 200 — aux sid only

    /// <summary>Live MWFILE handles: synthetic id → FILEIO fd.</summary>
    private readonly Dictionary<int, int> _mwFileHandles = new();
    private int _mwFileNextHandle = 1;

    // MSL sound init fno observed on Deadly Alliance after MSL.IRX load (live 2026-07-30).
    private const uint MslFnoInit = 0xDADA;
    // MFL (mflrpc.c) CallRpc fnos ground-truthed from DA EE client @ 0x22Cxxx:
    //   fno=1  init after bind 0x12347
    //   fno=24 open  (path@send, mode trailing)
    //   fno=21 stat / wait-ready poll
    //   fno=22 close / release
    //   fno=7  read  (common Midway; also accept when send has fd+buf+len)
    private const uint MflFnoInit = 1;
    private const uint MflFnoRead = 7;
    private const uint MflFnoStat = 21;
    private const uint MflFnoClose = 22;
    private const uint MflFnoOpen = 24;

    /// <summary>Live MFL handles: synthetic id → FILEIO fd.</summary>
    private readonly Dictionary<int, int> _mflHandles = new();
    private int _mflNextHandle = 1;
    /// <summary>True after MFL init fno or ready-flag seed / successful open.</summary>
    public bool MflInited { get; private set; }
    private bool _mkdaArtHashPlanted;
    private int _daPathHashScratchOff;

    /// <summary>
    /// Midway MSL.IRX + MFL file-link HLE (sids <see cref="SidMsl"/> / <see cref="SidMslMfl"/>).
    /// Soft-success for sound init (0xDADA). Real open/read/close on MFL so DA can mount
    /// MKDA.PAK and stream <c>gameart.ssf</c> without planting wait-ready status=4 (Exit).
    /// </summary>
    private int HandleMsl(SystemMemory mem, IopModuleHost iopModules, Cdvd cdvd,
        uint sid, uint fno, uint argBuf, uint sendSize, uint recvBuf, uint recvSize)
    {
        // First MSL/MFL touch → ensure shared art archive TOC is ready (DA artps2 / Dec art).
        EnsureMkdaPakMounted(iopModules, cdvd);

        // MFL file link (0x12347) — real open/read. Also accept file fnos on primary sid
        // (some family builds multiplex).
        if (sid == SidMslMfl || fno is MflFnoInit or MflFnoOpen or MflFnoRead or MflFnoStat or MflFnoClose)
        {
            int fr = HandleMfl(mem, iopModules, cdvd, fno, argBuf, sendSize, recvBuf, recvSize);
            return fr;
        }

        if (fno == MslFnoInit)
        {
            // Sound SysInit: zero result = OK. Paint version token for clients that read it.
            if (recvBuf != 0)
            {
                uint lim = recvSize != 0 ? recvSize : 4u;
                if (lim >= 4) mem.Write32(recvBuf, 0);
                if (lim >= 8) mem.Write32(recvBuf + 4, 0x00010704u);
            }
            // After sound init, EE may already have queued MKDA.PAK on the request ring —
            // proactively open so subsequent MFL open/stat see a warm FILEIO path.
            WarmMslAssetOpens(iopModules, cdvd);
            return 0;
        }

        // Remaining sound fnos (bank load / play / stream): soft-OK until per-fno ground truth.
        if (recvBuf != 0 && (recvSize == 0 || recvSize >= 4))
            mem.Write32(recvBuf, 0);
        return 0;
    }

    /// <summary>
    /// MFL open/read/close/stat. EE client (mflrpc.c) CallRpc shapes observed on DA:
    /// open fno=24 send has path string; read fno=7 send has handle/len; close fno=22.
    /// </summary>
    private int HandleMfl(SystemMemory mem, IopModuleHost iopModules, Cdvd cdvd,
        uint fno, uint argBuf, uint sendSize, uint recvBuf, uint recvSize)
    {
        bool trace = Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1";

        switch (fno)
        {
            case MflFnoInit:
            case 0: // bind probe / soft init
                MflInited = true;
                if (recvBuf != 0 && (recvSize == 0 || recvSize >= 4))
                    mem.Write32(recvBuf, 0);
                WarmMslAssetOpens(iopModules, cdvd);
                return 0;

            case MflFnoOpen:
            case 5: // allow MWFILE-style open fno as alias
            {
                string path = argBuf != 0 && sendSize > 0
                    ? ScanSendBufferForPath(mem, argBuf, sendSize)
                    : "";
                if (string.IsNullOrEmpty(path) && argBuf != 0 && sendSize >= 4)
                {
                    // Inline C string at +0 or +4 (mode then path).
                    path = ReadCString(mem, argBuf, 256);
                    if (!LooksLikeFsPath(path) && path.IndexOf('.') < 0)
                        path = sendSize >= 8 ? ReadCString(mem, argBuf + 4, 256) : "";
                }
                if (string.IsNullOrEmpty(path))
                {
                    if (recvBuf != 0 && (recvSize == 0 || recvSize >= 4))
                        mem.Write32(recvBuf, unchecked((uint)(-2)));
                    return -2;
                }
                path = AliasMidwayPakPath(path);
                int fd = iopModules.FileOpen(path, 1);
                if (fd < 0)
                {
                    EnsureMkdaPakMounted(iopModules, cdvd);
                    fd = TryOpenFromMkdaPak(iopModules, path, out _);
                }
                if (fd < 0)
                {
                    if (trace)
                        Console.Error.WriteLine($"[MSL-MFL] open FAIL path=\"{path}\"");
                    if (recvBuf != 0 && (recvSize == 0 || recvSize >= 4))
                        mem.Write32(recvBuf, unchecked((uint)(-2)));
                    return -2;
                }
                int h = _mflNextHandle++;
                _mflHandles[h] = fd;
                MflInited = true;
                if (iopModules.TryGetOpenFileSize(fd, out uint fsz) && fsz > 0)
                    cdvd.NoteHostReadSectors((int)Math.Min((fsz + 2047) / 2048, 256));
                if (trace)
                    Console.Error.WriteLine($"[MSL-MFL] open path=\"{path}\" h={h} fd={fd} size={fsz}");
                if (recvBuf != 0)
                {
                    if (recvSize == 0 || recvSize >= 4) mem.Write32(recvBuf, unchecked((uint)h));
                    if (recvSize >= 8) mem.Write32(recvBuf + 4, fsz);
                }
                return h;
            }

            case MflFnoRead:
            case 3: // common read alias
            {
                // Layout guess: +0 handle, +4 dst ptr, +8 length (or length at +4).
                int h = argBuf != 0 && sendSize >= 4 ? (int)mem.Read32(argBuf) : 0;
                uint dst = 0;
                int len = 0;
                if (argBuf != 0 && sendSize >= 12)
                {
                    dst = mem.Read32(argBuf + 4);
                    len = (int)mem.Read32(argBuf + 8);
                }
                else if (argBuf != 0 && sendSize >= 8)
                {
                    len = (int)mem.Read32(argBuf + 4);
                }
                if (!_mflHandles.TryGetValue(h, out int fd) || len <= 0)
                {
                    if (recvBuf != 0 && (recvSize == 0 || recvSize >= 4))
                        mem.Write32(recvBuf, 0);
                    return 0;
                }
                // Prefer host read into EE buffer when dst is RDRAM; else report size only.
                int got = 0;
                if (dst >= 0x00100000 && dst < SystemMemory.RDRAM_SIZE && len > 0)
                {
                    int toRead = Math.Min(len, 4 * 1024 * 1024);
                    // Offset 0 for first chunk; full sequential read is driven by EE seeks.
                    if (iopModules.TryReadOpenFileBytes(fd, 0, toRead, out byte[]? buf) && buf != null)
                    {
                        got = buf.Length;
                        for (int i = 0; i < got; i++)
                            mem.Write8(dst + (uint)i, buf[i]);
                        cdvd.NoteHostReadSectors((got + 2047) / 2048);
                    }
                }
                if (trace)
                    Console.Error.WriteLine($"[MSL-MFL] read h={h} dst=0x{dst:X8} len={len} got={got}");
                if (recvBuf != 0 && (recvSize == 0 || recvSize >= 4))
                    mem.Write32(recvBuf, unchecked((uint)got));
                return got;
            }

            case MflFnoStat: // 21 / 0x15 — DA "get file info" (0x22CC00), not a bare poll
            {
                // EE CallRpc fno=21 send=4 (handle) recv=40; then *(recv+4) & 8
                // (andi v0,v0,8 @ 0x22CC98). poll@0x2F5C6C abandons if that is 0.
                // +4 is FLAGS with bit3=ready; size lives at +16 (DA WAVE 3).
                int h = argBuf != 0 && sendSize >= 4 ? (int)mem.Read32(argBuf) : 0;
                uint fsz = 0;
                if (_mflHandles.TryGetValue(h, out int fdInfo))
                    iopModules.TryGetOpenFileSize(fdInfo, out fsz);
                if (fsz == 0 && h > 0)
                    fsz = 1;
                const uint InfoReadyFlag = 0x8;
                if (recvBuf != 0)
                {
                    uint lim = recvSize != 0 ? recvSize : 4u;
                    for (uint o = 0; o + 4 <= lim && o < 40; o += 4)
                        mem.Write32(recvBuf + o, 0);
                    if (lim >= 4) mem.Write32(recvBuf, unchecked((uint)h));
                    if (lim >= 8) mem.Write32(recvBuf + 4, InfoReadyFlag | (h > 0 ? 1u : 0u));
                    if (lim >= 12) mem.Write32(recvBuf + 8, 0);
                    if (lim >= 16) mem.Write32(recvBuf + 12, 1);
                    if (lim >= 20) mem.Write32(recvBuf + 16, fsz);
                    if (lim >= 24) mem.Write32(recvBuf + 20, fsz);
                }
                return h > 0 ? unchecked((int)InfoReadyFlag) : 0;
            }

            case MflFnoClose:
            {
                int h = argBuf != 0 && sendSize >= 4 ? (int)mem.Read32(argBuf) : 0;
                if (_mflHandles.TryGetValue(h, out int fd))
                {
                    try { iopModules.FileClose(fd); } catch { /* ignore */ }
                    _mflHandles.Remove(h);
                }
                if (recvBuf != 0 && (recvSize == 0 || recvSize >= 4))
                    mem.Write32(recvBuf, 0);
                return 0;
            }

            default:
                if (recvBuf != 0 && (recvSize == 0 || recvSize >= 4))
                    mem.Write32(recvBuf, 0);
                return 0;
        }
    }

    /// <summary>
    /// Eager FILEIO open of MKDA.PAK (and token sector note) so MFL open/stat after DADA
    /// does not race an unmounted archive. Idempotent.
    /// </summary>
    private void WarmMslAssetOpens(IopModuleHost iopModules, Cdvd cdvd)
    {
        EnsureMkdaPakMounted(iopModules, cdvd);
        // Touch the ISO root PAK + MSL sound bank so post-DADA opens are hot.
        // Dec ELF references "/sounds/MSLASSET.MS2"; ISO root has MSLASSET.MS2.
        string[] warm =
        {
            @"cdrom0:\MKDA.PAK",
            @"cdrom0:\MKDA.PAK;1",
            @"cdrom0:\MSLASSET.MS2",
            @"cdrom0:\MSLASSET.MS2;1",
            @"cdrom0:\MSLASSET.MS4",
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string p in warm)
        {
            string key = p.Replace(";1", "", StringComparison.OrdinalIgnoreCase);
            if (!seen.Add(key)) continue;
            int fd = iopModules.FileOpen(p, 1);
            if (fd < 0) continue;
            if (iopModules.TryGetOpenFileSize(fd, out uint sz) && sz > 0)
                cdvd.NoteHostReadSectors(Math.Min(8, (int)((sz + 2047) / 2048)));
            try { iopModules.FileClose(fd); } catch { /* ignore */ }
        }
        // Also warm the common first art member (Dec gameart.ssf / DA gameart via artps2).
        foreach (string member in new[] { "gameart.ssf", @"\ps2dvd\art\gameart.ssf", @"\ps2dvd\artps2\gameart.ssf" })
        {
            int mfd = TryOpenFromMkdaPak(iopModules, member, out uint msz);
            if (mfd < 0) continue;
            if (msz > 0)
                cdvd.NoteHostReadSectors(Math.Min(32, (int)((msz + 2047) / 2048)));
            try { iopModules.FileClose(mfd); } catch { /* ignore */ }
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[MSL-MFL] warm member \"{member}\" size={msz}");
            break;
        }
    }

    /// <summary>
    /// Pump MFL completions for EE-side request rings that queued a path open without a
    /// CallRpc (DA 0x2F53A0). Completes <c>cdrom0:\MKDA.PAK</c> / artps2 member paths via
    /// FILEIO so archive host+4 is non-null and gameart wait can reach status==4 honestly.
    /// Safe no-op when rings are empty or not the standard MSL layout.
    /// </summary>
    public void PumpMslFileRequests(SystemMemory mem, IopModuleHost iopModules, Cdvd cdvd)
    {
        if (mem == null || iopModules == null) return;
        EnsureMkdaPakMounted(iopModules, cdvd);

        // Publish MFL-ready so EE mflrpc open (0x22C9F0) does not hard-skip when the
        // MFL bind/CreateSema path was skipped (DA 0x22B640 residual). GP=0x410D70 on DA.
        // Only seed when the MSL response ring looks live (cap==0x28) so we don't poke cold boots.
        TrySeedMflReadyFlag(mem);
        TryRegisterMkdaArtMembers(mem, iopModules, cdvd);

        // DA live request ring header @ 0x587DA0: cap, free, base, stride, flags.
        // Response ring @ 0x587E60: cap, count, base, stride.
        // Layout shared by MSL EE client after 0x2F5960 init — also used by Dec family.
        TryCompleteMslRequestRing(mem, iopModules, cdvd, reqHdr: 0x00587DA0u, respHdr: 0x00587E60u);
    }

    /// <summary>
    /// DA MFL CallRpc client (live open/info/close at 0x22C9F0/0x22CC00/0x22CAB0).
    /// EE uses <c>lui a0,0x55; addiu a0,a0,-3584</c> → <c>0x54F200</c>, which is a
    /// separate SifRpcClientData from the MSL sound bind at <c>0x546E80</c>. Without a
    /// soft-bind here CallRpc resolves sid=0 (unknownServiceCalls++) and archive host+4
    /// stays null — the primary gameart wait wall at 0x2F55xx.
    /// </summary>
    private const uint MflClientDa = 0x0054F200;
    /// <summary>MSL sound client bound after MSL.IRX (DA live).</summary>
    private const uint MslClientDa = 0x00546E80;

    /// <summary>
    /// DA: gp-24716 (0x40ACE4 with gp=0x410D70) is the MFL RPC ready word. EE open/read
    /// clients return null when it is 0. Seed a positive sentinel once MSL rings exist,
    /// and soft-bind the MFL CallRpc client so fno 21/22/24 hit HandleMsl.
    /// </summary>
    private void TrySeedMflReadyFlag(SystemMemory mem)
    {
        // Discover GP-relative cell via the known absolute for DA when rings are up.
        uint respCap = mem.Read32(0x00587E60);
        if (respCap != 0x28) return;
        const uint mflReady = 0x0040ACE4; // gp(0x410D70) - 24716
        uint cur = mem.Read32(mflReady);
        if (cur == 0)
        {
            mem.Write32(mflReady, 1);
            MflInited = true;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine("[MSL-MFL] seed ready flag @0x40ACE4=1");
        }

        // Soft-bind MFL file client (distinct pointer 0x54F200) so fno 21/22/24 hit HandleMsl.
        TrySoftBindMflClient(mem);
    }

    /// <summary>
    /// Register <see cref="MflClientDa"/> in the HLE bind map and stamp sid at +36 so
    /// sceSifCallRpc packets from mflrpc carry a real Midway MSL/MFL service id.
    /// Idempotent; prefers cloning argBuf from the live MSL client when present.
    /// Stamp <see cref="SidMslMfl"/> (0x12347) when the sound client is not yet bound so
    /// CallRpc does not resolve sid=0; once MSL is live, keep that sid (family multiplex).
    /// </summary>
    private void TrySoftBindMflClient(SystemMemory mem)
    {
        if (_cdToSid.ContainsKey(MflClientDa))
        {
            // Keep sid stamped even if EE zeroed client memory after our first seed.
            if (mem.Read32(MflClientDa + 36) == 0)
            {
                uint keep = _cdToSid[MflClientDa];
                if (keep == 0) keep = SidMslMfl;
                mem.Write32(MflClientDa + 36, keep);
            }
            return;
        }

        // Prefer live MSL sid (family multiplexes file fnos on 0x12345). Else MFL-only 0x12347.
        uint sid = SidMslMfl;
        uint argBuf = 0;
        if (_cdToSid.TryGetValue(MslClientDa, out uint mslSid) && mslSid != 0)
        {
            sid = mslSid;
            if (_cdToArgBuf.TryGetValue(MslClientDa, out uint ab))
                argBuf = ab;
        }
        if (argBuf == 0)
            argBuf = AssignSlot();

        _cdToSid[MflClientDa] = sid;
        _cdToArgBuf[MflClientDa] = argBuf;

        // Mirror minimal SifRpcClientData_t fields the EE/HLE round-trip needs.
        // +8 sema: leave if EE already created one; else plant a non-zero token.
        if (mem.Read32(MflClientDa + 8) == 0)
        {
            uint mslSema = mem.Read32(MslClientDa + 8);
            mem.Write32(MflClientDa + 8, mslSema != 0 ? mslSema : 1u);
        }
        mem.Write32(MflClientDa + 20, argBuf);
        if (mem.Read32(MflClientDa + 24) == 0)
            mem.Write32(MflClientDa + 24, AssignSlot());
        mem.Write32(MflClientDa + 36, sid);

        MflInited = true;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine(
                $"[MSL-MFL] soft-bind client=0x{MflClientDa:X8} sid=0x{sid:X8} arg=0x{argBuf:X8}");
    }


    private const uint DaPathHashTable = 0x0053DCC0;
    private const uint DaPathHashScratch = 0x0007F000;

    /// <summary>Public re-try for Family Step after EE allocates path hash table.</summary>
    public void TryEnsureMkdaArtPathHash(SystemMemory mem, IopModuleHost iopModules, Cdvd cdvd)
    {
        if (mem == null || iopModules == null) return;
        TryRegisterMkdaArtMembers(mem, iopModules, cdvd);
    }

    private void TryRegisterMkdaArtMembers(SystemMemory mem, IopModuleHost iopModules, Cdvd cdvd)
    {
        if (_mkdaArtHashPlanted) return;
        uint buckets = mem.Read32(DaPathHashTable + 4);
        uint nbuckets = mem.Read32(DaPathHashTable + 8);
        uint entryPool = mem.Read32(DaPathHashTable + 12);
        if (buckets < 0x00100000 || nbuckets is 0 or > 100_000) return;
        if (entryPool < 0x00100000) return;
        EnsureMkdaPakMounted(iopModules, cdvd);
        foreach (string member in new[] { @"\ps2dvd\artps2\gameart.ssf", @"\ps2dvd\art\gameart.ssf", "gameart.ssf" })
        {
            int mfd = TryOpenFromMkdaPak(iopModules, member, out uint msz);
            if (mfd < 0 || msz == 0) continue;
            try { iopModules.FileClose(mfd); } catch { /* ignore */ }
            uint stream = AllocDaPathScratch(mem, 32);
            if (stream == 0) return;
            mem.Write32(stream + 0, 0x5354464Du);
            mem.Write32(stream + 4, stream);
            mem.Write32(stream + 8, msz);
            mem.Write32(stream + 12, msz);
            mem.Write32(stream + 20, 4);
            int planted = 0;
            foreach (string key in new[] { member, member.TrimStart('\\', '/') })
            {
                if (TryInsertDaPathHash(mem, key, stream)) planted++;
            }
            if (planted > 0)
            {
                _mkdaArtHashPlanted = true;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine($"[MSL-MFL] path-hash plant gameart entries={planted} size={msz}");
                return;
            }
        }
    }

    private uint AllocDaPathScratch(SystemMemory mem, int bytes)
    {
        int need = (bytes + 3) & ~3;
        if (_daPathHashScratchOff + need > 0xE00) return 0;
        uint addr = DaPathHashScratch + (uint)_daPathHashScratchOff;
        _daPathHashScratchOff += need;
        for (int i = 0; i < need; i += 4) mem.Write32(addr + (uint)i, 0);
        return addr;
    }

    private bool TryInsertDaPathHash(SystemMemory mem, string path, uint value)
    {
        if (string.IsNullOrEmpty(path) || value == 0) return false;
        uint buckets = mem.Read32(DaPathHashTable + 4);
        uint nbuckets = mem.Read32(DaPathHashTable + 8);
        uint entryPool = mem.Read32(DaPathHashTable + 12);
        uint count = mem.Read32(DaPathHashTable + 16);
        if (buckets < 0x00100000 || nbuckets == 0 || entryPool < 0x00100000) return false;
        if (count >= 0x10000 || entryPool + (count + 1) * 12 > SystemMemory.RDRAM_SIZE) return false;
        uint hash = 0;
        foreach (char ch in path)
        {
            uint c = (byte)ch;
            if (c is >= 'A' and <= 'Z') c += 32;
            hash = (hash << 4) + c;
            uint hi = hash & 0xF0000000u;
            if (hi != 0) { hash ^= hi >> 24; hash ^= hi; }
        }
        uint bucketAddr = buckets + (hash % nbuckets) * 4;
        for (uint e = mem.Read32(bucketAddr); e != 0; e = mem.Read32(e + 8))
        {
            if (e < 0x00100000 || e >= SystemMemory.RDRAM_SIZE) break;
            if (string.Equals(ReadCString(mem, mem.Read32(e), 128), path, StringComparison.OrdinalIgnoreCase))
            {
                mem.Write32(e + 4, value);
                return true;
            }
        }
        uint pathPtr = 0;
        if (mem.Read32(DaPathHashTable + 24) != 0)
        {
            uint strBase = mem.Read32(DaPathHashTable + 28);
            uint strCur = mem.Read32(DaPathHashTable + 36);
            if (strBase >= 0x00100000 && strCur < 0x100000
                && strBase + strCur + (uint)path.Length + 1 < SystemMemory.RDRAM_SIZE)
            {
                pathPtr = strBase + strCur;
                for (int i = 0; i < path.Length; i++) mem.Write8(pathPtr + (uint)i, (byte)path[i]);
                mem.Write8(pathPtr + (uint)path.Length, 0);
                mem.Write32(DaPathHashTable + 36, strCur + (uint)path.Length + 1);
            }
        }
        if (pathPtr == 0)
        {
            pathPtr = AllocDaPathScratch(mem, path.Length + 1);
            if (pathPtr == 0) return false;
            for (int i = 0; i < path.Length; i++) mem.Write8(pathPtr + (uint)i, (byte)path[i]);
            mem.Write8(pathPtr + (uint)path.Length, 0);
        }
        uint entry = entryPool + count * 12;
        mem.Write32(entry + 0, pathPtr);
        mem.Write32(entry + 4, value);
        mem.Write32(entry + 8, mem.Read32(bucketAddr));
        mem.Write32(bucketAddr, entry);
        mem.Write32(DaPathHashTable + 16, count + 1);
        return true;
    }

    private void TryCompleteMslRequestRing(SystemMemory mem, IopModuleHost iopModules, Cdvd cdvd,
        uint reqHdr, uint respHdr)
    {
        uint cap = mem.Read32(reqHdr);
        uint free = mem.Read32(reqHdr + 4);
        uint basePtr = mem.Read32(reqHdr + 8);
        uint stride = mem.Read32(reqHdr + 12);
        if (cap is 0 or > 64 || stride < 32 || stride > 256) return;
        if (basePtr < 0x00100000 || basePtr >= SystemMemory.RDRAM_SIZE) return;
        // free < cap ⇒ at least one slot claimed.
        if (free >= cap) return;

        int used = (int)(cap - free);
        bool trace = Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1";
        for (int i = 0; i < used && i < 8; i++)
        {
            uint slot = basePtr + (uint)(i * (int)stride);
            if (slot + stride > SystemMemory.RDRAM_SIZE) break;
            // Path at +8 (0x2F53A0 copies path to slot+8, mode to +68).
            string path = ReadCString(mem, slot + 8, 64);
            if (string.IsNullOrEmpty(path) || path.IndexOf('.') < 0) continue;
            // Already completed? status word at +0 non-zero and looks like a handle/flag.
            uint st = mem.Read32(slot);
            if (st == 4 || st == 1) continue;

            path = AliasMidwayPakPath(path);
            int fd = iopModules.FileOpen(path, 1);
            if (fd < 0)
            {
                EnsureMkdaPakMounted(iopModules, cdvd);
                fd = TryOpenFromMkdaPak(iopModules, path, out _);
            }
            if (fd < 0) continue;

            int h = _mflNextHandle++;
            _mflHandles[h] = fd;
            MflInited = true;
            if (iopModules.TryGetOpenFileSize(fd, out uint fsz) && fsz > 0)
                cdvd.NoteHostReadSectors((int)Math.Min((fsz + 2047) / 2048, 64));

            // Response object layout ground-truthed from DA poll @0x2F5A80:
            //   +0  status (1 = open done → info/close path @0x2F5C64 when +16 handle ≠ 0)
            //   +4  secondary status
            //   +8  flags (bit1 ⇒ complete to status=4 after info @0x2F5D74)
            //   +12 request slot ptr
            //   +16 MFL handle (non-zero required — zero takes empty-complete path)
            //   +20 size hint
            // Do NOT write request+16: path string lives at request+8 and spans into +16
            // ("cdrom0:\MKDA.PAK"). Stamping handle there corruptsthe path for retries.
            // Mark request +0 = 1 only as re-pump skip (poll open path already finished).
            const uint respObj = 0x0007FE00; // low scratch; outside ELF image
            mem.Write32(respObj + 0, 1);                     // open-done
            mem.Write32(respObj + 4, 0);
            mem.Write32(respObj + 8, 2);                     // bit1 → status=4 after info
            mem.Write32(respObj + 12, slot);                 // request
            mem.Write32(respObj + 16, unchecked((uint)h));  // handle for info/close
            mem.Write32(respObj + 20, fsz);
            mem.Write32(respObj + 24, fsz);
            mem.Write32(respObj + 28, 0);

            // Re-pump skip only — leave path bytes at +8.. intact.
            mem.Write32(slot, 1);

            uint rCap = mem.Read32(respHdr);
            uint rCount = mem.Read32(respHdr + 4);
            uint rBase = mem.Read32(respHdr + 8);
            uint rStride = mem.Read32(respHdr + 12);
            if (rCap is > 0 and <= 64 && rBase >= 0x00100000 && rBase < SystemMemory.RDRAM_SIZE
                && rStride is >= 4 and <= 64 && rCount < rCap)
            {
                // Prefer slot 0 if our earlier count=1 seed left a stale pointer.
                uint idx = rCount == 0 ? 0 : rCount;
                if (idx >= rCap) idx = 0;
                uint rSlot = rBase + idx * Math.Max(rStride, 4u);
                if (rSlot + 4 <= SystemMemory.RDRAM_SIZE)
                {
                    mem.Write32(rSlot, respObj); // ring[i] = &response_object
                    if (rCount == 0)
                        mem.Write32(respHdr + 4, 1);
                    else if (rCount < rCap)
                        mem.Write32(respHdr + 4, rCount); // keep; we overwrote slot 0 if seeded
                    // Ensure at least one pending response is visible.
                    if (mem.Read32(respHdr + 4) == 0)
                        mem.Write32(respHdr + 4, 1);
                }
            }

            // Also ensure MFL client is bound so poll's CallRpc info/close succeed.
            TrySoftBindMflClient(mem);

            if (trace)
                Console.Error.WriteLine(
                    $"[MSL-MFL] ring-complete path=\"{path}\" h={h} fd={fd} size={fsz} " +
                    $"slot=0x{slot:X8} respObj=0x{respObj:X8}");
        }
    }
    private bool _mwFileInited;

    /// <summary>
    /// Midway MWFILEFR RPC HLE. Prefer shared FILEIO/ISO over title-local PC patches.
    /// Returns the 4-byte result word the EE client reads from recv (INIT/CLOSE: 0 = ok;
    /// OPEN: non-zero handle; READ/WRITE: byte count or 0; errors: negative).
    /// </summary>
    private int HandleMwFile(SystemMemory mem, IopModuleHost iopModules, Cdvd cdvd,
        uint sid, uint fno, uint argBuf, uint sendSize, uint recvBuf, uint recvSize)
    {
        _ = recvBuf; _ = recvSize;
        bool trace = Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1";

        if (trace && argBuf != 0 && sendSize > 0)
        {
            uint n = Math.Min(sendSize, 64u);
            var sb = new System.Text.StringBuilder(160);
            sb.Append($"[MWFILE] sid=0x{sid:X8} fno=0x{fno:X} send={sendSize}:");
            for (uint o = 0; o + 4 <= n; o += 4)
                sb.Append($" {mem.Read32(argBuf + o):X8}");
            // Path probes at common offsets.
            foreach (uint off in new uint[] { 0, 4, 8, 12, 16 })
            {
                if (off + 4 > sendSize) break;
                string p = ReadCString(mem, argBuf + off, 96);
                if (LooksLikeFsPath(p) || (p.Length > 2 && p.IndexOf('.') >= 0))
                    sb.Append($" +{off}=\"{p}\"");
                uint maybePtr = mem.Read32(argBuf + off);
                if (maybePtr >= 0x00100000 && maybePtr < SystemMemory.RDRAM_SIZE)
                {
                    string pp = ReadCString(mem, maybePtr, 96);
                    if (LooksLikeFsPath(pp) || (pp.Length > 2 && pp.IndexOf('.') >= 0))
                        sb.Append($" *+{off}=\"{pp}\"");
                }
            }
            Console.Error.WriteLine(sb.ToString());
        }

        // Aux sid: only fno 0xC8 is special (IRX 0xA08); everything else returns OK buffer.
        if (sid == SidMwFileAux)
        {
            // Real handler: if fno==200, call release with *buf; always returns result-cell ptr.
            // Soft-success 0 is the EE expectation (Deci2-style done).
            return 0;
        }

        switch (fno)
        {
            case MwFnoInit:
                // Real IRX: allocate command/file pools from send (20B config), set inited=1,
                // return 0. EE (0x3D618C) continues when *recv==0.
                _mwFileInited = true;
                return 0;

            case MwFnoShutdown:
                foreach (var fd in _mwFileHandles.Values)
                {
                    try { iopModules.FileClose(fd); } catch { /* ignore */ }
                }
                _mwFileHandles.Clear();
                _mwFileInited = false;
                return 0;

            case MwFnoClose:
            {
                // EE fno=4: send=4, *send = handle (or IOP object ptr we minted).
                int handle = argBuf != 0 && sendSize >= 4 ? (int)mem.Read32(argBuf) : 0;
                if (_mwFileHandles.TryGetValue(handle, out int fd))
                {
                    iopModules.FileClose(fd);
                    _mwFileHandles.Remove(handle);
                }
                return 0;
            }

            case MwFnoOpenPath:
            case MwFnoOpen2:
            {
                // IRX fno5: path inline at +12; fno6: alternate layout.
                // Also accept EE pointer-at-+0 / +4 path forms used by async open packers.
                if (!_mwFileInited)
                    _mwFileInited = true; // tolerate open-before-init
                string path = MwFileExtractPath(mem, argBuf, sendSize);
                if (string.IsNullOrEmpty(path))
                {
                    if (trace) Console.Error.WriteLine("[MWFILE] open: empty path");
                    return 0; // fail as null handle (EE checks non-zero)
                }
                path = MwFileNormalizePath(path);
                path = AliasMidwayPakPath(path);
                int mode = 1; // O_RDONLY default
                if (argBuf != 0 && sendSize >= 8)
                {
                    uint m = mem.Read32(argBuf + 4);
                    if (m <= 3) mode = (int)m;
                }
                // Always warm MKDA TOC so subsequent member .ssf opens (same or later CallRpc)
                // hit virtual streams even when this open is the archive root itself.
                EnsureMkdaPakMounted(iopModules, cdvd);
                // Prefer PAK virtual members first for art paths (.ssf / ps2dvd / /art/) so we do
                // not accidentally open a same-named ISO root stub or miss host-style members.
                uint memberSz = 0;
                int fd = -1;
                if (LooksLikeMkdaMemberPath(path))
                    fd = TryOpenFromMkdaPak(iopModules, path, out memberSz);
                if (fd < 0)
                    fd = iopModules.FileOpen(path, mode);
                if (fd < 0)
                {
                    // Retry without device prefix and with cdrom0:
                    string leaf = path;
                    int colon = leaf.IndexOf(':');
                    if (colon >= 0) leaf = leaf[(colon + 1)..].TrimStart('\\', '/');
                    // /sounds/MSLASSET.MS2 (Dec ELF) lives at ISO root as MSLASSET.MS2
                    if (leaf.StartsWith("sounds\\", StringComparison.OrdinalIgnoreCase)
                        || leaf.StartsWith("sounds/", StringComparison.OrdinalIgnoreCase))
                    {
                        string soundLeaf = leaf[(leaf.IndexOfAny(new[] { '\\', '/' }) + 1)..];
                        fd = iopModules.FileOpen("cdrom0:\\" + soundLeaf, mode);
                    }
                    if (fd < 0) fd = iopModules.FileOpen("cdrom0:\\" + leaf, mode);
                    if (fd < 0) fd = iopModules.FileOpen(leaf, mode);
                }
                // Midway MKDA.PAK virtual members (art \ps2dvd\art\*.ssf etc.).
                if (fd < 0)
                    fd = TryOpenFromMkdaPak(iopModules, path, out memberSz);
                if (fd < 0)
                {
                    if (trace) Console.Error.WriteLine($"[MWFILE] open FAIL path=\"{path}\"");
                    return 0;
                }
                int handle = _mwFileNextHandle++;
                _mwFileHandles[handle] = fd;
                uint fsz = memberSz;
                if (fsz == 0)
                    iopModules.TryGetOpenFileSize(fd, out fsz);
                if (fsz > 0)
                    cdvd.NoteHostReadSectors((int)Math.Min((fsz + 2047) / 2048, 64));
                // Live open send word0 is an EE file-object pointer (Dec: 0xCDD420). Real
                // MWFILEFR fills object fields the post-open queue at 0x3D87xx inspects.
                // Stamp size at +8/+12 (force +8; +12 only when 0 or looks like leftover mode/ptr
                // junk below 0x10000). Also publish size at send+8 when that cell is 0 so clients
                // that re-read the DMA send buffer (not only recv/object) see a length.
                // Ground-truthed: without size the queue never drains after MKDA.PAK open.
                if (argBuf != 0 && sendSize >= 4 && fsz > 0)
                {
                    uint obj = mem.Read32(argBuf);
                    if (obj >= 0x00100000 && obj + 20 <= SystemMemory.RDRAM_SIZE)
                    {
                        // Always publish size at +8 (queue @0x3D8B70 lw +8).
                        mem.Write32(obj + 8, fsz);
                        uint o12 = mem.Read32(obj + 12);
                        if (o12 == 0 || o12 < 0x10000)
                            mem.Write32(obj + 12, fsz);
                        // +0x10 position/cursor stays 0; +0x14 sometimes used as aux size.
                        if (mem.Read32(obj + 0x14) == 0)
                            mem.Write32(obj + 0x14, fsz);
                    }
                    // send+8 is 0 on Dec open pack; leave +0 object ptr and +12 path intact.
                    if (sendSize >= 12 && mem.Read32(argBuf + 8) == 0)
                        mem.Write32(argBuf + 8, fsz);
                }
                // Multi-word recv: +0 handle (HandleCall), +4 size when present.
                if (recvBuf != 0 && recvSize >= 8 && fsz > 0)
                    mem.Write32(recvBuf + 4, fsz);
                if (trace)
                    Console.Error.WriteLine(
                        $"[MWFILE] open OK path=\"{path}\" handle={handle} fd={fd} size={fsz}" +
                        (memberSz > 0 ? " (pak-member)" : ""));
                return handle;
            }

            case MwFnoRead:
            case MwFnoWrite:
            {
                // Layout (IRX fno7/9): +0 handle-ish, +4 eeBuf, +8 size, +12 more.
                // EE also packs complex async command blobs; probe for handle+ptr+size.
                if (argBuf == 0 || sendSize < 12)
                    return 0;
                int handle = (int)mem.Read32(argBuf);
                uint eeBuf = mem.Read32(argBuf + 4);
                int size = (int)mem.Read32(argBuf + 8);
                // Alternate: handle at +0, size at +4, eeBuf at +8
                if (size <= 0 || size > 0x800000 || eeBuf < 0x100000)
                {
                    int size2 = (int)mem.Read32(argBuf + 4);
                    uint ee2 = mem.Read32(argBuf + 8);
                    if (size2 > 0 && size2 <= 0x800000 && ee2 >= 0x100000)
                    {
                        size = size2;
                        eeBuf = ee2;
                    }
                }
                if (!_mwFileHandles.TryGetValue(handle, out int fd))
                {
                    // Handle might be raw FILEIO fd if open returned fd directly in older path.
                    if (handle > 0 && handle < 256)
                        fd = handle;
                    else
                        return 0;
                }
                if (size <= 0) return 0;
                if (fno == MwFnoRead)
                {
                    int got = iopModules.FileRead(mem, fd, eeBuf, (uint)Math.Min(size, 0x100000));
                    if (trace)
                        Console.Error.WriteLine(
                            $"[MWFILE] read handle={handle} fd={fd} ee=0x{eeBuf:X8} size={size} got={got}");
                    return got;
                }
                else
                {
                    int put = iopModules.FileWrite(mem, fd, eeBuf, (uint)Math.Min(size, 0x100000));
                    if (trace)
                        Console.Error.WriteLine(
                            $"[MWFILE] write handle={handle} fd={fd} ee=0x{eeBuf:X8} size={size} put={put}");
                    return put;
                }
            }

            case MwFnoFlush:
            case MwFnoNop:
            case MwFnoConfig:
            case MwFnoAuxRelease:
                return 0;

            case MwFnoSeek:
            {
                if (argBuf == 0 || sendSize < 8) return 0;
                int handle = (int)mem.Read32(argBuf);
                int off = (int)mem.Read32(argBuf + 4);
                int whence = sendSize >= 12 ? (int)mem.Read32(argBuf + 8) : 0;
                if (!_mwFileHandles.TryGetValue(handle, out int fd))
                    return -1;
                return iopModules.FileSeek(fd, off, whence);
            }

            case MwFnoStat:
            {
                // Prefer path at +12; try getstat via a scratch io_stat_t in high RDRAM.
                string path = MwFileExtractPath(mem, argBuf, sendSize);
                if (string.IsNullOrEmpty(path)) return 0;
                path = MwFileNormalizePath(path);
                const uint scratch = 0x01FE8000;
                int rc = iopModules.FileGetStat(mem, path, scratch);
                if (rc < 0) return 0;
                // io_stat_t size often at +8 (ps2sdk); return non-zero success token.
                uint stSize = mem.Read32(scratch + 8);
                return stSize > 0 ? (int)stSize : 1;
            }

            default:
                // IRX invalid fno (0, 3, >13) returns -1; soft-success 0 is safer for probes.
                if (fno is 0 or 3)
                    return -1;
                return 0;
        }
    }

    private static string MwFileExtractPath(SystemMemory mem, uint argBuf, uint sendSize)
    {
        if (argBuf == 0) return "";
        // Inline path at +12 (IRX fno5).
        if (sendSize >= 16)
        {
            string p = ReadCString(mem, argBuf + 12, 240);
            if (LooksLikeFsPath(p) || (p.Length > 0 && p.IndexOf('.') >= 0))
                return p;
        }
        // Inline at +0
        if (sendSize >= 4)
        {
            string p = ReadCString(mem, argBuf, Math.Min((int)sendSize, 240));
            if (LooksLikeFsPath(p) || (p.Length > 2 && p.IndexOf('.') >= 0 && p.IndexOf('\0') < 0))
                return p;
        }
        // Pointer forms at +0 / +4 / +8
        foreach (uint off in new uint[] { 0, 4, 8, 12 })
        {
            if (off + 4 > sendSize) break;
            uint ptr = mem.Read32(argBuf + off);
            if (ptr < 0x00100000 || ptr >= SystemMemory.RDRAM_SIZE) continue;
            string p = ReadCString(mem, ptr, 240);
            if (LooksLikeFsPath(p) || (p.Length > 2 && p.IndexOf('.') >= 0))
                return p;
        }
        return "";
    }

    private static string MwFileNormalizePath(string path)
    {
        path = path.Trim().Replace('/', '\\');
        // host0: / atfile: → still try as-is; FILEIO host maps cdrom.
        if (path.StartsWith("host0:", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("host:", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("atfile:", StringComparison.OrdinalIgnoreCase))
        {
            int c = path.IndexOf(':');
            string rest = c >= 0 ? path[(c + 1)..].TrimStart('\\', '/') : path;
            return AliasMidwayPakPath("cdrom0:\\" + rest);
        }
        if (path.StartsWith("cdrom0:", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("cdrom:", StringComparison.OrdinalIgnoreCase))
            return AliasMidwayPakPath(path);
        // Bare relative → cdrom0
        if (path.Length > 0 && path.IndexOf(':') < 0)
            return AliasMidwayPakPath("cdrom0:\\" + path.TrimStart('\\', '/'));
        return AliasMidwayPakPath(path);
    }

    /// <summary>
    /// Midway Deception/DA path aliases: retail EE opens <c>/game/mkda.pak</c> (host-style)
    /// while the ISO root file is <c>MKDA.PAK</c>. Also normalize bare <c>mkda.pak</c>,
    /// <c>/sounds/MSLASSET.MS2</c> → ISO root, and keep art member paths intact for TOC.
    /// </summary>
    private static string AliasMidwayPakPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        string n = path.Replace('/', '\\').Trim();
        // Strip device for matching.
        string leaf = n;
        int colon = leaf.IndexOf(':');
        if (colon >= 0) leaf = leaf[(colon + 1)..].TrimStart('\\', '/');
        int semi = leaf.IndexOf(';');
        if (semi >= 0) leaf = leaf[..semi];
        if (leaf.Equals("MKDA.PAK", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("GAME\\MKDA.PAK", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("GAME/MKDA.PAK", StringComparison.OrdinalIgnoreCase)
            || leaf.EndsWith("\\MKDA.PAK", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("game\\mkda.pak", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("game/mkda.pak", StringComparison.OrdinalIgnoreCase))
            return @"cdrom0:\MKDA.PAK";
        // Dec ELF: "/sounds/MSLASSET.MS2" — ISO root file is MSLASSET.MS2 (no sounds/).
        if (leaf.Equals("SOUNDS\\MSLASSET.MS2", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("MSLASSET.MS2", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("MSLASSET.MS4", StringComparison.OrdinalIgnoreCase)
            || leaf.EndsWith("\\MSLASSET.MS2", StringComparison.OrdinalIgnoreCase)
            || leaf.EndsWith("\\MSLASSET.MS4", StringComparison.OrdinalIgnoreCase))
        {
            string ms = leaf.EndsWith("MS4", StringComparison.OrdinalIgnoreCase)
                ? "MSLASSET.MS4" : "MSLASSET.MS2";
            return @"cdrom0:\" + ms;
        }
        // Art member paths often arrive as host0:\ps2dvd\art\foo.ssf or \ps2dvd\art\...
        return path;
    }

    private static int HandleDbcMan(SystemMemory mem, uint fno, uint argBuf, uint recvBuf)
    {
        // libdbc prints versions as %d.%02x from a u16 where major=(v>>8), minor=(v&0xff).
        // Pack 3.10 as 0x0310 so (v>>8)=3, (v&0xff)=0x10.
        const uint Ver310 = 0x0310;

        // fno low byte often encodes the op. Version/init probes use 0x63 / sid|0x63.
        // Create/open-style ops (0x01, 0x04, 0x80001301, 0x80001304) need a non-version
        // handle — returning 0x310 for those made GoW treat "create" as a weird version.
        uint op = fno & 0xFF;
        bool versionProbe = op is 0x63 or 0x00 || fno == 0x80001363u;
        int result = versionProbe
            ? unchecked((int)Ver310)
            : 1 + (int)(op & 0x1F);

        if (recvBuf != 0)
        {
            mem.Write32(recvBuf, unchecked((uint)result));
            mem.Write32(recvBuf + 4, versionProbe ? Ver310 : unchecked((uint)result));
            mem.Write32(recvBuf + 8, 3);
            mem.Write32(recvBuf + 12, 0x10);
        }
        if (argBuf != 0)
        {
            mem.Write32(argBuf, unchecked((uint)result));
            mem.Write32(argBuf + 4, versionProbe ? Ver310 : unchecked((uint)result));
        }
        return result;
    }

    /// <summary>
    /// Logitech lgDevice RPC (sid=<see cref="SidLgDev"/> = 0x046D046D).
    /// Ground-truthed from Burnout 3 EE <c>lgDeviceInit</c> (0x443900..0x443B00):
    /// <list type="bullet">
    /// <item>Bind SID constructed as <c>lui/ori 0x046D046D</c>.</item>
    /// <item>fno=12 (0xC): version query. EE clears recv+4 then CallRpc; success path
    ///   requires <c>*(u32*)(recv+4) == 0x010B1B00</c> (LGDEVW 1.11.027). Else:
    ///   "lgDeviceInit: wrong version of lgDevice.irx" → infinite assert at 0x443A90.</item>
    /// <item>Other fnos: soft-success with empty device inventory (no wheel attached).</item>
    /// </list>
    /// </summary>
    private static int HandleLgDev(SystemMemory mem, uint fno, uint argBuf, uint sendSize, uint recvBuf, uint recvSize)
    {
        _ = argBuf; _ = sendSize;
        // fno 12 = version probe used by lgDeviceInit before any device enumeration.
        bool versionProbe = fno == 0xCu || fno == 12u;
        if (versionProbe)
        {
            if (recvBuf != 0)
            {
                // Layout observed: word0 = status/result (0=OK), word1 = packed version.
                mem.Write32(recvBuf + 0, 0);
                mem.Write32(recvBuf + 4, LgDevVersion_1_11_027);
                // Zero a modest prefix so later device-table walks see empty inventory.
                uint limit = recvSize > 0 ? Math.Min(recvSize, 0x40u) : 0x40u;
                for (uint o = 8; o + 4 <= limit; o += 4)
                    mem.Write32(recvBuf + o, 0);
            }
            // Skip lgDeviceInit's post-version fno=18 CallRpc path (*0x4B0400 != 0).
            // Live: that path floods cid=0 SIFCMD on the recv buffer and CreateSema thrash.
            mem.Write32(0x004B0400, 0);
            // Nop the jal CallRpc fno=18 at 0x443C3C so even a non-zero flag cannot re-enter.
            if (mem.Read32(0x00443C3C) != 0)
                mem.Write32(0x00443C3C, 0);
            // Permanent: branch the entire fno=18 setup (0x443C20) to the flag-clear +
            // epilogue (0x443C44). Survives re-entry even if jal nop is restored.
            // j 0x443C44 = 0x08000000 | (0x443C44>>2)
            mem.Write32(0x00443C20, 0x08110F11u); // j 0x00443C44
            mem.Write32(0x00443C24, 0x00000000u); // nop delay
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[RPC] HandleLgDev fno=0x{fno:X} version=0x{LgDevVersion_1_11_027:X8} recv=0x{recvBuf:X8}");
            return 0;
        }

        // Device open / enum / poll: no Logitech hardware under HLE.
        // Return 0 (success / no devices) with zeroed recv so callers take empty paths.
        // fno=18 is the post-version device-table push — same empty success.
        if (recvBuf != 0)
        {
            mem.Write32(recvBuf, 0);
            if (recvSize >= 8)
                mem.Write32(recvBuf + 4, 0);
            // Enumerators often expect a count at +0 or +4 — keep 0.
            uint limit = recvSize > 0 ? Math.Min(recvSize, 0x40u) : 0x40u;
            for (uint o = 0; o + 4 <= limit; o += 4)
                mem.Write32(recvBuf + o, 0);
        }
        return 0;
    }

    /// <summary>
    /// IOPFILE / GOE_FSRV RPC (Blood Omen 2). Low SIDs 0x20/0x21/0x29/0x30.
    /// </summary>
    /// <remarks>
    /// Reverse-truthed from disc <c>IOPFILE.IRX</c> / <c>IOPFILED.IRX</c> (<c>GOE_FSRV</c>,
    /// source <c>IOPFile.c</c>):
    /// <list type="bullet">
    /// <item>MsgHandler(fno, buf, size): fno 1=Open, 2=Start, 3=SetBytesConsumed,
    ///   4=Close, 5=Acquire, 6=Release. Jump table is <c>fno-1</c>.</item>
    /// <item>EE often calls with <c>fno=0</c> and places the op in <c>InMsg</c> word0, or
    ///   uses fno directly — accept both.</item>
    /// <item>Open InMsg: <c>+0 iStream</c>, <c>+4 flags</c>, <c>+8 path[…]</c>. Reply
    ///   (printf <c>status=%d, filesize=%d, scefd=%d, iStream=%d</c>): 4× s32 in recv.</item>
    /// <item>Stream size 0x34; up to 8 streams. Open mode O_RDONLY.</item>
    /// </list>
    /// Host open maps onto <see cref="IopModuleHost.FileOpen"/> so PS2.RKV streams and
    /// <c>cdvdSectors</c> grow.
    /// </remarks>
    private int _iopFileAcquires;
    private readonly Dictionary<int, string> _iopFileFds = new();
    private readonly Dictionary<int, int> _iopFileStreamToFd = new(); // iStream → host fd
    private readonly Dictionary<int, uint> _iopFileStreamSize = new();
    private readonly Dictionary<int, long> _iopFileStreamPos = new();

    // GOE MsgHandle* op codes (fno, or InMsg.word0 when fno==0).
    private const uint GoeOpOpen = 1;
    private const uint GoeOpStart = 2;
    private const uint GoeOpSetBytesConsumed = 3;
    private const uint GoeOpClose = 4;
    private const uint GoeOpAcquire = 5;
    private const uint GoeOpRelease = 6;

    private int HandleIopFile(SystemMemory mem, IopModuleHost iopModules, Cdvd cdvd,
        uint sid, uint fno, uint argBuf, uint sendSize, uint recvBuf, uint recvSize)
    {
        // Hex-dump first call args when tracing (helps confirm InMsg layout).
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1" && argBuf != 0 && sendSize > 0)
        {
            uint n = Math.Min(sendSize, 64u);
            var hex = new System.Text.StringBuilder((int)n * 3);
            for (uint i = 0; i < n; i++)
                hex.Append($"{mem.Read8(argBuf + i):X2} ");
            Console.Error.WriteLine(
                $"[IOPFILE] sid=0x{sid:X} fno=0x{fno:X} send={sendSize} recv=0x{recvBuf:X8}/{recvSize} arg={hex}");
        }

        // Resolve op: prefer fno in 1..6; else InMsg.word0 when fno==0 (EE pattern).
        uint op = fno;
        if (op == 0 && argBuf != 0 && sendSize >= 4)
        {
            uint w0 = mem.Read32(argBuf);
            if (w0 is >= 1 and <= 6)
                op = w0;
            else if (sendSize >= 8)
            {
                // Some clients stash op at +4 and iStream at +0.
                uint w1 = mem.Read32(argBuf + 4);
                if (w1 is >= 1 and <= 6)
                    op = w1;
            }
        }

        // Soft init / bind probe: fno=0 with empty or non-op payload.
        // Live BO2 sid=0x20 fno=0 send=48 is a DMA-channel setup blob (tags 0x09800001 /
        // 0x0A800001 ×4, recvSize=192) — not Open. Real MsgHandler invalid-fno still returns
        // the status buffer; EE treats status!=0 / non-error as "server ready".
        if (op == 0)
        {
            // status=1 (ready). Paint a larger recv so 192-byte clients see a full reply.
            WriteGoeReply(mem, recvBuf, recvSize, status: 1, filesize: 0, scefd: 0, iStream: 0);
            if (recvBuf != 0 && recvSize > 16)
            {
                for (uint off = 16; off + 4 <= Math.Min(recvSize, 192u); off += 4)
                    mem.Write32(recvBuf + off, 0);
            }
            // First successful GOE init: open PS2.RKV so archive streaming is hot and
            // cdvdSectors reflects the bigfile (token sectors; full stream on Open/Start).
            EnsureGoeArchiveMounted(iopModules, cdvd);
            // Warm real PRECODE.BG2 / CODE.BG2 so "usebigfile" / title path sees disc payloads
            // (token sector notes only; full stream on Open).
            WarmBo2CodeBg2(iopModules, cdvd);
            return 1;
        }

        switch (op)
        {
            case GoeOpAcquire:
            {
                _iopFileAcquires++;
                WriteGoeReply(mem, recvBuf, recvSize, status: 1, filesize: 0, scefd: 0, iStream: 0);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine($"[IOPFILE] Acquire n={_iopFileAcquires}");
                return 1;
            }
            case GoeOpRelease:
            {
                if (_iopFileAcquires > 0) _iopFileAcquires--;
                WriteGoeReply(mem, recvBuf, recvSize, status: 1, filesize: 0, scefd: 0, iStream: 0);
                return 1;
            }
            case GoeOpOpen:
                return HandleGoeOpen(mem, iopModules, cdvd, argBuf, sendSize, recvBuf, recvSize);
            case GoeOpStart:
                return HandleGoeStart(mem, iopModules, cdvd, argBuf, sendSize, recvBuf, recvSize);
            case GoeOpSetBytesConsumed:
                return HandleGoeSetBytesConsumed(mem, argBuf, sendSize, recvBuf, recvSize);
            case GoeOpClose:
                return HandleGoeClose(mem, iopModules, argBuf, sendSize, recvBuf, recvSize);
            default:
                // Fall through: path-bearing buffers still try open (legacy guess).
                break;
        }

        // Legacy / unknown op with embedded path → open.
        string path = argBuf != 0 && sendSize >= 8 ? ScanSendBufferForPath(mem, argBuf, sendSize) : "";
        if (!string.IsNullOrEmpty(path))
            return GoeOpenPath(mem, iopModules, cdvd, path, iStream: 0, recvBuf, recvSize);

        WriteGoeReply(mem, recvBuf, recvSize, status: 0, filesize: 0, scefd: 0, iStream: 0);
        return 0;
    }

    private static void WriteGoeReply(SystemMemory mem, uint recvBuf, uint recvSize,
        int status, uint filesize, int scefd, int iStream)
    {
        if (recvBuf == 0) return;
        // status / filesize / scefd / iStream — matches IOP "status=%d, filesize=%d, scefd=%d, iStream=%d"
        if (recvSize >= 4) mem.Write32(recvBuf + 0, unchecked((uint)status));
        if (recvSize >= 8) mem.Write32(recvBuf + 4, filesize);
        if (recvSize >= 12) mem.Write32(recvBuf + 8, unchecked((uint)scefd));
        if (recvSize >= 16) mem.Write32(recvBuf + 12, unchecked((uint)iStream));
        // Also paint a generous tail so large recv sizes still look complete.
        for (uint off = 16; off + 4 <= Math.Min(recvSize, 64u); off += 4)
            mem.Write32(recvBuf + off, 0);
    }

    private int HandleGoeOpen(SystemMemory mem, IopModuleHost iopModules, Cdvd cdvd,
        uint argBuf, uint sendSize, uint recvBuf, uint recvSize)
    {
        int iStream = 0;
        string path = "";
        if (argBuf != 0 && sendSize >= 4)
        {
            iStream = (int)mem.Read32(argBuf); // InMsg.iStream
            // Path: prefer scan, else inline @+8 (IOPFILE.IRX Open layout).
            path = ScanSendBufferForPath(mem, argBuf, sendSize);
            if (string.IsNullOrEmpty(path) && sendSize >= 12)
            {
                path = ReadCString(mem, argBuf + 8, 256);
                if (!LooksLikeFsPath(path) && path.IndexOf('.') < 0 && path.IndexOf('\\') < 0 && path.IndexOf('/') < 0)
                    path = "";
            }
            if (string.IsNullOrEmpty(path))
            {
                uint p1 = sendSize >= 8 ? mem.Read32(argBuf + 4) : 0;
                if (IsEeRamPointer(p1))
                    path = ReadCString(mem, p1 & 0x1FFFFFFFu, 256);
            }
        }
        return GoeOpenPath(mem, iopModules, cdvd, path, iStream, recvBuf, recvSize);
    }

    private int GoeOpenPath(SystemMemory mem, IopModuleHost iopModules, Cdvd cdvd,
        string path, int iStream, uint recvBuf, uint recvSize)
    {
        if (string.IsNullOrEmpty(path))
        {
            WriteGoeReply(mem, recvBuf, recvSize, status: 0, filesize: 0, scefd: -1, iStream: iStream);
            return 0;
        }

        string norm = NormalizeGoeDiscPath(path);
        int hostFd = iopModules.FileOpen(norm, 1); // O_RDONLY
        if (hostFd < 0)
        {
            // Retry common GOGAMES/BO2 rewrites.
            string alt = norm;
            if (!alt.Contains("GOGAMES", StringComparison.OrdinalIgnoreCase))
                alt = "cdrom0:\\GOGAMES\\BO2\\" + path.Replace('/', '\\').TrimStart('\\');
            hostFd = iopModules.FileOpen(alt, 1);
            if (hostFd >= 0) norm = alt;
        }
        // Archive TOC: music/streams/vo live only inside PS2.RKV.
        if (hostFd < 0)
        {
            EnsureGoeArchiveMounted(iopModules, cdvd);
            hostFd = TryOpenFromRkv(iopModules, path, out _);
            if (hostFd >= 0) norm = "rkv:" + path;
        }

        // Real PRECODE.BG2 / CODE.BG2 / MAINMENU.BG2 (and other level goefiles) on disc.
        // Game-initiated Open (vs host warm) always countSectors:true so telemetry / assists
        // see real bigfile load (CODE ~447 sectors, MAINMENU ~738).
        bool gameBg2 = false;
        if (hostFd < 0)
        {
            int bg2 = TryOpenBo2RealBg2(iopModules, cdvd, norm, countSectors: true);
            if (bg2 >= 0)
            {
                hostFd = bg2;
                gameBg2 = LooksLikeBo2GameBg2Path(norm) || LooksLikeBo2GameBg2Path(path);
            }
        }
        // Pack-resident ASSETS / .IMP / .ETP inside CODE/PRECODE goefile bigfiles.
        if (hostFd < 0)
        {
            int packFd = TryOpenBo2PackResident(iopModules, cdvd, norm, out uint packSz);
            if (packFd >= 0)
            {
                hostFd = packFd;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine(
                        $"[IOPFILE] open PACK path=\"{norm}\" fd={packFd} size={packSz}");
            }
        }
        if (hostFd < 0 && LooksLikeBo2SoftProbeStub(norm))
        {
            // Soft stub only for non-payload probes — never empty .BG2/MAINMENU/IMP/ETP.
            hostFd = iopModules.FileOpenMemoryStub(norm, Array.Empty<byte>());
            if (hostFd >= 0)
            {
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine($"[IOPFILE] open STUB path=\"{norm}\" fd={hostFd}");
            }
        }
        if (hostFd < 0)
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[IOPFILE] open FAIL path=\"{norm}\"");
            WriteGoeReply(mem, recvBuf, recvSize, status: 0, filesize: 0, scefd: -1, iStream: iStream);
            return 0;
        }

        uint fsz = 0;
        iopModules.TryGetOpenFileSize(hostFd, out fsz);
        // Count open preload for small files; large RKV is streamed so only note a token sector
        // here — real growth comes from Start/read.
        // Game BG2 already credited inside TryOpenBo2RealBg2(countSectors:true).
        if (!gameBg2 && LooksLikeDiscPath(norm) && fsz > 0)
        {
            int sectors = fsz <= 16u * 1024 * 1024
                ? (int)((fsz + 2047) / 2048)
                : 1; // token for bigfile open (PS2.RKV ~642MiB must not preload all)
            cdvd.NoteHostReadSectors(sectors);
        }

        // Close any prior stream slot re-use.
        if (_iopFileStreamToFd.TryGetValue(iStream, out int oldFd) && oldFd != hostFd)
        {
            iopModules.FileClose(oldFd);
            _iopFileFds.Remove(oldFd);
        }
        _iopFileStreamToFd[iStream] = hostFd;
        _iopFileStreamSize[iStream] = fsz;
        _iopFileStreamPos[iStream] = 0;
        _iopFileFds[hostFd] = norm;

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
        {
            string tag = gameBg2 ? "GAME BG2" : "path";
            Console.Error.WriteLine(
                $"[IOPFILE] open {tag}=\"{norm}\" fd={hostFd} size={fsz} iStream={iStream}");
        }

        WriteGoeReply(mem, recvBuf, recvSize, status: 1, filesize: fsz, scefd: hostFd, iStream: iStream);
        return 1;
    }

    /// <summary>True for BO2 code/menu goefile Open paths (game load, not soft probes).</summary>
    private static bool LooksLikeBo2GameBg2Path(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string p = path.Replace('/', '\\');
        return p.Contains("MAINMENU", StringComparison.OrdinalIgnoreCase)
            || p.Contains("PRECODE", StringComparison.OrdinalIgnoreCase)
            || p.Contains("CODE.BG2", StringComparison.OrdinalIgnoreCase)
            || (p.Contains(".BG2", StringComparison.OrdinalIgnoreCase)
                && (p.Contains("CODE", StringComparison.OrdinalIgnoreCase)
                    || p.Contains("LEVELS", StringComparison.OrdinalIgnoreCase)
                    || p.Contains("GOGAMES", StringComparison.OrdinalIgnoreCase)));
    }

    private static string NormalizeGoeDiscPath(string path)
    {
        string norm = path.Replace('/', '\\').Trim();
        // ArchiveFile="gogames\bo2\ps2.rkv" / "whiplash\ps2.rkv" / basePath-relative names.
        if (!norm.Contains(':'))
        {
            string rest = norm.TrimStart('\\');
            // Whiplash GAME.INI: ArchivePs2="whiplash/ps2.rkv", gamepath="whiplash"
            if (rest.StartsWith("whiplash\\", StringComparison.OrdinalIgnoreCase)
                || rest.StartsWith("whiplash/", StringComparison.OrdinalIgnoreCase)
                || rest.Equals("whiplash", StringComparison.OrdinalIgnoreCase))
            {
                string leaf = rest.StartsWith("whiplash", StringComparison.OrdinalIgnoreCase)
                    ? rest["whiplash".Length..].TrimStart('\\', '/')
                    : rest;
                norm = string.IsNullOrEmpty(leaf)
                    ? @"cdrom0:\WHIPLASH"
                    : @"cdrom0:\WHIPLASH\" + leaf.Replace('/', '\\');
            }
            else if (norm.StartsWith("gogames", StringComparison.OrdinalIgnoreCase)
                || norm.StartsWith("\\gogames", StringComparison.OrdinalIgnoreCase)
                || norm.Contains("PS2.RKV", StringComparison.OrdinalIgnoreCase)
                || norm.Contains(".rkv", StringComparison.OrdinalIgnoreCase)
                || norm.Contains("GAME.ERG", StringComparison.OrdinalIgnoreCase)
                || norm.Contains(".BG2", StringComparison.OrdinalIgnoreCase)
                || norm.Contains("PRECODE", StringComparison.OrdinalIgnoreCase)
                || norm.Contains("CODE.BG2", StringComparison.OrdinalIgnoreCase)
                || norm.Contains("MAINMENU", StringComparison.OrdinalIgnoreCase)
                || rest.Equals("CODE", StringComparison.OrdinalIgnoreCase)
                || rest.Equals("PRECODE", StringComparison.OrdinalIgnoreCase)
                || rest.StartsWith("resources\\", StringComparison.OrdinalIgnoreCase)
                || rest.StartsWith("assets\\", StringComparison.OrdinalIgnoreCase)
                || rest.StartsWith("levels\\", StringComparison.OrdinalIgnoreCase))
            {
                // gogames\bo2\ps2.rkv → cdrom0:\GOGAMES\BO2\PS2.RKV
                if (rest.StartsWith("gogames\\bo2\\", StringComparison.OrdinalIgnoreCase))
                    rest = rest["gogames\\bo2\\".Length..];
                else if (rest.StartsWith("gogames\\", StringComparison.OrdinalIgnoreCase))
                    rest = rest["gogames\\".Length..];
                // Bare PS2.RKV on Whiplash lives at WHIPLASH/PS2.RKV — try that prefix first
                // for non-gogames relative names (BO2 still has GOGAMES\BO2\PS2.RKV).
                if (rest.Equals("PS2.RKV", StringComparison.OrdinalIgnoreCase)
                    || rest.Equals("ps2.rkv", StringComparison.OrdinalIgnoreCase))
                    norm = @"cdrom0:\WHIPLASH\PS2.RKV";
                // Bare CODE / PRECODE / MAINMENU tokens from StartBigFile / usebigfile.
                else if (rest.Equals("CODE", StringComparison.OrdinalIgnoreCase))
                    norm = @"cdrom0:\GOGAMES\BO2\CODE.BG2";
                else if (rest.Equals("PRECODE", StringComparison.OrdinalIgnoreCase))
                    norm = @"cdrom0:\GOGAMES\BO2\PRECODE.BG2";
                else if (rest.Equals("MAINMENU", StringComparison.OrdinalIgnoreCase)
                    || rest.Equals("MAINMENU.BG2", StringComparison.OrdinalIgnoreCase))
                    norm = @"cdrom0:\GOGAMES\BO2\RESOURCES\LEVELS\UI\MAINMENU.BG2";
                else
                    norm = "cdrom0:\\GOGAMES\\BO2\\" + rest;
            }
            else
            {
                norm = "cdrom0:\\" + norm.TrimStart('\\');
            }
        }
        // Strip version ";1"
        int semi = norm.IndexOf(';');
        if (semi > 0) norm = norm[..semi];
        return norm;
    }

    private int HandleGoeStart(SystemMemory mem, IopModuleHost iopModules, Cdvd cdvd,
        uint argBuf, uint sendSize, uint recvBuf, uint recvSize)
    {
        // Start: iStream @+0, byteCount @+4, eeDest @+8 (IOPFILE.IRX stores +4/+8 into stream).
        int iStream = argBuf != 0 && sendSize >= 4 ? (int)mem.Read32(argBuf) : 0;
        uint byteCount = argBuf != 0 && sendSize >= 8 ? mem.Read32(argBuf + 4) : 0;
        uint eeDest = argBuf != 0 && sendSize >= 12 ? mem.Read32(argBuf + 8) : 0;
        eeDest &= 0x1FFFFFFFu;

        if (!_iopFileStreamToFd.TryGetValue(iStream, out int fd))
        {
            WriteGoeReply(mem, recvBuf, recvSize, status: 0, filesize: 0, scefd: -1, iStream: iStream);
            return 0;
        }

        // Perform the transfer immediately (real IOP uses a stream thread + SifSetDma).
        int n = 0;
        if (eeDest != 0 && byteCount is > 0 and < 0x1000000)
        {
            n = iopModules.FileRead(mem, fd, eeDest, byteCount);
            if (n > 0)
            {
                cdvd.NoteHostReadSectors((n + 2047) / 2048);
                if (_iopFileStreamPos.TryGetValue(iStream, out long pos))
                    _iopFileStreamPos[iStream] = pos + n;
            }
        }
        else if (byteCount is > 0 and < 0x1000000)
        {
            // No dest in packet — still advance pos / credit sectors (async DMA model).
            n = (int)byteCount;
            cdvd.NoteHostReadSectors((n + 2047) / 2048);
            if (_iopFileStreamPos.TryGetValue(iStream, out long pos))
                _iopFileStreamPos[iStream] = pos + n;
            // Seek host fd so subsequent reads continue.
            iopModules.FileSeek(fd, n, 1); // SEEK_CUR
        }

        uint fsz = _iopFileStreamSize.TryGetValue(iStream, out uint s) ? s : 0;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine(
                $"[IOPFILE] Start iStream={iStream} dest=0x{eeDest:X8} want={byteCount} n={n}");
        WriteGoeReply(mem, recvBuf, recvSize, status: 1, filesize: fsz, scefd: fd, iStream: iStream);
        return n > 0 ? n : 1;
    }

    private int HandleGoeSetBytesConsumed(SystemMemory mem, uint argBuf, uint sendSize,
        uint recvBuf, uint recvSize)
    {
        int iStream = argBuf != 0 && sendSize >= 4 ? (int)mem.Read32(argBuf) : 0;
        uint consumed = argBuf != 0 && sendSize >= 8 ? mem.Read32(argBuf + 4) : 0;
        // Real code aligns consumed &= ~0x7FF.
        consumed &= ~0x7FFu;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine($"[IOPFILE] SetBytesConsumed iStream={iStream} n={consumed}");
        WriteGoeReply(mem, recvBuf, recvSize, status: 1, filesize: consumed, scefd: 0, iStream: iStream);
        return 1;
    }

    private int HandleGoeClose(SystemMemory mem, IopModuleHost iopModules,
        uint argBuf, uint sendSize, uint recvBuf, uint recvSize)
    {
        int iStream = argBuf != 0 && sendSize >= 4 ? (int)mem.Read32(argBuf) : 0;
        int rc = 0;
        if (_iopFileStreamToFd.TryGetValue(iStream, out int fd))
        {
            rc = iopModules.FileClose(fd);
            _iopFileStreamToFd.Remove(iStream);
            _iopFileStreamSize.Remove(iStream);
            _iopFileStreamPos.Remove(iStream);
            _iopFileFds.Remove(fd);
        }
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine($"[IOPFILE] Close iStream={iStream} rc={rc}");
        WriteGoeReply(mem, recvBuf, recvSize, status: 1, filesize: 0, scefd: 0, iStream: iStream);
        return 1;
    }

    private bool _goeArchiveMounted;
    private bool _bo2CodeBg2Warmed;
    private int _goeArchiveFd = -1;
    private uint _goeArchiveDiscByteOffset; // absolute disc byte offset of PS2.RKV start
    private uint _goeArchiveSize;
    /// <summary>RKV TOC: normalized lowercase path → (offset within RKV, size).</summary>
    private readonly Dictionary<string, (uint Offset, uint Size)> _rkvToc =
        new(StringComparer.OrdinalIgnoreCase);
    private int _rkvTocCount;

    /// <summary>
    /// Touch real PRECODE.BG2 + CODE.BG2 on disc (Blood Omen 2 usebigfile path) so path
    /// resolution proves goefile payloads are reachable before title.
    /// Does <b>not</b> credit <see cref="Cdvd.SectorsRead"/> — host warm ≠ game load.
    /// Inflating cdvd via warm (≈+1130 sectors) tripped title assists (WaitSema leave /
    /// menu-kick at cdvd≥1600) before GOE bind sid=0x29 and ENGLISH.DIR / PRECODE Open.
    /// </summary>
    private void WarmBo2CodeBg2(IopModuleHost iopModules, Cdvd cdvd)
    {
        if (_bo2CodeBg2Warmed) return;
        _bo2CodeBg2Warmed = true;
        foreach (string name in new[] { "PRECODE.BG2", "CODE.BG2", @"RESOURCES\LEVELS\UI\MAINMENU.BG2" })
        {
            // countSectors: false — probe open only; game Open will credit real sectors.
            int fd = TryOpenBo2RealBg2(iopModules, cdvd, @"cdrom0:\GOGAMES\BO2\" + name,
                countSectors: false);
            if (fd >= 0)
            {
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1"
                    && iopModules.TryGetOpenFileSize(fd, out uint sz))
                    Console.Error.WriteLine($"[BO2] warm {name} size={sz} (no sector credit)");
                iopModules.FileClose(fd);
            }
        }
    }

    /// <summary>
    /// Mount PS2.RKV once after GOE init (Blood Omen 2: GOGAMES/BO2; Whiplash: WHIPLASH/),
    /// parse TOC, and make archive entries openable via <see cref="TryOpenFromRkv"/>.
    /// </summary>
    private void EnsureGoeArchiveMounted(IopModuleHost iopModules, Cdvd cdvd)
    {
        if (_goeArchiveMounted) return;
        _goeArchiveMounted = true;
        // Prefer disc layout by title serial family. Whiplash GAME.INI:
        //   ArchivePs2="whiplash/ps2.rkv" → WHIPLASH/PS2.RKV on ISO.
        string[] candidates =
        {
            @"cdrom0:\WHIPLASH\PS2.RKV",
            @"cdrom0:/WHIPLASH/PS2.RKV;1",
            @"cdrom0:\GOGAMES\BO2\PS2.RKV",
            @"cdrom0:/GOGAMES/BO2/PS2.RKV;1",
            @"cdrom0:\PS2.RKV",
        };
        int fd = -1;
        string rkvPath = candidates[0];
        foreach (string c in candidates)
        {
            fd = iopModules.FileOpen(c, 1);
            if (fd >= 0) { rkvPath = c; break; }
        }
        if (fd < 0)
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine("[IOPFILE] PS2.RKV mount FAIL");
            return;
        }
        _goeArchiveFd = fd;
        _iopFileFds[fd] = rkvPath;
        if (iopModules.TryGetOpenFileSize(fd, out uint fsz) && fsz > 0)
        {
            _goeArchiveSize = fsz;
            // Token sectors for telemetry (do not preload 642 MiB).
            uint tocHint = Math.Min(fsz, 0x20000u);
            cdvd.NoteHostReadSectors((int)((tocHint + 2047) / 2048));
            cdvd.NoteHostReadSectors(256);
            // Resolve absolute disc byte offset of the RKV for virtual sub-file streams.
            // Streamed open: Lba * 2048 is the file start (Position starts at 0 for full file).
            // We re-open via ISO path through a short TOC read into a temp buffer.
            ParseRkvToc(iopModules, cdvd, fd, fsz);
        }
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine(
                $"[IOPFILE] PS2.RKV mounted fd={fd} size={_goeArchiveSize} tocEntries={_rkvTocCount}");
    }

    /// <summary>
    /// Parse PS2.RKV TOC (length-prefixed names + 16-byte trailer starting at name null).
    /// Layout ground-truthed 2026-07-30: header u32×4 (ver, tocBytes≈0x19FF8, …);
    /// then entries: u32 strlen, char name[strlen], then 16 B at null:
    ///   w0 pad, w1 size-or-id, w2 offset (or swapped when w2 &lt; tocSize), w3 pad.
    /// RKV holds audio (music/streams/vo) only — game assets live in CODE.BG2 / RESOURCES.
    /// </summary>
    private void ParseRkvToc(IopModuleHost iopModules, Cdvd cdvd, int fd, uint fsz)
    {
        _rkvToc.Clear();
        _rkvTocCount = 0;
        // Read TOC prefix into EE scratch via FileRead, then parse host-side from a local buf.
        // Prefer a host-side read through FileSeek/FileRead into a temporary SystemMemory window
        // is heavy — instead use a small managed buffer via FileRead into a scratch EE addr.
        const uint Scratch = 0x01FC8000;
        const uint MaxToc = 0x1A000;
        uint tocBytes = Math.Min(fsz, MaxToc);
        // Ensure fd at start.
        iopModules.FileSeek(fd, 0, 0);
        // We need a SystemMemory — pull from any bound path: re-read via Cdvd disc if needed.
        // Use incremental FileRead into scratch requires mem; defer to ParseRkvTocFromHost.
        ParseRkvTocFromHost(iopModules, fd, tocBytes, out uint archiveLba);
        _goeArchiveDiscByteOffset = archiveLba * 2048u;
        cdvd.NoteHostReadSectors((int)((tocBytes + 2047) / 2048));
        _ = Scratch;
    }

    private void ParseRkvTocFromHost(IopModuleHost iopModules, int fd, uint tocBytes, out uint archiveLba)
    {
        archiveLba = 0;
        // FileRead needs SystemMemory — allocate a local byte[] by reading through a private path:
        // Use reflection-free approach: seek+read in chunks via TryGetOpenFileSize and
        // IopModuleHost's disc volume. Add a host-side byte read helper.
        if (!iopModules.TryReadOpenFileBytes(fd, 0, (int)tocBytes, out byte[]? toc) || toc == null)
            return;
        // Recover archive LBA from open file metadata if available.
        archiveLba = iopModules.TryGetOpenFileLba(fd, out uint lba) ? lba : 0;

        if (toc.Length < 32) return;
        uint ver = BitConverter.ToUInt32(toc, 0);
        uint tocSizeField = BitConverter.ToUInt32(toc, 4);
        if (ver != 1 || tocSizeField < 0x100) return;
        int limit = (int)Math.Min(tocSizeField, (uint)toc.Length);
        uint tocFloor = tocSizeField;
        var offsets = new List<(string Name, uint Off, uint Sz)>();

        // --- Format A (Blood Omen 2): u32 nlen, name[nlen] incl. implicit null trailer,
        // 16 B at null: pad/size/off/pad. First file entries typically at 0x30. ---
        int p = 0x30;
        while (p + 4 < limit)
        {
            uint nlen = BitConverter.ToUInt32(toc, p);
            if (nlen is < 1 or > 180) break;
            int ns = p + 4;
            if (ns + (int)nlen + 16 > limit) break;
            bool bad = false;
            for (int i = 0; i < (int)nlen; i++)
            {
                byte b = toc[ns + i];
                if (b < 32 || b > 126) { bad = true; break; }
            }
            if (bad) break;
            string name = System.Text.Encoding.ASCII.GetString(toc, ns, (int)nlen);
            int block = ns + (int)nlen; // starts at null
            uint w0 = BitConverter.ToUInt32(toc, block);
            uint w1 = BitConverter.ToUInt32(toc, block + 4);
            uint w2 = BitConverter.ToUInt32(toc, block + 8);
            uint w3 = BitConverter.ToUInt32(toc, block + 12);
            _ = w0; _ = w3;
            uint off, sz;
            if (w2 >= tocFloor && w2 < _goeArchiveSize)
            { off = w2; sz = w1; }
            else if (w1 >= tocFloor && w1 < _goeArchiveSize)
            { off = w1; sz = w2; }
            else
            { off = w2; sz = w1; }
            offsets.Add((name, off, sz));
            p = block + 16;
        }

        // --- Format B (Whiplash SLUS_206.84): no null after name.
        // Sliding scan: u32 nlen, char name[nlen], u32 type==1, u32 size, u32 offset, u32 pad.
        // Folder headers interleave with shorter records; byte-scan is robust.
        // Live 2026-07-30: ver=1 tocBytes=0x1C7F8, names "Code","firstscreen","frontend",… ---
        if (offsets.Count == 0)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (p = 0x0C; p + 24 < limit; p += 4)
            {
                uint nlen = BitConverter.ToUInt32(toc, p);
                if (nlen is < 1 or > 64) continue;
                int ns = p + 4;
                if (ns + (int)nlen + 16 > limit) continue;
                bool bad = false;
                for (int i = 0; i < (int)nlen; i++)
                {
                    byte b = toc[ns + i];
                    // Names are [A-Za-z0-9_./-]
                    if (!((b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9')
                          || b is (byte)'_' or (byte)'.' or (byte)'/' or (byte)'-' or (byte)'\\'))
                    { bad = true; break; }
                }
                if (bad) continue;
                string name = System.Text.Encoding.ASCII.GetString(toc, ns, (int)nlen);
                int block = ns + (int)nlen;
                uint type = BitConverter.ToUInt32(toc, block);
                if (type != 1) continue;
                uint w1 = BitConverter.ToUInt32(toc, block + 4);
                uint w2 = BitConverter.ToUInt32(toc, block + 8);
                uint off = w2, sz = w1;
                if (off < tocFloor || off >= _goeArchiveSize)
                {
                    if (w1 >= tocFloor && w1 < _goeArchiveSize)
                    { off = w1; sz = w2; }
                    else
                        continue;
                }
                if (!seen.Add(name)) continue;
                offsets.Add((name, off, sz));
            }
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[IOPFILE] RKV format-B (Whiplash-style) candidates={offsets.Count}");
        }

        // Fill zero sizes from next offset delta (sorted by offset).
        var byOff = offsets.Where(e => e.Off >= tocFloor && e.Off < Math.Max(_goeArchiveSize, tocFloor + 1))
            .OrderBy(e => e.Off).ToList();
        for (int i = 0; i < byOff.Count; i++)
        {
            uint sz = byOff[i].Sz;
            if (sz == 0 || sz > _goeArchiveSize)
            {
                uint next = i + 1 < byOff.Count ? byOff[i + 1].Off : _goeArchiveSize;
                if (next > byOff[i].Off)
                    sz = next - byOff[i].Off;
            }
            if (sz == 0 || sz > _goeArchiveSize) continue;
            string key = byOff[i].Name.Replace('\\', '/').Trim().ToLowerInvariant();
            _rkvToc[key] = (byOff[i].Off, sz);
            // Also basename key for loose lookups.
            int slash = key.LastIndexOf('/');
            if (slash >= 0)
            {
                string baseName = key[(slash + 1)..];
                if (!_rkvToc.ContainsKey(baseName))
                    _rkvToc[baseName] = (byOff[i].Off, sz);
            }
        }
        _rkvTocCount = _rkvToc.Count;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine($"[IOPFILE] RKV TOC parsed entries={_rkvTocCount} (raw={offsets.Count})");
    }

    /// <summary>Open a path from the mounted PS2.RKV TOC as a virtual disc stream.</summary>
    private int TryOpenFromRkv(IopModuleHost iopModules, string path, out uint size)
    {
        size = 0;
        if (_rkvTocCount == 0 || _goeArchiveSize == 0) return -1;
        string key = path.Replace('\\', '/').Trim().ToLowerInvariant();
        // Strip cdrom0: / gogames/bo2/ prefixes.
        int colon = key.IndexOf(':');
        if (colon >= 0) key = key[(colon + 1)..].TrimStart('/');
        if (key.StartsWith("gogames/bo2/")) key = key["gogames/bo2/".Length..];
        if (key.StartsWith("ps2.rkv/")) key = key["ps2.rkv/".Length..];
        int semi = key.IndexOf(';');
        if (semi > 0) key = key[..semi];

        if (!_rkvToc.TryGetValue(key, out var ent))
        {
            // Basename fallback.
            int slash = key.LastIndexOf('/');
            if (slash >= 0)
                _rkvToc.TryGetValue(key[(slash + 1)..], out ent);
        }
        if (ent.Size == 0 && ent.Offset == 0 && !_rkvToc.ContainsKey(key))
            return -1;
        if (ent.Size == 0) return -1;

        size = ent.Size;
        // Need archive base disc offset. If unknown, open via host read of full RKV is too big —
        // require LBA from mount.
        if (_goeArchiveDiscByteOffset == 0 && _goeArchiveFd >= 0)
        {
            if (iopModules.TryGetOpenFileLba(_goeArchiveFd, out uint lba))
                _goeArchiveDiscByteOffset = lba * 2048u;
        }
        if (_goeArchiveDiscByteOffset == 0) return -1;
        long abs = (long)_goeArchiveDiscByteOffset + ent.Offset;
        if (abs > uint.MaxValue) return -1;
        int vfd = iopModules.FileOpenVirtualStream(
            "rkv:" + key, (uint)abs, ent.Size);
        return vfd;
    }

    // -------------------------------------------------------------------------
    // Midway MKDA.PAK — shared Deception (SLUS_208.81) / Deadly Alliance archive.
    //
    // Ground-truthed 2026-07-30 from retail ISOs:
    //   Header @0 (LE u32): magic 0x50414B20 ('PAK '), ver 0x100, count N,
    //   payload_size, first_data_hint.
    //   Trailing TOC of (file_size - payload_size) bytes at EOF:
    //     skip 4 B, then N × { u32 offset, u32 size, u32 name_rel }
    //     name table starts immediately after entries; names are
    //     "\ps2dvd\art\foo.ssf" (Deception) / "\ps2dvd\artps2\foo.ssf" (DA).
    //     name_rel is relative to (name_table_base - first_name_rel), empirically
    //     name_table_start - 4 when the first string begins 4 B before entry_end.
    //   Members are nested SEC containers (magic 'SEC '), not MWo3 overlays.
    //   GAMEFD.ovl is NOT in the PAK — GAMER.OVL is a 384 B MWo3 stub on ISO root.
    // -------------------------------------------------------------------------
    private bool _mkdaPakMounted;
    private int _mkdaPakFd = -1;
    private uint _mkdaPakDiscByteOffset;
    private uint _mkdaPakSize;
    private int _mkdaPakTocCount;
    /// <summary>Normalized lowercase path → (offset within PAK, size).</summary>
    private readonly Dictionary<string, (uint Offset, uint Size)> _mkdaPakToc =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mount ISO-root MKDA.PAK and parse trailing TOC once.</summary>
    private void EnsureMkdaPakMounted(IopModuleHost iopModules, Cdvd cdvd)
    {
        if (_mkdaPakMounted) return;
        _mkdaPakMounted = true;
        string[] candidates =
        {
            @"cdrom0:\MKDA.PAK",
            @"cdrom0:/MKDA.PAK;1",
            @"cdrom0:\MKDA.PAK;1",
        };
        int fd = -1;
        string pakPath = candidates[0];
        foreach (string c in candidates)
        {
            fd = iopModules.FileOpen(c, 1);
            if (fd >= 0) { pakPath = c; break; }
        }
        if (fd < 0)
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine("[IOPFILE] MKDA.PAK mount FAIL");
            return;
        }
        _mkdaPakFd = fd;
        if (!iopModules.TryGetOpenFileSize(fd, out uint fsz) || fsz < 64)
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[IOPFILE] MKDA.PAK bad size={fsz}");
            return;
        }
        _mkdaPakSize = fsz;
        if (iopModules.TryGetOpenFileLba(fd, out uint lba))
            _mkdaPakDiscByteOffset = lba * 2048u;
        // Token sectors (do not preload ~750 MiB).
        cdvd.NoteHostReadSectors(8);
        ParseMkdaPakToc(iopModules, fd, fsz);
        // Free the mount FD — virtual member streams only need disc byte offset + TOC.
        // (Keeps IOMAN slots free for the title's own MKDA.PAK / FILEIO opens.)
        try { iopModules.FileClose(fd); } catch { /* ignore */ }
        _mkdaPakFd = -1;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine(
                $"[IOPFILE] MKDA.PAK mounted path=\"{pakPath}\" size={_mkdaPakSize} " +
                $"tocEntries={_mkdaPakTocCount} discOff=0x{_mkdaPakDiscByteOffset:X}");
    }

    private void ParseMkdaPakToc(IopModuleHost iopModules, int fd, uint fsz)
    {
        _mkdaPakToc.Clear();
        _mkdaPakTocCount = 0;
        // Header: magic, ver, count, payload_size, field16
        if (!iopModules.TryReadOpenFileBytes(fd, 0, 20, out byte[]? head) || head == null || head.Length < 20)
            return;
        uint magic = BitConverter.ToUInt32(head, 0);
        // On disk LE of 'PAK ' is bytes 20 4B 41 50 → u32 0x50414B20.
        if (magic != 0x50414B20u)
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[IOPFILE] MKDA.PAK bad magic 0x{magic:X8}");
            return;
        }
        uint ver = BitConverter.ToUInt32(head, 4);
        uint count = BitConverter.ToUInt32(head, 8);
        uint payload = BitConverter.ToUInt32(head, 12);
        if (ver != 0x100 || count is 0 or > 100_000 || payload >= fsz)
            return;
        uint tocBytes = fsz - payload;
        if (tocBytes < 16 || tocBytes > 2 * 1024 * 1024)
            return;
        // Read trailing TOC.
        if (!iopModules.TryReadOpenFileBytes(fd, (int)payload, (int)tocBytes, out byte[]? toc)
            || toc == null || toc.Length < 16)
            return;

        // Entries start at +4: {off, size, name_rel} × count
        int entryBase = 4;
        int need = entryBase + (int)count * 12;
        if (need > toc.Length) return;
        int nameTable = need; // immediately after entries

        // Name heap: first path C-string near entry_end (often 4 B earlier for leading '\').
        // Live Deception: firstString@0x6CC, first nrel=0x18 → name at firstString+nrel (ashrah.ssf).
        int firstStringOff = nameTable;
        for (int probe = Math.Max(0, nameTable - 8); probe < Math.Min(toc.Length, nameTable + 8); probe++)
        {
            if (toc[probe] is (byte)'\\' or (byte)'/')
            {
                firstStringOff = probe;
                break;
            }
        }
        uint firstNrel = BitConverter.ToUInt32(toc, entryBase + 8);
        int[] baseCandidates =
        {
            firstStringOff,                  // abs = firstString + nrel (validated Deception)
            firstStringOff - (int)firstNrel, // abs = origin + nrel maps first nrel → firstString
            nameTable,
            0,
        };
        int nameBase = firstStringOff;
        foreach (int cand in baseCandidates)
        {
            string? trial = ReadMkdaTocName(toc, cand + (int)firstNrel);
            if (trial != null && trial.IndexOf('.') >= 0 && (trial.Contains('\\') || trial.Contains('/')))
            {
                nameBase = cand;
                break;
            }
        }

        int parsed = 0;
        for (int i = 0; i < (int)count; i++)
        {
            int p = entryBase + i * 12;
            uint off = BitConverter.ToUInt32(toc, p);
            uint sz = BitConverter.ToUInt32(toc, p + 4);
            uint nrel = BitConverter.ToUInt32(toc, p + 8);
            if (off >= fsz || sz == 0 || off + sz > fsz + 0x1000)
                continue;
            string? name = ReadMkdaTocName(toc, nameBase + (int)nrel);
            if (name == null)
                name = ReadMkdaTocName(toc, (int)nrel);
            if (name == null)
                continue;
            parsed++;
            string key = NormalizeMkdaMemberKey(name);
            if (key.Length == 0) continue;
            _mkdaPakToc[key] = (off, sz);
            // Basename key for loose lookups (startup.ssf).
            int slash = key.LastIndexOf('/');
            if (slash >= 0)
            {
                string baseName = key[(slash + 1)..];
                if (!_mkdaPakToc.ContainsKey(baseName))
                    _mkdaPakToc[baseName] = (off, sz);
            }
        }
        _mkdaPakTocCount = _mkdaPakToc.Count;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine(
                $"[IOPFILE] MKDA.PAK TOC count={count} parsed={parsed} keys={_mkdaPakTocCount} " +
                $"tocBytes={tocBytes} nameBase=0x{nameBase:X}");
    }

    private static string? ReadMkdaTocName(byte[] toc, int off)
    {
        if (off < 0 || off >= toc.Length) return null;
        int e = off;
        while (e < toc.Length && e - off < 200 && toc[e] != 0)
        {
            byte b = toc[e];
            if (b < 32 || b > 126) return null;
            e++;
        }
        if (e == off) return null;
        if (e < toc.Length && toc[e] != 0) return null;
        return System.Text.Encoding.ASCII.GetString(toc, off, e - off);
    }

    private static string NormalizeMkdaMemberKey(string name)
    {
        string k = name.Replace('\\', '/').Trim().ToLowerInvariant();
        int semi = k.IndexOf(';');
        if (semi >= 0) k = k[..semi];
        // Strip device if present.
        int colon = k.IndexOf(':');
        if (colon >= 0) k = k[(colon + 1)..];
        while (k.StartsWith('/')) k = k[1..];
        return k;
    }

    /// <summary>
    /// True when path looks like a Midway MKDA.PAK art member (gameart.ssf / ps2dvd art).
    /// Used to prefer virtual PAK open before ISO FileOpen for member-shaped paths.
    /// </summary>
    private static bool LooksLikeMkdaMemberPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string p = path.Replace('\\', '/');
        if (p.Contains("ps2dvd/", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.Contains("/art/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/artps2/", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.EndsWith(".ssf", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.Contains("gameart", StringComparison.OrdinalIgnoreCase)) return true;
        // Host-style leaf without device (artps2 members).
        string leaf = p;
        int slash = leaf.LastIndexOf('/');
        if (slash >= 0) leaf = leaf[(slash + 1)..];
        if (leaf.EndsWith(".ssf", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Open a path from the mounted MKDA.PAK TOC as a virtual disc stream.</summary>
    private int TryOpenFromMkdaPak(IopModuleHost iopModules, string path, out uint size)
    {
        size = 0;
        if (_mkdaPakTocCount == 0 || _mkdaPakSize == 0) return -1;
        string key = NormalizeMkdaMemberKey(path);
        if (key.Length == 0) return -1;

        (uint Offset, uint Size) ent = default;
        bool found = false;
        var alts = new List<string> { key };
        if (key.StartsWith("game/")) alts.Add(key["game/".Length..]);
        // Dec asset tables use bare "gameart.ssf"; host paths use ps2dvd/art or /art/.
        alts.Add("ps2dvd/art/" + key);
        alts.Add("ps2dvd/artps2/" + key);
        if (key.StartsWith("art/")) alts.Add("ps2dvd/" + key);
        if (key.StartsWith("artps2/")) alts.Add("ps2dvd/" + key);
        if (key.StartsWith("ps2dvd/art/")) alts.Add(key["ps2dvd/art/".Length..]);
        if (key.StartsWith("ps2dvd/artps2/")) alts.Add(key["ps2dvd/artps2/".Length..]);
        if (key.StartsWith("ps2dvd/")) alts.Add(key["ps2dvd/".Length..]);
        if (key.StartsWith("art/")) alts.Add(key["art/".Length..]);
        if (key.StartsWith("artps2/")) alts.Add(key["artps2/".Length..]);
        int slash = key.LastIndexOf('/');
        if (slash >= 0) alts.Add(key[(slash + 1)..]);

        foreach (string a in alts)
        {
            if (_mkdaPakToc.TryGetValue(a, out ent) && ent.Size > 0)
            {
                found = true;
                key = a;
                break;
            }
        }
        if (!found) return -1;

        size = ent.Size;
        if (_mkdaPakDiscByteOffset == 0 && _mkdaPakFd >= 0)
        {
            if (iopModules.TryGetOpenFileLba(_mkdaPakFd, out uint lba))
                _mkdaPakDiscByteOffset = lba * 2048u;
        }
        if (_mkdaPakDiscByteOffset == 0) return -1;
        long abs = (long)_mkdaPakDiscByteOffset + ent.Offset;
        if (abs > uint.MaxValue) return -1;
        return iopModules.FileOpenVirtualStream("mkda:" + key, (uint)abs, ent.Size);
    }

    // -------------------------------------------------------------------------
    // Blood Omen 2 pack-resident assets (CODE.BG2 / PRECODE.BG2 / MAINMENU.BG2).
    //
    // Retail disc (usebigfile=1): entity .IMP/.ETP under ASSETS/ are NOT ISO leaves —
    // they are baked into Crystal Dynamics "goefile" bigfiles. Live FILEIO of
    // cdrom0:\GOGAMES\BO2\ASSETS\ETYPES\KAIN\KAIN.IMP → honest ENOENT without this HLE.
    //
    // goefile layout (ground-truthed 2026-07-31): tag[8] + size(incl. 16-byte hdr) + flags
    // + payload; nested "goefile" regions are real sub-packages. Path strings live in
    // "symlist" payloads. Prefer MEMBER EXTRACT (nested goefile slice) over whole-parent
    // serve so factory parsers see a single package stream — not the entire CODE.BG2
    // (914 KiB) for one .etp. Root-only symbols (kain.imp in PRECODE) still map to the
    // parent goefile at offset 0 (the package *is* the member).
    // -------------------------------------------------------------------------
    private bool _bo2PackIndexBuilt;
    /// <summary>Normalized relative path → member location inside a parent pack.</summary>
    private readonly Dictionary<string, Bo2PackMember> _bo2PackMembers =
        new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Parent pack disc path → raw goefile bytes (≤16 MiB packs only).</summary>
    private readonly Dictionary<string, byte[]> _bo2PackBytes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly struct Bo2PackMember
    {
        public readonly string Parent;
        public readonly int Offset;
        public readonly int Size;
        public Bo2PackMember(string parent, int offset, int size)
        {
            Parent = parent;
            Offset = offset;
            Size = size;
        }
    }

    /// <summary>
    /// Open a pack-resident BO2 asset (.IMP/.ETP/ASSETS/…) from PRECODE/CODE/MAINMENU
    /// goefile bigfiles when the path is not an ISO leaf. Serves the member slice when
    /// known (nested goefile), else the parent goefile for root-level symbols.
    /// </summary>
    private int TryOpenBo2PackResident(IopModuleHost iopModules, Cdvd cdvd, string path,
        out uint size)
    {
        size = 0;
        if (string.IsNullOrEmpty(path) || !LooksLikeBo2PackResidentPath(path))
            return -1;
        EnsureBo2PackIndex(iopModules, cdvd);
        if (_bo2PackMembers.Count == 0) return -1;

        string key = NormalizeBo2PackMemberKey(path);
        if (string.IsNullOrEmpty(key)) return -1;

        if (!_bo2PackMembers.TryGetValue(key, out Bo2PackMember member))
        {
            // Basename fallback (KAIN.IMP → assets/etypes/kain/kain.imp). Prefer tightest.
            int slash = key.LastIndexOf('/');
            string baseName = slash >= 0 ? key[(slash + 1)..] : key;
            if (baseName.Length < 3) return -1;
            Bo2PackMember? best = null;
            string? bestKey = null;
            foreach (var kv in _bo2PackMembers)
            {
                if (!(kv.Key.EndsWith("/" + baseName, StringComparison.OrdinalIgnoreCase)
                      || kv.Key.Equals(baseName, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (best is null || kv.Value.Size < best.Value.Size)
                {
                    best = kv.Value;
                    bestKey = kv.Key;
                }
            }
            if (best is null || string.IsNullOrEmpty(bestKey)) return -1;
            member = best.Value;
            key = bestKey;
        }

        string parent = member.Parent;
        if (string.IsNullOrEmpty(parent)) return -1;

        // Serve MEMBER bytes (slice of parent goefile). Do NOT inflate cdvd via unopened
        // CODE/MAINMENU NoteHostReadSectors — game Open of those packs is the honest signal.
        if (_bo2PackBytes.TryGetValue(parent, out byte[]? bytes) && bytes is { Length: > 0 })
        {
            int off = member.Offset;
            int memSz = member.Size;
            if (off < 0 || memSz <= 0 || off + memSz > bytes.Length)
            {
                off = 0;
                memSz = bytes.Length;
            }
            byte[] slice;
            if (off == 0 && memSz == bytes.Length)
                slice = bytes;
            else
            {
                slice = new byte[memSz];
                Buffer.BlockCopy(bytes, off, slice, 0, memSz);
            }
            size = (uint)slice.Length;
            Bo2PackResidentOpens++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1"
                || Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            {
                string leaf = parent;
                int bs = parent.LastIndexOf('\\');
                if (bs >= 0) leaf = parent[(bs + 1)..];
                Console.Error.WriteLine(
                    $"[BO2] pack-member open key=\"{key}\" parent={leaf} " +
                    $"off=0x{off:X} size={size} n={Bo2PackResidentOpens}");
            }
            return iopModules.FileOpenMemoryStub("bo2pack:" + key, slice);
        }

        int fd = iopModules.FileOpen(parent, 1);
        if (fd < 0)
            fd = TryOpenBo2RealBg2(iopModules, cdvd, parent, countSectors: false);
        if (fd >= 0)
        {
            Bo2PackResidentOpens++;
            if (iopModules.TryGetOpenFileSize(fd, out uint fsz))
                size = fsz;
        }
        return fd;
    }

    private static bool LooksLikeBo2PackResidentPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string p = path.Replace('/', '\\');
        if (p.Contains("PRECODE.BG2", StringComparison.OrdinalIgnoreCase)
            || p.Contains("CODE.BG2", StringComparison.OrdinalIgnoreCase)
            || p.Contains("MAINMENU.BG2", StringComparison.OrdinalIgnoreCase)
            || p.Contains("PS2.RKV", StringComparison.OrdinalIgnoreCase)
            || p.Contains("GAME.ERG", StringComparison.OrdinalIgnoreCase)
            || p.Contains("ENGLISH.DIR", StringComparison.OrdinalIgnoreCase))
            return false;
        return p.Contains("ASSETS", StringComparison.OrdinalIgnoreCase)
            || p.Contains("ETYPES", StringComparison.OrdinalIgnoreCase)
            || p.Contains(".IMP", StringComparison.OrdinalIgnoreCase)
            || p.Contains(".ETP", StringComparison.OrdinalIgnoreCase)
            || p.Contains(".REA", StringComparison.OrdinalIgnoreCase)
            || p.Contains(".FNT", StringComparison.OrdinalIgnoreCase)
            || p.Contains("fonts/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("fonts\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeBo2PackMemberKey(string path)
    {
        string key = path.Replace('\\', '/').Trim().ToLowerInvariant();
        int semi = key.IndexOf(';');
        if (semi > 0) key = key[..semi];
        int colon = key.IndexOf(':');
        if (colon >= 0) key = key[(colon + 1)..].TrimStart('/');
        if (key.StartsWith("gogames/bo2/")) key = key["gogames/bo2/".Length..];
        if (key.StartsWith("gogames/")) key = key["gogames/".Length..];
        return key.TrimStart('/');
    }

    /// <summary>
    /// Scan PRECODE.BG2 / CODE.BG2 / MAINMENU.BG2: parse goefile structure, index path
    /// strings to member ranges (prefer nested goefile slices over whole-parent).
    /// Packs are small (≤2 MiB) so full host read is fine.
    /// </summary>
    private void EnsureBo2PackIndex(IopModuleHost iopModules, Cdvd cdvd)
    {
        if (_bo2PackIndexBuilt) return;
        _bo2PackIndexBuilt = true;
        _ = cdvd;

        string[] packs =
        {
            @"cdrom0:\GOGAMES\BO2\PRECODE.BG2",
            @"cdrom0:\GOGAMES\BO2\CODE.BG2",
            @"cdrom0:\GOGAMES\BO2\RESOURCES\LEVELS\UI\MAINMENU.BG2",
            @"cdrom0:\GOGAMES\BO2\RESOUR~1\LEVELS\UI\MAINMENU.BG2",
        };
        var seenPack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string packPath in packs)
        {
            int fd = iopModules.FileOpen(packPath, 1);
            if (fd < 0) continue;
            if (!iopModules.TryGetOpenFileSize(fd, out uint fsz) || fsz is 0 or > 16u * 1024 * 1024)
            {
                iopModules.FileClose(fd);
                continue;
            }
            if (!iopModules.TryReadOpenFileBytes(fd, 0, (int)fsz, out byte[]? data)
                || data == null || data.Length < 32)
            {
                iopModules.FileClose(fd);
                continue;
            }
            iopModules.FileClose(fd);

            if (data.Length < 16
                || data[0] != (byte)'g' || data[1] != (byte)'o' || data[2] != (byte)'e'
                || data[3] != (byte)'f' || data[4] != (byte)'i' || data[5] != (byte)'l'
                || data[6] != (byte)'e')
                continue;

            string packKey = packPath;
            if (packPath.Contains("MAINMENU", StringComparison.OrdinalIgnoreCase))
                packKey = @"cdrom0:\GOGAMES\BO2\RESOURCES\LEVELS\UI\MAINMENU.BG2";
            if (!seenPack.Add(packKey))
                continue;
            _bo2PackBytes[packKey] = data;

            int added = IndexBo2GoeFileMembers(data, packKey);
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[BO2] pack index {packKey} size={data.Length} members+={added} " +
                    $"total={_bo2PackMembers.Count}");
        }
    }

    /// <summary>
    /// Index path-like strings in a goefile to member (offset, size) ranges.
    /// Nested <c>goefile</c> regions claim their symlist paths as tight slices; root
    /// symlist paths map to the whole pack (offset 0). Prefer smaller ranges on conflict.
    /// </summary>
    private int IndexBo2GoeFileMembers(byte[] data, string packKey)
    {
        int added = 0;
        // 1) Nested goefile packages first (tightest members).
        int search = 1; // skip root magic at 0
        while (search + 16 <= data.Length)
        {
            int nest = IndexOfBo2GoeMagic(data, search);
            if (nest < 0) break;
            int nestSize = ReadBo2GoeSizeField(data, nest);
            if (nestSize < 32 || nest + nestSize > data.Length)
            {
                search = nest + 7;
                continue;
            }
            added += RegisterBo2PathsInRange(data, nest, nest + nestSize, packKey, nest, nestSize,
                preferTighter: true);
            search = nest + Math.Max(7, nestSize);
        }

        // 2) Root-level paths (whole parent) for symbols not claimed by a nested package.
        added += RegisterBo2PathsInRange(data, 0, data.Length, packKey, 0, data.Length,
            preferTighter: false);
        return added;
    }

    private int RegisterBo2PathsInRange(byte[] data, int rangeStart, int rangeEnd,
        string packKey, int memberOff, int memberSize, bool preferTighter)
    {
        int added = 0;
        int i = Math.Max(0, rangeStart);
        int end = Math.Min(data.Length, rangeEnd);
        while (i < end)
        {
            byte b = data[i];
            if (b is < (byte)'A' or > (byte)'z')
            {
                i++;
                continue;
            }
            int start = i;
            while (i < end)
            {
                byte c = data[i];
                if (c is >= 32 and <= 126 && c != (byte)'"' && c != (byte)'\'')
                    i++;
                else
                    break;
            }
            int len = i - start;
            if (len is >= 8 and <= 180 && i < end && data[i] == 0)
            {
                string s = System.Text.Encoding.ASCII.GetString(data, start, len);
                if (IsBo2GoeMemberPath(s))
                {
                    string key = NormalizeBo2PackMemberKey(s);
                    if (key.Length > 0)
                    {
                        var cand = new Bo2PackMember(packKey, memberOff, memberSize);
                        if (!_bo2PackMembers.TryGetValue(key, out Bo2PackMember existing))
                        {
                            _bo2PackMembers[key] = cand;
                            added++;
                        }
                        else if (preferTighter && cand.Size > 0 && cand.Size < existing.Size)
                        {
                            _bo2PackMembers[key] = cand;
                        }
                        else if (!preferTighter && existing.Size <= 0)
                        {
                            _bo2PackMembers[key] = cand;
                        }
                    }
                }
            }
            if (i < end && data[i] == 0) i++;
        }
        return added;
    }

    private static int IndexOfBo2GoeMagic(byte[] data, int start)
    {
        for (int i = start; i + 7 < data.Length; i++)
        {
            if (data[i] == (byte)'g' && data[i + 1] == (byte)'o' && data[i + 2] == (byte)'e'
                && data[i + 3] == (byte)'f' && data[i + 4] == (byte)'i' && data[i + 5] == (byte)'l'
                && data[i + 6] == (byte)'e' && data[i + 7] == 0)
                return i;
        }
        return -1;
    }

    /// <summary>Read goefile size field (bytes from tag start, includes 16-byte header).</summary>
    private static int ReadBo2GoeSizeField(byte[] data, int off)
    {
        if (off + 12 > data.Length) return 0;
        return data[off + 8] | (data[off + 9] << 8) | (data[off + 10] << 16) | (data[off + 11] << 24);
    }

    private static bool IsBo2GoeMemberPath(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 8) return false;
        bool hasSep = s.Contains('/') || s.Contains('\\');
        string lower = s.ToLowerInvariant();
        if (!(hasSep || lower.StartsWith("assets") || lower.StartsWith("fonts")
              || lower.StartsWith("resources")))
            return false;
        return lower.Contains(".imp") || lower.Contains(".etp") || lower.Contains(".rea")
            || lower.Contains(".fnt") || lower.Contains(".chn") || lower.Contains(".txt")
            || lower.Contains(".bg2") || lower.Contains("assets/") || lower.Contains("fonts/");
    }

    /// <summary>
    /// Soft-stub allowlist for Blood Omen 2 probes that are safe as empty files.
    /// Explicitly excludes parseable payloads (.BG2 / MAINMENU / PRECODE / CODE / .IMP / .ETP)
    /// — empty stubs stall goefile/entity parsers and block title menu.
    /// </summary>
    private static bool LooksLikeBo2SoftProbeStub(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string p = path.Replace('/', '\\');
        // Never empty-stub real goefile / entity / menu payloads.
        if (p.Contains(".BG2", StringComparison.OrdinalIgnoreCase)
            || p.Contains("MAINMENU", StringComparison.OrdinalIgnoreCase)
            || p.Contains("PRECODE", StringComparison.OrdinalIgnoreCase)
            || p.Contains("CODE.BG2", StringComparison.OrdinalIgnoreCase)
            || p.Contains(".IMP", StringComparison.OrdinalIgnoreCase)
            || p.Contains(".ETP", StringComparison.OrdinalIgnoreCase)
            || p.Contains(".PSS", StringComparison.OrdinalIgnoreCase)
            || p.Contains("PS2.RKV", StringComparison.OrdinalIgnoreCase)
            || p.Contains("GAME.ERG", StringComparison.OrdinalIgnoreCase)
            || p.Contains("ENGLISH.DIR", StringComparison.OrdinalIgnoreCase)
            || p.Contains("LIST.TXT", StringComparison.OrdinalIgnoreCase))
            return false;
        // Safe soft probes: reaction scripts, tiny UI text, language sidecars, audio names.
        if (p.Contains(".REA", StringComparison.OrdinalIgnoreCase)
            || p.Contains(".GOF", StringComparison.OrdinalIgnoreCase)
            || p.Contains(".TCL", StringComparison.OrdinalIgnoreCase)
            || p.Contains(".ANM", StringComparison.OrdinalIgnoreCase)
            || p.Contains(".SND", StringComparison.OrdinalIgnoreCase)
            || p.Contains(".VAG", StringComparison.OrdinalIgnoreCase)
            || p.Contains(".TXT", StringComparison.OrdinalIgnoreCase)
            || p.Contains("FRONTEND", StringComparison.OrdinalIgnoreCase)
            || p.Contains(".ICN", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>
    /// Open real PRECODE.BG2 / CODE.BG2 / MAINMENU.BG2 (and other level .BG2) from the ISO,
    /// including ISO 9660 Level-1 short-name aliases (RESOURCES→RESOUR~1, etc.).
    /// </summary>
    /// <param name="countSectors">
    /// When false (host warm probe only), skip <see cref="Cdvd.NoteHostReadSectors"/> so
    /// telemetry and title assists reflect game-initiated I/O only.
    /// </param>
    private int TryOpenBo2RealBg2(IopModuleHost iopModules, Cdvd cdvd, string path,
        bool countSectors = true)
    {
        if (string.IsNullOrEmpty(path)) return -1;
        string p = path.Replace('/', '\\');
        bool wantsBg2 = p.Contains(".BG2", StringComparison.OrdinalIgnoreCase)
            || p.Contains("MAINMENU", StringComparison.OrdinalIgnoreCase)
            || p.Contains("PRECODE", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("\\CODE", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(":CODE", StringComparison.OrdinalIgnoreCase)
            || p.Equals("CODE", StringComparison.OrdinalIgnoreCase)
            || p.Equals("PRECODE", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("\\PRECODE", StringComparison.OrdinalIgnoreCase);
        if (!wantsBg2) return -1;

        // Candidate disc paths (long + short-name + basename).
        var candidates = new List<string>(8);
        void Add(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (!candidates.Exists(x => x.Equals(s, StringComparison.OrdinalIgnoreCase)))
                candidates.Add(s);
        }

        string norm = NormalizeGoeDiscPath(path);
        Add(norm);
        Add(path);
        // Bare PreCode / Code tokens from ELF "usebigfile" boot path.
        if (p.Contains("PRECODE", StringComparison.OrdinalIgnoreCase))
        {
            Add(@"cdrom0:\GOGAMES\BO2\PRECODE.BG2");
            Add(@"cdrom0:\GOGAMES\BO2\PRECODE.BG2;1");
        }
        if (p.Contains("CODE", StringComparison.OrdinalIgnoreCase)
            && !p.Contains("PRECODE", StringComparison.OrdinalIgnoreCase))
        {
            Add(@"cdrom0:\GOGAMES\BO2\CODE.BG2");
            Add(@"cdrom0:\GOGAMES\BO2\CODE.BG2;1");
        }
        if (p.Contains("MAINMENU", StringComparison.OrdinalIgnoreCase))
        {
            Add(@"cdrom0:\GOGAMES\BO2\RESOURCES\LEVELS\UI\MAINMENU.BG2");
            Add(@"cdrom0:\GOGAMES\BO2\RESOUR~1\LEVELS\UI\MAINMENU.BG2");
            Add(@"cdrom0:\GOGAMES\BO2\MAINMENU.BG2");
        }
        // Generic .BG2: try RESOURCES↔RESOUR~1 rewrite.
        string shorted = ToIsoShortNamePath(norm);
        if (!string.IsNullOrEmpty(shorted)) Add(shorted);

        foreach (string c in candidates)
        {
            int fd = iopModules.FileOpen(c, 1);
            if (fd < 0) continue;
            // Only credit sectors for game-initiated opens (default). Host warm probes must
            // not inflate cdvdSectors or title assists fire before GOE sid=0x29 / asset Open.
            if (countSectors
                && iopModules.TryGetOpenFileSize(fd, out uint fsz) && fsz > 0)
            {
                int sectors = fsz <= 16u * 1024 * 1024
                    ? (int)((fsz + 2047) / 2048)
                    : 1;
                cdvd.NoteHostReadSectors(sectors);
            }
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[BO2] real BG2 open path=\"{c}\" fd={fd} countSectors={countSectors}");
            return fd;
        }
        return -1;
    }

    /// <summary>Rewrite long ISO path segments to Level-1 8.3 aliases seen on BO2 retail.</summary>
    private static string ToIsoShortNamePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        // Known BO2 long→short directory aliases (primary volume only; no Joliet on this disc).
        string[][] map =
        {
            new[] { "RESOURCES", "RESOUR~1" },
            new[] { "ANIMATIONS", "ANIMAT~1" },
            new[] { "SMUGGLERS", "SMUGGL~1" },
            new[] { "BOSSROOMS", "BOSSRO~1" },
            new[] { "LOWERCITY", "LOWERC~1" },
            new[] { "UPPERCITY", "UPPERC~1" },
            new[] { "INDUSTRIAL", "INDUST~1" },
            new[] { "ETERNALPRISON", "ETERNA~1" },
            new[] { "HYLDENCITY", "HYLDEN~1" },
            new[] { "SANCTUARY", "SANCTU~1" },
            new[] { "LOAD_SCREENS", "LOAD_S~1" },
            new[] { "LOADSCREENS", "LOAD_S~1" },
            new[] { "CINEMATICS", "CINEMA~1" },
            new[] { "CHARMVAMPIRE", "CHARMV~1" },
            new[] { "MADVAMPIRE", "MADVAM~1" },
            new[] { "SPEEDVAMPIRE", "SPEEDV~1" },
            new[] { "JUMPVAMPIRE", "JUMPVA~1" },
        };
        string s = path;
        foreach (var pair in map)
            s = ReplacePathSegment(s, pair[0], pair[1]);
        return s;
    }

    private static string ReplacePathSegment(string path, string from, string to)
    {
        // Case-insensitive segment replace for \ and / separators.
        var parts = path.Replace('/', '\\').Split('\\');
        bool any = false;
        for (int i = 0; i < parts.Length; i++)
        {
            string seg = parts[i];
            int semi = seg.IndexOf(';');
            string ver = "";
            if (semi >= 0) { ver = seg[semi..]; seg = seg[..semi]; }
            if (seg.Equals(from, StringComparison.OrdinalIgnoreCase))
            {
                parts[i] = to + ver;
                any = true;
            }
        }
        if (!any) return path;
        // Preserve cdrom0: prefix style.
        string joined = string.Join("\\", parts);
        return joined;
    }

    /// <summary>
    /// Publish opened file size next to the open result for SN ProDG FILEIO clients.
    /// </summary>
    private static void WriteSnFioOpenSize(SystemMemory mem, uint argBuf, uint sendSize,
        uint recvBuf, uint recvSize, uint size)
    {
        if (recvBuf != 0 && recvSize >= 8)
            mem.Write32(recvBuf + 4, size);
        // SN wrapper: +4 eeReply* — write { result already in recv, size } if pointer-shaped.
        if (argBuf != 0 && sendSize >= 8 && LooksLikeSnFioWrapper(mem, argBuf, sendSize))
        {
            uint reply = mem.Read32(argBuf + 4);
            if (IsEeRamPointer(reply))
            {
                uint phys = reply & 0x1FFFFFFFu;
                // Common SN reply: +0 result (written by generic path into recv), +4 size.
                mem.Write32(phys + 4, size);
            }
        }
    }

    private static string ScanSendBufferForPath(SystemMemory mem, uint argBuf, uint sendSize)
    {
        uint max = Math.Min(sendSize, 0x800u);
        for (uint off = 0; off + 4 < max; off++)
        {
            byte b0 = mem.Read8(argBuf + off);
            // BO2 goefile / bigfile Open often uses relative "CODE" / "PRECODE" / "MAINMENU" /
            // "resources\\levels\\ui\\mainmenu.bg2" / "assets/…" without a device prefix.
            if (b0 is not ((byte)'c' or (byte)'C' or (byte)'r' or (byte)'R' or (byte)'h' or (byte)'H'
                or (byte)'g' or (byte)'G' or (byte)'p' or (byte)'P' or (byte)'m' or (byte)'M'
                or (byte)'a' or (byte)'A' or (byte)'f' or (byte)'F' or (byte)'l' or (byte)'L'))
                continue;
            string s = ReadCString(mem, argBuf + off, 256);
            if (string.IsNullOrEmpty(s) || s.Length < 3) continue;
            if (LooksLikeFsPath(s) || s.Contains("GOGAMES", StringComparison.OrdinalIgnoreCase)
                || s.Contains("PS2.RKV", StringComparison.OrdinalIgnoreCase)
                || s.Contains(".rkv", StringComparison.OrdinalIgnoreCase)
                || s.Contains(".ERG", StringComparison.OrdinalIgnoreCase)
                || s.Contains(".BG2", StringComparison.OrdinalIgnoreCase)
                || s.Contains("MAINMENU", StringComparison.OrdinalIgnoreCase)
                || s.Contains("PRECODE", StringComparison.OrdinalIgnoreCase)
                || s.Equals("CODE", StringComparison.OrdinalIgnoreCase)
                || s.Contains("assets/", StringComparison.OrdinalIgnoreCase)
                || s.Contains("assets\\", StringComparison.OrdinalIgnoreCase)
                || s.Contains("resources/", StringComparison.OrdinalIgnoreCase)
                || s.Contains("resources\\", StringComparison.OrdinalIgnoreCase))
                return s;
        }
        return "";
    }

    /// <summary>
    /// 989snd / 989nomid.irx RPC reply (sids <see cref="Sid989Snd"/> / <see cref="Sid989Snd2"/>).
    /// </summary>
    /// <remarks>
    /// Ground-truthed against God of War SCUS_973.99 EE client + disc <c>989NOMID.IRX</c>:
    /// <list type="bullet">
    /// <item>EE <c>snd_SendIOPCommand*</c> (SCUS @ 0x0026BF28) issues
    ///   <c>sceSifCallRpc(cd, fno, NOWAIT, send, ssize, recv=0x305600, rsize=12, end=NULL)</c>
    ///   after <c>mark_pending(recv, index=1)</c> which zeroes <c>recv[0]</c> and
    ///   <c>recv[1+index]</c> (= <c>recv[2]</c> at +8) and stores the pending ptr.</item>
    /// <item>Wait path (SCUS @ 0x0026BB98): once <c>sceSifCheckStatRpc</c> reports idle,
    ///   requires <c>pending[0] == 0xFFFFFFFF &amp;&amp; pending[1+index] == 0xFFFFFFFF</c>
    ///   or it DECI2-spams
    ///   <c>"989snd.c: Sif says RPC isn't busy, but we still don't have returns from the IOP!"</c>.</item>
    /// <item>IOP RPC entry (989nomid va 0x1878): returns a fixed retbuf base; init stores
    ///   <c>retbuf[0] = -1</c>; after every command stores <c>retbuf[2] = -1</c>
    ///   (<c>*(cursor+4)</c> with <c>cursor = base+4</c>). Result word lives at +4.
    ///   Real <c>sceSifExecRequest</c> DMAs that 12B retbuf → EE recv.</item>
    /// </list>
    /// So the HLE reply shape is: <c>{ 0xFFFFFFFF, result, 0xFFFFFFFF }</c> (and paint any
    /// extra recv words with the same done-magic so larger rsize still clears).
    /// </remarks>
    private static int Handle989Snd(SystemMemory mem, uint fno, uint argBuf, uint recvBuf, uint recvSize)
    {
        _ = fno;
        // Command result at +4. 0 = success for init / most bank-load status queries;
        // EE reads *(recv+4) after the wait loop as the caller's return value.
        const uint ResultOk = 0;
        const uint Done = 0xFFFFFFFFu;

        if (recvBuf != 0)
        {
            // Always write the canonical 12-byte retbuf; then extend done-magic if rsize > 12.
            mem.Write32(recvBuf + 0, Done);
            mem.Write32(recvBuf + 4, ResultOk);
            mem.Write32(recvBuf + 8, Done);
            uint paint = recvSize > 12 ? recvSize : 12;
            // Cap pathological sizes (same spirit as RDATA).
            if (paint > 0x1000) paint = 0x1000;
            for (uint off = 12; off + 4 <= paint; off += 4)
                mem.Write32(recvBuf + off, Done);
        }

        // IOP server buf (argBuf) is the *command* DMA target, not the retbuf, but some
        // titles also peek the shared slot after CallRpc. Mirror the same done shape.
        if (argBuf != 0)
        {
            mem.Write32(argBuf + 0, Done);
            mem.Write32(argBuf + 4, ResultOk);
            mem.Write32(argBuf + 8, Done);
        }

        // Return the +0 sentinel so any accidental single-word write still looks "done".
        return unchecked((int)Done);
    }

    /// <summary>sceCdReadClock: fill SCECdCLOCK (8 bytes) with a stable synthetic RTC, at the
    /// real reply offset (recvBuf+4 — see HandleCdScmd's doc comment for why +0 is reserved
    /// for the result word).</summary>
    private static int WriteCdClock(SystemMemory mem, uint recvBuf)
    {
        if (recvBuf == 0) return 1;
        // BCD-ish layout used by libcdvd: second, minute, hour, day, month, year…
        mem.Write8(recvBuf + 0, 0x00); // second
        mem.Write8(recvBuf + 1, 0x00); // minute
        mem.Write8(recvBuf + 2, 0x12); // hour
        mem.Write8(recvBuf + 3, 0x01); // day
        mem.Write8(recvBuf + 4, 0x01); // month
        mem.Write8(recvBuf + 5, 0x24); // year (2000+0x24)
        mem.Write8(recvBuf + 6, 0);
        mem.Write8(recvBuf + 7, 0);
        return 1;
    }

    /// <summary>
    /// NCMD GetToc (fno=4), ground-truthed against the real decompiled handler
    /// (FUN_0000340c, tools/bios-decomp/CDVDFSV_ALL.txt): the real reply is only **2 words** —
    /// result and an is-DVD flag (real code: `*param_3 = uVar1; param_3[1] = uVar4;`, where
    /// uVar4 comes from checking the real mechacon disc-type register). The actual 0x810-byte
    /// TOC payload is DMA'd to a *separate* IOP-side buffer on real hardware, not packed into
    /// this RPC reply — callers that read TOC fields out of the RPC reply buffer itself were
    /// reading track-count/lead-out/disc-type/layer-break words this file previously wrote at
    /// +0../+12 with no result word at all, a structure the real BIOS never produces. Kept those
    /// same fields, shifted to start after the real result+isDvd pair, as a best-effort
    /// TOC-shaped payload for callers that do read further into the buffer regardless.
    /// </summary>
    private static int WriteCdToc(SystemMemory mem, uint recvBuf, Cdvd cdvd)
    {
        if (recvBuf == 0) return 1;
        // SCECdPS2DVD=0x14, SCECdDVDV=0xFE (decomp DAT_bf40200f checks).
        uint isDvd = cdvd.DiscType is 0x14 or 0xFE ? 1u : 0u;
        mem.Write32(recvBuf + 4, isDvd);
        mem.Write32(recvBuf + 8, cdvd.TocTracks);
        mem.Write32(recvBuf + 12, cdvd.TocLeadOutSector);
        mem.Write32(recvBuf + 16, cdvd.DiscType);
        mem.Write32(recvBuf + 20, cdvd.LayerBreakLba);
        return 1;
    }

    private static int BreakCdvd(Cdvd cdvd)
    {
        cdvd.CancelAsync();
        return 1;
    }

    /// <summary>CDVDFSV sceCdInit (sid=0x80000592 / FUN_00000204).</summary>
    /// <remarks>
    /// EE libcdvd recv is a <c>CdInitPkt</c>: result (+0), cdvdfsv_version (+4),
    /// cdvdman_version (+8), isverbose (+12). Versions feed later dual-layer / MV paths
    /// (<c>initVersionCdvdman &gt;= 0x200</c>). Older HLE only wrote word 0 → versions 0.
    /// </remarks>
    private static int HandleCdInit(Cdvd cdvd, uint argBuf, SystemMemory mem)
    {
        // mode = *param_2; SCECdINIT=0, SCECdINoD=1, SCECdEXIT=5. All accepted.
        _ = argBuf != 0 ? mem.Read32(argBuf) : 0u;
        cdvd.Standby(); // bring drive to ready/spin
        // Version fields written via WriteCdInitPacket (0x200 = XCDVDMAN-era dual-layer).
        return 1;
    }

    /// <summary>Write full CdInitPkt into recv (result already written by generic path).</summary>
    private static void WriteCdInitPacket(SystemMemory mem, uint recvBuf, uint recvSize)
    {
        if (recvBuf == 0 || recvSize < 4) return;
        // +0 result filled by caller; fill version fields for SDK ≥ 2.0 layout.
        if (recvSize >= 8) mem.Write32(recvBuf + 4, 0x200);  // m_cdvdfsv_version
        if (recvSize >= 12) mem.Write32(recvBuf + 8, 0x200); // m_cdvdman_version
        if (recvSize >= 16) mem.Write32(recvBuf + 12, 0);    // m_cdvdfsv_isverbose
    }

    /// <summary>
    /// CDVDFSV SearchFile (sid=0x80000597 / FUN_000002f0). Arg buffer is an in/out
    /// <c>sceCdlFILE</c>-shaped region with the path string at offset +0x20 (decomp:
    /// <c>param_2 + 0x20</c>). On success writes lsn/size into the struct head and returns 1.
    /// </summary>
    private static int HandleCdSearchFile(SystemMemory mem, Cdvd cdvd, uint argBuf, uint recvBuf)
    {
        if (argBuf == 0) return 0;
        // Path lives at +0x20 in the sceCdlFILE / search packet (decomp FUN_000002f0).
        string name = ReadCString(mem, argBuf + 0x20, 256);
        if (string.IsNullOrEmpty(name))
            name = ReadCString(mem, argBuf, 256);
        name = name.Trim();
        if (name.Length == 0) return 0;

        // Strip device / version suffix: "\\SYSTEM.CNF;1" / "cdrom0:\\FOO.ELF;1"
        int colon = name.IndexOf(':');
        if (colon >= 0) name = name[(colon + 1)..];
        name = name.TrimStart('\\', '/');
        int semi = name.IndexOf(';');
        if (semi >= 0) name = name[..semi];

        string? path = cdvd.MountedPath;
        if (string.IsNullOrEmpty(path))
        {
            // Unmounted: synthetic "not found" rather than inventing disc contents.
            if (recvBuf != 0) mem.Write32(recvBuf, 0);
            return 0;
        }

        try
        {
            var vol = Iso9660.OpenFile(path);
            if (vol == null) return 0;
            var entry = Iso9660.FindFile(vol, name);
            // Also try bare leaf
            if (entry == null)
                entry = Iso9660.FindFile(vol, System.IO.Path.GetFileName(name));
            if (entry == null)
            {
                // Dispose volume's disc if OpenFile created a new FileDiscImage — OpenFile
                // returns vol with Disc owned; avoid leaking by disposing when we own it.
                // Cdvd already owns the mounted image; OpenFile opens a second handle — dispose.
                try { vol.Disc?.Dispose(); } catch { /* ignore */ }
                return 0;
            }

            // sceCdlFILE: +0 lsn, +4 size, +8 name[16], +0x18 date[8]
            mem.Write32(argBuf + 0, entry.ExtentLba);
            mem.Write32(argBuf + 4, entry.Size);
            // name[16] at +8
            string leaf = entry.Name.Length > 15 ? entry.Name[..15] : entry.Name;
            for (int i = 0; i < 16; i++)
                mem.Write8(argBuf + 8 + (uint)i, i < leaf.Length ? (byte)leaf[i] : (byte)0);

            try { vol.Disc?.Dispose(); } catch { /* ignore */ }
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// NCMD dispatcher body (FUN_00003f3c). Writes result to recvBuf+0 and any extra payload
    /// words for multi-word replies. Returns the result word.
    /// </summary>
    private void HandleCdNcmd(SystemMemory mem, Cdvd cdvd, uint fno, uint argBuf, uint recvBuf)
    {
        int result = HandleCdNcmdResult(mem, cdvd, fno, argBuf, recvBuf);
        if (recvBuf != 0)
            mem.Write32(recvBuf, unchecked((uint)result));
    }

    private int HandleCdNcmdResult(SystemMemory mem, Cdvd cdvd, uint fno, uint argBuf, uint recvBuf)
    {
        switch (fno)
        {
            case NcmdRead:
            case NcmdCddaRead:
            case NcmdDvdRead:
            {
                // sceCdRead-family: lbn, sectors, buf, mode… (FUN_000004d8 etc.)
                // Real handlers return the accumulated **byte count** actually transferred.
                uint lbn = argBuf != 0 ? mem.Read32(argBuf) : 0;
                uint sectors = argBuf != 0 ? Math.Max(1u, mem.Read32(argBuf + 4)) : 1u;
                uint bufAddr = argBuf != 0 ? mem.Read32(argBuf + 8) : 0;
                uint ok = cdvd.ReadSectorsTo(mem, lbn, sectors, bufAddr);
                return (int)(ok * (uint)Cdvd.SectorSize);
            }
            case NcmdGetToc:
                return WriteCdToc(mem, recvBuf, cdvd);
            case NcmdSeek:
            {
                uint lsn = argBuf != 0 ? mem.Read32(argBuf) : 0;
                return cdvd.SeekTo(lsn);
            }
            case NcmdStandby:
                return cdvd.Standby();
            case NcmdStop:
                return cdvd.Stop();
            case NcmdPause:
                return cdvd.Pause();
            case NcmdStream:
                return StartCdStream(cdvd, argBuf, mem);
            case NcmdCddaStream:
                // FUN_0000273c — same subcmd layout; bank-stat (cmd==6) returns byte estimate.
                return StartCdStream(cdvd, argBuf, mem);
            case NcmdReadKey:
            {
                // FUN_00003c90: *param_3 = result; param_3[1..4] = 16-byte key payload.
                // Synthetic zeros — DetPS2 does not fabricate MagicGate secrets.
                if (recvBuf != 0)
                {
                    mem.Write32(recvBuf + 4, 0);
                    mem.Write32(recvBuf + 8, 0);
                    mem.Write32(recvBuf + 12, 0);
                    mem.Write32(recvBuf + 16, 0);
                }
                return 1;
            }
            case NcmdApplyNCmd:
                // FUN_00003e0c: passthrough apply + sceCdSync(2). Accept and complete.
                return 1;
            case NcmdReadIopMem:
            {
                // FUN_00000380 "CdRead call from EE data load IOP Memory":
                // param: lbn, sectors, iop_addr, mode… — perform real sector fill.
                uint lbn = argBuf != 0 ? mem.Read32(argBuf) : 0;
                uint sectors = argBuf != 0 ? Math.Max(1u, mem.Read32(argBuf + 4)) : 1u;
                uint bufAddr = argBuf != 0 ? mem.Read32(argBuf + 8) : 0;
                uint ok = cdvd.ReadSectorsTo(mem, lbn, sectors, bufAddr);
                // Handler itself doesn't write *param_3 to a byte count in the decompile (void
                // return path), but EE wrappers treat success as non-zero RPC completion; return
                // byte count so callers that inspect recvBuf get the same contract as NCMD read.
                return (int)(ok * (uint)Cdvd.SectorSize);
            }
            case NcmdDiskReady:
                // FUN_00003ee0: 2 = SCECdComplete, 6 = SCECdNotReady
                return cdvd.DiskReady();
            case NcmdReadChain:
                // XCDVDFSV-only; this ROM's NCMD switch has no case 0xf. Accept as complete.
                return 1;
            default:
                return 1;
        }
    }

    // Real BIOS CD_SCMD function numbers, ground-truthed against the actual decompiled
    // CDVDFSV.IRX SCMD dispatcher (Ghidra FUN_000041b8, tools/bios-decomp/CDVDFSV_ALL.txt) —
    // not guessed from ps2sdk headers alone. Case 7 in particular was previously mislabeled
    // "ApplySCmd" in this file; the real dispatcher has no such case at 7, it's WRITE_ILinkID.
    private const uint ScmdReadIlinkId = 0x06;
    private const uint ScmdWriteIlinkId = 0x07;
    private const uint ScmdReadNvm = 0x08;
    private const uint ScmdWriteNvm = 0x09;
    private const uint ScmdDecSet1 = 0x0A;
    private const uint ScmdDecSet2 = 0x0B;
    private const uint ScmdSetHdMode = 0x0D;
    private const uint ScmdOpenConfig = 0x0E;
    private const uint ScmdCloseConfig = 0x0F;
    private const uint ScmdReadConfig = 0x10;
    private const uint ScmdWriteConfig = 0x11;
    private const uint ScmdReadConsoleId = 0x12;
    private const uint ScmdWriteConsoleId = 0x13;
    private const uint ScmdGetMecaconVersion = 0x14;
    private const uint ScmdCtrlAudioDigitalOut = 0x15;
    private const uint ScmdReadSubQ = 0x17;
    private const uint ScmdForbidDvdP = 0x18;
    private const uint ScmdAutoAdjustCtrl = 0x19;
    /// <summary>
    /// sceCdReadDvdDualInfo — EE libcdvd dual-layer query (on_dual + layer1_start).
    /// Older CDVDFSV tables stop at 0x19; X stacks add dual-info. Live GoW (SCUS_973.99)
    /// after PollSema-id fix: SCMD fno=<c>0x27</c> (not 0x1A). Also accept 0x1A as alias.
    /// Shape: result + on_dual(u32) + layer1_start(u32).
    /// </summary>
    private const uint ScmdReadDvdDualInfo = 0x1A;
    /// <summary>Live-traced God of War dual-info / extended SCMD after CdInit + Mmode.</summary>
    private const uint ScmdReadDvdDualInfoX = 0x27;
    /// <summary>sceCdMmode — media mode (CD/DVD). ps2sdk CD_SCMD_MMODE = 0x22.</summary>
    private const uint ScmdMmode = 0x22;
    /// <summary>sceCdChangeThreadPriority — ps2sdk CD_SCMD_SETTHREADPRI = 0x23.</summary>
    private const uint ScmdSetThreadPri = 0x23;
    /// <summary>sceCdPowerOff — CD_SCMD_POWEROFF = 0x21.</summary>
    private const uint ScmdPowerOff = 0x21;
    /// <summary>sceCdCancelPOffRdy — CD_SCMD_CANCELPOWEROFF = 0x1F.</summary>
    private const uint ScmdCancelPowerOff = 0x1F;
    /// <summary>sceCdBlueLedCtrl — CD_SCMD_BLUELEDCTRL = 0x20.</summary>
    private const uint ScmdBlueLedCtrl = 0x20;
    private const uint ScmdWriteModelName = 0x1B;
    private const uint ScmdForbidRead = 0x1C;
    private const uint ScmdSpinCtrl = 0x1D;
    private const uint ScmdBootCertify = 0x1E;

    /// <summary>
    /// Real BIOS CD_SCMD dispatcher (sid=0x80000593), ported from the actual decompiled
    /// CDVDFSV.IRX (Ghidra FUN_000041b8 — every case below traced to its real handler function
    /// and, where it has one, its real debug-print format string; see
    /// tools/bios-decomp/CDVDFSV_ALL.txt). Real reply convention confirmed directly from the
    /// decompile: word[0] (recvBuf+0) is always the function's own return value; any further
    /// payload the specific command produces starts at recvBuf+4 (most cases write into
    /// <c>param_3+1</c>, i.e. one word past the result — this file's earlier version wrote
    /// payload bytes starting at recvBuf+0 with no result word at all, which is wrong for a
    /// caller that checks the result before touching the payload).
    ///
    /// Hardware state DetPS2 doesn't model for real (mechacon RTC/NVM/iLink ID/console ID) gets
    /// stable, structurally-correct synthetic values — the real fix here is getting the *shape*
    /// (word count, result-then-payload ordering) right, not fabricating real console secrets.
    /// </summary>
    private void HandleCdScmd(SystemMemory mem, Cdvd cdvd, uint fno, uint argBuf, uint recvBuf)
    {
        int result;
        switch (fno)
        {
            case ScmdReadClock: // case 1, FUN_00003888
                result = WriteCdClock(mem, recvBuf + 4);
                break;
            case ScmdWriteClock: // case 2, FUN_000038d0 — echoes the 2-word request back
                result = 1;
                if (recvBuf != 0 && argBuf != 0)
                {
                    mem.Write32(recvBuf + 4, mem.Read32(argBuf));
                    mem.Write32(recvBuf + 8, mem.Read32(argBuf + 4));
                }
                break;
            case ScmdGetDiskType: // case 3 — raw getter, no debug string in the real dispatch;
                result = (int)cdvd.DiscType;
                break;
            case ScmdGetError: // case 4, FUN_00003e60 → FUN_00004810 last error
                result = cdvd.LastError; // SCECdErNO=0, ErOPENS, ErREAD, …
                break;
            case ScmdTrayReq: // case 5, FUN_00003e88 — *param_1 = mode; param_3[1] = traychk
            {
                int mode = argBuf != 0 ? (int)mem.Read32(argBuf) : Cdvd.TrayReqCheck;
                result = (int)cdvd.TrayRequest(mode);
                if (recvBuf != 0) mem.Write32(recvBuf + 4, cdvd.TrayOpen ? 1u : 0u);
                break;
            }
            case ScmdReadIlinkId: // case 6, FUN_000035b0 "READ ILinkID call" — 2-word ID payload
                result = 1;
                if (recvBuf != 0) { mem.Write32(recvBuf + 4, 0); mem.Write32(recvBuf + 8, 0); }
                break;
            case ScmdWriteIlinkId: // case 7, FUN_000035fc "WRITE ILinkID call"
                result = 1;
                break;
            case ScmdReadNvm: // case 8, FUN_00003944 "READ NVM call" — echoes request words back
                result = 1;
                if (recvBuf != 0 && argBuf != 0)
                {
                    mem.Write32(recvBuf + 4, mem.Read32(argBuf));
                    mem.Write32(recvBuf + 8, mem.Read32(argBuf + 4));
                }
                break;
            case ScmdWriteNvm: // case 9, FUN_000039b0 "WRITE NVM call"
                result = 1;
                if (recvBuf != 0 && argBuf != 0)
                {
                    mem.Write32(recvBuf + 4, mem.Read32(argBuf));
                    mem.Write32(recvBuf + 8, mem.Read32(argBuf + 4));
                }
                break;
            case ScmdDecSet1: // case 0xa, FUN_00003d10 "DEC SET call" — result only
                result = 0;
                break;
            case ScmdDecSet2: // case 0xb, FUN_00003d70 — a different real function, same debug
                // string; 4-word output (local_20..local_14 in the decompile).
                result = 0;
                if (recvBuf != 0)
                {
                    mem.Write32(recvBuf + 4, 0);
                    mem.Write32(recvBuf + 8, 0);
                    mem.Write32(recvBuf + 12, 0);
                }
                break;
            case ScmdStatus: // case 0xc, FUN_00003574 → sceCdStatus drive state (SCECdStat*)
                result = cdvd.DriveState;
                break;
            case ScmdSetHdMode: // case 0xd, FUN_00003a1c "SET HD mode call" — result only
                result = 1;
                break;
            case ScmdOpenConfig: // case 0xe, FUN_00003a88 "OpenConfig call" — result + block info
                result = 1;
                if (recvBuf != 0) mem.Write32(recvBuf + 4, 0);
                break;
            case ScmdCloseConfig: // case 0xf, FUN_00003b20 "CloseConfig call"
                result = 1;
                if (recvBuf != 0) mem.Write32(recvBuf + 4, 0);
                break;
            case ScmdReadConfig: // case 0x10, FUN_00003b94 "ReadConfig call" — 2-word payload
                result = 1;
                if (recvBuf != 0) { mem.Write32(recvBuf + 4, 0); mem.Write32(recvBuf + 8, 0); }
                break;
            case ScmdWriteConfig: // case 0x11, FUN_00003c0c "WriteConfig call"
                result = 1;
                if (recvBuf != 0) mem.Write32(recvBuf + 4, 0);
                break;
            case ScmdReadConsoleId: // case 0x12, FUN_00003654 "READ Console call" — 2-word ID
                result = 1;
                if (recvBuf != 0) { mem.Write32(recvBuf + 4, 0); mem.Write32(recvBuf + 8, 0); }
                break;
            case ScmdWriteConsoleId: // case 0x13, FUN_000036a0 "WRITE ConsoleID call"
                result = 1;
                break;
            case ScmdGetMecaconVersion: // case 0x14, FUN_000036f8 — 2-word version payload
                result = 1;
                if (recvBuf != 0) { mem.Write32(recvBuf + 4, 0x00020101); mem.Write32(recvBuf + 8, 0); }
                break;
            case ScmdCtrlAudioDigitalOut: // case 0x15, FUN_000037d8
                result = 1;
                if (recvBuf != 0) mem.Write32(recvBuf + 4, 0);
                break;
            case ScmdBreak: // case 0x16, FUN_00000280 == real sceCdAbort (debug-string confirmed)
                result = BreakCdvd(cdvd);
                break;
            case ScmdReadSubQ: // case 0x17, FUN_00003744 — 2-word subchannel Q payload
                result = 1;
                if (recvBuf != 0) { mem.Write32(recvBuf + 4, 0); mem.Write32(recvBuf + 8, 0); }
                break;
            case ScmdForbidDvdP: // case 0x18, FUN_00003790 "ForbidDVDP call"
                result = 1;
                if (recvBuf != 0) mem.Write32(recvBuf + 4, 0);
                break;
            case ScmdAutoAdjustCtrl: // case 0x19, FUN_00003830 "Auto Ajust Ctrl call"
                result = 1;
                if (recvBuf != 0) mem.Write32(recvBuf + 4, 0);
                break;
            // ps2sdk scmd enum: 0x1A = READ_MODEL_NAME. Dual-layer titles (GoW) also issue
            // fno=0x27 for sceCdReadDvdDualInfo. Write dual-info shape for both 0x1A and 0x27
            // so either numbering works: result + on_dual + layer1_start.
            case ScmdReadDvdDualInfo: // 0x1A
            case ScmdReadDvdDualInfoX: // 0x27 — live God of War DualInfo
            {
                bool dual = cdvd.LayerBreakLba != 0;
                result = 1;
                if (recvBuf != 0)
                {
                    mem.Write32(recvBuf + 4, dual ? 1u : 0u);
                    mem.Write32(recvBuf + 8, dual ? cdvd.LayerBreakLba : 0u);
                }
                break;
            }
            case ScmdWriteModelName: // 0x1B
            case ScmdForbidRead: // 0x1C
            case ScmdSpinCtrl: // 0x1D
            case ScmdBootCertify: // 0x1E
            case ScmdCancelPowerOff: // 0x1F
            case ScmdBlueLedCtrl: // 0x20
            case ScmdPowerOff: // 0x21
                result = 1;
                if (recvBuf != 0) mem.Write32(recvBuf + 4, 0);
                break;
            case ScmdMmode: // 0x22 sceCdMmode — accept CD/DVD media mode
            case ScmdSetThreadPri: // 0x23
                result = 1;
                break;
            default:
                // Unknown extended SCMD: success with zero payload (prefer progress over fail).
                result = 1;
                if (recvBuf != 0) mem.Write32(recvBuf + 4, 0);
                break;
        }
        if (recvBuf != 0)
            mem.Write32(recvBuf, unchecked((uint)result));
    }

    /// <summary>
    /// NCMD STREAM / CDDASTREAM: arg layout from ps2sdk <c>sceCdStream</c> —
    /// [0]=lbn, [1]=nsectors, [2]=buf, [3]=cmd (CdvdStCmd_t). Decomp FUN_00001d5c switches on cmd.
    /// </summary>
    private static int StartCdStream(Cdvd cdvd, uint argBuf, SystemMemory mem)
    {
        uint lbn = argBuf != 0 ? mem.Read32(argBuf) : 0;
        uint nsectors = argBuf != 0 ? mem.Read32(argBuf + 4) : 0;
        uint buf = argBuf != 0 ? mem.Read32(argBuf + 8) : 0;
        int cmd = argBuf != 0 ? (int)mem.Read32(argBuf + 12) : Cdvd.StCmdStart;
        // Legacy single-arg callers that only wrote LBA and left cmd=0: treat as START.
        if (cmd == 0) cmd = Cdvd.StCmdStart;
        return cdvd.StreamCommand(lbn, nsectors, buf, cmd, mem);
    }

    // libpad always calls sceSifCallRpc(..., rpc_number=1, ...); the real command is
    // buffer.command (PAD_RPCCMD_*). See ps2sdk ee/rpc/pad/src/libpad.c +
    // tools/bios-decomp/PADMAN_ALL2.txt FUN_0000655c. docs/bios-ports/PADMAN.md.
    private const uint PadRpcCmdOpenNew = 0x01;
    private const uint PadRpcCmdSetMModeNew = 0x06;
    private const uint PadRpcCmdSetActDirNew = 0x07;
    private const uint PadRpcCmdSetActAlignNew = 0x08;
    private const uint PadRpcCmdGetBtnMaskNew = 0x09;
    private const uint PadRpcCmdSetBtnInfoNew = 0x0A;
    private const uint PadRpcCmdSetVrefNew = 0x0B;
    private const uint PadRpcCmdGetPortMaxNew = 0x0C;
    private const uint PadRpcCmdGetSlotMaxNew = 0x0D;
    private const uint PadRpcCmdCloseNew = 0x0E;
    private const uint PadRpcCmdEndNew = 0x0F;
    private const uint PadRpcCmdInit = 0x10;
    private const uint PadRpcCmdGetModVer = 0x12;

    // OLD command codes (rom0:PADMAN FUN_0000655c)
    private const uint PadRpcCmdOpenOld = 0x80000100;
    private const uint PadRpcCmdInfoActOld = 0x80000102;
    private const uint PadRpcCmdInfoCombOld = 0x80000103;
    private const uint PadRpcCmdInfoModeOld = 0x80000104;
    private const uint PadRpcCmdSetMModeOld = 0x80000105;
    private const uint PadRpcCmdSetActDirOld = 0x80000106;
    private const uint PadRpcCmdSetActAlignOld = 0x80000107;
    private const uint PadRpcCmdGetBtnMaskOld = 0x80000108;
    private const uint PadRpcCmdSetBtnInfoOld = 0x80000109;
    private const uint PadRpcCmdSetVrefOld = 0x8000010A;
    private const uint PadRpcCmdGetPortMaxOld = 0x8000010B;
    private const uint PadRpcCmdGetSlotMaxOld = 0x8000010C;
    private const uint PadRpcCmdCloseOld = 0x8000010D;
    private const uint PadRpcCmdEndOld = 0x8000010E;

    private const int PadStateStable = 6;
    private const int PadRstatComplete = 0;
    /// <summary>DualShock digital+analog button mask used by padInfoPressMode (0x3ffff).</summary>
    private const int PadBtnMaskDualShock = 0x3FFFF;

    private struct PadOpenEntry
    {
        public uint PadArea;
        public bool OldStyle; // pad_data_old (rom0) vs pad_data_new
    }

    // (port<<8|slot) -> open DMA area + layout style
    private readonly Dictionary<uint, PadOpenEntry> _padAreas = new();
    /// <summary>
    /// Last-known open pad areas retained across IOP reboot. Active map is cleared so
    /// re-OPEN can succeed; ghost map keeps EE-polled DMA surfaces live until re-OPEN
    /// (MK IOPRP300 gen≥2 never re-OPEN'd while EE still polls the old padArea).
    /// </summary>
    private readonly Dictionary<uint, PadOpenEntry> _padAreasGhost = new();
    private uint _padFrame;

    private int HandlePad(SystemMemory mem, PadInput pad, uint fno, uint argBuf, uint recvBuf, bool oldStyle)
    {
        // fno is almost always 1; command lives in arg buffer word 0.
        uint cmd = argBuf != 0 ? mem.Read32(argBuf) : fno;
        if (cmd == 0)
            cmd = fno != 0 ? fno : (oldStyle ? PadRpcCmdOpenOld : PadRpcCmdOpenNew);

        // Infer layout from command family when caller used the wrong SID but correct cmd words.
        bool useOld = oldStyle
            || cmd >= 0x80000100 && cmd <= 0x8000010E;

        int result = 1;
        switch (cmd)
        {
            case PadRpcCmdInit: // 0x10 — padPortInit (NEW only): result at +0x0C, open_slot DMA
            {
                uint statBuf = argBuf != 0 ? mem.Read32(argBuf + 0x10) : 0;
                if (statBuf != 0 && statBuf < SystemMemory.RDRAM_SIZE - 0x100)
                {
                    // open_slot: frame, openSlots[2], padding — mark both ports connected (bit0)
                    mem.Write32(statBuf + 0, ++_padFrame);
                    mem.Write32(statBuf + 4, 0x01); // port0 slot0
                    mem.Write32(statBuf + 8, 0x01); // port1 slot0
                    mem.Write32(statBuf + 0x80, _padFrame);
                    mem.Write32(statBuf + 0x84, 0x01);
                    mem.Write32(statBuf + 0x88, 0x01);
                }
                result = 1;
                WritePadResultAt(mem, argBuf, recvBuf, result, 0x0C);
                break;
            }
            case PadRpcCmdOpenNew: // 0x01
            case PadRpcCmdOpenOld: // 0x80000100
            {
                // padOpenArgs: +0 cmd, +4 port, +8 slot, +0xC unk, +0x10 padArea
                int port = argBuf != 0 ? (int)mem.Read32(argBuf + 4) : 0;
                int slot = argBuf != 0 ? (int)mem.Read32(argBuf + 8) : 0;
                uint padArea = argBuf != 0 ? mem.Read32(argBuf + 0x10) : 0;
                // NEW needs 512B double-buffer; OLD needs 128B (64×2). Cap conservatively.
                uint need = useOld ? 0x100u : 0x200u;
                if (padArea != 0 && padArea < SystemMemory.RDRAM_SIZE - need)
                {
                    uint key = ((uint)port << 8) | (uint)(slot & 0xFF);
                    // rom0 OPEN fails if already open ("this slot is already open")
                    if (_padAreas.ContainsKey(key))
                    {
                        result = 0;
                    }
                    else
                    {
                        bool style = cmd == PadRpcCmdOpenOld || useOld;
                        var entry = new PadOpenEntry { PadArea = padArea, OldStyle = style };
                        _padAreas[key] = entry;
                        // Real OPEN supersedes any ghost for this port/slot.
                        _padAreasGhost.Remove(key);
                        InitPadArea(mem, pad, padArea, style);
                        result = 1;
                        // padOpenResult: result @+0x0C, padBuf @+0x14 (libpad.c)
                        if (argBuf != 0)
                        {
                            mem.Write32(argBuf + 0x0C, 1);
                            mem.Write32(argBuf + 0x14, padArea);
                        }
                        if (recvBuf != 0 && recvBuf != argBuf)
                        {
                            mem.Write32(recvBuf + 0x0C, 1);
                            mem.Write32(recvBuf + 0x14, padArea);
                        }
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                            || Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                            Console.Error.WriteLine(
                                $"[RPC] PADMAN OPEN port={port} slot={slot} area=0x{padArea:X8} " +
                                $"old={style} opens={_padAreas.Count}");
                    }
                }
                else result = 0;
                WritePadResultAt(mem, argBuf, recvBuf, result, 0x0C);
                break;
            }
            case PadRpcCmdCloseNew:
            case PadRpcCmdCloseOld:
            {
                int port = argBuf != 0 ? (int)mem.Read32(argBuf + 4) : 0;
                int slot = argBuf != 0 ? (int)mem.Read32(argBuf + 8) : 0;
                uint key = ((uint)port << 8) | (uint)(slot & 0xFF);
                result = _padAreas.Remove(key) ? 1 : 0;
                WritePadResultAt(mem, argBuf, recvBuf, result, 0x0C);
                break;
            }
            case PadRpcCmdEndNew:
            case PadRpcCmdEndOld:
                // FUN_00002f18: tear down vblank + open state — clear all open DMA areas.
                _padAreas.Clear();
                result = 1;
                WritePadResultAt(mem, argBuf, recvBuf, result, 0x0C);
                break;
            case PadRpcCmdGetPortMaxNew:
            case PadRpcCmdGetPortMaxOld:
                // FUN_00003df4 always returns 2
                result = 2;
                WritePadResultAt(mem, argBuf, recvBuf, result, 0x0C);
                break;
            case PadRpcCmdGetSlotMaxNew:
            case PadRpcCmdGetSlotMaxOld:
                // FUN_00003dfc always returns 1 (rom0: no multitap)
                result = 1;
                WritePadResultAt(mem, argBuf, recvBuf, result, 0x0C);
                break;
            case PadRpcCmdGetModVer:
                // NEW/disc PADMAN (sid 0x80000100, cmd 0x12): major in high byte.
                // Title split (2026-07-30 A/B on SLUS_210.87 Shaolin Monks):
                //   - major=4 (0x0400): MK:DA / some SN libpad gates (sra ver,8; beq 4).
                //   - major=3 (0x0300): Shaolin Monks retail — 0x0400 drives open-bus thrash
                //     at ~16.8M (PC 0x08002000 → main with SP=0x250 → syscall trampoline
                //     walk 0x47FExx, gifP3 stuck at logo spine). Pre-merge menu6 spine used
                //     0x0300 and reached gifP3=12 / pad band.
                // Default 0x0300 (SM + broad retail). Set <see cref="PadModVerMajor4"/> for
                // titles that hard-require XPADMAN major 4 (MK:DA).
                result = PadModVerMajor4 ? 0x0400 : 0x0300;
                WritePadResultAt(mem, argBuf, recvBuf, result, 0x0C);
                break;
            case PadRpcCmdGetBtnMaskNew:
            case PadRpcCmdGetBtnMaskOld:
                // DualShock full digital+analog mask (padInfoPressMode expects 0x3ffff).
                result = PadBtnMaskDualShock;
                WritePadResultAt(mem, argBuf, recvBuf, result, 0x0C);
                break;
            // OLD info/set cmds write result at +0x14 (FUN_000062e0..FUN_000063e0 family)
            case PadRpcCmdInfoActOld:
            {
                // padInfoActArgs: +4 port, +8 slot, +0xC actuator, +0x10 act_cmd
                int actuator = argBuf != 0 ? (int)mem.Read32(argBuf + 0x0C) : -1;
                result = actuator == -1 ? 2 : 1; // 2 actuators on DualShock
                WritePadResultAt(mem, argBuf, recvBuf, result, 0x14);
                break;
            }
            case PadRpcCmdInfoCombOld:
                result = 0; // no combos HLE'd
                WritePadResultAt(mem, argBuf, recvBuf, result, 0x14);
                break;
            case PadRpcCmdInfoModeOld:
            {
                // padInfoModeArgs: +0xC infoMode, +0x10 index — DualShock cur id = 7
                int infoMode = argBuf != 0 ? (int)mem.Read32(argBuf + 0x0C) : 1;
                result = infoMode == 1 ? 7 : 1;
                WritePadResultAt(mem, argBuf, recvBuf, result, 0x14);
                break;
            }
            case PadRpcCmdSetMModeOld:
            case PadRpcCmdSetActDirOld:
            case PadRpcCmdSetActAlignOld:
                result = 1;
                WritePadResultAt(mem, argBuf, recvBuf, result, 0x14);
                break;
            case PadRpcCmdSetBtnInfoOld:
                // FUN_00006488 writes result at +0x10
                result = 1;
                WritePadResultAt(mem, argBuf, recvBuf, result, 0x10);
                break;
            case PadRpcCmdSetVrefOld:
                // FUN_000064c4 writes result at +0x1c
                result = 1;
                WritePadResultAt(mem, argBuf, recvBuf, result, 0x1c);
                break;
            case PadRpcCmdSetMModeNew:
            case PadRpcCmdSetActDirNew:
            case PadRpcCmdSetActAlignNew:
            case PadRpcCmdSetBtnInfoNew:
            case PadRpcCmdSetVrefNew:
                result = 1;
                WritePadResultAt(mem, argBuf, recvBuf, result, 0x0C);
                break;
            default:
                // Unknown command word: success-shaped so callers progress.
                result = 1;
                WritePadResultAt(mem, argBuf, recvBuf, result, 0x0C);
                break;
        }

        // Always refresh open pad DMA buffers so padGetState (EE-side, not RPC) sees STABLE
        RefreshAllPadAreas(mem, pad);
        return result;
    }

    /// <summary>
    /// Write result to the decomp-backed field offset and also dword0 of recv for generic Dispatch.
    /// </summary>
    private static void WritePadResultAt(SystemMemory mem, uint argBuf, uint recvBuf, int result, uint fieldOff)
    {
        uint r = unchecked((uint)result);
        if (argBuf != 0) mem.Write32(argBuf + fieldOff, r);
        if (recvBuf != 0)
        {
            mem.Write32(recvBuf, r); // Dispatch first-dword convention
            if (fieldOff != 0)
                mem.Write32(recvBuf + fieldOff, r);
        }
    }

    // Play! Iop_PadMan.h PADDATAEX sizeof=0x80; dual-buffer total 0x100 (EE SyncDCache
    // range for Midway libpad is padArea..padArea+0xFF). Stride was wrongly 256, so the
    // second slot at +0x80 (frame@+0xD8) never received STABLE/mode — Dec thrash at
    // SyncDCache 0x10C6xx after PADMAN OPEN (diagnose wall px=0 gif4 dmac4 cdvd137).
    private const uint PadDataExStride = 0x80;
    private const uint PadDataExAreaBytes = 0x100; // dual buffer
    // Play! PAD_MODE_DUALSHOCK=7; modeCurId stores mode in high nibble (7<<4=0x70).
    private const byte PadModeDualShock = 7;
    private const byte PadModeCurIdDualShock = PadModeDualShock << 4;

    private void InitPadArea(SystemMemory mem, PadInput pad, uint padArea, bool oldStyle)
    {
        if (oldStyle)
        {
            // pad_data_old is 64B × 2 (double-buffered). SyncDCache uses 256 for safety.
            WritePadDataOld(mem, pad, padArea, preferHigherFrame: false);
            WritePadDataOld(mem, pad, padArea + 64, preferHigherFrame: true);
        }
        else
        {
            // PADDATAEX dual buffer at +0 / +0x80 (Play! / Midway libpad sll 7 select).
            WritePadDataNew(mem, pad, padArea, preferHigherFrame: false);
            WritePadDataNew(mem, pad, padArea + PadDataExStride, preferHigherFrame: true);
        }
    }

    /// <summary>
    /// pad_data_old (ps2sdk libpad.c / rom0:PADMAN) — 64-byte records, double-buffered.
    /// frame@0, state@4, reqState@5, ok@6, data[32]@8, length@0x28, CTP@0x2D, model@0x2E.
    /// </summary>
    private void WritePadDataOld(SystemMemory mem, PadInput pad, uint baseP, bool preferHigherFrame)
    {
        _padFrame++;
        uint frame = preferHigherFrame ? _padFrame + 1 : _padFrame;
        mem.Write32(baseP + 0x00, frame);
        mem.Write8(baseP + 0x04, (byte)PadStateStable);
        mem.Write8(baseP + 0x05, (byte)PadRstatComplete);
        mem.Write8(baseP + 0x06, 1); // ok
        mem.Write8(baseP + 0x07, 0);
        WritePadButtonData(mem, pad, baseP + 0x08);
        mem.Write32(baseP + 0x28, 32); // length
        mem.Write8(baseP + 0x2C, 0);   // request
        mem.Write8(baseP + 0x2D, 2);   // CTP=2 config
        mem.Write8(baseP + 0x2E, 3);   // model DualShock
        mem.Write8(baseP + 0x2F, 1);   // correction applied
        mem.Write8(baseP + 0x30, 0);   // errorCount
    }

    /// <summary>
    /// pad_data_new / Play! <c>PADDATAEX</c> (128 B per slot, dual-buffer total 256 B).
    /// data[32]@0, reserved/act@0x20.., modeTable u16[4]@0x50, frame@0x58, length@0x60,
    /// modeOk@0x64, modeCurId@0x65, model@0x66, nrOfModes@0x68, modeCurOffset@0x69,
    /// state@0x70, reqState@0x71, ok@0x72. EE selects slot via frame@+0x58 vs frame@+0xD8
    /// (offset 0x80) — Midway <c>sll 7</c> dual-buffer pick (Dec <c>0x1187B4</c>).
    /// </summary>
    private void WritePadDataNew(SystemMemory mem, PadInput pad, uint baseP, bool preferHigherFrame)
    {
        _padFrame++;
        uint frame = preferHigherFrame ? _padFrame + 1 : _padFrame;
        for (uint o = 0; o < 32; o++)
            mem.Write8(baseP + o, 0xFF);
        WritePadButtonData(mem, pad, baseP);

        // DualShock actuator/mode table defaults (Play! PDF_InitializeStruct1).
        mem.Write8(baseP + 0x30, 0); // act0 type
        mem.Write8(baseP + 0x34, 1); // act1 type (large motor)
        // modeTable[0] = PAD_MODE_DUALSHOCK (u16 LE) — was mis-encoded as 0x0700.
        mem.Write8(baseP + 0x50, PadModeDualShock);
        mem.Write8(baseP + 0x51, 0);
        mem.Write8(baseP + 0x6A, 2); // nrOfActuators

        mem.Write32(baseP + 0x58, frame);
        mem.Write32(baseP + 0x5C, 0); // findPadRetries
        mem.Write32(baseP + 0x60, 32); // length
        mem.Write8(baseP + 0x64, 2); // modeOk / modeConfig (config done)
        // modeCurId: mode in high nibble (Play! SetModeCurId(PAD_MODE_DUALSHOCK<<4)).
        mem.Write8(baseP + 0x65, PadModeCurIdDualShock);
        mem.Write8(baseP + 0x66, 3); // model DualShock
        mem.Write8(baseP + 0x67, 1); // buttonDataReady (reserved in EX; keep 1)
        mem.Write8(baseP + 0x68, 4); // nrOfModes (Play! PDF_InitializeStruct1)
        mem.Write8(baseP + 0x69, 0); // modeCurOffset
        mem.Write8(baseP + 0x70, (byte)PadStateStable);
        mem.Write8(baseP + 0x71, (byte)PadRstatComplete);
        mem.Write8(baseP + 0x72, 1); // ok (Play! nOk; EE also treats as currentTask==1)
        mem.Write8(baseP + 0x73, 0);
    }

    /// <summary>
    /// Standard padButtonStatus bytes: ok, mode, btns (u16 active-low), rjoy, ljoy.
    /// Default dualshock-shaped (0x79) so mode-set waits do not thrash on digital 0x41
    /// when AnalogMode has not been host-toggled yet (commercial padPortOpen path).
    /// </summary>
    private static void WritePadButtonData(SystemMemory mem, PadInput pad, uint dataBase)
    {
        mem.Write8(dataBase + 0, 0x00); // ok
        // Prefer DualShock type byte unless host explicitly left digital-only.
        mem.Write8(dataBase + 1, (byte)0x79);
        ushort btns = (ushort)(~pad.Buttons & 0xFFFF); // hardware/libpad: active-low
        mem.Write8(dataBase + 2, (byte)(btns & 0xFF));
        mem.Write8(dataBase + 3, (byte)(btns >> 8));
        mem.Write8(dataBase + 4, pad.Rx);
        mem.Write8(dataBase + 5, pad.Ry);
        mem.Write8(dataBase + 6, pad.Lx);
        mem.Write8(dataBase + 7, pad.Ly);
        for (uint o = 8; o < 32; o++)
            mem.Write8(dataBase + o, 0x00); // pressure / reserved
    }

    private void RefreshPadMap(SystemMemory mem, PadInput pad, Dictionary<uint, PadOpenEntry> map)
    {
        foreach (var kv in map)
        {
            uint padArea = kv.Value.PadArea;
            if (padArea == 0 || padArea >= SystemMemory.RDRAM_SIZE - 0x80) continue;
            if (kv.Value.OldStyle)
            {
                WritePadDataOld(mem, pad, padArea, preferHigherFrame: false);
                WritePadDataOld(mem, pad, padArea + 64, preferHigherFrame: true);
            }
            else
            {
                // Need room for dual PADDATAEX (0x100 total).
                if (padArea >= SystemMemory.RDRAM_SIZE - PadDataExAreaBytes) continue;
                WritePadDataNew(mem, pad, padArea, preferHigherFrame: false);
                WritePadDataNew(mem, pad, padArea + PadDataExStride, preferHigherFrame: true);
            }
        }
    }

    private void RefreshAllPadAreas(SystemMemory mem, PadInput pad)
    {
        RefreshPadMap(mem, pad, _padAreas);
        // After IOP reboot with no re-OPEN yet, keep ghost surfaces live so EE padRead
        // still sees STABLE + current buttons (MK gen≥2 IOPRP300 path).
        if (_padAreas.Count == 0 && _padAreasGhost.Count > 0)
            RefreshPadMap(mem, pad, _padAreasGhost);
    }

    /// <summary>
    /// IOP PADMAN continuous update — padGetState/padRead are EE-side DMA buffer polls,
    /// not RPC. Call once per VBlank so STABLE + button data stay live.
    /// Also refreshes ghost areas retained across IOP reboot until re-OPEN.
    /// </summary>
    public void TickPadDma(SystemMemory mem, PadInput pad)
    {
        if (_padAreas.Count == 0 && _padAreasGhost.Count == 0) return;
        RefreshAllPadAreas(mem, pad);
    }

    /// <summary>Test/diagnostics: number of currently open pad DMA areas (active only).</summary>
    public int OpenPadCount => _padAreas.Count;

    /// <summary>Ghost pad areas retained across IOP reboot (diagnostics).</summary>
    public int GhostPadCount => _padAreasGhost.Count;

    /// <summary>Force-refresh pad DMA now (menu pad inject / host present). Safe no-op when empty.</summary>
    public void ForceRefreshPad(SystemMemory mem, PadInput pad) => TickPadDma(mem, pad);



    private const uint McFnoInit = 0x70;
    private const uint McFnoOpen = 0x71;
    private const uint McFnoClose = 0x72;
    private const uint McFnoRead = 0x73;
    private const uint McFnoWrite = 0x74;
    private const uint McFnoSeek = 0x75;
    private const uint McFnoGetDir = 0x76;
    private const uint McFnoFormat = 0x77;
    private const uint McFnoGetInfo = 0x78;
    private const uint McFnoDelete = 0x79;
    private const uint McFnoFlush = 0x7A;
    private const uint McFnoChDir = 0x7B;
    private const uint McFnoSetInfo = 0x7C;
    private const uint McFnoEraseBlock = 0x7D;
    private const uint McFnoReadPage = 0x7E;
    private const uint McFnoWritePage = 0x7F;
    private const uint McFnoUnformat = 0x80;
    // XMCSERV (disc MODULES/MCSERV.IRX, Midway MK:DA et al.): init is fno 0xFE, not 0x70.
    // Returning -5 here made libmc-style probes "fall back" but Midway hard-fails and Exit(0).
    private const uint McFnoXInit = 0xFE;
    // XMCSERV getInfo is fno 0x01 (classic MCSERV uses 0x78).
    private const uint McFnoXGetInfo = 0x01;

    // libmc-common.h result / type codes used by EE-side endFunc + probes.
    private const int McResSucceed = 0;
    private const int McResChangedCard = -1;
    private const int McResNoFormat = -2;
    private const int McResNoEntry = -4;
    private const int McResDeniedPermit = -5; // sceMcResDeniedPermit — unhandled / bad fd
    private const int McTypePs1 = 1;
    private const int McTypePs2 = 2;

    // sceMcFileAttr* bits commonly set on directory table entries.
    private const ushort McAttrFileRwx = 0x0010 | 0x0001 | 0x0002 | 0x8000; // File|R|W|Exists
    private const ushort McAttrDirRwx = 0x0020 | 0x0001 | 0x0002 | 0x8000;  // Subdir|R|W|Exists

    // Open mode bits used by libmc / MCMAN (sceMcFileCreateFile / CreateDir).
    private const int McOpenCreateFile = 0x0200;
    private const int McOpenCreateDir = 0x0040;

    private sealed class McOpenFile
    {
        public string Name = "";
        public int Position;
        public int Mode;
        public byte[] Data = Array.Empty<byte>();
        public bool Dirty;
        public bool IsDir;
    }

    private readonly Dictionary<int, McOpenFile> _mcFds = new();
    private int _mcNextFd;
    private string _mcCwd = "/";

    /// <summary>
    /// MCSERV RPC dispatcher. Arg layouts from ps2sdk <c>mcDescParam_t</c> (fd ops) and
    /// <c>libmc_name_param_stru</c> (path ops). Backend is dual-format <see cref="MemoryCard"/>
    /// (DetPS2 native / Sony PS2 MCFS / PS1) — see <c>docs/bios-ports/MCSERV.md</c>.
    /// Unmapped classic fnos return <see cref="McResDeniedPermit"/>. XMCSERV init
    /// (<c>0xFE</c>) and getInfo (<c>0x01</c>) are accepted so disc MCSERV.IRX clients
    /// (MK: Deadly Alliance after LoadModule MCSERV) do not Exit on probe failure.
    /// </summary>
    private int HandleMcServ(SystemMemory mem, IopModuleHost iopModules, uint fno, uint argBuf, uint recvBuf)
    {
        MemoryCard card = iopModules.MemCard;
        _ = recvBuf;

        int result = fno switch
        {
            McFnoInit => McservInit(mem, argBuf),
            McFnoXInit => McservXInit(mem, argBuf, recvBuf),
            McFnoOpen => McservOpen(mem, card, argBuf),
            McFnoClose => McservClose(mem, card, argBuf),
            McFnoRead => McservRead(mem, argBuf),
            McFnoWrite => McservWrite(mem, argBuf),
            McFnoSeek => McservSeek(mem, argBuf),
            McFnoGetDir => McservGetDir(mem, card, argBuf),
            McFnoFormat => McservFormat(mem, card, argBuf),
            McFnoGetInfo => McservGetInfo(mem, card, argBuf),
            McFnoXGetInfo => McservGetInfo(mem, card, argBuf), // XMCSERV GET_INFO
            McFnoDelete => McservDelete(mem, card, argBuf),
            McFnoFlush => McservFlush(mem, card, argBuf),
            McFnoChDir => McservChDir(mem, argBuf),
            McFnoSetInfo => McservSetInfo(mem, card, argBuf),
            McFnoEraseBlock => McservEraseBlock(mem, card, argBuf),
            McFnoReadPage => McservReadPage(mem, card, argBuf),
            McFnoWritePage => McservWritePage(mem, card, argBuf),
            McFnoUnformat => McservUnformat(mem, card, argBuf),
            _ => McResDeniedPermit,
        };
        return result;
    }

    /// <summary>
    /// XMCSERV INIT (fno 0xFE). Midway MK:DA (and libmc XMCSERV clients) expect a 12-byte
    /// reply: result@+0, mcservVer@+4, mcmanVer@+8. DA's EE gate at 0x117E74 requires
    /// mcservVer ≥ 522 and mcmanVer ≥ 526; bare result=0 left those words zero → -120 Exit.
    /// </summary>
    private static int McservXInit(SystemMemory mem, uint argBuf, uint recvBuf)
    {
        _ = argBuf;
        // Version tokens above Midway's floors (522/526). Not exact IRX module versions —
        // structural HLE so version gates pass without parsing disc MCSERV.IRX headers.
        const uint McServVer = 0x020A; // 522
        const uint McManVer = 0x020E;  // 526
        if (recvBuf != 0)
        {
            mem.Write32(recvBuf + 0, 0);          // result
            mem.Write32(recvBuf + 4, McServVer);
            mem.Write32(recvBuf + 8, McManVer);
        }
        return McResSucceed;
    }

    // mcDescParam_t offsets (libmc-common.h, size 48).
    private static int McDescFd(SystemMemory mem, uint a) => a != 0 ? (int)mem.Read32(a + 0) : -1;
    private static int McDescPort(SystemMemory mem, uint a) => a != 0 ? (int)mem.Read32(a + 4) : 0;
    private static int McDescSlot(SystemMemory mem, uint a) => a != 0 ? (int)mem.Read32(a + 8) : 0;
    private static int McDescSize(SystemMemory mem, uint a) => a != 0 ? (int)mem.Read32(a + 12) : 0;
    private static int McDescOffset(SystemMemory mem, uint a) => a != 0 ? (int)mem.Read32(a + 16) : 0;
    private static int McDescOrigin(SystemMemory mem, uint a) => a != 0 ? (int)mem.Read32(a + 20) : 0;
    private static uint McDescBuffer(SystemMemory mem, uint a) => a != 0 ? mem.Read32(a + 24) : 0;
    private static uint McDescParam(SystemMemory mem, uint a) => a != 0 ? mem.Read32(a + 28) : 0;

    // name param: port@0 slot@4 flags@8 maxent@12 ptr@16 name@20
    private static int McNamePort(SystemMemory mem, uint a) => a != 0 ? (int)mem.Read32(a + 0) : 0;
    private static int McNameSlot(SystemMemory mem, uint a) => a != 0 ? (int)mem.Read32(a + 4) : 0;
    private static int McNameFlags(SystemMemory mem, uint a) => a != 0 ? (int)mem.Read32(a + 8) : 0;
    private static int McNameMaxent(SystemMemory mem, uint a) => a != 0 ? (int)mem.Read32(a + 12) : 0;
    private static uint McNamePtr(SystemMemory mem, uint a) => a != 0 ? mem.Read32(a + 16) : 0;
    private static string McNameString(SystemMemory mem, uint a) =>
        a != 0 ? ReadCString(mem, a + 20, 1024) : "";

    /// <summary>0x70 INIT — FUN_00000320. libmc sets offset=-217 as MCSERV probe magic.</summary>
    private static int McservInit(SystemMemory mem, uint argBuf)
    {
        // Real handler only gates MCMAN init; always succeeds for present card HLE.
        _ = mem; _ = argBuf;
        return McResSucceed;
    }

    /// <summary>0x71 OPEN — name param; returns fd ≥ 0. mkdir uses flags=0x40.</summary>
    private int McservOpen(SystemMemory mem, MemoryCard card, uint argBuf)
    {
        string name = NormalizeMcPath(McNameString(mem, argBuf));
        int flags = McNameFlags(mem, argBuf);

        if ((flags & McOpenCreateDir) != 0)
        {
            // Directory create: no real dir tree on HLE card — succeed so games proceed.
            int dirFd = AllocMcFd(new McOpenFile { Name = name, IsDir = true, Mode = flags });
            return dirFd;
        }

        byte[]? existing = card.ReadFile(name);
        if (existing == null)
        {
            if ((flags & McOpenCreateFile) == 0 && (flags & 0x0202) == 0)
            {
                // Not create: fail missing (allow O_WRONLY-ish without create for truncate paths
                // some titles use — if purely read without create, NoEntry).
                if ((flags & 0x3) == 1) // O_RDONLY
                    return McResNoEntry;
            }
            existing = Array.Empty<byte>();
            if ((flags & McOpenCreateFile) != 0 || (flags & 0x0200) != 0 || (flags & 0x2) != 0)
                card.WriteFile(name, existing);
        }

        int fd = AllocMcFd(new McOpenFile
        {
            Name = name,
            Mode = flags,
            Data = existing,
            Position = 0,
        });
        return fd;
    }

    /// <summary>0x72 CLOSE — desc.fd.</summary>
    private int McservClose(SystemMemory mem, MemoryCard card, uint argBuf)
    {
        int fd = McDescFd(mem, argBuf);
        if (!_mcFds.TryGetValue(fd, out var file))
            return McResDeniedPermit;
        if (file.Dirty && !file.IsDir)
            card.WriteFile(file.Name, file.Data);
        _mcFds.Remove(fd);
        return McResSucceed;
    }

    /// <summary>0x73 READ — desc: fd@0 size@12 buffer@24 param@28 (align fixup endParam).</summary>
    private int McservRead(SystemMemory mem, uint argBuf)
    {
        int fd = McDescFd(mem, argBuf);
        if (!_mcFds.TryGetValue(fd, out var file) || file.IsDir)
            return McResDeniedPermit;

        int size = McDescSize(mem, argBuf);
        uint buffer = McDescBuffer(mem, argBuf);
        size = Math.Clamp(size, 0, 0x10000);

        int available = Math.Max(0, file.Data.Length - file.Position);
        int n = Math.Min(size, available);
        for (int i = 0; i < n && buffer != 0; i++)
            mem.Write8(buffer + (uint)i, file.Data[file.Position + i]);
        // Pad remainder with zeros when reading past EOF with larger request (short read OK).
        for (int i = n; i < size && buffer != 0 && n == size; i++)
            mem.Write8(buffer + (uint)i, 0);

        // Old MCSERV also DMAs mcEndParam_t to param for unaligned head/tail fixup on EE.
        // Aligned whole-buffer path: size1=size2=0 is fine (libmc fixup is a no-op).
        uint param = McDescParam(mem, argBuf);
        if (param != 0)
        {
            mem.Write32(param + 0, 0); // size1
            mem.Write32(param + 4, 0); // size2
            mem.Write32(param + 8, 0); // dest1
            mem.Write32(param + 12, 0); // dest2
        }

        file.Position += n;
        return n;
    }

    /// <summary>0x74 WRITE — desc: size@12 origin@20 (unaligned head len) buffer@24 data@32.</summary>
    private int McservWrite(SystemMemory mem, uint argBuf)
    {
        int fd = McDescFd(mem, argBuf);
        if (!_mcFds.TryGetValue(fd, out var file) || file.IsDir)
            return McResDeniedPermit;

        int size = McDescSize(mem, argBuf);     // aligned bulk
        int origin = McDescOrigin(mem, argBuf); // unaligned head (≤16)
        uint buffer = McDescBuffer(mem, argBuf);
        origin = Math.Clamp(origin, 0, 16);
        size = Math.Clamp(size, 0, 0x10000);

        int total = size + origin;
        EnsureMcFileCapacity(file, file.Position + total);

        // Unaligned head bytes live inline in desc.data[16] at +32.
        for (int i = 0; i < origin && argBuf != 0; i++)
            file.Data[file.Position + i] = mem.Read8(argBuf + 32 + (uint)i);

        for (int i = 0; i < size && buffer != 0; i++)
            file.Data[file.Position + origin + i] = mem.Read8(buffer + (uint)i);

        file.Position += total;
        file.Dirty = true;
        return total;
    }

    /// <summary>0x75 SEEK — desc: fd@0 offset@16 origin@20 (0=SET,1=CUR,2=END).</summary>
    private int McservSeek(SystemMemory mem, uint argBuf)
    {
        int fd = McDescFd(mem, argBuf);
        if (!_mcFds.TryGetValue(fd, out var file))
            return McResDeniedPermit;

        int offset = McDescOffset(mem, argBuf);
        int origin = McDescOrigin(mem, argBuf);
        int pos = origin switch
        {
            1 => file.Position + offset,
            2 => file.Data.Length + offset,
            _ => offset,
        };
        if (pos < 0) pos = 0;
        file.Position = pos;
        return pos;
    }

    /// <summary>0x76 GET_DIR — name param; maxent@12 table@16; returns entry count.</summary>
    private int McservGetDir(SystemMemory mem, MemoryCard card, uint argBuf)
    {
        int maxent = McNameMaxent(mem, argBuf);
        uint table = McNamePtr(mem, argBuf);
        string pattern = NormalizeMcPath(McNameString(mem, argBuf));
        if (maxent <= 0) return 0;
        maxent = Math.Min(maxent, 64);

        // Wildcard / empty / "*" / "/*" → all files. Simple suffix/prefix * support.
        var names = new List<string>();
        foreach (string n in card.FileNames)
        {
            if (McNameMatches(pattern, n))
                names.Add(n);
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);

        int count = Math.Min(maxent, names.Count);
        for (int i = 0; i < count && table != 0; i++)
        {
            uint e = table + (uint)(i * 64);
            // Zero the 64-byte sceMcTblGetDir entry.
            for (int b = 0; b < 64; b++)
                mem.Write8(e + (uint)b, 0);

            byte[]? data = card.ReadFile(names[i]);
            int len = data?.Length ?? 0;
            mem.Write32(e + 16, (uint)len);          // FileSizeByte
            mem.Write8(e + 20, unchecked((byte)McAttrFileRwx)); // AttrFile (u16 LE)
            mem.Write8(e + 21, unchecked((byte)(McAttrFileRwx >> 8)));
            WriteMcEntryName(mem, e + 32, names[i]);  // EntryName[32]
        }
        return count;
    }

    /// <summary>
    /// 0x77 FORMAT — desc port@4 slot@8.
    /// Produces a Sony PS2 MCFS image (magic + "1.1.0.0" superblock + IFC/FAT) so MCMAN
    /// dual-format probes (FUN_000005ac type=2) and raw page readers see a real layout.
    /// </summary>
    private int McservFormat(SystemMemory mem, MemoryCard card, uint argBuf)
    {
        _ = McDescPort(mem, argBuf);
        _ = McDescSlot(mem, argBuf);
        card.FormatSonyPs2();
        _mcFds.Clear();
        _mcCwd = "/";
        return McResSucceed;
    }

    /// <summary>
    /// 0x78 GET_INFO — desc port@4 slot@8; size/offset/origin are want-type/free/format flags
    /// for rom0 MCSERV; result type/free written to param (mcEndParam_t) for EE endFunc.
    /// Type comes from dual-format <see cref="MemoryCard.CardType"/>; free from FAT/block free list.
    /// </summary>
    private static int McservGetInfo(SystemMemory mem, MemoryCard card, uint argBuf)
    {
        int wantType = McDescSize(mem, argBuf);    // old MCSERV: size flag → type
        int wantFree = McDescOffset(mem, argBuf);  // offset flag → free
        int wantFmt = McDescOrigin(mem, argBuf);   // origin flag → format (emulated)
        uint param = McDescParam(mem, argBuf);

        if (!card.Formatted)
        {
            // Unformatted: type may still be reported as present media; free=0.
            if (param != 0)
            {
                if (wantType != 0) mem.Write32(param + 0, McTypePs2);
                if (wantFree != 0) mem.Write32(param + 4, 0);
                if (wantFmt != 0) mem.Write32(param + 144, 0);
                if (wantType == 0 && wantFree == 0)
                {
                    mem.Write32(param + 0, McTypePs2);
                    mem.Write32(param + 4, 0);
                }
            }
            return McResNoFormat;
        }

        int type = (int)card.CardType;
        if (type == 0) type = McTypePs2;
        int free = Math.Max(0, card.FreeUnits);
        int formatted = 1;

        if (param != 0)
        {
            // mcEndParam_t / mcEndParam2_t: type@0 free@4; formatted@144 on new only.
            if (wantType != 0) mem.Write32(param + 0, (uint)type);
            if (wantFree != 0) mem.Write32(param + 4, (uint)free);
            // Also write formatted for newer endFunc that reads endParam2.formatted@144.
            if (wantFmt != 0) mem.Write32(param + 144, (uint)formatted);
            // Mirror into first words when all flags set so simple probes work.
            if (wantType == 0 && wantFree == 0)
            {
                mem.Write32(param + 0, (uint)type);
                mem.Write32(param + 4, (uint)free);
            }
        }

        // mcSync result: 0 = same card. (Hotplug change −1 not tracked without media model.)
        return McResSucceed;
    }

    /// <summary>0x7C SET_INFO — name param; accept and succeed (attrs not persisted on all kinds).</summary>
    private static int McservSetInfo(SystemMemory mem, MemoryCard card, uint argBuf)
    {
        // Real MCSERV updates create/modify times + attr mask from name-param payload.
        // HLE: validate name exists when non-empty; still succeed for create-path probes.
        string name = NormalizeMcPath(McNameString(mem, argBuf));
        if (!string.IsNullOrEmpty(name) && !card.HasFile(name))
        {
            // Directory / not-yet-written entries: still OK (games may set info before flush).
        }
        return McResSucceed;
    }

    /// <summary>
    /// 0x7D ERASE_BLOCK — dual-format aware.
    /// MCSERV decomp FUN_00000ab8: port&amp;1+2 type probe; PS2 erases 16 pages/block, PS1 uses 0x80 frame path.
    /// </summary>
    private static int McservEraseBlock(SystemMemory mem, MemoryCard card, uint argBuf)
    {
        int block = McDescOffset(mem, argBuf); // block index commonly in offset/origin; accept fd field too
        if (block == 0) block = McDescFd(mem, argBuf);
        if (block < 0) block = 0;
        return card.EraseBlock(block) ? McResSucceed : McResDeniedPermit;
    }

    /// <summary>0x79 DELETE — name param.</summary>
    private int McservDelete(SystemMemory mem, MemoryCard card, uint argBuf)
    {
        string name = NormalizeMcPath(McNameString(mem, argBuf));
        if (!card.HasFile(name))
            return McResNoEntry;
        card.DeleteFile(name);
        // Drop any open handles on that name.
        foreach (var kv in _mcFds)
        {
            if (string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                kv.Value.Dirty = false;
        }
        return McResSucceed;
    }

    /// <summary>0x7A FLUSH — desc.fd. Invalid fd (e.g. 0xFFFFFFFF) → DeniedPermit (libmc probe).</summary>
    private int McservFlush(SystemMemory mem, MemoryCard card, uint argBuf)
    {
        int fd = McDescFd(mem, argBuf);
        if (!_mcFds.TryGetValue(fd, out var file))
            return McResDeniedPermit;
        if (file.Dirty && !file.IsDir)
        {
            card.WriteFile(file.Name, file.Data);
            file.Dirty = false;
        }
        return McResSucceed;
    }

    /// <summary>0x7B CH_DIR — name param; optional curdir buffer @+16.</summary>
    private int McservChDir(SystemMemory mem, uint argBuf)
    {
        string newDir = McNameString(mem, argBuf);
        if (!string.IsNullOrEmpty(newDir))
            _mcCwd = newDir.StartsWith('/') ? newDir : "/" + newDir;

        uint curdir = McNamePtr(mem, argBuf);
        if (curdir != 0)
        {
            string cwd = _mcCwd;
            for (int i = 0; i < 1023 && i < cwd.Length; i++)
                mem.Write8(curdir + (uint)i, (byte)cwd[i]);
            mem.Write8(curdir + (uint)Math.Min(1023, cwd.Length), 0);
        }
        return McResSucceed;
    }

    /// <summary>0x7E READ_PAGE — desc: page in fd field, port@4 slot@8 buffer@24.</summary>
    private static int McservReadPage(SystemMemory mem, MemoryCard card, uint argBuf)
    {
        int page = McDescFd(mem, argBuf);
        uint buffer = McDescBuffer(mem, argBuf);
        if (buffer == 0) return McResSucceed;
        Span<byte> tmp = stackalloc byte[MemoryCard.PageSize];
        card.ReadPage(page, tmp);
        for (int i = 0; i < MemoryCard.PageSize; i++)
            mem.Write8(buffer + (uint)i, tmp[i]);
        return McResSucceed;
    }

    /// <summary>0x7F WRITE_PAGE — desc: page in fd, buffer@24, optional misaligned data@32.</summary>
    private static int McservWritePage(SystemMemory mem, MemoryCard card, uint argBuf)
    {
        int page = McDescFd(mem, argBuf);
        uint buffer = McDescBuffer(mem, argBuf);
        Span<byte> tmp = stackalloc byte[MemoryCard.PageSize];
        if (buffer != 0)
        {
            for (int i = 0; i < MemoryCard.PageSize; i++)
                tmp[i] = mem.Read8(buffer + (uint)i);
        }
        card.WritePage(page, tmp);
        return McResSucceed;
    }

    /// <summary>0x80 UNFORMAT — wipe and re-init as empty Sony PS2 dual-format card.</summary>
    private int McservUnformat(SystemMemory mem, MemoryCard card, uint argBuf)
    {
        _ = McDescPort(mem, argBuf);
        _ = McDescSlot(mem, argBuf);
        // Real unformat leaves media unformatted; HLE re-formats to Sony PS2 so subsequent
        // getInfo still sees present media (titles rarely distinguish unformat vs format here).
        card.FormatSonyPs2();
        _mcFds.Clear();
        _mcCwd = "/";
        return McResSucceed;
    }

    private int AllocMcFd(McOpenFile file)
    {
        // MCMAN-like small fd space; reuse holes first.
        for (int i = 0; i < 16; i++)
        {
            if (!_mcFds.ContainsKey(i))
            {
                _mcFds[i] = file;
                if (i >= _mcNextFd) _mcNextFd = i + 1;
                return i;
            }
        }
        return McResDeniedPermit; // up-limit handles
    }

    private static void EnsureMcFileCapacity(McOpenFile file, int minLen)
    {
        if (file.Data.Length >= minLen) return;
        var grown = new byte[minLen];
        Buffer.BlockCopy(file.Data, 0, grown, 0, file.Data.Length);
        file.Data = grown;
    }

    private static string NormalizeMcPath(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        // Strip leading "mc0:" / "mc1:" / slashes for MemoryCard flat namespace.
        int colon = name.IndexOf(':');
        if (colon >= 0) name = name[(colon + 1)..];
        name = name.Trim().TrimStart('/', '\\');
        // Drop trailing slash for dir names.
        name = name.TrimEnd('/', '\\');
        // Take final component for flat card store.
        int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (slash >= 0) name = name[(slash + 1)..];
        return name;
    }

    private static bool McNameMatches(string pattern, string name)
    {
        if (string.IsNullOrEmpty(pattern) || pattern is "*" or "/*" or "?*")
            return true;
        pattern = NormalizeMcPath(pattern);
        if (pattern is "*" or "") return true;
        if (pattern.EndsWith('*'))
        {
            string prefix = pattern[..^1];
            return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        if (pattern.StartsWith('*'))
        {
            string suffix = pattern[1..];
            return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteMcEntryName(SystemMemory mem, uint addr, string name)
    {
        int n = Math.Min(31, name.Length);
        for (int i = 0; i < n; i++)
            mem.Write8(addr + (uint)i, (byte)name[i]);
        mem.Write8(addr + (uint)n, 0);
    }

}
