using System;
using DetPS2.Core;

namespace DetPS2.Tests;

/// <summary>
/// T1 / WP-05+06 IOP core execution smokes (kept separate from SmokeTests.cs to avoid
/// multi-agent thrash on the mega Main list). Invoked from <see cref="SmokeTests.Main"/>.
/// </summary>
public static class IopExecSmokes
{
    /// <summary>
    /// WP-05: run exactly 1000 instruction slots twice — GPRs + InstructionsExecuted match.
    /// Tight counter loop so the budget is fully consumed (no early halt).
    /// </summary>
    public static void RunInstructions_1k_Deterministic()
    {
        uint Addiu(uint rt, uint rs, short imm) =>
            (0x09u << 26) | (rs << 21) | (rt << 16) | (ushort)imm;
        uint Beq(uint rs, uint rt, short off) =>
            (0x04u << 26) | (rs << 21) | (rt << 16) | (ushort)off;
        const uint Nop = 0;

        const uint baseAddr = 0x2000;
        // base+0: addiu r2,r0,0; base+4: addiu r2,r2,1; base+8: beq r0,r0,-2; base+12: nop
        // Branch at +8: delay PC=+12, target=+4 → off=(4-12)/4=-2.
        var words = new uint[]
        {
            Addiu(2, 0, 0),
            Addiu(2, 2, 1),
            Beq(0, 0, -2),
            Nop
        };

        static (ulong insns, uint r2, int retired) RunOnce(uint[] prog)
        {
            var sys = new Ps2System();
            sys.Iop.LoadProgram(baseAddr, prog);
            int retired = sys.Iop.RunInstructions(1000);
            return (sys.Iop.InstructionsExecuted, sys.Iop.GetGpr(2), retired);
        }

        var a = RunOnce(words);
        var b = RunOnce(words);
        if (a.retired != 1000 || b.retired != 1000)
            throw new Exception($"IOP 1k budget not fully used: a={a.retired} b={b.retired}");
        if (a.insns != 1000 || b.insns != 1000)
            throw new Exception($"InstructionsExecuted expected 1000 got a={a.insns} b={b.insns}");
        if (a.r2 != b.r2)
            throw new Exception($"IOP 1k not deterministic r2 a={a.r2} b={b.r2}");
        if (a.r2 == 0)
            throw new Exception("IOP 1k loop did not advance r2");

        Console.WriteLine($"[Smoke] Iop_RunInstructions_1k_Deterministic OK (insns=1000 r2={a.r2})");
    }

    /// <summary>
    /// WP-06: SYSCALL + InstallMinimalExceptionStub returns to next insn; BREAK same path.
    /// Synthetic IRX-style: setup → syscall → continue → break → continue.
    /// </summary>
    public static void SyscallBreak_VectorRfe_Returns()
    {
        uint Addiu(uint rt, uint rs, short imm) =>
            (0x09u << 26) | (rs << 21) | (rt << 16) | (ushort)imm;
        uint Syscall(uint code) => (code << 6) | 0x0Cu;
        uint Break(uint code) => (code << 6) | 0x0Du;
        uint Beq(uint rs, uint rt, short off) =>
            (0x04u << 26) | (rs << 21) | (rt << 16) | (ushort)off;
        const uint Nop = 0;

        const uint baseAddr = 0x3000;
        var words = new uint[]
        {
            Addiu(3, 0, 1),
            Syscall(0x42),
            Addiu(3, 3, 10),
            Break(0x7),
            Addiu(3, 3, 100),
            Beq(0, 0, -1), // spin
            Nop
        };

        var sys = new Ps2System();
        sys.Iop.Cop0Status = 0; // BEV=0 → vector 0x80000080
        sys.Iop.InstallMinimalExceptionStub();
        sys.Iop.LoadProgram(baseAddr, words);
        sys.Iop.RunInstructions(64);

        if (sys.Iop.ExceptionCount < 2)
            throw new Exception($"expected ≥2 exceptions got {sys.Iop.ExceptionCount}");
        // After both exceptions resume, r3 = 1 + 10 + 100 = 111
        if (sys.Iop.GetGpr(3) != 111)
            throw new Exception($"IOP r3 after SYSCALL/BREAK resume expected 111 got {sys.Iop.GetGpr(3)}");
        if (sys.Iop.LastExceptionCode != 9)
            throw new Exception($"LastExceptionCode expected 9 got {sys.Iop.LastExceptionCode}");
        if (sys.Iop.LastSyscallCode != 0x7)
            throw new Exception($"LastSyscallCode expected 0x7 got 0x{sys.Iop.LastSyscallCode:X}");

        var sys2 = new Ps2System();
        sys2.Iop.Cop0Status = 0;
        sys2.Iop.InstallMinimalExceptionStub();
        sys2.Iop.LoadProgram(baseAddr, words);
        sys2.Iop.RunInstructions(64);
        if (sys2.Iop.GetGpr(3) != sys.Iop.GetGpr(3) ||
            sys2.Iop.ExceptionCount != sys.Iop.ExceptionCount ||
            sys2.Iop.InstructionsExecuted != sys.Iop.InstructionsExecuted)
            throw new Exception("IOP SYSCALL/BREAK path not deterministic");

        Console.WriteLine(
            $"[Smoke] Iop_SyscallBreak_VectorRfe_Returns OK (exc={sys.Iop.ExceptionCount} r3={sys.Iop.GetGpr(3)} insns={sys.Iop.InstructionsExecuted})");
    }

    /// <summary>Extend legacy hand-assembled loop to use RunInstructions + ExceptionCount.</summary>
    public static void HandAssembledLoop_UsesRunInstructions()
    {
        uint Addiu(uint rt, uint rs, short imm) =>
            (0x09u << 26) | (rs << 21) | (rt << 16) | (ushort)imm;
        uint Bne(uint rs, uint rt, short off) =>
            (0x05u << 26) | (rs << 21) | (rt << 16) | (ushort)off;
        const uint Nop = 0;
        const uint Syscall = 0x0000000C;
        const uint baseAddr = 0x1000;

        var words = new uint[]
        {
            Addiu(2, 0, 5),
            Addiu(3, 0, 0),
            Addiu(3, 3, 1),
            Bne(3, 2, -2),
            Nop,
            Syscall
        };

        var sys = new Ps2System();
        sys.Iop.LoadProgram(baseAddr, words);
        int n = sys.Iop.RunInstructions(200);
        if (n <= 0) throw new Exception("RunInstructions retired 0");
        if (sys.Iop.GetGpr(3) != 5) throw new Exception($"r3={sys.Iop.GetGpr(3)}");
        if (sys.Iop.ExceptionCount < 1 || sys.Iop.LastExceptionCode != 8)
            throw new Exception($"exc count={sys.Iop.ExceptionCount} code={sys.Iop.LastExceptionCode}");

        var sys2 = new Ps2System();
        sys2.Iop.LoadProgram(baseAddr, words);
        sys2.Iop.RunInstructions(200);
        if (sys2.Iop.InstructionsExecuted != sys.Iop.InstructionsExecuted ||
            sys2.Iop.ExceptionCount != sys.Iop.ExceptionCount)
            throw new Exception("not deterministic");

        Console.WriteLine($"[Smoke] Iop_HandAssembled_RunInstructions OK (insns={sys.Iop.InstructionsExecuted})");
    }
}
