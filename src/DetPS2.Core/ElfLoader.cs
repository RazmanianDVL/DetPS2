using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// PS2 ELF loader (Phase 9): PT_LOAD, BSS clear, entry + optional GP from .reginfo / section heuristics.
/// </summary>
public static class ElfLoader
{
    public sealed class LoadResult
    {
        public ulong Entry { get; init; }
        public ulong Gp { get; init; }
        public int SegmentsLoaded { get; init; }
        public uint Machine { get; init; }
        public uint Flags { get; init; }
        public bool IsMips { get; init; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Elf32_Ehdr
    {
        public uint e_ident_0;
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
    private const uint PT_MIPS_REGINFO = 0x70000000;
    private const uint ELF_MAGIC = 0x464C457F;
    private const ushort EM_MIPS = 8;

    public static LoadResult LoadElfDetailed(byte[] elfData, SystemMemory memory)
    {
        if (elfData.Length < Marshal.SizeOf<Elf32_Ehdr>())
            throw new ArgumentException("File too small to be a valid ELF");

        Elf32_Ehdr ehdr = ReadStruct<Elf32_Ehdr>(elfData, 0);
        if (ehdr.e_ident_0 != ELF_MAGIC)
            throw new InvalidDataException("Not a valid ELF file (magic mismatch)");

        bool isMips = ehdr.e_machine == EM_MIPS;
        if (!isMips)
            Console.WriteLine($"[ElfLoader] Warning: machine={ehdr.e_machine} (expected MIPS=8)");

        ulong entry = ehdr.e_entry;
        ulong gp = 0;
        int segments = 0;

        int phoff = (int)ehdr.e_phoff;
        int phentsize = ehdr.e_phentsize;
        int phnum = ehdr.e_phnum;

        for (int i = 0; i < phnum; i++)
        {
            int offset = phoff + i * phentsize;
            if (offset + phentsize > elfData.Length) continue;

            Elf32_Phdr phdr = ReadStruct<Elf32_Phdr>(elfData, offset);

            if (phdr.p_type == PT_MIPS_REGINFO && phdr.p_filesz >= 24 && phdr.p_offset + 24 <= (uint)elfData.Length)
            {
                // ri_gp_value at offset 20 in Elf32_RegInfo
                gp = BitConverter.ToUInt32(elfData, (int)phdr.p_offset + 20);
            }

            if (phdr.p_type != PT_LOAD) continue;

            ulong loadAddr = phdr.p_vaddr;
            uint fileSz = phdr.p_filesz;
            uint memSz = phdr.p_memsz;

            if (fileSz > 0)
            {
                int fileOffset = (int)phdr.p_offset;
                int copy = (int)Math.Min(fileSz, memSz);
                if (fileOffset + copy > elfData.Length)
                    copy = Math.Max(0, elfData.Length - fileOffset);
                if (copy > 0)
                {
                    memory.LoadBinary(elfData.AsSpan(fileOffset, copy), loadAddr);
                    segments++;
                    Console.WriteLine($"[ElfLoader] PT_LOAD @ 0x{loadAddr:X8} file=0x{copy:X} mem=0x{memSz:X}");
                }
            }

            // BSS: zero [filesz, memsz)
            if (memSz > fileSz)
            {
                ulong bssStart = loadAddr + fileSz;
                uint bssLen = memSz - fileSz;
                for (uint b = 0; b < bssLen; b++)
                    memory.Write8(bssStart + b, 0);
            }
        }

        // Fallback GP: common homebrew _gp near end of first writable segment — leave 0 if unknown
        Console.WriteLine($"[ElfLoader] Entry=0x{entry:X8} GP=0x{gp:X8} segments={segments} flags=0x{ehdr.e_flags:X}");

        return new LoadResult
        {
            Entry = entry,
            Gp = gp,
            SegmentsLoaded = segments,
            Machine = ehdr.e_machine,
            Flags = ehdr.e_flags,
            IsMips = isMips
        };
    }

    public static ulong LoadElf(byte[] elfData, SystemMemory memory) =>
        LoadElfDetailed(elfData, memory).Entry;

    public static ulong LoadElf(string filePath, SystemMemory memory) =>
        LoadElf(File.ReadAllBytes(filePath), memory);

    /// <summary>Load ELF and apply entry/GP to Emotion Engine.</summary>
    public static LoadResult LoadIntoEe(byte[] elfData, Ps2System system)
    {
        var result = LoadElfDetailed(elfData, system.Memory);
        system.EE.PC = result.Entry;
        if (result.Gp != 0)
            system.EE.SetGpr(28, new EmotionEngine.Gpr128 { Lo = result.Gp }); // $gp
        // Stack pointer default for homebrew
        if (system.EE.GetGpr(29).Lo == 0)
            system.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x02000000 });
        return result;
    }

    /// <summary>
    /// Build a minimal 32-bit MIPS ELF containing a raw code blob at loadVaddr.
    /// Used for homebrew / tests without an external toolchain.
    /// </summary>
    public static byte[] BuildMinimalElf(uint loadVaddr, ReadOnlySpan<byte> code, uint entryOffset = 0)
    {
        uint entry = loadVaddr + entryOffset;
        // Layout: ehdr (52) + phdr (32) + code
        int ehdrSize = 52;
        int phdrSize = 32;
        int codeOff = ehdrSize + phdrSize;
        byte[] elf = new byte[codeOff + code.Length];

        // e_ident
        elf[0] = 0x7F; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
        elf[4] = 1; // ELFCLASS32
        elf[5] = 1; // ELFDATA2LSB
        elf[6] = 1; // EV_CURRENT

        Write16(elf, 16, 2); // ET_EXEC
        Write16(elf, 18, EM_MIPS);
        Write32(elf, 20, 1); // version
        Write32(elf, 24, entry);
        Write32(elf, 28, (uint)ehdrSize); // phoff
        Write32(elf, 32, 0); // shoff
        Write32(elf, 36, 0x20924001); // e_flags typical PS2
        Write16(elf, 40, (ushort)ehdrSize);
        Write16(elf, 42, (ushort)phdrSize);
        Write16(elf, 44, 1); // phnum
        Write16(elf, 46, 0);
        Write16(elf, 48, 0);
        Write16(elf, 50, 0);

        // phdr PT_LOAD
        Write32(elf, ehdrSize + 0, PT_LOAD);
        Write32(elf, ehdrSize + 4, (uint)codeOff);
        Write32(elf, ehdrSize + 8, loadVaddr);
        Write32(elf, ehdrSize + 12, loadVaddr);
        Write32(elf, ehdrSize + 16, (uint)code.Length);
        Write32(elf, ehdrSize + 20, (uint)code.Length);
        Write32(elf, ehdrSize + 24, 5); // RX
        Write32(elf, ehdrSize + 28, 0x1000);

        code.CopyTo(elf.AsSpan(codeOff));
        return elf;
    }

    /// <summary>Assemble tiny homebrew: clear GS + draw sprite/tri + exit via HLE syscalls.</summary>
    public static byte[] BuildHomebrewGsDemoElf(uint loadVaddr = 0x00100000)
    {
        var code = new List<byte>(128);
        void W(uint op)
        {
            code.Add((byte)op);
            code.Add((byte)(op >> 8));
            code.Add((byte)(op >> 16));
            code.Add((byte)(op >> 24));
        }

        // a0 = dark blue color 0xFF101020; v1 = SysGsClear; syscall
        W(Lui(4, 0xFF10));
        W(Ori(4, 4, 0x1020));
        W(Ori(3, 0, (ushort)BiosHle.SysGsClear));
        W(Syscall());

        // sprite: a0=0x00500064 (x=100,y=80), a1=0x008C00DC (w=220,h=140), a2=green
        W(Lui(4, 0x0050));
        W(Ori(4, 4, 0x0064));
        W(Lui(5, 0x008C));
        W(Ori(5, 5, 0x00DC));
        W(Lui(6, 0xFF00));
        W(Ori(6, 6, 0xFF00));
        W(Ori(3, 0, (ushort)BiosHle.SysGsDrawSprite));
        W(Syscall());

        // triangle a0,a1,a2 packed xy, a3 color
        W(Lui(4, 0x0032)); W(Ori(4, 4, 0x00C8));
        W(Lui(5, 0x0032)); W(Ori(5, 5, 0x01C2));
        W(Lui(6, 0x00FA)); W(Ori(6, 6, 0x0140));
        W(Lui(7, 0xFFFF)); W(Ori(7, 7, 0x0000));
        W(Ori(3, 0, (ushort)BiosHle.SysGsDrawTri));
        W(Syscall());

        // exit 0
        W(Ori(4, 0, 0));
        W(Ori(3, 0, (ushort)BiosHle.SysExit));
        W(Syscall());
        W(0);

        return BuildMinimalElf(loadVaddr, code.ToArray());
    }

    private static uint Lui(uint rt, uint imm) => (0x0Fu << 26) | (rt << 16) | (imm & 0xFFFF);
    private static uint Ori(uint rt, uint rs, uint imm) => (0x0Du << 26) | (rs << 21) | (rt << 16) | (imm & 0xFFFF);
    private static uint Syscall() => 0x0000000C;

    private static T ReadStruct<T>(byte[] data, int offset) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        unsafe
        {
            fixed (byte* p = &data[offset])
                return Marshal.PtrToStructure<T>((IntPtr)p);
        }
    }

    private static void Write16(byte[] b, int o, ushort v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
    }

    private static void Write32(byte[] b, int o, uint v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
    }
}
