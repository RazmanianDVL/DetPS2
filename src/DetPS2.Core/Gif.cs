using System;

namespace DetPS2.Core;

/// <summary>
/// Graphics Interface (GIF) - GS Lane improvements
/// 
/// This version improves GIFtag parsing:
/// - Properly respects NLOOP
/// - Handles EOP flag
/// - Better structure for future PACKED / REGLIST / IMAGE modes
/// - Still calls into Gs for register writes and primitive assembly
/// 
/// Real PS2 GIF packets are complex (multiple formats, loop modes, etc).
/// This is a solid incremental step while keeping things working.
/// </summary>
public sealed class Gif
{
    private readonly Gs _gs;

    public Gif(Gs gs)
    {
        _gs = gs ?? throw new ArgumentNullException(nameof(gs));
    }

    public void Reset() { }

    public void ReceivePath3Data(uint address, uint qwc)
    {
        Console.WriteLine($"[GIF] PATH3 transfer: {qwc} quadwords @ 0x{address:X8}");

        uint currentAddr = address;
        uint remaining = qwc;

        while (remaining > 0)
        {
            // Read GIFtag (first 64 bits of the 128-bit tag)
            uint tagLow  = _gs.Memory?.Read32(currentAddr) ?? 0;
            uint tagHigh = _gs.Memory?.Read32(currentAddr + 4) ?? 0;

            uint nloop = tagLow & 0x7FFF;
            bool eop   = (tagLow & (1 << 15)) != 0;
            uint format = (tagLow >> 26) & 0x3;           // 0=PACKED, 1=REGLIST, 2=IMAGE, 3=IMAGE
            bool pre   = (tagLow & (1 << 18)) != 0;       // PRE flag (use PRIM in tag)
            uint prim  = (tagLow >> 19) & 0x7FF;          // PRIM field when PRE=1

            Console.WriteLine($"[GIF] Tag: NLOOP={nloop}, EOP={eop}, Format={format}, PRE={pre}");

            currentAddr += 16;   // advance past the tag itself
            remaining--;

            if (format == 0) // PACKED mode (most common for drawing)
            {
                for (uint i = 0; i < nloop && remaining > 0; i++)
                {
                    uint regDataLow  = _gs.Memory?.Read32(currentAddr) ?? 0;
                    uint regDataHigh = _gs.Memory?.Read32(currentAddr + 4) ?? 0;

                    // In PACKED mode, each 128-bit word is usually:
                    //   lower 64 bits = data for one register
                    //   upper 64 bits contain register address + control
                    // For simplicity we pass the whole 128-bit as two values to GS
                    _gs.ProcessGifPackedWord(regDataLow, regDataHigh);

                    currentAddr += 16;
                    remaining--;
                }
            }
            else if (format == 1) // REGLIST (simplified)
            {
                for (uint i = 0; i < nloop && remaining > 0; i++)
                {
                    uint d0 = _gs.Memory?.Read32(currentAddr) ?? 0;
                    // Very basic handling - treat as vertex data for now
                    _gs.DrawVertex(d0);
                    currentAddr += 16;
                    remaining--;
                }
            }
            else
            {
                // IMAGE mode or unknown - skip the data
                uint skip = Math.Min(nloop, remaining);
                currentAddr += skip * 16;
                remaining -= skip;
            }

            if (eop)
            {
                Console.WriteLine("[GIF] EOP encountered");
                break;
            }
        }

        // Final call so GS can finish any pending primitive assembly
        _gs.ReceiveCommandList(address, qwc);
    }

    public void Step(ulong cycles) { }
}