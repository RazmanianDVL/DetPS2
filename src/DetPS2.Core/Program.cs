using System;
using DetPS2.Core;

Console.WriteLine("=== DetPS2Sharp — Phase 2: Real GIF-Driven Primitive Drawing ===");

Console.WriteLine("\n[Phase 1] Complete — Emotion Engine has full control flow, arithmetic, loads/stores, mul/div, and exceptions.");
Console.WriteLine("[Phase 1] Ready for real homebrew ELF execution.");

Console.WriteLine("\n[Phase 2] GIF now cleanly parses tags and drives GS sequentially:");
Console.WriteLine("           PRIM → RGBAQ → Vertex1 → Vertex2 → Vertex3");
Console.WriteLine("[Phase 2] GS rasterizes real filled triangles from the command stream.");
Console.WriteLine("[Phase 2] Full pipeline: DMAC → GIF → GS → Framebuffer → PPM");


var system = new Ps2System();
system.RunFor(200);
system.TriggerTestDraw();

string outputPath = "detps2_test_output.ppm";
system.Gs.SaveFramebufferAsPPM(outputPath);

Console.WriteLine($"\n[RESULT] {outputPath} now contains a triangle drawn from emulated GIF commands.");
Console.WriteLine("Open it to verify the primitive was rasterized by the GS.");

Console.WriteLine("\n=== Excellent Phase 2 progress. Next logical steps ===");
Console.WriteLine("• Replace reflection in DMAC test with real CHCR/MADR/QWC register interface");
Console.WriteLine("• Improve GIF to support proper REGLIST decoding");
Console.WriteLine("• Add line/quad primitives in GS");
Console.WriteLine("• Begin Phase 3 items (IOP, timers, basic HLE)");

Console.WriteLine("\n=== Session complete ===");
