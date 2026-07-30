from pathlib import Path

p = Path("src/DetPS2.Core/GameQuirks/GodOfWarAssist.cs")
t = p.read_text(encoding="utf-8")

if "private int _tickWaitEscapes" not in t:
    old = "    private int _globalFreeEscapes;\n    private ulong _lastWorldKickCyc;"
    new = "    private int _globalFreeEscapes;\n    private int _tickWaitEscapes;\n    private ulong _lastWorldKickCyc;"
    if old not in t:
        raise SystemExit("field anchor missing")
    t = t.replace(old, new, 1)
    print("field added")
else:
    print("field exists")

if "_tickWaitEscapes = 0" not in t:
    old = "        _globalFreeEscapes = 0;\n        _lastWorldKickCyc = 0;"
    new = "        _globalFreeEscapes = 0;\n        _tickWaitEscapes = 0;\n        _lastWorldKickCyc = 0;"
    if old not in t:
        raise SystemExit("reset anchor missing")
    t = t.replace(old, new, 1)
    print("reset added")
else:
    print("reset exists")

if "public const uint SoftTickPtr" not in t:
    old = (
        "    /// <summary>Payload arena after config nodes (see <see cref=\"PlantFreelistHeader\"/>).</summary>\n"
        "    public const uint HeapArenaBase = HeapDefaultNodeBase + 0x200;\n"
        "    public const uint HeapArenaBytes = 0x00180000; // 1.5 MiB\n"
        "    public const uint HeapBlockSize = 0x1000; // 4 KiB carve units\n"
    )
    new = (
        "    /// <summary>Payload arena after config nodes (see <see cref=\"PlantFreelistHeader\"/>).</summary>\n"
        "    /// <remarks>Must stay under 32 MiB RDRAM (base 0x01FD8200 leaves ~160 KiB).</remarks>\n"
        "    public const uint HeapArenaBase = HeapDefaultNodeBase + 0x200;\n"
        "    public const uint HeapArenaBytes = 0x00025000; // ~148 KiB, end 0x01FFD200\n"
        "    public const uint HeapBlockSize = 0x400; // 1 KiB carve units\n"
        "\n"
        "    /// <summary>Software tick counter polled by wait leaf 0x17A1D0 (*0x29C7D4).</summary>\n"
        "    public const uint SoftTickPtr = 0x0029C7D4;\n"
        "    /// <summary>Flag polled by software delay 0x17A328 / 0x183880.</summary>\n"
        "    public const uint SoftSpinFlagPtr = 0x0029C7D0;\n"
        "    /// <summary>Nonzero → tick-wait takes fast clear+return after tick satisfied.</summary>\n"
        "    public const uint SoftTickFastPtr = 0x0029C664;\n"
    )
    if old not in t:
        # arena may already be partially updated
        old2 = (
            "    public const uint HeapArenaBase = HeapDefaultNodeBase + 0x200;\n"
            "    public const uint HeapArenaBytes = 0x00180000; // 1.5 MiB\n"
            "    public const uint HeapBlockSize = 0x1000; // 4 KiB carve units\n"
        )
        new2 = (
            "    public const uint HeapArenaBase = HeapDefaultNodeBase + 0x200;\n"
            "    public const uint HeapArenaBytes = 0x00025000; // ~148 KiB, end 0x01FFD200\n"
            "    public const uint HeapBlockSize = 0x400; // 1 KiB carve units\n"
            "\n"
            "    public const uint SoftTickPtr = 0x0029C7D4;\n"
            "    public const uint SoftSpinFlagPtr = 0x0029C7D0;\n"
            "    public const uint SoftTickFastPtr = 0x0029C664;\n"
        )
        if old2 not in t:
            raise SystemExit("const anchor missing: " + repr([ln for ln in t.splitlines() if "HeapArena" in ln]))
        t = t.replace(old2, new2, 1)
        print("consts added (short)")
    else:
        t = t.replace(old, new, 1)
        print("consts added")
else:
    print("consts exist")

p.write_text(t, encoding="utf-8")
print("wrote", p)
