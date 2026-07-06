    private int ExecuteRegimm(uint opcode)
    {
        uint rt = (opcode >> 16) & 0x1F;
        uint rs = (opcode >> 21) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong target = PC + (ulong)((long)offset << 2);  // PC already advanced

        bool take = rt switch
        {
            0 => (long)_gprs[rs].Lo < 0,   // BLTZ
            1 => (long)_gprs[rs].Lo >= 0,  // BGEZ
            _ => false
        };

        if (take)
        {
            _pendingBranchTarget = target;
            _branchPending = true;
        }
        return 1;
    }