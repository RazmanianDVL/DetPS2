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

    /// <summary>
    /// C1 scaffolding: default OFF path is no-op (create/switch fail, single context).
    /// With EnableMultiThreadScaffolding: dual context, unique SP, switch preserves GPRs/PC.
    /// </summary>
    public static void IopThreadContext_Scaffolding_FlagAndSwitch()
    {
        // --- Flag default OFF: zero path change ---
        var off = new Ps2System();
        if (off.Iop.MultiThreadEnabled && !Iop.MultiThreadEnvEnabled)
            throw new Exception("MultiThreadEnabled true without DETPS2_IOP_THREADS env");
        if (!Iop.MultiThreadEnvEnabled)
        {
            if (off.Iop.CreateSecondaryContext(0x2000) != -1)
                throw new Exception("CreateSecondaryContext must return -1 when flag off");
            if (off.Iop.CreateThreadContext(0x2000, 0x1C2000) != -1)
                throw new Exception("CreateThreadContext must return -1 when flag off");
            if (off.Iop.SwitchToThread(1))
                throw new Exception("SwitchToThread must fail when flag off");
            if (off.Iop.CurrentThreadId != 0 || off.Iop.ThreadCount != 1)
                throw new Exception("OFF path must report single boot context");
            if (off.Iop.TryGetThreadContext(0, out _))
                throw new Exception("TryGetThreadContext must fail when flag off (no table)");
        }

        // --- ON path via scaffolding enable (does not require process env) ---
        var sys = new Ps2System();
        sys.Iop.EnableMultiThreadScaffolding();
        if (!sys.Iop.MultiThreadEnabled)
            throw new Exception("EnableMultiThreadScaffolding did not enable");

        // Parent context: plant callee-saved-ish regs + PC
        sys.Iop.PC = 0x00004000;
        sys.Iop.SetGpr(16, 0x11111111); // $s0
        sys.Iop.SetGpr(17, 0x22222222); // $s1
        sys.Iop.SetGpr(29, 0x001F0000); // $sp parent
        sys.Iop.SetGpr(31, 0xDEADBEEF); // $ra

        int worker = sys.Iop.CreateSecondaryContext(0x00005000);
        if (worker < 1)
            throw new Exception($"CreateSecondaryContext failed: {worker}");
        if (sys.Iop.ThreadCount != 2)
            throw new Exception($"expected 2 contexts got {sys.Iop.ThreadCount}");
        if (!sys.Iop.TryGetThreadContext(worker, out var wctx) || wctx == null)
            throw new Exception("worker context missing");
        if (wctx.PC != 0x00005000)
            throw new Exception($"worker PC 0x{wctx.PC:X8}");
        if (wctx.Sp == 0 || wctx.Sp == 0x001F0000)
            throw new Exception($"worker SP must be unique, got 0x{wctx.Sp:X8}");
        uint workerSp = wctx.Sp;

        // Switch to worker: parent must be saved; live PC/SP become worker's
        if (!sys.Iop.SwitchToThread(worker))
            throw new Exception("SwitchToThread(worker) failed");
        if (sys.Iop.CurrentThreadId != worker)
            throw new Exception($"CurrentThreadId={sys.Iop.CurrentThreadId}");
        if (sys.Iop.PC != 0x00005000)
            throw new Exception($"live PC after switch 0x{sys.Iop.PC:X8}");
        if (sys.Iop.GetGpr(29) != workerSp)
            throw new Exception($"live SP after switch 0x{sys.Iop.GetGpr(29):X8}");
        // Worker starts with clear s-regs
        if (sys.Iop.GetGpr(16) != 0)
            throw new Exception("worker s0 should be 0 at create");

        // Mutate worker then return to parent
        sys.Iop.SetGpr(16, 0xAAAAAAAA);
        sys.Iop.PC = 0x00005010;
        if (!sys.Iop.SwitchToThread(0))
            throw new Exception("SwitchToThread(0) failed");
        if (sys.Iop.PC != 0x00004000)
            throw new Exception($"parent PC not restored: 0x{sys.Iop.PC:X8}");
        if (sys.Iop.GetGpr(16) != 0x11111111 || sys.Iop.GetGpr(17) != 0x22222222)
            throw new Exception("parent $s* not restored");
        if (sys.Iop.GetGpr(29) != 0x001F0000 || sys.Iop.GetGpr(31) != 0xDEADBEEF)
            throw new Exception("parent $sp/$ra not restored");

        // Worker still has its mutation when switched back
        if (!sys.Iop.SwitchToThread(worker))
            throw new Exception("re-switch worker failed");
        if (sys.Iop.PC != 0x00005010 || sys.Iop.GetGpr(16) != 0xAAAAAAAA)
            throw new Exception("worker state not preserved across switch");

        // Explicit stack create
        int t2 = sys.Iop.CreateThreadContext(0x6000, 0x001C8000, 0x1000);
        if (t2 < 1 || t2 == worker)
            throw new Exception($"CreateThreadContext bad id {t2}");
        if (!sys.Iop.TryGetThreadContext(t2, out var t2c) || t2c!.Sp != 0x001C8000)
            throw new Exception("explicit stack top not applied");

        Console.WriteLine(
            $"[Smoke] IopThreadContext_Scaffolding_FlagAndSwitch OK " +
            $"(envOn={Iop.MultiThreadEnvEnabled} workerSp=0x{workerSp:X8} threads={sys.Iop.ThreadCount})");
    }

    /// <summary>
    /// C1.2: <see cref="IopModuleHost.PrepareModuleEntry"/> / <see cref="IopModuleHost.StartLoadedModule"/>
    /// bind unique per-module stacks when multi-thread is on; flag-off keeps shared DefaultModuleStack.
    /// </summary>
    public static void PrepareModuleEntry_UniqueStacks_WhenMultiThread()
    {
        // --- Flag OFF (default scaffolding not enabled): shared DefaultModuleStack ---
        {
            var off = new Ps2System();
            if (off.Iop.MultiThreadEnabled && !Iop.MultiThreadEnvEnabled)
                throw new Exception("unexpected MultiThreadEnabled without env");

            byte[] a = IrxLoader.BuildMinimalIrx("STKA");
            byte[] b = IrxLoader.BuildMinimalIrx("STKB");
            if (!off.LoadIrx(a, "STKA").Success || !off.LoadIrx(b, "STKB").Success)
                throw new Exception("LoadIrx failed (off path)");
            int idA = off.IopModules.SearchModuleByName("STKA");
            int idB = off.IopModules.SearchModuleByName("STKB");
            if (idA < 1 || idB < 1) throw new Exception("module ids");

            if (!off.IopModules.PrepareModuleEntry(off.Iop, idA, off.Memory))
                throw new Exception("PrepareModuleEntry A failed (off)");
            uint spA = off.Iop.GetGpr(29);
            if (!off.IopModules.PrepareModuleEntry(off.Iop, idB, off.Memory))
                throw new Exception("PrepareModuleEntry B failed (off)");
            uint spB = off.Iop.GetGpr(29);

            // Non-THREADMAN modules share DefaultModuleStack when multi-thread is off.
            if (!off.Iop.MultiThreadEnabled)
            {
                if (spA != IopModuleHost.DefaultModuleStack || spB != IopModuleHost.DefaultModuleStack)
                    throw new Exception(
                        $"flag-off SP must be DefaultModuleStack " +
                        $"spA=0x{spA:X8} spB=0x{spB:X8} def=0x{IopModuleHost.DefaultModuleStack:X8}");
                if (!off.IopModules.TryGetIrx(idA, out var recA) || recA.EntryThreadId != -1)
                    throw new Exception("flag-off must leave EntryThreadId unbound");
            }
        }

        // --- Multi-thread ON via scaffolding: unique SP + EntryThreadId bind ---
        {
            var sys = new Ps2System();
            sys.Iop.EnableMultiThreadScaffolding();

            byte[] a = IrxLoader.BuildMinimalIrx("MTSTA");
            byte[] b = IrxLoader.BuildMinimalIrx("MTSTB");
            if (!sys.LoadIrx(a, "MTSTA").Success || !sys.LoadIrx(b, "MTSTB").Success)
                throw new Exception("LoadIrx failed (on path)");
            int idA = sys.IopModules.SearchModuleByName("MTSTA");
            int idB = sys.IopModules.SearchModuleByName("MTSTB");

            // Parent boot context plant — must survive StartLoadedModule switch-back.
            sys.Iop.SetGpr(16, 0xCAFEBABE);
            sys.Iop.PC = 0x00001234;
            int bootTid = sys.Iop.CurrentThreadId;

            if (!sys.IopModules.PrepareModuleEntry(sys.Iop, idA, sys.Memory))
                throw new Exception("PrepareModuleEntry A failed (on)");
            if (!sys.IopModules.TryGetIrx(idA, out var ra) || ra.EntryThreadId < 1)
                throw new Exception($"EntryThreadId A not bound ({ra?.EntryThreadId})");
            uint spA = sys.Iop.GetGpr(29);
            if (spA == 0 || spA == IopModuleHost.DefaultModuleStack)
                throw new Exception($"A SP not unique: 0x{spA:X8}");
            if (ra.EntryStackTop != spA)
                throw new Exception("EntryStackTop A mismatch");
            if (sys.Iop.CurrentThreadId != ra.EntryThreadId)
                throw new Exception("PrepareModuleEntry must switch onto entry thread");

            if (!sys.IopModules.PrepareModuleEntry(sys.Iop, idB, sys.Memory))
                throw new Exception("PrepareModuleEntry B failed (on)");
            if (!sys.IopModules.TryGetIrx(idB, out var rb) || rb.EntryThreadId < 1)
                throw new Exception($"EntryThreadId B not bound ({rb?.EntryThreadId})");
            uint spB = sys.Iop.GetGpr(29);
            if (spB == 0 || spB == spA || spB == IopModuleHost.DefaultModuleStack)
                throw new Exception($"B SP must differ from A and default: A=0x{spA:X8} B=0x{spB:X8}");
            if (rb.EntryThreadId == ra.EntryThreadId)
                throw new Exception("modules must bind distinct entry threads");

            // StartLoadedModule: runs on entry thread, restores caller context.
            sys.Iop.SwitchToThread(bootTid);
            sys.Iop.SetGpr(16, 0xCAFEBABE);
            sys.Iop.PC = 0x00001234;
            var run = sys.IopModules.StartLoadedModule(sys, idA, maxInstructions: 64);
            if (!run.Success || !run.ReturnedToSentinel)
                throw new Exception($"StartLoadedModule failed: {run.Message}");
            if (sys.Iop.CurrentThreadId != bootTid)
                throw new Exception($"caller thread not restored: {sys.Iop.CurrentThreadId}");
            if (sys.Iop.GetGpr(16) != 0xCAFEBABE || sys.Iop.PC != 0x00001234)
                throw new Exception("caller $s0/PC not restored after StartLoadedModule");

            // Entry context still holds its unique SP after switch-back.
            if (!sys.Iop.TryGetThreadContext(ra.EntryThreadId, out var actx) || actx == null)
                throw new Exception("entry context A lost");
            if (actx.StackTop != spA)
                throw new Exception($"entry A stack not preserved: 0x{actx.StackTop:X8}");

            Console.WriteLine(
                $"[Smoke] PrepareModuleEntry_UniqueStacks_WhenMultiThread OK " +
                $"(spA=0x{spA:X8} spB=0x{spB:X8} tidA={ra.EntryThreadId} tidB={rb.EntryThreadId})");
        }
    }

    /// <summary>
    /// C1.3: WaitSema/SleepThread-shaped yield hooks — park current as WAIT, run another READY,
    /// wake parent with intact callee-saves. Flag-off path is a pure no-op.
    /// </summary>
    public static void IopThreadContext_YieldHooks_ParkAndReady()
    {
        uint Addiu(uint rt, uint rs, short imm) =>
            (0x09u << 26) | (rs << 21) | (rt << 16) | (ushort)imm;
        uint Beq(uint rs, uint rt, short off) =>
            (0x04u << 26) | (rs << 21) | (rt << 16) | (ushort)off;
        const uint Nop = 0;

        // --- Flag OFF: all hooks no-op ---
        {
            var off = new Ps2System();
            if (!Iop.MultiThreadEnvEnabled)
            {
                if (off.Iop.YieldToReady())
                    throw new Exception("YieldToReady must fail when multi-thread off");
                if (off.Iop.ParkAndYieldToReady())
                    throw new Exception("ParkAndYieldToReady must fail when multi-thread off");
                if (off.Iop.WaitSemaYieldHook() || off.Iop.SleepThreadYieldHook())
                    throw new Exception("Wait/Sleep yield hooks must fail when multi-thread off");
                if (off.Iop.ReadyThread(0) || off.Iop.ReadyThread(1))
                    throw new Exception("ReadyThread must fail when multi-thread off");
                if (off.Iop.FindNextReadyThread() != -1)
                    throw new Exception("FindNextReadyThread must return -1 when multi-thread off");
                if (off.Iop.GetThreadStatus(0) != IopThreadStatus.None)
                    throw new Exception("GetThreadStatus must be None when multi-thread off");
            }
        }

        // --- ON: parent parks → worker runs real insns → parent resumes intact ---
        {
            var sys = new Ps2System();
            sys.Iop.EnableMultiThreadScaffolding();

            // Worker program at 0x5000: r2 = 0; r2++; loop (so RunInstructions advances r2)
            const uint workerBase = 0x00005000;
            var workerProg = new uint[]
            {
                Addiu(2, 0, 0),
                Addiu(2, 2, 1),
                Beq(0, 0, -2),
                Nop
            };
            for (int i = 0; i < workerProg.Length; i++)
                sys.Memory.IopWrite32(workerBase + (uint)(i * 4), workerProg[i]);

            // Parent (boot tid 0) plants callee-saved + PC that must survive park
            const uint parentPc = 0x00004000;
            const uint parentS0 = 0x11111111;
            const uint parentS1 = 0x22222222;
            const uint parentSp = 0x001F0000;
            const uint parentRa = 0xDEADBEEF;
            sys.Iop.PC = parentPc;
            sys.Iop.SetGpr(16, parentS0);
            sys.Iop.SetGpr(17, parentS1);
            sys.Iop.SetGpr(29, parentSp);
            sys.Iop.SetGpr(31, parentRa);
            sys.Iop.SetGpr(2, 0x99); // parent v0 — must not leak into worker

            int worker = sys.Iop.CreateSecondaryContext(workerBase);
            if (worker < 1)
                throw new Exception($"CreateSecondaryContext failed: {worker}");
            if (sys.Iop.FindNextReadyThread() != worker)
                throw new Exception($"FindNextReadyThread expected worker {worker} got {sys.Iop.FindNextReadyThread()}");
            if (sys.Iop.GetThreadStatus(worker) != IopThreadStatus.Ready)
                throw new Exception("worker must be READY at create");

            // Alone-park: with only boot RUN and we park before... worker is READY so park yields.
            // First prove YieldToReady (cooperative, both stay runnable-ish):
            // ParkAndYieldToReady: parent → WAIT, switch to worker.
            if (!sys.Iop.ParkAndYieldToReady())
                throw new Exception("ParkAndYieldToReady should switch to READY worker");
            if (sys.Iop.CurrentThreadId != worker)
                throw new Exception($"after park CurrentThreadId={sys.Iop.CurrentThreadId} want {worker}");
            if (sys.Iop.PC != workerBase)
                throw new Exception($"worker live PC 0x{sys.Iop.PC:X8}");
            if (sys.Iop.GetThreadStatus(0) != IopThreadStatus.Wait)
                throw new Exception($"parent status after park={sys.Iop.GetThreadStatus(0)} want Wait");
            if (!sys.Iop.TryGetThreadContext(0, out var pctx) || pctx == null)
                throw new Exception("parent context missing after park");
            if (pctx.PC != parentPc || pctx.Gprs[16] != parentS0 || pctx.Gprs[17] != parentS1 ||
                pctx.Gprs[29] != parentSp || pctx.Gprs[31] != parentRa)
                throw new Exception("parent GPRs/PC not preserved across ParkAndYield");
            if (pctx.Status != IopThreadStatus.Wait)
                throw new Exception("saved parent status must be Wait");

            // Worker executes real R3000 quanta while parent is parked
            int retired = sys.Iop.RunInstructions(32);
            if (retired <= 0)
                throw new Exception("worker RunInstructions retired 0");
            if (sys.Iop.GetGpr(2) == 0)
                throw new Exception("worker r2 did not advance (loop not running)");
            uint workerR2 = sys.Iop.GetGpr(2);
            uint workerPcAfter = sys.Iop.PC;

            // Worker cannot YieldToReady to parent while parent is WAIT
            if (sys.Iop.YieldToReady())
                throw new Exception("YieldToReady must not switch to WAIT parent");
            if (sys.Iop.FindNextReadyThread() != -1)
                throw new Exception("no READY peer while parent WAIT");

            // SignalSema-shaped wake: WAIT → READY, then cooperative yield
            if (!sys.Iop.ReadyThread(0))
                throw new Exception("ReadyThread(parent) failed");
            if (sys.Iop.GetThreadStatus(0) != IopThreadStatus.Ready)
                throw new Exception("parent not READY after wake");
            if (!sys.Iop.YieldToReady())
                throw new Exception("YieldToReady after ReadyThread should switch to parent");
            if (sys.Iop.CurrentThreadId != 0)
                throw new Exception($"expected parent after yield, got {sys.Iop.CurrentThreadId}");
            if (sys.Iop.PC != parentPc)
                throw new Exception($"parent PC not restored: 0x{sys.Iop.PC:X8}");
            if (sys.Iop.GetGpr(16) != parentS0 || sys.Iop.GetGpr(17) != parentS1)
                throw new Exception("parent $s* not restored after yield cycle");
            if (sys.Iop.GetGpr(29) != parentSp || sys.Iop.GetGpr(31) != parentRa)
                throw new Exception("parent $sp/$ra not restored after yield cycle");
            if (sys.Iop.GetGpr(2) != 0x99)
                throw new Exception("parent $v0 clobbered by worker quanta");

            // Worker still holds its progress when switched back
            if (!sys.Iop.SwitchToThread(worker))
                throw new Exception("re-switch worker failed");
            if (sys.Iop.GetGpr(2) != workerR2 || sys.Iop.PC != workerPcAfter)
                throw new Exception("worker state lost across parent resume");

            // Aliases must match ParkAndYield (parent READY again → park yields to worker)
            sys.Iop.SwitchToThread(0);
            sys.Iop.ReadyThread(worker); // ensure worker READY after prior Run status
            // After SwitchToThread(0), worker was left Ready (SwitchToThread demotes Run→Ready).
            if (!sys.Iop.WaitSemaYieldHook())
                throw new Exception("WaitSemaYieldHook should park+yield");
            if (sys.Iop.CurrentThreadId != worker || sys.Iop.GetThreadStatus(0) != IopThreadStatus.Wait)
                throw new Exception("WaitSemaYieldHook semantics");
            sys.Iop.ReadyThread(0);
            sys.Iop.SwitchToThread(0);
            // SleepThread-shaped same contract
            if (!sys.Iop.SleepThreadYieldHook())
                throw new Exception("SleepThreadYieldHook should park+yield");
            if (sys.Iop.GetThreadStatus(0) != IopThreadStatus.Wait)
                throw new Exception("SleepThreadYieldHook must leave parent WAIT");

            // Alone-park: only one runnable — mark WAIT, return false, stay current
            {
                var alone = new Ps2System();
                alone.Iop.EnableMultiThreadScaffolding();
                alone.Iop.PC = 0x6000;
                alone.Iop.SetGpr(16, 0xABCDEF01);
                if (alone.Iop.ParkAndYieldToReady())
                    throw new Exception("alone ParkAndYield must return false");
                if (alone.Iop.CurrentThreadId != 0)
                    throw new Exception("alone park must stay on boot thread");
                if (alone.Iop.GetThreadStatus(0) != IopThreadStatus.Wait)
                    throw new Exception("alone park must still mark WAIT");
                if (alone.Iop.PC != 0x6000 || alone.Iop.GetGpr(16) != 0xABCDEF01)
                    throw new Exception("alone park must not clobber live regs");
            }

            Console.WriteLine(
                $"[Smoke] IopThreadContext_YieldHooks_ParkAndReady OK " +
                $"(worker={worker} workerR2={workerR2} retired={retired})");
        }
    }

    /// <summary>
    /// C1 yield-start: with multi-thread + yield scaffolding, a long-running module entry that
    /// sees a READY peer at the 16k checkpoint enqueues residual work; drain advances PC without
    /// rewinding to entry. Flag-off / scaffolding-off leaves residual queue empty.
    /// </summary>
    public static void IopYieldStart_ResidualOnReadyPeer()
    {
        // --- Scaffolding off: no residual path (per-instance; no process static leak) ---
        {
            var off = new Ps2System();
            if (off.IopModules.YieldStartEnabled && !IopModuleHost.YieldStartEnvEnabled)
                throw new Exception("YieldStartEnabled true without env or scaffolding on off host");
            byte[] irx = IrxLoader.BuildMinimalIrx("YSOFF");
            if (!off.LoadIrx(irx, "YSOFF").Success)
                throw new Exception("LoadIrx YSOFF failed");
            int id = off.IopModules.SearchModuleByName("YSOFF");
            var run = off.IopModules.StartLoadedModule(off, id, maxInstructions: 256);
            if (!run.Success || !run.ReturnedToSentinel)
                throw new Exception($"minimal start failed off-path: {run.Message}");
            if (run.PartialYieldStart || off.IopModules.HasResidualModuleStart)
                throw new Exception("off-path must not enqueue residual");
        }

        // --- Multi-thread + per-instance yield scaffolding: residual on READY peer ---
        {
            var sys = new Ps2System();
            sys.Iop.EnableMultiThreadScaffolding();
            sys.IopModules.EnableYieldStartScaffolding();
            if (!sys.IopModules.YieldStartEnabled)
                throw new Exception("EnableYieldStartScaffolding did not enable");
            // Sibling host must not see scaffolding leak.
            var sibling = new Ps2System();
            if (sibling.IopModules.YieldStartEnabled && !IopModuleHost.YieldStartEnvEnabled)
                throw new Exception("yield-start scaffolding leaked to sibling Ps2System");

            // READY peer so checkpoint sees FindNextReadyThread >= 0.
            int peer = sys.Iop.CreateSecondaryContext(0x00007000);
            if (peer < 1)
                throw new Exception("CreateSecondaryContext peer failed");

            byte[] irx = IrxLoader.BuildMinimalIrx("YSON");
            var lr = sys.LoadIrx(irx, "YSON");
            if (!lr.Success)
                throw new Exception("LoadIrx YSON failed");
            int id = sys.IopModules.SearchModuleByName("YSON");
            if (!sys.IopModules.TryGetIrx(id, out var rec) || rec.Entry == 0)
                throw new Exception("YSON entry missing");

            // Overwrite entry with a self-loop (j self; nop) so we hit the 16k checkpoint.
            uint entryPhys = rec.Entry & 0x1FFFFFFFu;
            uint jSelf = (2u << 26) | ((entryPhys >> 2) & 0x03FFFFFFu);
            sys.Memory.IopWrite32(entryPhys, jSelf);
            sys.Memory.IopWrite32(entryPhys + 4, 0); // nop delay

            int boot = sys.Iop.CurrentThreadId;
            var run = sys.IopModules.StartLoadedModule(sys, id, maxInstructions: 100_000);
            if (!run.PartialYieldStart)
                throw new Exception(
                    $"expected PartialYieldStart with READY peer; msg={run.Message} " +
                    $"insns={run.InstructionsExecuted} residual={sys.IopModules.HasResidualModuleStart}");
            if (!sys.IopModules.HasResidualModuleStart)
                throw new Exception("residual queue empty after partial yield-start");
            if (run.InstructionsExecuted < IopModuleHost.YieldStartCheckpointInsn)
                throw new Exception(
                    $"should run at least one 16k checkpoint, got {run.InstructionsExecuted}");
            if (sys.Iop.CurrentThreadId != boot)
                throw new Exception("boot thread not restored after partial start");

            // Drain one residual slice — must retire instructions without rewinding to entry.
            if (!sys.IopModules.TryGetIrx(id, out var mid) || mid.EntryThreadId < 1)
                throw new Exception("entry thread not bound for residual");
            if (!sys.Iop.TryGetThreadContext(mid.EntryThreadId, out var ect) || ect == null)
                throw new Exception("entry context missing");
            uint pcBefore = ect.PC;
            int drained = sys.IopModules.DrainResidualModuleStarts(sys);
            if (drained <= 0)
                throw new Exception($"DrainResidualModuleStarts retired {drained}");
            if (!sys.Iop.TryGetThreadContext(mid.EntryThreadId, out var ect2) || ect2 == null)
                throw new Exception("entry context lost after drain");
            if (ect2.PC == 0)
                throw new Exception("entry PC clobbered to 0");
            // Core contract: drain retired work and entry context still has a live PC
            // (not rewound via PrepareModuleEntry to a cold zero state). Exact loop PC
            // depends on KSEG mapping of the self-j body — not asserted here.
            _ = pcBefore;

            Console.WriteLine(
                $"[Smoke] IopYieldStart_ResidualOnReadyPeer OK " +
                $"(partialInsns={run.InstructionsExecuted} drained={drained} peer={peer} " +
                $"pcBefore=0x{pcBefore:X8} pcAfter=0x{ect2.PC:X8})");
        }
    }

    /// <summary>
    /// C1.4: compose live RealSifRpc dispatch with multi-thread contexts.
    /// Flag-off: TryEnterRealRpcDispatch is a no-op; LiveRpcDispatchEnabled matches product default.
    /// Multi-thread on: dedicated dispatch context preserves caller GPRs/PC across a mid-quantum
    /// synthetic handler (jr $ra to ModuleReturnSentinel).
    /// </summary>
    public static void RealRpc_DispatchCompose_WithMultiThread()
    {
        // --- Flag helpers: default prefer-live; NO_REAL_RPC / IOP_REAL_RPC=0 hard-off ---
        // (Cannot mutate process env safely for other smokes — only assert current process
        //  defaults and the static helper's documented mapping for unset vars.)
        if (Environment.GetEnvironmentVariable("DETPS2_NO_REAL_RPC") != "1" &&
            Environment.GetEnvironmentVariable("DETPS2_IOP_REAL_RPC") != "0")
        {
            if (!RealSifRpc.LiveRpcDispatchEnabled())
                throw new Exception("LiveRpcDispatchEnabled should be true with default env");
        }
        if (Environment.GetEnvironmentVariable("DETPS2_NO_REAL_RPC") == "1" ||
            Environment.GetEnvironmentVariable("DETPS2_IOP_REAL_RPC") == "0")
        {
            if (RealSifRpc.LiveRpcDispatchEnabled())
                throw new Exception("LiveRpcDispatchEnabled should be false when opt-out env set");
        }

        // --- Flag OFF: TryEnterRealRpcDispatch no-op ---
        {
            var off = new Ps2System();
            if (!Iop.MultiThreadEnvEnabled)
            {
                if (off.Iop.TryEnterRealRpcDispatch(out int prev))
                    throw new Exception("TryEnterRealRpcDispatch must fail when multi-thread off");
                if (prev != 0)
                    throw new Exception("previousThreadId should be 0 when multi-thread off");
                if (off.Iop.RpcDispatchThreadId != -1)
                    throw new Exception("RpcDispatchThreadId must be -1 when multi-thread off");
            }
        }

        // --- ON: caller preserved across dedicated dispatch + synthetic jr ra ---
        {
            uint Jr(uint rs) => (rs << 21) | 0x08u; // SPECIAL jr rs
            const uint Nop = 0;

            var sys = new Ps2System();
            sys.Iop.EnableMultiThreadScaffolding();
            sys.Hle.EnableSonyKernel();
            var rpc = sys.Hle.Sony!.RealRpc;
            rpc.BindHost(sys);

            const uint parentPc = 0x00004100;
            const uint parentS0 = 0xCAFEBABE;
            const uint parentSp = 0x001F0000;
            const uint parentRa = 0xA5A5A5A5;
            const uint parentV0 = 0x11112222;
            sys.Iop.PC = parentPc;
            sys.Iop.SetGpr(16, parentS0);
            sys.Iop.SetGpr(29, parentSp);
            sys.Iop.SetGpr(31, parentRa);
            sys.Iop.SetGpr(2, parentV0);

            // Synthetic handler at 0x7000: addiu v0, zero, 0x42; jr ra; nop
            // With $ra = ModuleReturnSentinel, dispatch loop stops on sentinel.
            const uint handlerBase = 0x00007000;
            uint Addiu(uint rt, uint rs, short imm) =>
                (0x09u << 26) | (rs << 21) | (rt << 16) | (ushort)imm;
            var handler = new uint[]
            {
                Addiu(2, 0, 0x42), // v0 = 0x42 (reply pointer-ish for smoke)
                Jr(31),           // jr $ra
                Nop
            };
            for (int i = 0; i < handler.Length; i++)
                sys.Memory.IopWrite32(handlerBase + (uint)(i * 4), handler[i]);

            if (!sys.Iop.TryEnterRealRpcDispatch(out int prevTid, Iop.RealRpcDispatchStackTop))
                throw new Exception("TryEnterRealRpcDispatch should succeed with multi-thread on");
            if (prevTid != 0)
                throw new Exception($"expected prevTid=0 got {prevTid}");
            if (sys.Iop.CurrentThreadId == prevTid)
                throw new Exception("dispatch should switch off caller thread");
            if (sys.Iop.RpcDispatchThreadId < 1)
                throw new Exception("RpcDispatchThreadId not set");

            // Parent slot must still hold pre-enter GPRs (saved by SwitchToThread).
            if (!sys.Iop.TryGetThreadContext(prevTid, out var pctx) || pctx == null)
                throw new Exception("caller context missing after enter");
            if (pctx.PC != parentPc || pctx.Gprs[16] != parentS0 || pctx.Gprs[29] != parentSp ||
                pctx.Gprs[31] != parentRa || pctx.Gprs[2] != parentV0)
                throw new Exception("caller GPRs/PC not preserved in thread table during dispatch");

            // Arm handler like TryDispatchRealRegisteredRpc
            sys.Iop.PC = handlerBase;
            sys.Iop.SetGpr(29, Iop.RealRpcDispatchStackTop);
            sys.Iop.SetGpr(30, Iop.RealRpcDispatchStackTop);
            sys.Iop.SetGpr(31, IopModuleHost.ModuleReturnSentinel);
            sys.Iop.SetGpr(4, 1);
            sys.Iop.SetGpr(5, 0);
            sys.Iop.SetGpr(6, 0);

            ulong before = sys.Iop.InstructionsExecuted;
            bool returned = false;
            for (int i = 0; i < 32; i++)
            {
                if (sys.Iop.PC == IopModuleHost.ModuleReturnSentinel) { returned = true; break; }
                sys.Iop.Step(1);
                if (sys.Iop.PC == IopModuleHost.ModuleReturnSentinel) { returned = true; break; }
            }
            if (!returned)
                throw new Exception($"handler did not return to sentinel pc=0x{sys.Iop.PC:X8}");
            uint replyV0 = sys.Iop.GetGpr(2);
            if (replyV0 != 0x42)
                throw new Exception($"handler v0 expected 0x42 got 0x{replyV0:X}");
            if (sys.Iop.InstructionsExecuted <= before)
                throw new Exception("handler retired 0 insns");

            sys.Iop.LeaveRealRpcDispatch(prevTid);
            if (sys.Iop.CurrentThreadId != prevTid)
                throw new Exception($"Leave did not restore caller tid={sys.Iop.CurrentThreadId}");
            if (sys.Iop.PC != parentPc)
                throw new Exception($"caller PC not restored: 0x{sys.Iop.PC:X8}");
            if (sys.Iop.GetGpr(16) != parentS0 || sys.Iop.GetGpr(29) != parentSp ||
                sys.Iop.GetGpr(31) != parentRa || sys.Iop.GetGpr(2) != parentV0)
                throw new Exception("caller live GPRs not restored after LeaveRealRpcDispatch");

            // Counters exist and start at 0 on a fresh RealSifRpc
            if (rpc.LiveRpcHits != 0 || rpc.LiveRpcFallbacks != 0)
                throw new Exception("LiveRpc counters should be 0 before any live CALL");

            // Second enter reuses the same dispatch slot
            if (!sys.Iop.TryEnterRealRpcDispatch(out int prev2))
                throw new Exception("second TryEnterRealRpcDispatch failed");
            if (sys.Iop.RpcDispatchThreadId < 1)
                throw new Exception("dispatch tid lost after re-enter");
            sys.Iop.LeaveRealRpcDispatch(prev2);

            Console.WriteLine(
                $"[Smoke] RealRpc_DispatchCompose_WithMultiThread OK " +
                $"(dispatchTid={sys.Iop.RpcDispatchThreadId} replyV0=0x{replyV0:X} " +
                $"liveEnabled={RealSifRpc.LiveRpcDispatchEnabled()})");
        }
    }

    /// <summary>
    /// M3-e (docs/infra-audits/real-sif-rpc-dual-path.md §7): plant a synthetic
    /// <c>sceSifRegisterRpc</c> server entry in IOP RAM matching
    /// <c>RealSifRpc.TryFindRealRpcServer</c> layout, run a tiny R3000 handler that returns
    /// known reply bytes, EE BIND+CALL through existing <see cref="RealSifRpc.TryHandle"/>,
    /// assert <see cref="RealSifRpc.LiveRpcHits"/> increments and HLE unknown path is skipped.
    /// <para>
    /// Honest limitations: does not assemble a real IRX that calls <c>sceSifRegisterRpc</c>;
    /// plants the SIFCMD queue/server chain that live registration would have built. BIND is
    /// still HLE (product policy). Skips when live dispatch is env-opted-out so fleet defaults
    /// and emergency <c>DETPS2_NO_REAL_RPC=1</c> are not forced on.
    /// </para>
    /// </summary>
    public static void RealRpc_SyntheticLiveRegister_BindCallHits()
    {
        if (!RealSifRpc.LiveRpcDispatchEnabled())
        {
            Console.WriteLine(
                "[Smoke] RealRpc_SyntheticLiveRegister_BindCallHits SKIP " +
                "(DETPS2_NO_REAL_RPC=1 or DETPS2_IOP_REAL_RPC=0 — live path hard-off)");
            return;
        }

        uint Addiu(uint rt, uint rs, short imm) =>
            (0x09u << 26) | (rs << 21) | (rt << 16) | (ushort)imm;
        uint Lui(uint rt, ushort imm) => (0x0Fu << 26) | (rt << 16) | imm;
        uint Ori(uint rt, uint rs, ushort imm) =>
            (0x0Du << 26) | (rs << 21) | (rt << 16) | imm;
        uint Sw(uint rt, uint baseReg, short off) =>
            (0x2Bu << 26) | (baseReg << 21) | (rt << 16) | (ushort)off;
        uint Jr(uint rs) => (rs << 21) | 0x08u;
        const uint Nop = 0;

        // Proprietary test SID — not in the RealSifRpc allow-list, so HLE would soft-0 +
        // UnknownServiceCalls++ if live dispatch were skipped.
        const uint testSid = 0x4D33452Du; // 'M3E-'
        // Ground-truthed SIFCMD .data chain head (same private const as RealSifRpc).
        const uint sifCmdQueueChainOffset = 0x2a60;
        // Free IOP phys below RealSifRpc scratch (0x1F0000) and above typical IRX loads.
        const uint queuePhys = 0x00190000;
        const uint serverPhys = 0x00190100;
        const uint buffPhys = 0x00190200;
        const uint magic0 = 0xDEADBEEFu;
        const uint magic1 = 0xCAFEBABEu;

        var sys = new Ps2System();
        sys.Hle.EnableSonyKernel();
        var rpc = sys.Hle.Sony!.RealRpc;
        // BindHost already set by SonyKernelHle ctor; re-bind is idempotent and keeps
        // TryDispatchRealRegisteredRpc's _host.Iop path honest if ctor order changes.
        rpc.BindHost(sys);
        var mem = sys.Memory;
        var k = sys.Hle.Kernel;

        // 1) SIFCMD name + image so TryFindRealRpcServer can resolve chain head.
        // 2) LIVETST image hosts the R3000 handler (func must land in a loaded module).
        if (!sys.LoadIrx(IrxLoader.BuildMinimalIrx("SIFCMD"), "SIFCMD").Success)
            throw new Exception("LoadIrx SIFCMD failed");
        if (!sys.LoadIrx(IrxLoader.BuildMinimalIrx("LIVETST"), "LIVETST").Success)
            throw new Exception("LoadIrx LIVETST failed");
        if (!sys.IopModules.TryGetModule("SIFCMD", out int sifId) ||
            !sys.IopModules.TryGetIrx(sifId, out var sifcmd) || !sifcmd.HasImage)
            throw new Exception("SIFCMD module not registered with image");
        if (!sys.IopModules.TryGetModule("LIVETST", out int liveId) ||
            !sys.IopModules.TryGetIrx(liveId, out var liveMod) || !liveMod.HasImage)
            throw new Exception("LIVETST module not registered with image");
        if (liveMod.Size == 0)
            throw new Exception("LIVETST size 0 — IsWithinAnyLoadedModule would reject handler");

        // Handler at module base (EE-mapped LoadBase). SifRpcFunc_t:
        //   void *handler(int fno, void *buf, int size)  — a0/a1/a2
        // Writes magic0/magic1 into *buf, returns buf in v0.
        uint handlerEe = liveMod.LoadBase;
        uint handlerPhys = IopModuleHost.ToIopPhys(handlerEe);
        var handlerWords = new uint[]
        {
            // v0 = a1 (buf)
            Addiu(2, 5, 0),
            // t0 = 0xDEADBEEF; sw t0, 0(a1)
            Lui(8, (ushort)(magic0 >> 16)),
            Ori(8, 8, (ushort)(magic0 & 0xFFFF)),
            Sw(8, 5, 0),
            // t0 = 0xCAFEBABE; sw t0, 4(a1)
            Lui(8, (ushort)(magic1 >> 16)),
            Ori(8, 8, (ushort)(magic1 & 0xFFFF)),
            Sw(8, 5, 4),
            Jr(31),
            Nop
        };
        if (handlerWords.Length * 4 > liveMod.Size)
            throw new Exception("handler larger than LIVETST image floor");
        for (int i = 0; i < handlerWords.Length; i++)
            mem.IopWrite32(handlerPhys + (uint)(i * 4), handlerWords[i]);

        uint buffEe = SystemMemory.IOP_RAM_BASE + buffPhys;
        // Clear reply buffer so a silent HLE soft-0 cannot look like a live hit.
        mem.IopWrite32(buffPhys, 0);
        mem.IopWrite32(buffPhys + 4, 0);

        // Plant SifRpcDataQueue_t + SifRpcServerData_t matching TryFindRealRpcServer:
        //   queue +0x08 = server list head, +0x14 = next queue
        //   server +0x00 = sid, +0x04 = func, +0x08 = buff, +0x38 = next server
        // Func stored EE-mapped so IsWithinAnyLoadedModule (compares to LoadBase) accepts it.
        for (uint o = 0; o < 0x40; o += 4)
        {
            mem.IopWrite32(queuePhys + o, 0);
            mem.IopWrite32(serverPhys + o, 0);
        }
        mem.IopWrite32(queuePhys + 0x08, serverPhys);
        mem.IopWrite32(queuePhys + 0x14, 0);
        mem.IopWrite32(serverPhys + 0x00, testSid);
        mem.IopWrite32(serverPhys + 0x04, handlerEe);
        mem.IopWrite32(serverPhys + 0x08, buffEe);
        mem.IopWrite32(serverPhys + 0x38, 0);
        // SIFCMD global chain head at LoadBase + 0x2a60
        uint chainHead = sifcmd.LoadBase + sifCmdQueueChainOffset;
        mem.IopWrite32(chainHead, queuePhys);

        if (rpc.LiveRpcHits != 0 || rpc.LiveRpcFallbacks != 0)
            throw new Exception("LiveRpc counters must start at 0");

        // --- EE BIND (still HLE plant of cd handles — product dual-path policy) ---
        const uint cd = 0x0000F100;
        const uint bindPkt = 0x0000F000;
        int sema = k.CreateSema(0, 1);
        mem.Write32(cd + 8, (uint)sema);
        mem.Write32(bindPkt + 8, RealSifRpc.CidRpcBind);
        mem.Write32(bindPkt + 16, 1); // PACKET_F_ALLOC
        mem.Write32(bindPkt + 28, cd);
        mem.Write32(bindPkt + 32, testSid);
        ulong unkBindBefore = rpc.UnknownBindSids;
        if (!rpc.TryHandle(mem, k, sys.Cdvd, sys.Pad, sys.IopModules, bindPkt))
            throw new Exception("BIND TryHandle returned false");
        if (rpc.Binds == 0)
            throw new Exception("BIND not counted");
        // Unknown SID on BIND is telemetry-only; still completes (allow-list miss is fine).
        if (rpc.UnknownBindSids < unkBindBefore + 1)
            throw new Exception("test SID should count as UnknownBindSids (not in allow-list)");
        if (mem.Read32(bindPkt + 8) != RealSifRpc.CidRpcEnd)
            throw new Exception("BIND must stamp RPC_END");

        // --- EE CALL through live path ---
        const uint recvBuf = 0x0000F200;
        const uint callPkt = 0x0000F300;
        mem.Write32(recvBuf, 0);
        mem.Write32(recvBuf + 4, 0);
        mem.Write32(callPkt + 8, RealSifRpc.CidRpcCall);
        mem.Write32(callPkt + 16, 1);
        mem.Write32(callPkt + 28, cd);
        mem.Write32(callPkt + 32, 1); // fno
        mem.Write32(callPkt + 36, 0); // send_size (no arg DMA needed)
        mem.Write32(callPkt + 40, recvBuf);
        mem.Write32(callPkt + 44, 8); // recv_size
        ulong unkCallBefore = rpc.UnknownServiceCalls;
        ulong hitsBefore = rpc.LiveRpcHits;
        ulong fbBefore = rpc.LiveRpcFallbacks;

        if (!rpc.TryHandle(mem, k, sys.Cdvd, sys.Pad, sys.IopModules, callPkt))
            throw new Exception("CALL TryHandle returned false");

        if (rpc.LiveRpcHits < hitsBefore + 1)
            throw new Exception(
                $"LiveRpcHits did not increment (got {rpc.LiveRpcHits}, before={hitsBefore}) — " +
                "live registry miss or handler did not return");
        if (rpc.LiveRpcFallbacks != fbBefore)
            throw new Exception($"LiveRpcFallbacks unexpectedly advanced to {rpc.LiveRpcFallbacks}");
        // HLE unknown branch must not have run for this SID.
        if (rpc.UnknownServiceCalls != unkCallBefore)
            throw new Exception(
                $"UnknownServiceCalls advanced ({unkCallBefore}->{rpc.UnknownServiceCalls}) — HLE branch ran");
        if (mem.Read32(callPkt + 8) != RealSifRpc.CidRpcEnd)
            throw new Exception("live CALL must stamp RPC_END");
        if (mem.Read32(recvBuf) != magic0 || mem.Read32(recvBuf + 4) != magic1)
            throw new Exception(
                $"reply bytes wrong: 0x{mem.Read32(recvBuf):X8} 0x{mem.Read32(recvBuf + 4):X8} " +
                $"(want DEADBEEF CAFEBABE from R3000 handler)");
        // Handler also wrote IOP-side buff (same reply source).
        if (mem.IopRead32(buffPhys) != magic0 || mem.IopRead32(buffPhys + 4) != magic1)
            throw new Exception("IOP buff not written by live handler");

        // Determinism: second CALL also hits live and does not grow UnknownServiceCalls.
        mem.Write32(recvBuf, 0);
        mem.Write32(recvBuf + 4, 0);
        mem.Write32(callPkt + 8, RealSifRpc.CidRpcCall);
        mem.Write32(callPkt + 16, 1);
        mem.Write32(callPkt + 28, cd);
        mem.Write32(callPkt + 32, 2);
        mem.Write32(callPkt + 36, 0);
        mem.Write32(callPkt + 40, recvBuf);
        mem.Write32(callPkt + 44, 8);
        ulong hits2 = rpc.LiveRpcHits;
        if (!rpc.TryHandle(mem, k, sys.Cdvd, sys.Pad, sys.IopModules, callPkt))
            throw new Exception("second CALL failed");
        if (rpc.LiveRpcHits < hits2 + 1)
            throw new Exception("second live CALL did not hit");
        if (mem.Read32(recvBuf) != magic0)
            throw new Exception("second CALL reply wrong");
        if (rpc.UnknownServiceCalls != unkCallBefore)
            throw new Exception("UnknownServiceCalls changed after second live CALL");

        Console.WriteLine(
            $"[Smoke] RealRpc_SyntheticLiveRegister_BindCallHits OK " +
            $"(sid=0x{testSid:X8} LiveRpcHits={rpc.LiveRpcHits} " +
            $"handler=0x{handlerEe:X8} sifcmdBase=0x{sifcmd.LoadBase:X8})");
    }
}
