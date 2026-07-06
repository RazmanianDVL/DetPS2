    private int ExecuteJ(uint opcode)
    {
        uint target = opcode & 0x03FFFFFF;
        _pendingBranchTarget = (PC & 0xF0000000UL) | ((ulong)target << 2);
        _branchPending = true;
        return 1;
    }

    private int ExecuteJal(uint opcode)
    {
        _gprs[31].Lo = PC + 4; // $ra (delay slot address)
        uint target = opcode & 0x03FFFFFF;
        _pendingBranchTarget = (PC & 0xF0000000UL) | ((ulong)target << 2);
        _branchPending = true;
        return 1;
    }

    private int ExecuteBeq(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        if (_gprs[rs].Lo == _gprs[rt].Lo)
        {
            _pendingBranchTarget = PC + (ulong)((long)offset << 2);
            _branchPending = true;
        }
        return 1;
    }

    private int ExecuteBne(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        if (_gprs[rs].Lo != _gprs[rt].Lo)
        {
            _pendingBranchTarget = PC + (ulong)((long)offset << 2);
            _branchPending = true;
        }
        return 1;
    }

    private int ExecuteBlez(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        if ((long)_gprs[rs].Lo <= 0)
        {
            _pendingBranchTarget = PC + (ulong)((long)offset << 2);
            _branchPending = true;
        }
        return 1;
    }

    private int ExecuteBgtz(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        if ((long)_gprs[rs].Lo > 0)
        {
            _pendingBranchTarget = PC + (ulong)((long)offset << 2);
            _branchPending = true;
        }
        return 1;
    }