using System;

namespace DetPS2.Core;

/// <summary>
/// Graphics Interface (GIF) - GS Lane improvements
/// 
/// Work-Cost Prototype integration: GIF now participates in deterministic timing.
/// Step() returns meaningful cycle consumption instead of 0.
/// Full cost model lives in Gs.CalculateWorkCost (called from here in future refinements).
/// </summary>
public sealed class Gif
{
    private readonly Gs _gs;

    // Tracks last transfer size for cost calculation (deterministic)
    private uint _lastQwcProcessed;

    public Gif(Gs gs)
    {
        _gs = gs ?? throw new ArgumentNullException(nameof(gs));
    }

    public void Reset() 
    { 
        _lastQwcProcessed = 0; 
    }

    public void ReceivePath3Data(uint address, uint qwc)
    {
        Console.WriteLine($"[GIF] PATH3 transfer: {qwc} quadwords @ 0x{address:X8}");

        _lastQwcProcessed = qwc; // for deterministic cost reporting

        uint currentAddr = address;
        uint remaining = qwc;

        while (remaining > 0)
        {
            uint tagLow  = _gs.Memory?.Read32(currentAddr) ?? 0;
            uint tagHigh = _gs.Memory?.Read32(currentAddr + 4) ?? 0;

            uint nloop = tagLow & 0x7FFF;
            bool eop   = (tagLow & (1 << 15)) != 0;
            uint format = (tagLow >> 26) & 0x3;
            bool pre   = (tagLow & (1 << 18)) != 0;
            uint prim  = (tagLow >> 19) & 0x7FF;

            Console.WriteLine($"[GIF] Tag: NLOOP={nloop}, EOP={eop}, Format={format}, PRE={pre}");

            currentAddr += 16;
            remaining--;

            if (format == 0)
            {
                for (uint i = 0; i < nloop && remaining > 0; i++)
                {
                    uint regDataLow  = _gs.Memory?.Read32(currentAddr) ?? 0;
                    uint regDataHigh = _gs.Memory?.Read32(currentAddr + 4) ?? 0;
                    _gs.ProcessGifPackedWord(regDataLow, regDataHigh);
                    currentAddr += 16;
                    remaining--;
                }
            }
            else if (format == 1)
            {
                for (uint i = 0; i < nloop && remaining > 0; i++)
                {
                    uint d0 = _gs.Memory?.Read32(currentAddr) ?? 0;
                    _gs.DrawVertex(d0);
                    currentAddr += 16;
                    remaining--;
                }
            }
            else
            {
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

        _gs.ReceiveCommandList(address, qwc);
    }

    /// <summary>
    /// ISchedulable implementation for GIF.
    /// Returns deterministic cycle cost based on last transfer.
    /// In full implementation this will call _gs.CalculateWorkCost(_lastQwcProcessed, nreg).
    /// </summary>
    public int Step(ulong maxCycles)
    {
        if (_lastQwcProcessed == 0)
            return 1; // minimal progress when idle

        // Use Gs work-cost model when possible (integer only, deterministic)
        int cost = _gs.CalculateWorkCost(_lastQwcProcessed, 4); // assume average 4 registers per tag for now
        _lastQwcProcessed = 0; // reset after reporting

        return Math.Min(cost, (int)maxCycles);
    }
}