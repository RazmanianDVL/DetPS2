using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DetPS2.Core;

// Headless CLI
//   detps2                       — self-check
//   detps2 commercial-boot [user-media.json] [out.json]
//   detps2 dump-spine [user-media.json]
//   detps2 play-path
//   detps2 majority-campaign [out.md]
//   detps2 majority-catalog [out.md]
//   detps2 commercial-checklist
//   detps2 netplay-soak [frames]
//   detps2 netplay-cert [frames]

if (args.Length > 0 && args[0].Equals("commercial-boot", StringComparison.OrdinalIgnoreCase))
{
    string? mediaPath = args.Length > 1 ? args[1] : null;
    string? outPath = args.Length > 2 ? args[2] : null;

    UserMediaConfig cfg = mediaPath != null
        ? UserMediaConfig.Load(mediaPath)
        : UserMediaConfig.LoadDefault();

    Console.WriteLine(VersionInfo.Banner);
    Console.WriteLine($"Media: bios={cfg.HasBios} titles={cfg.ExistingTitleCount}");
    var report = CommercialBootRunner.Run(cfg, allowSyntheticFallback: true);
    Console.WriteLine(report.Summary);

    if (!string.IsNullOrEmpty(outPath))
    {
        CommercialBootRunner.WriteReport(report, outPath);
        Console.WriteLine($"Wrote {outPath}");
    }
    else
    {
        string defaultOut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DetPS2", "last-boot-report.json");
        CommercialBootRunner.WriteReport(report, defaultOut);
        Console.WriteLine($"Wrote {defaultOut}");
    }

    int code = report.P0Plus >= 1 ? 0 : 1;
    Environment.Exit(code);
}

if (args.Length > 0 && args[0].Equals("dump-spine", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(VersionInfo.Banner);
    UserMediaConfig cfg = args.Length > 1
        ? UserMediaConfig.Load(args[1])
        : UserMediaConfig.LoadDefault();
    var spine = DumpBootSpine.Run(cfg, allowSynthetic: true);
    Console.WriteLine(DumpBootSpine.Format(spine));
    string outDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DetPS2");
    DumpBootSpine.WriteBlockerMarkdown(spine, Path.Combine(outDir, "boot-blockers.md"));
    CommercialBootRunner.WriteReport(spine.Boot, Path.Combine(outDir, "last-boot-report.json"));
    Environment.Exit(spine.SpineInfraOk ? 0 : 1);
}

if (args.Length > 0 && args[0].Equals("elf-info", StringComparison.OrdinalIgnoreCase))
{
    // Diagnostic: is the boot ELF from user-media.json a stripped retail build or a
    // debug build (unstripped symbols / .debug sections)? Helps explain otherwise-odd
    // runtime behavior like heavy Deci2Call (Sony's dev-kit debug protocol) usage.
    UserMediaConfig cfg = args.Length > 1 && !args[1].StartsWith("--")
        ? UserMediaConfig.Load(args[1])
        : UserMediaConfig.LoadDefault();
    if (cfg.Titles.Count == 0) { Console.WriteLine("No titles in user-media.json"); Environment.Exit(1); }
    var t = cfg.Titles[0];
    var vol = Iso9660.OpenFile(t.Path);
    if (vol == null) { Console.WriteLine("Could not open ISO"); Environment.Exit(1); }
    byte[]? cnfBytes = Iso9660.ReadFile(vol, "SYSTEM.CNF");
    string boot2 = cnfBytes != null ? SystemCnf.Parse(System.Text.Encoding.ASCII.GetString(cnfBytes)).Boot2 : "";
    string bootFile = boot2;
    int semi = bootFile.IndexOf(';');
    if (semi >= 0) bootFile = bootFile[..semi];
    int slash = bootFile.LastIndexOfAny(new[] { '/', '\\' });
    if (slash >= 0) bootFile = bootFile[(slash + 1)..];
    Console.WriteLine($"BOOT2={boot2} -> file={bootFile}");
    byte[]? elf = Iso9660.ReadFile(vol, bootFile);
    if (elf == null) { Console.WriteLine("Could not read boot ELF"); Environment.Exit(1); }
    Console.WriteLine($"ELF size={elf.Length} bytes");

    uint shoff = BitConverter.ToUInt32(elf, 0x20);
    ushort shentsize = BitConverter.ToUInt16(elf, 0x2E);
    ushort shnum = BitConverter.ToUInt16(elf, 0x30);
    ushort shstrndx = BitConverter.ToUInt16(elf, 0x32);
    Console.WriteLine($"shoff=0x{shoff:X} shentsize={shentsize} shnum={shnum} shstrndx={shstrndx}");
    if (shoff == 0 || shnum == 0) { Console.WriteLine("No section headers (fully stripped)."); Environment.Exit(0); }

    uint strTabOff = BitConverter.ToUInt32(elf, (int)(shoff + shstrndx * shentsize + 16));
    bool hasSymtab = false, hasDebug = false;
    int symCount = 0;
    for (int i = 0; i < shnum; i++)
    {
        int off = (int)shoff + i * shentsize;
        uint nameOff = BitConverter.ToUInt32(elf, off);
        uint type = BitConverter.ToUInt32(elf, off + 4);
        uint secOffset = BitConverter.ToUInt32(elf, off + 16);
        uint size = BitConverter.ToUInt32(elf, off + 20);
        uint entsize = BitConverter.ToUInt32(elf, off + 36);
        int nameStart = (int)(strTabOff + nameOff);
        int nameEnd = nameStart;
        while (nameEnd < elf.Length && elf[nameEnd] != 0) nameEnd++;
        string name = nameStart < elf.Length ? System.Text.Encoding.ASCII.GetString(elf, nameStart, nameEnd - nameStart) : "?";
        if (name.Length > 0) Console.WriteLine($"  [{i}] {name} type={type} offset=0x{secOffset:X} size={size}");
        if (type == 2) { hasSymtab = true; symCount = entsize > 0 ? (int)(size / entsize) : 0; } // SHT_SYMTAB
        if (name.Contains("debug", StringComparison.OrdinalIgnoreCase)) hasDebug = true;

        if (name == ".vutext" && size >= 8 && secOffset + size <= elf.Length)
        {
            var words = new uint[size / 4];
            Buffer.BlockCopy(elf, (int)secOffset, words, 0, words.Length * 4);
            var stats = VectorUnit.AnalyzeMicrocode(words);
            double upperPct = stats.Instructions > 0 ? 100.0 * (stats.Instructions - stats.UnmappedUpper) / stats.Instructions : 0;
            double lowerPct = stats.Instructions > 0 ? 100.0 * (stats.Instructions - stats.UnmappedLower) / stats.Instructions : 0;
            Console.WriteLine($"    .vutext decode: instructions={stats.Instructions} " +
                $"upperRecognized={upperPct:F1}% ({stats.Instructions - stats.UnmappedUpper}/{stats.Instructions}) " +
                $"lowerRecognized={lowerPct:F1}% ({stats.Instructions - stats.UnmappedLower}/{stats.Instructions})");
            if (stats.UnmappedLowerHistogram != null)
                foreach (var kv in stats.UnmappedLowerHistogram)
                    if (kv.Value > 20)
                        Console.WriteLine($"      unmapped lower op=0x{kv.Key:X2} count={kv.Value}");
        }
    }
    Console.WriteLine($"hasSymtab={hasSymtab} symCount={symCount} hasDebugSection={hasDebug}");
    Console.WriteLine(hasSymtab || hasDebug ? "=> looks like a DEBUG/unstripped build" : "=> looks like a stripped retail build");
    Environment.Exit(0);
}

if (args.Length > 0 && args[0].Equals("blocker-trace", StringComparison.OrdinalIgnoreCase))
{
    // Generic PC-level trace for whichever title(s) are in user-media.json — no hardcoded
    // title/path assumptions, so it stays useful across the whole compat campaign.
    Console.WriteLine(VersionInfo.Banner);
    UserMediaConfig cfg = args.Length > 1 && !args[1].StartsWith("--")
        ? UserMediaConfig.Load(args[1])
        : UserMediaConfig.LoadDefault();
    ulong cycles = 5_000_000;
    foreach (var a in args)
        if (a.StartsWith("--cycles=") && ulong.TryParse(a.AsSpan(9), out var c)) cycles = c;

    if (!cfg.HasBios) { Console.WriteLine("No BIOS in user-media.json"); Environment.Exit(1); }
    foreach (var title in cfg.Titles)
    {
        if (!title.Exists) { Console.WriteLine($"[{title.Id}] missing: {title.Path}"); continue; }
        var traceSys = new Ps2System();
        traceSys.Telemetry.Reset();
        traceSys.LoadBios(cfg.BiosPath);
        string msg;
        if ((title.Kind ?? "iso").ToLowerInvariant() == "elf")
        {
            var load = traceSys.LoadElf(File.ReadAllBytes(title.Path));
            msg = $"ELF entry=0x{load.Entry:X8}";
        }
        else
        {
            msg = traceSys.BootDiscFile(title.Path).Message;
        }
        Console.WriteLine($"[{title.Id}] {msg}");
        traceSys.RunFor(cycles);
        Console.WriteLine($"  after {cycles} cyc: PC=0x{traceSys.EE.PC:X8} hits={traceSys.Telemetry.TotalHits} unique={traceSys.Telemetry.UniqueKeys}");
        Console.WriteLine($"  px={traceSys.Gs.PixelsWritten} gifPath3={traceSys.Gif.Path3Transfers} dmac={traceSys.Dmac.TransfersCompleted} sifBytes={traceSys.Sif.BytesTransferred} syscalls={traceSys.Hle.SyscallCount} spu2Writes={traceSys.Spu2.Writes} spu2Samples={traceSys.Spu2.SamplesGenerated} cdvdSectors={traceSys.Cdvd.SectorsRead}");
        if (traceSys.Hle.Sony != null)
        {
            Console.WriteLine("  top syscalls:");
            foreach (var kv in traceSys.Hle.Sony.SyscallHistogram)
                if (kv.Value > 100)
                    Console.WriteLine($"    0x{kv.Key:X2} x{kv.Value}");
        }
        foreach (var ev in traceSys.Telemetry.SnapshotEvents())
            Console.WriteLine($"    cyc={ev.Cycle,10} pc=0x{ev.Pc:X8} {ev.Kind,-16} key=0x{ev.Key:X8} {ev.Detail}");

        foreach (var a in args)
        {
            if (!a.StartsWith("--dump=")) continue;
            var parts = a.Substring(7).Split(':');
            uint start = Convert.ToUInt32(parts[0], 16);
            uint len = parts.Length > 1 ? Convert.ToUInt32(parts[1], 16) : 0x40u;
            Console.WriteLine($"  dump 0x{start:X8}..0x{start + len:X8}:");
            for (uint addr = start; addr < start + len; addr += 4)
                Console.WriteLine($"    {addr:X8}: {traceSys.Memory.Read32(addr):X8}");
            Console.WriteLine($"  GPRs: v0={traceSys.EE.GetGpr(2).Lo:X} v1={traceSys.EE.GetGpr(3).Lo:X} a0={traceSys.EE.GetGpr(4).Lo:X} a1={traceSys.EE.GetGpr(5).Lo:X} " +
                $"a2={traceSys.EE.GetGpr(6).Lo:X} a3={traceSys.EE.GetGpr(7).Lo:X} t0={traceSys.EE.GetGpr(8).Lo:X} t1={traceSys.EE.GetGpr(9).Lo:X} " +
                $"s0={traceSys.EE.GetGpr(16).Lo:X} s1={traceSys.EE.GetGpr(17).Lo:X} sp={traceSys.EE.GetGpr(29).Lo:X} ra={traceSys.EE.GetGpr(31).Lo:X}");
            if (traceSys.EE.GetGpr(29).Lo != 0)
            {
                uint sp = (uint)traceSys.EE.GetGpr(29).Lo;
                for (uint off = 0; off < 0x80; off += 4)
                    Console.WriteLine($"    sp+0x{off:X2} (0x{sp + off:X8}): {traceSys.Memory.Read32(sp + off):X8}");
            }
        }

        foreach (var a in args)
        {
            if (!a.StartsWith("--trace-window=")) continue;
            ulong window = ulong.TryParse(a.AsSpan(15), out var w) ? w : 3000ul;
            traceSys.Tracer.MaxEntries = (int)Math.Min(window + 16, int.MaxValue);
            traceSys.Tracer.Enable();
            traceSys.RunFor(window);
            traceSys.Tracer.Disable();
            Console.WriteLine($"  trace-window: {traceSys.Tracer.Count} entries captured after cycle {cycles}");
            var pcCounts = new Dictionary<ulong, int>();
            foreach (var e in traceSys.Tracer.Entries) pcCounts[e.Pc] = pcCounts.GetValueOrDefault(e.Pc) + 1;
            Console.WriteLine($"  unique PCs in window: {pcCounts.Count}");
            foreach (var kv in pcCounts.OrderBy(k => k.Key))
                Console.WriteLine($"    pc=0x{kv.Key:X8} hits={kv.Value} op=0x{traceSys.Memory.Read32((uint)kv.Key):X8}");
        }
    }
    Environment.Exit(0);
}

if (args.Length > 0 && args[0].Equals("play-path", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(VersionInfo.Banner);
    var play = PlayPathCampaign.Run();
    Console.WriteLine(PlayPathCampaign.Format(play));
    Environment.Exit(play.GateMet ? 0 : 1);
}

if (args.Length > 0 && args[0].Equals("majority-campaign", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(VersionInfo.Banner);
    var report = MajorityCampaign.RunScoredCampaign();
    Console.WriteLine(MajorityCampaign.FormatReport(report));
    string outPath = args.Length > 1
        ? args[1]
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DetPS2", "majority-report.md");
    MajorityCampaign.WriteReportMarkdown(report, outPath);
    string dxPath = Path.Combine(Path.GetDirectoryName(outPath) ?? ".", "DX_LIST.md");
    MajorityCampaign.WriteDxList(report, dxPath);
    Console.WriteLine($"Wrote {outPath}");
    Console.WriteLine($"Wrote {dxPath}");
    Environment.Exit(report.MajorityGateMet || report.ScoredMajorityGateMet ? 0 : 1);
}

if (args.Length > 0 && args[0].Equals("majority-catalog", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(VersionInfo.Banner);
    var report = MajorityCatalog.RunFull(UserMediaConfig.LoadDefault());
    Console.WriteLine(MajorityCatalog.Format(report));
    string outPath = args.Length > 1
        ? args[1]
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DetPS2", "majority-catalog.md");
    string dxPath = Path.Combine(Path.GetDirectoryName(outPath) ?? ".", "DX_LIST.md");
    MajorityCatalog.Publish(report, outPath, dxPath);
    Console.WriteLine($"Wrote {outPath}");
    Environment.Exit(report.MajorityGateMet ? 0 : 1);
}

if (args.Length > 0 && args[0].Equals("commercial-checklist", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(VersionInfo.Banner);
    var result = CommercialSmokeChecklist.Run();
    Console.WriteLine(CommercialSmokeChecklist.Format(result));
    Environment.Exit(result.AllRequiredPassed ? 0 : 1);
}

if (args.Length > 0 && args[0].Equals("netplay-soak", StringComparison.OrdinalIgnoreCase))
{
    int frames = args.Length > 1 && int.TryParse(args[1], out int f) ? f : 120;
    Console.WriteLine(VersionInfo.Banner);
    var soak = ProductionRollbackPeer.SoakTwoPlayer(frames, delay: 2, frameAdvantage: 1);
    Console.WriteLine($"soak frames={soak.Frames} sync={soak.Sync} rb={soak.Rollbacks} certified={soak.Certified}");
    Console.WriteLine(soak.NetGraph);
    Environment.Exit(soak.Sync ? 0 : 1);
}

if (args.Length > 0 && args[0].Equals("netplay-cert", StringComparison.OrdinalIgnoreCase))
{
    int frames = args.Length > 1 && int.TryParse(args[1], out int f) ? f : 600;
    Console.WriteLine(VersionInfo.Banner);
    var cert = NetplayCertification.Run(frames);
    Console.WriteLine(NetplayCertification.Format(cert));
    string outPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DetPS2", "NETPLAY_CERTIFIED.md");
    NetplayCertification.Publish(cert, outPath);
    Console.WriteLine($"Wrote {outPath}");
    Environment.Exit(cert.ProductionGateMet ? 0 : 1);
}

// detps2 probe-iso — list ISO files matching MIDWAY/IOP
if (args.Length > 0 && args[0].Equals("probe-iso", StringComparison.OrdinalIgnoreCase))
{
    string iso = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    var vol = Iso9660.OpenFile(iso);
    if (vol == null) { Console.WriteLine("bad iso"); Environment.Exit(2); }
    foreach (var f in vol.Files)
    {
        string u = f.Path.ToUpperInvariant();
        if (u.Contains("MIDWAY") || u.Contains("IOP") || u.Contains(".IRX") || u.Contains(".SFD")
            || u.Contains("FRONT") || u.Contains("MOVIE") || u.Contains("LOGO") || u.Contains("JARVOS"))
            Console.WriteLine($"{(f.IsDirectory ? "DIR " : "FILE")} {f.Path} size={f.Size} lba={f.ExtentLba}");
    }
    Console.WriteLine($"total entries={vol.Files.Count}");
    Environment.Exit(0);
}

// detps2 probe-str — scan RDRAM for path-like strings after boot
if (args.Length > 0 && args[0].Equals("probe-str", StringComparison.OrdinalIgnoreCase))
{
    string bios = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PCSX2", "bios", "Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin");
    string iso = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    var p = new Ps2System();
    p.LoadBios(bios);
    p.BootDiscFile(iso);
    p.RunFor(10_000_000);
    string[] needles = { "cdrom", "CDROM", "host", "logo", "LOGO", "midway", "MIDWAY", ".irx", ".IRX", ".img", ".IMG", "SLUS", "BOOT", "SYSTEM" };
    for (uint a = 0x100000; a < 0x800000; a += 4)
    {
        // quick check first byte
        byte b0 = (byte)p.Memory.Read8(a);
        if (b0 is < 32 or > 126) continue;
        // build short string
        var sb = new System.Text.StringBuilder(64);
        for (int i = 0; i < 48; i++)
        {
            byte b = (byte)p.Memory.Read8(a + (uint)i);
            if (b == 0) break;
            if (b < 32 || b > 126) { sb.Clear(); break; }
            sb.Append((char)b);
        }
        string s = sb.ToString();
        if (s.Length < 4) continue;
        foreach (var n in needles)
        {
            if (s.Contains(n, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"0x{a:X8}: {s}");
                break;
            }
        }
    }
    Console.WriteLine($"px={p.Gs.PixelsWritten} gif={p.Gif.Path3Transfers} PC=0x{p.EE.PC:X8}");
    Environment.Exit(0);
}

// detps2 probe-frame — boot MK and write framebuffer PPM + syscall hist
if (args.Length > 0 && args[0].Equals("probe-frame", StringComparison.OrdinalIgnoreCase))
{
    string bios = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PCSX2", "bios", "Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin");
    string iso = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    var p = new Ps2System();
    p.LoadBios(bios);
    p.BootDiscFile(iso);
    p.RunFor(8_000_000);
    string outPpm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DetPS2", "mk-frame.ppm");
    Directory.CreateDirectory(Path.GetDirectoryName(outPpm)!);
    p.Gs.SaveFramebufferAsPPM(outPpm);
    Console.WriteLine($"px={p.Gs.PixelsWritten} prims={p.Gs.PrimitivesDrawn} gifP3={p.Gif.Path3Transfers} dmac={p.Dmac.TransfersCompleted}");
    Console.WriteLine($"PC=0x{p.EE.PC:X8} sys={p.Hle.SyscallCount} wrote {outPpm}");
    Console.WriteLine($"assist={p.MidwayAssist.Status} logoFrames={p.MidwayAssist.LogoFramesTotal} presented={p.MidwayAssist.FramesPresented} workDone={p.MidwayAssist.WorkCompletions}");
    uint pc = (uint)(p.EE.PC & 0x1FFFFF00);
    Console.WriteLine($"code near PC:");
    for (uint a = pc; a < pc + 0x80; a += 4)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    if (p.Hle.Sony != null)
        foreach (var kv in p.Hle.Sony.SyscallHistogram.OrderByDescending(k => k.Value).Take(15))
            Console.WriteLine($"  sc 0x{kv.Key:X2} x{kv.Value}");
    // Continue until logo finishes (or timeout)
    for (int i = 0; i < 80; i++)
    {
        p.RunFor(1_000_000);
        Console.WriteLine($"  +{i + 1}M PC=0x{p.EE.PC:X8} px={p.Gs.PixelsWritten} " +
                          $"assist={p.MidwayAssist.Status} logo={p.MidwayAssist.LogoFrame}/{p.MidwayAssist.LogoFramesTotal} pres={p.MidwayAssist.FramesPresented}");
        if (p.MidwayAssist.Status is "logo-done" or "synthetic-logo")
            break;
        // Snapshot mid-logo
        if (p.MidwayAssist.LogoFrame == 5 || p.MidwayAssist.LogoFrame == 20)
            p.Gs.SaveFramebufferAsPPM(outPpm.Replace(".ppm", $"-f{p.MidwayAssist.LogoFrame}.ppm"));
    }
    p.Gs.SaveFramebufferAsPPM(outPpm);
    // Also write a binary PPM for quick viewing via ffmpeg
    string outPng = Path.Combine(Path.GetDirectoryName(outPpm)!, "mk-logo.png");
    try
    {
        string? ff = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ffmpeg", "bin", "ffmpeg.exe");
        if (File.Exists(ff))
        {
            // Convert last P3 ppm is awkward; write raw from GetFramebuffer instead via P6
            string p6 = outPpm.Replace(".ppm", "-bin.ppm");
            WriteP6(p6, p.Gs);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ff,
                Arguments = $"-y -i \"{p6}\" -update 1 \"{outPng}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(15000);
        }
    }
    catch { /* optional */ }
    Console.WriteLine($"final assist={p.MidwayAssist.Status} presented={p.MidwayAssist.FramesPresented} px={p.Gs.PixelsWritten}");
    Console.WriteLine($"wrote {outPpm}");
    Environment.Exit(p.MidwayAssist.FramesPresented > 0 || p.Gs.PixelsWritten > 0 ? 0 : 1);

    static void WriteP6(string path, Gs gs)
    {
        using var fs = File.Create(path);
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{Gs.FB_WIDTH} {Gs.FB_HEIGHT}\n255\n");
        fs.Write(header);
        var fb = gs.GetFramebufferSpan();
        Span<byte> pix = stackalloc byte[3];
        for (int i = 0; i < fb.Length; i++)
        {
            uint p = fb[i];
            pix[0] = (byte)((p >> 16) & 0xFF);
            pix[1] = (byte)((p >> 8) & 0xFF);
            pix[2] = (byte)(p & 0xFF);
            fs.Write(pix);
        }
    }
}

// detps2 probe-path — dump natural path to SIF init
if (args.Length > 0 && args[0].Equals("probe-path", StringComparison.OrdinalIgnoreCase))
{
    string bios = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PCSX2", "bios", "Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin");
    string iso = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    var p = new Ps2System();
    p.LoadBios(bios);
    p.BootDiscFile(iso);
    Console.WriteLine("0x213180-0x213400:");
    for (uint a = 0x213180; a < 0x213400; a += 4)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    Console.WriteLine("0x212F70:");
    for (uint a = 0x212F70; a < 0x213000; a += 4)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    Console.WriteLine($"fnptr 0x205DE8={p.Memory.Read32(0x205DE8):X8} 0x205DF0={p.Memory.Read32(0x205DF0):X8}");
    // Natural boot WITHOUT commercial kicks: temporarily clear by using short run before 1.5M
    // Actually kicks at 1.5M - run with tracking
    bool[] hit = new bool[4];
    for (int i = 0; i < 8000; i++)
    {
        p.RunFor(500);
        uint pc = (uint)(p.EE.PC & 0x1FFFFFFF);
        if (pc is >= 0x212F70 and < 0x213800) hit[0] = true;
        if (pc is >= 0x482E98 and < 0x483000) hit[1] = true;
        if (pc is >= 0x2131C0 and < 0x2131E0) hit[2] = true;
        if (pc is >= 0x4800E0 and < 0x480100) hit[3] = true;
    }
    Console.WriteLine($"hits: main212F70={hit[0]} sifInit={hit[1]} call2131C8={hit[2]} flush={hit[3]}");
    Console.WriteLine($"final PC=0x{p.EE.PC:X8} c={p.MasterCycles} 563FE4={p.Memory.Read32(0x563FE4):X8}");
    Console.WriteLine($"77A080={p.Memory.Read32(0x77A080):X8} sifDma={p.Hle.Sony?.SifDmaCalls}");
    Environment.Exit(0);
}

// detps2 probe-callers — who calls SIF init / flag 563FE4
if (args.Length > 0 && args[0].Equals("probe-callers", StringComparison.OrdinalIgnoreCase))
{
    string bios = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PCSX2", "bios", "Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin");
    string iso = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    var p = new Ps2System();
    p.LoadBios(bios);
    p.BootDiscFile(iso);
    uint[] jals = { 0x0C120BA6, /*482E98*/ 0x0C120BA0, /*482E80*/ 0x0C120B9A /*482E68*/ };
    string[] names = { "482E98", "482E80", "482E68" };
    for (int i = 0; i < jals.Length; i++)
    {
        Console.WriteLine($"JAL {names[i]}:");
        for (uint a = 0x100000; a < 0x600000; a += 4)
            if (p.Memory.Read32(a) == jals[i])
                Console.WriteLine($"  from 0x{a:X8}");
    }
    // refs to 0x563FE4: lui v?,0x56 / lw off 0x3FE4
    Console.WriteLine("refs 0x563FE4:");
    for (uint a = 0x100000; a < 0x600000; a += 4)
    {
        uint w = p.Memory.Read32(a);
        if ((w & 0xFFE0FFFF) == 0x3C000056) // lui ?, 0x56
        {
            uint n = p.Memory.Read32(a + 4);
            if ((n & 0xFFFF) == 0x3FE4)
                Console.WriteLine($"  @{a:X8}: {w:X8} {n:X8} {p.Memory.Read32(a + 8):X8}");
        }
    }
    // Disable commercial kicks by not running long - just dump
    // Find main path after CRT0: first jal after 11C178
    Console.WriteLine("CRT0 tail 11C178-11C300:");
    for (uint a = 0x11C178; a < 0x11C300; a += 4)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    Environment.Exit(0);
}

// detps2 probe-gif — try Path3 on candidate display lists in MK ELF
if (args.Length > 0 && args[0].Equals("probe-gif", StringComparison.OrdinalIgnoreCase))
{
    string bios = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PCSX2", "bios", "Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin");
    string iso = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    var p = new Ps2System();
    p.LoadBios(bios);
    p.BootDiscFile(iso);
    p.RunFor(3_000_000);
    Console.WriteLine($"after boot sifDma={p.Hle.Sony?.SifDmaCalls} 77A088={p.Memory.Read32(0x77A088):X}");
    // Handlers registered by init
    foreach (uint addr in new uint[] { 0x483198, 0x483410, 0x483630, 0x4832A8, 0x4A6184, 0x4A6424 })
    {
        long before = p.Gs.PixelsWritten;
        p.Gif.ReceivePath3Data(addr, 128);
        Console.WriteLine($"Path3 0x{addr:X8} +px={p.Gs.PixelsWritten - before}");
    }
    // Scan for GIF tags that produce pixels
    int hits = 0;
    for (uint a = 0x00100000; a < 0x00600000 && hits < 20; a += 16)
    {
        uint lo = p.Memory.Read32(a);
        uint hi = p.Memory.Read32(a + 4);
        uint nloop = lo & 0x7FFF;
        uint flg = (hi >> 14) & 3; // rough
        // Heuristic: nloop 1..64, looks like tag
        if (nloop is < 1 or > 64) continue;
        if ((hi & 0xFFFF0000) == 0 && nloop > 0)
        {
            long b = p.Gs.PixelsWritten;
            p.Gif.ReceivePath3Data(a, Math.Min(nloop + 8, 64u));
            long d = p.Gs.PixelsWritten - b;
            if (d > 100)
            {
                Console.WriteLine($"HIT 0x{a:X8} lo={lo:X8} hi={hi:X8} +px={d}");
                hits++;
            }
        }
    }
    Console.WriteLine($"total px={p.Gs.PixelsWritten} prims={p.Gs.PrimitivesDrawn} path3={p.Gif.Path3Transfers}");
    // Dump SIFCMD handlers
    Console.WriteLine("handler 0x483198:");
    for (uint a = 0x483198; a < 0x483220; a += 4)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    Environment.Exit(p.Gs.PixelsWritten > 0 ? 0 : 1);
}

// detps2 probe-desktop — simulate Desktop 1.5M ticks and verify host overlay is non-black
if (args.Length > 0 && args[0].Equals("probe-desktop", StringComparison.OrdinalIgnoreCase))
{
    string bios = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PCSX2", "bios", "Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin");
    string iso = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    var p = new Ps2System();
    p.LoadBios(bios);
    p.BootDiscFile(iso);
    Console.WriteLine($"boot assist={p.MidwayAssist.Status} ready={p.MidwayAssist.FramesReady} frames={p.MidwayAssist.LogoFramesTotal}");
    for (int i = 0; i < 80 && !p.MidwayAssist.FramesReady; i++)
        Thread.Sleep(50);
    Console.WriteLine($"warm assist={p.MidwayAssist.Status} ready={p.MidwayAssist.FramesReady} frames={p.MidwayAssist.LogoFramesTotal}");
    // Phase 1 gate: ≥20 distinct FMV frame advances across host presents (G1/G2)
    const int MinAdvances = 20;
    bool ok = false;
    int prevFmv = -1;
    int advances = 0;
    for (int t = 0; t < 200; t++)
    {
        // Mimic Desktop MainWindow tick: RunFor → OnHostPresent once → PresentFrame
        p.RunFor(1_500_000);
        p.MidwayAssist.OnHostPresent(p);
        p.PresentFrame();
        int fmv = p.MidwayAssist.LogoFrame;
        if (fmv != prevFmv && prevFmv >= 0) advances++;
        prevFmv = fmv;
        if (t < 10 || t % 20 == 0 || p.MidwayAssist.Status.StartsWith("logo-hold", StringComparison.Ordinal)
            || p.MidwayAssist.Status is "logo-done" or "post-logo-main")
            Console.WriteLine($"tick {t}: c={p.MasterCycles} overlay={p.Gs.HostOverlayActive} " +
                              $"assist={p.MidwayAssist.Status} fmv={fmv}/{p.MidwayAssist.LogoFramesTotal} advances={advances}");
        if (advances >= MinAdvances && p.Gs.HostOverlayActive)
        {
            ok = true;
            string outP = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DetPS2", "desktop-sim-logo.ppm");
            var span = p.Gs.GetPresentSpan();
            p.Gs.BlitArgb8888(span, Gs.FB_WIDTH, Gs.FB_HEIGHT);
            p.Gs.SaveFramebufferAsPPM(outP);
            Console.WriteLine($"PHASE1 GATE PASS advances={advances} (need {MinAdvances}) wrote {outP}");
            break;
        }
        if (p.MidwayAssist.Status is "logo-done" or "post-logo-main")
        {
            ok = advances >= MinAdvances || p.MidwayAssist.FramesPresented >= MinAdvances;
            Console.WriteLine($"end assist={p.MidwayAssist.Status} advances={advances} presented={p.MidwayAssist.FramesPresented} ok={ok}");
            break;
        }
    }
    if (!ok)
        Console.WriteLine($"PHASE1 GATE FAIL advances={advances} need {MinAdvances} assist={p.MidwayAssist.Status}");
    Environment.Exit(ok ? 0 : 1);
}

// detps2 probe-sif — dump SIF/worklist funcs + post-boot state
if (args.Length > 0 && args[0].Equals("probe-sif", StringComparison.OrdinalIgnoreCase))
{
    string bios = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PCSX2", "bios", "Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin");
    string iso = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    var p = new Ps2System();
    p.LoadBios(bios);
    p.BootDiscFile(iso);
    void Dump(uint lo, uint hi, string name)
    {
        Console.WriteLine($"--- {name} 0x{lo:X8}-0x{hi:X8} ---");
        for (uint a = lo; a < hi; a += 4)
            Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    }
    Dump(0x482E98, 0x483100, "sif-init");
    Dump(0x4834E0, 0x483620, "work-add");
    Dump(0x483060, 0x483120, "work-proc");
    Dump(0x206268, 0x206340, "wait-work");
    Dump(0x482A20, 0x482B00, "reg-handler");
    // milestone with large slices
    bool[] hit = new bool[6];
    for (int i = 0; i < 400; i++)
    {
        p.RunFor(100_000);
        uint pc = (uint)(p.EE.PC & 0x1FFFFFFF);
        if (pc is >= 0x212F70 and < 0x213200) hit[0] = true;
        if (pc is >= 0x2131C0 and < 0x213200) hit[1] = true;
        if (pc is >= 0x482E98 and < 0x483100) hit[2] = true;
        if (pc is >= 0x2062C0 and < 0x2062E0) hit[3] = true;
        if (pc is >= 0x4834E0 and < 0x483600) hit[4] = true;
        if (p.Gif.Path3Transfers > 0) hit[5] = true;
        if (i % 20 == 19)
            Console.WriteLine($"c={p.MasterCycles} PC=0x{pc:X8} gif={p.Gif.Path3Transfers} px={p.Gs.PixelsWritten} " +
                              $"sifDma={p.Hle.Sony?.SifDmaCalls} 563FE4={p.Memory.Read32(0x563FE4):X} 77A080={p.Memory.Read32(0x77A080):X}");
    }
    Console.WriteLine($"hits mainBody={hit[0]} sifCallSite={hit[1]} sifInit={hit[2]} waitWork={hit[3]} workAdd={hit[4]} gif={hit[5]}");
    Console.WriteLine($"final PC=0x{p.EE.PC:X8} sifDma={p.Hle.Sony?.SifDmaCalls} cd={p.Cdvd.SectorsRead}");
    for (uint a = 0x77A080; a < 0x77A0C0; a += 4)
        Console.WriteLine($"  mem[{a:X8}]={p.Memory.Read32(a):X8}");
    Environment.Exit(0);
}

// detps2 probe-cmp — compare large vs small RunFor chunk behavior
if (args.Length > 0 && args[0].Equals("probe-cmp", StringComparison.OrdinalIgnoreCase))
{
    string bios = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PCSX2", "bios", "Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin");
    string iso = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    void RunStyle(string name, Action<Ps2System> runner)
    {
        var p = new Ps2System();
        p.LoadBios(bios);
        p.BootDiscFile(iso);
        runner(p);
        Console.WriteLine($"{name}: c={p.MasterCycles} PC=0x{p.EE.PC:X8} gif={p.Gif.Path3Transfers} px={p.Gs.PixelsWritten} " +
                          $"dmac={p.Dmac.TransfersCompleted} sifDma={p.Hle.Sony?.SifDmaCalls} cd={p.Cdvd.SectorsRead} " +
                          $"77A080={p.Memory.Read32(0x77A080):X8}");
    }
    RunStyle("one-8M", p => p.RunFor(8_000_000));
    RunStyle("16x500k", p => { for (int i = 0; i < 16; i++) p.RunFor(500_000); });
    RunStyle("160x50k", p => { for (int i = 0; i < 160; i++) p.RunFor(50_000); });
    RunStyle("800x10k", p => { for (int i = 0; i < 800; i++) p.RunFor(10_000); });
    // Dump key funcs once
    {
        var p = new Ps2System();
        p.LoadBios(bios);
        p.BootDiscFile(iso);
        Console.WriteLine("fn 0x205F00 (ctors):");
        for (uint a = 0x205F00; a < 0x206300; a += 4)
            Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
        Console.WriteLine("fn 0x482E98 (sif init):");
        for (uint a = 0x482E98; a < 0x482F80; a += 4)
            Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
        Console.WriteLine("fn 0x486008:");
        for (uint a = 0x486000; a < 0x486080; a += 4)
            Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
        // strings at main argv compare bases
        foreach (uint a in new uint[] { 0x584B58, 0x584B68, 0x584B78, 0x4E0000 })
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 40; i++)
            {
                byte b = p.Memory.Read8(a + (uint)i);
                if (b == 0) break;
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }
            Console.WriteLine($"str@0x{a:X8}='{sb}'");
        }
    }
    Environment.Exit(0);
}

// detps2 probe-main — dense PC trace after KickMidwayMainPath
if (args.Length > 0 && args[0].Equals("probe-main", StringComparison.OrdinalIgnoreCase))
{
    string bios = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PCSX2", "bios", "Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin");
    string iso = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    var p = new Ps2System();
    p.LoadBios(bios);
    p.BootDiscFile(iso);
    Console.WriteLine("main body 0x212F70-0x213200:");
    for (uint a = 0x212F70; a < 0x213200; a += 4)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    // Milestone PCs in main / SIF / logo path
    var milestones = new (uint lo, uint hi, string name)[]
    {
        (0x00212F70, 0x00213030, "main-entry"),
        (0x00213030, 0x002131C0, "main-pre-sif"),
        (0x002131C0, 0x00213200, "main-sif-call"),
        (0x00213200, 0x00213400, "main-post-sif"),
        (0x00482E98, 0x00484000, "sif-init"),
        (0x00205F00, 0x00206000, "ctors"),
        (0x0020ECD0, 0x0020F200, "fn-20ECD0"),
        (0x00474C00, 0x00476000, "fn-474C78"),
        (0x0023C500, 0x0023C800, "fn-23C540"),
        (0x0024D100, 0x0024D400, "fn-24D128"),
    };
    var hitMs = new bool[milestones.Length];
    var firstMs = new ulong[milestones.Length];
    bool sawMain = false, sawSif = false;
    uint lastBucket = 0;
    int transitions = 0;
    for (int i = 0; i < 500_000; i++)
    {
        p.RunFor(500);
        uint pc = (uint)(p.EE.PC & 0x1FFFFFFF);
        if (pc is >= 0x00212F70 and < 0x00215000) sawMain = true;
        if (pc is >= 0x00482E98 and < 0x00484000) sawSif = true;
        for (int m = 0; m < milestones.Length; m++)
        {
            if (!hitMs[m] && pc >= milestones[m].lo && pc < milestones[m].hi)
            {
                hitMs[m] = true;
                firstMs[m] = p.MasterCycles;
                Console.WriteLine($"  HIT {milestones[m].name} @ c={p.MasterCycles} PC=0x{pc:X8} ra=0x{p.EE.GetGpr(31).Lo:X8}");
            }
        }
        uint bucket = pc & ~0xFFFu;
        if (bucket != lastBucket && transitions < 40 && p.MasterCycles > 100_000)
        {
            if (transitions < 25 || (pc is >= 0x00212F00 and < 0x00215000) || (pc is >= 0x00482E00 and < 0x00485000))
            {
                Console.WriteLine($"  t={p.MasterCycles,10} PC=0x{pc:X8} ra=0x{p.EE.GetGpr(31).Lo:X8} v0=0x{p.EE.GetGpr(2).Lo:X}");
                transitions++;
            }
            lastBucket = bucket;
        }
        if (p.MasterCycles > 40_000_000) break;
        // Log every 5M
        if (p.MasterCycles % 5_000_000 < 500 && p.MasterCycles > 1_000_000)
            Console.WriteLine($"  .. c={p.MasterCycles} PC=0x{pc:X8} gif={p.Gif.Path3Transfers} px={p.Gs.PixelsWritten} sifDma={p.Hle.Sony?.SifDmaCalls} cd={p.Cdvd.SectorsRead}");
    }
    Console.WriteLine($"sawMain={sawMain} sawSif={sawSif} finalPC=0x{p.EE.PC:X8}");
    Console.WriteLine($"sifDma={p.Hle.Sony?.SifDmaCalls} sifGet={p.Hle.Sony?.SifGetRegCalls} gif={p.Gif.Path3Transfers} px={p.Gs.PixelsWritten} cd={p.Cdvd.SectorsRead}");
    Console.WriteLine($"77A080={p.Memory.Read32(0x77A080):X8} 563FE4={p.Memory.Read32(0x563FE4):X8}");
    for (int m = 0; m < milestones.Length; m++)
        Console.WriteLine($"  ms {milestones[m].name}: {(hitMs[m] ? $"YES @{firstMs[m]}" : "no")}");
    // Dump hang region if stuck
    uint fpc = (uint)(p.EE.PC & 0x1FFFFFFF);
    Console.WriteLine($"code near final PC 0x{fpc:X8}:");
    for (uint a = fpc & ~0xFu; a < (fpc & ~0xFu) + 0x40; a += 4)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    if (p.Hle.Sony != null)
        foreach (var kv in p.Hle.Sony.SyscallHistogram.OrderByDescending(k => k.Value).Take(15))
            Console.WriteLine($"  sc 0x{kv.Key:X2} x{kv.Value}");
    Environment.Exit(0);
}

// detps2 probe-hang — diagnose post-clear spin (0x1668xx) and boot progress
if (args.Length > 0 && args[0].Equals("probe-hang", StringComparison.OrdinalIgnoreCase))
{
    string bios = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PCSX2", "bios", "Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin");
    string iso = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    var p = new Ps2System();
    p.LoadBios(bios);
    p.BootDiscFile(iso);
    // Advance until we land in the known spin or hit 30M cycles
    for (int i = 0; i < 60; i++)
    {
        p.RunFor(500_000);
        uint pc = (uint)(p.EE.PC & 0x1FFFFFFF);
        if (i % 4 == 0)
            Console.WriteLine($"  c={p.MasterCycles} PC=0x{pc:X8} px={p.Gs.PixelsWritten} gif={p.Gif.Path3Transfers} " +
                              $"sifDma={p.Hle.Sony?.SifDmaCalls} sifGet={p.Hle.Sony?.SifGetRegCalls} cd={p.Cdvd.SectorsRead} tid={p.Hle.Kernel.CurrentThreadId}");
        if (pc is >= 0x00166800 and < 0x00166B00 && p.MasterCycles > 10_000_000)
            break;
    }
    uint pc0 = (uint)(p.EE.PC & 0x1FFFFFFF);
    Console.WriteLine($"--- hang snapshot PC=0x{pc0:X8} cycles={p.MasterCycles} ---");
    Console.WriteLine($"px={p.Gs.PixelsWritten} gifP3={p.Gif.Path3Transfers} dmac={p.Dmac.TransfersCompleted}");
    Console.WriteLine($"sifDma={p.Hle.Sony?.SifDmaCalls} sifGet={p.Hle.Sony?.SifGetRegCalls} sifB={p.Sif.BytesTransferred} cdvd={p.Cdvd.SectorsRead}");
    Console.WriteLine($"threads={p.Hle.Kernel.ThreadCount} tid={p.Hle.Kernel.CurrentThreadId} waitVb={p.Hle.Kernel.WaitingVblank}");
    for (int t = 1; t <= Math.Min(20, p.Hle.Kernel.ThreadCount + 6); t++)
    {
        var th = p.Hle.Kernel.GetThread(t);
        if (th == null) continue;
        Console.WriteLine($"  t{t}: alive={th.Alive} sleep={th.Sleeping} waitSema={th.WaitSemaId} " +
                          $"entry=0x{th.Entry:X8} pc=0x{th.SavedPc:X8} sp=0x{th.SavedSp:X8} started={th.Started}");
    }
    Console.WriteLine("code @ 0x1668C0-0x166980:");
    for (uint a = 0x001668C0; a < 0x00166980; a += 4)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    // Who calls the spin? Look at RA and nearby JAL targets
    Console.WriteLine($"v0={p.EE.GetGpr(2).Lo:X} v1={p.EE.GetGpr(3).Lo:X} a0={p.EE.GetGpr(4).Lo:X} a1={p.EE.GetGpr(5).Lo:X}");
    Console.WriteLine($"s0={p.EE.GetGpr(16).Lo:X} s1={p.EE.GetGpr(17).Lo:X} s2={p.EE.GetGpr(18).Lo:X} ra={p.EE.GetGpr(31).Lo:X} sp={p.EE.GetGpr(29).Lo:X}");
    foreach (uint a in new uint[] { 0x77A080, 0x77A084, 0x77A088, 0x563FE4, 0x56409C, 0x5C9C00, 0x4860C0, 0x480330 })
        Console.WriteLine($"mem[0x{a:X8}]=0x{p.Memory.Read32(a):X8}");
    if (p.Hle.Sony != null)
    {
        Console.WriteLine("--- syscall hist ---");
        foreach (var kv in p.Hle.Sony.SyscallHistogram.OrderByDescending(k => k.Value).Take(25))
            Console.WriteLine($"  sc 0x{kv.Key:X2} x{kv.Value}");
        Console.WriteLine("--- SetSyscall hooks ---");
        foreach (var kv in p.Hle.Sony.CustomSyscalls.OrderBy(k => k.Key))
            Console.WriteLine($"  0x{kv.Key:X2} -> 0x{kv.Value:X8}");
    }
    // Dense PC hist
    var hist = new Dictionary<uint, int>();
    for (int i = 0; i < 8000; i++)
    {
        p.RunFor(25);
        uint b = (uint)(p.EE.PC & 0x1FFFFF00);
        hist[b] = hist.GetValueOrDefault(b) + 1;
    }
    Console.WriteLine("--- PC buckets ---");
    foreach (var kv in hist.OrderByDescending(k => k.Value).Take(20))
        Console.WriteLine($"  0x{kv.Key:X8} x{kv.Value}");
    // Dump functions around hot buckets
    foreach (var kv in hist.OrderByDescending(k => k.Value).Take(5))
    {
        uint baseA = kv.Key;
        Console.WriteLine($"code @ 0x{baseA:X8}:");
        for (uint a = baseA; a < baseA + 0x40; a += 4)
            Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    }
    // Scan for MIDWAY path in RDRAM
    Console.WriteLine("--- string scan MIDWAY/SFD/cdrom ---");
    string[] needles = { "MIDWAY", "SFD", "cdrom", "CDROM", "IOP/", "CRI", "PADMAN", "logo", "LOGO" };
    for (uint a = 0x100000; a < 0x800000; a += 4)
    {
        // quick ASCII start check
        byte c0 = p.Memory.Read8(a);
        if (c0 is < 0x20 or > 0x7E) continue;
        foreach (var n in needles)
        {
            bool ok = true;
            for (int i = 0; i < n.Length; i++)
            {
                if (p.Memory.Read8(a + (uint)i) != (byte)n[i]) { ok = false; break; }
            }
            if (!ok) continue;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 80; i++)
            {
                byte b = p.Memory.Read8(a + (uint)i);
                if (b == 0) break;
                if (b is < 0x20 or > 0x7E) { sb.Append('.'); continue; }
                sb.Append((char)b);
            }
            Console.WriteLine($"  0x{a:X8}: {sb}");
            break;
        }
    }
    Environment.Exit(0);
}

// detps2 probe-mk5 — find SIF init function entry + force-call experiment
if (args.Length > 0 && args[0].Equals("probe-mk5", StringComparison.OrdinalIgnoreCase))
{
    string bios = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PCSX2", "bios", "Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin");
    string iso = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    var p = new Ps2System();
    p.LoadBios(bios);
    p.BootDiscFile(iso);
    // Find function prologues before 482EE0
    Console.WriteLine("backwalk from 482EE0:");
    for (uint a = 0x482E00; a < 0x482EF0; a += 4)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    uint[] targets = { 0x482EE0, 0x482E80, 0x482E00, 0x482D80, 0x482D00, 0x482C80, 0x482768 };
    foreach (uint t in targets)
    {
        uint jal = 0x0C000000u | ((t >> 2) & 0x03FFFFFF);
        int n = 0;
        for (uint a = 0x100000; a < 0x600000; a += 4)
        {
            if (p.Memory.Read32(a) == jal)
            {
                Console.WriteLine($"JAL 0x{t:X8} from 0x{a:X8}");
                if (++n > 8) break;
            }
        }
    }
    // After short boot, force-call 0x482EE0-ish with stack and see 77A080
    p.RunFor(3_000_000);
    Console.WriteLine($"pre-force PC=0x{p.EE.PC:X8} 77A080={p.Memory.Read32(0x77A080):X8}/{p.Memory.Read32(0x77A084):X8}/{p.Memory.Read32(0x77A088):X8}");
    // Find addiu sp,sp,-N before 482EE0
    uint entry = 0x482E80;
    for (uint a = 0x482EE0; a > 0x482C00; a -= 4)
    {
        uint w = p.Memory.Read32(a);
        if ((w & 0xFFFF0000) == 0x27BD0000) // addiu sp,sp,imm
        {
            entry = a;
            Console.WriteLine($"prologue candidate 0x{a:X8}: {w:X8}");
            break;
        }
    }
    // Force call
    ulong savedPc = p.EE.PC, savedRa = p.EE.GetGpr(31).Lo, savedSp = p.EE.GetGpr(29).Lo;
    p.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = 0x00081000 }); // stub jr ra
    p.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x01FE0000 });
    p.EE.PC = entry;
    // plant return stub already at 81000
    for (int i = 0; i < 200000 && p.EE.PC != 0x81000 && (p.EE.PC & 0x1FFFFFFF) > 0x1000; i++)
        p.EE.Step(1);
    Console.WriteLine($"post-force PC=0x{p.EE.PC:X8} steps done, 77A080={p.Memory.Read32(0x77A080):X8}/{p.Memory.Read32(0x77A084):X8}/{p.Memory.Read32(0x77A088):X8}");
    Console.WriteLine($"sys={p.Hle.SyscallCount} sifDma={p.Hle.Sony?.SifDmaCalls} sifGet={p.Hle.Sony?.SifGetRegCalls}");
    // restore and continue
    p.EE.PC = savedPc;
    p.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = savedRa });
    p.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = savedSp });
    p.RunFor(30_000_000);
    Console.WriteLine($"after 30M PC=0x{p.EE.PC:X8} px={p.Gs.PixelsWritten} gif={p.Gif.Path3Transfers} dmac={p.Dmac.TransfersCompleted} sifB={p.Sif.BytesTransferred}");
    Console.WriteLine($"77A080={p.Memory.Read32(0x77A080):X8}/{p.Memory.Read32(0x77A084):X8}/{p.Memory.Read32(0x77A088):X8}");
    if (p.Hle.Sony != null)
        foreach (var kv in p.Hle.Sony.SyscallHistogram.OrderByDescending(k => k.Value).Take(20))
            Console.WriteLine($"  sc 0x{kv.Key:X2} x{kv.Value}");
    Environment.Exit(p.Gif.Path3Transfers > 0 ? 0 : 1);
}

// detps2 probe-mk4 — find callers of init + dump pre-outer
if (args.Length > 0 && args[0].Equals("probe-mk4", StringComparison.OrdinalIgnoreCase))
{
    string bios = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PCSX2", "bios", "Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin");
    string iso = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    var p = new Ps2System();
    p.LoadBios(bios);
    p.BootDiscFile(iso);
    Console.WriteLine("ptrs to 0x482F00 / lui t0,0x78 near JALs:");
    for (uint a = 0x100000; a < 0x600000; a += 4)
    {
        uint w = p.Memory.Read32(a);
        if (w == 0x00482F00 || w == 0x80482F00)
            Console.WriteLine($"  ptr @{a:X8}");
        if (w == 0x3C080078) // lui t0, 0x78
            Console.WriteLine($"  lui t0,0x78 @{a:X8} next={p.Memory.Read32(a+4):X8} {p.Memory.Read32(a+8):X8}");
    }
    Console.WriteLine("around 0x2129A0 (caller of outer):");
    for (uint a = 0x212900; a < 0x212A80; a += 4)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    // dump 0x482EE0 in case prologue before F00
    Console.WriteLine("0x482EE0:");
    for (uint a = 0x482EE0; a < 0x482F80; a += 4)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    Environment.Exit(0);
}

// detps2 probe-mk3 — find SIF syscall wrappers + who calls 482F00 init
if (args.Length > 0 && args[0].Equals("probe-mk3", StringComparison.OrdinalIgnoreCase))
{
    string bios = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PCSX2", "bios", "Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin");
    string iso = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    var p = new Ps2System();
    p.LoadBios(bios);
    p.BootDiscFile(iso);
    // Scan for li v1, imm; syscall patterns (2403XXXX 0000000C)
    Console.WriteLine("syscall wrappers (li v1; syscall):");
    var seen = new HashSet<uint>();
    for (uint a = 0x100000; a < 0x500000; a += 4)
    {
        uint w = p.Memory.Read32(a);
        uint n = p.Memory.Read32(a + 4);
        if (n == 0x0000000C && (w & 0xFFFF0000) == 0x24030000)
        {
            uint num = w & 0xFFFF;
            if (num > 0x7F) num = (uint)(short)(num); // sign extend for negative
            if (seen.Add(num))
                Console.WriteLine($"  @{a:X8} v1=0x{num:X} ({(int)num})");
        }
    }
    // JAL to 0x482F00
    uint jalInit = 0x0C120BC0;
    Console.WriteLine("calls to 0x482F00:");
    for (uint a = 0x100000; a < 0x600000; a += 4)
        if (p.Memory.Read32(a) == jalInit)
            Console.WriteLine($"  JAL @{a:X8}");
    // JAL to 0x206268 (outer?) 
    uint jalOuter = 0x0C08189A; // 0x206268>>2 = 0x8189A
    // actually 0x206268 >> 2 = 0x8189A, encoding 0x0C08189A
    Console.WriteLine("calls to 0x206268:");
    for (uint a = 0x100000; a < 0x600000; a += 4)
        if (p.Memory.Read32(a) == 0x0C08189A)
            Console.WriteLine($"  JAL @{a:X8}");
    // dump 482A20 and 482C20 and 480270
    foreach (uint baseA in new uint[] { 0x482A20, 0x482C20, 0x480270, 0x482740, 0x4801D0 })
    {
        Console.WriteLine($"fn 0x{baseA:X8}:");
        for (uint a = baseA; a < baseA + 0x40; a += 4)
            Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    }
    // Run and see if PC ever hits 482F00
    bool hit = false;
    for (int i = 0; i < 5000; i++)
    {
        p.RunFor(2000);
        if ((p.EE.PC & 0x1FFFFFFFUL) is >= 0x482F00 and < 0x483000)
        {
            Console.WriteLine($"HIT init PC=0x{p.EE.PC:X8} at c={p.MasterCycles}");
            hit = true;
            break;
        }
    }
    Console.WriteLine($"hit482F00={hit} finalPC=0x{p.EE.PC:X8} c={p.MasterCycles} sys={p.Hle.SyscallCount}");
    if (p.Hle.Sony != null)
        foreach (var kv in p.Hle.Sony.SyscallHistogram.OrderByDescending(k => k.Value).Take(25))
            Console.WriteLine($"  sc 0x{kv.Key:X2} x{kv.Value}");
    Environment.Exit(0);
}

// detps2 probe-mk2 — structs + 77A080 writers + CRT0
if (args.Length > 0 && args[0].Equals("probe-mk2", StringComparison.OrdinalIgnoreCase))
{
    string bios = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PCSX2", "bios", "Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin");
    string iso = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    var p = new Ps2System();
    p.LoadBios(bios);
    p.BootDiscFile(iso);
    p.RunFor(5_000_000);
    Console.WriteLine($"PC=0x{p.EE.PC:X8} thrEntry=0x{p.Hle.Sony?.LastCreatedThreadEntry:X8}");
    Console.WriteLine("struct 0x64E600:");
    for (int i = 0; i < 20; i++)
        Console.WriteLine($"  +{i * 4:X2} = {p.Memory.Read32(0x64E600 + (uint)(i * 4)):X8}");
    foreach (uint a in new uint[] { 0x565B9C, 0x583C28, 0x583D78, 0x5B2000, 0x77F5F8, 0x77F900, 0x56409C })
    {
        Console.Write($"@{a:X8}: ");
        for (int i = 0; i < 8; i++) Console.Write($"{p.Memory.Read32(a + (uint)(i * 4)):X8} ");
        Console.WriteLine();
    }
    int hits = 0;
    for (uint a = 0x100000; a < 0x600000; a += 4)
    {
        uint w = p.Memory.Read32(a);
        if ((w & 0xFFE0FFFF) == 0x3C000078) // lui ?,0x78
        {
            uint n = p.Memory.Read32(a + 4);
            if ((n & 0xFFFF) == 0xA080)
            {
                Console.WriteLine($"77A080 @ {a:X8}: {w:X8} {n:X8} next={p.Memory.Read32(a + 8):X8}");
                if (++hits > 20) break;
            }
        }
    }
    Console.WriteLine("CRT0:");
    for (uint a = 0x11C070; a < 0x11C180; a += 4)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    Console.WriteLine("0x480300:");
    for (uint a = 0x480300; a < 0x4803C0; a += 4)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    // dump 483060 enqueue-ish neighbors and 47FEA0 (worker jal)
    Console.WriteLine("0x47FEA0:");
    for (uint a = 0x47FEA0; a < 0x47FF40; a += 4)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    Console.WriteLine("outer 0x206200:");
    for (uint a = 0x206200; a < 0x206350; a += 4)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    Console.WriteLine("0x482F00 (77A080 site):");
    for (uint a = 0x482F00; a < 0x483000; a += 4)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    // dump state machine first handler
    Console.WriteLine("handler 0x4A6184:");
    for (uint a = 0x4A6184; a < 0x4A6200; a += 4)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    // strings at 583C40
    Console.Write("str@583C40: ");
    for (uint a = 0x583C40; a < 0x583C80; a++)
    {
        byte b = (byte)p.Memory.Read8(a);
        Console.Write(b >= 32 && b < 127 ? (char)b : '.');
    }
    Console.WriteLine();
    Environment.Exit(0);
}

// detps2 probe-mk — dense early boot + 0x77 bank dump + GIF chase
if (args.Length > 0 && args[0].Equals("probe-mk", StringComparison.OrdinalIgnoreCase))
{
    string bios = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PCSX2", "bios", "Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin");
    string iso = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    var p = new Ps2System();
    p.LoadBios(bios);
    p.BootDiscFile(iso);
    var pcs = new Dictionary<uint, int>();
    var scHist = new Dictionary<uint, int>();
    // sample every 1k for first 2M
    for (int i = 0; i < 2000; i++)
    {
        p.RunFor(1000);
        uint b = (uint)(p.EE.PC & 0x1FFFFF00UL);
        pcs[b] = pcs.GetValueOrDefault(b) + 1;
    }
    Console.WriteLine($"=== after 2M PC=0x{p.EE.PC:X8} sys={p.Hle.SyscallCount} tid={p.Hle.Kernel.CurrentThreadId} ===");
    foreach (var kv in pcs.OrderByDescending(k => k.Value).Take(20))
        Console.WriteLine($"  bucket 0x{kv.Key:X8} x{kv.Value}");
    if (p.Hle.Sony != null)
        foreach (var kv in p.Hle.Sony.SyscallHistogram.OrderByDescending(k => k.Value))
            Console.WriteLine($"  sc 0x{kv.Key:X2} x{kv.Value}");
    // non-zero words in 0x770000-0x780000
    Console.WriteLine("--- non-zero 0x77xxxx ---");
    int nz = 0;
    for (uint a = 0x00770000; a < 0x00780000; a += 4)
    {
        uint w = p.Memory.Read32(a);
        if (w != 0)
        {
            Console.WriteLine($"  {a:X8}: {w:X8}");
            if (++nz > 80) { Console.WriteLine("  ..."); break; }
        }
    }
    // continue to 30M with MMIO write watch via telemetry
    p.RunFor(28_000_000);
    Console.WriteLine($"=== after 30M PC=0x{p.EE.PC:X8} px={p.Gs.PixelsWritten} gif={p.Gif.Path3Transfers} dmac={p.Dmac.TransfersCompleted} sifB={p.Sif.BytesTransferred} sys={p.Hle.SyscallCount} ===");
    Console.WriteLine($"D_CTRL=0x{p.Memory.Read32(0x1000E000):X} GIF_CHCR=0x{p.Memory.Read32(0x1000A000):X} VIF1=0x{p.Memory.Read32(0x10009000):X}");
    Console.WriteLine($"0x77A080: {p.Memory.Read32(0x77A080):X8} {p.Memory.Read32(0x77A084):X8} {p.Memory.Read32(0x77A088):X8}");
    Console.WriteLine($"0x777D30: {p.Memory.Read32(0x777D30):X8}");
    Console.WriteLine(p.Telemetry.FormatReport(20));
    // dump code around last PC
    uint pc = (uint)(p.EE.PC & 0x1FFFFFFFUL);
    Console.WriteLine($"code near PC 0x{pc:X8}:");
    for (int i = -8; i < 16; i++)
    {
        uint a = (uint)(pc + i * 4);
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    }
    Environment.Exit(p.Gs.PixelsWritten > 0 && p.Gif.Path3Transfers > 0 ? 0 : 1);
}

// detps2 probe-worker [bios] [iso] — dump worker entry + mid-loop tails
if (args.Length > 0 && args[0].Equals("probe-worker", StringComparison.OrdinalIgnoreCase))
{
    string bios = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PCSX2", "bios", "Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin");
    string iso = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    var p = new Ps2System();
    p.LoadBios(bios);
    p.BootDiscFile(iso);
    // Run until CreateThread has fired (early)
    p.RunFor(2_000_000);
    Console.WriteLine($"after 2M: PC=0x{p.EE.PC:X8} tid={p.Hle.Kernel.CurrentThreadId} threads={p.Hle.Kernel.ThreadCount}");
    Console.WriteLine($"lastThr entry=0x{p.Hle.Sony?.LastCreatedThreadEntry:X8} sp=0x{p.Hle.Sony?.LastCreatedThreadStack:X8}");
    Console.WriteLine($"global 0x777D30={p.Memory.Read32(0x777D30):X8} 0x777930={p.Memory.Read32(0x777930):X8}");
    for (uint a = 0x777D00; a < 0x777E00; a += 16)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8} {p.Memory.Read32(a+4):X8} {p.Memory.Read32(a+8):X8} {p.Memory.Read32(a+12):X8}");
    uint we = p.Hle.Sony?.LastCreatedThreadEntry ?? 0;
    if (we != 0)
    {
        Console.WriteLine($"worker @{we:X8}:");
        for (int i = 0; i < 64; i++)
            Console.WriteLine($"  {we + (uint)(i * 4):X8}: {p.Memory.Read32(we + (uint)(i * 4)):X8}");
    }
    // dump empty-list path of main loop
    Console.WriteLine("main 483510-483680:");
    for (uint a = 0x483510; a < 0x483680; a += 4)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    // dump 480A18 area (expected worker)
    Console.WriteLine("0x480A18:");
    for (uint a = 0x480A18; a < 0x480B18; a += 4)
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    // Scan RDRAM for JAL/j to worker and pointer literals
    uint jal = 0x0C120286; // jal 0x480A18
    int jalHits = 0, ptrHits = 0, jHits = 0;
    for (uint a = 0x00100000; a < 0x01000000; a += 4)
    {
        uint w = p.Memory.Read32(a);
        if (w == jal || w == (0x08000000u | (0x480A18u >> 2))) // j 0x480A18
        {
            Console.WriteLine($"J/JAL worker @ 0x{a:X8} w=0x{w:X8}");
            jalHits++;
            if (jalHits > 20) break;
        }
        if (w == 0x00480A18 || w == 0x80480A18)
        {
            Console.WriteLine($"ptr worker @ 0x{a:X8}");
            ptrHits++;
            if (ptrHits > 20) break;
        }
    }
    Console.WriteLine($"jalHits={jalHits} ptrHits={ptrHits}");

    p.RunFor(30_000_000);
    Console.WriteLine($"after 32M: PC=0x{p.EE.PC:X8} px={p.Gs.PixelsWritten} dmac={p.Dmac.TransfersCompleted} sifB={p.Sif.BytesTransferred} sys={p.Hle.SyscallCount}");
    Console.WriteLine($"tid={p.Hle.Kernel.CurrentThreadId} gifP3={p.Gif.Path3Transfers}");
    Console.WriteLine($"0x777D30={p.Memory.Read32(0x777D30):X8} 0x77A080+0={p.Memory.Read32(0x77A080):X8} +4={p.Memory.Read32(0x77A084):X8} +8={p.Memory.Read32(0x77A088):X8}");
    if (p.Hle.Sony != null)
        foreach (var kv in p.Hle.Sony.SyscallHistogram.OrderByDescending(k => k.Value).Take(15))
            Console.WriteLine($"  sc 0x{kv.Key:X2} x{kv.Value}");
    Environment.Exit(0);
}

// detps2 probe-struct [bios] [iso] — dump Midway main-loop structures
if (args.Length > 0 && args[0].Equals("probe-struct", StringComparison.OrdinalIgnoreCase))
{
    string bios = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PCSX2", "bios", "Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin");
    string iso = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    var p = new Ps2System();
    p.LoadBios(bios);
    p.BootDiscFile(iso);
    p.RunFor(20_000_000);
    uint s1 = 0x77A080;
    Console.WriteLine($"s1 @ 0x{s1:X8}:");
    for (int i = 0; i < 16; i++)
        Console.WriteLine($"  +{i * 4:X2} = 0x{p.Memory.Read32(s1 + (uint)(i * 4)):X8}");
    uint s0 = p.Memory.Read32(s1 + 4);
    Console.WriteLine($"list head s0=0x{s0:X8}");
    if (s0 != 0 && (s0 & 0x1FFFFFFFu) < SystemMemory.RDRAM_SIZE)
        for (int i = 0; i < 16; i++)
            Console.WriteLine($"  s0+{i * 4:X2} = 0x{p.Memory.Read32(s0 + (uint)(i * 4)):X8}");
    Console.WriteLine("func 0x483060:");
    for (int i = 0; i < 48; i++)
    {
        uint a = 0x483060u + (uint)(i * 4);
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    }
    Console.WriteLine("func 0x4834E0:");
    for (int i = 0; i < 48; i++)
    {
        uint a = 0x4834E0u + (uint)(i * 4);
        Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
    }
    Console.WriteLine($"PC=0x{p.EE.PC:X8} px={p.Gs.PixelsWritten} dmac={p.Dmac.TransfersCompleted} sif={p.Sif.BytesTransferred} sys={p.Hle.SyscallCount}");
    Environment.Exit(0);
}

// Micro: detps2 probe-di  — verify COP0 DI clears EIE
if (args.Length > 0 && args[0].Equals("probe-di", StringComparison.OrdinalIgnoreCase))
{
    var mem = new SystemMemory();
    var ee = new EmotionEngine(mem);
    ee.COP0_Status = (1u << 16) | 1u; // EIE | IE
    Console.WriteLine($"before DI Status=0x{ee.COP0_Status:X8}");
    mem.Write32(0x100000, 0x42000039u); // DI
    mem.Write32(0x100004, 0x00000000u); // nop
    mem.Write32(0x100008, 0x40026000u); // mfc0 v0, Status
    mem.Write32(0x10000C, 0x00000000u);
    ee.PC = 0x100000;
    ee.Step(4);
    Console.WriteLine($"after DI+mfc0 Status=0x{ee.COP0_Status:X8} v0=0x{ee.GetGpr(2).Lo:X8}");
    Environment.Exit((ee.COP0_Status & (1u << 16)) == 0 ? 0 : 1);
}

// Retail boot probe: detps2 probe-boot [bios] [iso] [cycles]
if (args.Length > 0 && args[0].Equals("probe-boot", StringComparison.OrdinalIgnoreCase))
{
    string bios = args.Length > 1
        ? args[1]
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PCSX2", "bios", "Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin");
    string iso = args.Length > 2
        ? args[2]
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    ulong total = args.Length > 3 && ulong.TryParse(args[3], out ulong c) ? c : 80_000_000UL;

    Console.WriteLine(VersionInfo.Banner);
    Console.WriteLine($"BIOS={bios}");
    Console.WriteLine($"ISO={iso}");
    if (!File.Exists(bios) || !File.Exists(iso))
    {
        Console.WriteLine("Missing BIOS or ISO");
        Environment.Exit(2);
    }

    var probe = new Ps2System();
    probe.LoadBios(bios);
    var boot = probe.BootDiscFile(iso);
    Console.WriteLine($"Boot: {boot.Message} success={boot.Success}");
    if (!boot.Success) Environment.Exit(1);

    ulong entry = boot.Elf?.Entry ?? 0;
    Console.WriteLine($"entry=0x{entry:X8} sony={probe.Hle.SonyKernelMode}");

    ulong step = 5_000_000;
    for (ulong done = 0; done < total; done += step)
    {
        probe.RunFor(step);
        var sony = probe.Hle.Sony;
        Console.WriteLine(
            $"c={probe.MasterCycles,12:N0} PC=0x{probe.EE.PC:X8} lastGood=0x{probe.LastGoodEePc:X8} " +
            $"px={probe.Gs.PixelsWritten} gifP3={probe.Gif.Path3Transfers} dmac={probe.Dmac.TransfersCompleted} " +
            $"sys={probe.Hle.SyscallCount} sifDma={sony?.SifDmaCalls ?? 0} sifGet={sony?.SifGetRegCalls ?? 0} " +
            $"sonyH={sony?.Handled ?? 0} sonyU={sony?.Unknown ?? 0} " +
            $"sifB={probe.Sif.BytesTransferred} intc={probe.Intc.Stat:X} sp=0x{probe.EE.GetGpr(29).Lo:X8}");
    }

    Console.WriteLine("--- top blockers ---");
    Console.WriteLine(probe.Telemetry.FormatReport(12));
    if (probe.Hle.Sony != null)
    {
        Console.WriteLine("--- syscall histogram ---");
        foreach (var kv in probe.Hle.Sony.SyscallHistogram.OrderByDescending(k => k.Value))
            Console.WriteLine($"  0x{kv.Key:X2} x{kv.Value}");
        Console.WriteLine("--- SetSyscall hooks ---");
        foreach (var kv in probe.Hle.Sony.CustomSyscalls.OrderBy(k => k.Key))
            Console.WriteLine($"  num=0x{kv.Key:X2} -> 0x{kv.Value:X8}");
        Console.Write($"cdvd sectors={probe.Cdvd.SectorsRead} complete={probe.Cdvd.Completions} ");
        Console.WriteLine($"pending={probe.Cdvd.ReadPending}");
    }

    // Dump hot-loop disasm windows and a few global words
    uint[] addrs =
    {
        0x00483060, 0x004834E0, 0x00485FC0, 0x00485FE0, 0x00486000,
        0x00206280, 0x00486190, 0x00485FB0, 0x0011C070
    };
    foreach (uint a in addrs)
    {
        Console.Write($"@{a:X8}:");
        for (int i = 0; i < 16; i++)
            Console.Write($" {probe.Memory.Read32(a + (uint)(i * 4)):X8}");
        Console.WriteLine();
    }
    Console.WriteLine(
        $"v0={probe.EE.GetGpr(2).Lo:X} v1={probe.EE.GetGpr(3).Lo:X} a0={probe.EE.GetGpr(4).Lo:X} " +
        $"s0={probe.EE.GetGpr(16).Lo:X} s1={probe.EE.GetGpr(17).Lo:X} s2={probe.EE.GetGpr(18).Lo:X} " +
        $"ra={probe.EE.GetGpr(31).Lo:X} threads={probe.Hle.Kernel.ThreadCount} tid={probe.Hle.Kernel.CurrentThreadId}");
    foreach (uint a in new uint[] { 0x56409C, 0x4860C0, 0x486194, 0x4861D8, 0x480330, 0x1000E000, 0x1000A000, 0x10009000, 0x1000F000, 0x1000F010 })
        Console.WriteLine($"mem[0x{a:X8}]=0x{probe.Memory.Read32(a):X8}");

    // Histogram of PC high words over a short dense sample
    var hist = new Dictionary<uint, int>();
    for (int i = 0; i < 2000; i++)
    {
        probe.RunFor(200);
        uint p = (uint)(probe.EE.PC & 0x1FFFFF00UL); // 256-byte buckets
        hist[p] = hist.GetValueOrDefault(p) + 1;
    }
    Console.WriteLine("--- PC buckets (after +400k) ---");
    foreach (var kv in hist.OrderByDescending(k => k.Value).Take(12))
        Console.WriteLine($"  0x{kv.Key:X8} x{kv.Value}");

    Environment.Exit(probe.Gs.PixelsWritten > 0 ? 0 : 1);
}

Console.WriteLine($"=== {VersionInfo.Banner} ===\n");

var sys = new Ps2System();

sys.Memory.Write32(0x4000, 0);
sys.Memory.Write32(0x4004, 0);
sys.Memory.Write32(0x4008, 0);
sys.EE.PC = 0x4000;
sys.Debugger.Enabled = true;
sys.Debugger.AddBreakpoint(0x4008);
sys.EE.Step(16);
Console.WriteLine($"[DBG] halted={sys.Debugger.Halted} PC=0x{sys.Debugger.HaltPc:X8}");

byte[] raw = sys.SaveState(false);
byte[] zip = sys.SaveState(true);
Console.WriteLine($"[SAVE] raw={raw.Length:N0} compressed={zip.Length:N0}");

sys.InputRecording.StartRecording();
sys.Pad.SetButtons(1);
sys.RunFor(500);
sys.InputRecording.StopRecording();
byte[] tape = sys.InputRecording.Serialize();
Console.WriteLine($"[INP] tape bytes={tape.Length} frames={sys.InputRecording.FrameCount}");

sys.Gs.RenderTestScene();
sys.PresentFrame();
Console.WriteLine($"[PRES] {sys.Present.Active.Name} count={sys.Present.Software.PresentCount}");

Console.WriteLine("\nHeadless OK. Commands:");
Console.WriteLine("  commercial-boot [user-media.json] [report.json]");
Console.WriteLine("  dump-spine [user-media.json]");
Console.WriteLine("  play-path");
Console.WriteLine("  majority-campaign [out.md]");
Console.WriteLine("  majority-catalog [out.md]");
Console.WriteLine("  commercial-checklist");
Console.WriteLine("  netplay-soak [frames]");
Console.WriteLine("  netplay-cert [frames]");
Console.WriteLine("Copy user-media.example.json → user-media.json (gitignored).");
