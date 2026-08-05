"""One-shot disasm of Vexx SLUS around post-PR thrash / residual PCs."""
import struct
import sys

iso = r"C:/Users/user/Downloads/Vexx(USA).iso"
lba = 0x156E32
size = 3432732
with open(iso, "rb") as f:
    f.seek(lba * 2048)
    elf = f.read(size)

e_phoff = struct.unpack_from("<I", elf, 28)[0]
e_phnum = struct.unpack_from("<H", elf, 44)[0]
e_phentsize = struct.unpack_from("<H", elf, 42)[0]
loads = []
for i in range(e_phnum):
    p = e_phoff + i * e_phentsize
    p_type, p_offset, p_vaddr, p_paddr, p_filesz, p_memsz = struct.unpack_from(
        "<IIIIII", elf, p
    )
    if p_type == 1:
        loads.append((p_offset, p_vaddr, p_filesz, p_memsz))
        print(f"LOAD off={p_offset:#x} va={p_vaddr:#x} fsz={p_filesz:#x}")


def read_va(va: int, n: int = 4):
    for po, pv, fs, ms in loads:
        if pv <= va < pv + fs:
            o = po + (va - pv)
            return elf[o : o + n]
    return None


regs = [
    "zero", "at", "v0", "v1", "a0", "a1", "a2", "a3",
    "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7",
    "s0", "s1", "s2", "s3", "s4", "s5", "s6", "s7",
    "t8", "t9", "k0", "k1", "gp", "sp", "fp", "ra",
]


def dis(w: int, pc: int = 0) -> str:
    op = (w >> 26) & 0x3F
    rs = (w >> 21) & 0x1F
    rt = (w >> 16) & 0x1F
    rd = (w >> 11) & 0x1F
    sh = (w >> 6) & 0x1F
    fn = w & 0x3F
    imm = w & 0xFFFF
    simm = imm - 0x10000 if imm >= 0x8000 else imm
    region = pc & 0xF0000000

    if op == 0:
        names = {
            0: "sll", 2: "srl", 3: "sra", 8: "jr", 9: "jalr",
            0x10: "mfhi", 0x12: "mflo", 0x18: "mult", 0x19: "multu",
            0x1A: "div", 0x1B: "divu", 0x20: "add", 0x21: "addu",
            0x23: "subu", 0x24: "and", 0x25: "or", 0x27: "nor",
            0x2A: "slt", 0x2B: "sltu", 0x0C: "syscall",
        }
        n = names.get(fn, f"spec_{fn:02x}")
        if fn == 8:
            return f"{n} {regs[rs]}"
        if fn == 9:
            return f"{n} {regs[rd]}, {regs[rs]}"
        if fn in (0, 2, 3):
            return f"{n} {regs[rd]}, {regs[rt]}, {sh}"
        if fn in (0x10, 0x12):
            return f"{n} {regs[rd]}"
        return f"{n} {regs[rd]}, {regs[rs]}, {regs[rt]}"
    if op == 1:
        if rt == 0:
            return f"bltz {regs[rs]}, {simm}"
        if rt == 1:
            return f"bgez {regs[rs]}, {simm}"
        return f"regimm rt={rt}"
    if op == 2:
        return f"j {((w & 0x3FFFFFF) << 2) | region:08X}"
    if op == 3:
        return f"jal {((w & 0x3FFFFFF) << 2) | region:08X}"
    if op == 4:
        return f"beq {regs[rs]}, {regs[rt]}, {simm}"
    if op == 5:
        return f"bne {regs[rs]}, {regs[rt]}, {simm}"
    if op == 6:
        return f"blez {regs[rs]}, {simm}"
    if op == 7:
        return f"bgtz {regs[rs]}, {simm}"
    if op == 8:
        return f"addi {regs[rt]}, {regs[rs]}, {simm}"
    if op == 9:
        return f"addiu {regs[rt]}, {regs[rs]}, {simm}"
    if op == 0xA:
        return f"slti {regs[rt]}, {regs[rs]}, {simm}"
    if op == 0xB:
        return f"sltiu {regs[rt]}, {regs[rs]}, {simm}"
    if op == 0xC:
        return f"andi {regs[rt]}, {regs[rs]}, {imm:#x}"
    if op == 0xD:
        return f"ori {regs[rt]}, {regs[rs]}, {imm:#x}"
    if op == 0xE:
        return f"xori {regs[rt]}, {regs[rs]}, {imm:#x}"
    if op == 0xF:
        return f"lui {regs[rt]}, {imm:#x}"
    if op == 0x20:
        return f"lb {regs[rt]}, {simm}({regs[rs]})"
    if op == 0x21:
        return f"lh {regs[rt]}, {simm}({regs[rs]})"
    if op == 0x23:
        return f"lw {regs[rt]}, {simm}({regs[rs]})"
    if op == 0x24:
        return f"lbu {regs[rt]}, {simm}({regs[rs]})"
    if op == 0x25:
        return f"lhu {regs[rt]}, {simm}({regs[rs]})"
    if op == 0x28:
        return f"sb {regs[rt]}, {simm}({regs[rs]})"
    if op == 0x29:
        return f"sh {regs[rt]}, {simm}({regs[rs]})"
    if op == 0x2B:
        return f"sw {regs[rt]}, {simm}({regs[rs]})"
    if op == 0x1C:
        return f"mmi fn={fn:02x}"
    if op == 0x1F:
        return f"sq? {regs[rt]}, {simm}({regs[rs]})"  # EE
    if op == 0x1E:
        return f"lq? {regs[rt]}, {simm}({regs[rs]})"
    return f"op{op:02x} rs={rs} rt={rt} imm={simm}"


def dump_range(start: int, end: int, label: str):
    print(f"=== {label} {start:#x}..{end:#x} ===")
    marks = {0x2243A0, 0x2243E8, 0x225004, 0x35B534, 0x35E190, 0x224560, 0x224380}
    for va in range(start, end, 4):
        raw = read_va(va, 4)
        if not raw:
            print(f"{va:08X}: <missing>")
            continue
        w = struct.unpack_from("<I", raw)[0]
        m = " <<" if va in marks else ""
        print(f"{va:08X}: {w:08X}  {dis(w, va)}{m}")


print("--- prologues / jals near thrash ---")
for va in range(0x223F00, 0x225100, 4):
    raw = read_va(va, 4)
    if not raw:
        continue
    w = struct.unpack_from("<I", raw)[0]
    if (w & 0xFFFF0000) == 0x27BD0000:  # addiu sp
        print(f"  addiu sp @ {va:08X}: {dis(w, va)}")
    if (w & 0xFC000000) == 0x0C000000:  # jal
        t = ((w & 0x3FFFFFF) << 2) | (va & 0xF0000000)
        if 0x224000 <= t <= 0x225200 or 0x224000 <= va <= 0x224500:
            print(f"  jal @ {va:08X} -> {t:08X}")
    if w == 0x03E00008:  # jr ra
        d = read_va(va + 4, 4)
        dw = struct.unpack_from("<I", d)[0] if d else 0
        print(f"  jr ra @ {va:08X} delay={dis(dw, va + 4)}")

dump_range(0x2242E0, 0x224480, "thrash body")
dump_range(0x224540, 0x2245C0, "callee ~0x224560")
dump_range(0x224F80, 0x2250A0, "hard-leave area")
dump_range(0x35B4A0, 0x35B5E0, "final residual 0x35B534")

# who calls 0x2243xx?
print("--- callers of 0x2243xx / 0x224000 band (sample PT_LOAD) ---")
targets = set()
for va in range(0x100000, 0x100000 + 0x345F00, 4):
    raw = read_va(va, 4)
    if not raw:
        continue
    w = struct.unpack_from("<I", raw)[0]
    if (w & 0xFC000000) == 0x0C000000:  # jal
        t = ((w & 0x3FFFFFF) << 2) | (va & 0xF0000000)
        if 0x224000 <= t <= 0x225000:
            print(f"  caller {va:08X} jal {t:08X}")
            targets.add(t)
            if len(targets) > 40:
                break
print("unique targets sample", sorted(hex(x) for x in targets)[:30])
