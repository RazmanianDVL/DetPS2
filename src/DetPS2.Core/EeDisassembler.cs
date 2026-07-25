using System;

namespace DetPS2.Core;

/// <summary>
/// EE (R5900/MIPS III + MMI + COP0/1/2) disassembler for diagnostics. Mnemonic tables here
/// are deliberately kept in lockstep with EmotionEngine.cs's actual decode switches — this
/// exists specifically so real-boot investigation can read mnemonics instead of hand-decoding
/// hex opcodes one bit-field at a time, and so what it prints is guaranteed to match what the
/// interpreter actually does (not a second, independently-fallible opcode table).
/// </summary>
public static class EeDisassembler
{
    private static readonly string[] GprNames =
    {
        "zero","at","v0","v1","a0","a1","a2","a3",
        "t0","t1","t2","t3","t4","t5","t6","t7",
        "s0","s1","s2","s3","s4","s5","s6","s7",
        "t8","t9","k0","k1","gp","sp","fp","ra"
    };

    private static string R(uint n) => GprNames[n & 0x1F];
    private static string F(uint n) => $"f{n & 0x1F}";
    private static string V(uint n) => $"vi{n & 0xF}";

    /// <summary>Disassemble one 32-bit EE instruction at the given PC. Returns a
    /// "mnemonic operands" string; never throws — unrecognized bit patterns produce
    /// "unk 0x........" rather than an exception, since this is a best-effort diagnostic
    /// tool that must tolerate garbage/data-as-code without crashing.</summary>
    public static string Disassemble(uint pc, uint op)
    {
        try
        {
            uint primary = (op >> 26) & 0x3F;
            uint rs = (op >> 21) & 0x1F, rt = (op >> 16) & 0x1F, rd = (op >> 11) & 0x1F;
            uint sa = (op >> 6) & 0x1F, func = op & 0x3F;
            short imm = (short)(op & 0xFFFF);
            uint uimm = op & 0xFFFF;

            string Br(string mn, uint a, uint b) => $"{mn} {R(a)}, {R(b)}, 0x{pc + 4 + (uint)(imm << 2):X8}";
            string Br1(string mn, uint a) => $"{mn} {R(a)}, 0x{pc + 4 + (uint)(imm << 2):X8}";
            string Mem(string mn, uint baseReg, uint tReg) => $"{mn} {R(tReg)}, {imm}({R(baseReg)})";
            string MemF(string mn, uint baseReg, uint tReg) => $"{mn} {F(tReg)}, {imm}({R(baseReg)})";
            string RegRegImm(string mn) => $"{mn} {R(rt)}, {R(rs)}, {imm}";
            string RegRegUImm(string mn) => $"{mn} {R(rt)}, {R(rs)}, 0x{uimm:X}";

            switch (primary)
            {
                case 0x00: return DisassembleSpecial(op, rs, rt, rd, sa, func);
                case 0x01: return DisassembleRegimm(pc, op, rs, rt, imm);
                case 0x02: return $"j 0x{((pc + 4) & 0xF0000000) | ((op & 0x03FFFFFF) << 2):X8}";
                case 0x03: return $"jal 0x{((pc + 4) & 0xF0000000) | ((op & 0x03FFFFFF) << 2):X8}";
                case 0x04: return Br("beq", rs, rt);
                case 0x05: return Br("bne", rs, rt);
                case 0x06: return Br1("blez", rs);
                case 0x07: return Br1("bgtz", rs);
                case 0x08: return RegRegImm("addi");
                case 0x09: return RegRegImm("addiu");
                case 0x0A: return RegRegImm("slti");
                case 0x0B: return RegRegImm("sltiu");
                case 0x0C: return RegRegUImm("andi");
                case 0x0D: return RegRegUImm("ori");
                case 0x0E: return RegRegUImm("xori");
                case 0x0F: return $"lui {R(rt)}, 0x{uimm:X}";
                case 0x10: return DisassembleCop0(op, rs, rt, rd, func);
                case 0x11: return DisassembleCop1(pc, op, rs, rt, rd, sa, func, imm);
                case 0x12: return $"cop2 0x{op & 0x1FFFFFF:X7}";
                case 0x14: return Br("beql", rs, rt);
                case 0x15: return Br("bnel", rs, rt);
                case 0x16: return Br1("blezl", rs);
                case 0x17: return Br1("bgtzl", rs);
                case 0x18: return RegRegImm("daddi");
                case 0x19: return RegRegImm("daddiu");
                case 0x1A: return $"ldl {R(rt)}, {imm}({R(rs)})";
                case 0x1B: return $"ldr {R(rt)}, {imm}({R(rs)})";
                case 0x1C: return DisassembleMmi(op, rs, rt, rd, sa, func);
                case 0x1E: return Mem("lq", rs, rt);
                case 0x1F: return Mem("sq", rs, rt);
                case 0x20: return Mem("lb", rs, rt);
                case 0x21: return Mem("lh", rs, rt);
                case 0x22: return Mem("lwl", rs, rt);
                case 0x23: return Mem("lw", rs, rt);
                case 0x24: return Mem("lbu", rs, rt);
                case 0x25: return Mem("lhu", rs, rt);
                case 0x26: return Mem("lwr", rs, rt);
                case 0x27: return Mem("lwu", rs, rt);
                case 0x28: return Mem("sb", rs, rt);
                case 0x29: return Mem("sh", rs, rt);
                case 0x2A: return Mem("swl", rs, rt);
                case 0x2B: return Mem("sw", rs, rt);
                case 0x2C: return Mem("sdl", rs, rt);
                case 0x2D: return Mem("sdr", rs, rt);
                case 0x2E: return Mem("swr", rs, rt);
                case 0x2F: return "cache (nop)";
                case 0x31: return MemF("lwc1", rs, rt);
                case 0x33: return "pref (nop)";
                case 0x35: return MemF("ldc1", rs, rt);
                case 0x36: return $"lqc2 {V(rt)}, {imm}({R(rs)})";
                case 0x37: return Mem("ld", rs, rt);
                case 0x39: return MemF("swc1", rs, rt);
                case 0x3D: return MemF("sdc1", rs, rt);
                case 0x3E: return $"sqc2 {V(rt)}, {imm}({R(rs)})";
                case 0x3F: return Mem("sd", rs, rt);
                default: return $"unk primary=0x{primary:X2} (0x{op:X8})";
            }
        }
        catch
        {
            return $"unk 0x{op:X8}";
        }
    }

    private static string DisassembleSpecial(uint op, uint rs, uint rt, uint rd, uint sa, uint func)
    {
        string RRR(string mn) => $"{mn} {R(rd)}, {R(rs)}, {R(rt)}";
        string RRS(string mn) => $"{mn} {R(rd)}, {R(rt)}, {sa}";
        return func switch
        {
            0x00 => sa == 0 && rd == 0 && rt == 0 ? "nop" : $"sll {R(rd)}, {R(rt)}, {sa}",
            0x02 => RRS("srl"),
            0x03 => RRS("sra"),
            0x04 => $"sllv {R(rd)}, {R(rt)}, {R(rs)}",
            0x06 => $"srlv {R(rd)}, {R(rt)}, {R(rs)}",
            0x07 => $"srav {R(rd)}, {R(rt)}, {R(rs)}",
            0x08 => $"jr {R(rs)}",
            0x09 => rd == 31 ? $"jalr {R(rs)}" : $"jalr {R(rd)}, {R(rs)}",
            0x0C => "syscall",
            0x0D => "break",
            0x0F => "sync",
            0x10 => $"mfhi {R(rd)}",
            0x11 => $"mthi {R(rs)}",
            0x12 => $"mflo {R(rd)}",
            0x13 => $"mtlo {R(rs)}",
            0x14 => $"dsllv {R(rd)}, {R(rt)}, {R(rs)}",
            0x16 => $"dsrlv {R(rd)}, {R(rt)}, {R(rs)}",
            0x17 => $"dsrav {R(rd)}, {R(rt)}, {R(rs)}",
            0x18 => $"mult {R(rs)}, {R(rt)}",
            0x19 => $"multu {R(rs)}, {R(rt)}",
            0x1A => $"div {R(rs)}, {R(rt)}",
            0x1B => $"divu {R(rs)}, {R(rt)}",
            0x20 => RRR("add"),
            0x21 => RRR("addu"),
            0x22 => RRR("sub"),
            0x23 => RRR("subu"),
            0x24 => RRR("and"),
            0x25 => RRR("or"),
            0x26 => RRR("xor"),
            0x27 => RRR("nor"),
            0x2A => RRR("slt"),
            0x2B => RRR("sltu"),
            0x2C => RRR("dadd"),
            0x2D => RRR("daddu"),
            0x2E => RRR("dsub"),
            0x2F => RRR("dsubu"),
            0x38 => RRS("dsll"),
            0x3A => RRS("dsrl"),
            0x3B => RRS("dsra"),
            0x3C => RRS("dsll32"),
            0x3E => RRS("dsrl32"),
            0x3F => RRS("dsra32"),
            _ => $"unk special func=0x{func:X2}"
        };
    }

    private static string DisassembleRegimm(uint pc, uint op, uint rs, uint rt, short imm)
    {
        uint target = (uint)(pc + 4 + (imm << 2));
        return rt switch
        {
            0x00 => $"bltz {R(rs)}, 0x{target:X8}",
            0x01 => $"bgez {R(rs)}, 0x{target:X8}",
            0x02 => $"bltzl {R(rs)}, 0x{target:X8}",
            0x03 => $"bgezl {R(rs)}, 0x{target:X8}",
            0x10 => $"bltzal {R(rs)}, 0x{target:X8}",
            0x11 => $"bgezal {R(rs)}, 0x{target:X8}",
            0x12 => $"bltzall {R(rs)}, 0x{target:X8}",
            0x13 => $"bgezall {R(rs)}, 0x{target:X8}",
            _ => $"unk regimm rt=0x{rt:X2}"
        };
    }

    private static string DisassembleCop0(uint op, uint rs, uint rt, uint rd, uint func)
    {
        return rs switch
        {
            0x00 => $"mfc0 {R(rt)}, $c0_{rd}",
            0x04 => $"mtc0 {R(rt)}, $c0_{rd}",
            0x10 => func switch { 0x18 => "eret", 0x38 => "ei", 0x39 => "di", _ => $"cop0.co func=0x{func:X2}" },
            _ => $"unk cop0 rs=0x{rs:X2}"
        };
    }

    private static string DisassembleCop1(uint pc, uint op, uint rs, uint rt, uint rd, uint sa, uint func, short imm)
    {
        uint fs = rd, fd = sa;
        switch (rs)
        {
            case 0x00: return $"mfc1 {R(rt)}, {F(fs)}";
            case 0x02: return $"cfc1 {R(rt)}, $fcr{fs}";
            case 0x04: return $"mtc1 {R(rt)}, {F(fs)}";
            case 0x06: return $"ctc1 {R(rt)}, $fcr{fs}";
            case 0x08:
                uint target = (uint)(pc + 4 + (imm << 2));
                return (rt & 1) == 0 ? $"bc1f 0x{target:X8}" : $"bc1t 0x{target:X8}";
            case 0x10: // S format
                return func switch
                {
                    0x00 => $"add.s {F(fd)}, {F(fs)}, {F(rt)}",
                    0x01 => $"sub.s {F(fd)}, {F(fs)}, {F(rt)}",
                    0x02 => $"mul.s {F(fd)}, {F(fs)}, {F(rt)}",
                    0x03 => $"div.s {F(fd)}, {F(fs)}, {F(rt)}",
                    0x04 => $"sqrt.s {F(fd)}, {F(fs)}",
                    0x05 => $"abs.s {F(fd)}, {F(fs)}",
                    0x06 => $"mov.s {F(fd)}, {F(fs)}",
                    0x07 => $"neg.s {F(fd)}, {F(fs)}",
                    0x24 => $"cvt.w.s {F(fd)}, {F(fs)}",
                    _ when (func & 0x30) == 0x30 => $"c.cond.s {F(fs)}, {F(rt)} (func=0x{func:X2})",
                    _ => $"unk cop1.s func=0x{func:X2}"
                };
            case 0x14: // W format
                return func == 0x20 ? $"cvt.s.w {F(fd)}, {F(fs)}" : $"unk cop1.w func=0x{func:X2}";
            default: return $"unk cop1 rs=0x{rs:X2}";
        }
    }

    /// <summary>MMI: real two-field (sa,func) dispatch, verified against PCSX2's
    /// R5900OpcodeTables.cpp/MMI.cpp (tbl_MMI[64] direct func index; func 8/9/0x28/0x29
    /// delegate to independent 32-entry MMI0/2/1/3 tables keyed by sa). Mirrors
    /// EmotionEngine.ExecuteMmi/ExecuteMmiFamily exactly.</summary>
    private static string DisassembleMmi(uint op, uint rs, uint rt, uint rd, uint sa, uint func)
    {
        if (func is 0x08 or 0x09 or 0x28 or 0x29)
        {
            uint key = (sa << 6) | func;
            string mn = key switch
            {
                (0u << 6) | 0x08 => "paddw", (1u << 6) | 0x08 => "psubw",
                (2u << 6) | 0x28 => "pceqw", (2u << 6) | 0x08 => "pcgtw",
                (3u << 6) | 0x08 => "pmaxw", (3u << 6) | 0x28 => "pminw",
                (16u << 6) | 0x08 => "paddsw", (17u << 6) | 0x08 => "psubsw",
                (16u << 6) | 0x28 => "padduw", (17u << 6) | 0x28 => "psubuw",
                (18u << 6) | 0x28 => "pextuw",
                (4u << 6) | 0x08 => "paddh", (5u << 6) | 0x08 => "psubh",
                (6u << 6) | 0x28 => "pceqh", (6u << 6) | 0x08 => "pcgth",
                (7u << 6) | 0x08 => "pmaxh", (7u << 6) | 0x28 => "pminh",
                (20u << 6) | 0x08 => "paddsh", (21u << 6) | 0x08 => "psubsh",
                (20u << 6) | 0x28 => "padduh", (21u << 6) | 0x28 => "psubuh",
                (22u << 6) | 0x28 => "pextuh",
                (4u << 6) | 0x28 => "padsbh", (1u << 6) | 0x28 => "pabsw", (5u << 6) | 0x28 => "pabsh",
                (8u << 6) | 0x08 => "paddb", (9u << 6) | 0x08 => "psubb",
                (10u << 6) | 0x28 => "pceqb", (10u << 6) | 0x08 => "pcgtb",
                (24u << 6) | 0x08 => "paddsb", (25u << 6) | 0x08 => "psubsb",
                (24u << 6) | 0x28 => "paddub", (25u << 6) | 0x28 => "psubub",
                (26u << 6) | 0x28 => "pextub", (27u << 6) | 0x28 => "qfsrv",
                (18u << 6) | 0x09 => "pand", (18u << 6) | 0x29 => "por",
                (19u << 6) | 0x09 => "pxor", (19u << 6) | 0x29 => "pnor",
                (18u << 6) | 0x08 => "pextlw", (22u << 6) | 0x08 => "pextlh", (26u << 6) | 0x08 => "pextlb",
                (14u << 6) | 0x09 => "pcpyld", (14u << 6) | 0x29 => "pcpyud",
                (8u << 6) | 0x09 => "pmfhi", (9u << 6) | 0x09 => "pmflo",
                (8u << 6) | 0x29 => "pmthi", (9u << 6) | 0x29 => "pmtlo",
                (10u << 6) | 0x09 => "pinth", (10u << 6) | 0x29 => "pinteh",
                (26u << 6) | 0x09 => "pexeh", (27u << 6) | 0x09 => "prevh",
                (30u << 6) | 0x09 => "pexew", (31u << 6) | 0x09 => "prot3w",
                (26u << 6) | 0x29 => "pexch", (27u << 6) | 0x29 => "pcpyh", (30u << 6) | 0x29 => "pexcw",
                _ => $"mmi.family sa={sa} func=0x{func:X2}"
            };
            return $"{mn} {R(rd)}, {R(rs)}, {R(rt)}";
        }
        return func switch
        {
            0x00 => $"madd {R(rd)}, {R(rs)}, {R(rt)}",
            0x01 => $"maddu {R(rd)}, {R(rs)}, {R(rt)}",
            0x04 => $"plzcw {R(rd)}, {R(rs)}",
            0x10 => $"mfhi1 {R(rd)}",
            0x11 => $"mthi1 {R(rs)}",
            0x12 => $"mflo1 {R(rd)}",
            0x13 => $"mtlo1 {R(rs)}",
            0x18 => $"mult1 {R(rs)}, {R(rt)}",
            0x19 => $"multu1 {R(rs)}, {R(rt)}",
            0x1A => $"div1 {R(rs)}, {R(rt)}",
            0x1B => $"divu1 {R(rs)}, {R(rt)}",
            0x20 => $"madd1 {R(rd)}, {R(rs)}, {R(rt)}",
            0x21 => $"maddu1 {R(rd)}, {R(rs)}, {R(rt)}",
            0x34 => $"psllh {R(rd)}, {R(rt)}, {sa}",
            0x36 => $"psrlh {R(rd)}, {R(rt)}, {sa}",
            0x37 => $"psrah {R(rd)}, {R(rt)}, {sa}",
            0x3C => $"psllw {R(rd)}, {R(rt)}, {sa}",
            0x3E => $"psrlw {R(rd)}, {R(rt)}, {sa}",
            0x3F => $"psraw {R(rd)}, {R(rt)}, {sa}",
            _ => $"unk mmi func=0x{func:X2}"
        };
    }
}
