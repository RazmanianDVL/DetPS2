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

    private readonly byte[] _rdram = new byte[RDRAM_SIZE];
    private readonly byte[] _scratchpad = new byte[SPR_SIZE];
    private readonly byte[] _iopRam = new byte[IOP_RAM_SIZE];
    private readonly byte[] _bios = new byte[BIOS_SIZE];

    private MmioBus? _mmio;
    private Spu2? _spu2;
    /// <summary>When true, refuse writes to exception vector page (0x0–0x2FF) so memset cannot wipe handlers.</summary>
    public bool ProtectKernelVectors { get; set; }

    public void AttachMmio(MmioBus bus) => _mmio = bus ?? throw new ArgumentNullException(nameof(bus));
    public void AttachSpu2(Spu2 spu2) => _spu2 = spu2;

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
            return;
        }

        if (paddr < (ulong)RDRAM_SIZE)
        {
            if (ProtectKernelVectors && paddr < 0x300)
                return; // preserve exception vectors
            _rdram[paddr] = value;
            return;
        }

        if (paddr >= IOP_RAM_BASE && paddr < IOP_RAM_BASE + (ulong)IOP_RAM_SIZE)
        {
            _iopRam[paddr - IOP_RAM_BASE] = value;
            return;
        }

        // BIOS ROM — ignore writes
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

        // MMIO window (EE hardware regs) — after SPR so 0x7000_0000 is not stolen
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

    /// <summary>Diagnostic-only write watchpoint (opt-in via blocker-trace --watch=ADDR). Null
    /// when unused so normal Write32/Write8 callers pay no cost; set once per process, so it's
    /// intentionally not thread-safe — this is a single-process CLI diagnostic tool, not runtime
    /// infrastructure.</summary>
    public static uint? WatchAddr;
    public static readonly List<(ulong Pc, ulong Vaddr, uint Value, bool IsWrite)> WatchHits = new();
    public static ulong CurrentPcForWatch;

    /// <summary>Diagnostic-only: when true, every 32-bit-aligned RDRAM write overwrites its slot
    /// in <see cref="LastWriterLog"/> with (cycle, pc, value) — a live "who last touched this
    /// address" index, queryable at any point (typically after the run, once a corrupted value
    /// has been found) without needing to have set --watch on that exact address in advance.
    /// Built specifically because --watch requires knowing the target address before it's
    /// written, which doesn't work for tracing corruption whose destination address is itself
    /// computed at runtime (see DEVELOPER_GUIDE.md §7.4, the cyc≈97.66M lead). Off by default —
    /// a dictionary write per store is not free — opt-in via blocker-trace --track-writers.</summary>
    public static bool TrackLastWriter;
    public static readonly Dictionary<uint, (ulong Cycle, ulong Pc, uint Value)> LastWriterLog = new();
    public static ulong CurrentCycleForWriterLog;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NoteLastWriter(ulong vaddr, uint value)
    {
        if (!TrackLastWriter) return;
        uint key = (uint)(vaddr & 0xFFFFFFFCUL);
        LastWriterLog[key] = (CurrentCycleForWriterLog, CurrentPcForWatch, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write32(ulong vaddr, uint value)
    {
        if (WatchAddr.HasValue && (vaddr & 0xFFFFFFFFUL) == WatchAddr.Value)
            WatchHits.Add((CurrentPcForWatch, vaddr, value, true));
        NoteLastWriter(vaddr, value);
        if (IsScratchpad(vaddr))
        {
            int off = (int)((vaddr - SPR_BASE) & (SPR_SIZE - 1));
            if (off + 3 < SPR_SIZE)
            {
                Unsafe.WriteUnaligned(ref _scratchpad[off], value);
                return;
            }
        }

        ulong paddr = TranslateAddress(vaddr);

        if (paddr >= Spu2.PhysBase && paddr < Spu2.PhysBase + 0x800 && _spu2 != null)
        {
            _spu2.WriteRegister((uint)paddr, value);
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

    public void ClearIopRam() => Array.Clear(_iopRam);
    public void ClearScratchpad() => Array.Clear(_scratchpad);

    public ReadOnlySpan<byte> GetRDRAMSpan() => _rdram;
    public Span<byte> GetIopRamSpan() => _iopRam;
}
