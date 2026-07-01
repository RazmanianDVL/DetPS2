using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DetPS2.Core;

/// <summary>
/// System Memory with raw access for save states.
/// </summary>
public sealed class SystemMemory
{
    public const int RDRAM_SIZE = 32 * 1024 * 1024;
    private readonly byte[] _rdram = new byte[RDRAM_SIZE];

    public const int SPR_SIZE = 16 * 1024;
    private readonly byte[] _scratchpad = new byte[SPR_SIZE];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong TranslateAddress(ulong virtualAddress)
    {
        return virtualAddress & 0x1FFFFFFF;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Read8(ulong vaddr)
    {
        ulong paddr = TranslateAddress(vaddr);
        if (paddr < RDRAM_SIZE) return _rdram[paddr];
        if (paddr >= 0x70000000 && paddr < 0x70000000 + SPR_SIZE)
            return _scratchpad[paddr - 0x70000000];
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write8(ulong vaddr, byte value)
    {
        ulong paddr = TranslateAddress(vaddr);
        if (paddr < RDRAM_SIZE) { _rdram[paddr] = value; return; }
        if (paddr >= 0x70000000 && paddr < 0x70000000 + SPR_SIZE)
            _scratchpad[paddr - 0x70000000] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read32(ulong vaddr)
    {
        ulong paddr = TranslateAddress(vaddr);
        if (paddr + 3 < RDRAM_SIZE)
            return Unsafe.ReadUnaligned<uint>(ref _rdram[paddr]);
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
    /// Returns a copy of raw RDRAM for save states.
    /// </summary>
    public byte[] GetRawData() => (byte[])_rdram.Clone();

    /// <summary>
    /// Restores RDRAM from save state data.
    /// </summary>
    public void SetRawData(byte[] data)
    {
        if (data == null || data.Length != RDRAM_SIZE)
            throw new ArgumentException("Invalid memory data size");
        Buffer.BlockCopy(data, 0, _rdram, 0, data.Length);
    }

    public ReadOnlySpan<byte> GetRDRAMSpan() => _rdram;
}
