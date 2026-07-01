using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DetPS2.Core;

/// <summary>
/// PS2 system memory with accurate memory map handling.
/// Starts with the most important regions for early emulation:
/// - 32 MB RDRAM (main EE RAM)
/// - Scratchpad RAM (16 KB fast on-chip memory at 0x70000000)
/// Simple KSEG translation (addr & 0x1FFFFFFF) for now — good enough for most homebrew and early boot.
/// </summary>
public sealed class SystemMemory
{
    // 32 MB RDRAM - the main workhorse
    public const int RDRAM_SIZE = 32 * 1024 * 1024;
    private readonly byte[] _rdram = new byte[RDRAM_SIZE];

    // 16 KB Scratchpad RAM (very fast, on EE die)
    public const int SPR_SIZE = 16 * 1024;
    private readonly byte[] _scratchpad = new byte[SPR_SIZE];

    // TODO later: IOP RAM (2 MB), GS VRAM (4 MB eDRAM), BIOS region, hardware registers, etc.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong TranslateAddress(ulong virtualAddress)
    {
        // Simple KUSEG / KSEG0 / KSEG1 translation used by most early emulators and homebrew.
        // This strips the top 3 bits for cached/uncached kernel segments.
        // More accurate TLB/COP0 handling comes much later.
        return virtualAddress & 0x1FFFFFFF;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Read8(ulong vaddr)
    {
        ulong paddr = TranslateAddress(vaddr);

        if (paddr < RDRAM_SIZE)
            return _rdram[paddr];

        if (paddr >= 0x70000000 && paddr < 0x70000000 + SPR_SIZE)
            return _scratchpad[paddr - 0x70000000];

        // TODO: Hardware registers, BIOS, etc. will go here
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write8(ulong vaddr, byte value)
    {
        ulong paddr = TranslateAddress(vaddr);

        if (paddr < RDRAM_SIZE)
        {
            _rdram[paddr] = value;
            return;
        }

        if (paddr >= 0x70000000 && paddr < 0x70000000 + SPR_SIZE)
        {
            _scratchpad[paddr - 0x70000000] = value;
            return;
        }

        // TODO: Handle writes to registers, etc.
    }

    // 32-bit and 64-bit helpers (little-endian, which PS2 uses)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read32(ulong vaddr)
    {
        // For maximum determinism we could do four Read8 calls,
        // but for speed on little-endian hosts we use direct span access.
        ulong paddr = TranslateAddress(vaddr);
        if (paddr + 3 < RDRAM_SIZE)
        {
            return Unsafe.ReadUnaligned<uint>(ref _rdram[paddr]);
        }
        // Fallback for scratchpad / other regions
        return (uint)(Read8(vaddr) | (Read8(vaddr + 1) << 8) | (Read8(vaddr + 2) << 16) | (Read8(vaddr + 3) << 24));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write32(ulong vaddr, uint value)
    {
        ulong paddr = TranslateAddress(vaddr);
        if (paddr + 3 < RDRAM_SIZE)
        {
            Unsafe.WriteUnaligned(ref _rdram[paddr], value);
            return;
        }
        Write8(vaddr, (byte)value);
        Write8(vaddr + 1, (byte)(value >> 8));
        Write8(vaddr + 2, (byte)(value >> 16));
        Write8(vaddr + 3, (byte)(value >> 24));
    }

    /// <summary>
    /// Load a simple ELF or raw binary into RDRAM at the given physical address.
    /// For now this is very basic — we'll improve it when we add proper ELF parsing.
    /// </summary>
    public void LoadBinary(ReadOnlySpan<byte> data, ulong physicalAddress)
    {
        ulong paddr = physicalAddress & 0x1FFFFFFF;
        if (paddr + (ulong)data.Length > RDRAM_SIZE)
            throw new ArgumentException("Binary too large for RDRAM region");

        data.CopyTo(_rdram.AsSpan((int)paddr, data.Length));
    }

    public ReadOnlySpan<byte> GetRDRAMSpan() => _rdram;
}
