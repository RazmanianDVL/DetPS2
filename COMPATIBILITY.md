# DetPS2 Compatibility Tracker

**Last updated**: 2026-07-27 (real commercial bring-up in progress — see `docs/DEVELOPER_GUIDE.md`;
full per-title status and cross-title triage on the [GitHub wiki](https://github.com/RazmanianDVL/DetPS2/wiki))

This document tracks boot/runtime compatibility. DetPS2 **v0.1.0** ships engineering tooling for dumps and netplay; **no commercial title has reached its main menu yet**. Most entries below describe automated tests and synthetic fixtures only (no copyrighted dumps) — the one real-dump attempt in progress is called out explicitly.

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
| Mortal Kombat: Shaolin Monks (`SLUS_210.87`) | Retail | **In progress** | Its long-standing `Exit(1)` crash is fixed (was a garbage-execution artifact, not a real panic); now stalls at a real wait (`0x00212DD0`), unchanged 5M-900M cycles — see `docs/DEVELOPER_GUIDE.md` §7.10-7.11 |
| Vexx (`SLUS_203.83`) | Retail | **In progress** | Most active title tested — 274K+ syscalls, 5.8MB real SIF traffic (entirely from *general* fixes, no Vexx-specific work) before stalling ~100M-200M cycles |
| Blood Omen 2 (`SLUS_200.24`) | Retail | **In progress** | Real `Exit(1)` crash before 20M cycles (confirmed distinct from Shaolin Monks' now-fixed mechanism), untraced |
| Burnout 3: Takedown (`SLUS_210.50`) | Retail | **In progress** | Stalls ~20M cycles on a shared SN Systems ProDG SDK wait-flag routine — same bug also affects MK: Deadly Alliance and MK: Deception |
| God of War (`SCUS_973.99`) | Retail | **In progress** | Stalls ~20M cycles on one identified, unimplemented MMI instruction — likely the cheapest fix of any title tested |
| Haven: Call of the King (`SLUS_205.17`) | Retail | **In progress** | Real progress to 100M cycles, then stalls, untraced |
| Mortal Kombat: Deadly Alliance (`SLUS_204.23`) | Retail | **In progress** | Same shared SDK wait-flag bug as Burnout 3 |
| Mortal Kombat: Deception (`SLUS_208.81`) | Retail | **In progress** | Same shared SDK wait-flag bug as Burnout 3 |
| Whiplash (`SLUS_206.84`) | Retail | **In progress** | Stalls ~20M cycles, no rendering activity, untraced |
| Other commercial game ISOs | Retail | **Untested** | Fixes found via Shaolin Monks are general (kernel/HLE), so likely to help broadly |

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
