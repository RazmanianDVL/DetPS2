using System;
using System.Collections.Generic;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// IOP IRX / ELF loader — real MIPS ELF-REL relocation processing.
///
/// Real Sony IRX files are NOT plain ET_EXEC binaries with fixed load addresses — ground-truthed
/// this by extracting and byte-decoding real modules from a real BIOS dump and a real game disc
/// (Shaolin Monks' IOP/CDVDSTM.IRX and IOP/PADMAN.IRX, 2026-07-28): e_type is a Sony-specific
/// relocatable type (observed 0xFF80, not standard ET_EXEC=2), every section has sh_addr==0 (i.e.
/// addresses are module-relative, not absolute), and there are substantial .rel.text/.rel.data
/// sections (real modules can be 40%+ relocation entries by size) using STANDARD MIPS o32 ABI
/// relocation types: R_MIPS_26 (type 4, J/JAL targets), R_MIPS_HI16 (type 5, upper half of a
/// split 32-bit address), R_MIPS_LO16 (type 6, lower half). The previous version of this loader
/// only ever copied PT_LOAD segment bytes verbatim with zero relocation processing — every
/// address-bearing instruction in a real module would have been wrong the moment it was actually
/// executed (this was never caught because nothing ever executed loaded IOP module code — see
/// DEVELOPER_GUIDE.md's IRX execution write-up).
///
/// Every real relocation entry observed had r_sym==0 (the reserved null symbol) — NOT because
/// nothing needs relocating, but because Sony's toolchain uses symbol index 0 uniformly for
/// "this address is module-relative; add wherever the loader placed the module" (S=0 in the
/// standard ELF relocation formula, with the module's own load delta supplied by the loader
/// itself rather than looked up via .symtab). Real cross-module imports (calling into another
/// module's exported function) are a completely separate mechanism — a proprietary Sony stub
/// table pattern, not classic ELF undefined-symbol relocation — and are not handled here; see
/// the IRX execution plan in DEVELOPER_GUIDE.md for that follow-on work.
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
        public ushort VersionMajor { get; init; }
        public ushort VersionMinor { get; init; }
    }

    // Confirmed live (2026-07-28) against a real disc IRX (IOP/CDVDSTM.IRX): the real value is
    // 0x70000080, not the 0x70000000 first guessed here — verified by decoding the section
    // header's own type field byte-for-byte rather than trusting the guess.
    private const uint SHT_MIPS_IOPMOD = 0x70000080;
    private const uint SHT_REL = 9;
    private const uint SHF_ALLOC = 2;
    private const uint SHT_NOBITS = 8; // .bss/.sbss

    private sealed class Section
    {
        public string Name = "";
        public uint Type;
        public uint Flags;
        public uint Addr;
        public uint Offset;
        public uint Size;
        public uint Link;
        public uint EntSize;
    }

    public static LoadResult Load(byte[] elf, SystemMemory memory, uint iopLoadBase = DefaultLoadBase)
    {
        if (elf == null || elf.Length < 52)
            return Fail("ELF too small");
        if (BitConverter.ToUInt32(elf, 0) != 0x464C457F)
            return Fail("bad magic");
        if (elf[4] != 1 || elf[5] != 1)
            return Fail("need ELF32 LE");

        uint entry = BitConverter.ToUInt32(elf, 24);
        uint shOff = BitConverter.ToUInt32(elf, 32);
        ushort shEntSize = BitConverter.ToUInt16(elf, 46);
        ushort shNum = BitConverter.ToUInt16(elf, 48);
        ushort shStrNdx = BitConverter.ToUInt16(elf, 50);

        // Real IRX files (section-header-based, relocatable) take this path. The legacy
        // synthetic test fixture (BuildMinimalIrx) has no section headers at all and falls
        // through to the plain PT_LOAD copy path below, unchanged from before.
        if (shNum > 0 && shEntSize >= 40 && shOff + (uint)shNum * shEntSize <= elf.Length)
            return LoadWithSections(elf, memory, iopLoadBase, shOff, shEntSize, shNum, shStrNdx, entry);

        return LoadPtLoadOnly(elf, memory, iopLoadBase, entry);
    }

    private static LoadResult LoadWithSections(byte[] elf, SystemMemory memory, uint iopLoadBase,
        uint shOff, ushort shEntSize, ushort shNum, ushort shStrNdx, uint entry)
    {
        var sections = new Section[shNum];
        for (int i = 0; i < shNum; i++)
        {
            uint o = shOff + (uint)i * shEntSize;
            sections[i] = new Section
            {
                Type = BitConverter.ToUInt32(elf, (int)o + 4),
                Flags = BitConverter.ToUInt32(elf, (int)o + 8),
                Addr = BitConverter.ToUInt32(elf, (int)o + 12),
                Offset = BitConverter.ToUInt32(elf, (int)o + 16),
                Size = BitConverter.ToUInt32(elf, (int)o + 20),
                Link = BitConverter.ToUInt32(elf, (int)o + 24),
                EntSize = BitConverter.ToUInt32(elf, (int)o + 36),
            };
        }
        if (shStrNdx < shNum)
        {
            uint strBase = sections[shStrNdx].Offset;
            for (int i = 0; i < shNum; i++)
            {
                uint nameOff = BitConverter.ToUInt32(elf, (int)(shOff + (uint)i * shEntSize));
                sections[i].Name = ReadCStr(elf, (int)(strBase + nameOff));
            }
        }

        // Load every SHF_ALLOC section at moduleBase + sh_addr — real Sony IRX files place
        // .text at addr 0 and everything else (.rodata/.data/.sbss/.bss) contiguously right
        // after it, so sh_addr is already exactly the module-relative placement the real
        // loader (loadcore) would use; PT_LOAD program headers in these files were found live
        // (2026-07-28, PADMAN.IRX) to NOT cover the full section range and are unreliable —
        // sections are the authoritative source for real IRX loading, matching how a
        // relocatable (not directly-executable) ELF is conventionally loaded.
        int segs = 0;
        uint highestEnd = 0;
        foreach (var s in sections)
        {
            if ((s.Flags & SHF_ALLOC) == 0) continue;
            uint destEe = SystemMemory.IOP_RAM_BASE + iopLoadBase + s.Addr;
            if (s.Type == SHT_NOBITS)
            {
                for (uint b = 0; b < s.Size; b++) memory.Write8(destEe + b, 0);
            }
            else
            {
                int copy = (int)Math.Min(s.Size, (uint)Math.Max(0, elf.Length - (int)s.Offset));
                for (int b = 0; b < copy; b++) memory.Write8(destEe + (uint)b, elf[(int)s.Offset + b]);
            }
            segs++;
            highestEnd = Math.Max(highestEnd, s.Addr + s.Size);
        }
        if (segs == 0) return Fail("no allocatable sections");

        // Apply relocations — see this class's own doc comment for why S is always 0 (the
        // loader-supplied base substitutes for a real symbol lookup here).
        foreach (var s in sections)
        {
            if (s.Type != SHT_REL) continue;
            if (s.Link >= sections.Length) continue;
            var target = FindSectionByIndexOrName(sections, s.Name);
            if (target == null) continue;
            ApplyRelRelocations(elf, memory, s, target, iopLoadBase);
        }

        uint entryLocal = iopLoadBase + entry;
        uint entryEe = SystemMemory.IOP_RAM_BASE + entryLocal;

        var (name, gp, verMajor, verMinor) = ParseIopMod(elf, sections, iopLoadBase);

        return new LoadResult
        {
            Success = true,
            Message = $"IRX loaded (sections) segs={segs} entry=0x{entryEe:X8} name={name}",
            Entry = entryEe,
            Gp = gp,
            LoadBase = SystemMemory.IOP_RAM_BASE + iopLoadBase,
            Segments = segs,
            ModuleName = string.IsNullOrEmpty(name) ? "IRX" : name,
            VersionMajor = verMajor,
            VersionMinor = verMinor,
        };
    }

    /// <summary>.rel.text relocates .text, .rel.data relocates .data, etc — real ELF convention
    /// is via sh_info (section index) or, as observed here, name suffix matching (".rel" + target
    /// section name). Match by name suffix since it's unambiguous for these files and avoids
    /// relying on sh_info, which some of these Sony-toolchain files leave as 0.</summary>
    private static Section? FindSectionByIndexOrName(Section[] sections, string relName)
    {
        if (!relName.StartsWith(".rel", StringComparison.Ordinal)) return null;
        string targetName = relName.Substring(4);
        foreach (var s in sections)
            if (s.Name == targetName) return s;
        return null;
    }

    private static void ApplyRelRelocations(byte[] elf, SystemMemory memory, Section relSec, Section targetSec, uint iopLoadBase)
    {
        uint destBase = SystemMemory.IOP_RAM_BASE + iopLoadBase + targetSec.Addr;
        // Two different "base to add" values are needed depending on relocation kind:
        //  - R_MIPS_26 (J/JAL) only ever encodes the low 28 bits of the target — real MIPS
        //    hardware reconstructs the full address at execution time as
        //    (currentPC & 0xF0000000) | (field << 2), taking the top 4 bits from wherever the
        //    jump instruction itself happens to be running, never from the encoded field. So
        //    the addend here must be shifted by the runtime address's own LOW 28 bits only —
        //    adding the full address (including IOP_RAM_BASE's top nibble) would double-count
        //    that nibble once the CPU reconstructs it from its own PC at runtime. Confirmed
        //    live (2026-07-28): using the full address here produced a jal target outside the
        //    loaded module's own IOP RAM window entirely (0x100140C4 instead of the correct
        //    0x1C0140C4) the first time this loader was tested against a real disc IRX
        //    (IOP/CDVDSTM.IRX).
        //  - R_MIPS_HI16/LO16 (lui+addiu pairs) encode a complete, independent 32-bit address
        //    with no implicit PC-relative segment trick, so they need the FULL runtime address.
        uint fullBase = SystemMemory.IOP_RAM_BASE + iopLoadBase;
        uint low28Base = fullBase & 0x0FFFFFFFu;
        int count = (int)(relSec.Size / 8); // Elf32_Rel = 8 bytes: r_offset, r_info
        var pendingHi16 = new List<uint>(); // IOP RAM addresses of unpatched HI16 instructions

        for (int i = 0; i < count; i++)
        {
            uint recOff = relSec.Offset + (uint)i * 8;
            if (recOff + 8 > elf.Length) break;
            uint rOffset = BitConverter.ToUInt32(elf, (int)recOff);
            uint rInfo = BitConverter.ToUInt32(elf, (int)recOff + 4);
            uint rType = rInfo & 0xFF;
            // r_sym (rInfo >> 8) intentionally unused — see class doc comment: real files
            // observed always use the null symbol for these, with the loader's own base
            // substituting for what would otherwise be a symbol-table lookup.

            uint instrAddr = destBase + rOffset;
            uint instr = memory.Read32(instrAddr);

            switch (rType)
            {
                case 4: // R_MIPS_26 — J/JAL 26-bit shifted target (low-28-bit base — see above)
                {
                    uint a = (instr & 0x03FFFFFFu) << 2;
                    uint newTarget = a + low28Base;
                    uint newInstr = (instr & 0xFC000000u) | ((newTarget >> 2) & 0x03FFFFFFu);
                    memory.Write32(instrAddr, newInstr);
                    break;
                }
                case 5: // R_MIPS_HI16 — buffer until the matching LO16 arrives
                    pendingHi16.Add(instrAddr);
                    break;
                case 6: // R_MIPS_LO16 — flush all pending HI16s against this addend
                {
                    short loImm = (short)(instr & 0xFFFF);
                    uint loBase = 0; // AHL for the LO16 itself; HI16 contributes separately per entry
                    foreach (var hiAddr in pendingHi16)
                    {
                        uint hiInstr = memory.Read32(hiAddr);
                        uint ahl = (hiInstr & 0xFFFFu) << 16;
                        uint a = unchecked(ahl + (uint)(int)loImm);
                        uint newAddr = a + fullBase;
                        uint newHi = (newAddr + 0x8000u) >> 16;
                        memory.Write32(hiAddr, (hiInstr & 0xFFFF0000u) | (newHi & 0xFFFFu));
                        loBase = newAddr; // last one wins for the LO16 patch below (matches the
                                           // overwhelmingly common 1:1 HI16/LO16 compiler pattern)
                    }
                    pendingHi16.Clear();
                    if (loBase == 0)
                    {
                        // No pending HI16 (a bare LO16, e.g. small offset off $gp) — addend is
                        // just the LO16's own sign-extended immediate, shifted by the module base.
                        uint newAddr = unchecked((uint)(int)loImm) + fullBase;
                        memory.Write32(instrAddr, (instr & 0xFFFF0000u) | (newAddr & 0xFFFFu));
                    }
                    else
                    {
                        memory.Write32(instrAddr, (instr & 0xFFFF0000u) | (loBase & 0xFFFFu));
                    }
                    break;
                }
                default:
                    // R_MIPS_32 and others exist in the ABI but weren't observed in any real
                    // sample this loader was ground-truthed against; leave unpatched rather than
                    // guess, so a title that needs one fails loudly (wrong code) instead of
                    // silently (a plausible-looking but wrong patch).
                    break;
            }
        }
    }

    /// <summary>Parses the real .iopmod section — confirmed live (2026-07-28) against
    /// IOP/CDVDSTM.IRX: field 1 (offset 4) matched e_entry exactly, and the module name string
    /// ("cdvd_st_driver") sits at a fixed offset after a 2-byte version pair. Layout:
    /// [0] next (u32, filled at real runtime link time — always 0 in the file) [4] entry (u32)
    /// [8] gp (u32) [12] text_size (u32) [16] data_size (u32) [20] bss_size (u32)
    /// [24] version_minor (u8) [25] version_major (u8) [26] name (NUL-terminated ASCII).</summary>
    private static (string name, uint gp, ushort verMajor, ushort verMinor) ParseIopMod(byte[] elf, Section[] sections, uint iopLoadBase)
    {
        foreach (var s in sections)
        {
            if (s.Type != SHT_MIPS_IOPMOD) continue;
            if (s.Offset + 26 > elf.Length) break;
            uint gp = BitConverter.ToUInt32(elf, (int)s.Offset + 8);
            byte verMinor = elf[s.Offset + 24];
            byte verMajor = elf[s.Offset + 25];
            string name = ReadCStr(elf, (int)s.Offset + 26);
            return (name, gp, verMajor, verMinor);
        }
        return ("", 0, 0, 0);
    }

    private static string ReadCStr(byte[] data, int off)
    {
        if (off < 0 || off >= data.Length) return "";
        int end = off;
        while (end < data.Length && data[end] != 0) end++;
        return Encoding.ASCII.GetString(data, off, end - off);
    }

    /// <summary>Legacy path for the synthetic test fixture (BuildMinimalIrx) and any genuine
    /// ET_EXEC-with-PT_LOAD IOP binary with no section headers at all — plain segment copy, no
    /// relocation (none needed/possible without section-level relocation data anyway).</summary>
    private static LoadResult LoadPtLoadOnly(byte[] elf, SystemMemory memory, uint iopLoadBase, uint entry)
    {
        uint phOff = BitConverter.ToUInt32(elf, 28);
        ushort phEnt = BitConverter.ToUInt16(elf, 42);
        ushort phNum = BitConverter.ToUInt16(elf, 44);
        if (phNum == 0 || phEnt < 32)
            return Fail("no program headers");

        int segs = 0;
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

            uint destLocal = iopLoadBase + (pVaddr - firstVaddr);
            uint destEe = SystemMemory.IOP_RAM_BASE + destLocal;

            for (uint b = 0; b < pMemsz; b++)
                memory.Write8(destEe + b, 0);

            int copy = (int)Math.Min(pFilesz, (uint)Math.Max(0, elf.Length - (int)pOffset));
            for (int b = 0; b < copy; b++)
                memory.Write8(destEe + (uint)b, elf[pOffset + b]);

            segs++;
        }

        if (segs == 0)
            return Fail("no PT_LOAD");

        uint entryLocal = iopLoadBase + (entry >= firstVaddr ? entry - firstVaddr : entry);
        uint entryEe = SystemMemory.IOP_RAM_BASE + entryLocal;

        return new LoadResult
        {
            Success = true,
            Message = $"IRX loaded (PT_LOAD) segs={segs} entry=0x{entryEe:X8}",
            Entry = entryEe,
            Gp = 0,
            LoadBase = SystemMemory.IOP_RAM_BASE + iopLoadBase,
            Segments = segs,
            ModuleName = "IRX",
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

    /// <summary>Builds a synthetic, section-header-based IRX exercising the real relocatable
    /// format (not the legacy PT_LOAD-only fixture BuildMinimalIrx produces) — for tests, since a
    /// real retail IRX can't be committed to this repo. Mirrors the real structure ground-truthed
    /// against actual disc files (see this class's own doc comment): .text with one R_MIPS_26
    /// (jal) and one R_MIPS_HI16/R_MIPS_LO16 pair (lui+addiu), a matching .rel.text, and a real
    /// .iopmod section. jalTargetModuleRelative and hiLoTargetModuleRelative are the pre-
    /// relocation (module-base-0) addresses the caller should independently recompute the
    /// expected patched instruction words from, to verify ApplyRelRelocations end to end.</summary>
    public static byte[] BuildRelocatableTestIrx(string moduleName, uint jalTargetModuleRelative, uint hiLoTargetModuleRelative)
    {
        // .text: jal <target>; lui v0,%hi(target2); addiu v0,v0,%lo(target2)
        uint jalField = (jalTargetModuleRelative >> 2) & 0x03FFFFFF;
        uint jalWord = (3u << 26) | jalField; // opcode 3 = J-type JAL
        uint hi = (hiLoTargetModuleRelative + 0x8000u) >> 16;
        ushort lo = (ushort)hiLoTargetModuleRelative;
        uint luiWord = (0xFu << 26) | (2u << 16) | (hi & 0xFFFFu); // lui v0, hi
        uint addiuWord = (0x9u << 26) | (2u << 21) | (2u << 16) | lo; // addiu v0, v0, lo

        byte[] text = new byte[12];
        BitConverter.GetBytes(jalWord).CopyTo(text, 0);
        BitConverter.GetBytes(luiWord).CopyTo(text, 4);
        BitConverter.GetBytes(addiuWord).CopyTo(text, 8);

        byte[] relText = new byte[24];
        void Rel(int idx, uint offset, uint type) // Elf32_Rel: r_offset, r_info(sym<<8|type)
        {
            BitConverter.GetBytes(offset).CopyTo(relText, idx * 8);
            BitConverter.GetBytes(type).CopyTo(relText, idx * 8 + 4); // sym=0
        }
        Rel(0, 0, 4);  // R_MIPS_26 on the jal
        Rel(1, 4, 5);  // R_MIPS_HI16 on the lui
        Rel(2, 8, 6);  // R_MIPS_LO16 on the addiu

        byte[] nameBytes = Encoding.ASCII.GetBytes(moduleName + "\0");
        byte[] iopmod = new byte[26 + nameBytes.Length];
        BitConverter.GetBytes(0u).CopyTo(iopmod, 0);   // next
        BitConverter.GetBytes(0u).CopyTo(iopmod, 4);   // entry (module-relative 0, matches e_entry below)
        BitConverter.GetBytes(0u).CopyTo(iopmod, 8);   // gp (unused by this test)
        BitConverter.GetBytes(0u).CopyTo(iopmod, 12);
        BitConverter.GetBytes(0u).CopyTo(iopmod, 16);
        BitConverter.GetBytes(0u).CopyTo(iopmod, 20);
        iopmod[24] = 1; // version_minor
        iopmod[25] = 0; // version_major
        nameBytes.CopyTo(iopmod, 26);

        string[] shNames = { "", ".text", ".rel.text", ".iopmod", ".shstrtab" };
        var shstrtabBytes = new List<byte>();
        var shNameOffsets = new uint[shNames.Length];
        for (int i = 0; i < shNames.Length; i++)
        {
            shNameOffsets[i] = (uint)shstrtabBytes.Count;
            shstrtabBytes.AddRange(Encoding.ASCII.GetBytes(shNames[i]));
            shstrtabBytes.Add(0);
        }
        byte[] shstrtab = shstrtabBytes.ToArray();

        // Layout file contents sequentially after the 52-byte ELF header.
        int textOff = 52;
        int relTextOff = textOff + text.Length;
        int iopmodOff = relTextOff + relText.Length;
        int shstrtabOff = iopmodOff + iopmod.Length;
        int shOff = shstrtabOff + shstrtab.Length;
        const int shEntSize = 40;
        int totalSize = shOff + shEntSize * shNames.Length;

        byte[] elf = new byte[totalSize];
        elf[0] = 0x7F; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
        elf[4] = 1; elf[5] = 1; elf[6] = 1;
        BitConverter.GetBytes((ushort)0xFF80).CopyTo(elf, 16); // e_type: real Sony IRX value
        BitConverter.GetBytes((ushort)8).CopyTo(elf, 18);      // e_machine: EM_MIPS
        BitConverter.GetBytes(0u).CopyTo(elf, 24);             // e_entry (module-relative 0)
        BitConverter.GetBytes((uint)shOff).CopyTo(elf, 32);    // e_shoff
        BitConverter.GetBytes((ushort)52).CopyTo(elf, 40);     // e_ehsize
        BitConverter.GetBytes((ushort)shEntSize).CopyTo(elf, 46);
        BitConverter.GetBytes((ushort)shNames.Length).CopyTo(elf, 48); // e_shnum
        BitConverter.GetBytes((ushort)4).CopyTo(elf, 50);      // e_shstrndx (.shstrtab is index 4)

        text.CopyTo(elf, textOff);
        relText.CopyTo(elf, relTextOff);
        iopmod.CopyTo(elf, iopmodOff);
        shstrtab.CopyTo(elf, shstrtabOff);

        void WriteShdr(int idx, uint nameOff, uint type, uint flags, uint addr, uint fileOff, uint size)
        {
            int o = shOff + idx * shEntSize;
            BitConverter.GetBytes(nameOff).CopyTo(elf, o);
            BitConverter.GetBytes(type).CopyTo(elf, o + 4);
            BitConverter.GetBytes(flags).CopyTo(elf, o + 8);
            BitConverter.GetBytes(addr).CopyTo(elf, o + 12);
            BitConverter.GetBytes(fileOff).CopyTo(elf, o + 16);
            BitConverter.GetBytes(size).CopyTo(elf, o + 20);
        }
        WriteShdr(0, shNameOffsets[0], 0, 0, 0, 0, 0); // null section
        WriteShdr(1, shNameOffsets[1], 1 /*PROGBITS*/, SHF_ALLOC, 0, (uint)textOff, (uint)text.Length);
        WriteShdr(2, shNameOffsets[2], SHT_REL, 0, 0, (uint)relTextOff, (uint)relText.Length);
        WriteShdr(3, shNameOffsets[3], SHT_MIPS_IOPMOD, 0, 0, (uint)iopmodOff, (uint)iopmod.Length);
        WriteShdr(4, shNameOffsets[4], 3 /*STRTAB*/, 0, 0, (uint)shstrtabOff, (uint)shstrtab.Length);

        return elf;
    }

    private static LoadResult Fail(string m) => new() { Success = false, Message = m };
}
