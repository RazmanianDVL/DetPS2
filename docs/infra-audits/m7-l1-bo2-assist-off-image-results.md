# M7-L1 results — Blood Omen 2 natural IMAGE (assist chrome already soft-off)

**Status:** **measured** — honest residual; **no Core** this seat  
**Tip:** `e3a943f` (+ this docs commit)  
**Peer:** `m7-l1-whip-assist-off-image-results.md`  
**Parent honesty:** `m7-residual-honesty-rollup-2026-08-04.md` (BO2 R1 MAINMENU stream)

---

## 1. Product state of BO2 IMAGE assists

| Assist | Status at tip |
|--------|----------------|
| **PL-027 / G-GFX-3 / MENU-BO2** Host→Local MAINMENU.BG2 / MAINSKY.BG2 | **DISABLED** (2026-08-02) — goefile symbol table paint, not pixels (`BloodOmen2SnAssist.cs`) |
| M8 PreferIopRp / version plant | product **soft-off** (M8-a) |
| UseBigfile / CODE stream / CreatingMainLayer | boot/stream path — **not** GIF IMAGE inject of goefile as pixels |

Product arm **is** the IMAGE assist-off arm for Host→Local menu chrome.

---

## 2. Measure (diagnose 20M, tip `e3a943f`)

```text
dotnet exec out/scoreboard-build/DetPS2.Core.dll scoreboard-metrics user-media-bloodomen2.json `
  --cycles=20000000 --host-present --out=out/canaries/m7-l1-bo2/<stamp>/product-metrics.json
```

Artifact: `out/canaries/m7-l1-bo2/20260804-194450/product-metrics.json`  
Wall ~7.5 s. `exitRequested=false`, exit 0.

| Field | Value |
|-------|-------|
| **imgBytes** | **0** |
| gifP2 / gifP3 | 0 / 2 |
| gifCompleted | 3 |
| px / prims | 286720 / 1 |
| compositeSource | None |
| expandHits | 1 |
| naturalDispfb | true |
| G2 | **N** |
| T3 | N |
| PC | `0x00488898` |
| binds / calls / cdvd | 14 / 62 / 2211 |

Matches prior M8 soft-off + fleet flag-off BO2 20M identity rows (`imgBytes=0`).

---

## 3. Verdict

| Question | Answer |
|----------|--------|
| Natural game IMAGE at diagnose? | **No** — `imgBytes=0`, Path2=0 |
| Assist Host→Local chrome still inventing IMAGE? | **No** — MENU-BO2 / PL-027 disabled |
| Class | **R1 honest residual** — MAINMENU stream path may load BG2; Soft-GS never sees Host→Local IMAGE |
| Core this seat? | **No** — do not invent Path2 IMAGE or re-enable goefile paint |
| Reopen when? | Real goefile **texture-block** parser (or proven non-goefile menu asset path) posts GIF IMAGE |

---

## 4. Pair with Whip

| Title | Host→Local chrome | imgBytes @20M | Seat status |
|-------|-------------------|---------------|-------------|
| Whip | MENU-WHIP-2 off | 0 | closed honesty |
| BO2 | MENU-BO2 off | 0 | closed honesty |
| Haven | (optional later) | — | free if dual-idle wants peer |

```text
M7-L1 BO2 IMAGE
  product=assist-off for Host→Local chrome
  imgBytes=0 @20M tip e3a943f — R1 honest residual
  no Core; reopen only on real texture path
```
