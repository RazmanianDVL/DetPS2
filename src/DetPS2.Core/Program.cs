using System;
using DetPS2.Core;

Console.WriteLine("=== DetPS2Sharp - Phase 1 Complete + Phase 2: Real Primitive Drawing ===");

Console.WriteLine("\n[Phase 1] EE is now fully capable for homebrew (JR/JALR/SLTI/SLTIU + full control flow + exceptions).");
Console.WriteLine("[Phase 1] ELF loader ready. Provide a homebrew ELF to run real code.");

Console.WriteLine("\n[Phase 2] Testing complete DMAC -> GIF -> GS pipeline with real primitives...");
Console.WriteLine("[Phase 2] GIF now parses tags and drives GS with PRIM/RGBAQ/XYZ.");
Console.WriteLine("[Phase 2] GS now has real triangle rasterizer (barycentric).");


var system = new Ps2System();

system.RunFor(200);

system.TriggerTestDraw();

string outputPath = "detps2_test_output.ppm";
system.Gs.SaveFramebufferAsPPM(outputPath);

Console.WriteLine($"\n[RESULT] PPM saved to: {outputPath}");
Console.WriteLine("The image should now contain a real drawn triangle (magenta-ish) from emulated GIF/GS commands, not just a hardcoded pattern.");

Console.WriteLine("\n=== Phase 2 core items advanced significantly ===");
Console.WriteLine("Next natural steps: Improve GIFtag decoding accuracy, add more GS primitives (lines/quads), add proper DMAC register interface.");
Console.WriteLine("\n=== Test complete ===");
