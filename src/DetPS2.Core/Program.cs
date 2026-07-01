using System;
using DetPS2.Core;

Console.WriteLine("=== DetPS2 Phase 2 Test ===");

var system = new Ps2System();

// Run some CPU cycles
system.RunFor(100);

// Trigger a test draw through the graphics pipeline
system.TriggerTestDraw();

// Check if we have pixels in the framebuffer
var fb = system.Gs.GetFramebuffer();
bool hasPixels = false;
foreach (var pixel in fb)
{
    if (pixel != 0)
    {
        hasPixels = true;
        break;
    }
}

Console.WriteLine(hasPixels 
    ? "[SUCCESS] Framebuffer contains pixels! Phase 2 pipeline is producing output."
    : "[INFO] No pixels yet.");

Console.WriteLine($"Framebuffer size: {system.Gs.FramebufferWidth}x{system.Gs.FramebufferHeight}");
Console.WriteLine("\n=== Phase 2 test complete ===");
