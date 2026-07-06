    private int ExecuteLwc1(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = _gprs[rs].Lo + (ulong)(long)offset;
        uint bits = _memory.Read32(addr);
        _fprs[rt] = BitConverter.Int32BitsToSingle((int)bits);
        return 1;
    }

    private int ExecuteSwc1(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = _gprs[rs].Lo + (ulong)(long)offset;
        uint bits = (uint)BitConverter.SingleToInt32Bits(_fprs[rt]);
        _memory.Write32(addr, bits);
        return 1;
    }

    private int HandleUnknown(uint opcode)
    {
        Console.WriteLine($"[EE] Unknown opcode 0x{opcode:X8} at PC=0x{PC:X8}");
        return 1;
    }