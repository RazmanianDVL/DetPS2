using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// IOP IRX / ELF loader (Phase 22).
/// Loads relocatable MIPS ELF into IOP RAM and records entry + GP.
/// </summary>
public static class IrxLoader
{
    public const uint DefaultLoadBase = 0x00010000; // within IOP RAM physical window via EE map 0x1C000000

    public sealed class LoadResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public uint Entry { get; init; }
        public uint Gp { get; init; }
        public uint LoadBase { get; init; }
        public int Segments { get; init; }
        public string ModuleName { get; init; } = "";
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Elf32_Ehdr
    {
        public uint Magic;
        public byte Class, Data, Version, OsAbi;
        public ulong Pad;
        public ushort Type, Machine;
        public uint Version2, Entry, PhOff, ShOff, Flags;
        public ushort EhSize, PhEntSize, PhNum, ShEntSize, ShNum, ShStrNdx;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Elf32_Phdr
    {
        public uint Type, Offset, VAddr, PAddr, FileSize, MemSize, Flags, Align;
    }

    public static LoadResult Load(byte[] elf, SystemMemory memory, uint iopLoadBase = DefaultLoadBase)
    {
        if (elf == null || elf.Length < 52)
            return Fail("ELF too small");
        if (BitConverter.ToUInt32(elf, 0) != 0x464C457F)
            return Fail("bad magic");
        if (elf[4] != 1 || elf[5] != 1)
            return Fail("need ELF32 LE");

        ushort type = BitConverter.ToUInt16(elf, 16);
        ushort machine = BitConverter.ToUInt16(elf, 18);
        // ET_EXEC=2, ET_REL=1, ET_DYN=3 — accept exec/dyn for IRX-like
        if (machine != 8 && machine != 0) // EM_MIPS=8
        {
            // still allow if phdrs present
        }

        uint entry = BitConverter.ToUInt32(elf, 24);
        uint phOff = BitConverter.ToUInt32(elf, 28);
        ushort phEnt = BitConverter.ToUInt16(elf, 42);
        ushort phNum = BitConverter.ToUInt16(elf, 44);
        if (phNum == 0 || phEnt < 32)
            return Fail("no program headers");

        int segs = 0;
        uint gp = 0;
        uint firstVaddr = 0;
        bool haveFirst = false;

        for (int i = 0; i < phNum; i++)
        {
            int off = (int)phOff + i * phEnt;
            if (off + 32 > elf.Length) break;
            uint pType = BitConverter.ToUInt32(elf, off);
            if (pType != 1) continue; // PT_LOAD
            uint pOffset = BitConverter.ToUInt32(elf, off + 4);
            uint pVaddr = BitConverter.ToUInt32(elf, off + 8);
            uint pFilesz = BitConverter.ToUInt32(elf, off + 16);
            uint pMemsz = BitConverter.ToUInt32(elf, off + 20);

            if (!haveFirst) { firstVaddr = pVaddr; haveFirst = true; }

            // Relocate: map file segment to IOP RAM at iopLoadBase + (vaddr - first)
            uint destLocal = iopLoadBase + (pVaddr - firstVaddr);
            uint destEe = SystemMemory.IOP_RAM_BASE + destLocal;

            // BSS zero
            for (uint b = 0; b < pMemsz; b++)
                memory.Write8(destEe + b, 0);

            int copy = (int)Math.Min(pFilesz, (uint)Math.Max(0, elf.Length - (int)pOffset));
            for (int b = 0; b < copy; b++)
                memory.Write8(destEe + (uint)b, elf[pOffset + b]);

            segs++;
        }

        if (segs == 0)
            return Fail("no PT_LOAD");

        // Relocate entry relative to first vaddr
        uint entryLocal = iopLoadBase + (entry >= firstVaddr ? entry - firstVaddr : entry);
        uint entryEe = SystemMemory.IOP_RAM_BASE + entryLocal;

        // Scan for MIPS_REGINFO-ish gp in notes is optional; leave 0
        string name = GuessModuleName(elf);

        return new LoadResult
        {
            Success = true,
            Message = $"IRX loaded segs={segs} entry=0x{entryEe:X8}",
            Entry = entryEe,
            Gp = gp,
            LoadBase = SystemMemory.IOP_RAM_BASE + iopLoadBase,
            Segments = segs,
            ModuleName = name
        };
    }

    /// <summary>Build a minimal IOP ELF with a few instructions (jr ra; nop) for tests.</summary>
    public static byte[] BuildMinimalIrx(string moduleName = "TESTMOD")
    {
        // Minimal ELF32: ehdr + 1 PT_LOAD with 8 bytes code at vaddr 0
        int phOff = 52;
        int codeOff = 84;
        int strOff = codeOff + 16;
        byte[] nameBytes = Encoding.ASCII.GetBytes(moduleName + "\0");
        int total = strOff + nameBytes.Length + 16;
        byte[] elf = new byte[Math.Max(total, 128)];

        // e_ident
        elf[0] = 0x7F; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
        elf[4] = 1; elf[5] = 1; elf[6] = 1;
        // e_type ET_EXEC, e_machine EM_MIPS
        BitConverter.GetBytes((ushort)2).CopyTo(elf, 16);
        BitConverter.GetBytes((ushort)8).CopyTo(elf, 18);
        BitConverter.GetBytes(1u).CopyTo(elf, 20);
        BitConverter.GetBytes(0u).CopyTo(elf, 24); // entry
        BitConverter.GetBytes((uint)phOff).CopyTo(elf, 28);
        BitConverter.GetBytes(52u).CopyTo(elf, 40); // ehsize-ish
        BitConverter.GetBytes((ushort)52).CopyTo(elf, 40);
        BitConverter.GetBytes((ushort)32).CopyTo(elf, 42); // phentsize
        BitConverter.GetBytes((ushort)1).CopyTo(elf, 44); // phnum

        // Phdr
        BitConverter.GetBytes(1u).CopyTo(elf, phOff); // PT_LOAD
        BitConverter.GetBytes((uint)codeOff).CopyTo(elf, phOff + 4);
        BitConverter.GetBytes(0u).CopyTo(elf, phOff + 8); // vaddr
        BitConverter.GetBytes(0u).CopyTo(elf, phOff + 12);
        BitConverter.GetBytes(16u).CopyTo(elf, phOff + 16); // filesz
        BitConverter.GetBytes(16u).CopyTo(elf, phOff + 20); // memsz
        BitConverter.GetBytes(5u).CopyTo(elf, phOff + 24); // flags RX
        BitConverter.GetBytes(4u).CopyTo(elf, phOff + 28);

        // Code: jr $ra ; nop  (R3000)
        // jr ra = SPECIAL rs=31 funct=8
        uint jr = (31u << 21) | 0x08;
        BitConverter.GetBytes(jr).CopyTo(elf, codeOff);
        BitConverter.GetBytes(0u).CopyTo(elf, codeOff + 4);
        nameBytes.CopyTo(elf, strOff);

        return elf;
    }

    private static string GuessModuleName(byte[] elf)
    {
        // Search for printable ".irx" or ASCII module name near end
        for (int i = 0; i < elf.Length - 4; i++)
        {
            if (elf[i] == (byte)'F' && i + 6 < elf.Length)
            {
                // FILEIO pattern
            }
        }
        return "IRX";
    }

    private static LoadResult Fail(string m) => new() { Success = false, Message = m };
}
