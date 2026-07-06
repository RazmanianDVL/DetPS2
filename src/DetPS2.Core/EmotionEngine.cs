            case 0x34: // TEQ
            case 0x36: // TNE
            case 0x0C: // SYSCALL (already handled at primary level)
            case 0x0D: // BREAK
                return 1;