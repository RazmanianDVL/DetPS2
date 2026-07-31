# God of War (USA) — commercial title port

| Field | Value |
|-------|--------|
| **Title** | God of War (USA) |
| **user-media id** | `god-of-war` |
| **Serial / BOOT2** | `SCUS_973.99` |
| **ISO** | `C:/Users/xxraz/Downloads/GodofWar(USA).iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-god-of-war.json` |
| **Worktree** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-gow-w11` |
| **Branch** | `agent/menu-gow-w11` |
| **ROMDIR gate** | **CLOSED** |
| **Status** | **Wave-11 residual:** Fedo R_SHELL disasm + shell decode consumer seed + retail 0x27D7C8 restore; **MENU NO** — Soft-GS **px=0 gifPath3=0 FRAME_1=0** (Path3MaskedByVif held) |
| **Last updated** | 2026-07-31 |

### MENU gate

**first-gs-interactive** = Soft-GS **px>0 non-black** + pad interactive surface — **not** MK MAINMENU.

### Wave-11 evidence (agent/menu-gow-w11)

#### Fedo / R_SHELL layout (disasm + ISO)

- `R_SHELL.WAD` TOC size **0xBAA95** @ PART1 off **0x36ED10**
- Magic LE **`0x4665646F`** ("odeF" bytes) + version u16 `"2","1"`
- Hashed name table of shell entities: `goSKCDiveHit`, `goSKCflasher`, `goSKFkillB`, …
- **No** raw A+D `FRAME(0x4C)` / `PRIM` in host buffer — gzip/zlib payloads embed later
- Retail expand required before Soft-GS can paint

#### LoadWad / decode consumer (SCUS_973.99)

| Addr | Role |
|------|------|
| `0x1BBEE8` | thin LoadWad(`"R_Shell"`) → `0x1BB7E8` |
| `0x1BB7E8` | walks wad contexts `*0x335280` (name at **+0**, flags **+0x68** bit0) |
| `0x21E494` | first-load SM calls LoadWad with `a0=0x2AE550` |
| `0x27DBF0` | stream-follow: loop `jal 0x27D7C8` while cursor &lt; `*(slot+0x170)` |
| `0x27D7C8` | per-item **handle resolver** (not Fedo inflate) |
| `0x13F40C` | GS dirty packer writes A+D **FRAME** when dirty bit `0x1000` |
| `0x27C4xx` | worker cmd posters need `*0x2A3310≠0` + id table `0x322748` |

#### Claim 100M (SEMA_STALL_YIELD OFF) — Soft-GS

```
@100M: px=0 prims=0 gifPath2=1082 gifPath3=0 dmac=28 spu2Samples=32552
       cdvdSectors=1202 (full R_SHELL+TIT1 host)
       type-2: fill pin + dbf0Seen + complete @~39.85M (no permanent soft-ok plant)
       R_SHELL Fedo OK @0x01E00000; streamObj magic=0x100; *0x2A3310=1
       wadCtx name-leading "R_Shell" @0x335280; retail 0x27D7C8 restored post-type2
       softgs-regs: FRAME_1=0 DISPFB1=0x800005090D0 SCISSOR full XYOFFSET=0 TEST=0
       prims=0 fragTest=0 — Path2 setup only (no XYZ / no FRAME write)
       Path3MaskedByVif + high-TADR END held
```

Wave-11 assist changes:

1. **Disasm Fedo + LoadWad** — documented layout; no invented PATH3/GIF/FRAME plant.
2. **`TrySeedShellDecodeConsumer`** — `*0x2A3310=1`, id table `0x322748`, name-leading wad contexts at `0x335280` (`"R_Shell"` at +0, flags +0x68=1), host Fedo handles re-published.
3. **`TryRestoreRetailStreamItemResolver`** — after type-2 complete, restore original `0x27D7C8` / `0x27DCC8` (soft-ok was hang-avoid only during type-2).
4. **Rejected force PC→LoadWad** — claim1 jumped into streamObj/TIT1 (`pc=0x01CFE008`); hang-guard data-as-code → stream poll only.
5. Path3MaskedByVif **held**. No invent type-3/4. No StartThread re-break.

Rejected:

- Inventing PATH3 GIF / fake Soft-GS pixels / FRAME plant.
- Ungating Path3MaskedByVif.
- Force-post worker type-3/4.
- Permanent soft-ok of `0x27DBF0`.
- Force PC into LoadWad with incomplete queue consumer (data-as-code).

### How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / MOD_LOAD IRX | **Yes** |
| CDVD past IRX-only | **Yes** (cdvd=1202) |
| Worker type-2 success | **Yes** |
| R_SHELL Fedo host extract | **Yes** (`shellOk=True`) |
| streamObj magic 0x100 | **Yes** |
| `*0x2A3310` stream-ready | **Yes** (w11) |
| Wad ctx name-leading R_Shell | **Yes** (w11) |
| Retail 0x27D7C8 restored post-type2 | **Yes** (w11) |
| LoadWad natural expand → FRAME | **No** |
| Soft-GS px>0 | **No** (FRAME_1=0, prims=0) |
| Interactive title surface | **No** |

### Wall / next

1. **Natural LoadWad queue consumer** past `0x1BB0F8` ring (`*0x2ACC40`) so Fedo expand runs without PC force.
2. **Display dirty bit 0x1000** so retail packer `0x13F40C` writes FRAME after shell objects exist.
3. **PRIM+XYZ** after FRAME — Soft-GS cannot paint without prims (truth).
4. Path3MaskedByVif held.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-gow-w11
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-gow-w11/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
```
