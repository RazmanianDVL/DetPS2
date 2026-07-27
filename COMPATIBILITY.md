# DetPS2 Compatibility Tracker

**Last updated**: 2026-07-27 (real commercial bring-up in progress — see `docs/DEVELOPER_GUIDE.md`)

This document tracks boot/runtime compatibility. DetPS2 **v3.0** ships production tooling for dumps and netplay; **retail game majority is not claimed** without your legal BIOS/ISOs. Entries describe automated tests and synthetic fixtures only (no copyrighted dumps).

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
| Mortal Kombat: Shaolin Monks (`SLUS_210.87`) | Retail | **In progress** | Boots past logo into real gameplay/menu-adjacent code (100M+ cycles of genuine SIF activity); blocked on a traced runtime-library registry bug — see `docs/DEVELOPER_GUIDE.md` |
| Other commercial game ISOs | Retail | **Untested** | Not yet attempted; fixes found via Shaolin Monks are general (kernel/HLE), so likely to help broadly |

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
