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
//   detps2 elf-info [user-media.json]
//   detps2 blocker-trace [user-media.json] [cycles] [--dump=ADDR:LEN] [--trace-window=N]
//   detps2 disasm <user-media.json> <cycles> <addr>:<len> [titleIndex]
//   detps2 pad-inject [user-media.json] --cycles=N [--press=BUTTON:CYCLE[:HOLD]]... [--sample-every=N] [--host-present]
//   detps2 long-run <user-media.json> --hours=N [--log=PATH] [--checkpoint-seconds=S]

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
    string boot2 = (cnfBytes != null ? SystemCnf.Parse(System.Text.Encoding.ASCII.GetString(cnfBytes)).Boot2 : "") ?? "";
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
    uint? watchAddrArg = null;
    foreach (var a in args)
        if (a.StartsWith("--watch=")) watchAddrArg = Convert.ToUInt32(a.Substring(8), 16);
    // Defers arming --watch until this many cycles have already run — without it, --watch
    // records every access to the address across the ENTIRE run (often hundreds of thousands
    // of hits for a commonly-reused stack slot), drowning out the specific access you want.
    ulong watchAfter = 0;
    foreach (var a in args)
        if (a.StartsWith("--watch-after=") && ulong.TryParse(a.AsSpan(14), out var wa)) watchAfter = wa;
    foreach (var a in args)
        if (a.StartsWith("--pcbreak="))
        {
            var pcbParts = a.Substring(10).Split(':');
            EmotionEngine.PcBreakGpr = Convert.ToUInt32(pcbParts[0], 16);
            EmotionEngine.PcBreakEnd = pcbParts.Length > 1 ? Convert.ToUInt32(pcbParts[1], 16) : (uint?)null;
        }
    // --host-present: drive ActiveQuirk.OnHostPresent once per 1M-cycle slice, matching
    // probe-frame's and the real Desktop per-tick RunFor+OnHostPresent pattern. Without this,
    // MidwayBootAssist's FMV/logo pacing (which only advances on host-present ticks by design,
    // see MidwayBootAssist.OnHostPresent's own doc comment) never fires under blocker-trace, so
    // a plain RunFor-only trace can show "0 pcbreak hits past the logo" even though the logo
    // never even finished — a test-harness gap, not evidence of unreachability. Opt-in (not
    // default) to keep this tool's existing plain-RunFor telemetry comparable to prior runs.
    bool driveHostPresent = args.Contains("--host-present");
    if (args.Contains("--no-assist")) Ps2System.DisableMidwayAssist = true;
    if (args.Contains("--no-force-sif")) Ps2System.DisableForceSifInit = true;
    if (args.Contains("--no-unstick-waits")) Ps2System.DisableUnstickSifWaits = true;
    if (args.Contains("--no-auto-complete")) Ps2System.DisableAutoCompleteWorkItems = true;
    // --track-writers + --find-writer=ADDR[:LEN]: a retroactive "who last wrote this address"
    // index (see SystemMemory.LastWriterLog's own doc comment) — for tracing a corrupted value
    // back to its source when you don't know the destination address until AFTER it's already
    // been written (--watch requires knowing it in advance; this doesn't).
    if (args.Contains("--track-writers")) SystemMemory.TrackLastWriter = true;
    var findWriterRanges = new List<(uint start, uint len)>();
    foreach (var a in args)
        if (a.StartsWith("--find-writer="))
        {
            var parts = a.Substring(14).Split(':');
            uint fwStart = Convert.ToUInt32(parts[0], 16);
            uint fwLen = parts.Length > 1 ? Convert.ToUInt32(parts[1], 16) : 4u;
            findWriterRanges.Add((fwStart, fwLen));
            SystemMemory.TrackLastWriter = true; // implied
        }
    // --find-value=VALHEX[:MASKHEX]: reverse lookup — which address(es) last held this exact
    // (or masked-match) value, for when you have a corrupted register/pointer value but don't
    // know which memory address it was read from. Complements --find-writer (address -> writer).
    var findValues = new List<(uint value, uint mask)>();
    foreach (var a in args)
        if (a.StartsWith("--find-value="))
        {
            var parts = a.Substring(13).Split(':');
            uint fvVal = Convert.ToUInt32(parts[0], 16);
            uint fvMask = parts.Length > 1 ? Convert.ToUInt32(parts[1], 16) : 0xFFFFFFFFu;
            findValues.Add((fvVal, fvMask));
            SystemMemory.TrackLastWriter = true; // implied
        }
    // --trace-threads: log every thread create/start/delete and every context switch (cooperative,
    // syscall-boundary, or forced preemption) with (cycle, threadId, pc, sp) — see
    // KernelState.ThreadLog's own doc comment for why this exists: a raw PC trace alone can't
    // distinguish "two unrelated calls into the same shared function" from "one continuous call,"
    // which is exactly what caused several false leads tracing MK Shaolin Monks. --thread-at=CYCLE
    // answers "which thread was active at this cycle" directly instead of re-deriving it by hand.
    if (args.Contains("--trace-threads")) KernelState.TraceThreads = true;
    var threadAtCycles = new List<ulong>();
    foreach (var a in args)
        if (a.StartsWith("--thread-at=") && ulong.TryParse(a.AsSpan(12), out var tac))
        {
            threadAtCycles.Add(tac);
            KernelState.TraceThreads = true; // implied
        }
    // --track-transfers: log every DMAC channel start, SIF0/SIF1 EE<->IOP transfer, and GIF
    // Path1/2/3 receive (EE->GS) — the actual bulk-transmission mechanisms on real hardware, as
    // opposed to individual CPU store instructions (already covered by --track-writers). See
    // TransferLog's own doc comment. --find-transfer=ADDR[:LEN] filters the dump to transfers
    // whose source or dest falls in the given range, instead of printing every single one.
    if (args.Contains("--track-transfers")) TransferLog.Enabled = true;
    var findTransferRanges = new List<(uint start, uint len)>();
    foreach (var a in args)
        if (a.StartsWith("--find-transfer="))
        {
            var parts = a.Substring(16).Split(':');
            uint ftStart = Convert.ToUInt32(parts[0], 16);
            uint ftLen = parts.Length > 1 ? Convert.ToUInt32(parts[1], 16) : 4u;
            findTransferRanges.Add((ftStart, ftLen));
            TransferLog.Enabled = true; // implied
        }

    if (!cfg.HasBios) { Console.WriteLine("No BIOS in user-media.json"); Environment.Exit(1); }
    foreach (var title in cfg.Titles)
    {
        if (!title.Exists) { Console.WriteLine($"[{title.Id}] missing: {title.Path}"); continue; }
        SystemMemory.WatchHits.Clear();
        SystemMemory.LastWriterLog.Clear();
        KernelState.ThreadLog.Clear();
        TransferLog.Reset();
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
        if (watchAddrArg.HasValue && watchAfter > 0)
        {
            traceSys.RunFor(Math.Min(watchAfter, cycles));
            SystemMemory.WatchAddr = watchAddrArg;
        }
        else if (watchAddrArg.HasValue)
        {
            SystemMemory.WatchAddr = watchAddrArg;
        }
        ulong remaining = cycles > watchAfter ? cycles - watchAfter : 0;
        if (driveHostPresent)
        {
            const ulong slice = 1_000_000;
            while (remaining > 0)
            {
                ulong step = Math.Min(slice, remaining);
                traceSys.RunFor(step);
                traceSys.ActiveQuirk?.OnHostPresent(traceSys);
                remaining -= step;
            }
        }
        else
        {
            traceSys.RunFor(remaining);
        }
        // telemetryHits/telemetryUniqueKeys (previously printed as bare "hits"/"unique" right next
        // to "PC=..." — easy to misread as a PC-visit or loop-iteration counter, which it is NOT;
        // confirmed a real point of confusion investigating Shaolin Monks 2026-07-27, see
        // DEVELOPER_GUIDE.md). This is Telemetry.TotalHits/UniqueKeys: how many *notable/unknown*
        // events (e.g. UnknownMmioRead) have been recorded, and how many distinct keys among them.
        // It freezing does NOT mean the EE stopped executing or is stuck looping — it means no NEW
        // unknown/notable event has fired since; check EE.exitRequested/px/syscalls (printed below)
        // for whether real execution is actually progressing.
        Console.WriteLine($"  after {cycles} cyc: PC=0x{traceSys.EE.PC:X8} telemetryHits={traceSys.Telemetry.TotalHits} telemetryUniqueKeys={traceSys.Telemetry.UniqueKeys}");
        // Printed immediately, right after the header line, rather than buried further down —
        // this is the single most important "is the run actually still doing anything" signal
        // (EmotionEngine.Step's very first real check every call is `if (ExitRequested) break;`,
        // so once this is true every subsequent cycle of however large a --cycles budget executes
        // literally nothing further for the EE, while IOP/SPU2 keep advancing independently and
        // can make a halted run look deceptively "still alive" via growing spu2Samples/IOP.pc).
        // Confirmed a real, costly point of confusion investigating Shaolin Monks 2026-07-27 — a
        // "clean idle steady state" was wrongly diagnosed for one whole investigation round before
        // this exact flag was checked directly. See DEVELOPER_GUIDE.md.
        Console.WriteLine($"  EE: exitRequested={traceSys.Hle.ExitRequested} exitCode={traceSys.Hle.ExitCode}");
        if (SystemMemory.WatchAddr.HasValue)
        {
            Console.WriteLine($"  watch 0x{SystemMemory.WatchAddr.Value:X8}: {SystemMemory.WatchHits.Count} access(es)");
            foreach (var (wpc, wvaddr, wval, isWrite) in SystemMemory.WatchHits)
            {
                string kind = isWrite ? $"WROTE 0x{wval:X8}" : "READ ";
                Console.WriteLine($"    pc=0x{wpc:X8} {kind} 0x{wvaddr:X8}  {EeDisassembler.Disassemble((uint)wpc, traceSys.Memory.Read32((uint)wpc))}");
            }
        }
        if (findWriterRanges.Count > 0)
        {
            Console.WriteLine($"  last-writer log: {SystemMemory.LastWriterLog.Count} distinct address(es) tracked");
            foreach (var (fwStart, fwLen) in findWriterRanges)
            {
                Console.WriteLine($"  find-writer 0x{fwStart:X8}..0x{fwStart + fwLen:X8}:");
                for (uint addr = fwStart & ~3u; addr < fwStart + fwLen; addr += 4)
                {
                    // Key by the same physical-or-scratchpad scheme LastWriterLog is stored
                    // under (see SystemMemory.NoteLastWriter's own doc comment) — otherwise a
                    // query via a different KSEG alias than the one the write actually used
                    // would silently miss it.
                    bool isScratch = addr is >= SystemMemory.SPR_BASE and < SystemMemory.SPR_BASE + SystemMemory.SPR_SIZE;
                    uint key = isScratch ? addr : (uint)(traceSys.Memory.TranslateAddress(addr) & 0xFFFFFFFCUL);
                    if (SystemMemory.LastWriterLog.TryGetValue(key, out var w))
                        Console.WriteLine($"    0x{addr:X8}: last written at cyc={w.Cycle} pc=0x{w.Pc:X8} value=0x{w.Value:X8}  {EeDisassembler.Disassemble((uint)w.Pc, traceSys.Memory.Read32((uint)w.Pc))}");
                    else
                        Console.WriteLine($"    0x{addr:X8}: NEVER WRITTEN (current value=0x{traceSys.Memory.Read32(addr):X8})");
                }
            }
        }
        foreach (var (fvVal, fvMask) in findValues)
        {
            Console.WriteLine($"  find-value 0x{fvVal:X8} mask=0x{fvMask:X8}:");
            int fvHits = 0;
            foreach (var kv in SystemMemory.LastWriterLog)
            {
                if ((kv.Value.Value & fvMask) != (fvVal & fvMask)) continue;
                Console.WriteLine($"    addr=0x{kv.Key:X8} written at cyc={kv.Value.Cycle} pc=0x{kv.Value.Pc:X8} value=0x{kv.Value.Value:X8}  {EeDisassembler.Disassemble((uint)kv.Value.Pc, traceSys.Memory.Read32((uint)kv.Value.Pc))}");
                if (++fvHits >= 50) { Console.WriteLine("    ...(truncated at 50)"); break; }
            }
            if (fvHits == 0) Console.WriteLine("    no address currently holds this value");
        }
        if (KernelState.TraceThreads)
        {
            Console.WriteLine($"  thread log: {KernelState.ThreadLog.Count} event(s)");
            foreach (var ev in KernelState.ThreadLog)
                Console.WriteLine($"    cyc={ev.Cycle,10} {ev.Kind,-10} tid={ev.ThreadId,3} pc=0x{ev.Pc:X8} sp=0x{ev.Sp:X8} {ev.Detail}");
            foreach (var tac in threadAtCycles)
            {
                KernelState.ThreadEvent? last = null;
                foreach (var ev in KernelState.ThreadLog)
                {
                    if (ev.Cycle > tac) break;
                    last = ev;
                }
                Console.WriteLine(last.HasValue
                    ? $"  thread-at cyc={tac}: most recent event was {last.Value.Kind} tid={last.Value.ThreadId} at cyc={last.Value.Cycle} pc=0x{last.Value.Pc:X8} sp=0x{last.Value.Sp:X8} {last.Value.Detail}"
                    : $"  thread-at cyc={tac}: no thread event recorded before this cycle");
            }
        }
        if (TransferLog.Enabled)
        {
            Console.WriteLine($"  transfer log: {TransferLog.Events.Count} event(s)");
            if (findTransferRanges.Count > 0)
            {
                foreach (var (ftStart, ftLen) in findTransferRanges)
                {
                    Console.WriteLine($"  find-transfer 0x{ftStart:X8}..0x{ftStart + ftLen:X8}:");
                    int ftHits = 0;
                    foreach (var ev in TransferLog.Events)
                    {
                        bool srcHit = ev.Source >= ftStart && ev.Source < ftStart + ftLen;
                        bool dstHit = ev.Dest >= ftStart && ev.Dest < ftStart + ftLen;
                        if (!srcHit && !dstHit) continue;
                        Console.WriteLine($"    cyc={ev.Cycle,10} pc=0x{ev.Pc:X8} {ev.Kind,-14} src=0x{ev.Source:X8} dst=0x{ev.Dest:X8} size=0x{ev.Size:X} {ev.Detail}");
                        if (++ftHits >= 100) { Console.WriteLine("    ...(truncated at 100)"); break; }
                    }
                    if (ftHits == 0) Console.WriteLine("    no transfer touched this range");
                }
            }
            else
            {
                // No filter given — dump is otherwise unbounded on a long run; show a bounded
                // sample (first/last 25) rather than silently truncating with no indication.
                int total = TransferLog.Events.Count;
                int shown = 0;
                foreach (var ev in TransferLog.Events)
                {
                    Console.WriteLine($"    cyc={ev.Cycle,10} pc=0x{ev.Pc:X8} {ev.Kind,-14} src=0x{ev.Source:X8} dst=0x{ev.Dest:X8} size=0x{ev.Size:X} {ev.Detail}");
                    if (++shown >= 25) break;
                }
                if (total > 50)
                {
                    Console.WriteLine($"    ...({total - 50} more, use --find-transfer=ADDR to filter)...");
                    foreach (var ev in TransferLog.Events.Skip(Math.Max(0, total - 25)))
                        Console.WriteLine($"    cyc={ev.Cycle,10} pc=0x{ev.Pc:X8} {ev.Kind,-14} src=0x{ev.Source:X8} dst=0x{ev.Dest:X8} size=0x{ev.Size:X} {ev.Detail}");
                }
            }
        }
        Console.WriteLine($"  px={traceSys.Gs.PixelsWritten} gifPath3={traceSys.Gif.Path3Transfers} dmac={traceSys.Dmac.TransfersCompleted} sifBytes={traceSys.Sif.BytesTransferred} syscalls={traceSys.Hle.SyscallCount} spu2Writes={traceSys.Spu2.Writes} spu2Samples={traceSys.Spu2.SamplesGenerated} cdvdSectors={traceSys.Cdvd.SectorsRead}");
        Console.WriteLine($"  lastCreatedThread: entry=0x{traceSys.Hle.Sony?.LastCreatedThreadEntry:X8} sp=0x{traceSys.Hle.Sony?.LastCreatedThreadStack:X8}");
        if (traceSys.Hle.Sony != null)
        {
            Console.WriteLine("  threads:");
            foreach (var t in traceSys.Hle.Kernel.AllThreads)
                Console.WriteLine($"    id={t.Id} alive={t.Alive} started={t.Started} sleeping={t.Sleeping} waitSemaId={t.WaitSemaId}");
            Console.WriteLine($"  currentThreadId={traceSys.Hle.Kernel.CurrentThreadId}");
        }
        Console.WriteLine($"  IOP: pc=0x{traceSys.Iop.PC:X8}");
        if (traceSys.Hle.Sony != null)
        {
            Console.WriteLine("  top syscalls:");
            // >100 hides everything on a low-syscall-count run (e.g. 41 total) where every
            // individual number is well under that threshold but still the whole story —
            // show the top 30 by count unconditionally, high-frequency ones are still first.
            foreach (var kv in traceSys.Hle.Sony.SyscallHistogram.OrderByDescending(k => k.Value).Take(30))
                Console.WriteLine($"    0x{kv.Key:X2} x{kv.Value}");
            var rpc = traceSys.Hle.Sony.RealRpc;
            Console.WriteLine($"  RealSifRpc: binds={rpc.Binds} calls={rpc.Calls} unknownServiceCalls={rpc.UnknownServiceCalls} unknownBindSids={rpc.UnknownBindSids}");
            foreach (var sid in rpc.UnknownSidsSeen)
                Console.WriteLine($"    unknown sid=0x{sid:X8}");
        }
        foreach (var ev in traceSys.Telemetry.SnapshotEvents())
            Console.WriteLine($"    cyc={ev.Cycle,10} pc=0x{ev.Pc:X8} {ev.Kind,-16} key=0x{ev.Key:X8} {ev.Detail}");
        if (PcProfiler.Enabled)
        {
            Console.WriteLine($"  PcProfiler: samples={PcProfiler.TotalSamples} unique={PcProfiler.UniqueCount}");
            foreach (var (pc, count) in PcProfiler.Top(20))
                Console.WriteLine($"    0x{pc:X8} x{count}");
        }

        foreach (var a in args)
        {
            if (!a.StartsWith("--dump=")) continue;
            var parts = a.Substring(7).Split(':');
            uint start = Convert.ToUInt32(parts[0], 16);
            uint len = parts.Length > 1 ? Convert.ToUInt32(parts[1], 16) : 0x40u;
            Console.WriteLine($"  dump 0x{start:X8}..0x{start + len:X8}:");
            for (uint addr = start; addr < start + len; addr += 4)
            {
                uint word = traceSys.Memory.Read32(addr);
                Console.WriteLine($"    {addr:X8}: {word:X8}  {EeDisassembler.Disassemble(addr, word)}");
            }
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
            if (!a.StartsWith("--find-string=")) continue;
            string needle = a.Substring("--find-string=".Length);
            byte[] pat = System.Text.Encoding.ASCII.GetBytes(needle);
            Console.WriteLine($"  find-string \"{needle}\" ({pat.Length} bytes) in RDRAM (0x00000000-0x01FFFFFF):");
            int found = 0;
            for (uint addr = 0; addr <= SystemMemory.RDRAM_SIZE - pat.Length && found < 100; addr++)
            {
                bool match = true;
                for (int i = 0; i < pat.Length; i++)
                    if (traceSys.Memory.Read8(addr + (uint)i) != pat[i]) { match = false; break; }
                if (match)
                {
                    byte after = addr + (uint)pat.Length < SystemMemory.RDRAM_SIZE ? traceSys.Memory.Read8(addr + (uint)pat.Length) : (byte)0xFF;
                    Console.WriteLine($"    0x{addr:X8} (next byte after match: 0x{after:X2})");
                    found++;
                }
            }
            if (found == 0) Console.WriteLine("    no match");
        }

        foreach (var a in args)
        {
            if (!a.StartsWith("--trace-window=")) continue;
            ulong window = ulong.TryParse(a.AsSpan(15), out var w) ? w : 3000ul;
            bool chrono = args.Contains("--trace-chrono");
            traceSys.Tracer.MaxEntries = (int)Math.Min(window + 16, int.MaxValue);
            traceSys.Tracer.Enable();
            traceSys.RunFor(window);
            traceSys.Tracer.Disable();
            Console.WriteLine($"  trace-window: {traceSys.Tracer.Count} entries captured after cycle {cycles}");
            if (chrono)
            {
                // Entries are already in execution order (Tracer.Append is insertion-ordered) —
                // unlike the deduped/address-sorted view below, this preserves control flow, so
                // a bad jr/jalr/branch shows up as a visible discontinuity in the pc column.
                Console.WriteLine("  chronological trace:");
                foreach (var e in traceSys.Tracer.Entries)
                    Console.WriteLine($"    cyc={e.Cycle,10} pc=0x{e.Pc:X8} op=0x{e.Opcode:X8}  {EeDisassembler.Disassemble((uint)e.Pc, e.Opcode)}");
                continue;
            }
            var pcCounts = new Dictionary<ulong, int>();
            foreach (var e in traceSys.Tracer.Entries) pcCounts[e.Pc] = pcCounts.GetValueOrDefault(e.Pc) + 1;
            Console.WriteLine($"  unique PCs in window: {pcCounts.Count}");
            foreach (var kv in pcCounts.OrderBy(k => k.Key))
            {
                uint word = traceSys.Memory.Read32((uint)kv.Key);
                Console.WriteLine($"    pc=0x{kv.Key:X8} hits={kv.Value} op=0x{word:X8}  {EeDisassembler.Disassemble((uint)kv.Key, word)}");
            }
        }
    }
    Environment.Exit(0);
}

// detps2 pad-inject [user-media.json] --cycles=N [--press=BUTTON:CYCLE[:HOLDCYCLES]]...
//        [--sample-every=N] [--host-present]
//   A real controller-input API for the CLI: schedules actual PadInput.Press/Release calls at
//   specific cycle counts during a live run (not a heuristic auto-press loop), and prints a
//   state sample (px/prims/syscalls/PC/exitRequested) immediately around every event plus on a
//   regular cadence throughout — so "does pressing this button actually change anything" can be
//   answered directly from observed state deltas, rather than inferred from code-shape guessing.
// Built specifically to settle whether a given halt point is a menu genuinely waiting for input
// or something else, by sending real button presses through the same Pad object the game's own
// pad-polling code reads and watching what happens next.
//   Button names match PadInput.Button: Select, L3, R3, Start, Up, Right, Down, Left, L2, R2,
//   L1, R1, Triangle, Circle, Cross, Square.
// Example: detps2 pad-inject user-media.json --cycles=300000000 --host-present
//          --press=Start:22600000:100000 --press=Cross:25000000:100000 --sample-every=1000000
if (args.Length > 0 && args[0].Equals("pad-inject", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(VersionInfo.Banner);
    UserMediaConfig picfg = args.Length > 1 && !args[1].StartsWith("--")
        ? UserMediaConfig.Load(args[1])
        : UserMediaConfig.LoadDefault();
    ulong piCycles = 50_000_000;
    foreach (var a in args)
        if (a.StartsWith("--cycles=") && ulong.TryParse(a.AsSpan(9), out var c)) piCycles = c;
    ulong sampleEvery = 1_000_000;
    foreach (var a in args)
        if (a.StartsWith("--sample-every=") && ulong.TryParse(a.AsSpan(15), out var se)) sampleEvery = se;
    bool piHostPresent = args.Contains("--host-present");

    var events = new List<(ulong pressAt, ulong releaseAt, PadInput.Button button, string name)>();
    foreach (var a in args)
    {
        if (!a.StartsWith("--press=")) continue;
        var parts = a.Substring(8).Split(':');
        if (parts.Length < 2)
        {
            Console.WriteLine($"bad --press= arg (need BUTTON:CYCLE[:HOLD]): {a}");
            continue;
        }
        if (!Enum.TryParse<PadInput.Button>(parts[0], ignoreCase: true, out var btn))
        {
            Console.WriteLine($"unknown button '{parts[0]}' — valid: {string.Join(",", Enum.GetNames(typeof(PadInput.Button)))}");
            continue;
        }
        if (!ulong.TryParse(parts[1], out var pressAt))
        {
            Console.WriteLine($"bad cycle in --press= arg: {a}");
            continue;
        }
        ulong hold = 50_000;
        if (parts.Length > 2) ulong.TryParse(parts[2], out hold);
        events.Add((pressAt, pressAt + hold, btn, parts[0]));
    }
    events.Sort((x, y) => x.pressAt.CompareTo(y.pressAt));

    if (!picfg.HasBios) { Console.WriteLine("No BIOS in user-media.json"); Environment.Exit(1); }
    var piTitle = picfg.Titles.FirstOrDefault(t => t.Exists);
    if (piTitle == null) { Console.WriteLine("No existing title in user-media.json"); Environment.Exit(1); }

    var piSys = new Ps2System();
    piSys.LoadBios(picfg.BiosPath);
    string piBootMsg = (piTitle.Kind ?? "iso").ToLowerInvariant() == "elf"
        ? $"ELF entry=0x{piSys.LoadElf(File.ReadAllBytes(piTitle.Path)).Entry:X8}"
        : piSys.BootDiscFile(piTitle.Path).Message;
    Console.WriteLine($"[{piTitle.Id}] {piBootMsg}");
    Console.WriteLine(events.Count > 0
        ? $"scheduled: {string.Join(", ", events.Select(e => $"{e.name}@{e.pressAt}-{e.releaseAt}"))}"
        : "scheduled: (no --press= events given — plain observation run)");
    Console.WriteLine();
    // gifPath3/dmac/sifBytes alongside px: px is unreliable once MidwayBootAssist's logo-hold
    // overlay is active (Gs.SetHostOverlay unconditionally adds a full framebuffer's worth of
    // pixels to PixelsWritten every host-present tick just to keep the "no video" UI hint from
    // showing, per its own doc comment — nothing to do with real game rendering). gifPath3
    // (Gif.Path3Transfers) is what MidwayBootAssist itself uses to detect genuine organic
    // rendering has started (KeepLogoVisible drops the overlay once Path3Transfers > 4) — a
    // trustworthy signal px is not, once the logo-hold path is active.
    Console.WriteLine($"{"cyc",12}  {"PC",-10} {"px",10} {"prims",7} {"syscalls",8} {"gifPath1",8} {"gifPath3",8} {"dmac",6} {"sifBytes",8} {"exit",5}  note");

    long prevPx = -1, prevPrims = -1;
    ulong prevSyscalls = ulong.MaxValue;
    ulong prevGifPath1 = ulong.MaxValue, prevGifPath3 = ulong.MaxValue, prevDmac = ulong.MaxValue, prevSifBytes = ulong.MaxValue;
    ulong done = 0, nextSample = 0, nextHostPresent = 1_000_000;
    int eventIdx = 0;
    var pending = new List<(ulong releaseAt, PadInput.Button button, string name)>();
    // OnHostPresent must fire at EXACTLY the same 1,000,000-cycle boundaries blocker-trace's own
    // --host-present uses (its slice is 1,000,000 for both RunFor stepping and the OnHostPresent
    // call, every iteration) — MidwayBootAssist paces the logo/FMV sequence by counting *calls*,
    // not cycles, so firing it at a different offset measurably changes timing. An early version
    // of this tool used a fixed, finer RunFor step (25,000) for event precision and called
    // OnHostPresent independently on its own 1,000,000-cycle counter — that still desynced from
    // blocker-trace (different px/syscalls/exit trajectory at the same --cycles) because the
    // very first OnHostPresent call landed at cyc=1,025,000 instead of exactly 1,000,000, and the
    // drift compounded from there. Fixed by computing each step size adaptively: run exactly up
    // to whichever is nearer, the next host-present boundary or the next scheduled press/release
    // event, so OnHostPresent's cycle alignment is bit-for-bit identical to blocker-trace's while
    // press/release events still land at their exact requested cycle (2026-07-27).
    const ulong hostPresentPeriod = 1_000_000;

    void Sample(string note)
    {
        long px = piSys.Gs.PixelsWritten, prims = piSys.Gs.PrimitivesDrawn;
        ulong syscalls = piSys.Hle.SyscallCount;
        ulong gifPath1 = piSys.Gif.Path1Transfers, gifPath3 = piSys.Gif.Path3Transfers, dmac = piSys.Dmac.TransfersCompleted, sifBytes = piSys.Sif.BytesTransferred;
        bool changed = px != prevPx || prims != prevPrims || syscalls != prevSyscalls
            || gifPath1 != prevGifPath1 || gifPath3 != prevGifPath3 || dmac != prevDmac || sifBytes != prevSifBytes;
        string mark = changed && prevSyscalls != ulong.MaxValue ? "  <-- CHANGED since last sample" : "";
        Console.WriteLine($"{done,12}  0x{piSys.EE.PC:X8} {px,10} {prims,7} {syscalls,8} {gifPath1,8} {gifPath3,8} {dmac,6} {sifBytes,8} {piSys.Hle.ExitRequested,5}  {note}{mark}");
        prevPx = px; prevPrims = prims; prevSyscalls = syscalls;
        prevGifPath1 = gifPath1; prevGifPath3 = gifPath3; prevDmac = dmac; prevSifBytes = sifBytes;
    }

    Sample("(initial)");
    while (done < piCycles)
    {
        ulong nextBoundary = piHostPresent ? nextHostPresent : piCycles;
        if (eventIdx < events.Count) nextBoundary = Math.Min(nextBoundary, events[eventIdx].pressAt);
        foreach (var p in pending) nextBoundary = Math.Min(nextBoundary, p.releaseAt);
        nextBoundary = Math.Min(nextBoundary, piCycles);
        ulong step = nextBoundary > done ? nextBoundary - done : 1;
        step = Math.Min(step, piCycles - done);
        piSys.RunFor(step);
        done += step;
        if (piHostPresent && done >= nextHostPresent)
        {
            piSys.ActiveQuirk?.OnHostPresent(piSys);
            nextHostPresent = done + hostPresentPeriod;
        }

        bool fired = false;
        while (eventIdx < events.Count && events[eventIdx].pressAt <= done)
        {
            var e = events[eventIdx++];
            piSys.Pad.Press(e.button);
            pending.Add((e.releaseAt, e.button, e.name));
            Console.WriteLine($"  >>> cyc={done,12} PRESS   {e.name}");
            fired = true;
        }
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            if (pending[i].releaseAt > done) continue;
            piSys.Pad.Release(pending[i].button);
            Console.WriteLine($"  <<< cyc={done,12} RELEASE {pending[i].name}");
            pending.RemoveAt(i);
            fired = true;
        }
        if (fired)
        {
            Sample("(post-event)");
            nextSample = done + sampleEvery;
        }
        else if (done >= nextSample)
        {
            Sample("");
            nextSample = done + sampleEvery;
        }
    }
    Sample("(final)");
    Environment.Exit(0);
}

// detps2 long-run <media.json> --hours=N [--log=PATH] [--checkpoint-seconds=S] — boots the
// first title and runs it in bounded chunks for up to N wall-clock hours, writing a flushed
// checkpoint line to a log file after every chunk. Exists specifically so a multi-hour
// unattended run survives the terminal being closed or the process being killed: the log file
// is the durable record, not console output, which dies with the window. Ctrl+C writes one
// final "interrupted" line before exiting; the log is otherwise append-only and safe to tail
// while the run is still in progress.
if (args.Length > 0 && args[0].Equals("long-run", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(VersionInfo.Banner);
    if (args.Length < 2) { Console.WriteLine("usage: detps2 long-run <media.json> --hours=N [--log=PATH] [--checkpoint-seconds=S]"); Environment.Exit(1); }
    UserMediaConfig lcfg = UserMediaConfig.Load(args[1]);
    double hours = 6.0;
    string? logPath = null;
    int checkpointSeconds = 60;
    foreach (var a in args)
    {
        if (a.StartsWith("--hours=") && double.TryParse(a.AsSpan(8), out var h)) hours = h;
        else if (a.StartsWith("--log=")) logPath = a.Substring(6);
        else if (a.StartsWith("--checkpoint-seconds=") && int.TryParse(a.AsSpan(21), out var cs)) checkpointSeconds = cs;
    }
    logPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DetPS2", $"long-run-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

    if (lcfg.Titles.Count == 0) { Console.WriteLine("No titles in user-media.json"); Environment.Exit(1); }
    var ltitle = lcfg.Titles[0];
    var lsys = new Ps2System();
    lsys.LoadBios(lcfg.BiosPath!);
    var lmsg = lsys.BootDiscFile(ltitle.Path);

    using var logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
    void Log(string line)
    {
        string stamped = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}";
        Console.WriteLine(stamped);
        logWriter.WriteLine(stamped);
        logWriter.Flush();
    }

    Log($"=== long-run started: {ltitle.Id} target={hours}h checkpoint={checkpointSeconds}s log={logPath} ===");
    Log($"boot: {lmsg.Message}");

    bool stopRequested = false;
    Console.CancelKeyPress += (_, ev) =>
    {
        ev.Cancel = true; // let us write the final line before exiting
        stopRequested = true;
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        // Best-effort: fires on graceful shutdown paths; a hard taskkill /F or closed
        // console window will NOT run this, which is exactly why checkpoints below
        // (not this handler) are the actual durability guarantee for this feature.
        try { Log("=== process exiting ==="); } catch { /* stream may already be gone */ }
    };

    var started = DateTime.Now;
    var deadline = started.AddHours(hours);
    ulong totalCycles = 0;
    const ulong chunkCycles = 20_000_000; // ~a few seconds per chunk at typical interpreter throughput
    var lastCheckpoint = started;
    ulong lastPx = 0;
    int stableCheckpoints = 0;

    while (DateTime.Now < deadline && !stopRequested)
    {
        lsys.RunFor(chunkCycles);
        totalCycles += chunkCycles;

        if ((DateTime.Now - lastCheckpoint).TotalSeconds >= checkpointSeconds)
        {
            lastCheckpoint = DateTime.Now;
            ulong px = (ulong)lsys.Gs.PixelsWritten;
            bool stalledPx = px == lastPx;
            stableCheckpoints = stalledPx ? stableCheckpoints + 1 : 0;
            lastPx = px;
            var elapsed = DateTime.Now - started;
            Log($"checkpoint cyc={totalCycles} elapsed={elapsed:hh\\:mm\\:ss} PC=0x{lsys.EE.PC:X8} " +
                $"px={px} gifPath3={lsys.Gif.Path3Transfers} dmac={lsys.Dmac.TransfersCompleted} " +
                $"syscalls={lsys.Hle.SyscallCount} spu2Samples={lsys.Spu2.SamplesGenerated} " +
                $"rpcBinds={lsys.Hle.Sony?.RealRpc.Binds} rpcCalls={lsys.Hle.Sony?.RealRpc.Calls} " +
                $"pxStableFor={stableCheckpoints}x{checkpointSeconds}s");
            if (stableCheckpoints > 0 && stableCheckpoints % 10 == 0)
                Log($"NOTE: pixel count hasn't changed in {stableCheckpoints * checkpointSeconds}s — " +
                    "may be a legitimate long CPU-bound stretch (matches loops seen earlier this session) " +
                    "or a genuine new stall; worth a --dump/--trace-window pass at this PC if it persists.");
        }
    }

    Log($"=== long-run ended: reason={(stopRequested ? "interrupted" : "deadline reached")} " +
        $"totalCycles={totalCycles} PC=0x{lsys.EE.PC:X8} px={lsys.Gs.PixelsWritten} ===");
    string ppmPath = Path.Combine(Path.GetDirectoryName(logPath)!, "long-run-final-frame.ppm");
    lsys.Gs.SaveFramebufferAsPPM(ppmPath);
    Log($"wrote final framebuffer: {ppmPath}");
    Environment.Exit(0);
}

// detps2 find-store <media.json> <cycles> <targetAddrHex> [codeStart] [codeEnd] — boot, run N
// cycles, then scan a code range for "lui rX, hi16 ... sw/sh/sb rY, lo16(rX)" instruction pairs
// that write to the given absolute address, tracking a small forward window per lui so it
// catches near (not just adjacent) store instructions. Used to find where game code populates a
// specific global (e.g. a cached SIF RPC client-data pointer) so we can see what it's supposed
// to hold and diagnose why our HLE leaves it in a "not yet ready" state.
if (args.Length > 0 && args[0].Equals("find-store", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(VersionInfo.Banner);
    if (args.Length < 4) { Console.WriteLine("usage: detps2 find-store <media.json> <cycles> <targetAddrHex> [codeStart] [codeEnd]"); Environment.Exit(1); }
    UserMediaConfig fcfg = UserMediaConfig.Load(args[1]);
    ulong fcycles = ulong.TryParse(args[2], out var fc) ? fc : 5_000_000ul;
    uint target = Convert.ToUInt32(args[3], 16);
    uint codeStart = args.Length > 4 ? Convert.ToUInt32(args[4], 16) : 0x00100000u;
    uint codeEnd = args.Length > 5 ? Convert.ToUInt32(args[5], 16) : 0x00700000u;
    var fsys = new Ps2System();
    fsys.LoadBios(fcfg.BiosPath!);
    var fmsg = fsys.BootDiscFile(fcfg.Titles[0].Path);
    Console.WriteLine($"[{fcfg.Titles[0].Id}] {fmsg.Message}");
    fsys.RunFor(fcycles);
    Console.WriteLine($"after {fcycles} cyc: PC=0x{fsys.EE.PC:X8}; scanning 0x{codeStart:X8}..0x{codeEnd:X8} for stores to 0x{target:X8}");
    ushort targetHi = (ushort)(target >> 16);
    ushort targetLo = (ushort)(target & 0xFFFF);
    if ((targetLo & 0x8000) != 0) targetHi++; // compiler emits lui(hi16+1) when the low half's sign bit would otherwise subtract
    int found = 0;
    for (uint addr = codeStart; addr < codeEnd; addr += 4)
    {
        uint op = fsys.Memory.Read32(addr);
        if ((op >> 26) != 0x0F) continue; // lui
        uint rt = (op >> 16) & 0x1F;
        if ((ushort)(op & 0xFFFF) != targetHi) continue;
        for (uint fwd = addr + 4; fwd < addr + 4 + (20 * 4) && fwd < codeEnd; fwd += 4)
        {
            uint op2 = fsys.Memory.Read32(fwd);
            uint opc2 = op2 >> 26;
            uint baseReg = (op2 >> 21) & 0x1F;
            uint rtOrRs = (op2 >> 16) & 0x1F; // for lui/addiu/ori clobber-check on rt
            if (opc2 is 0x28 or 0x29 or 0x2B && baseReg == rt && (ushort)(op2 & 0xFFFF) == targetLo)
            {
                Console.WriteLine($"  {addr:X8}: {op:X8}  {EeDisassembler.Disassemble(addr, op)}");
                Console.WriteLine($"  {fwd:X8}: {op2:X8}  {EeDisassembler.Disassemble(fwd, op2)}   <== store to target");
                found++;
                break;
            }
            // stop scanning forward if rt gets clobbered by another lui/ori/addiu/move before a matching store
            bool clobbers = (opc2 == 0x0F && rtOrRs == rt) || (opc2 == 0x0D && rtOrRs == rt) || (opc2 == 0x09 && rtOrRs == rt);
            if (clobbers) break;
        }
    }
    Console.WriteLine($"scan complete: {found} candidate store site(s) found");
    Environment.Exit(0);
}

// detps2 find-word <media.json> <cycles> <wordHex> [codeStart] [codeEnd] — boot, run N cycles,
// then scan a code range for an exact 32-bit instruction word match (e.g. a specific "jal
// 0xTARGET" encoding) so callers of a given function can be found without a symbol table.
if (args.Length > 0 && args[0].Equals("find-word", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(VersionInfo.Banner);
    if (args.Length < 4) { Console.WriteLine("usage: detps2 find-word <media.json> <cycles> <wordHex> [codeStart] [codeEnd]"); Environment.Exit(1); }
    UserMediaConfig wcfg = UserMediaConfig.Load(args[1]);
    ulong wcycles = ulong.TryParse(args[2], out var wc) ? wc : 5_000_000ul;
    uint target = Convert.ToUInt32(args[3], 16);
    uint wStart = args.Length > 4 ? Convert.ToUInt32(args[4], 16) : 0x00100000u;
    uint wEnd = args.Length > 5 ? Convert.ToUInt32(args[5], 16) : 0x00700000u;
    uint wMask = 0xFFFFFFFFu;
    foreach (var a in args)
        if (a.StartsWith("--mask=")) wMask = Convert.ToUInt32(a.Substring(7), 16);
    var wsys = new Ps2System();
    wsys.LoadBios(wcfg.BiosPath!);
    var wmsg = wsys.BootDiscFile(wcfg.Titles[0].Path);
    Console.WriteLine($"[{wcfg.Titles[0].Id}] {wmsg.Message}");
    wsys.RunFor(wcycles);
    Console.WriteLine($"after {wcycles} cyc: PC=0x{wsys.EE.PC:X8}; scanning 0x{wStart:X8}..0x{wEnd:X8} for word 0x{target:X8} mask=0x{wMask:X8}");
    int wfound = 0;
    for (uint addr = wStart; addr < wEnd; addr += 4)
    {
        uint word = wsys.Memory.Read32(addr);
        if ((word & wMask) != (target & wMask)) continue;
        Console.WriteLine($"  {addr:X8}: {word:X8}  {EeDisassembler.Disassemble(addr, word)}");
        wfound++;
    }
    Console.WriteLine($"scan complete: {wfound} match(es) found");
    Environment.Exit(0);
}

// detps2 scanmasked <media.json> <patternHex> <maskHex> <startHex> <lenHex> [titleIndex] — boot,
// scan a virtual-memory range for a 32-bit instruction word matching <pattern> under <mask> (0
// bits in the mask are "don't care") — e.g. finding "lui $any, 0x78" regardless of register.
if (args.Length > 0 && args[0].Equals("scanmasked", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(VersionInfo.Banner);
    if (args.Length < 6) { Console.WriteLine("usage: detps2 scanmasked <media.json> <patternHex> <maskHex> <startHex> <lenHex> [titleIndex]"); Environment.Exit(1); }
    UserMediaConfig mcfg = UserMediaConfig.Load(args[1]);
    uint mpattern = Convert.ToUInt32(args[2], 16);
    uint mmask = Convert.ToUInt32(args[3], 16);
    uint mstart = Convert.ToUInt32(args[4], 16);
    uint mlen = Convert.ToUInt32(args[5], 16);
    int mtitleIdx = args.Length > 6 && int.TryParse(args[6], out var mti) ? mti : 0;
    if (mtitleIdx >= mcfg.Titles.Count) { Console.WriteLine("No such title index"); Environment.Exit(1); }
    var mtitle = mcfg.Titles[mtitleIdx];
    var msys = new Ps2System();
    msys.LoadBios(mcfg.BiosPath!);
    var mmsg = msys.BootDiscFile(mtitle.Path);
    Console.WriteLine($"[{mtitle.Id}] {mmsg.Message}");
    int mhits = 0;
    for (uint addr = mstart; addr < mstart + mlen; addr += 4)
    {
        uint w = msys.Memory.Read32(addr);
        if ((w & mmask) == mpattern)
        {
            Console.WriteLine($"  0x{addr:X8}: {w:X8}  {EeDisassembler.Disassemble(addr, w)}");
            mhits++;
        }
    }
    Console.WriteLine($"total matches: {mhits}");
    Environment.Exit(0);
}

if (args.Length > 0 && args[0].Equals("scanword", StringComparison.OrdinalIgnoreCase))
{
    // Finds every occurrence of a raw 32-bit word (e.g. a specific JAL encoding, to
    // locate all callers of a given function) across a title's loaded code+data range.
    Console.WriteLine(VersionInfo.Banner);
    if (args.Length < 5) { Console.WriteLine("usage: detps2 scanword <media.json> <word_hex> <start_hex> <len_hex> [titleIndex]"); Environment.Exit(1); }
    UserMediaConfig scfg = UserMediaConfig.Load(args[1]);
    uint word = Convert.ToUInt32(args[2], 16);
    uint sstart = Convert.ToUInt32(args[3], 16);
    uint slen = Convert.ToUInt32(args[4], 16);
    int stitleIdx = args.Length > 5 && int.TryParse(args[5], out var sti) ? sti : 0;
    if (stitleIdx >= scfg.Titles.Count) { Console.WriteLine("No such title index"); Environment.Exit(1); }
    var stitle = scfg.Titles[stitleIdx];
    var ssys = new Ps2System();
    ssys.LoadBios(scfg.BiosPath!);
    var smsg = ssys.BootDiscFile(stitle.Path);
    Console.WriteLine($"[{stitle.Id}] {smsg.Message}");
    int hits = 0;
    for (uint addr = sstart; addr < sstart + slen; addr += 4)
    {
        if (ssys.Memory.Read32(addr) == word)
        {
            Console.WriteLine($"  0x{addr:X8}");
            hits++;
        }
    }
    Console.WriteLine($"total matches: {hits}");
    Environment.Exit(0);
}

// detps2 disasm <media.json> <cycles> <addr>:<len> [titleIndex] — boot, run N cycles, then
// disassemble a raw address range with EeDisassembler. Standalone tool for reading real-boot
// code without going through blocker-trace's fuller (and slower) telemetry/tracer machinery.
if (args.Length > 0 && args[0].Equals("disasm", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(VersionInfo.Banner);
    if (args.Length < 4) { Console.WriteLine("usage: detps2 disasm <media.json> <cycles> <addr>:<len> [titleIndex]"); Environment.Exit(1); }
    UserMediaConfig cfg = UserMediaConfig.Load(args[1]);
    ulong dcycles = ulong.TryParse(args[2], out var dc) ? dc : 5_000_000ul;
    var drange = args[3].Split(':');
    uint dstart = Convert.ToUInt32(drange[0], 16);
    uint dlen = drange.Length > 1 ? Convert.ToUInt32(drange[1], 16) : 0x100u;
    int titleIdx = args.Length > 4 && int.TryParse(args[4], out var ti) ? ti : 0;
    if (titleIdx >= cfg.Titles.Count) { Console.WriteLine("No such title index"); Environment.Exit(1); }
    var dtitle = cfg.Titles[titleIdx];
    var dsys = new Ps2System();
    dsys.LoadBios(cfg.BiosPath!);
    var dmsg = dsys.BootDiscFile(dtitle.Path);
    Console.WriteLine($"[{dtitle.Id}] {dmsg.Message}");
    dsys.RunFor(dcycles);
    Console.WriteLine($"after {dcycles} cyc: PC=0x{dsys.EE.PC:X8}");
    Console.WriteLine($"disasm 0x{dstart:X8}..0x{dstart + dlen:X8}:");
    for (uint addr = dstart; addr < dstart + dlen; addr += 4)
    {
        uint word = dsys.Memory.Read32(addr);
        string marker = addr == (uint)dsys.EE.PC ? " <== PC" : "";
        Console.WriteLine($"  {addr:X8}: {word:X8}  {EeDisassembler.Disassemble(addr, word)}{marker}");
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

// detps2 extract-file <iso> <isoPathOrSubstring> <outPath> — pull a raw file off the disc image
// so it can be examined directly (e.g. a real IOP .IRX module) rather than only through the
// running emulator's memory.
if (args.Length > 0 && args[0].Equals("extract-file", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 4) { Console.WriteLine("usage: detps2 extract-file <iso> <isoPathOrSubstring> <outPath>"); Environment.Exit(1); }
    var evol = Iso9660.OpenFile(args[1]);
    if (evol == null) { Console.WriteLine("bad iso"); Environment.Exit(2); }
    var match = evol.Files.FirstOrDefault(f => !f.IsDirectory && f.Path.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    if (match == null) { Console.WriteLine($"no file matching '{args[2]}'"); Environment.Exit(1); }
    byte[]? data = Iso9660.ReadFile(evol, match.Path);
    if (data == null) { Console.WriteLine($"failed to read {match.Path}"); Environment.Exit(1); }
    Directory.CreateDirectory(Path.GetDirectoryName(args[3])!);
    File.WriteAllBytes(args[3], data);
    Console.WriteLine($"extracted {match.Path} ({data.Length} bytes) -> {args[3]}");
    Environment.Exit(0);
}

// detps2 elf-sections <filePath> — dump section headers of any ELF/IRX file (not just the
// game's boot ELF via user-media.json) so extracted IOP modules can be inspected directly.
if (args.Length > 0 && args[0].Equals("elf-sections", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 2) { Console.WriteLine("usage: detps2 elf-sections <filePath>"); Environment.Exit(1); }
    byte[] elf = File.ReadAllBytes(args[1]);
    ushort etype = BitConverter.ToUInt16(elf, 0x10);
    ushort emachine = BitConverter.ToUInt16(elf, 0x12);
    uint eentry = BitConverter.ToUInt32(elf, 0x18);
    uint eshoff = BitConverter.ToUInt32(elf, 0x20);
    ushort eshentsize = BitConverter.ToUInt16(elf, 0x2E);
    ushort eshnum = BitConverter.ToUInt16(elf, 0x30);
    ushort eshstrndx = BitConverter.ToUInt16(elf, 0x32);
    Console.WriteLine($"e_type=0x{etype:X4} e_machine={emachine} e_entry=0x{eentry:X8} shoff=0x{eshoff:X} shnum={eshnum} shstrndx={eshstrndx}");
    if (eshoff == 0 || eshnum == 0) { Console.WriteLine("no section headers"); Environment.Exit(0); }
    uint strTabOff = BitConverter.ToUInt32(elf, (int)(eshoff + eshstrndx * eshentsize + 16));
    for (int i = 0; i < eshnum; i++)
    {
        int off = (int)eshoff + i * eshentsize;
        uint nameOff = BitConverter.ToUInt32(elf, off);
        uint type = BitConverter.ToUInt32(elf, off + 4);
        uint addr = BitConverter.ToUInt32(elf, off + 12);
        uint secOffset = BitConverter.ToUInt32(elf, off + 16);
        uint size = BitConverter.ToUInt32(elf, off + 20);
        int nameStart = (int)(strTabOff + nameOff);
        int nameEnd = nameStart;
        while (nameEnd < elf.Length && elf[nameEnd] != 0) nameEnd++;
        string name = nameStart < elf.Length ? System.Text.Encoding.ASCII.GetString(elf, nameStart, nameEnd - nameStart) : "?";
        Console.WriteLine($"  [{i}] {name,-16} type={type} addr=0x{addr:X8} fileOff=0x{secOffset:X6} size=0x{size:X6}");
    }
    Environment.Exit(0);
}

// detps2 iop-disasm <filePath> <fileOffsetHex>:<lenHex> — disassemble raw bytes from any file
// as R3000A/IOP code (see IopDisassembler.cs). Operates on raw file offsets, not a running
// system's memory — used to read real IOP module (.IRX) code extracted from the disc.
if (args.Length > 0 && args[0].Equals("iop-disasm", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3) { Console.WriteLine("usage: detps2 iop-disasm <filePath> <fileOffsetHex>:<lenHex>"); Environment.Exit(1); }
    byte[] ibytes = File.ReadAllBytes(args[1]);
    var irange = args[2].Split(':');
    uint istart = Convert.ToUInt32(irange[0], 16);
    uint ilen = irange.Length > 1 ? Convert.ToUInt32(irange[1], 16) : 0x100u;
    for (uint off = istart; off < istart + ilen && off + 3 < ibytes.Length; off += 4)
    {
        uint word = BitConverter.ToUInt32(ibytes, (int)off);
        Console.WriteLine($"  {off:X6}: {word:X8}  {IopDisassembler.Disassemble(off, word)}");
    }
    Environment.Exit(0);
}

// detps2 iop-find-word <filePath> <wordHex> [start] [end] — scan raw file bytes for an exact
// 32-bit word match (e.g. a specific "jal TARGET" encoding), file-offset based.
if (args.Length > 0 && args[0].Equals("iop-find-word", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3) { Console.WriteLine("usage: detps2 iop-find-word <filePath> <wordHex> [start] [end]"); Environment.Exit(1); }
    byte[] fbytes = File.ReadAllBytes(args[1]);
    uint ftarget = Convert.ToUInt32(args[2], 16);
    uint fstart = args.Length > 3 ? Convert.ToUInt32(args[3], 16) : 0u;
    uint fend = args.Length > 4 ? Convert.ToUInt32(args[4], 16) : (uint)fbytes.Length;
    int ffound = 0;
    for (uint off = fstart; off + 3 < fend && off + 3 < fbytes.Length; off += 4)
    {
        if (BitConverter.ToUInt32(fbytes, (int)off) != ftarget) continue;
        Console.WriteLine($"  {off:X6}: {ftarget:X8}  {IopDisassembler.Disassemble(off, ftarget)}");
        ffound++;
    }
    Console.WriteLine($"scan complete: {ffound} match(es)");
    Environment.Exit(0);
}

// detps2 probe-frame — boot MK and write framebuffer PPM + syscall hist. Unlike the other
// probe-* commands (removed as disposable one-off diagnostics — see §10 of DEVELOPER_GUIDE.md),
// this one is a documented, actively-used stable tool: quick visual sanity check of the boot-logo
// FMV path without needing user-media.json set up.
if (args.Length > 0 && args[0].Equals("probe-frame", StringComparison.OrdinalIgnoreCase))
{
    string[] posArgs = args.Skip(1).Where(a => !a.StartsWith("--")).ToArray();
    string bios = posArgs.Length > 0 ? posArgs[0] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PCSX2", "bios", "Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin");
    string iso = posArgs.Length > 1 ? posArgs[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "Mortal Kombat - Shaolin Monks (USA).iso");
    foreach (var a in args)
        if (a.StartsWith("--watch=")) SystemMemory.WatchAddr = Convert.ToUInt32(a.Substring(8), 16);
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
    // Continue until logo finishes (or timeout). FMV only advances on host-present ticks
    // (see MidwayBootAssist.OnHostPresent's own doc comment) — this loop previously only
    // called RunFor, which never drives that path at all (a test-tool gap, not a product bug;
    // matches MainWindow.axaml.cs's real per-tick RunFor+OnHostPresent+PresentFrame pattern).
    int postLogoGrace = 400;
    for (int i = 0; i < 800; i++)
    {
        p.RunFor(1_000_000);
        p.ActiveQuirk?.OnHostPresent(p);
        // Test whether the post-logo black screen is a real "press start" wait: tap Start
        // every ~10 frames once we're past the logo (a single held press could be missed by
        // edge-triggered input handling; a real controller also releases between presses).
        // Was gated on "post-logo-main" only -- with real code now reaching this point on its
        // own (see docs/DEVELOPER_GUIDE.md's LWL/JR-guard fixes), MaybePostLogoAdvance's forced
        // jump (the only thing that ever set Status to "post-logo-main") often never fires at
        // all anymore, leaving Status parked at "logo-done" indefinitely even though real
        // execution has genuinely moved past the logo -- widened to match so this tool's own
        // "try pressing Start" heuristic still runs instead of silently going dead.
        if (p.MidwayAssist.Status is "post-logo-main" or "logo-done" && i % 10 < 2)
            p.Pad.Press(PadInput.Button.Start);
        else
            p.Pad.Release(PadInput.Button.Start);
        Console.WriteLine($"  +{i + 1}M PC=0x{p.EE.PC:X8} px={p.Gs.PixelsWritten} prims={p.Gs.PrimitivesDrawn} " +
                          $"gifP3={p.Gif.Path3Transfers} cdvd={p.Cdvd.SectorsRead} " +
                          $"assist={p.MidwayAssist.Status} logo={p.MidwayAssist.LogoFrame}/{p.MidwayAssist.LogoFramesTotal} pres={p.MidwayAssist.FramesPresented}");
        if (p.MidwayAssist.Status is "post-logo-main" or "logo-done" && i % 50 == 0)
            p.Gs.SaveFramebufferAsPPM(outPpm.Replace(".ppm", $"-post{i}.ppm"));
        if (p.MidwayAssist.Status is "logo-done" or "synthetic-logo" or "post-logo-main")
        {
            if (postLogoGrace-- <= 0) break;
        }
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
    if (SystemMemory.WatchAddr.HasValue)
    {
        Console.WriteLine($"  watch 0x{SystemMemory.WatchAddr.Value:X8}: {SystemMemory.WatchHits.Count} access(es)");
        foreach (var (wpc, wvaddr, wval, isWrite) in SystemMemory.WatchHits)
        {
            string kind = isWrite ? $"WROTE 0x{wval:X8}" : "READ ";
            Console.WriteLine($"    pc=0x{wpc:X8} {kind} 0x{wvaddr:X8}  {EeDisassembler.Disassemble((uint)wpc, p.Memory.Read32((uint)wpc))}");
        }
    }
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
