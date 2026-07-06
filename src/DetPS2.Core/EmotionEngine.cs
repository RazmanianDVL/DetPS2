    /// <summary>
    /// Execute one instruction with correct MIPS delay slot semantics.
    /// Branch/jump instructions execute their delay slot before taking the branch.
    /// </summary>
    public int Step()
    {
        ulong thisPC = PC;
        uint opcode = _memory.Read32(thisPC);

        // Pre-advance past the current instruction
        PC += 4;

        int cycles = Dispatch(opcode);

        if (_branchPending)
        {
            // Execute the delay slot instruction (now at current PC)
            uint delaySlotOpcode = _memory.Read32(PC);
            PC += 4;
            Dispatch(delaySlotOpcode); // ignore its cycle count for simplicity

            // Now take the branch/jump
            PC = _pendingBranchTarget;
            _branchPending = false;
        }

        return cycles;
    }

    private int Dispatch(uint opcode)
    {
        uint primary = (opcode >> 26) & 0x3F;

        return primary switch
        {
            0x00 => ExecuteSpecial(opcode),
            0x01 => ExecuteRegimm(opcode),
            0x02 => ExecuteJ(opcode),
            0x03 => ExecuteJal(opcode),
            0x04 => ExecuteBeq(opcode),
            0x05 => ExecuteBne(opcode),
            0x06 => ExecuteBlez(opcode),
            0x07 => ExecuteBgtz(opcode),
            0x08 => ExecuteAddi(opcode),
            0x09 => ExecuteAddiu(opcode),
            0x0A => ExecuteSlti(opcode),
            0x0B => ExecuteSltiu(opcode),
            0x0C => ExecuteSyscall(opcode),
            0x0D => ExecuteOri(opcode),
            0x0E => ExecuteXori(opcode),
            0x0F => ExecuteLui(opcode),
            0x10 => ExecuteCop0(opcode),
            0x11 => ExecuteCop1(opcode),
            0x12 => ExecuteCop2(opcode),
            0x20 => ExecuteLb(opcode),
            0x21 => ExecuteLh(opcode),
            0x22 => ExecuteLwl(opcode),
            0x23 => ExecuteLw(opcode),
            0x24 => ExecuteLbu(opcode),
            0x25 => ExecuteLhu(opcode),
            0x26 => ExecuteLwr(opcode),
            0x28 => ExecuteSb(opcode),
            0x29 => ExecuteSh(opcode),
            0x2A => ExecuteSwl(opcode),
            0x2B => ExecuteSw(opcode),
            0x2E => ExecuteSwr(opcode),
            0x31 => ExecuteLwc1(opcode),
            0x39 => ExecuteSwc1(opcode),
            _ => HandleUnknown(opcode)
        };
    }