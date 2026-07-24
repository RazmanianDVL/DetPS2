using System;
using DetPS2.Core;

var bios = @"C:\Users\xxraz\Documents\PCSX2\bios\Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin";
var iso = @"C:\Users\xxraz\Downloads\Mortal Kombat - Shaolin Monks (USA).iso";
var sys = new Ps2System();
sys.LoadBios(bios);
sys.BootDiscFile(iso);

// Trace first 200k instructions' unique PC ranges and any DMAC/GIF touches via sampling after short runs
ulong[] samples = new ulong[64];
int si = 0;
for (int i = 0; i < 400; i++)
{
    sys.RunFor(500);
    if (si < samples.Length) samples[si++] = sys.EE.PC;
}
Console.WriteLine($"PC after 200k: 0x{sys.EE.PC:X8} sys={sys.Hle.SyscallCount}");
// Disasm around current
uint pc = (uint)(sys.EE.PC & 0x1FFFFFFF);
Console.WriteLine($"regs: v0={sys.EE.GetGpr(2).Lo:X} v1={sys.EE.GetGpr(3).Lo:X} a0={sys.EE.GetGpr(4).Lo:X} t0={sys.EE.GetGpr(8).Lo:X} s0={sys.EE.GetGpr(16).Lo:X} s1={sys.EE.GetGpr(17).Lo:X}");
// Check D_CTRL and GIF CHCR
uint dctrl = sys.Memory.Read32(0x1000E000);
uint gifChcr = sys.Memory.Read32(0x1000A000);
uint vif1Chcr = sys.Memory.Read32(0x10009000);
Console.WriteLine($"D_CTRL=0x{dctrl:X} GIF_CHCR=0x{gifChcr:X} VIF1_CHCR=0x{vif1Chcr:X}");
// Walk from entry looking for first JAL to graph-ish
Console.WriteLine("sample PCs:");
foreach (var p in samples) Console.Write($" {p:X8}");
Console.WriteLine();
