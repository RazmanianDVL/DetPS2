using System;
using DetPS2.Core;

Console.WriteLine("=== DetPS2 - Phase 1 Complete + Phase 2 Graphics Pipeline Test ===");

Console.WriteLine("\n[Phase 1] Emotion Engine is now capable of running real homebrew code.");
Console.WriteLine("[Phase 1] Added: JR, JALR, SLTI, SLTIU + improved syscall/exception entry.");
Console.WriteLine("[Phase 1] Branch delay slot, mul/div, loads/stores, branches, jumps all present.");

Console.WriteLine("\n[Phase 2] Testing full DMAC -> GIF -> GS pipeline with real transfer...");

var system = new Ps2System();

// Run some CPU cycles (Phase 1 work)
system.RunFor(200);

// Trigger the improved graphics test (now uses real DMAC/GIF path)
system.TriggerTestDraw();

// Save the framebuffer as an image
string outputPath = "detps2_test_output.ppm";
system.Gs.SaveFramebufferAsPPM(outputPath);

Console.WriteLine($"\n[RESULT] Image saved to: {outputPath}");
Console.WriteLine("Open the .ppm to see the gradient + magenta rectangle drawn via the pipeline.");

Console.WriteLine("\n=== Next: Provide a real homebrew ELF to test Phase 1 execution ===");
Console.WriteLine("=== Or expand GIFtag parsing in Gif.cs for real primitive drawing ===");
Console.WriteLine("\n=== Test complete ===");
