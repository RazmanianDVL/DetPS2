using System;
using DetPS2.Core;

Console.WriteLine("=== DetPS2Sharp — Deterministic PS2 Emulator (C#) ===");
Console.WriteLine("Starting minimal Emotion Engine test...\n");

var system = new Ps2System();

// For a very basic test, let's manually put some code into RDRAM
// and point the EE at it. This is the equivalent of the "hello world" stage.

// Simple test program (MIPS assembly equivalent):
// lui   $t0, 0x1234
// ori   $t0, $t0, 0x5678
// sw    $t0, 0($zero)
// lui   $t1, 0xDEAD
// ori   $t1, $t1, 0xBEEF
// or    $t2, $t0, $t1
// infinite loop: j loop (but we'll just execute a fixed number of steps)

byte[] testCode = new byte[]
{
    // lui $t0, 0x1234     (0x3C08 1234)
    0x3C, 0x08, 0x12, 0x34,
    // ori $t0, $t0, 0x5678 (0x3508 5678)
    0x35, 0x08, 0x56, 0x78,
    // sw  $t0, 0($zero)    (0xAC08 0000)  — store to address 0
    0xAC, 0x08, 0x00, 0x00,
    // lui $t1, 0xDEAD
    0x3C, 0x09, 0xDE, 0xAD,
    // ori $t1, $t1, 0xBEEF
    0x35, 0x29, 0xBE, 0xEF,
    // or  $t2, $t0, $t1    (0x0109 5025)  SPECIAL, rs=8, rt=9, rd=10, func=0x25
    0x01, 0x09, 0x50, 0x25,
    // Simple infinite loop using j (but for test we'll just step a fixed number of times)
    // For now we just let it run into unknown opcodes which are handled gracefully.
};

system.Memory.LoadBinary(testCode, 0x100000); // Load at a convenient RDRAM address
system.EE.PC = 0x100000;                       // Start executing there

Console.WriteLine("Initial registers:");
system.EE.DumpRegisters();

Console.WriteLine("\n--- Executing 10 steps ---");
for (int i = 0; i < 10; i++)
{
    system.RunFor(1);
    Console.WriteLine($"After step {i + 1} (MasterCycles={system.MasterCycles}):");
    system.EE.DumpRegisters();
    Console.WriteLine();
}

Console.WriteLine("Memory at address 0 (should contain 0x12345678 if SW worked):");
uint memVal = system.Memory.Read32(0);
Console.WriteLine($"0x00000000 = 0x{memVal:X8}");

Console.WriteLine("\n=== Test complete. This is the absolute beginning. ===");
Console.WriteLine("Next milestones: proper ELF loader, more instructions, DMAC, GIF, software GS renderer.");
