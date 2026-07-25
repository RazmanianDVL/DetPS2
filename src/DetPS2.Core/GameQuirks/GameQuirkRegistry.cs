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
