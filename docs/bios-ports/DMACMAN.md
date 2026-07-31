# DMACMAN port — gap analysis (contract HLE)

**Agent:** DMACMAN (generic BIOS HLE only — zero title PCs / commercial hacks)  
**Date:** 2026-07-30  
**Worktree:** `subagent-019fb1c8-c40b-7830-a67e-ea1084c64177`

## Authority

| Source | Status |
|--------|--------|
| `docs/BIOS_DISSECTION.md` §1 DMACMAN, §2 IOPBTCONF, §6.3 SIFMAN | Present |
| Sibling `detps2/tools/bios-extract/DMACMAN.bin` | **Absent** (not extracted yet; ROMDIR size 14069) |
| Sibling `detps2/tools/bios-decomp/DMACMAN_ALL.txt` | **Absent** (no Ghidra dump) |
| ps2sdk `iop/system/dmacman/include/dmacman.h` | Fetched (channel map, CHCR flags, import ordinals 4–35) |
| ps2sdk `iop/system/dmacman/src/dmacman.c` | Fetched (SCE SDK 1.3.4-based recreation; full export bodies) |
| ps2sdk `iop/system/dmacman/src/exports.tab` | Fetched (`dmacman` 1.2 table) |
| Existing EE `Dmac.cs` | EE 10-channel DMAC — **must not thrash**; IOP side is separate |
| Existing `Sif.cs` | Abstract EE↔IOP transport (SIFMAN functional stand-in, §6.3) |

**IOPBTCONF order (verbatim):**  
`… → SSBUSC → **DMACMAN** → TIMEMANP → TIMEMANI → … → SIFMAN → …`

DMACMAN is **not** an EE RPC server (no sid). Other IRX modules **import** library `dmacman` via LOADCORE after IOPBTCONF has already registered it.

## Namespace / architecture

| Side | Owner | Surface |
|------|-------|---------|
| EE DMAC (VIF/GIF/SIF0/1/SPR…) | `Dmac.cs` | MMIO `0x1000_8xxx`, INTC DMAC IRQ |
| IOP DMAC regs (`0xBF8010xx` / `0xBF8015xx`) | Hardware model | **Missing** — SIFMAN would poke these on R3000 |
| dmacman IRX exports | **This agent** | `IopDmacManHost` contract HLE |
| EE↔IOP byte transport | `Sif.cs` / `RealSifRpc` | Unchanged; do not reimplement SIFMAN as raw DMAC |

## Real contracts (ground truth)

### Export library (`dmacman` v1.1/1.2 — exports.tab)

| Ordinal | Symbol | Notes |
|--------:|--------|-------|
| 0 | `_start` | Register library; DPCR=0x07777777, DPCR2=0x07777777, DPCR3=0x777; zero ch 0–0xC; BF801578=1 |
| 2 | `dmacman_deinit` | Clear TR on active ch; BF801578=0 |
| 4–5 | `dmac_ch_set/get_madr` | MADR; store masks `addr & 0xFFFFFF` on SetSlice |
| 6–7 | `dmac_ch_set/get_bcr` | BCR = `(size & 0xFFFF) \| (count << 16)` |
| 8–9 | `dmac_ch_set/get_chcr` | CHCR; TR = bit 24 (`0x1000000`) |
| 10–11 | `dmac_ch_set/get_tadr` | **SPU (4) and SIF0 (9) only** |
| 12–13 | `dmac_set/get_4_9_a` | Extra reg SPU/SIF0/SIF1 |
| 14–19 | `dmac_set/get_dpcr{,2,3}` | Priority + enable fields |
| 20–23 | `dmac_set/get_dicr{,2}` | Interrupt control |
| 24–27 | `dmac_set/get_BF80157{C,8}` | 578 = master enable |
| 28 | `sceSetSliceDMA` | Setup slice; **0** if ch≥0xD or OTC; **1** ok |
| 29 | `dmac_set_dma_chained_spu_sif0` | SPU/SIF0; CHCR=`0x601` |
| 30 | `dmac_set_dma_sif0` | SIF0 only; CHCR=`0x701` |
| 31 | `dmac_set_dma_sif1` | SIF1 only; CHCR=`0x40000300` |
| 32 | `sceStartDMA` | `CHCR \|= 0x1000000` if ch &lt; 0xF |
| 33 | `sceSetDMAPriority` | 3-bit field in DPCR/DPCR2/DPCR3 |
| 34–35 | `sceEnable/DisableDMAChannel` | Enable bit in DPCR family |

### Channel map (IOP)

| Ch | Name | Typical consumers |
|---:|------|-------------------|
| 0–1 | MDECin/out | Video decode |
| 2 | SIF2 | GPU (PS1-compat) |
| 3 | CDVD | CDVDMAN sector DMA |
| 4 | SPU | Sound |
| 5 | PIO | |
| 6 | OTC | **rejected** by SetSliceDMA |
| 7 | SPU2 | |
| 8 | DEV9 | HDD/network |
| 9 | SIF0 | IOP→EE (SIFMAN) |
| 10 | SIF1 | EE→IOP (SIFMAN) |
| 11–12 | SIO2 in/out | SIO2MAN / PAD / MC |
| 13–15 | FDMA0–2 | Priority only |
| 67 / 85 | CPU / USB | Priority / enable only |

### CHCR / SetSlice semantics (dmacman.c)

```
sceSetSliceDMA(ch, addr, size, count, dir):
  if ch >= 0xD || ch == OTC: return 0
  MADR = addr & 0xFFFFFF
  BCR  = (size & 0xFFFF) | (count << 16)
  CHCR = (dir & 1) | 0x200 | (dir == 0 ? 0x40000000 : 0)
  return 1

sceStartDMA(ch):
  if ch < 0xF: CHCR |= 0x1000000   // TR
```

Direction: `DMAC_TO_MEM=0`, `DMAC_FROM_MEM=1`.

## Pre-port DetPS2 surface

| Area | Status | Gap |
|------|--------|-----|
| ROMDIR name `DMACMAN` | Registered in `BiosBootHost` | Role string only |
| EE `Dmac.cs` | Working for EE channels | Unrelated to IOP dmacman |
| IOP DMAC MMIO | Missing | SIFMAN literal port blocked (§6.3) |
| dmacman exports | **Missing** | SIF/CDVD/SIO2 IRX import stubs would hang |
| FinishIopServices plant | No | `_start` DPCR defaults not applied |
| SaveState | N/A | No IOP DMAC state |

## Landed (waves + Phase 2 deepen)

1. **`IopDmacManHost`** — contract HLE of exports 4–35 + `_start` / deinit defaults.  
2. **Channel state** (MADR/BCR/CHCR/TADR/49A) for ch 0–12; DPCR/DPCR2/DPCR3/DICR*/BF801578/57C.  
3. **`SetSliceDma` / `SetDmaSif0` / `SetDmaSif1` / `SetDmaChainedSpuSif0`** match return codes and CHCR constants.  
4. **`StartDma`** sets TR then **completes immediately** (clears TR) so pollers and enable paths do not hang without IOP MMIO.  
5. **`EnableDmaChannel` / `DisableDmaChannel` / `SetDmaPriority`** update DPCR bitfields exactly as ps2sdk.  
6. **`BiosBootHost.FinishIopServices`** calls `IopDmacMan.Start()` after INTRMAN plant (IOPBTCONF-after-SSBUSC).  
7. **`Ps2System.IopDmacMan`** property + `Reset()` wiring.  
8. **Phase 2 (AGENT-I):** `RequestChannel` / `ReleaseChannel` lifecycle; DICR/DICR2 IE→IF on complete; `SetChannelInterruptEnable` / `IsChannelInterruptPending` / `AcknowledgeChannelInterrupt`; `IsTransferActive`.  
9. **Smoke** `BiosHle_IopDmacManContracts` (boot plant, SetSlice, SIF0/1, enable/priority, OTC reject, Start complete, Request/Release, DICR IF, Deinit).  
10. **Zero game hacks** / no Midway / no title PCs; EE `Dmac` / `Sif` left intact.

**Gate:** DMACMAN → **OK** (contract HLE + smokes; residual = no physical MMIO / no async cycle complete / no Ghidra dump).

### Intentional HLE divergences

| Divergence | Why |
|------------|-----|
| No physical `0xBF8010xx` MMIO | Project has no IOP DMAC hardware model; SIFMAN remains abstract (`Sif.cs`) per §6.3 |
| StartDMA completes immediately | Without IOP IRQ/MMIO, leaving TR set forever hangs any CHCR poller |
| No real byte copy on Start | EE↔IOP bytes already moved by `Sif` / RPC paths; dmacman HLE is enable/setup contract for import tables |
| DICR IF latched but no INTRMAN irq-3 pulse | Bookkeeping only until IOP INTC MMIO; consumers can poll IF bits |
| Ghidra retail not reconciled | No `DMACMAN.bin`/`_ALL.txt` in-tree; ps2sdk is SCE 1.3.4 recreation of the same ABI |

## Remaining gaps (non-blocking)

Ordered by contract value:

1. **Extract + Ghidra `DMACMAN.bin` / `DMACMAN_ALL.txt`** — confirm retail export ordinals and any post-1.3.4 deltas vs ps2sdk.  
2. **IOP DMAC MMIO window** (`0xBF801080`–`0xBF8015F0`) shared with this host so SIFMAN-style register pokes and dmacman APIs see one truth.  
3. **Async StartDMA** — schedule complete after N IOP cycles; pulse IOP DMA IRQ (INTRMAN irq 3) via `IopSystemHost.RaiseIntr`.  
4. **LOADCORE export registry plant** — publish a synthetic `dmacman` export table so `IrxLoader` import resolution binds ordinals to HLE thunks if R3000 IRX runs.  
5. **Wire CDVDMAN / SIO2MAN HLE** to call `RequestAndStart` when those ports grow past RPC-only stubs.  
6. **SaveState** for DPCR + per-channel regs if commercial mid-transfer resume ever needs IOP DMA.  
7. **Real R3000 execution of DMACMAN.IRX** — retires this host when IOP BIOS modules run.

## Acceptance

- `IopDmacMan.Started` after `StartCommercialIop`.  
- SetSliceDMA valid ch → 1; OTC / out-of-range → 0.  
- StartDMA leaves CHCR.TR clear; `CompleteCount` increments.  
- Enable SIF0 sets DPCR2 bit `0x800`; SetDMAPriority updates field.  
- Request/Release lifecycle clears regs + enable bit.  
- DICR IE → IF on complete; Acknowledge clears IF.  
- EE `Dmac` / `Sif` smokes still green; no commercial title hacks.
