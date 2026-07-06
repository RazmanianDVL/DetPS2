        bool take = false;
        bool link = false;

        switch (rt)
        {
            case 0:  take = (long)_gprs[rs].Lo < 0; break;   // BLTZ
            case 1:  take = (long)_gprs[rs].Lo >= 0; break;  // BGEZ
            case 0x10: take = (long)_gprs[rs].Lo < 0; link = true; break;  // BLTZAL
            case 0x11: take = (long)_gprs[rs].Lo >= 0; link = true; break; // BGEZAL
        }

        if (link)
        {
            _gprs[31].Lo = PC + 4; // link to delay slot
        }

        if (take)
        {
            _pendingBranchTarget = target;
            _branchPending = true;
        }
        return 1;