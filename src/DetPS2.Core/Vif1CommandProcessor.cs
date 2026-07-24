using System;

namespace DetPS2.Core;

/// <summary>
/// VIF1 command processor — thin adapter over <see cref="Vif1"/> / <see cref="Vif"/>.
/// Phase 50: no empty TODOs; all codes execute on the production VIF path.
/// </summary>
public sealed class Vif1CommandProcessor
{
    private readonly Vif1 _vif1;

    public ulong Commands { get; private set; }

    public Vif1CommandProcessor(Vif1 vif1, Vu1 vu1)
    {
        _vif1 = vif1 ?? throw new ArgumentNullException(nameof(vif1));
        _ = vu1 ?? throw new ArgumentNullException(nameof(vu1));
    }

    public void Reset() => Commands = 0;

    public void ProcessCommand(uint vifCode)
    {
        _vif1.SendVifCode(vifCode);
        Commands++;
    }

    /// <summary>Process a memory stream as VIF FIFO (wordCount in 32-bit words).</summary>
    public void ProcessStream(uint address, uint wordCount)
    {
        uint qwc = (wordCount + 3) / 4;
        if (qwc == 0) return;
        _vif1.ProcessData(address, qwc);
        Commands++;
    }
}
