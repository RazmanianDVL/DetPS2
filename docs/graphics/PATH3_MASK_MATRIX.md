# PATH3 mask matrix — `Path3MaskedByVif` (GIF_STAT.M3P)

**Seat:** S8 GFX-PATH · **WP:** GX-005 · **Season:** G0  
**Freeze:** Soft-GS truth · no invent PATH3 packets · **no wholesale M3P clear without soak**  
**Code:** `Gif.Path3MaskedByVif` / `Gif.SetMskPath3` · VIF `MSKPATH3` (`CmdMskPath3`, IMM bit 15) · `Dmac` path3Hold drain

---

## 1. Hardware contract (what we implement)

| Signal | Source | Behavior in DetPS2 |
|--------|--------|--------------------|
| **M3P** | VIF1 `MSKPATH3` IMM bit 15 → `Gif.SetMskPath3` | PATH3 DMA holds in FIFO queue; FQC≥1 while masked |
| **M3R** | `GIF_MODE` bit 0 | Permanent PATH3 mask (same hold path) |
| **Unmask** | `MSKPATH3` IMM bit15=0 or clear M3R | `DrainHeldPath3()` → `ProcessTransfer` all held entries |
| **gifPath3** | `Gif.Path3Transfers` | Increments on **submit** (including held) |
| **heldP3n / heldP3qwc** | claim `gif-path:` | Live hold queue depth (cap 48 entries) |

Real titles use M3P for **path-sync** (poll M3P/FQC around VIF1 DIRECT vs GIF PATH3). Instant-drain under mask hung Burnout 3 at `0x001F19C0` / FQC spin `0x001F1A28`.

---

## 2. When is `Path3MaskedByVif == true`?

| Condition | Result |
|-----------|--------|
| VIF processes `MSKPATH3` with IMM `& 0x8000` | M3P=1 until opposite MSKPATH3 |
| `GIF_CTRL` reset bit | M3P cleared |
| Assist / quirk calls `SetMskPath3(false)` | M3P=0 + drain (title-local; see below) |
| Game never issues MSKPATH3 | M3P stays 0 for whole boot |

**Not** the same as “gifPath3==0”. M3P can be 1 with held PATH3 traffic (`heldSubmits>0`, `gifPath3` climbing, Soft-GS px stuck).

---

## 3. Title matrix (MENU-era Soft-GS fleet)

Statuses from title-port claims / assists as of 2026-07-31. **Natural** = game VIF unmask. **Assist clear** = quirk `SetMskPath3(false)`. **Held policy** = do not force-clear.

| Title | Serial | gifP3 @ claim (typ.) | M3P at residual? | Who clears M3P? | PATH3 role at MENU | Safe clear? |
|-------|--------|----------------------|------------------|-----------------|--------------------|-------------|
| **God of War** | SCUS_973.99 | **0** (shell IMAGE residual) | Often false; policy **held** | **None** (GoW assist must not invent PATH3 / unmask) | Path2 sticky title SPRITE; PATH3 not natural yet | **No** — wait real MSKPATH3 unmask + PATH3 DMA |
| **Burnout 3** | SLUS_210.50 | high (hundreds–1k+) | Yes early path-sync | **Assist sticky** when `M3P && px==0 && gifP3≥30` (`Burnout3Assist`) | PATH3 IMAGE / flip; Soft-GS merge composite | **Soak only** — early global unmask regresses path-sync |
| **Blood Omen 2** | SLUS_200.24 | low (~2) | Can stick post-English | **Assist** unmask in list-stub / draw kick path | Dual list + Path2 title surface | Title-local only; no core default |
| **MK Shaolin Monks** | SLUS_210.87 | ~18 | Assist / Midway can clear on second chrome | **MidwayBootAssist** `SetMskPath3(false)` near PATH3 plant | Logo + second-chrome PATH3 | Do not remove plant without G4 natural PATH3 |
| **MK Deception** | SLUS_208.81 | Path2-heavy | Midway family | Midway clears on keep-alive paths | Path2 idle pump + display | Same as SM |
| **MK Deadly Alliance** | SLUS_204.23 | Path2-heavy | Midway family | Midway clears | Path2 display chains | Same as SM |
| **Haven** | SLUS_205.17 | ~68 | Unclear / often drained | Prefer natural | PATH3 logo clear | No core sticky clear |
| **Vexx** | SLUS_203.83 | low / Path2 | Usually off | Natural | Title-surface Path2 | Leave |
| **Whiplash** | SLUS_206.84 | Path2 title | Usually off | Natural | Path2 firstscreen | Leave |

### DMAC interaction (not a title quirk)

When `Path3MaskedByVif`, DMAC forces extra **Step** drain on VIF0/VIF1/GIF CHCR starts so path-sync chains under M3P complete (`Dmac` `path3Hold`). **DA** also gets TTE VIF1 high-TADR drain without requiring M3P. **Do not** make this unconditional — GoW early boot regressed when VIF/GIF always micro-drained (`agent/menu-gow-w3`).

---

## 4. Safe clear hypotheses (for GX-015 / GX-050 — not G0 implement)

Ordered **least → most invasive**. Each needs **multi-title soak** (≥5, include GoW + B3 + Dec/SM) before core change.

| ID | Hypothesis | Expected win | Risk | Soak gate |
|----|------------|--------------|------|-----------|
| **H0** | **Do nothing in core** — only game MSKPATH3 unmask + existing hold queue | Correct hardware model | None | Default G0/G1 |
| **H1** | Unmask when VIF goes idle **and** no DIRECT remaining **and** heldP3n>0 **and** MSKPATH3 last was mask (timeout N vblanks) | B3/BO2 stuck M3P without assist | False unmask mid path-sync | 5-title + B3 px hold |
| **H2** | Unmask only if `heldP3qwc>0 && prims==0 && cycles since last MSKPATH3 > T` | px recovery when game forgot unmask | Same as H1 | Same |
| **H3** | Keep **title assists** for H1/H2 (current B3/BO2/Midway) | MENU hold | Title debt | Document only |
| **H4** | Dynamic unmask when next VIF cmd is not path-sync related | G-GFX-7 natural gifP3 | High | GX-050 |

**Rejected without soak:**

- Global `SetMskPath3(false)` on any PATH3 submit  
- Clearing M3P from Soft-GS when `px==0` in `Gif` itself  
- Inventing PATH3 GIF packets to “force” chrome (forbidden; GoW/S8 freeze)

---

## 5. Telemetry (claim lines after GX-003)

Always on blocker-trace:

```text
gif-path: p1=… p1qws=… p2=… p2qws=… p3=… p3qws=… m3p=True|False heldP3n=… heldP3qwc=… heldSubmits=… mskPath3=…
gif-pkts: completed=… aborted=… spannedCalls=… inFlight=… tags=… p2qws=…
gif-tags: packed=… reglist=… image=… disable=… abortNewDir=… abortTrunc=… abortOther=… lastAbort=…
```

With `DETPS2_TRACE_GIF=1`: stderr Path1/2/3 xfer + tag lines + claim `gif-trace-ring` dump.

**Diagnose stuck PATH3 under mask:** `m3p=True` + `heldP3n>0` + `p3` climbing + `prims/px` flat → unmask missing (H1/H3), not Soft-GS raster.

**Diagnose GoW shell:** `m3p=False` + `p3=0` + Path2 `completed/aborted` healthy → not an M3P wall; stream/PATH3 submit residual.

---

## 6. Freezes / handoff

| Rule | Owner |
|------|--------|
| Path3MaskedByVif policy changes need this matrix + soak note | S8 |
| No invent PATH3 packets for chrome | S8 + title seats |
| Raster / ofx expand | S9 (not this doc) |
| Claim scrape of new gif-path lines | S10 tools optional |

**Next WPs:** GX-015 (clear conditions) · GX-050 (dynamic unmask) · title seats report natural `gifP3` with `m3p` history.
