using System;

namespace DetPS2.Core;

/// <summary>
/// Graphics Interface (GIF) - Improved GIFtag parsing.
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
        Console.WriteLine($"[GIF] PATH3 transfer: {qwc} quadwords");

        uint currentQwc = qwc;
        uint currentAddr = address;

        while (currentQwc > 0)
        {
            // Read GIFtag (first 128 bits / 16 bytes)
            uint nloop = _gs.Memory?.Read32(currentAddr) ?? 0;
            uint prim = _gs.Memory?.Read32(currentAddr + 4) ?? 0;

            uint nloopCount = nloop & 0x7FFF;
            bool eop = (nloop & (1 << 15)) != 0;
            uint primValue = prim & 0x7FF; // PRIM register value
            uint format = (nloop >> 26) & 0x3; // 0=PACKED, 1=REGLIST, 2=IMAGE

            Console.WriteLine($"[GIF] Tag: NLOOP={nloopCount}, EOP={eop}, Format={format}, PRIM=0x{primValue:X}");

            // Skip the tag itself
            currentAddr += 16;
            currentQwc--;

            // Very rough: skip the data for now (we'll parse it properly later)
            uint dataQwc = nloopCount; // rough estimate
            currentAddr += dataQwc * 16;
            currentQwc -= dataQwc;

            if (eop)
                break;

            if (currentQwc == 0)
                break;
        }

        // Forward whatever we have to GS
        _gs.ReceiveCommandList(address, qwc);
    }

    public void Step(ulong cycles) { }
}
