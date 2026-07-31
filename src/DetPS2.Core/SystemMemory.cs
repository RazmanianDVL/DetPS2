using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DetPS2.Core;

/// <summary>
/// System memory map (Phase 8).
/// EE RDRAM 32MB, Scratchpad 16KB @ 0x70000000, IOP RAM 2MB @ 0x1C000000,
/// BIOS window 4MB @ 0x1FC00000, MMIO @ 0x10000000–0x1FFFFFFF via optional bus.
/// </summary>
public sealed class SystemMemory
{
    public const int RDRAM_SIZE = 32 * 1024 * 1024;
    public const int SPR_SIZE = 16 * 1024;
    public const int IOP_RAM_SIZE = 2 * 1024 * 1024;
    public const int BIOS_SIZE = 4 * 1024 * 1024;

    public const uint SPR_BASE = 0x70000000;
    public const uint IOP_RAM_BASE = 0x1C000000;
    public const uint BIOS_BASE = 0x1FC00000;
    public const uint MMIO_BASE = 0x10000000;
    public const uint MMIO_END = 0x1BFFFFFF;

    /// <summary>
    /// True if <paramref name="addr"/> looks like EE user code, not string/data.
    /// Used by open-bus / nop-sled rescue so we never re-home into ASCII .rodata
    /// (MK thrash: 0x06403800 ↔ 0x00520040 data words, 2026-07-30).
    /// Requires two consecutive plausible instructions — a single REGIMM-looking
    /// data word (e.g. 0x04089300) previously slipped through.
    /// </summary>
    public bool IsLikelyEeCode(ulong addr)
    {
        uint p = (uint)(addr & 0x1FFFFFFFUL);
        // Commercial EE .text sits in low RDRAM; high ELF image is data/BSS/strings.
        if (p < 0x00100000u || p >= 0x00580000u) return false;
        if ((p & 3u) != 0) return false;
        return WordLooksLikeInsn(Read32(p)) && WordLooksLikeInsn(Read32(p + 4));
    }

    private static bool WordLooksLikeInsn(uint op)
    {
        if (op == 0) return false; // nop alone is weak; dual-check needs real work
        // Reject ASCII-heavy words (3+ printable bytes) — classic string mis-exec.
        int printable = 0, zeros = 0;
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(op >> (8 * i));
            if (b is >= 0x20 and <= 0x7E) printable++;
            if (b == 0) zeros++;
        }
        if (printable >= 3) return false;
        // Sparse words (3 zero bytes) are almost always data, not real ALU/branch ops.
        if (zeros >= 3) return false;
        uint primary = op >> 26;
        if (primary == 1) // REGIMM — only known rt sub-ops
        {
            uint rt = (op >> 16) & 0x1F;
            return rt is 0 or 1 or 2 or 3 or 8 or 9 or 10 or 11 or 12 or 14
                or 16 or 17 or 18 or 19;
        }
        // Common R5900 primary opcodes (SPECIAL/J/branches/ALU/COP/loads/stores/MMI).
        // Include 62/63 (LQ/SQ and LD/SD family used heavily in EE prologs: sd s0,0(sp)).
        // Without 63, dual-word IsLikelyEeCode rejects real Midway callbacks that start
        // with addiu sp / sd sN (group-6 multi registration targets, 2026-07-30).
        return primary is 0 or 2 or 3 or 4 or 5 or 6 or 7
            or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15
            or 16 or 17 or 18
            or 20 or 21 or 22 or 23
            or 28 or 31
            or 32 or 33 or 34 or 35 or 36 or 37 or 38 or 39
            or 40 or 41 or 42 or 43 or 44 or 45 or 46 or 47
            or 49 or 51 or 53 or 57 or 59 or 61
            or 62 or 63;
    }

    private readonly byte[] _rdram = new byte[RDRAM_SIZE];
    private readonly byte[] _scratchpad = new byte[SPR_SIZE];
    private readonly byte[] _iopRam = new byte[IOP_RAM_SIZE];
    private readonly byte[] _bios = new byte[BIOS_SIZE];
    /// <summary>IOP I/O ports at phys 0x1F801000 (word-indexed). See <see cref="IOP_IO_BASE"/>.</summary>
    private readonly uint[] _iopIo = new uint[IOP_IO_SIZE / 4];

    private MmioBus? _mmio;
    private Spu2? _spu2;
    private Sif? _sif;
    private Cdvd? _cdvd;
    /// <summary>When true, refuse writes to exception vector page (0x0–0x2FF) so memset cannot wipe handlers.</summary>
    public bool ProtectKernelVectors { get; set; }

    public void AttachMmio(MmioBus bus) => _mmio = bus ?? throw new ArgumentNullException(nameof(bus));
    public void AttachSpu2(Spu2 spu2) => _spu2 = spu2;
    public void AttachSif(Sif sif) => _sif = sif;
    /// <summary>IOP CDVD register window at <see cref="Cdvd.PhysBase"/> (WP-18).</summary>
    public void AttachCdvd(Cdvd cdvd) => _cdvd = cdvd;

    /// <summary>Reset IOP I/O port scratchpad to power-on defaults (INTRMAN config bit, etc.).</summary>
    public void ResetIopIoPorts()
    {
        Array.Clear(_iopIo);
        // Indices are offsets from IOP_IO_BASE (0x1F801000).
        // 0x1F801450 — INTRMANP PS2-mode / board-ID bit 3 must be set for full init.
        _iopIo[0x450 / 4] = IopIoIntrmanConfigDefault;
        // Common DMAC master-enable / DPCR-ish defaults so DMACMAN/SIFMAN stores stick.
        _iopIo[0x0F0 / 4] = 0x07777777u;
        _iopIo[0x570 / 4] = 0x07777777u;
        _iopIo[0x578 / 4] = 1u;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIopIoPort(uint rawPhys) =>
        rawPhys >= IOP_IO_BASE && rawPhys < IOP_IO_BASE + IOP_IO_SIZE;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadIopIo32(uint rawPhys)
    {
        uint off = rawPhys - IOP_IO_BASE;
        return _iopIo[off / 4];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteIopIo32(uint rawPhys, uint value)
    {
        uint off = rawPhys - IOP_IO_BASE;
        _iopIo[off / 4] = value;
    }

    /// <summary>Real IOP-side SIF mailbox window (ps2tek: IOP sees these at 0x1D000000, the EE
    /// sees the SAME shared hardware mailbox at 0x1000F200 via MmioBus/Sif.ReadRegister/
    /// WriteRegister — same register offsets, 0x00=MSCOM/0x10=SMCOM/0x20=MSFLAG/0x30=SMFLAG/
    /// 0x40=STAT). Only needed on the Iop*() accessor family below.</summary>
    public const uint IOP_SIF_BASE = 0x1D000000;
    public const uint IOP_SIF_SIZE = 0x100;

    /// <summary>
    /// PS1/IOP I/O port window (KSEG1 <c>0xBF801000</c> → phys <c>0x1F801000</c>).
    /// DMAC, INTC, timers, SSBUS, etc. Scratchpad of 4 KiB word slots so IRX can
    /// program DPCR/CHCR and probe config bits without open-bus zeros.
    /// </summary>
    public const uint IOP_IO_BASE = 0x1F801000;
    public const uint IOP_IO_SIZE = 0x1000;

    /// <summary>
    /// INTRMANP (PRId ≥ 16 path) reads <c>0xBF801450</c> and requires bit 3 set or it
    /// early-returns without installing handlers. Default power-on value for PS2 IOP.
    /// </summary>
    public const uint IopIoIntrmanConfigDefault = 0x00000008;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong TranslateAddress(ulong virtualAddress) => virtualAddress & 0x1FFFFFFFUL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Read8(ulong vaddr)
    {
        ulong paddr = TranslateAddress(vaddr);

        // Scratchpad uses uncached physical window; also accept kseg-style 0x70000000 before mask loses high bits
        if ((vaddr & 0xFFFFFFFFUL) is >= SPR_BASE and < SPR_BASE + SPR_SIZE)
            return _scratchpad[(vaddr - SPR_BASE) & (SPR_SIZE - 1)];

        if (paddr < (ulong)RDRAM_SIZE)
            return _rdram[paddr];

        if (paddr >= IOP_RAM_BASE && paddr < IOP_RAM_BASE + (ulong)IOP_RAM_SIZE)
            return _iopRam[paddr - IOP_RAM_BASE];

        if (paddr >= BIOS_BASE && paddr < BIOS_BASE + (ulong)BIOS_SIZE)
            return _bios[paddr - BIOS_BASE];

        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write8(ulong vaddr, byte value)
    {
        if (WatchAddr.HasValue && (vaddr & 0xFFFFFFFFUL & ~3UL) == WatchAddr.Value)
            WatchHits.Add((CurrentPcForWatch, vaddr, value, true));
        ulong paddr = TranslateAddress(vaddr);

        if ((vaddr & 0xFFFFFFFFUL) is >= SPR_BASE and < SPR_BASE + SPR_SIZE)
        {
            _scratchpad[(vaddr - SPR_BASE) & (SPR_SIZE - 1)] = value;
            if (TrackLastWriter) NoteLastWriterByteRegion(_scratchpad, (vaddr - SPR_BASE) & (SPR_SIZE - 1), (uint)SPR_BASE);
            return;
        }

        if (paddr < (ulong)RDRAM_SIZE)
        {
            if (ProtectKernelVectors && paddr < 0x300)
                return; // preserve exception vectors
            _rdram[paddr] = value;
            if (TrackLastWriter) NoteLastWriterByteRegion(_rdram, paddr, 0);
            return;
        }

        if (paddr >= IOP_RAM_BASE && paddr < IOP_RAM_BASE + (ulong)IOP_RAM_SIZE)
        {
            _iopRam[paddr - IOP_RAM_BASE] = value;
            if (TrackLastWriter) NoteLastWriterByteRegion(_iopRam, paddr - IOP_RAM_BASE, IOP_RAM_BASE);
            return;
        }

        // BIOS ROM — ignore writes
    }

    /// <summary>Reconstructs the containing word from raw bytes of the given region (no watch
    /// side effects, unlike Read32) so SB/SH writes update LastWriterLog with an accurate current
    /// value instead of being invisible to it — Write32's NoteLastWriter alone missed any field a
    /// compiler stored via SH/SB (see DEVELOPER_GUIDE.md §7.4, the cyc≈97.66M lead: this exact gap
    /// first showed up as a false "never written" reading for a 16-bit field actually set via SH).
    /// Covers RDRAM, scratchpad, and IOP RAM — whichever region's backing array is passed in —
    /// so byte/halfword stores to any of them are visible to --find-writer, not just RDRAM.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NoteLastWriterByteRegion(byte[] region, ulong regionOffset, uint keyBase)
    {
        ulong wordBase = regionOffset & ~3UL;
        if (wordBase + 3 >= (ulong)region.Length) return;
        uint word = (uint)(region[wordBase] | (region[wordBase + 1] << 8) | (region[wordBase + 2] << 16) | (region[wordBase + 3] << 24));
        LastWriterLog[keyBase + (uint)wordBase] = (CurrentCycleForWriterLog, CurrentPcForWatch, word);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsScratchpad(ulong vaddr)
    {
        // Must use the untranslated address — 0x70000000 & 0x1FFFFFFF aliases MMIO.
        ulong a = vaddr & 0xFFFFFFFFUL;
        return a >= SPR_BASE && a < SPR_BASE + (ulong)SPR_SIZE;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read32(ulong vaddr)
    {
        if (WatchAddr.HasValue && (vaddr & 0xFFFFFFFFUL) == WatchAddr.Value)
            WatchHits.Add((CurrentPcForWatch, vaddr, 0, false));
        if (IsScratchpad(vaddr))
        {
            int off = (int)((vaddr - SPR_BASE) & (SPR_SIZE - 1));
            if (off + 3 < SPR_SIZE)
                return Unsafe.ReadUnaligned<uint>(ref _scratchpad[off]);
        }

        ulong paddr = TranslateAddress(vaddr);

        // SPU2 physical window
        if (paddr >= Spu2.PhysBase && paddr < Spu2.PhysBase + 0x800 && _spu2 != null)
            return _spu2.ReadRegister((uint)paddr);

        // Real EE I/O windows only (64 KB device space + GS privileged). Holes in
        // 0x10010000–0x11FFFFFF (except VU at 0x1100xxxx, still hole here) often appear
        // as game pointers with bit28 set (e.g. Burnout 3 SQ to 0x108D5Cxx for DMA tags);
        // alias those to RDRAM so CPU stores and DMAC reads share the same bytes.
        if (IsRealEeDeviceWindow(paddr) && _mmio != null)
            return _mmio.Read32((uint)paddr);
        if (TryRdramAliasFromMmioHole(paddr, out uint aliasRd) && aliasRd + 3 < (uint)RDRAM_SIZE)
            return Unsafe.ReadUnaligned<uint>(ref _rdram[aliasRd]);
        // Legacy full-window MMIO decode for anything else still in 0x10–0x1B (e.g. rare probes)
        if (paddr >= MMIO_BASE && paddr <= MMIO_END && _mmio != null)
            return _mmio.Read32((uint)paddr);

        if (paddr + 3 < (ulong)RDRAM_SIZE)
            return Unsafe.ReadUnaligned<uint>(ref _rdram[paddr]);

        if (paddr >= IOP_RAM_BASE && paddr + 3 < IOP_RAM_BASE + (ulong)IOP_RAM_SIZE)
            return Unsafe.ReadUnaligned<uint>(ref _iopRam[paddr - IOP_RAM_BASE]);

        if (paddr >= BIOS_BASE && paddr + 3 < BIOS_BASE + (ulong)BIOS_SIZE)
            return Unsafe.ReadUnaligned<uint>(ref _bios[paddr - BIOS_BASE]);

        return (uint)(Read8(vaddr) | (Read8(vaddr + 1) << 8) | (Read8(vaddr + 2) << 16) | (Read8(vaddr + 3) << 24));
    }

    /// <summary>
    /// IOP-side memory accessors (Iop.cs's own bus — NOT the EE's Read8/Read32/Write8/Write32
    /// above). On real hardware the EE and IOP are separate CPUs on separate physical buses:
    /// an IOP address like 0x00001000 refers to a byte in the IOP's own 2MB RAM chip, completely
    /// unrelated to the EE's identically-numbered RDRAM address. This emulator gives the IOP its
    /// own backing array (<see cref="_iopRam"/>) but, until 2026-07-26, Iop.cs's own load/store
    /// helpers called straight into the EE's Read8/Read32/Write8/Write32 above with the IOP's raw
    /// (untranslated, unmasked) address — which resolve low addresses to `_rdram` (checked before
    /// the IOP_RAM_BASE-offset branch even exists in that path), not `_iopRam`. Confirmed via
    /// --dump: with a real PS2 BIOS loaded and the IOP core actually stepping, Iop.PC settled at
    /// a stable address whose disassembly was genuine EE R5900/MMI opcodes (`padduw`, `sq`, 64-bit
    /// `sd`/`ld`) — instructions that don't exist on the IOP's 32-bit R3000A — meaning the "IOP"
    /// was silently misinterpreting the EE's own compiled game binary as if it were IOP firmware,
    /// not executing anything IOP-side at all. These accessors give the IOP a correctly isolated
    /// view: its own RAM at IOP-physical 0x00000000-0x001FFFFF (mapped to the SAME `_iopRam`
    /// array the EE side reaches via IOP_RAM_BASE, just without that offset — this is the same
    /// physical chip, two different numbering schemes depending which CPU's bus is asking), the
    /// shared BIOS ROM window (physically the same chip both CPUs boot from), the real IOP-side
    /// SIF mailbox window (ps2tek: 0x1D000000, routed to the same <see cref="Sif"/> object the EE
    /// reaches via MmioBus/0x1000F200 — same hardware register, two address windows), and zero /
    /// no-op for anything else this emulator doesn't model on the IOP's own bus (its own DMA
    /// controller, timers, interrupt controller — real IOP kernel firmware needs all of these to
    /// actually boot, which is out of scope here; see Sif.SmFlag's proactive SIFINIT/CMDINIT/
    /// BOOTEND bits for how the EE-visible *effects* of a completed IOP boot are represented
    /// instead of simulating the boot itself).
    /// </summary>
    /// <summary>
    /// Normalize IOP CPU virtual address to a bus/phys form.
    /// KSEG0/KSEG1 (0x8…/0xA…) collapse via <c>&amp; 0x1FFFFFFF</c>.
    /// EE window <see cref="IOP_RAM_BASE"/> (0x1C000000) is aliased to phys 0 — IrxLoader
    /// relocates modules for the EE map, so IRX text contains 0x1C… absolute addresses that
    /// must hit the same IOP RAM chip when executed on the R3000.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint NormalizeIopBusAddr(uint addr)
    {
        uint p = addr & 0x1FFFFFFFu;
        if (p >= IOP_RAM_BASE && p < IOP_RAM_BASE + (uint)IOP_RAM_SIZE)
            return p - IOP_RAM_BASE;
        return p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte IopRead8(uint addr)
    {
        uint paddr = NormalizeIopBusAddr(addr);
        if (paddr < (uint)IOP_RAM_SIZE) return _iopRam[paddr];
        // SIF/CDVD use full phys including high bits before strip of EE window only
        uint raw = addr & 0x1FFFFFFFu;
        if (raw >= IOP_SIF_BASE && raw < IOP_SIF_BASE + IOP_SIF_SIZE && _sif != null)
            return (byte)(_sif.ReadRegister(raw) >> (int)((raw & 3) * 8));
        if (IsIopIoPort(raw))
            return (byte)(ReadIopIo32(raw & ~3u) >> (int)((raw & 3) * 8));
        if (_cdvd != null && Cdvd.IsMmioAddress(raw))
            return _cdvd.ReadMmio8(raw);
        if (raw >= BIOS_BASE && raw < BIOS_BASE + (uint)BIOS_SIZE) return _bios[raw - BIOS_BASE];
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IopWrite8(uint addr, byte value)
    {
        uint paddr = NormalizeIopBusAddr(addr);
        if (paddr < (uint)IOP_RAM_SIZE) { _iopRam[paddr] = value; return; }
        uint raw = addr & 0x1FFFFFFFu;
        if (raw >= IOP_SIF_BASE && raw < IOP_SIF_BASE + IOP_SIF_SIZE && _sif != null)
        {
            _sif.WriteRegister(raw, value);
            return;
        }
        if (IsIopIoPort(raw))
        {
            uint aligned = raw & ~3u;
            int shift = (int)((raw & 3) * 8);
            uint cur = ReadIopIo32(aligned);
            WriteIopIo32(aligned, (cur & ~(0xFFu << shift)) | ((uint)value << shift));
            return;
        }
        if (_cdvd != null && Cdvd.IsMmioAddress(raw))
        {
            _cdvd.WriteMmio8(raw, value);
            return;
        }
        // BIOS ROM / unmapped — ignore writes, matching the EE-side Write8 policy.
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint IopRead32(uint addr)
    {
        uint paddr = NormalizeIopBusAddr(addr);
        if (paddr + 3 < (uint)IOP_RAM_SIZE) return Unsafe.ReadUnaligned<uint>(ref _iopRam[paddr]);
        uint raw = addr & 0x1FFFFFFFu;
        if (raw >= IOP_SIF_BASE && raw < IOP_SIF_BASE + IOP_SIF_SIZE && _sif != null)
            return _sif.ReadRegister(raw);
        if (IsIopIoPort(raw))
            return ReadIopIo32(raw);
        if (_cdvd != null && Cdvd.IsMmioAddress(raw))
        {
            // Byte-lane assemble (CDVD is an 8-bit register window; LB/LBU is the common path).
            return (uint)(_cdvd.ReadMmio8(raw)
                | (_cdvd.ReadMmio8(raw + 1) << 8)
                | (_cdvd.ReadMmio8(raw + 2) << 16)
                | (_cdvd.ReadMmio8(raw + 3) << 24));
        }
        if (raw >= BIOS_BASE && raw + 3 < BIOS_BASE + (uint)BIOS_SIZE)
            return Unsafe.ReadUnaligned<uint>(ref _bios[raw - BIOS_BASE]);
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IopWrite32(uint addr, uint value)
    {
        uint paddr = NormalizeIopBusAddr(addr);
        if (paddr + 3 < (uint)IOP_RAM_SIZE) { Unsafe.WriteUnaligned(ref _iopRam[paddr], value); return; }
        uint raw = addr & 0x1FFFFFFFu;
        if (raw >= IOP_SIF_BASE && raw < IOP_SIF_BASE + IOP_SIF_SIZE && _sif != null)
        {
            _sif.WriteRegister(raw, value);
            return;
        }
        if (IsIopIoPort(raw))
        {
            WriteIopIo32(raw, value);
            return;
        }
        if (_cdvd != null && Cdvd.IsMmioAddress(raw))
        {
            _cdvd.WriteMmio8(raw, (byte)value);
            _cdvd.WriteMmio8(raw + 1, (byte)(value >> 8));
            _cdvd.WriteMmio8(raw + 2, (byte)(value >> 16));
            _cdvd.WriteMmio8(raw + 3, (byte)(value >> 24));
            return;
        }
        // BIOS ROM / unmapped — ignore writes.
    }

    /// <summary>Diagnostic-only write watchpoint (opt-in via blocker-trace --watch=ADDR). Null
    /// when unused so normal Write32/Write8 callers pay no cost; set once per process, so it's
    /// intentionally not thread-safe — this is a single-process CLI diagnostic tool, not runtime
    /// infrastructure.</summary>
    public static uint? WatchAddr;
    public static readonly List<(ulong Pc, ulong Vaddr, uint Value, bool IsWrite)> WatchHits = new();
    public static ulong CurrentPcForWatch;

    /// <summary>Diagnostic-only: when true, every 32-bit-aligned write (any region — RDRAM,
    /// scratchpad, IOP RAM, MMIO, SPU2 registers, all of it) overwrites its slot in
    /// <see cref="LastWriterLog"/> with (cycle, pc, value) — a live "who last touched this
    /// address" index, queryable at any point (typically after the run, once a corrupted value
    /// has been found) without needing to have set --watch on that exact address in advance.
    /// Built specifically because --watch requires knowing the target address before it's
    /// written, which doesn't work for tracing corruption whose destination address is itself
    /// computed at runtime (see DEVELOPER_GUIDE.md §7.4, the cyc≈97.66M lead). Off by default —
    /// a dictionary write per store is not free — opt-in via blocker-trace --track-writers.
    ///
    /// Keyed by *physical* address (post-KSEG-translation), not raw virtual address — the same
    /// byte written via 0x00xxxxxx (KUSEG), 0x80xxxxxx (KSEG0, cached), or 0xA0xxxxxx (KSEG1,
    /// uncached) must land in the same log entry, or a query using a different alias than the
    /// one the write actually used would silently miss it. This was a real bug in the original
    /// version of this tracker (Write32 keyed by raw vaddr, Write8's RDRAM path already keyed by
    /// paddr — the two disagreed), fixed 2026-07-26 while extending logging coverage more
    /// broadly. Scratchpad is the one exception: it's a genuinely separate address window with
    /// no KSEG aliasing on real hardware, so it's keyed by its own fixed virtual window
    /// (0x7000_0000+) directly, which can never collide with a translated physical key (physical
    /// space tops out at 0x1FFF_FFFF).</summary>
    public static bool TrackLastWriter;
    public static readonly Dictionary<uint, (ulong Cycle, ulong Pc, uint Value)> LastWriterLog = new();
    public static ulong CurrentCycleForWriterLog;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NoteLastWriter(uint key, uint value)
    {
        if (!TrackLastWriter) return;
        LastWriterLog[key] = (CurrentCycleForWriterLog, CurrentPcForWatch, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write32(ulong vaddr, uint value)
    {
        if (WatchAddr.HasValue && (vaddr & 0xFFFFFFFFUL) == WatchAddr.Value)
            WatchHits.Add((CurrentPcForWatch, vaddr, value, true));
        if (IsScratchpad(vaddr))
        {
            int off = (int)((vaddr - SPR_BASE) & (SPR_SIZE - 1));
            if (off + 3 < SPR_SIZE)
            {
                Unsafe.WriteUnaligned(ref _scratchpad[off], value);
                if (TrackLastWriter) NoteLastWriter((uint)(vaddr & 0xFFFFFFFCUL), value);
                return;
            }
        }

        ulong paddr = TranslateAddress(vaddr);
        if (TrackLastWriter) NoteLastWriter((uint)(paddr & 0xFFFFFFFCUL), value);

        if (paddr >= Spu2.PhysBase && paddr < Spu2.PhysBase + 0x800 && _spu2 != null)
        {
            _spu2.WriteRegister((uint)paddr, value);
            return;
        }

        if (IsRealEeDeviceWindow(paddr) && _mmio != null)
        {
            _mmio.Write32((uint)paddr, value);
            return;
        }
        if (TryRdramAliasFromMmioHole(paddr, out uint aliasWr) && aliasWr + 3 < (uint)RDRAM_SIZE)
        {
            if (ProtectKernelVectors && aliasWr < 0x300)
                return;
            Unsafe.WriteUnaligned(ref _rdram[aliasWr], value);
            return;
        }
        if (paddr >= MMIO_BASE && paddr <= MMIO_END && _mmio != null)
        {
            _mmio.Write32((uint)paddr, value);
            return;
        }

        if (paddr + 3 < (ulong)RDRAM_SIZE)
        {
            if (ProtectKernelVectors && paddr < 0x300)
                return;
            Unsafe.WriteUnaligned(ref _rdram[paddr], value);
            return;
        }

        if (paddr >= IOP_RAM_BASE && paddr + 3 < IOP_RAM_BASE + (ulong)IOP_RAM_SIZE)
        {
            Unsafe.WriteUnaligned(ref _iopRam[paddr - IOP_RAM_BASE], value);
            return;
        }

        Write8(vaddr, (byte)value);
        Write8(vaddr + 1, (byte)(value >> 8));
        Write8(vaddr + 2, (byte)(value >> 16));
        Write8(vaddr + 3, (byte)(value >> 24));
    }

    public void LoadBinary(ReadOnlySpan<byte> data, ulong vaddr)
    {
        for (int i = 0; i < data.Length; i++)
            Write8(vaddr + (ulong)i, data[i]);
    }

    public void LoadBiosRom(ReadOnlySpan<byte> data)
    {
        int n = Math.Min(data.Length, BIOS_SIZE);
        data.Slice(0, n).CopyTo(_bios);
    }

    /// <summary>Write a 32-bit word into the BIOS ROM window (physical 0x1FC00000+).</summary>
    public void WriteBios32(uint offset, uint value)
    {
        if (offset + 3 >= (uint)BIOS_SIZE) return;
        _bios[offset] = (byte)value;
        _bios[offset + 1] = (byte)(value >> 8);
        _bios[offset + 2] = (byte)(value >> 16);
        _bios[offset + 3] = (byte)(value >> 24);
    }

    public byte[] GetRawData() => (byte[])_rdram.Clone();

    public void SetRawData(byte[] data)
    {
        if (data == null || data.Length != RDRAM_SIZE)
            throw new ArgumentException("Invalid memory data size");
        Buffer.BlockCopy(data, 0, _rdram, 0, data.Length);
    }

    public byte[] GetIopRamCopy() => (byte[])_iopRam.Clone();
    public byte[] GetScratchpadCopy() => (byte[])_scratchpad.Clone();

    public void ClearIopRam()
    {
        Array.Clear(_iopRam);
        ResetIopIoPorts();
    }
    public void ClearScratchpad() => Array.Clear(_scratchpad);

    public SystemMemory()
    {
        ResetIopIoPorts();
    }

    public ReadOnlySpan<byte> GetRDRAMSpan() => _rdram;
    public Span<byte> GetIopRamSpan() => _iopRam;

    /// <summary>
    /// True EE device windows that must hit <see cref="MmioBus"/> (not RDRAM alias).
    /// 0x10000000–0x1000FFFF: timers/IPU/GIF/VIF/DMAC/INTC/SIF/MCH.
    /// 0x12000000–0x12001FFF: GS privileged.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsRealEeDeviceWindow(ulong paddr) =>
        (paddr >= 0x10000000UL && paddr < 0x10010000UL) ||
        (paddr >= 0x12000000UL && paddr < 0x12002000UL);

    /// <summary>
    /// MMIO-window holes (bit28 set pointers used by commercial titles) alias into RDRAM.
    /// Burnout 3 writes GIF DMA tags via SQ to 0x108D5Cxx; DMAC then reads 0x008D5Cxx —
    /// without this, tags live only in MmioBus's unmapped dict and PATH3 chain sees zeros.
    /// Excludes real device windows and the VU mem window (0x11000000–0x1100FFFF).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryRdramAliasFromMmioHole(ulong paddr, out uint rdramOff)
    {
        rdramOff = 0;
        if (paddr < 0x10000000UL || paddr > 0x1BFFFFFFUL)
            return false;
        if (IsRealEeDeviceWindow(paddr))
            return false;
        // VU0/VU1 micro/data mem — not RDRAM (left to MmioBus / future VU window).
        if (paddr >= 0x11000000UL && paddr < 0x11010000UL)
            return false;
        rdramOff = (uint)(paddr & 0x01FFFFFFUL);
        return rdramOff < (uint)RDRAM_SIZE;
    }
}
