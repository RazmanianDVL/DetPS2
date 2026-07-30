from pathlib import Path

p = Path('src/DetPS2.Core/GameQuirks/GodOfWarAssist.cs')
text = p.read_text(encoding='utf-8')

# 1) Step: inject soft tick advance
old_step_start = '''    public void Step(Ps2System sys)
    {
        ulong c = sys.Scheduler.MasterCycles;

        // Re-plant after ELF load (PT_LOAD overwrites OnDiscMounted plants).
'''
new_step_start = '''    public void Step(Ps2System sys)
    {
        ulong c = sys.Scheduler.MasterCycles;

        // Keep software tick moving every Step after early boot — VBlank handler at
        // 0x182F28 only runs when INTC fires; busy-wait paths disable progress otherwise.
        if (c >= 30_000_000 && (c % 50_000) < 5_000)
            AdvanceSoftTick(sys, minTarget: 0);

        // Re-plant after ELF load (PT_LOAD overwrites OnDiscMounted plants).
'''
if 'AdvanceSoftTick(sys, minTarget: 0);' not in text.split('public void Step', 1)[1][:800]:
    if old_step_start not in text:
        raise SystemExit('step start not found')
    text = text.replace(old_step_start, new_step_start, 1)
    print('step tick advance inserted')
else:
    print('step tick advance already present')

old_spin = '''        // Software delay + flag poll — two sibling sites share *0x29C7D0:
        //   0x17A328..0x17A35C (live PC 0x17A334): countdown 20000 while *flag==1
        //   0x183880..0x1838C8 (wave-2 profiler #1: countdown 20000 while flag==1)
        // Flag stuck at 1 → forever. Clear AND snap past the outer beq (not just mid-count).
        if (c >= 38_000_000
            && (pc is >= 0x0017A320 and <= 0x0017A360
                || pc is >= 0x00183880 and <= 0x001838C8))
        {
            uint fl = sys.Memory.Read32(0x0029C7D0);
            if (fl == 1 || fl != 0)
                sys.Memory.Write32(0x0029C7D0, 0);
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // v0
            sys.EE.SetGpr(3, new EmotionEngine.Gpr128 { Lo = 0 }); // v1 != 1 so outer exits
            if (pc is >= 0x00183880 and <= 0x001838C8)
            {
                // 0x1838C8 is jr ra — only take it when $ra is real code.
                uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
                if (sys.Memory.IsLikelyEeCode(ra) && ra is >= 0x00100000 and < 0x002C0000
                    && ra is not (>= 0x00183880 and <= 0x001838D0))
                    sys.EE.PC = 0x001838C8; // jr ra
                else
                    sys.EE.PC = PickSafeResume(sys, 0x0026C0EC);
                sys.EE.COP0_Status &= ~0x6u;
            }
            else
            {
                // Skip past beq flag,1,0x17A328 — land on post-loop body at 0x17A360.
                // Mid-count snap to 0x17A350 re-reads flag via a2 and can restart if a2 bad.
                sys.EE.PC = 0x0017A360;
                sys.EE.COP0_Status &= ~0x6u;
            }
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (c % 10_000_000) < 50_000)
                Console.Error.WriteLine($"[GOW] exit spin @0x{pc:X8} flag was {fl} cyc={c}");
        }
'''

new_spin = '''        // Tick-wait leaf 0x17A1D0 (PcProfiler #1 @ 0x17A204): while *0x29C7D4 < a0
        // busy-delay 2000. Tick stuck at 0 → forever, starving FILEIO past IRX (cdvd=142).
        if (c >= 35_000_000 && pc is >= 0x0017A1D0 and <= 0x0017A294)
            TryEscapeTickWait(sys, pc, c);

        // Software delay + flag poll — *0x29C7D0. Clear flag, advance tick, land at 0x17A360
        // so jal 0x17A1D0 still runs with tick already satisfied.
        if (c >= 35_000_000
            && (pc is >= 0x0017A320 and <= 0x0017A370
                || pc is >= 0x00183880 and <= 0x001838C8))
        {
            uint fl = sys.Memory.Read32(SoftSpinFlagPtr);
            if (fl == 1 || fl != 0)
                sys.Memory.Write32(SoftSpinFlagPtr, 0);
            AdvanceSoftTick(sys, minTarget: 0);
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // v0
            sys.EE.SetGpr(3, new EmotionEngine.Gpr128 { Lo = 0 }); // v1 != 1 so outer exits
            if (pc is >= 0x00183880 and <= 0x001838C8)
            {
                uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
                if (sys.Memory.IsLikelyEeCode(ra) && ra is >= 0x00100000 and < 0x002C0000
                    && ra is not (>= 0x00183880 and <= 0x001838D0))
                    sys.EE.PC = 0x001838C8; // jr ra
                else
                    sys.EE.PC = PickSafeResume(sys, 0x0026C0EC);
                sys.EE.COP0_Status &= ~0x6u;
            }
            else
            {
                sys.EE.PC = 0x0017A360;
                sys.EE.COP0_Status &= ~0x6u;
            }
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (c % 10_000_000) < 50_000)
                Console.Error.WriteLine($"[GOW] exit spin @0x{pc:X8} flag was {fl} cyc={c}");
        }
'''

if old_spin in text:
    text = text.replace(old_spin, new_spin, 1)
    print('spin patched')
elif 'TryEscapeTickWait(sys, pc, c)' in text:
    print('spin already patched')
else:
    raise SystemExit('spin block not found')

methods = '''
    /// <summary>
    /// Bump software tick *0x29C7D4 so wait leaf 0x17A1D0 can exit.
    /// Ensures *0x29C664 != 0 for the fast clear+return path.
    /// </summary>
    private static void AdvanceSoftTick(Ps2System sys, uint minTarget)
    {
        uint tick = sys.Memory.Read32(SoftTickPtr);
        uint next = tick + 1u;
        if (minTarget != 0 && next < minTarget)
            next = minTarget;
        if (next == 0 || next > 0x7FFF_FFF0u)
            next = minTarget != 0 ? minTarget : 1u;
        sys.Memory.Write32(SoftTickPtr, next);
        if (sys.Memory.Read32(SoftTickFastPtr) == 0)
            sys.Memory.Write32(SoftTickFastPtr, 1);
    }

    /// <summary>
    /// Tick-wait leaf 0x17A1D0: while *0x29C7D4 &lt; a0 busy-delay 2000.
    /// Snap to jr ra at 0x17A294 WITHOUT zeroing tick (0x17A290 zeros it → re-stall).
    /// </summary>
    private void TryEscapeTickWait(Ps2System sys, uint pc, ulong c)
    {
        if (pc is >= 0x0017A294 and <= 0x0017A298)
            return;
        if (_tickWaitEscapes >= 1024)
            return;

        uint a0 = (uint)sys.EE.GetGpr(4).Lo;
        uint tick = sys.Memory.Read32(SoftTickPtr);
        uint target = a0 == 0xFFFFFFFFu ? tick + 1u : a0;
        bool midDelay = pc is >= 0x0017A1FC and <= 0x0017A288;
        bool unsatisfied = tick < target || (target == 0 && midDelay);
        if (!unsatisfied && !midDelay)
            return;

        if (target == 0) target = 1;
        AdvanceSoftTick(sys, minTarget: target);
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.PC = 0x0017A294; // jr ra — do not take 0x17A290 (zeros tick)
        sys.EE.COP0_Status &= ~0x6u;
        _tickWaitEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && _tickWaitEscapes <= 24)
            Console.Error.WriteLine(
                $"[GOW] escape tick-wait pc=0x{pc:X8} a0=0x{a0:X8} tick was 0x{tick:X8} " +
                $"-> 0x17A294 n={_tickWaitEscapes} cyc={c}");
    }

'''

if 'private static void AdvanceSoftTick' not in text:
    marker = '    /// <summary>\n    /// Plant a null-terminated free-range node at <c>*0x29BEB0</c>'
    if marker not in text:
        raise SystemExit('PlantGlobalFreeHead marker missing')
    text = text.replace(marker, methods + marker, 1)
    print('methods inserted')
else:
    print('methods already present')

old_alloc = '''    private uint AllocArenaBlock(Ps2System sys, uint minSize = HeapBlockSize)
    {
        uint size = minSize < HeapBlockSize ? HeapBlockSize : (minSize + 0xFu) & ~0xFu;
        if (_arenaBump < HeapArenaBase || _arenaBump >= HeapArenaBase + HeapArenaBytes)
            _arenaBump = HeapArenaBase;
        if (_arenaBump + size > HeapArenaBase + HeapArenaBytes)
            _arenaBump = HeapArenaBase; // wrap — better than returning header
        uint block = _arenaBump;
        _arenaBump += size;
        uint zeroLen = size > 0x200u ? 0x200u : size;
        for (uint o = 0; o < zeroLen; o += 4)
            sys.Memory.Write32(block + o, 0);
        // Self-link first word so naïve list walks terminate (cursor == next).
        sys.Memory.Write32(block, block);
        return block;
    }
'''
new_alloc = '''    private uint AllocArenaBlock(Ps2System sys, uint minSize = HeapBlockSize)
    {
        const uint rdramEnd = (uint)SystemMemory.RDRAM_SIZE; // 0x02000000
        uint arenaEnd = HeapArenaBase + HeapArenaBytes;
        if (arenaEnd > rdramEnd - 0x40u)
            arenaEnd = rdramEnd - 0x40u;
        uint size = minSize < HeapBlockSize ? HeapBlockSize : (minSize + 0xFu) & ~0xFu;
        if (size > 0x1000u) size = 0x1000u;
        if (_arenaBump < HeapArenaBase || _arenaBump >= arenaEnd)
            _arenaBump = HeapArenaBase;
        if (_arenaBump + size > arenaEnd)
            _arenaBump = HeapArenaBase;
        uint block = _arenaBump;
        _arenaBump += size;
        if (block < 0x00100000u || block + size > rdramEnd)
        {
            block = HeapArenaBase;
            _arenaBump = HeapArenaBase + size;
        }
        uint zeroLen = size > 0x200u ? 0x200u : size;
        for (uint o = 0; o < zeroLen; o += 4)
            sys.Memory.Write32(block + o, 0);
        sys.Memory.Write32(block, block);
        return block;
    }
'''
if old_alloc in text:
    text = text.replace(old_alloc, new_alloc, 1)
    print('alloc patched')
else:
    print('alloc already patched or different')

if 'if (_free2Escapes >= 96)' in text:
    text = text.replace(
        'if (_free2Escapes >= 96)\n            return;',
        '// Uncapped soft-escape — permanent .text plant regressed RPC/dmac.\n'
        '        if (_free2Escapes >= 100000)\n            return;',
        1)
    print('freelist cap raised')

old_res = '''        if (c >= 40_000_000 && sys.Cdvd.SectorsRead > 0 && sys.Gs.PixelsWritten == 0
            && pc is >= 0x00239300 and <= 0x002396EF
            && (_free2Escapes < 96))
            TryEscapeSecondaryFreelist(sys, pc, c);'''
new_res = '''        if (c >= 40_000_000 && sys.Cdvd.SectorsRead > 0 && sys.Gs.PixelsWritten == 0
            && pc is >= 0x00239300 and <= 0x00239810)
            TryEscapeSecondaryFreelist(sys, pc, c);'''
if old_res in text:
    text = text.replace(old_res, new_res, 1)
    print('residual window patched')

old_kick = '''        if (c - _lastWorldKickCyc < 200_000) return;
        _lastWorldKickCyc = c;
        if (_worldKickPulses >= 768) return;
        _worldKickPulses++;

        // Live final PC 0x26C0E0:'''
new_kick = '''        if (c - _lastWorldKickCyc < 200_000) return;
        _lastWorldKickCyc = c;
        if (_worldKickPulses >= 768) return;
        _worldKickPulses++;

        AdvanceSoftTick(sys, minTarget: 0);
        sys.Memory.Write32(SoftSpinFlagPtr, 0);
        if (pc is >= 0x0017A1D0 and <= 0x0017A294)
            TryEscapeTickWait(sys, pc, c);
        if (pc is >= 0x0017A320 and <= 0x0017A35C)
        {
            sys.EE.PC = 0x0017A360;
            sys.EE.COP0_Status &= ~0x6u;
        }

        // Live final PC 0x26C0E0:'''
if old_kick in text:
    text = text.replace(old_kick, new_kick, 1)
    print('world kick patched')

old_null = '''        // After many hits: return block via epilogue (0x23AA28) — still never the header.
        if (_heapNullEscapes > 32)
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = block });
            sys.EE.PC = 0x0023AA28;
        }'''
new_null = '''        // After few hits / synthetic header thrash: return via epilogue (never header).
        if (_heapNullEscapes > 8 || onSynthetic)
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = block });
            sys.EE.PC = 0x0023AA28;
            sys.EE.COP0_Status &= ~0x6u;
        }'''
if old_null in text:
    text = text.replace(old_null, new_null, 1)
    print('null heap patched')

p.write_text(text, encoding='utf-8')
print('wrote', p)

ps = Path('src/DetPS2.Core/Ps2System.cs')
pt = ps.read_text(encoding='utf-8')
old_hot = '''                bool gowHot = ActiveQuirk is GodOfWarAssist && pcPhys is
                    (>= 0x0015F2C0UL and <= 0x0015FA80UL)
                    or (>= 0x001312C0UL and <= 0x001312F0UL)  // link-search thrash
                    or (>= 0x00293C00UL and <= 0x00293C80UL)  // WaitSema empty SIF poll
                    or (>= 0x0023A900UL and <= 0x0023AA30UL)  // null freelist thrash
                    or (>= 0x002C0000UL and <= 0x02000000UL)  // data/heap as PC
                    or (>= 0x00183880UL and <= 0x001838D0UL)
                    or (>= 0x0017A320UL and <= 0x0017A360UL)
                    or (>= 0x00233AD0UL and <= 0x00233B44UL)
                    or (>= 0x00284780UL and <= 0x002848B0UL)
                    or (>= 0x0021FF00UL and <= 0x00220600UL)
                    or (>= 0x0013DED0UL and <= 0x0013DEF8UL)
                    or (>= 0x0013E1C0UL and <= 0x0013E1F4UL)  // global free-search circular
                    or (>= 0x80000180UL and <= 0x80000200UL);'''
new_hot = '''                bool gowHot = ActiveQuirk is GodOfWarAssist && pcPhys is
                    (>= 0x0015F2C0UL and <= 0x0015FA80UL)
                    or (>= 0x001312C0UL and <= 0x001312F0UL)  // link-search thrash
                    or (>= 0x00293C00UL and <= 0x00293C80UL)  // WaitSema empty SIF poll
                    or (>= 0x00239300UL and <= 0x00239810UL)  // secondary freelist thrash
                    or (>= 0x0023A900UL and <= 0x0023AA30UL)  // null freelist thrash
                    or (>= 0x002C0000UL and <= 0x02000000UL)  // data/heap as PC
                    or (>= 0x00183880UL and <= 0x001838D0UL)
                    or (>= 0x0017A1D0UL and <= 0x0017A298UL)  // soft-tick wait leaf (*0x29C7D4)
                    or (>= 0x0017A320UL and <= 0x0017A37CUL)  // flag spin + jal tick-wait
                    or (>= 0x00233AD0UL and <= 0x00233B44UL)
                    or (>= 0x00284780UL and <= 0x002848B0UL)
                    or (>= 0x0021FF00UL and <= 0x00220600UL)
                    or (>= 0x0013DED0UL and <= 0x0013DEF8UL)
                    or (>= 0x0013E1C0UL and <= 0x0013E1F4UL)  // global free-search circular
                    or (>= 0x80000180UL and <= 0x80000200UL);'''
if old_hot in pt:
    pt = pt.replace(old_hot, new_hot, 1)
    ps.write_text(pt, encoding='utf-8')
    print('Ps2System gowHot patched')
elif '0x0017A1D0UL' in pt:
    print('Ps2System already patched')
else:
    print('Ps2System pattern not found')
