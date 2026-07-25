using System;

namespace DetPS2.Core;

/// <summary>
/// R3000A (IOP) disassembler for reading real, extracted .IRX module code — plain MIPS I,
/// no MMI/128-bit GPRs/VU/COP2/FPU (the IOP has none of those; that's all EE-specific). Built
/// to reverse-engineer proprietary IOP-side RPC service protocols (no HLE substitute exists
/// for them) directly from the real module bytes, the same primary-source-first discipline
/// used for the EE disassembler, just aimed at plain MIPS I encoding.
/// </summary>
public static class IopDisassembler
{
    private static readonly string[] GprNames =
    {
        "zero","at","v0","v1","a0","a1","a2","a3",
        "t0","t1","t2","t3","t4","t5","t6","t7",
        "s0","s1","s2","s3","s4","s5","s6","s7",
        "t8","t9","k0","k1","gp","sp","fp","ra"
    };

    private static string R(uint n) => GprNames[n & 0x1F];

    /// <summary>Disassemble one 32-bit R3000A instruction. `addr` is used only for
    /// branch/jump target display, not memory access. Never throws.</summary>
    public static string Disassemble(uint addr, uint op)
    {
        try
        {
            uint primary = (op >> 26) & 0x3F;
            uint rs = (op >> 21) & 0x1F, rt = (op >> 16) & 0x1F, rd = (op >> 11) & 0x1F;
            uint sa = (op >> 6) & 0x1F, func = op & 0x3F;
            short imm = (short)(op & 0xFFFF);
            uint uimm = op & 0xFFFF;

            string Br(string mn, uint a, uint b) => $"{mn} {R(a)}, {R(b)}, 0x{addr + 4 + (uint)(imm << 2):X6}";
            string Br1(string mn, uint a) => $"{mn} {R(a)}, 0x{addr + 4 + (uint)(imm << 2):X6}";
            string Mem(string mn, uint baseReg, uint tReg) => $"{mn} {R(tReg)}, {imm}({R(baseReg)})";
            string RegRegImm(string mn) => $"{mn} {R(rt)}, {R(rs)}, {imm}";
            string RegRegUImm(string mn) => $"{mn} {R(rt)}, {R(rs)}, 0x{uimm:X}";

            switch (primary)
            {
                case 0x00: return DisassembleSpecial(rs, rt, rd, sa, func);
                case 0x01: return DisassembleRegimm(addr, rs, rt, imm);
                case 0x02: return $"j 0x{((addr + 4) & 0xF0000000) | ((op & 0x03FFFFFF) << 2):X6}";
                case 0x03: return $"jal 0x{((addr + 4) & 0xF0000000) | ((op & 0x03FFFFFF) << 2):X6}";
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
                case 0x10: return DisassembleCop0(rs, rt, rd, func);
                case 0x20: return Mem("lb", rs, rt);
                case 0x21: return Mem("lh", rs, rt);
                case 0x22: return Mem("lwl", rs, rt);
                case 0x23: return Mem("lw", rs, rt);
                case 0x24: return Mem("lbu", rs, rt);
                case 0x25: return Mem("lhu", rs, rt);
                case 0x26: return Mem("lwr", rs, rt);
                case 0x28: return Mem("sb", rs, rt);
                case 0x29: return Mem("sh", rs, rt);
                case 0x2A: return Mem("swl", rs, rt);
                case 0x2B: return Mem("sw", rs, rt);
                case 0x2E: return Mem("swr", rs, rt);
                default: return $"unk primary=0x{primary:X2} (0x{op:X8})";
            }
        }
        catch
        {
            return $"unk 0x{op:X8}";
        }
    }

    private static string DisassembleSpecial(uint rs, uint rt, uint rd, uint sa, uint func)
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
            0x10 => $"mfhi {R(rd)}",
            0x11 => $"mthi {R(rs)}",
            0x12 => $"mflo {R(rd)}",
            0x13 => $"mtlo {R(rs)}",
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
            _ => $"unk special func=0x{func:X2}"
        };
    }

    private static string DisassembleRegimm(uint addr, uint rs, uint rt, short imm)
    {
        uint target = (uint)(addr + 4 + (imm << 2));
        return rt switch
        {
            0x00 => $"bltz {R(rs)}, 0x{target:X6}",
            0x01 => $"bgez {R(rs)}, 0x{target:X6}",
            0x10 => $"bltzal {R(rs)}, 0x{target:X6}",
            0x11 => $"bgezal {R(rs)}, 0x{target:X6}",
            _ => $"unk regimm rt=0x{rt:X2}"
        };
    }

    private static string DisassembleCop0(uint rs, uint rt, uint rd, uint func)
    {
        return rs switch
        {
            0x00 => $"mfc0 {R(rt)}, $c0_{rd}",
            0x04 => $"mtc0 {R(rt)}, $c0_{rd}",
            0x10 => func == 0x10 ? "rfe" : $"cop0.co func=0x{func:X2}",
            _ => $"unk cop0 rs=0x{rs:X2}"
        };
    }
}
