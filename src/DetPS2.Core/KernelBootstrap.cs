using System;

namespace DetPS2.Core;

/// <summary>
/// Minimal EE kernel runtime for commercial fast-boot (no full BIOS execution).
/// Installs exception/interrupt vectors and a low-memory trap so games that
/// take IRQs or jump through unset pointers don't thrash on NOP sleds forever.
/// </summary>
public static class KernelBootstrap
{
    public const uint PhysTlbRefill = 0x00000000;
    public const uint PhysCommon = 0x00000180;
    public const uint PhysInterrupt = 0x00000200;
    public const uint PhysRecovery = 0x00008000;

    // kseg0 mirrors
    public const uint Kseg0Tlb = 0x80000000;
    public const uint Kseg0Common = 0x80000180;
    public const uint Kseg0Interrupt = 0x80000200;

    /// <summary>Call after LoadBios + before/after disc ELF boot for commercial titles.</summary>
    public static void InstallCommercialRuntime(Ps2System sys)
    {
        if (sys == null) return;
        // Install first, then lock — protect would block our own vector writes
        sys.Memory.ProtectKernelVectors = false;
        InstallExceptionVectors(sys.Memory);
        InstallLowMemoryTrap(sys.Memory);
        sys.Memory.ProtectKernelVectors = true;
        WireInterrupts(sys);

        // Vectors ready in RAM (BEV=0). Do NOT force TakeExceptions yet:
        // without a full ISR that ACKs INTC, VBlank would storm the EE.
        // Games that install their own handlers via AddIntcHandler can enable later.
        //
        // Real BIOS boot also leaves Status.IM2 (bit10, INTC summary) and IM7 (bit15,
        // Compare/Timer) set by the time it hands off to a game — our fast-boot skips that
        // init sequence, so approximate its end state here. EmotionEngine.SyncInterruptsFromIntc
        // gates delivery on these IM bits matching the pending Cause.IPx bits (real MIPS
        // semantics); a game is still free to mask them back off itself (e.g. around a
        // deliberate INTC_STAT busy-poll) via its own mtc0 Status writes.
        sys.EE.COP0_Status = (sys.EE.COP0_Status & ~(1u << 22)) | (1u << 16) | (1u << 15) | (1u << 10) | 1u;
        sys.EE.TakeExceptions = false;

        // Mask open so software polling of INTC.STAT sees expected sources; delivery is polled via Sync
        sys.Intc.SetMask(
            (1u << (int)Intc.InterruptSource.VBlankStart) |
            (1u << (int)Intc.InterruptSource.VBlankEnd) |
            (1u << (int)Intc.InterruptSource.DmaController) |
            (1u << (int)Intc.InterruptSource.Sif) |
            (1u << (int)Intc.InterruptSource.GS) |
            (1u << (int)Intc.InterruptSource.Timer0));
    }

    public static void InstallExceptionVectors(SystemMemory mem)
    {
        // Common handler @ 0x180 / interrupt @ 0x200 / TLB @ 0x0
        // Strategy: skip faulting insn for non-int; for int just ERET to EPC.
        //
        //   mfc0 k0, Cause     ; k0=$26
        //   andi k0, k0, 0x7c  ; ExcCode field
        //   beq  k0, zero, do_eret  ; interrupt — don't skip
        //   nop
        //   mfc0 k0, EPC
        //   addiu k0, k0, 4
        //   mtc0 k0, EPC
        // do_eret:
        //   eret
        //   nop

        WriteHandler(mem, PhysCommon, skipFaulting: true);
        WriteHandler(mem, PhysInterrupt, skipFaulting: false);
        WriteHandler(mem, PhysTlbRefill, skipFaulting: true);

        // Mirror already via phys==kseg0 & 0x1FFFFFFF for these low addresses
    }

    private static void WriteHandler(SystemMemory mem, uint phys, bool skipFaulting)
    {
        uint p = phys;
        // mfc0 $k0, $13 (Cause) — COP0 rs=0 rt=26 rd=13
        Write32(mem, p, Cop0Mfc(26, 13)); p += 4;
        // andi $k0, $k0, 0x7C
        Write32(mem, p, Andi(26, 26, 0x7C)); p += 4;
        if (skipFaulting)
        {
            // beq $k0, $zero, +4 insns (to eret path without skip) — if ExcCode==0, skip the skip
            // From here: nop, mfc0, addiu, mtc0, [ack block], eret. Both paths converge on the
            // ack block right before eret (word 7) — see AckIntc's own doc comment for why. The
            // branch's target word index (7) is unchanged by inserting the ack block there (it
            // just replaces what used to be "eret" at that slot, pushing the real eret to
            // word 11), so this offset (+4) still lands correctly with no recalculation needed.
            // Layout:
            // 0: mfc0 cause
            // 1: andi
            // 2: beq k0,0,+4  -> target = 2+1+4 = 7
            // 3: nop
            // 4: mfc0 epc
            // 5: addiu epc,4
            // 6: mtc0 epc
            // 7..10: ack block (4 words)
            // 11: eret
            // 12: nop
            Write32(mem, p, Beq(26, 0, 4)); p += 4;
            Write32(mem, p, 0); p += 4; // nop delay
            // mfc0 k0, EPC (rd=14)
            Write32(mem, p, Cop0Mfc(26, 14)); p += 4;
            // addiu k0, k0, 4
            Write32(mem, p, Addiu(26, 26, 4)); p += 4;
            // mtc0 k0, EPC
            Write32(mem, p, Cop0Mtc(26, 14)); p += 4;
            p = AckIntc(mem, p);
            // eret
            Write32(mem, p, Eret()); p += 4;
            Write32(mem, p, 0); // nop after eret
        }
        else
        {
            // Interrupt: ack INTC, then ERET
            p = AckIntc(mem, p);
            Write32(mem, p, Eret()); p += 4;
            Write32(mem, p, 0);
        }
    }

    /// <summary>
    /// Real PS2 kernels always have at least a baseline VBlank/timer service routine installed
    /// from boot — even before a game calls AddIntcHandler for its own subsystems, SOMETHING in
    /// the real kernel's default dispatch always acknowledges INTC (clears I_STAT), or every
    /// later interrupt would immediately re-fire the instant IE is set, permanently starving real
    /// code. Our fast-boot deliberately skips full BIOS/kernel init (see InstallCommercialRuntime's
    /// own comment: "without a full ISR that ACKs INTC, VBlank would storm the EE") and originally
    /// relied on TakeExceptions staying false until a real handler was installed — but both
    /// KickMidwayMainPath's forced COP0_Status write and real CRT0's own `ei` enable interrupts
    /// well before any such handler exists, exactly triggering the storm the comment predicted.
    /// Confirmed directly (2026-07-26): DETPS2_TRACE_INTC=1 shows VBlankStart raised only 3 times
    /// in ~1M cycles (a perfectly normal rate), with the second raise already alreadyRaised=True —
    /// the first was never acknowledged, so the CPU re-enters the exception vector every ~64
    /// cycles forever, unable to make real progress (see DEVELOPER_GUIDE.md §7.4).
    ///
    /// Fix: give our own synthesized vector this same baseline behavior — read the real INTC
    /// I_STAT register and write the same value back (write-1-to-clear is real INTC hardware
    /// semantics, so this acks exactly the bits that were set, nothing else) right before every
    /// eret. Uses $k0/$k1 ($26/$27), the MIPS-reserved kernel-scratch registers already used by
    /// the rest of this handler, so nothing else needs saving/restoring.
    /// </summary>
    private static uint AckIntc(SystemMemory mem, uint p)
    {
        // lui k1, hi16(Intc.AddrStat) ; ori k1, k1, lo16(Intc.AddrStat)
        Write32(mem, p, Lui(27, Intc.AddrStat >> 16)); p += 4;
        Write32(mem, p, Ori(27, 27, Intc.AddrStat & 0xFFFF)); p += 4;
        // lw k0, 0(k1) ; sw k0, 0(k1)  — read-then-write-back-same-value = ack whatever's pending
        Write32(mem, p, Lw(26, 27, 0)); p += 4;
        Write32(mem, p, Sw(26, 27, 0)); p += 4;
        return p;
    }

    /// <summary>
    /// Fill low RAM with a trampoline so accidental jumps to 0x0..0x7FFF recover.
    /// </summary>
    public static void InstallLowMemoryTrap(SystemMemory mem)
    {
        // Recovery routine at PhysRecovery:
        //   mfc0 k0, EPC
        //   addiu k0, 4
        //   mtc0 k0, EPC  
        //   eret
        // If EPC is also low/bad, jump to a safe spin that waits for IRQ
        uint p = PhysRecovery;
        Write32(mem, p, Cop0Mfc(26, 14)); p += 4;
        Write32(mem, p, Addiu(26, 26, 4)); p += 4;
        Write32(mem, p, Cop0Mtc(26, 14)); p += 4;
        Write32(mem, p, Eret()); p += 4;
        Write32(mem, p, 0);

        // Every 16 bytes in 0x0..0x7FF0: j recovery; nop
        // j target: (2<<26) | ((target>>2)&0x3FFFFFF) — same 256MB region
        uint jRec = (2u << 26) | ((PhysRecovery >> 2) & 0x03FFFFFF);
        for (uint a = 0; a < PhysRecovery; a += 8)
        {
            // Don't overwrite exception vectors we just wrote
            if (a < 0x280) continue;
            Write32(mem, a, jRec);
            Write32(mem, a + 4, 0);
        }
    }

    public static void WireInterrupts(Ps2System sys)
    {
        // EE already registered SyncInterruptsFromIntc via SetIntc — don't replace notify.
        sys.EE.SyncInterruptsFromIntc();
    }

    /// <summary>
    /// If the EE is stuck in low memory / exception vectors, resume at last good game PC.
    /// </summary>
    public static void RescueIfLostInLowMem(Ps2System sys, ulong lastGoodPc = 0)
    {
        // Legitimate KSEG0 exception-vector execution (interrupt/syscall/exception dispatch —
        // see EmotionEngine.GetExceptionVector: 0x80000000/0x80000180/0x80000200, the common
        // non-BEV vectors) is NOT "lost." The check below used to run unconditionally on the
        // masked `PC & 0x1FFFFFFF`, which collapses these KSEG0 addresses to tiny physical
        // offsets (0x80000200 -> 0x200) that trivially fail the "in RDRAM" test below — meaning
        // this safety net could fire (and it did, confirmed via MK Shaolin Monks) WHILE the CPU
        // was legitimately mid-exception-handler, between vector entry and its own `eret`. That
        // forcibly clears COP0 EXL/ERL and overwrites PC with a locally-recomputed "resume"
        // guess *before* eret's own proper unwind runs, sending execution to a garbage address
        // instead of wherever eret would have actually returned to.
        if (sys.EE.PC >= 0x80000000UL && sys.EE.PC < 0x80001000UL) return;

        ulong pcPhys = sys.EE.PC & 0x1FFFFFFFUL;
        // Game code for commercial titles lives in RDRAM (1MB .. 32MB)
        if (pcPhys >= 0x00100000UL && pcPhys < SystemMemory.RDRAM_SIZE) return;

        // Stuck in vector page, low trap, recovery trampoline, or unmapped high garbage
        ulong resume = lastGoodPc;
        ulong epcPhys = sys.EE.COP0_EPC & 0x1FFFFFFFUL;
        if (epcPhys >= 0x00100000UL && epcPhys < 0x02000000UL)
            resume = sys.EE.COP0_EPC + 4;

        static bool LooksLikeCode(SystemMemory mem, ulong addr)
        {
            uint p = (uint)(addr & 0x1FFFFFFFUL);
            if (p < 0x00100000u || p >= 0x02000000u) return false;
            uint op = mem.Read32(p);
            if (op == 0) return false; // nop-sled / empty
            // Reject pure data-looking words in high BSS
            if (p >= 0x00600000u && (op & 0xFC000000) == 0 && (op & 0x3F) == 0)
                return false;
            return true;
        }

        if (!LooksLikeCode(sys.Memory, resume))
        {
            if (LooksLikeCode(sys.Memory, lastGoodPc))
                resume = lastGoodPc;
            else if (sys.Memory.Read32(0x00212F70) == 0x27BDFEE0)
                resume = 0x00212F70UL;
            else
                resume = 0x0011C250UL;
        }

        // Ensure a usable stack if SP was wiped by a bad jump
        ulong sp = sys.EE.GetGpr(29).Lo;
        if ((sp & 0x1FFFFFFFUL) < 0x00100000UL || (sp & 0x1FFFFFFFUL) >= 0x02000000UL)
            sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x01FF0000 });

        sys.EE.COP0_Status &= ~0x6u; // clear EXL|ERL
        sys.EE.PC = resume;
        sys.EE.SyncInterruptsFromIntc();

        // Keep vectors healthy (game may have stomped them)
        bool prev = sys.Memory.ProtectKernelVectors;
        sys.Memory.ProtectKernelVectors = false;
        InstallExceptionVectors(sys.Memory);
        InstallLowMemoryTrap(sys.Memory);
        sys.Memory.ProtectKernelVectors = prev;
    }

    // ---- MIPS encoding helpers ----
    private static void Write32(SystemMemory mem, uint addr, uint value) =>
        mem.Write32(addr, value);

    private static uint Cop0Mfc(uint rt, uint rd) =>
        (0x10u << 26) | (0u << 21) | (rt << 16) | (rd << 11);

    private static uint Cop0Mtc(uint rt, uint rd) =>
        (0x10u << 26) | (4u << 21) | (rt << 16) | (rd << 11);

    private static uint Eret() => (0x10u << 26) | (0x10u << 21) | 0x18;

    private static uint Andi(uint rt, uint rs, uint imm) =>
        (0x0Cu << 26) | (rs << 21) | (rt << 16) | (imm & 0xFFFF);

    private static uint Addiu(uint rt, uint rs, int imm) =>
        (0x09u << 26) | (rs << 21) | (rt << 16) | ((uint)imm & 0xFFFF);

    private static uint Beq(uint rs, uint rt, int offsetInsns) =>
        (0x04u << 26) | (rs << 21) | (rt << 16) | ((uint)offsetInsns & 0xFFFF);

    private static uint Lui(uint rt, uint imm) =>
        (0x0Fu << 26) | (rt << 16) | (imm & 0xFFFF);

    private static uint Ori(uint rt, uint rs, uint imm) =>
        (0x0Du << 26) | (rs << 21) | (rt << 16) | (imm & 0xFFFF);

    private static uint Lw(uint rt, uint rs, int offset) =>
        (0x23u << 26) | (rs << 21) | (rt << 16) | ((uint)offset & 0xFFFF);

    private static uint Sw(uint rt, uint rs, int offset) =>
        (0x2Bu << 26) | (rs << 21) | (rt << 16) | ((uint)offset & 0xFFFF);
}
