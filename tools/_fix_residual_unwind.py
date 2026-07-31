#!/usr/bin/env python3
from pathlib import Path

p = Path("src/DetPS2.Core/GameQuirks/Burnout3Assist.cs")
t = p.read_text(encoding="utf-8")
old = """                    sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = 0x004427FCu });
                    sys.EE.PC = 0x004427FCu;"""
new = """                    // CallRpc epi at 0x10F3A8 unwinds sp+192 then ld ra -> parent 0x4427FC.
                    // Jumping to parent without unwind drops sp each residual (live FC10->F290).
                    sys.EE.PC = 0x0010F3A8;"""
if old not in t:
    raise SystemExit("target missing")
# avoid duplicate SetGpr(2) lines - keep the one above old
t = t.replace(old, new, 1)
t = t.replace("residual LGDEV->parent post-jal", "residual LGDEV CallRpc->parent", 1)
p.write_text(t, encoding="utf-8")
print("ok")
