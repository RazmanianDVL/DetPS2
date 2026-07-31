"""Wave-4 SM: fix sm+0x28 jalr poison; prefill desc buffers; arena + early escape."""
from pathlib import Path

path = Path("src/DetPS2.Core/MidwayBootAssist.cs")
text = path.read_text(encoding="utf-8")

# --- constants ---
if "ResourceArenaBase" not in text:
    needle = """    private const uint ResourceHeapTable = 0x0065E998;

    private bool _worklistPlanted;"""
    insert = """    private const uint ResourceHeapTable = 0x0065E998;
    /// <summary>Wave-4: early abort if force PC leaves target bands (AdEL rehome).</summary>
    private const ulong ResourceBindEscapeCyc = 250_000;
    // Wave-4: scratch arena + EE bump-alloc stubs for stream-manager allocator slots.
    // FUN_0043BE08 does jalr *(sm+0x28) with a0=*(sm+0x30); FUN_0043BE98 jalr *(sm+0x2C).
    // Wave-3 planted 0x100000 into +0x28/+0x2C as "capacity" — that is a jalr target, not a size
    // (live: AdEL @0x1FFFFFFF during force 43B670 → TIMEOUT, slot0 empty).
    private const uint ResourceArenaBase = 0x00C00000;
    private const uint ResourceArenaSize = 0x00800000; // 8 MiB
    private const uint ResourceAllocStub = 0x01FE0100;  // EE bump-alloc code
    private const uint ResourceAllocCtx = 0x01FE0200;   // {cursor, end}
    private const uint ResourceDescBufSize = 0x00600000; // 6 MiB descriptor bump region

    private bool _worklistPlanted;
    private bool _resourceArenaReady;"""
    if needle not in text:
        raise SystemExit("const needle missing")
    text = text.replace(needle, insert, 1)
    print("constants ok")
else:
    print("constants already")

if "_resourceArenaReady = false" not in text:
    needle = """        _resourceBindForceStartCyc = 0;
        _resourceBindUsedSlotAlloc = false;
        _resourceLoadForced = false;"""
    insert = """        _resourceBindForceStartCyc = 0;
        _resourceBindUsedSlotAlloc = false;
        _resourceArenaReady = false;
        _resourceLoadForced = false;"""
    if needle not in text:
        raise SystemExit("reset needle missing")
    text = text.replace(needle, insert, 1)
    print("reset ok")
else:
    print("reset already")

# --- kill poison capacity plants in MaybeInitStreamManager ---
old_cap = """        // Wave-3: soft capacity for FUN_0043B9F8 when heap alloc is 0.
        if (sys.Memory.Read32(StreamManagerBase + 0x28) == 0)
            sys.Memory.Write32(StreamManagerBase + 0x28, 0x100000);
        if (sys.Memory.Read32(StreamManagerBase + 0x2C) == 0)
            sys.Memory.Write32(StreamManagerBase + 0x2C, 0x100000);

        _streamManagerInits++;"""
new_cap = """        // Wave-4: sm+0x28/+0x2C are allocator *function pointers* (43BE08/43BE98 jalr),
        // not raw capacity. Wave-3 0x100000 plant caused AdEL during 43B670 object create.
        EnsureResourceArenaAndAllocators(sys.Memory);

        _streamManagerInits++;"""
if old_cap in text:
    text = text.replace(old_cap, new_cap, 1)
    print("sm capacity fix ok")
elif "EnsureResourceArenaAndAllocators(sys.Memory)" in text:
    print("sm capacity already")
else:
    raise SystemExit("sm capacity needle missing")

# --- replace force/resume block ---
start = text.find("    /// <summary>\n    /// Wave-2/3 MENU: drive the real resource-manager path")
if start < 0:
    start = text.find("    /// <summary>\n    /// Wave-2/3/4 MENU: drive the real resource-manager path")
if start < 0:
    start = text.find("    /// <summary>\n    /// Wave-2 MENU: drive the real resource-manager path")
if start < 0:
    raise SystemExit("method start missing")
end = text.find("    private void MaybeStartLogo(Ps2System sys)", start)
if end < 0:
    raise SystemExit("method end missing")

block = r'''    /// <summary>
    /// Wave-2/3/4 MENU: drive the real resource-manager path that binds stream work slots.
    /// ELF XREF sole chain: 32EA08 -&gt; 26FD80 -&gt; 26F918 (43B670) -&gt; 26FBF0 -&gt; 43BFC0 -&gt; 43C1C0.
    /// Wave-4: sm+0x28/+0x2C are EE allocator fn ptrs (not capacity); prefill desc+0x18/+0x1C
    /// so FUN_0043BDD0 bump-alloc works; seed heap arena; early-abort force on AdEL rehome.
    /// Always force FUN_0043B670 with reconstructed descriptor. Never force 26FD80.
    /// No synthetic type5 plants.
    /// </summary>
    private void MaybeForceResourceSlotBind(Ps2System sys)
    {
        if (_resourceBindPhase >= 4) return;
        if (_resourceBindResumePending) return;
        if (_sifResumePending || _managerInitResumePending || _initLocksResumePending) return;

        if (sys.Cdvd.SectorsRead < 180_000) return;
        if (sys.MasterCycles < 70_000_000) return;
        if (sys.Gif.Path3Transfers < 8) return;

        bool multiLive = sys.Memory.Read32(0x0075E950) == 0x0043F920u;
        bool frameCbLive = sys.Memory.Read32(0x0075BDD8) == 0x0043F920u;
        if (!multiLive && !frameCbLive) return;

        if (sys.Memory.Read32(StreamManagerBase + 0x38) != 1
            && sys.Memory.Read32(0x0055E1EC) != 1)
            return;

        uint slot0 = sys.Memory.Read32(0x0055E25C);
        if (slot0 == 1 && sys.Memory.Read32(0x0055E25C + 0x3C) != 0)
        {
            _resourceBindPhase = 4;
            return;
        }

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        if (pc is (>= 0x0026F900 and <= 0x0026FD80)
            or (>= 0x0043B670 and <= 0x0043C400)
            or (>= 0x0043FAE0 and <= 0x0043FD00)
            or (>= 0x00450000 and <= 0x00452000)
            or (>= 0x00420000 and <= 0x00422000))
            return;

        if (!_resourceBindTrampolineWritten)
        {
            sys.Memory.Write32(ResourceBindReturnTrampoline, 0x1000FFFFu);
            sys.Memory.Write32(ResourceBindReturnTrampoline + 4, 0);
            _resourceBindTrampolineWritten = true;
        }

        if (_resourceBindPhase == 0 && !_resourceBindKickForced)
        {
            EnsureResourceArenaAndAllocators(sys.Memory);
            SeedMidwayResourceHeap(sys.Memory);

            uint pathPtr = PrepareResourceHandleForKick(sys.Memory, ResourceLevel0Id);
            uint descPtr = ResourceHandleBase + 0x24;
            BuildResourceDescriptor(sys.Memory, descPtr, ResourceLevel0Width, ResourceLevel0Height);

            // Always force 43B670 with reconstructed descriptor + arena buffers.
            _resourceBindUsedSlotAlloc = true;

            _resourceBindSavedPc = sys.EE.PC;
            _resourceBindSavedGpr = new ulong[32];
            for (int i = 0; i < 32; i++)
                _resourceBindSavedGpr[i] = sys.EE.GetGpr(i).Lo;

            for (int i = 4; i <= 11; i++)
                sys.EE.SetGpr(i, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = descPtr });
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = ResourceBindReturnTrampoline });
            ulong sp = sys.EE.GetGpr(29).Lo;
            if ((sp & 0x1FFFFFFFUL) < 0x100000 || (sp & 0x1FFFFFFFUL) >= 0x2000000)
                sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x01FF0000 });
            ReHomeSpIfInHleScratch(sys);

            sys.EE.PC = ResourceSlotAllocFn;
            sys.LastGoodEePc = ResourceSlotAllocFn;
            _resourceBindKickForced = true;
            _resourceBindResumePending = true;
            _resourceBindForceStartCyc = sys.MasterCycles;
            _resourceBindPhase = 1;
            Assists++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[BIOS] force resource slot-alloc FUN_0043B670 a0=0x{descPtr:X} " +
                    $"dims={ResourceLevel0Width}x{ResourceLevel0Height} path=0x{pathPtr:X8} " +
                    $"descBuf=0x{sys.Memory.Read32(descPtr + 0x18):X8}/" +
                    $"0x{sys.Memory.Read32(descPtr + 0x1C):X} " +
                    $"sm28=0x{sys.Memory.Read32(StreamManagerBase + 0x28):X} " +
                    $"heapLive={IsResourceHeapLive(sys.Memory)} " +
                    $"savedPc=0x{_resourceBindSavedPc:X8} slot0={slot0:X} " +
                    $"gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
            return;
        }

        if (_resourceBindPhase == 2 && !_resourceBindPollForced)
        {
            uint handlePtr = sys.Memory.Read32(ResourceHandleBase);
            if (handlePtr < 0x100000 || handlePtr >= (uint)SystemMemory.RDRAM_SIZE)
            {
                uint slotBase = StreamManagerBase + 0x6C;
                handlePtr = 0;
                for (int i = 0; i < 8; i++)
                {
                    uint s = slotBase + (uint)(i * 0x2AC);
                    uint flag = sys.Memory.Read32(s);
                    uint obj = sys.Memory.Read32(s + 0x3C);
                    if (flag != 0 && obj != 0 && obj < (uint)SystemMemory.RDRAM_SIZE)
                    {
                        sys.Memory.Write32(ResourceHandleBase, s);
                        handlePtr = s;
                        break;
                    }
                }
            }
            if (handlePtr < 0x100000 || handlePtr >= (uint)SystemMemory.RDRAM_SIZE)
            {
                _resourceBindPhase = 4;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                    Console.Error.WriteLine(
                        $"[BIOS] resource kick left *0x678458=0 - no bind poll " +
                        $"slot0={sys.Memory.Read32(0x0055E25C):X} " +
                        $"sm28={sys.Memory.Read32(StreamManagerBase + 0x28):X} " +
                        $"sm2c={sys.Memory.Read32(StreamManagerBase + 0x2C):X} " +
                        $"via43B670={_resourceBindUsedSlotAlloc} cyc={sys.MasterCycles}");
                return;
            }

            ForceResourceHandleDone(sys.Memory, handlePtr);
            ForceResourceHandleDone(sys.Memory, ResourceHandleBase);
            uint st = sys.Memory.Read32(handlePtr + 0x48);
            if (st == 0 || (int)st > 0)
                sys.Memory.Write32(handlePtr + 0x48, unchecked((uint)(-1)));

            _resourceBindSavedPc = sys.EE.PC;
            _resourceBindSavedGpr = new ulong[32];
            for (int i = 0; i < 32; i++)
                _resourceBindSavedGpr[i] = sys.EE.GetGpr(i).Lo;

            for (int i = 4; i <= 11; i++)
                sys.EE.SetGpr(i, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = ResourceHandleBase });
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = ResourceBindReturnTrampoline });
            ReHomeSpIfInHleScratch(sys);

            sys.EE.PC = ResourceBindFn;
            sys.LastGoodEePc = ResourceBindFn;
            _resourceBindPollForced = true;
            _resourceBindResumePending = true;
            _resourceBindForceStartCyc = sys.MasterCycles;
            _resourceBindPhase = 3;
            Assists++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[BIOS] force resource bind FUN_0026FBF0 a0=0x{ResourceHandleBase:X} " +
                    $"*handle=0x{handlePtr:X8} slot0={sys.Memory.Read32(0x0055E25C):X} " +
                    $"obj={sys.Memory.Read32(0x0055E25C + 0x3C):X8} " +
                    $"gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
        }
    }

    /// <summary>
    /// Wave-4: install EE bump-alloc stub + stream-manager allocator fn ptrs.
    /// Replaces the wave-3 poison plant of 0x100000 into sm+0x28/+0x2C.
    /// </summary>
    private void EnsureResourceArenaAndAllocators(SystemMemory mem)
    {
        // EE code at ResourceAllocStub:
        //   lw v0,0(a0); lw v1,4(a0); addu t0,v0,a1; sltu t1,v1,t0;
        //   bne t1,fail; nop; sw t0,0(a0); jr ra; nop; fail: jr ra; move v0,zero
        if (!_resourceArenaReady)
        {
            mem.Write32(ResourceAllocStub + 0x00, 0x8C820000u);
            mem.Write32(ResourceAllocStub + 0x04, 0x8C830004u);
            mem.Write32(ResourceAllocStub + 0x08, 0x00454021u);
            mem.Write32(ResourceAllocStub + 0x0C, 0x0068482Bu);
            mem.Write32(ResourceAllocStub + 0x10, 0x15200003u);
            mem.Write32(ResourceAllocStub + 0x14, 0x00000000u);
            mem.Write32(ResourceAllocStub + 0x18, 0xAC880000u);
            mem.Write32(ResourceAllocStub + 0x1C, 0x03E00008u);
            mem.Write32(ResourceAllocStub + 0x20, 0x00000000u);
            mem.Write32(ResourceAllocStub + 0x24, 0x03E00008u);
            mem.Write32(ResourceAllocStub + 0x28, 0x0000102Du);

            mem.Write32(ResourceAllocCtx + 0, ResourceArenaBase + ResourceDescBufSize);
            mem.Write32(ResourceAllocCtx + 4, ResourceArenaBase + ResourceArenaSize);
            _resourceArenaReady = true;
        }

        uint sm28 = mem.Read32(StreamManagerBase + 0x28);
        uint sm2c = mem.Read32(StreamManagerBase + 0x2C);
        bool poison28 = sm28 == 0 || sm28 == 0x100000 || sm28 >= (uint)SystemMemory.RDRAM_SIZE
                        || !IsLikelyResourceAllocFn(sm28);
        bool poison2c = sm2c == 0 || sm2c == 0x100000 || sm2c >= (uint)SystemMemory.RDRAM_SIZE
                        || !IsLikelyResourceAllocFn(sm2c);
        if (poison28)
            mem.Write32(StreamManagerBase + 0x28, ResourceAllocStub);
        if (poison2c)
            mem.Write32(StreamManagerBase + 0x2C, ResourceAllocStub);
        uint sm30 = mem.Read32(StreamManagerBase + 0x30);
        if (sm30 == 0 || sm30 == 0x100000)
            mem.Write32(StreamManagerBase + 0x30, ResourceAllocCtx);
    }

    private static bool IsLikelyResourceAllocFn(uint addr)
    {
        if (addr == ResourceAllocStub) return true;
        if (addr < 0x100000 || addr >= 0x01FE0000) return false;
        if ((addr & 3) != 0) return false;
        return addr >= 0x00100000 && addr < 0x00600000;
    }

    /// <summary>
    /// Wave-4: seed Midway heap table entry used by FUN_0020F058 for natural alloc paths.
    /// </summary>
    private static void SeedMidwayResourceHeap(SystemMemory mem)
    {
        uint heapStart = ResourceArenaBase + ResourceDescBufSize;
        uint heapEnd = ResourceArenaBase + ResourceArenaSize;
        for (uint i = 0; i < 2; i++)
        {
            uint baseH = ResourceHeapTable + i * 0x68;
            if (baseH + 0x20 >= (uint)SystemMemory.RDRAM_SIZE) break;
            if (mem.Read32(baseH + 0x10) != 0 && mem.Read32(baseH + 0x10) != 0xFFFFFFFFu)
                continue;
            mem.Write32(baseH + 0x08, heapStart);
            mem.Write32(baseH + 0x0C, heapEnd);
            mem.Write32(baseH + 0x10, 16);
            mem.Write32(baseH + 0x1C, 0);
        }
        if (mem.Read32(ResourceHeapGlobal) == 0 || mem.Read32(ResourceHeapGlobal) == 0x400)
            mem.Write32(ResourceHeapGlobal, 0);
    }

    /// <summary>
    /// Heap id for FUN_0020F058. Callers of FUN_0032EA08 load a2 from *0x584918 / *0x4E5138.
    /// </summary>
    private static uint ResolveResourceHeapId(SystemMemory mem)
    {
        uint h = mem.Read32(ResourceHeapGlobal);
        if (h != 0xFFFFFFFFu)
        {
            uint idx = h & 0x7F;
            uint baseH = ResourceHeapTable + idx * 0x68;
            if (baseH + 0x20 < (uint)SystemMemory.RDRAM_SIZE)
            {
                uint sz = mem.Read32(baseH + 0x10);
                if (sz != 0 && sz != 0xFFFFFFFFu)
                    return h & 0x7Fu;
            }
        }
        h = mem.Read32(ResourceHeapGlobalAlt);
        if (h != 0 && h != 0xFFFFFFFFu)
            return h & 0x7Fu;
        for (uint i = 0; i < 32; i++)
        {
            uint baseH = ResourceHeapTable + i * 0x68;
            if (baseH + 0x20 >= (uint)SystemMemory.RDRAM_SIZE) break;
            uint sz = mem.Read32(baseH + 0x10);
            if (sz != 0 && sz != 0xFFFFFFFFu)
                return i;
        }
        return 0;
    }

    /// <summary>True when at least one Midway heap slot has a non-zero size field.</summary>
    private static bool IsResourceHeapLive(SystemMemory mem)
    {
        for (uint i = 0; i < 32; i++)
        {
            uint baseH = ResourceHeapTable + i * 0x68;
            if (baseH + 0x20 >= (uint)SystemMemory.RDRAM_SIZE) break;
            uint sz = mem.Read32(baseH + 0x10);
            if (sz != 0 && sz != 0xFFFFFFFFu)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Build FUN_0026F918 descriptor (handle+0x24). Wave-4: +0x18/+0x1C prefilled so
    /// 43BA48→43BDD0 bump path is used (not 43BE08 jalr of sm+0x28).
    /// </summary>
    private static void BuildResourceDescriptor(SystemMemory mem, uint desc, uint width, uint height)
    {
        for (uint o = 0; o < 0x30; o += 4)
            mem.Write32(desc + o, 0);
        mem.Write32(desc + 0x00, 1);
        mem.Write32(desc + 0x04, 0x003D0900u);
        mem.Write32(desc + 0x08, width);
        mem.Write32(desc + 0x0C, height);
        mem.Write32(desc + 0x10, 2);
        mem.Write32(desc + 0x14, 1);
        mem.Write32(desc + 0x18, ResourceArenaBase);
        mem.Write32(desc + 0x1C, ResourceDescBufSize);
        mem.Write32(desc + 0x20, 17);
    }

    /// <summary>
    /// Mirror FUN_0026FD80 setup: zero handle, flags, path at +0xEC, stream allocators.
    /// Returns path string pointer written into the handle (0 if missing).
    /// </summary>
    private uint PrepareResourceHandleForKick(SystemMemory mem, uint resourceId)
    {
        EnsureResourceArenaAndAllocators(mem);

        for (uint o = 0; o < 0x208; o += 4)
            mem.Write32(ResourceHandleBase + o, 0);
        mem.Write32(ResourceHandleBase + 0x1F0, 1);
        mem.Write32(ResourceHandleBase + 0x1EC, 0);
        mem.Write32(ResourceHandleBase + 4, 0x3F800000u);
        mem.Write32(ResourceHandleBase + 8, 1);
        mem.Write32(ResourceHandleBase + 0xC, 1);

        uint pathPtr = 0;
        if (resourceId < 30)
        {
            uint namePtr = mem.Read32(ResourceNameTable + resourceId * 4);
            if (namePtr != 0 && namePtr + 1 < (uint)SystemMemory.RDRAM_SIZE)
            {
                pathPtr = namePtr + 1;
                uint dest = ResourceHandleBase + ResourcePathInHandle;
                for (uint i = 0; i < 0x80; i++)
                {
                    byte b = mem.Read8(pathPtr + i);
                    mem.Write8(dest + i, b);
                    if (b == 0) break;
                }
            }
        }

        if (mem.Read32(0x0055E1EC) == 0)
            mem.Write32(0x0055E1EC, 1);
        if (mem.Read32(StreamManagerBase + 0x38) == 0)
            mem.Write32(StreamManagerBase + 0x38, 1);
        if (mem.Read32(StreamManagerBase + 0x24) == 1)
            mem.Write32(StreamManagerBase + 0x24, 0);

        return pathPtr;
    }

    private void MaybeResumeAfterForcedResourceBind(Ps2System sys)
    {
        if (!_resourceBindResumePending) return;

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFF);
        bool atTrampoline = pc == ResourceBindReturnTrampoline;
        bool timedOut = _resourceBindForceStartCyc != 0
            && sys.MasterCycles >= _resourceBindForceStartCyc + ResourceBindTimeoutCyc;
        bool inForceBand = pc is (>= 0x0026F900 and <= 0x0026FD80)
            or (>= 0x0043A000 and <= 0x0043F000)
            or (>= 0x00450000 and <= 0x00452000)
            or (>= 0x00420000 and <= 0x00422000)
            or (>= 0x00474000 and <= 0x00475000)
            or (pc >= ResourceAllocStub && pc < ResourceAllocStub + 0x40)
            or (pc == ResourceBindReturnTrampoline);
        bool escaped = !atTrampoline && _resourceBindForceStartCyc != 0
            && sys.MasterCycles >= _resourceBindForceStartCyc + ResourceBindEscapeCyc
            && !inForceBand;
        bool lost = !atTrampoline && (timedOut || escaped);
        if (!atTrampoline && !lost) return;

        uint slot0 = sys.Memory.Read32(0x0055E25C);
        uint slot0obj = sys.Memory.Read32(0x0055E25C + 0x3C);
        uint handlePtr = sys.Memory.Read32(ResourceHandleBase);
        uint v0 = (uint)sys.EE.GetGpr(2).Lo;

        if (_resourceBindPhase == 1 && _resourceBindUsedSlotAlloc
            && v0 >= 0x100000 && v0 < (uint)SystemMemory.RDRAM_SIZE
            && (handlePtr < 0x100000 || handlePtr >= (uint)SystemMemory.RDRAM_SIZE))
        {
            sys.Memory.Write32(ResourceHandleBase, v0);
            handlePtr = v0;
        }
        if (handlePtr > 0 && handlePtr < 0x100000)
        {
            sys.Memory.Write32(ResourceHandleBase, 0);
            handlePtr = 0;
        }

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[BIOS] resource bind resume phase={_resourceBindPhase} " +
                $"{(timedOut ? "TIMEOUT " : escaped ? "ESCAPE " : "")}" +
                $"*0x678458=0x{handlePtr:X8} slot0={slot0:X} obj=0x{slot0obj:X8} " +
                $"v0=0x{v0:X} pc=0x{pc:X8} gifP3={sys.Gif.Path3Transfers} " +
                $"cyc={sys.MasterCycles}");

        sys.EE.PC = _resourceBindSavedPc;
        if (_resourceBindSavedGpr != null)
            for (int i = 1; i < 32; i++)
                sys.EE.SetGpr(i, new EmotionEngine.Gpr128 { Lo = _resourceBindSavedGpr[i] });
        sys.LastGoodEePc = _resourceBindSavedPc;
        _resourceBindResumePending = false;
        _resourceBindForceStartCyc = 0;

        if (lost)
            _resourceBindPhase = 4;
        else if (_resourceBindPhase == 1)
            _resourceBindPhase = 2;
        else if (_resourceBindPhase == 3)
            _resourceBindPhase = 4;

        Assists++;
    }


'''

text = text[:start] + block + text[end:]
path.write_text(text, encoding="utf-8")

# sanity
assert "ResourceArenaBase" in text
assert "EnsureResourceArenaAndAllocators" in text
assert "slot-alloc FUN_0043B670" in text
assert "0x100000);\n            if (sys.Memory.Read32(StreamManagerBase + 0x2C)" not in text
# poison plant should be gone from InitStreamManager
assert "soft capacity for FUN_0043B9F8" not in text
print("patch complete", len(text))
