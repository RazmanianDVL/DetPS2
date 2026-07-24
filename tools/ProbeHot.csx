using System;
using DetPS2.Core;
var bios = @"C:\Users\xxraz\Documents\PCSX2\bios\Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin";
var iso = @"C:\Users\xxraz\Downloads\Mortal Kombat - Shaolin Monks (USA).iso";
var sys = new Ps2System();
sys.LoadBios(bios);
sys.BootDiscFile(iso);
// sample PCs denser for first 2M
var pcs = new System.Collections.Generic.Dictionary<ulong,int>();
for (int i=0;i<200;i++) {
  sys.RunFor(10000);
  ulong p = sys.EE.PC & 0x1FFFFFFFUL;
  pcs[p] = pcs.GetValueOrDefault(p)+1;
}
Console.WriteLine($"after 2M: PC=0x{sys.EE.PC:X8} sys={sys.Hle.SyscallCount}");
foreach (var kv in pcs.OrderByDescending(k=>k.Value).Take(15))
  Console.WriteLine($"  PC=0x{kv.Key:X8} n={kv.Value}");
// dump a few words around main loop hotspots
foreach (uint a in new uint[]{0x483068,0x4830F4,0x4834E4,0x485FE4,0x486014,0x20629C}) {
  Console.Write($"@{a:X8}:");
  for (int i=0;i<8;i++) Console.Write($" {sys.Memory.Read32(a+(uint)(i*4)):X8}");
  Console.WriteLine();
}
// GPRs of interest
Console.WriteLine($"v0={sys.EE.GetGpr(2).Lo:X} a0={sys.EE.GetGpr(4).Lo:X} s0={sys.EE.GetGpr(16).Lo:X} s1={sys.EE.GetGpr(17).Lo:X} gp={sys.EE.GetGpr(28).Lo:X} ra={sys.EE.GetGpr(31).Lo:X}");
