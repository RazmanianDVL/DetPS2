"""Disasm Vexx ELF for PL-032p: name-search callers + path-object layout + 0x11C200 thunks."""
import struct
import sys

iso = r"C:/Users/xxraz/Downloads/Vexx(USA).iso"
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
    t, o, v, _, fs, ms = struct.unpack_from("<IIIIII", elf, p)
    if t == 1:
        loads.append((o, v, fs, ms))
        print(f"LOAD off={o:#x} va={v:#x} fsz={fs:#x}")


def read_va(va: int, n: int = 4):
    for po, pv, fs, ms in loads:
        if pv <= va < pv + fs:
            return elf[po + (va - pv) : po + (va - pv) + n]
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
        if fn == 8:
            return f"jr {regs[rs]}"
        if fn == 9:
            return f"jalr {regs[rd]}, {regs[rs]}"
        if fn == 0:
            return f"sll {regs[rd]}, {regs[rt]}, {sh}"
        if fn == 2:
            return f"srl {regs[rd]}, {regs[rt]}, {sh}"
        if fn == 3:
            return f"sra {regs[rd]}, {regs[rt]}, {sh}"
        if fn == 0x21:
            return f"addu {regs[rd]}, {regs[rs]}, {regs[rt]}"
        if fn == 0x23:
            return f"subu {regs[rd]}, {regs[rs]}, {regs[rt]}"
        if fn == 0x24:
            return f"and {regs[rd]}, {regs[rs]}, {regs[rt]}"
        if fn == 0x25:
            if rs == 0:
                return f"move {regs[rd]}, {regs[rt]}"
            if rt == 0:
                return f"move {regs[rd]}, {regs[rs]}"
            return f"or {regs[rd]}, {regs[rs]}, {regs[rt]}"
        if fn == 0x2A:
            return f"slt {regs[rd]}, {regs[rs]}, {regs[rt]}"
        if fn == 0x2B:
            return f"sltu {regs[rd]}, {regs[rs]}, {regs[rt]}"
        if fn == 0x2D:
            return f"daddu {regs[rd]}, {regs[rs]}, {regs[rt]}"
        return f"spec_{fn:02x}"
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
    if op == 0x1F:
        return f"sq {regs[rt]}, {simm}({regs[rs]})"
    if op == 0x1E:
        return f"lq {regs[rt]}, {simm}({regs[rs]})"
    if op == 0x3F:
        return f"sd {regs[rt]}, {simm}({regs[rs]})"
    if op == 0x37:
        return f"ld {regs[rt]}, {simm}({regs[rs]})"
    return f"op_{op:02x} rs={rs} rt={rt} imm={simm}"


def dump(lo, hi, label):
    print(f"=== {label} {lo:08X}-{hi:08X} ===")
    for va in range(lo, hi, 4):
        b = read_va(va, 4)
        if not b:
            continue
        w = struct.unpack_from("<I", b)[0]
        print(f"{va:08X}: {w:08X}  {dis(w, va)}")


# Find function prologues near name-search callers
print("\n--- name-search call sites ---")
for site in [0x2A0448, 0x2A2AA4, 0x2A2BA8, 0x2B0E74]:
    dump(site - 0x40, site + 0x80, f"around jal name-search @{site:X}")

# Thunk family: what offsets
print("\n--- thunk family 0x11C1xx ---")
for va in range(0x11C000, 0x11C400, 4):
    b = read_va(va, 4)
    if not b:
        continue
    w = struct.unpack_from("<I", b)[0]
    if w == 0x8C990028:  # lw t9, 0x28(a0)
        b2 = read_va(va + 4, 4)
        w2 = struct.unpack_from("<I", b2)[0] if b2 else 0
        off = w2 & 0xFFFF
        if off >= 0x8000:
            off -= 0x10000
        print(f"  {va:08X}: lw t9,0x28(a0); lw t9,{off}(t9)  [slot {off//4}]")

# Find stores of name at +0xC on objects near constructors
# Search for sw *, 0xC(reg) near vtable plant sw *, 0x28(reg)
print("\n--- functions that sw to +0xC and +0x28 (name + vtable candidates) ---")
# scan code for pattern: sw rt, 0xC(rs) and nearby sw to 0x28
hits = []
for po, pv, fs, ms in loads:
    chunk = elf[po : po + fs]
    for i in range(0, len(chunk) - 8, 4):
        w = struct.unpack_from("<I", chunk, i)[0]
        op = (w >> 26) & 0x3F
        if op != 0x2B:  # sw
            continue
        simm = w & 0xFFFF
        if simm >= 0x8000:
            simm -= 0x10000
        if simm != 0xC:
            continue
        rs = (w >> 21) & 0x1F
        # look ahead 32 words for sw *,0x28(same rs)
        for j in range(0, 48):
            if i + j * 4 + 4 > len(chunk):
                break
            w2 = struct.unpack_from("<I", chunk, i + j * 4)[0]
            op2 = (w2 >> 26) & 0x3F
            if op2 != 0x2B:
                continue
            simm2 = w2 & 0xFFFF
            if simm2 >= 0x8000:
                simm2 -= 0x10000
            rs2 = (w2 >> 21) & 0x1F
            if simm2 == 0x28 and rs2 == rs:
                va = pv + i
                hits.append(va)
                break

print(f"found {len(hits)} sites with +0xC and +0x28 stores")
for va in hits[:40]:
    # dump surrounding for vtable source
    dump(va - 0x20, va + 0x60, f"name+vt @{va:X}")

# Data refs of large vtables in .rodata — look at known ObjDerivedVtable area and nearby
print("\n--- sample vtable heads at known bases ---")
for base in [0x3F5060, 0x3F5690, 0x3F5000, 0x3F4000, 0x3F6000]:
    # these are BSS/data after LOAD — may not be in file
    b = read_va(base, 16)
    print(f"  {base:08X}: {b.hex() if b else 'MISS (BSS?)'}")

# Find large contiguous pointer tables in ELF that look like vtables (code pointers)
print("\n--- candidate vtables (runs of code ptrs, size>=0x300) ---")
for po, pv, fs, ms in loads:
    chunk = elf[po : po + fs]
    i = 0
    while i < len(chunk) - 0x300:
        # count consecutive code-looking ptrs
        run = 0
        while i + run * 4 + 4 <= len(chunk):
            p = struct.unpack_from("<I", chunk, i + run * 4)[0]
            if 0x00100000 <= p < 0x00450000 and (p & 3) == 0:
                run += 1
            else:
                break
        if run >= 0xB0:  # at least 0x2C0 bytes / 176 slots — covers +0x298
            va = pv + i
            # sample slots at 0x28, 0x298
            s28 = struct.unpack_from("<I", chunk, i + 0x28)[0]
            s298 = struct.unpack_from("<I", chunk, i + 0x298)[0]
            print(f"  vt cand @ {va:08X} run={run} slots (+0x28={s28:08X} +0x298={s298:08X})")
            i += run * 4
        else:
            i += 4

# Find who uses name-search return — look at beq after jal
print("\n--- find constructor that sets name@+0xC for path objects ---")
# Search sw name-like: after strcpy into object
# Also dump 0x224000 area for path-object factory
dump(0x224000, 0x224100, "path factory head")
dump(0x223F00, 0x224000, "before path factory")

# Who jals 0x224000-ish path register?
print("\n--- xrefs to 0x224360 already known; xrefs to 0x224000 entry ---")
# find function at 0x224000
dump(0x223E80, 0x224040, "0x223E80")

sys.stdout.flush()
