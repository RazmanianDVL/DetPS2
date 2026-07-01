using System;
using DetPS2.Core;

Console.WriteLine("=== DetPS2Sharp — Phase 2 Complete + Phase 3 Foundation ===");

Console.WriteLine("\n[Phase 1] Complete");
Console.WriteLine("[Phase 2] Complete — Full graphics pipeline (DMAC + VIF + GIF + GS + PCRTC)");
Console.WriteLine("[Phase 3] Foundation — Intc + Iop (with improved SIF) integrated and stepped");


var system = new Ps2System();

system.RunFor(200);

// Demonstrate both paths
system.TriggerTestDraw(useVif: false);  // Main DMAC path
// system.TriggerTestDraw(useVif: true); // Alternative Vif path

Console.WriteLine("\n=== Ready for deeper Phase 3 work ===");
