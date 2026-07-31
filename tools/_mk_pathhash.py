from pathlib import Path
p = Path("src/DetPS2.Core/RealSifRpc.cs")
text = p.read_text(encoding="utf-8")

if "_mkdaArtHashPlanted" not in text:
    old = "    public bool MflInited { get; private set; }\n\n    /// <summary>\n    /// Midway MSL.IRX + MFL file-link HLE"
    new = "    public bool MflInited { get; private set; }\n    private bool _mkdaArtHashPlanted;\n    private int _daPathHashScratchOff;\n\n    /// <summary>\n    /// Midway MSL.IRX + MFL file-link HLE"
    if old not in text:
        raise SystemExit("field anchor missing")
    text = text.replace(old, new, 1)
    print("added fields")
else:
    print("fields ok")

if "TryRegisterMkdaArtMembers" not in text:
    needle = "    private void TryCompleteMslRequestRing(SystemMemory mem, IopModuleHost iopModules, Cdvd cdvd,\n        uint reqHdr, uint respHdr)"
    # Prefer inserting after soft-bind method: find last TrySoftBind closing before TryComplete
    idx = text.find(needle)
    if idx < 0:
        raise SystemExit("TryComplete anchor missing")
    # Only insert if soft-bind exists just before
    pre = text[max(0, idx-400):idx]
    if "TrySoftBindMflClient" not in pre and "soft-bind client" not in pre:
        print("warning: soft-bind not immediately before TryComplete")
    methods = r'''
    private const uint DaPathHashTable = 0x0053DCC0;
    private const uint DaPathHashScratch = 0x0007F000;

    /// <summary>Public re-try for Family Step after EE allocates path hash table.</summary>
    public void TryEnsureMkdaArtPathHash(SystemMemory mem, IopModuleHost iopModules, Cdvd cdvd)
    {
        if (mem == null || iopModules == null) return;
        TryRegisterMkdaArtMembers(mem, iopModules, cdvd);
    }

    private void TryRegisterMkdaArtMembers(SystemMemory mem, IopModuleHost iopModules, Cdvd cdvd)
    {
        if (_mkdaArtHashPlanted) return;
        uint buckets = mem.Read32(DaPathHashTable + 4);
        uint nbuckets = mem.Read32(DaPathHashTable + 8);
        uint entryPool = mem.Read32(DaPathHashTable + 12);
        if (buckets < 0x00100000 || nbuckets is 0 or > 100_000) return;
        if (entryPool < 0x00100000) return;
        EnsureMkdaPakMounted(iopModules, cdvd);
        foreach (string member in new[] { @"\ps2dvd\artps2\gameart.ssf", @"\ps2dvd\art\gameart.ssf", "gameart.ssf" })
        {
            int mfd = TryOpenFromMkdaPak(iopModules, member, out uint msz);
            if (mfd < 0 || msz == 0) continue;
            try { iopModules.FileClose(mfd); } catch { /* ignore */ }
            uint stream = AllocDaPathScratch(mem, 32);
            if (stream == 0) return;
            mem.Write32(stream + 0, 0x5354464Du);
            mem.Write32(stream + 4, stream);
            mem.Write32(stream + 8, msz);
            mem.Write32(stream + 12, msz);
            mem.Write32(stream + 20, 4);
            int planted = 0;
            foreach (string key in new[] { member, member.TrimStart('\\', '/') })
            {
                if (TryInsertDaPathHash(mem, key, stream)) planted++;
            }
            if (planted > 0)
            {
                _mkdaArtHashPlanted = true;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                    Console.Error.WriteLine($"[MSL-MFL] path-hash plant gameart entries={planted} size={msz}");
                return;
            }
        }
    }

    private uint AllocDaPathScratch(SystemMemory mem, int bytes)
    {
        int need = (bytes + 3) & ~3;
        if (_daPathHashScratchOff + need > 0xE00) return 0;
        uint addr = DaPathHashScratch + (uint)_daPathHashScratchOff;
        _daPathHashScratchOff += need;
        for (int i = 0; i < need; i += 4) mem.Write32(addr + (uint)i, 0);
        return addr;
    }

    private bool TryInsertDaPathHash(SystemMemory mem, string path, uint value)
    {
        if (string.IsNullOrEmpty(path) || value == 0) return false;
        uint buckets = mem.Read32(DaPathHashTable + 4);
        uint nbuckets = mem.Read32(DaPathHashTable + 8);
        uint entryPool = mem.Read32(DaPathHashTable + 12);
        uint count = mem.Read32(DaPathHashTable + 16);
        if (buckets < 0x00100000 || nbuckets == 0 || entryPool < 0x00100000) return false;
        if (count >= 0x10000 || entryPool + (count + 1) * 12 > SystemMemory.RDRAM_SIZE) return false;
        uint hash = 0;
        foreach (char ch in path)
        {
            uint c = (byte)ch;
            if (c is >= 'A' and <= 'Z') c += 32;
            hash = (hash << 4) + c;
            uint hi = hash & 0xF0000000u;
            if (hi != 0) { hash ^= hi >> 24; hash ^= hi; }
        }
        uint bucketAddr = buckets + (hash % nbuckets) * 4;
        for (uint e = mem.Read32(bucketAddr); e != 0; e = mem.Read32(e + 8))
        {
            if (e < 0x00100000 || e >= SystemMemory.RDRAM_SIZE) break;
            if (string.Equals(ReadCString(mem, mem.Read32(e), 128), path, StringComparison.OrdinalIgnoreCase))
            {
                mem.Write32(e + 4, value);
                return true;
            }
        }
        uint pathPtr = 0;
        if (mem.Read32(DaPathHashTable + 24) != 0)
        {
            uint strBase = mem.Read32(DaPathHashTable + 28);
            uint strCur = mem.Read32(DaPathHashTable + 36);
            if (strBase >= 0x00100000 && strCur < 0x100000
                && strBase + strCur + (uint)path.Length + 1 < SystemMemory.RDRAM_SIZE)
            {
                pathPtr = strBase + strCur;
                for (int i = 0; i < path.Length; i++) mem.Write8(pathPtr + (uint)i, (byte)path[i]);
                mem.Write8(pathPtr + (uint)path.Length, 0);
                mem.Write32(DaPathHashTable + 36, strCur + (uint)path.Length + 1);
            }
        }
        if (pathPtr == 0)
        {
            pathPtr = AllocDaPathScratch(mem, path.Length + 1);
            if (pathPtr == 0) return false;
            for (int i = 0; i < path.Length; i++) mem.Write8(pathPtr + (uint)i, (byte)path[i]);
            mem.Write8(pathPtr + (uint)path.Length, 0);
        }
        uint entry = entryPool + count * 12;
        mem.Write32(entry + 0, pathPtr);
        mem.Write32(entry + 4, value);
        mem.Write32(entry + 8, mem.Read32(bucketAddr));
        mem.Write32(bucketAddr, entry);
        mem.Write32(DaPathHashTable + 16, count + 1);
        return true;
    }

'''
    text = text[:idx] + methods + text[idx:]
    print("inserted methods")
else:
    print("methods ok")

# 3) Call plant from ring-complete after soft-bind
old_rc = """            // Also ensure MFL client is bound so poll's CallRpc info/close succeed.
            TrySoftBindMflClient(mem);

            if (trace)
                Console.Error.WriteLine(
                    $"[MSL-MFL] ring-complete path=\\"#{path}\\" h={h} fd={fd} size={fsz} " +
                    $"slot=0x{slot:X8} respObj=0x{respObj:X8}");"""
# fix path quote
old_rc = """            // Also ensure MFL client is bound so poll's CallRpc info/close succeed.
            TrySoftBindMflClient(mem);

            if (trace)
                Console.Error.WriteLine(
                    $"[MSL-MFL] ring-complete path=\\"{path}\\" h={h} fd={fd} size={fsz} " +
                    $"slot=0x{slot:X8} respObj=0x{respObj:X8}");"""
if "TryRegisterMkdaArtMembers(mem, iopModules, cdvd)" not in text:
    old = """            // Also ensure MFL client is bound so poll's CallRpc info/close succeed.
            TrySoftBindMflClient(mem);

            if (trace)"""
    new = """            // Also ensure MFL client is bound so poll's CallRpc info/close succeed.
            TrySoftBindMflClient(mem);
            if (path.Contains("MKDA", StringComparison.OrdinalIgnoreCase))
                TryRegisterMkdaArtMembers(mem, iopModules, cdvd);

            if (trace)"""
    if old not in text:
        raise SystemExit("ring-complete plant anchor missing")
    text = text.replace(old, new, 1)
    print("added ring plant call")
else:
    print("ring plant call ok")

# 4) Pump also tries path hash
old_pump = """        TrySeedMflReadyFlag(mem);

        // DA live request ring header @ 0x587DA0"""
new_pump = """        TrySeedMflReadyFlag(mem);
        TryRegisterMkdaArtMembers(mem, iopModules, cdvd);

        // DA live request ring header @ 0x587DA0"""
if "TryRegisterMkdaArtMembers(mem, iopModules, cdvd);" not in text[text.find("public void PumpMslFileRequests"):text.find("public void PumpMslFileRequests")+500]:
    if old_pump not in text:
        print("pump anchor missing - skip")
    else:
        text = text.replace(old_pump, new_pump, 1)
        print("added pump plant")
else:
    print("pump plant ok")

p.write_text(text, encoding="utf-8")
print("wrote", p, "len", len(text))
