using System;

namespace DetPS2.Core;

/// <summary>
/// Extension point for per-title / per-middleware HLE workarounds.
///
/// DetPS2 does high-level emulation of the IOP side (no real R3000A execution of every
/// module) plus a fast-boot EE runtime instead of full BIOS execution. Both are genuine,
/// necessary simplifications — but they mean some titles hit a real-hardware behavior our
/// HLE hasn't modeled yet (a proprietary RPC protocol, a boot-time timing assumption, a
/// polling idiom our synthesized vectors don't fully replicate). When that happens for one
/// specific disc, the fix belongs here — as an isolated module keyed by disc serial — not
/// as another hardcoded PC range hand-edited into a shared core file like
/// <see cref="EmotionEngine"/> or <see cref="RealSifRpc"/>.
///
/// Policy (see docs/TITLE_HACKS.md): always prefer a general emulation-correctness fix over
/// a title-specific one. Only reach for a quirk module when the global fix would require
/// modeling something DetPS2 doesn't do yet (e.g. real IOP module execution) and the disc
/// needs to boot in the meantime.
///
/// See <see cref="GameQuirkRegistry"/> for how modules are discovered, and
/// <c>MidwayBootAssist</c> for the current (pre-SDK) reference implementation whose hooks
/// this interface is modeled after.
/// </summary>
public interface IGameQuirkModule
{
    /// <summary>Normalized disc serial this module targets, e.g. "SLUS_210.87".
    /// Must match the format <see cref="MediaVerify.NormalizeSerial"/> produces.</summary>
    string Serial { get; }

    /// <summary>Human-readable title, for logs/UI — e.g. "Mortal Kombat: Shaolin Monks (USA)".</summary>
    string DisplayName { get; }

    /// <summary>Called once, right after <see cref="DiscBoot.BootFromDisc"/> has parsed
    /// SYSTEM.CNF and loaded the boot ELF, before any cycles run.</summary>
    void OnDiscMounted(Ps2System sys);

    /// <summary>Called periodically from <see cref="Ps2System.RunFor"/>'s commercial-boot
    /// slice loop (roughly every 25k master cycles) — the main place to poll EE/PC state
    /// and nudge things forward. Keep this cheap; it runs on the hot path.</summary>
    void Step(Ps2System sys);

    /// <summary>Called once per host display refresh (see the Desktop shell's present loop),
    /// independent of how many EE cycles ran that tick. Use for anything that should be
    /// paced by wall-clock frames rather than emulated cycles (e.g. FMV playback).</summary>
    void OnHostPresent(Ps2System sys);

    /// <summary>Called from <see cref="Ps2System.Reset"/> — clear all module-local state.</summary>
    void Reset();
}
