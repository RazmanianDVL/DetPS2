from pathlib import Path

b3 = Path("../detps2-wiki/Burnout-3-Takedown.md")
da = Path("../detps2-wiki/Mortal-Kombat-Deadly-Alliance.md")

note_b3 = """

### Wave-7 scout (2026-07-30, post-G0) — commit `e90eaef` / evidence `out/b3-da-deliver`

| Signal | Value |
|--------|-------|
| STG bind | **YES** (sid=`0x00475453`) on deliver binary |
| Full TXD | **YES** fno=5 n=**1146112** `Data\\Global.txd` |
| FRONTEND | **open** (STAGEHED+FRONTEND+HEADUS; fno=5 arm FRONTEND after TXD) |
| cdvd | **2425** (deliver 80M); tip after SM RR may re-park residual (cdvd=425) — re-validate |
| gifP3 / dmac / calls | **656 / 831 / 602** |
| px / MENU | **0 / No** |
| Presentation wall | UnknownMmioRead flood **0x21A5xx** / PC **0x1F308C** after TXD |
| Assist | `Burnout3Assist` post-TXD MMIO probe leave @0x21A5xx/0x1F308C → 0x1F2520 (`e90eaef`) |

**Residuals (#20):** first GS frame (px>0) after FRONTEND DMA; tip residual-STG flaky vs deliver binary after concurrent SM G0 RR (`44328d2`).
"""

note_da = """

### Wave-4 scout (2026-07-30, post-G0) — MFL pump restored `dc102d8`

| Signal | Value |
|--------|-------|
| MFL pump | **YES** — `MidwayFamilyAssist.TryPumpMslFiles` → `PumpMslFileRequests` |
| MKDA ring-complete | **YES** path=`cdrom0:\\MKDA.PAK` h=1 size=464752116 |
| gameart warm | **YES** — MSL DADA warm member `gameart.ssf` size=**4298752** |
| cdvd | **259** (was 219) |
| CallRpc sid `0x12347` | still **No** (only MSL `0x12345` DADA) |
| PC | **0x2F5580** wait-ready (s0 null path) |
| px / MENU | **0 / No** |

**Residuals (#16):** archive host+4 / honest gameart job non-null + MFL CallRpc member open; do **not** force-complete wait status=4 (Exit). Shared with Dec #22.
"""

if b3.exists():
    t = b3.read_text(encoding="utf-8")
    if "Wave-7 scout" not in t:
        b3.write_text(t.rstrip() + note_b3, encoding="utf-8")
        print("B3 wiki updated")
    else:
        print("B3 wiki already has Wave-7")
else:
    print("B3 wiki missing")

if da.exists():
    t = da.read_text(encoding="utf-8")
    if "Wave-4 scout" not in t:
        da.write_text(t.rstrip() + note_da, encoding="utf-8")
        print("DA wiki updated")
    else:
        print("DA wiki already has Wave-4")
else:
    print("DA wiki missing")
