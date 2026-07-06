            case 0x0F: // SYNC
                return 1;

            case 0x0B: // MOVN
                if (rd != 0 && _gprs[rt].Lo != 0) _gprs[rd].Lo = _gprs[rs].Lo;
                return 1;

            case 0x0A: // MOVZ
                if (rd != 0 && _gprs[rt].Lo == 0) _gprs[rd].Lo = _gprs[rs].Lo;
                return 1;