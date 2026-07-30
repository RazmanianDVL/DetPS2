using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Serial → module factory lookup for <see cref="IGameQuirkModule"/>.
///
/// Contributing a new title's quirks: add a file under GameQuirks/, implement
/// <see cref="IGameQuirkModule"/>, and register a factory for it in
/// <see cref="Register"/> below — one line, one PR, no other file needs to change.
/// <see cref="DiscBoot.BootFromDisc"/> resolves the mounted disc's serial and calls
/// <see cref="Resolve"/> automatically; nothing else needs to know your title exists.
///
/// A fresh instance is created per boot (via the factory) so module-local mutable state
/// (e.g. "have I already planted the worklist") never leaks between runs or Ps2System
/// instances — do not use a shared/static instance for a module's own state.
/// </summary>
public static class GameQuirkRegistry
{
    private static readonly Dictionary<string, Func<IGameQuirkModule>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    static GameQuirkRegistry()
    {
        // Register new titles here:
        Register("SLUS_210.87", () => new MidwayBootAssist());
        Register("SLUS_200.24", () => new BloodOmen2SnAssist());
        Register("SCUS_973.99", () => new GodOfWarAssist());
        Register("SLUS_210.50", () => new Burnout3Assist());
        Register("SLUS_203.83", () => new VexxAssist());
        Register("SLUS_206.84", () => new WhiplashAssist());
        // MK Midway family (DA/Deception/Armageddon): version-policy only — no SM plants.
        Register("SLUS_204.23", () => new MidwayFamilyAssist("SLUS_204.23", "Mortal Kombat: Deadly Alliance (USA)"));
        Register("SLUS_208.81", () => new MidwayFamilyAssist("SLUS_208.81", "Mortal Kombat: Deception (USA)"));
        // Armageddon USA standard (SLUS_215.50) + Premium Edition (SLUS_215.43) — same SN gates.
        Register("SLUS_215.50", () => new MidwayFamilyAssist("SLUS_215.50", "Mortal Kombat: Armageddon (USA)"));
        Register("SLUS_215.43", () => new MidwayFamilyAssist("SLUS_215.43", "Mortal Kombat: Armageddon (USA) (Premium Edition)"));
        // IOPRP ASCII GetVersion policy (no memory plants) — PreferIopRpGetVersion only.
        Register("SCUS_974.72", () => new TeamIcoAssist("SCUS_974.72", "Shadow of the Colossus (USA)"));
        Register("SCUS_971.13", () => new TeamIcoAssist("SCUS_971.13", "Ico (USA)"));
        // Haven: Call of the King — IOPRP250 → "2500"; classic 0x00020000 → Exit before MOD_LOAD.
        Register("SLUS_205.17", () => new TeamIcoAssist("SLUS_205.17", "Haven: Call of the King (USA)"));
        // Register("SLUS_XXXXX", () => new MyNewTitleQuirks());
    }

    /// <summary>Add or replace the factory for a serial. Safe to call from a module's own
    /// static constructor if you'd rather keep registration next to the implementation —
    /// but the convention is to list it here so every module is discoverable in one place.</summary>
    public static void Register(string serial, Func<IGameQuirkModule> factory) =>
        _factories[serial] = factory;

    /// <summary>Returns a fresh module instance for the given normalized serial, or null if
    /// no module is registered for it (the overwhelmingly common case — most titles need no
    /// quirks at all).</summary>
    public static IGameQuirkModule? Resolve(string? serial)
    {
        if (string.IsNullOrEmpty(serial)) return null;
        return _factories.TryGetValue(serial, out var factory) ? factory() : null;
    }
}
