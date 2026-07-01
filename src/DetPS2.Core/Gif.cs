using System;

namespace DetPS2.Core;

/// <summary>
/// Graphics Interface (GIF) - Phase 2/3
/// Parses GIFtags and drives the GS with drawing commands.
/// Still simplified but functional for Phase 2.
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
            uint tag0 = _gs.Memory?.Read32(currentAddr) ?? 0;

            uint nloop = tag0 & 0x7FFF;
            bool eop = (tag0 & (1 << 15)) != 0;
            uint format = (tag0 >> 26) & 0x3;

            Console.WriteLine($"[GIF] Tag: NLOOP={nloop}, EOP={eop}, Format={format}");

            currentAddr += 16;
            remaining--;

            if (format == 0 || format == 1) // PACKED or REGLIST (simplified handling)
            {
                for (uint i = 0; i < nloop && remaining > 0; i++)
                {
                    uint d0 = _gs.Memory?.Read32(currentAddr) ?? 0;

                    if (i == 0)
                    {
                        _gs.SetPrim(d0);
                    }
                    else if (i == 1)
                    {
                        _gs.SetRGBAQ(d0);
                    }
                    else
                    {
                        _gs.DrawVertex(d0);
                    }

                    currentAddr += 16;
                    remaining--;
                }
            }
            else
            {
                // IMAGE mode - skip
                currentAddr += nloop * 16;
                remaining -= nloop;
            }

            if (eop) break;
        }

        _gs.ReceiveCommandList(address, qwc);
    }

    public void Step(ulong cycles) { }
}
