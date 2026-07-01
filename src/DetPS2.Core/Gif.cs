using System;

namespace DetPS2.Core;

/// <summary>
/// Graphics Interface (GIF) - Phase 2
/// Parses GIFtags (especially PATH3) and drives the GS with real primitive data.
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
        Console.WriteLine($"[GIF] PATH3 transfer received: {qwc} quadwords at 0x{address:X}");

        uint currentAddr = address;
        uint remainingQwc = qwc;

        while (remainingQwc > 0)
        {
            // Read GIFtag (first 128 bits)
            uint nloopRaw = _gs.Memory?.Read32(currentAddr) ?? 0;
            uint primRaw  = _gs.Memory?.Read32(currentAddr + 4) ?? 0;

            uint nloop = nloopRaw & 0x7FFF;
            bool eop = (nloopRaw & (1 << 15)) != 0;
            uint format = (nloopRaw >> 26) & 0x3; // 0=PACKED, 1=REGLIST, 2=IMAGE

            Console.WriteLine($"[GIF] Tag parsed: NLOOP={nloop}, EOP={eop}, Format={format}");

            currentAddr += 16;
            remainingQwc--;

            if (format == 0) // PACKED - most common for simple drawing
            {
                // Process NLOOP * 1 quadword of data (simplified for Phase 2)
                for (uint i = 0; i < nloop && remainingQwc > 0; i++)
                {
                    uint data0 = _gs.Memory?.Read32(currentAddr) ?? 0;
                    uint data1 = _gs.Memory?.Read32(currentAddr + 4) ?? 0;
                    uint data2 = _gs.Memory?.Read32(currentAddr + 8) ?? 0;
                    uint data3 = _gs.Memory?.Read32(currentAddr + 12) ?? 0;

                    // Very rough command detection for test (real impl will decode REGLIST properly)
                    // Assume first word after tag can be PRIM or RGBAQ or XYZ
                    if ((data0 & 0xFF) == 0x00) // rough PRIM marker for test
                    {
                        _gs.SetPrim(data0);
                    }
                    else if ((data0 & 0xFF000000) != 0) // rough RGBAQ
                    {
                        _gs.SetRGBAQ(data0);
                    }
                    else
                    {
                        // Treat as XYZ vertex
                        _gs.DrawVertex(data0);
                    }

                    currentAddr += 16;
                    remainingQwc--;
                }
            }
            else
            {
                // Skip other formats for Phase 2
                currentAddr += nloop * 16;
                remainingQwc -= nloop;
            }

            if (eop)
                break;
        }

        // Forward to GS (it will draw based on state we set above)
        _gs.ReceiveCommandList(address, qwc);
    }

    public void Step(ulong cycles) { }
}
