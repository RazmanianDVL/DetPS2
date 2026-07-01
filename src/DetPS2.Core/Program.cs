using System;
using DetPS2.Core;

Console.WriteLine("=== DetPS2Sharp — Phase 3 Progress ===");

Console.WriteLine("\n[Phase 1] Complete");
Console.WriteLine("[Phase 2] Complete — Full graphics pipeline");
Console.WriteLine("[Phase 3] In Progress — IOP now has basic R3000A interpreter + SIF + HLE syscalls");


var system = new Ps2System();
system.RunFor(500);

system.TriggerTestDraw();

Console.WriteLine("\n=== Continuing Phase 3 development ===");
