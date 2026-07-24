using System;

namespace DetPS2.Core;

/// <summary>
/// VIF1 façade over the production <see cref="Vif"/> backend (Phase 50 integrity).
/// No TODO stubs: codes and DMA streams use <see cref="Vif.ProcessStream"/> / <see cref="Vif.ProcessVifCode"/>.
/// </summary>
public sealed class Vif1
{
    private readonly SystemMemory _memory;
    private readonly Vif _vif;
    private readonly Vu1 _vu1;

    public Vif Backend => _vif;
    public ulong CodesSent { get; private set; }
    public ulong WordsProcessed { get; private set; }

    public Vif1(Vif vif, Vu1 vu1, SystemMemory memory)
    {
        _vif = vif ?? throw new ArgumentNullException(nameof(vif));
        _vu1 = vu1 ?? throw new ArgumentNullException(nameof(vu1));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _vif.SetVu1(_vu1);
    }

    public Vif1(SystemMemory memory, Vu1 vu1)
        : this(new Vif(memory), vu1, memory)
    {
    }

    public void Reset()
    {
        _vif.Reset();
        CodesSent = WordsProcessed = 0;
    }

    /// <summary>Process <paramref name="qwc"/> quadwords from EE memory as a VIF stream.</summary>
    public void ProcessData(uint address, uint qwc)
    {
        uint words = qwc * 4;
        _vif.ProcessStream(address, words);
        WordsProcessed += words;
    }

    /// <summary>Send one VIF code through the production command processor.</summary>
    public void SendVifCode(uint vifCode)
    {
        _vif.ProcessVifCode(vifCode);
        CodesSent++;
    }

    public void FeedData(uint word)
    {
        _vif.FeedData(word);
        WordsProcessed++;
    }

    public int Step(ulong cycles) => _vif.Step(cycles);
}
