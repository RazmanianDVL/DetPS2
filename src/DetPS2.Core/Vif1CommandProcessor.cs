using System;

namespace DetPS2.Core;

/// <summary>
/// Vif1CommandProcessor - Foundational structure for processing VIF1 commands.
/// 
/// VIF1 receives commands (VifCodes) such as MSCAL (run microcode), MPG (load microprogram), etc.
/// 
/// This class provides the architectural foundation only.
/// Actual command parsing and execution logic should be implemented on top.
/// </summary>
public sealed class Vif1CommandProcessor
{
    private readonly Vif1 _vif1;
    private readonly Vu1 _vu1;

    public Vif1CommandProcessor(Vif1 vif1, Vu1 vu1)
    {
        _vif1 = vif1 ?? throw new ArgumentNullException(nameof(vif1));
        _vu1 = vu1 ?? throw new ArgumentNullException(nameof(vu1));
    }

    public void Reset() { }

    /// <summary>
    /// Process a VifCode command.
    /// Foundation method - real command handling goes here later.
    /// </summary>
    public void ProcessCommand(uint vifCode)
    {
        // TODO: Parse vifCode and execute appropriate action
        // Examples:
        // - MSCAL: Tell VU1 to run microcode from a specific address
        // - MPG: Load microprogram into VU1
        // - UNPACK: Trigger unpacking
    }
}
