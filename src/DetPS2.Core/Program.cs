using System;
using DetPS2.Core;

Console.WriteLine("=== DetPS2 Phase 2 - Pixels on Screen Test ===");

var system = new Ps2System();

// Run some CPU cycles
system.RunFor(200);

// Trigger graphics pipeline
system.TriggerTestDraw();

// Save the framebuffer as an image
string outputPath = "detps2_test_output.ppm";
system.Gs.SaveFramebufferAsPPM(outputPath);

Console.WriteLine($"\n[RESULT] Image saved to: {outputPath}");
Console.WriteLine("You can open this .ppm file with most image viewers or convert it.");
Console.WriteLine("\n=== Phase 2 test complete ===");
