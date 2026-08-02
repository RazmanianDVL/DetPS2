# Vexx (USA) — residual + draw-graph charter (S7 / PL-005 · GX-008)

| Field | Value |
|-------|--------|
| **Title** | Vexx (USA) |
| **user-media id** | `vexx` |
| **Serial / BOOT2** | `SLUS_203.83` |
| **ISO** | `user-media-vexx.json` → operator ISO (never commit path) |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-vexx.json` |
| **Seat / branch** | **S7 STREAM** · MENU-VEXX-11 PL-032p |
| **Build** | `out/seat-menu-vexx` |
| **Assist** | `VexxAssist.cs` (owned) |
| **Status** | **NOT MENU YES** — lit=6405 hold; PL-032p: 0x11C200 thunk **CLEARED**; widget.atr FULL; residual **SIF sid 0x54323** / PC≈0x399130 |
| **Last updated** | 2026-07-31 |
| **WP** | Bind/HLE SIF RPC sid 0x54323 + clear syscall 0x2F/0x3E storm → lit>>20k |

### MENU gate

**title-surface Soft-GS** = non-black Soft-GS after STREE0 virtual FS binds frontend assets.
Not MK MAINMENU language.

---

## Diag 40M/100M (SEMA_STALL_YIELD OFF) — MENU-VEXX-11 PL-032p · 2026-07-31

### Hang class

| Class | Verdict |
|-------|---------|
| **WaitSema** | **No** — main `started=True sleeping=False waitSemaId=0` |
| **MMIO / thunk** | **Cleared↑** — PC no longer stuck at 0x11C200 (open-bus was false-CRT0) |
| **SIF RPC** | **Wall** — bind-retry delay @0x1B12xx after widget.atr; unknown sid **0x54323** |
| **Soft-GS** | **Plateau** — lit=6405 prims=26 (Path2 list frozen; gifP3↑ to 15) |

### ELF ground-truth (PL-032p)

- Residual **0x0011C200** is **not** Midway CRT0. Family `0x11C170..0x11C3F0`:
  `lw t9,0x28(a0); lw t9,slot(t9); jr t9` (slot 0x298 @0x11C200).
- Path-object retail: alloc **260 (0x104)** @`0x223BD8` → ctor `0x21CDB0`; name @+0xC;
  payload @+0x10/+0x14 after load; caller miss path `jal 0x223970` create/register.
- Open-bus rescue using `0x11C200` as CRT0 **re-entered the thunk forever** on Vexx.
- Post-thunk wall: SIF bind wait `0x1B1198` — `jal 0x1C5018` fail → 0x100000-iter delay
  @`0x1B1294`/`0x1B131C` → retry. Claim: `unknownBindSids=1 sid=0x00054323`.

### PL-032p assist

1. Path stub **0x110** + host actor vtable @+0x28 (nop methods covering slot 0x298).
2. FORCE-**MISS** name-search (v0=s0=0) so retail create `0x223970` re-opens path
   (screenproxy.atr OPEN #40+#41); payload still cached for residual consumers.
3. Null-vtable thunk escape + **reject open-bus re-home to thunk** (`IsSafeOpenBusCrt0`
   in `EmotionEngine` — skip `lw t9,0x28(a0)` pattern).
4. SIF bind-wait delay skip + hard-return after retries (v0=0).
5. Never stack-death resume to 0x11C200.

### Diag @100M TRACE (PL-032p5)

```
@100M: lit=6405  prims=26  px=883720  cdvd=5907  gifP3=15 (was 5) dmac=19 (was 9)
      PC=0x00399130  telemetryHits=33  syscalls≈762k (0x2F×444k 0x3E×222k)
      begin.mtf MEMBER OPEN+read (PR alias) — hold
      FORCE-MISS screenproxy.atr → create re-open #41
      widget.atr MEMBER OPEN #42 size=10577262 FULL multi-chunk cache
      sif-rpc bind-wait escape ×5 → leave 0x1B12xx → PC 0x37348C then 0x399xxx
      Path2 prims=26 frozen; Soft-GS title surface unchanged
```

**CLAIM LINE (Vexx / PL-032p — honest, NOT MENU YES upgrade):**  
`Vexx SEMA_OFF @100M title-surface hold px=883720 prims=26 img=38912 dispfb=6534 lit=6405 gifP3=15 | begin.mtf OPEN | widget.atr FULL 10.5MB | 0x11C200 thunk CLEARED | residual PC=0x399130 SIF-RPC sid=0x54323 / syscall 0x2F·0x3E storm | lit>>20k next (bind 0x54323)`

Build: `out/seat-menu-vexx`  
Traces: `out/traces/menu-vexx-{diag40m,100m}-pl032p5-{out,err}.txt`  
**No MENU YES.**

### Next wall (PL-032q candidate)

1. **Implement / HLE-bind SIF RPC sid `0x00054323`** (unknownBindSids=1) so post-widget
   frontend does not busy-wait or syscall-storm.
2. Diagnose syscall **0x2F / 0x3E** storm @PC≈0x399130 after bind-wait hard-return.
3. Path2 still prims=26 — actor mesh bind → IMAGE/draw still residual even after RPC.

---

## Diag 40M (SEMA_STALL_YIELD OFF) — MENU-VEXX-10 PL-032m · 2026-07-31 (prior)

### Hang class

| Class | Verdict |
|-------|---------|
| **WaitSema** | **No** — main `started=True sleeping=False waitSemaId=0` |
| **MMIO** | **Cleared↑** — UnknownMmioRead storm gone (`telemetryHits=0`) |
| **Name bind** | **Wall** — search @0x224360 for `screenproxy.atr` sees **a0=0** on every slot |

### PL-032l baseline (superseded)

```
@40M/100M PL-032l: lit=6405 prims=26 PC=0x35B534
  escape #1 0x2243A0 → 0x225004 (mid-other-fn hard-leave — wrong)
  no TRACE on queue jobs; thrash left but corrupt resume
```

### ELF ground-truth (PL-032m)

- `0x224360`: pointer-table **name search** (`lw a0,0xC(s5); jal 0x1CF410`).
- `0x1CF410`: case-fold strcmp (fold table `0x3D3010`); delay of `jr ra` is `subu v0,v1,v0`.
- PL-032l hard-leave `0x225004` is mid-body of a different float/copy frame — **not** a safe exit.
- Natural miss epilogue: `move v0,s0` @`0x2243F0` / `jr ra` @`0x224410` (saved ra @`sp+0x60`).

### PL-032m assist

- Host-complete strcmp on bad a0/a1 → return via **caller ra** (not jr-delay).
- Skip bad name-search slots → continue `0x2243B8`.
- Last-resort natural epilogue `0x2243F0` with s0=v0=0; deep stack scan incl. sp+0x60.
- vexxHot: `0x1CF410` + `0x224360` band.

### Diag @40M TRACE (PL-032m)

```
@40M: lit=6405  prims=26  px=883720  cdvd=630  PC=0x0011C200  telemetry=0
      begin.mtf MEMBER OPEN+full-read 2302 (PR alias)
      MEMBER screenproxy.tgax/mtf deitynofade.atr screenproxy.atr (members=38)
      screenproxy.atr OPEN #40 — **no host-read** after open
      name-strcmp host-complete ×32+ a0=0 a1=needle "data\actors\widgets\screenproxy.atr"
      host-open empty-path @23.05M → idle dispatch thunk @0x11C200
      Path2 prims frozen (lit residual)
```

**CLAIM LINE (Vexx / PL-032m):**  
`Vexx SEMA_OFF @40M MENU hold px=883720 prims=26 img=38912 dispfb=6534 lit=6405 | begin.mtf OPEN+read | name-search MMIO cleared | screenproxy.atr OPEN no-read | residual PC=0x11C200 null-name table | lit>>20k next`

Build: `out/seat-menu-vexx`  
Diag 40M: `out/traces/menu-vexx-diag40m-pl032m-{out,err}.txt`  
PL-032l queue: `out/live-queue/done/menu-vexx-{40m,100m}-pl032l-20260731-150519.json`  
**No 100M verify enqueued** (lit did not move).

### PL-032o progress (evening 2026-07-31) — still NOT MENU YES

```
@100M PL-032o5: lit=6405 prims=26 px=883720 cdvd≈5905 PC=0x0011C200
  FORCE-MATCH #1 screenproxy.atr stub+payload n=883
  widget.atr MEMBER OPEN #41 FULL multi-chunk cache n=10577262/10577262
  residual: path-stub zeroed → vtable thunk 0x11C200 (lw t9,0x28(a0); jr t9)
  Path2 prims frozen
```

**CLAIM (honest):**  
`Vexx SEMA_OFF @100M title-surface hold lit=6405 | begin.mtf OPEN | widget.atr FULL 10.5MB host-cache | FORCE-MATCH screenproxy | residual PC=0x11C200 actor-stub vtable | NOT MENU YES | lit>>20k next`

Traces: `out/traces/menu-vexx-100m-pl032o5-{out,err}.txt`

### Next wall (PL-032p)

1. Force-match stub must be a valid actor object (vtable at +0 / method slot for 0x11C200), not zeroed bump.
2. Or register host-cached atr payload into retail object table so name-search finds real s5.
3. Path2 still prims=26 / lit=6405 until bind works.

---

## Diag 40M/100M (SEMA_STALL_YIELD OFF) — MENU-VEXX-8 PL-032k · 2026-07-31 (prior)

### Hang class (NOT WaitSema / NOT MMIO)

| Class | Verdict |
|-------|---------|
| **WaitSema** | **No** — main thread `waitSemaId=0` sleeping=False; thrash is pure EE |
| **MMIO** | **Partial** — post-PR residual freelist @0x1CF43C (was UnknownMmioRead @0x2243A0) |
| **Parse / list** | **↑↑** — **begin.mtf MEMBER OPEN+full-read 2302 ELIF** via *PR path alias |

### *PR skip → alias open (PL-032k)

begin.pcl (690B) precache list (ground-truth from STREE0):

```
*PR = "y:\data\textures\onscreengraphics\screenproxy.tgax"   ← OPEN
*PR = "y:\data\levels\frontend\memorycard\begin\begin.mtf"   ← path-specific SKIP
*PR = "y:\data\actors\widgets\screenproxy.mtf"                 ← OPEN
…
```

**Root cause (PL-032k diagnosed):** *PR skip is **path-specific** to
`y:\data\levels\frontend\memorycard\begin\begin.mtf` — not position (reorder to 3rd
slot still skipped), not pack-TOC membership alone (TOC off/sz/CRC scrub still skipped),
not stream-map dual-inject (inject caused silent miss path). begin0.tre embeds the same
mesh at pack+0x504 (FILE/ELIF @2302B, NameCRC `0x2EA9190F`) but EE does not host-open
the retail path string.

**Fix (PL-032k5):** after begin.pcl BADARGS recover, alias the *PR path in-place to a
same-length `y:\data\actors\widgets\zzzz…begin.mtf` string; HostCdOpen remaps alias →
real dual-slide STREE member `data\levels\frontend\memorycard\begin\begin.mtf`.

### PL-032k assist

- begin.pcl *PR path alias (50-char, actors\widgets style) + HostCdOpen remap.
- begin0.tre pack base note + TOC scrub begin.mtf slot (off/sz/CRC zero).
- Stream-map dual-inject for companion dual-only CRCs (not begin.mtf — HIT skipped open).
- ACTOR_MESH `$\\DATA\…` normalize; begin.mtf leaf → full key.
- Post-PR residual soft-escape @0x2243A0 band.
- (PL-032j retained) package-buffer ring; begin0.tre TRE prefer; ELIF score.

### Diag @100M (PL-032o — 2026-07-31 evening)

```
@100M: lit=6405  prims=26  px=883720  cdvd≈5905  (was ~630)
      begin.mtf MEMBER OPEN+read (PR alias) — hold
      name-search @0x2243A0 = strcmp path-table (ELF ground-truth); PL-032m host-complete
      FORCE-MATCH screenproxy.atr stub+payload 883B
      widget.atr MEMBER OPEN #41 size=10577262 FULL multi-chunk cache n=10577262/10577262
      residual PC=0x0011C200 vtable thunk (lw t9,0x28(a0); jr t9) — zeroed path-stub unusable as actor
      Path2 prims=26 frozen; Soft-GS title surface unchanged
```

**CLAIM LINE (Vexx / PL-032o — honest, NOT MENU YES):**  
`Vexx SEMA_OFF @100M title-surface hold px=883720 prims=26 img=38912 dispfb=6534 lit=6405 | begin.mtf OPEN | widget.atr FULL 10.5MB host-cache | FORCE-MATCH screenproxy.atr | residual PC=0x11C200 path-stub vtable | lit>>20k next (actor object layout)`

Build: `out/seat-menu-vexx` + Release Core  
Traces: `out/traces/menu-vexx-100m-pl032o5-{out,err}.txt`  
**No MENU YES.**

### PL-032i baseline (superseded for load path)

```
@40M PL-032i: lit=6405 begin.pcl→0x672C10 (clobbered atr); begin0.tre force-FAIL; PR to deity; begin.mtf skip
```

### PL-032h baseline (superseded for load path)

```
@100M PL-032h: lit=6405 begin.pcl OPEN (freelist BADARGS) residual PC=0x35E190 ready-flag poll
```

### PL-032e baseline (superseded for load path; Soft-GS metrics same)

```
@100M PL-032e: lit=6405 members=29 begin.atr BADARGS→stale bump (full-read FAIL) cdvd=476
```

Reproduce:

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/seat-menu-vexx
$env:DETPS2_TRACE_VEXX='1'
dotnet exec out/seat-menu-vexx/DetPS2.Core.dll blocker-trace user-media-vexx.json --cycles=100000000 --host-present
# or enqueue: user-media-vexx.json cycles=100000000 hostPresent=true → out/live-queue/inbox/
```

---

## How far

| Milestone | Result |
|-----------|--------|
| Disc / ELF `SLUS_203.83` | **Yes** |
| IOPRP252 PreferIopRp + pad OPEN | **Yes** |
| SearchFile GAME.TXT / STREE0.TRE | **Yes** (path +0x24) |
| STREE0 TOC + stream-map hash table | **Yes** (count=11364, table host-built) |
| Virtual member FS (NameCRC→off/sz) | **Yes** (index≈9674; **23 MEMBER** opens @100M) |
| Soft-GS **px>0** title surface | **Yes** (**px=877830 prims=25**) |
| IMAGE bytes (TEX path) | **Yes↑** (**imgBytes=38912**, was 5120) |
| DISPFB present path | **Partial** (**dispfbPx=644**, was 0) |
| host-read BADARGS (text/begin.atr) | **Recovered** (freelist/s-reg/host-bump buffer) |
| Full TRE member completeness | **PL-032e↑** button2–11 + loadtimer_* + begin.atr(200B); nested stree/sound0 force-FAIL |
| Soft-GS present lit | **Residual** lit≈6405/286720 sparse (same Path2 list; not TRE-bound) |
| begin.atr full-read (PL-032f) | **Yes** (EE-heap dest + s1 div patch; was FAIL→stall) |
| swooshes.swh | **Yes** sz=57379 (*SWOOSH text score) |
| begin.pvsx | **empty-stub** (absent from STREE0) |
| begin.mtf / begin.ati | **In STREE0** (CRC hit, FILE/ELIF @2302B); pcl *PR names mtf but **EE never host-opens** (PL-032j residual) |
| begin.pcl PR list | **↑** — tgax + screenproxy.mtf + deity + **screenproxy.atr**; begin.mtf still skipped |
| Pad inject START/CROSS (PL-017) | **Yes** (≥1536 pulses @100M) |
| T2 INTERACTIVE (state/prim delta) | **Residual** (pad live; frontend depth residual) |

---

## Draw-graph charter (menu / title-surface)

| Path | Count / signal | Notes |
|------|----------------|-------|
| **Path1 (VU1)** | gifPath1=**0** | No VU1 title path yet |
| **Path2 (VIF1/DIRECT)** | gifPath2=**12**, p2qws=**2210** | Dominant title draws; XYZ2=48 |
| **Path3 (GIF PATH3)** | gifPath3=**5** | Light PATH3; not assist invent |
| **PRIM/XYZ** | prims=**25**, XYZ2 **48** | Multi-prim surface |
| **IMAGE / TEX** | imgBytes=**38912**, image tags=**4** | PL-032 texture crumbs↑ |
| **DISPFB / PCRTC** | dispfbPx=**644**, naturalDispfb=1 | S10 partial |
| **FRAME / XYOFFSET** | FRAME_1=`0xA008C`, ofx=`0x6C00` ofy=`0x7200` | Retail-center band |
| **Expand** | expandHits=**0** | Natural draw class (not ofx strip) |
| **Rejects** | all **0** | Draws land in Soft-GS FB |

**Draw class:** multi-prim Path2 title chrome + IMAGE crumbs + early DISPFB sample.

---

## Residual truth — **TRE VFS (PL-032 progress)**

### Working spine

1. Host CD I/O vtable @ `0x3AD3A8` → stubs ≥1MiB (`HostCdStubBase=0xF00000`).
2. STREE0 stream-map BUILD + **aligned 24-byte** NameCRC index (`[2]=CRC [4]=off [5]=sz`).
3. Binary texture score ≥ min (compact .tgax/.bmpx no longer rejected at score 8–9).
4. BADARGS bulk-read recover (recent freelist / s-reg / host bump).
5. 23 MEMBER opens: defaultmat, fonts, text, SOUND.AD6, memcard, shadows, hit-flash mats, buttons 1/5–8/10/11, loadtimer_w-alpha, **begin.atr**.

### Open fails / residual (PL-032f)

| Path class | Examples | Status |
|------------|----------|--------|
| Nested TRE probe | `stree1.tre`, `patch0.tre`, `sound0.tre` | **force-FAIL** (must not NameCRC-success) |
| Button fonts | `data\textures\onscreengraphics\fonts\button2–11.tgax` | **OPEN** (score + leaf prefix) |
| Loading UI | `…\loadingscreen\loadtimer_*` | **OPEN** |
| begin.atr | `…\memorycard\begin\begin.atr` | **OPEN+full-read** (PL-032f heap dest + s1/div) |
| begin.pvsx | optional pre-vis | **empty-stub** (not in STREE0) |
| swooshes.swh | `data\swooshes\swooshes.swh` | **OPEN sz=57379** (*SWOOSH score beats binary dual-slide) |
| begin.mtf / .ati | actor mesh/instances | **PL-032k begin.mtf OPEN+read** (PR path alias); begin.ati still residual |
| begin.pcl | precache autolist | **PL-032j** freelist full-read (atr at 0x672C10 preserved) |
| begin0.tre | dual-slide C = real TRE | **PL-032j OPEN** sz=23257 (was force-FAIL atr-as-tre) |
| Present lit | Soft-GS `lit≈6405` | **Residual** — Path2 prims=26 frozen (draw list not asset-bound) |
| PC 0x330E54 | float-expand list | **PL-032g escaped** → natural epilogue |
| PC 0x34030C | epilogue stomp / AdEL | **PL-032h CLEARED** — re-plant `AE00003C`/`DFBF0010` |
| PC 0x32CF18 | id-table search | **PL-032h FAIL-escape** on garbage count near swooshes payload |
| PC 0x35E190 | ready-flag poll | **PL-032i decoded** — left after pcl full-read; residual moved post-PR |
| PC ~0x18Axxxx | freelist asset-as-code | **PL-032i residual** — "SWOO"/path tags after deity atr |

### Forbidden

- Global WaitSema fabricate (WHIP-only).
- Invent PATH3 / plant pixels / FFmpeg logos.
- Full 1GB TRE map into EE RAM (TOC + member stream only).
- Empty nested-TRE open stubs for `streeN.tre` (causes probe cascade).

---

## Assists (current — title-local)

- IOPRP252 version cells + PreferIopRpGetVersion
- CRT/string heap bump + freelist escape
- SearchFile path slide + TRE size cap
- Host CD I/O open/read/seek/tell/size/close
- STREE0 stream-map + NameCRC virtual member FS (aligned + sliding)
- **PL-032** binary texture score + BADARGS recover + sound.ad6 stub only
- **PL-032f** BADARGS EE-heap dest (sp+0x30) + full-read s1/div patch (heap-only) + pvsx empty-stub + *SWOOSH score + recover-buf demotion
- **PL-032g** float-expand circular-list escape (PC≈0x330E54) + freelist tighter post-members + vexxHot slices
- **PL-032h** object-ctor epilogue re-plant (stomp `F000003C`) + host-complete `0x3402E0`/`0x2EE7F0` + id-table FAIL-escape + freelist2 + precache stubs
- **PL-032i** begin.pcl EE-heap full-read + package-era BADARGS demotion; ready-flag decode/force; stack/bump data-as-code rescue; y: path strip; mtf score
- **PL-032j** package-buffer ring (pcl must not clobber atr); code-band reject; begin0.tre real TRE open; ELIF .mtf score; high-stack ∉ freelist
- **PL-032k** begin.pcl *PR path alias → begin.mtf host-open+full-read; begin0.tre TOC scrub; stream-map dual companions; post-PR residual escape
- **PL-032l** reject in-band ra; thrash band leave (wrong hard-leave 0x225004 — superseded)
- **PL-032m** name-search @0x224360 + strcmp @0x1CF410 host-complete; natural epilogue 0x2243F0; hot-slice
- **PL-032n/o** force-match/cache path stubs + widget.atr multi-chunk (superseded strategy by p FORCE-MISS)
- **PL-032p** path stub 0x110 + host vtable; FORCE-MISS create; open-bus thunk reject; SIF bind-wait escape
- **PL-017** dense pad inject + ForceRefreshPad
- Shared: GX-041b sparse natural→FRAME residual (no lit gain — local FRAME matches sparse chrome)

## Debt class

`VexxAssist` TITLE · **PL-032p↑** 0x11C200 thunk cleared; lit plateau 6405 / prims=26; **SIF RPC sid 0x54323** + syscall 0x2F/0x3E storm; begin.ati still residual

## Next WPs (seat S7)

| WP | Goal |
|----|------|
| PL-017 | **Done (pad live)** |
| PL-032 | **Partial↑** — members→40, widget.atr FULL |
| PL-032g | **Done** — hang decoded + float-expand escaped |
| PL-032h | **Done** — AdEL 0x34030C cleared; ctor/id-table |
| PL-032i | **Done↑** — pcl full-read path; superseded by j for clobber |
| PL-032j | **Done↑** — clobber/TRE fixed |
| PL-032k | **Done↑** — begin.mtf OPEN+read (PR alias) |
| PL-032l | **Superseded** — left MMIO band via bad hard-leave |
| PL-032m | **Done↑** — MMIO cleared |
| PL-032n/o | **Partial** — cache+match landed widget.atr; stub not enough for actor VT |
| PL-032p | **Partial↑** — 0x11C200 cleared; wall = SIF sid 0x54323 |
| PL-032q | Bind/HLE sid 0x54323 + clear 0x2F/0x3E storm → lit>>20k |
| GX-062 | First-area textures |
| PL-053 | Title→game first level |

## Related

- Scoreboard row: [`SCOREBOARD.md`](SCOREBOARD.md)
- Plan: [`POST_MENU_PHASE_PLAN.md`](../POST_MENU_PHASE_PLAN.md) S7 · [`GRAPHICS_PIPELINE_PHASE_PLAN.md`](../GRAPHICS_PIPELINE_PHASE_PLAN.md) GX-008
- Issue family: #19 SearchFile / STREE stream
