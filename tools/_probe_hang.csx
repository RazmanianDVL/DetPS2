using System;
using System.Linq;
using DetPS2.Core;
var bios = args[0]; var iso = args[1];
var p = new Ps2System();
p.LoadBios(bios);
p.BootDiscFile(iso);
// Run until hang region
for (int i=0;i<40;i++) {
  p.RunFor(2_000_000);
  uint pc = (uint)(p.EE.PC & 0x1FFFFFFF);
  if (pc >= 0x00166800 && pc < 0x00166A00) break;
}
uint pc0 = (uint)(p.EE.PC & 0x1FFFFFFF);
Console.WriteLine($"PC=0x{pc0:X8} cycles={p.MasterCycles} px={p.Gs.PixelsWritten} gif={p.Gif.Path3Transfers} dmac={p.Dmac.TransfersCompleted}");
Console.WriteLine($"sifDma={p.Hle.Sony?.SifDmaCalls} sifGet={p.Hle.Sony?.SifGetRegCalls} sifB={p.Sif.BytesTransferred} cdvd={p.Cdvd.SectorsRead}");
Console.WriteLine($"threads={p.Hle.Kernel.ThreadCount} tid={p.Hle.Kernel.CurrentThreadId}");
for (int t=1;t<=Math.Min(16,p.Hle.Kernel.ThreadCount+4);t++) {
  var th = p.Hle.Kernel.GetThread(t);
  if (th==null) continue;
  Console.WriteLine($"  t{t}: alive={th.Alive} sleep={th.Sleeping} waitSema={th.WaitSemaId} entry=0x{th.Entry:X8} pc=0x{th.SavedPc:X8} sp=0x{th.SavedSp:X8}");
}
Console.WriteLine("code @ hang:");
for (uint a=0x001668C0; a<0x00166980; a+=4)
  Console.WriteLine($"  {a:X8}: {p.Memory.Read32(a):X8}");
// GPRs
Console.WriteLine($"v0={p.EE.GetGpr(2).Lo:X} a0={p.EE.GetGpr(4).Lo:X} a1={p.EE.GetGpr(5).Lo:X} s0={p.EE.GetGpr(16).Lo:X} s1={p.EE.GetGpr(17).Lo:X} ra={p.EE.GetGpr(31).Lo:X}");
// Key globals
foreach (uint a in new uint[]{0x77A080,0x563FE4,0x56409C,0x5C9C00,0x4860C0,0x480330})
  Console.WriteLine($"mem[{a:X}]=0x{p.Memory.Read32(a):X8}");
// syscall hist
if (p.Hle.Sony!=null)
  foreach (var kv in p.Hle.Sony.SyscallHistogram.OrderByDescending(k=>k.Value).Take(20))
    Console.WriteLine($"  sc 0x{kv.Key:X2} x{kv.Value}");
// Recent syscalls
Console.Write("recent:");
foreach (var n in p.Hle.Sony!.RecentSyscalls) Console.Write($" {n:X2}");
Console.WriteLine();
// Step and watch who runs
var hist = new Dictionary<uint,int>();
for (int i=0;i<5000;i++) {
  p.RunFor(50);
  uint b = (uint)(p.EE.PC & 0x1FFFFF00);
  hist[b]=hist.GetValueOrDefault(b)+1;
}
Console.WriteLine("PC buckets:");
foreach (var kv in hist.OrderByDescending(k=>k.Value).Take(15))
  Console.WriteLine($"  0x{kv.Key:X8} x{kv.Value}");
