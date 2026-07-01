using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DetPS2.Core;

/// <summary>
/// Basic ELF loader for PS2 homebrew.
/// Supports standard 32-bit MIPS ELF files (most homebrew uses this format).
/// This is a Phase 1 component to allow running real compiled code.
/// </summary>
public static class ElfLoader
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Elf32_Ehdr
    {
        public uint e_ident_0;      // Magic + other
        public uint e_ident_4;
        public uint e_ident_8;
        public uint e_ident_12;
        public ushort e_type;
        public ushort e_machine;
        public uint e_version;
        public uint e_entry;
        public uint e_phoff;
        public uint e_shoff;
        public uint e_flags;
        public ushort e_ehsize;
        public ushort e_phentsize;
        public ushort e_phnum;
        public ushort e_shentsize;
        public ushort e_shnum;
        public ushort e_shstrndx;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Elf32_Phdr
    {
        public uint p_type;
        public uint p_offset;
        public uint p_vaddr;
        public uint p_paddr;
        public uint p_filesz;
        public uint p_memsz;
        public uint p_flags;
        public uint p_align;
    }

    private const uint PT_LOAD = 1;
    private const uint ELF_MAGIC = 0x464C457F; // 0x7F 'E' 'L' 'F'

    /// <summary>
    /// Loads a 32-bit MIPS ELF into the provided SystemMemory and returns the entry point.
    /// </summary>
    public static ulong LoadElf(byte[] elfData, SystemMemory memory)
    {
        if (elfData.Length < Marshal.SizeOf<Elf32_Ehdr>())
            throw new ArgumentException("File too small to be a valid ELF");

        // Read ELF header
        Elf32_Ehdr ehdr;
        unsafe
        {
            fixed (byte* ptr = elfData)
            {
                ehdr = Marshal.PtrToStructure<Elf32_Ehdr>((IntPtr)ptr);
            }
        }

        // Validate magic
        if (ehdr.e_ident_0 != ELF_MAGIC)
            throw new InvalidDataException("Not a valid ELF file (magic mismatch)");

        if (ehdr.e_machine != 8) // EM_MIPS
            Console.WriteLine("[ElfLoader] Warning: ELF machine type is not MIPS (continuing anyway)");

        ulong entryPoint = ehdr.e_entry;

        // Read program headers
        int phoff = (int)ehdr.e_phoff;
        int phentsize = ehdr.e_phentsize;
        int phnum = ehdr.e_phnum;

        for (int i = 0; i < phnum; i++)
        {
            int offset = phoff + (i * phentsize);
            if (offset + phentsize > elfData.Length)
                continue;

            Elf32_Phdr phdr;
            unsafe
            {
                fixed (byte* ptr = &elfData[offset])
                {
                    phdr = Marshal.PtrToStructure<Elf32_Phdr>((IntPtr)ptr);
                }
            }

            if (phdr.p_type != PT_LOAD)
                continue;

            if (phdr.p_filesz > 0)
            {
                int fileOffset = (int)phdr.p_offset;
                int memSize = (int)Math.Min(phdr.p_filesz, phdr.p_memsz);

                if (fileOffset + memSize > elfData.Length)
                {
                    Console.WriteLine($"[ElfLoader] Warning: Segment {i} exceeds file size");
                    memSize = elfData.Length - fileOffset;
                }

                // Load into memory at p_vaddr (we use simple KSEG translation in SystemMemory)
                ulong loadAddr = phdr.p_vaddr;
                ReadOnlySpan<byte> segmentData = elfData.AsSpan(fileOffset, memSize);
                memory.LoadBinary(segmentData, loadAddr);

                Console.WriteLine($"[ElfLoader] Loaded segment {i} @ 0x{loadAddr:X8} (size 0x{memSize:X})");
            }

            // Zero out remaining BSS if p_memsz > p_filesz
            if (phdr.p_memsz > phdr.p_filesz)
            {
                // For simplicity we rely on memory being zeroed on startup for now.
                // A more complete loader would explicitly zero the extra bytes.
            }
        }

        Console.WriteLine($"[ElfLoader] ELF loaded successfully. Entry point: 0x{entryPoint:X8}");
        return entryPoint;
    }

    /// <summary>
    /// Convenience overload that loads from a file path.
    /// </summary>
    public static ulong LoadElf(string filePath, SystemMemory memory)
    {
        byte[] data = File.ReadAllBytes(filePath);
        return LoadElf(data, memory);
    }
}
