# DetPS2 Compatibility Tracker

**Last updated**: 2026-07-31 (MENU YES **9/9** Soft-GS campaign — see `docs/title-ports/SCOREBOARD.md` and `docs/POST_MENU_PHASE_PLAN.md`;
full per-title status on the [GitHub wiki](https://github.com/RazmanianDVL/DetPS2/wiki))

This document tracks boot/runtime compatibility. DetPS2 **v0.1.0** ships engineering tooling for dumps and netplay. **Commercial Soft-GS MENU YES is 9/9** on tip (scoreboard `menuKind` bars) — **not** fully playable; residuals and next gates live in **`docs/POST_MENU_PHASE_PLAN.md`**. Synthetic fixtures remain the automated CI path (no copyrighted dumps in-repo).

## Legend

| Status | Meaning |
|--------|---------|
| **Pass** | Automated test green |
| **Partial** | Loads / runs subset without full title behavior |
| **Stub** | Interface present; behavior incomplete |
| **Fail** | Known broken |
| **Untested** | Not run |

## Software / paths (v1.0 campaign)

| Title / path | Type | Status | Notes |
|--------------|------|--------|-------|
| Built-in homebrew GS demo ELF | Synthetic homebrew | **Pass** | HLE clear/draw/exit. `Homebrew_Elf_DrawsGsFrame`, `TitleFixtures.RunHomebrewGsDemo` |
| Synthetic ISO + SYSTEM.CNF + BOOT.ELF | Disc boot | **Pass** | `SystemCnf_Iso_BootLoadsElf`, `TitleFixtures.RunIsoBoot` |
| Multi-dir ISO (MODULES/…) | Disc layout | **Pass** | `Iso_MultiDir_Lookup`, campaign `iso-multidir-modules` |
| Input tape replay | Tooling | **Pass** | Identical FB hash. `InputReplay_IdenticalHash`, campaign |
| Stub BIOS harness | BIOS stub | **Pass** | `RunBiosHarness` / BootTrace |
| In-memory netplay lockstep | Netplay | **Pass** | `Netplay_InMemory_LockstepSync` |
| Real PS2 BIOS dump | BIOS | **Partial** | Load path + expanded HLE; verified against a real user-supplied dump |
| Public domain / ps2dev homebrew | Homebrew | **Partial** | Loader + HLE + more ISA; title-dependent |
| Mortal Kombat: Shaolin Monks (`SLUS_210.87`) | Retail | **MENU YES + INTERACTIVE YES** | Soft-GS mk-mainmenu (gifP3=18 px≈966k) + PL-011 sel-idx 0..4 + accept latch. Residual: natural texture DMA / AnimMenuGUI natural submenu. |
| Vexx (`SLUS_203.83`) | Retail | **MENU YES** | Soft-GS title-surface (STREE0 VFS). Residual: richer frontend TRE members. |
| Blood Omen 2 (`SLUS_200.24`) | Retail | **MENU YES** | Soft-GS title-surface. Residual: multi-prim IMAGE/DISPFB chrome. |
| Burnout 3: Takedown (`SLUS_210.50`) | Retail | **MENU YES** | Soft-GS logo-frontend (px multi-M). Residual: DISPFB + pad main-menu advance. |
| God of War (`SCUS_973.99`) | Retail | **MENU YES** | Soft-GS first-gs (Path2 sticky + ofx expand). Residual: Fedo shell decode, IRX-only stream class. |
| Haven: Call of the King (`SLUS_205.17`) | Retail | **MENU YES** | Soft-GS title-surface + NUSOUND. Residual: IMAGE chrome. |
| Mortal Kombat: Deadly Alliance (`SLUS_204.23`) | Retail | **MENU YES** | Soft-GS midway-menu keep-alive. Residual: fail-tail plants / richer chrome. |
| Mortal Kombat: Deception (`SLUS_208.81`) | Retail | **MENU YES** + **INTERACTIVE** (assist sel-idx) | Soft-GS midway-menu (p2qws≈5988). PL-012 pad + **PL-029** Host→Local gameart SEC tiles **imgBytes=557056**. Residual: natural EE IMAGE + AnimMenu accept. See `docs/title-ports/MK_DECEPTION.md`. |
| Whiplash (`SLUS_206.84`) | Retail | **MENU YES** | Soft-GS title-surface. Residual: full texture path; WHIP WaitSema fabricate. |
| Other commercial game ISOs | Retail | **Untested / free-ride** | Next free-ride target e.g. SotC — post-menu plan P7. |

## Subsystems

| Subsystem | Status | Notes |
|-----------|--------|-------|
| ELF load | **Pass** | PT_LOAD, BSS, GP |
| BIOS / kernel HLE | **Partial** | Graph, pad, FIO RPC, threads, semas, WaitVblank, LoadExec |
| ISO9660 | **Pass** (subset) | Multi-dir Level-1 |
| CDVD | **Partial** | Sync + async + IRQ stand-in |
| Pad | **Pass** | Digital + analog + RPC status |
| SPU2 | **Partial** | Square mix + sink; no ADPCM device |
| EE COP0 / exceptions | **Pass** | MFC0/MTC0, ERET, IRQ vector |
| EE ISA | **Partial** | MULTU/DIVU, DSLL*, likely branches, MMI subset |
| VU1 Path1 | **Partial** | XgKick |
| GS | **Partial** | Software prims, PSMCT32/16 |
| SIF RPC | **Pass** (stubs) | FILEIO/PADMAN/CDVDMAN registry |
| Netplay | **Pass** (TCP + UDP + in-mem) | Rollback soak cert synthetic; N4 UDP prototype |
| Present | **Pass** | Software + GPU + Vulkan-shaped path; Det = soft GS |
| IPU / FMV | **Partial** | SkipFMV + MPEG header stub; not full MPEG |
| Majority campaign | **Pass** (synthetic) | ≥70% scored gate; commercial needs dumps |
| Save states | **Pass** | v4 Deflate + delta snapshots |

## PC trace notes (synthetic)

Campaign reports include short PC samples after boot/run. Example pattern for homebrew demo: entry `0x00100000` → exit near `0x00100064` after HLE syscalls.

## Known blockers (post-v1.0)

1. Incomplete R5900 / COP1 / full MMI  
2. Real IRX ELF loading  
3. Commercial GS/VU timing  
4. SPU2 ADPCM + host audio device  
5. Full BIOS kernel path without heavy HLE  

## How to add a note

Append a row: date, dump type, cycles, last PC, symptoms, new HLE/MMIO required.
