using System;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Vexx (USA) SLUS_203.83 — IOPRP252 + null-path basename + CRT/string heap plant +
/// SearchFile 0x128 path-layout (+0x24) + freelist bump escape + STREE0 re-plant.
///
/// Wave-1 residual: GAME.TXT SearchFile+CdRead (cdvd=4). Wave-2: STREE0.TRE path sits at
/// +0x24 while stale GAME.TXT leaf/lsn remain at +0x20/+0 — re-plant STREE0; freelist thrash
/// only after pad/GAME.TXT; stack integrity when PC lands in path ASCII. See issue #19.
/// </summary>
public sealed class VexxAssist : IGameQuirkModule
{
    public string Serial => "SLUS_203.83";
    public string DisplayName => "Vexx (USA)";

    public const uint IopVersionCellA = 0x003D18B8;
    public const uint IopVersionCellB = 0x003D1938;
    public const uint PathBasenameA = 0x00146170;
    public const uint PathBasenameB = 0x00146230;
    public const uint StubA = 0x00090000;
    public const uint StubB = 0x00090040;
    public const uint CrtMallocSlot = 0x003BCD00;
    public const uint CrtFreeSlot = 0x003BCD04;
    public const uint CrtReallocSlot = 0x003BCD08;
    public const uint StringAllocHook = 0x00444998;
    public const uint StringFreeHook = 0x004449A0;
    public const uint SmallPoolRoot = 0x003F71B0;
    public const uint MallocStub = 0x00090100;
    public const uint FreeStub = 0x00090140;
    public const uint ReallocStub = 0x00090160;
    public const uint BumpCursorCell = 0x00090180;
    public const uint BumpArenaBase = 0x01800000;
    public const uint BumpArenaEnd = 0x01C00000;
    public const uint PathNormalizeLoop = 0x00372ABC;
    public const uint PathNormalizeAfterLoop = 0x00372B04;
    public const uint EmptyStringSentinel = 0x003C4C58;
    public const uint FreelistWalkLo = 0x001CE190;
    public const uint FreelistWalkHi = 0x001CE210;
    public const uint FreelistSuccessStore = 0x001CE280;
    public const uint SearchFileArgBuf = 0x1C1F4000;
    public const uint SearchFilePacket = 0x003F7B00;
    /// <summary>Do not freelist-bump until pad/IOPRP stack is past early CRT init.</summary>
    /// <summary>Allow freelist bump after CRT plant settles (not during whip-era thrash).</summary>
    public const ulong FreelistEscapeMinCycles = 1_000_000UL;

    private bool _pathPatched, _mallocPlanted;
    private int _versionReplants, _nullPathEscapes, _pathNormEscapes, _mallocReplants;
    private int _hookReplants, _freelistEscapes, _searchPathFixes, _searchPlants;
    private int _stackRescues;
    private Iso9660.Volume? _isoVol;
    private string? _isoVolPath;

    public void Reset()
    {
        _pathPatched = _mallocPlanted = false;
        _versionReplants = _nullPathEscapes = _pathNormEscapes = _mallocReplants = 0;
        _hookReplants = _freelistEscapes = _searchPathFixes = _searchPlants = 0;
        _stackRescues = 0;
        try { _isoVol?.Disc?.Dispose(); } catch { }
        _isoVol = null; _isoVolPath = null;
    }

    public void OnDiscMounted(Ps2System sys)
    {
        Reset();
        if (sys.Hle?.Sony?.RealRpc != null)
            sys.Hle.Sony.RealRpc.PreferIopRpGetVersion = true;
        PlantIopRpVersion(sys);
        PlantCrtMallocTable(sys);
        PlantStringHeapHook(sys);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine("[VEXX] OnDiscMounted: IOPRP252 + CRT/string heap plant ready");
    }

    public void OnHostPresent(Ps2System sys) => _ = sys;

    public void Step(Ps2System sys)
    {
        if (!VersionCellsOk(sys)) { PlantIopRpVersion(sys); _versionReplants++; }

        if (!_mallocPlanted || sys.Memory.Read32(CrtMallocSlot) == 0)
        {
            PlantCrtMallocTable(sys);
            _mallocPlanted = true;
            _mallocReplants++;
        }

        if (sys.Memory.Read32(StringAllocHook) != MallocStub)
        {
            PlantStringHeapHook(sys);
            _hookReplants++;
        }

        if (!_pathPatched || !PathStubActive(sys, PathBasenameA))
        {
            PatchNullPathBasename(sys);
            _pathPatched = true;
        }

        if (sys.Scheduler.MasterCycles >= 3_000_000UL)
        {
            foreach (uint buf in new[] { SearchFileArgBuf, SearchFilePacket })
            {
                if (MaybeFixSearchFilePathLayout(sys, buf)) _searchPathFixes++;
                if (MaybePlantSearchFileResult(sys, buf)) _searchPlants++;
            }
        }

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFu);

        if ((pc is >= 0x0014619C and <= 0x001461BC) || (pc is >= 0x0014625C and <= 0x0014627C))
        {
            if (sys.EE.GetGpr(16).Lo == 0)
            {
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
                sys.EE.PC = sys.EE.GetGpr(31).Lo;
                _nullPathEscapes++;
            }
        }

        if (pc is >= PathNormalizeLoop and <= PathNormalizeAfterLoop)
        {
            uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFu);
            if (sp >= 0x1000 && sp + 0x40 < SystemMemory.RDRAM_SIZE)
            {
                uint pathPtr = sys.Memory.Read32(sp + 0x38);
                if (pathPtr < 0x10000u)
                {
                    sys.Memory.Write32(sp + 0x38, EmptyStringSentinel);
                    sys.EE.SetGpr(7, new EmotionEngine.Gpr128 { Lo = EmptyStringSentinel });
                    sys.EE.SetGpr(6, new EmotionEngine.Gpr128 { Lo = 0 });
                    sys.EE.PC = PathNormalizeAfterLoop;
                    _pathNormEscapes++;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
                        Console.Error.WriteLine(
                            $"[VEXX] path-normalize escape #{_pathNormEscapes} wasPtr=0x{pathPtr:X} cyc={sys.Scheduler.MasterCycles}");
                }
            }
        }

        // Early freelist escape (pre-pad) corrupts CRT and open-bus thrash (binds=0).
        if (sys.Scheduler.MasterCycles >= FreelistEscapeMinCycles
            && pc is >= FreelistWalkLo and <= FreelistWalkHi)
        {
            long walks = (long)sys.EE.GetGpr(22).Lo;
            uint size = (uint)sys.EE.GetGpr(16).Lo;
            if (walks > 64)
            {
                if (size > 0 && size < 0x00100000u)
                {
                    uint mem = HostBumpAlloc(sys, size + 64);
                    if (mem != 0)
                    {
                        sys.EE.SetGpr(20, new EmotionEngine.Gpr128 { Lo = mem });
                        sys.EE.SetGpr(21, new EmotionEngine.Gpr128 { Lo = mem + 32 });
                        sys.EE.PC = FreelistSuccessStore;
                        _freelistEscapes++;
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _freelistEscapes <= 16)
                            Console.Error.WriteLine(
                                $"[VEXX] freelist bump #{_freelistEscapes} size=0x{size:X} mem=0x{mem:X} cyc={sys.Scheduler.MasterCycles}");
                    }
                }
                else
                {
                    sys.EE.SetGpr(20, new EmotionEngine.Gpr128 { Lo = 0 });
                    sys.EE.SetGpr(21, new EmotionEngine.Gpr128 { Lo = 0 });
                    sys.EE.PC = FreelistSuccessStore;
                    _freelistEscapes++;
                }
            }
        }

        // Stack death residual: PC lands in path ASCII (STREE0.TRE / GAME.TXT) as code.
        if (sys.Scheduler.MasterCycles >= FreelistEscapeMinCycles && LooksLikePathAsciiPc(sys, pc))
            MaybeRescueStackDeath(sys, pc);
    }

    private void MaybeRescueStackDeath(Ps2System sys, uint pc)
    {
        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFu);
        uint resume = 0;
        if (sp is >= 0x00100000 and < SystemMemory.RDRAM_SIZE)
        {
            for (uint off = 0; off <= 0x80; off += 4)
            {
                uint cand = sys.Memory.Read32(sp + off);
                if ((cand & 3) == 0 && sys.Memory.IsLikelyEeCode(cand)
                    && (cand & 0x1FFFFFFFu) is >= 0x00100000 and < 0x00400000)
                {
                    resume = cand & 0x1FFFFFFFu;
                    break;
                }
            }
        }
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFu);
        if (resume == 0 && (ra & 3) == 0 && sys.Memory.IsLikelyEeCode(ra)
            && ra is >= 0x00100000 and < 0x00400000)
            resume = ra;
        if (resume == 0 && sys.Memory.IsLikelyEeCode(0x0011C200u))
            resume = 0x0011C200u;
        if (resume == 0) return;

        sys.EE.PC = resume;
        _stackRescues++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _stackRescues <= 16)
            Console.Error.WriteLine(
                $"[VEXX] stack-death rescue #{_stackRescues} from=0x{pc:X} -> 0x{resume:X} cyc={sys.Scheduler.MasterCycles}");
    }

    private static bool LooksLikePathAsciiPc(Ps2System sys, uint pc)
    {
        if (pc < 0x00300000 || pc + 4 >= SystemMemory.RDRAM_SIZE) return false;
        if (sys.Memory.IsLikelyEeCode(pc)) return false;
        int printable = 0;
        for (int i = 0; i < 4; i++)
        {
            byte b = sys.Memory.Read8(pc + (uint)i);
            if (b is >= 0x20 and <= 0x7E) printable++;
        }
        if (printable < 3) return false;
        for (int i = 0; i < 12; i++)
        {
            byte b = sys.Memory.Read8(pc + (uint)i);
            if (b is (byte)'.' or (byte)'\\' or (byte)'/' or (byte)';') return true;
        }
        uint w = sys.Memory.Read32(pc);
        // "STRE" "GAME" "e0.t" fragments from STREE0.TRE
        if (w is 0x45525453u or 0x454D4147u or 0x742E3065u) return true;
        return printable >= 4;
    }

    public static void PlantIopRpVersion(Ps2System sys)
    {
        WriteCString4(sys, IopVersionCellA, "2520");
        WriteCString4(sys, IopVersionCellB, "2520");
    }

    public static void PlantStringHeapHook(Ps2System sys)
    {
        if (sys.Memory.Read32(MallocStub) == 0)
            PlantCrtMallocTable(sys);
        sys.Memory.Write32(StringAllocHook, MallocStub);
        sys.Memory.Write32(StringFreeHook, 0x001CEBC0); // CRT free trampoline
        sys.Memory.Write32(SmallPoolRoot, 0);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine($"[VEXX] string-hook malloc=0x{MallocStub:X} free→CRT; pool cleared");
    }

    public static void PlantCrtMallocTable(Ps2System sys)
    {
        uint cur = BumpCursorCell, stub = MallocStub, end = BumpArenaEnd;
        uint existing = sys.Memory.Read32(cur);
        if (existing < BumpArenaBase || existing >= BumpArenaEnd)
            sys.Memory.Write32(cur, BumpArenaBase);

        uint[] mallocOps =
        {
            0x3C080000u | (cur >> 16), 0x35080000u | (cur & 0xFFFF), 0x8D020000u,
            0x2489000Fu, 0x00094902u, 0x00094900u, 0x00495021u,
            0x3C0B0000u | (end >> 16), 0x356B0000u | (end & 0xFFFF),
            0x014B602Bu, 0x11800004u, 0x00000000u, 0xAD0A0000u,
            0x03E00008u, 0x00000000u, 0x03E00008u, 0x0000102Du,
        };
        for (int i = 0; i < mallocOps.Length; i++)
            sys.Memory.Write32(stub + (uint)(i * 4), mallocOps[i]);

        sys.Memory.Write32(FreeStub + 0, 0x03E00008u);
        sys.Memory.Write32(FreeStub + 4, 0x00000000u);
        sys.Memory.Write32(ReallocStub + 0, 0x00A0202Du);
        sys.Memory.Write32(ReallocStub + 4, 0x08000000u | ((MallocStub >> 2) & 0x03FFFFFF));
        sys.Memory.Write32(ReallocStub + 8, 0x00000000u);
        sys.Memory.Write32(CrtMallocSlot, MallocStub);
        sys.Memory.Write32(CrtFreeSlot, FreeStub);
        sys.Memory.Write32(CrtReallocSlot, ReallocStub);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine($"[VEXX] CRT malloc table → bump 0x{BumpArenaBase:X}-0x{BumpArenaEnd:X}");
    }

    public static uint HostBumpAlloc(Ps2System sys, uint size)
    {
        uint cur = sys.Memory.Read32(BumpCursorCell);
        if (cur < BumpArenaBase || cur >= BumpArenaEnd)
        {
            cur = BumpArenaBase;
            sys.Memory.Write32(BumpCursorCell, cur);
        }
        uint aligned = (size + 15u) & ~15u;
        if (aligned == 0) aligned = 16;
        ulong next = (ulong)cur + aligned;
        if (next >= BumpArenaEnd) return 0;
        sys.Memory.Write32(BumpCursorCell, (uint)next);
        return cur;
    }

    public static bool MaybeFixSearchFilePathLayout(Ps2System sys, uint buf)
    {
        if (buf + 0x120 >= SystemMemory.RDRAM_SIZE) return false;
        byte at24 = sys.Memory.Read8(buf + 0x24);
        if (at24 is not ((byte)'\\' or (byte)'/' or (>= (byte)'A' and <= (byte)'Z')
            or (>= (byte)'a' and <= (byte)'z') or (byte)'$'))
            return false;

        var tmp = new byte[0x100];
        int len = 0;
        for (; len < tmp.Length; len++)
        {
            byte b = sys.Memory.Read8(buf + 0x24 + (uint)len);
            tmp[len] = b;
            if (b == 0) { len++; break; }
        }
        if (len <= 1) return false;

        // Slide when +0x20 empty OR stale (different leaf than +0x24) — STREE0 after GAME.TXT.
        string path24 = Encoding.ASCII.GetString(tmp, 0, Math.Max(0, len - 1));
        string path20 = ReadCStringStatic(sys, buf + 0x20, 128);
        string leaf24 = NormalizeSearchLeaf(path24);
        string leaf20 = NormalizeSearchLeaf(path20);
        if (!IsPlausibleSearchLeaf(leaf24)) return false;
        bool needSlide = path20.Length == 0 || (leaf24.Length > 0 && leaf24 != leaf20);
        if (!needSlide) return false;

        for (int i = 0; i < len; i++)
            sys.Memory.Write8(buf + 0x20 + (uint)i, tmp[i]);
        // New path: clear stale lsn/size so plant / HLE rewrite for STREE0 etc.
        if (leaf24.Length > 0 && leaf24 != leaf20)
        {
            sys.Memory.Write32(buf + 0, 0);
            sys.Memory.Write32(buf + 4, 0);
        }

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine($"[VEXX] SearchFile path slide @0x{buf:X} → \"{path24}\"");
        return true;
    }

    public bool MaybePlantSearchFileResult(Ps2System sys, uint buf)
    {
        string? isoPath = sys.Cdvd.MountedPath;
        if (string.IsNullOrEmpty(isoPath) || buf + 0x30 >= SystemMemory.RDRAM_SIZE) return false;

        string name = ReadCString(sys, buf + 0x20, 128);
        if (name.Length == 0) name = ReadCString(sys, buf + 0x24, 128);
        if (name.Length == 0) return false;

        name = NormalizeSearchLeaf(name);
        if (!IsPlausibleSearchLeaf(name)) return false;
        if (name.Contains('\\') || name.Contains('/') || name.StartsWith('$')) return false;

        // Re-plant when lsn empty OR planted leaf at +8 mismatches requested path (STREE0).
        string plantedLeaf = ReadCString(sys, buf + 8, 16);
        uint curLsn = sys.Memory.Read32(buf);
        if (curLsn != 0 && string.Equals(plantedLeaf, name, StringComparison.OrdinalIgnoreCase))
            return false;
        if (curLsn != 0 && plantedLeaf.Length > 0
            && name.StartsWith(plantedLeaf, StringComparison.OrdinalIgnoreCase))
            return false;

        if (_isoVol == null || _isoVolPath != isoPath)
        {
            try { _isoVol?.Disc?.Dispose(); } catch { }
            _isoVol = Iso9660.OpenFile(isoPath);
            _isoVolPath = isoPath;
        }
        if (_isoVol == null) return false;

        try
        {
            var entry = Iso9660.FindFile(_isoVol, name)
                ?? Iso9660.FindFile(_isoVol, System.IO.Path.GetFileName(name));
            if (entry == null) return false;

            sys.Memory.Write32(buf + 0, entry.ExtentLba);
            sys.Memory.Write32(buf + 4, entry.Size);
            string leaf = entry.Name.Length > 15 ? entry.Name[..15] : entry.Name;
            for (int i = 0; i < 16; i++)
                sys.Memory.Write8(buf + 8 + (uint)i, i < leaf.Length ? (byte)leaf[i] : (byte)0);

            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
                Console.Error.WriteLine(
                    $"[VEXX] SearchFile plant @0x{buf:X} \"{name}\" lsn={entry.ExtentLba} size={entry.Size}");
            return true;
        }
        catch { return false; }
    }

    private static string NormalizeSearchLeaf(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        int colon = name.IndexOf(':');
        if (colon >= 0) name = name[(colon + 1)..];
        name = name.TrimStart('\\', '/');
        int semi = name.IndexOf(';');
        if (semi >= 0) name = name[..semi];
        return name.Trim();
    }

    /// <summary>ISO leaf like GAME.TXT / STREE0.TRE — not "." or empty junk.</summary>
    private static bool IsPlausibleSearchLeaf(string leaf)
    {
        if (string.IsNullOrEmpty(leaf) || leaf.Length is < 3 or > 64) return false;
        if (leaf is "." or "..") return false;
        bool hasAlnum = false, hasDot = false;
        foreach (char c in leaf)
        {
            if (char.IsAsciiLetterOrDigit(c)) hasAlnum = true;
            else if (c == '.') hasDot = true;
            else if (c is not ('_' or '-' or ' ')) return false;
        }
        return hasAlnum && hasDot;
    }

    private static string ReadCStringStatic(Ps2System sys, uint addr, int max)
    {
        var sb = new StringBuilder(max);
        for (int i = 0; i < max; i++)
        {
            byte b = sys.Memory.Read8(addr + (uint)i);
            if (b == 0) break;
            if (b is < 32 or >= 127) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static bool VersionCellsOk(Ps2System sys) =>
        ReadCString4(sys, IopVersionCellA) == "2520" || ReadCString4(sys, IopVersionCellB) == "2520";

    private static bool PathStubActive(Ps2System sys, uint entry) =>
        (sys.Memory.Read32(entry) >> 26) == 2;

    public static void PatchNullPathBasename(Ps2System sys)
    {
        PlantOne(sys, PathBasenameA, StubA);
        PlantOne(sys, PathBasenameB, StubB);
    }

    private static void PlantOne(Ps2System sys, uint entry, uint stub)
    {
        uint w0 = sys.Memory.Read32(entry);
        uint w1 = sys.Memory.Read32(entry + 4);
        if ((w0 >> 26) == 2) return;
        uint cont = (entry + 8) >> 2;
        sys.Memory.Write32(stub + 0x00, 0x10800005u);
        sys.Memory.Write32(stub + 0x04, 0x00000000u);
        sys.Memory.Write32(stub + 0x08, w0);
        sys.Memory.Write32(stub + 0x0C, w1);
        sys.Memory.Write32(stub + 0x10, 0x08000000u | (cont & 0x03FFFFFF));
        sys.Memory.Write32(stub + 0x14, 0x00000000u);
        sys.Memory.Write32(stub + 0x18, 0x03E00008u);
        sys.Memory.Write32(stub + 0x1C, 0x0000102Du);
        sys.Memory.Write32(entry + 0x00, 0x08000000u | ((stub >> 2) & 0x03FFFFFF));
        sys.Memory.Write32(entry + 0x04, 0x00000000u);
    }

    private static string ReadCString4(Ps2System sys, uint addr)
    {
        var chars = new char[4];
        for (int i = 0; i < 4; i++)
        {
            byte b = sys.Memory.Read8(addr + (uint)i);
            if (b == 0) return new string(chars, 0, i);
            chars[i] = (char)b;
        }
        return new string(chars);
    }

    private static void WriteCString4(Ps2System sys, uint addr, string s)
    {
        for (int i = 0; i < 4; i++)
            sys.Memory.Write8(addr + (uint)i, i < s.Length ? (byte)s[i] : (byte)0);
    }

    private static string ReadCString(Ps2System sys, uint addr, int max)
    {
        var sb = new StringBuilder(max);
        for (int i = 0; i < max; i++)
        {
            byte b = sys.Memory.Read8(addr + (uint)i);
            if (b == 0) break;
            if (b is < 32 or >= 127) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }
}
