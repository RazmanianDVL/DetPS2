"""Wave-3 SM: reconstruct 26F918 load request; prefer 43B670 when heaps dead; trampoline timeout."""
from pathlib import Path

path = Path("src/DetPS2.Core/MidwayBootAssist.cs")
text = path.read_text(encoding="utf-8")

# --- constants (idempotent) ---
if "ResourceSlotAllocFn" not in text:
    needle = "    private const uint ResourceHandleBase = 0x00678458;\n\n    private bool _worklistPlanted;"
    insert = """    private const uint ResourceHandleBase = 0x00678458;
    /// <summary>FUN_0043B670 stream-slot alloc from descriptor (26F918 core). Prefer when heaps dead.</summary>
    private const uint ResourceSlotAllocFn = 0x0043B670;
    /// <summary>Max master-cycles on resource force trampoline before abandon.</summary>
    private const ulong ResourceBindTimeoutCyc = 2_000_000;
    // Wave-3 reconstructed 32EA08/26FD80 load-request (ELF ground truth).
    // Level table 0x4D3BA0[0] MIDWAY.SFD: dims 512x384, resource id 0.
    // a1/a2 are display dimensions; t0 is heap id; path at handle+0xEC via 211148.
    private const uint ResourceNameTable = 0x004D3A10;
    private const uint ResourceHeapGlobal = 0x00584918;
    private const uint ResourceHeapGlobalAlt = 0x004E5138;
    private const uint ResourcePathInHandle = 0xEC;
    private const uint ResourceLevel0Width = 512;
    private const uint ResourceLevel0Height = 384;
    private const uint ResourceLevel0Id = 0;
    private const uint ResourceHeapTable = 0x0065E998;

    private bool _worklistPlanted;"""
    if needle not in text:
        raise SystemExit("const needle missing")
    text = text.replace(needle, insert, 1)
    print("constants ok")
else:
    print("constants already")

if "_resourceBindForceStartCyc" not in text:
    needle = "    private ulong[]? _resourceBindSavedGpr;\n    private bool _logoPrepared;"
    insert = "    private ulong[]? _resourceBindSavedGpr;\n    private ulong _resourceBindForceStartCyc;\n    private bool _resourceBindUsedSlotAlloc;\n    private bool _logoPrepared;"
    if needle not in text:
        raise SystemExit("field needle missing")
    text = text.replace(needle, insert, 1)
    print("fields ok")
else:
    print("fields already")

if "_resourceBindForceStartCyc = 0" not in text:
    needle = "        _resourceBindSavedGpr = null;\n        _resourceLoadForced = false;"
    insert = "        _resourceBindSavedGpr = null;\n        _resourceBindForceStartCyc = 0;\n        _resourceBindUsedSlotAlloc = false;\n        _resourceLoadForced = false;"
    if needle not in text:
        raise SystemExit("reset needle missing")
    text = text.replace(needle, insert, 1)
    print("reset ok")
else:
    print("reset already")

if "StreamManagerBase + 0x28) == 0)" not in text:
    needle = """        if (sys.Memory.Read32(StreamManagerBase + 0x24) == 1)
            sys.Memory.Write32(StreamManagerBase + 0x24, 0);

        _streamManagerInits++;"""
    insert = """        if (sys.Memory.Read32(StreamManagerBase + 0x24) == 1)
            sys.Memory.Write32(StreamManagerBase + 0x24, 0);
        // Wave-3: soft capacity for FUN_0043B9F8 when heap alloc is 0.
        if (sys.Memory.Read32(StreamManagerBase + 0x28) == 0)
            sys.Memory.Write32(StreamManagerBase + 0x28, 0x100000);
        if (sys.Memory.Read32(StreamManagerBase + 0x2C) == 0)
            sys.Memory.Write32(StreamManagerBase + 0x2C, 0x100000);

        _streamManagerInits++;"""
    if needle not in text:
        raise SystemExit("sm needle missing")
    text = text.replace(needle, insert, 1)
    print("sm capacity ok")
else:
    print("sm capacity already")

# --- replace force/resume block ---
start = text.find("    /// <summary>\n    /// Wave-2 MENU: drive the real resource-manager path")
if start < 0:
    start = text.find("    /// <summary>\n    /// Wave-2/3 MENU: drive the real resource-manager path")
if start < 0:
    raise SystemExit("method start missing")
end = text.find("    private void MaybeStartLogo(Ps2System sys)", start)
if end < 0:
    raise SystemExit("method end missing")

block = r'''    /// <summary>
    /// Wave-2/3 MENU: drive the real resource-manager path that binds stream work slots.
    /// ELF XREF sole chain: 32EA08 -&gt; 26FD80 -&gt; 26F918 (43B670) -&gt; 26FBF0 -&gt; 43BFC0 -&gt; 43C1C0.
    /// Wave-3: reconstruct real load-request (dims/path/heap). When Midway heaps are dead under
    /// HLE (table 0x65E998 empty), force FUN_0043B670 with a prepared descriptor instead of full
    /// 26F918 (which AdELs in FUN_0020F058). Never force 26FD80 (infinite poll).
    /// No synthetic type5 plants. No PresentEeSifHandshake in StartLoadedModule.
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
            or (>= 0x0043FAE0 and <= 0x0043FD00))
            return;

        if (!_resourceBindTrampolineWritten)
        {
            sys.Memory.Write32(ResourceBindReturnTrampoline, 0x1000FFFFu);
            sys.Memory.Write32(ResourceBindReturnTrampoline + 4, 0);
            _resourceBindTrampolineWritten = true;
        }

        if (_resourceBindPhase == 0 && !_resourceBindKickForced)
        {
            uint pathPtr = PrepareResourceHandleForKick(sys.Memory, ResourceLevel0Id);
            uint descPtr = ResourceHandleBase + 0x24;
            BuildResourceDescriptor(sys.Memory, descPtr, ResourceLevel0Width, ResourceLevel0Height);

            // Live heaps stay zero under HLE — full 26F918 hits 20F058 and AdEL-rescues off
            // the trampoline. Prefer direct 43B670 with reconstructed descriptor + capacity.
            bool heapLive = IsResourceHeapLive(sys.Memory);
            uint targetFn = heapLive ? ResourceKickFn : ResourceSlotAllocFn;
            _resourceBindUsedSlotAlloc = !heapLive;

            _resourceBindSavedPc = sys.EE.PC;
            _resourceBindSavedGpr = new ulong[32];
            for (int i = 0; i < 32; i++)
                _resourceBindSavedGpr[i] = sys.EE.GetGpr(i).Lo;

            for (int i = 4; i <= 11; i++)
                sys.EE.SetGpr(i, new EmotionEngine.Gpr128 { Lo = 0 });
            if (heapLive)
            {
                uint heapId = ResolveResourceHeapId(sys.Memory);
                sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = ResourceHandleBase });
                sys.EE.SetGpr(5, new EmotionEngine.Gpr128 { Lo = ResourceLevel0Width });
                sys.EE.SetGpr(6, new EmotionEngine.Gpr128 { Lo = ResourceLevel0Height });
                sys.EE.SetGpr(7, new EmotionEngine.Gpr128 { Lo = 0 });
                sys.EE.SetGpr(8, new EmotionEngine.Gpr128 { Lo = heapId });
                sys.EE.SetFpr(12, 1.0f);
            }
            else
            {
                sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = descPtr });
            }
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = ResourceBindReturnTrampoline });
            ulong sp = sys.EE.GetGpr(29).Lo;
            if ((sp & 0x1FFFFFFFUL) < 0x100000 || (sp & 0x1FFFFFFFUL) >= 0x2000000)
                sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x01FF0000 });
            ReHomeSpIfInHleScratch(sys);

            sys.EE.PC = targetFn;
            sys.LastGoodEePc = targetFn;
            _resourceBindKickForced = true;
            _resourceBindResumePending = true;
            _resourceBindForceStartCyc = sys.MasterCycles;
            _resourceBindPhase = 1;
            Assists++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[BIOS] force resource {(heapLive ? "kick FUN_0026F918" : "slot-alloc FUN_0043B670")} " +
                    $"a0=0x{(heapLive ? ResourceHandleBase : descPtr):X} " +
                    $"dims={ResourceLevel0Width}x{ResourceLevel0Height} path=0x{pathPtr:X8} " +
                    $"heapLive={heapLive} savedPc=0x{_resourceBindSavedPc:X8} slot0={slot0:X} " +
                    $"gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
            return;
        }

        if (_resourceBindPhase == 2 && !_resourceBindPollForced)
        {
            uint handlePtr = sys.Memory.Read32(ResourceHandleBase);
            if (handlePtr == 0 || handlePtr >= (uint)SystemMemory.RDRAM_SIZE)
            {
                uint slotBase = StreamManagerBase + 0x6C;
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
            if (handlePtr == 0 || handlePtr >= (uint)SystemMemory.RDRAM_SIZE)
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
    /// Heap id for FUN_0020F058. Callers of FUN_0032EA08 load a2 from *0x584918 / *0x4E5138.
    /// </summary>
    private static uint ResolveResourceHeapId(SystemMemory mem)
    {
        uint h = mem.Read32(ResourceHeapGlobal);
        if (h != 0 && h != 0xFFFFFFFFu)
            return h;
        h = mem.Read32(ResourceHeapGlobalAlt);
        if (h != 0 && h != 0xFFFFFFFFu)
            return h;
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
    /// Build FUN_0026F918 descriptor (normally handle+0x24): word0=1, +4=0x3D0900,
    /// +8=width, +0xC=height, +0x10=2, +0x14=1, +0x18=0, +0x1C=1, +0x20=17.
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
        mem.Write32(desc + 0x18, 0);
        mem.Write32(desc + 0x1C, 1);
        mem.Write32(desc + 0x20, 17);
    }

    /// <summary>
    /// Mirror FUN_0026FD80 setup: zero handle, flags, path at +0xEC, stream capacity.
    /// Returns path string pointer written into the handle (0 if missing).
    /// </summary>
    private static uint PrepareResourceHandleForKick(SystemMemory mem, uint resourceId)
    {
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
        if (mem.Read32(StreamManagerBase + 0x28) == 0)
            mem.Write32(StreamManagerBase + 0x28, 0x100000);
        if (mem.Read32(StreamManagerBase + 0x2C) == 0)
            mem.Write32(StreamManagerBase + 0x2C, 0x100000);

        return pathPtr;
    }

    private void MaybeResumeAfterForcedResourceBind(Ps2System sys)
    {
        if (!_resourceBindResumePending) return;

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFF);
        bool atTrampoline = pc == ResourceBindReturnTrampoline;
        bool timedOut = _resourceBindForceStartCyc != 0
            && sys.MasterCycles >= _resourceBindForceStartCyc + ResourceBindTimeoutCyc;
        bool lost = !atTrampoline && timedOut;
        if (!atTrampoline && !lost) return;

        uint slot0 = sys.Memory.Read32(0x0055E25C);
        uint slot0obj = sys.Memory.Read32(0x0055E25C + 0x3C);
        uint handlePtr = sys.Memory.Read32(ResourceHandleBase);
        uint v0 = (uint)sys.EE.GetGpr(2).Lo;

        // Direct 43B670: v0 = slot pointer on success. Store into *0x678458 for 26FBF0.
        if (_resourceBindPhase == 1 && _resourceBindUsedSlotAlloc
            && v0 != 0 && v0 < (uint)SystemMemory.RDRAM_SIZE
            && (handlePtr == 0 || handlePtr >= (uint)SystemMemory.RDRAM_SIZE))
        {
            sys.Memory.Write32(ResourceHandleBase, v0);
            handlePtr = v0;
        }

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[BIOS] resource bind resume phase={_resourceBindPhase} " +
                $"{(lost ? "TIMEOUT " : "")}" +
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
assert "slot-alloc FUN_0043B670" in text
assert "IsResourceHeapLive" in text
assert "ResourceLevel0Width" in text
assert "ResourceSlotAllocFn" in text
print("patch complete", len(text))
