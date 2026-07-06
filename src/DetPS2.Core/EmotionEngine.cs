    private int ExecuteSyscall(uint opcode)
    {
        COP0_EPC = PC - 4; // point to syscall instruction
        COP0_Cause = (8 << 2);

        uint syscallNumber = (uint)(_gprs[4].Lo & 0xFFFF);

        switch (syscallNumber)
        {
            case 0x01:
                HleSifInitialized = true;
                _gprs[2].Lo = 0;
                break;

            case 0x02: case 0x03: case 0x04: case 0x05: case 0x06: case 0x07: case 0x08: case 0x09: case 0x0A: case 0x0B: case 0x0C:
            case 0x10: case 0x11: case 0x12: case 0x13: case 0x14: case 0x15: case 0x16: case 0x17: case 0x18: case 0x19:
            case 0x20: case 0x21: case 0x22: case 0x23: case 0x24: case 0x25: case 0x26: case 0x27: case 0x30: case 0x40:
            case 0x50: case 0x60: case 0x61: case 0x70: case 0x71:
            case 0x80: case 0x81: case 0x90: case 0x91: case 0xA0: case 0xA1: case 0xB0: case 0xB1: case 0xC0: case 0xC1:
                _gprs[2].Lo = 0;
                break;

            default:
                Console.WriteLine($"[EE HLE] Unhandled syscall 0x{syscallNumber:X}");
                _gprs[2].Lo = 0;
                break;
        }

        _pendingBranchTarget = 0x80000180;
        _branchPending = true;
        return 1;
    }

    private int ExecuteCop0(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rd = (opcode >> 11) & 0x1F;
        uint function = opcode & 0x3F;

        if (rs == 0x10)
        {
            switch (function)
            {
                case 0x18: // ERET (very important for BIOS)
                    PC = COP0_EPC;
                    COP0_Status &= ~(1u << 1); // clear EXL
                    _branchPending = false;
                    return 1;
            }
        }
        return 1;
    }

    private int ExecuteCop1(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        uint rd = (opcode >> 11) & 0x1F;
        uint function = opcode & 0x3F;

        switch (rs)
        {
            case 0x00: // MFC1
                if (rt != 0) _gprs[rt].Lo = (uint)BitConverter.SingleToInt32Bits(_fprs[rd]);
                return 1;

            case 0x04: // MTC1
                _fprs[rd] = BitConverter.Int32BitsToSingle((int)_gprs[rt].Lo);
                return 1;

            case 0x10: // Single precision
                switch (function)
                {
                    case 0x00: _fprs[rd] = _fprs[rs] + _fprs[rt]; break; // ADD.S
                    case 0x01: _fprs[rd] = _fprs[rs] - _fprs[rt]; break; // SUB.S
                    case 0x02: _fprs[rd] = _fprs[rs] * _fprs[rt]; break; // MUL.S
                    case 0x03: _fprs[rd] = (_fprs[rt] != 0f) ? _fprs[rs] / _fprs[rt] : 0f; break; // DIV.S
                    case 0x06: _fprs[rd] = _fprs[rs]; break; // MOV.S
                    case 0x07: _fprs[rd] = -_fprs[rs]; break; // NEG.S
                }
                return 1;
        }
        return 1;
    }

    private int ExecuteCop2(uint opcode)
    {
        uint function = opcode & 0x3F;
        uint rt = (opcode >> 16) & 0x1F;
        uint rd = (opcode >> 11) & 0x1F;
        uint sa = (opcode >> 6) & 0x1F;
        uint rs = (opcode >> 21) & 0x1F;

        _vu0.ExecuteVuInstruction(function, rs, rt, rd, sa);
        return 1;
    }