# SIF bridge — EE ↔ IOP path (WP-19 / Track T4)

**Status:** foundation for executing SIFMAN (WP-20)  
**Owned code:** `src/DetPS2.Core/Sif.cs` (primary); mailbox/DMA only in `SifRpc.cs` if required  
**Related:** `SystemMemory` IOP SIF window, `MmioBus` EE SIF window, `SonyKernelHle.PerformSifSetDma` / `DeliverIopSifCmdToEe`, `Dmac` SIF0/SIF1  
**Env:** `DETPS2_LITERAL_IRX=1` (target); `DETPS2_TRACE_SIF_HLE=1` (optional HLE-bypass log)

---

## 1. Hardware model (what must be true)

On real PS2 the EE and IOP are **separate chips** joined only by the **SIF / SBUS**.  
CDVD, SPU2, pad/memcard live on the **IOP sub-bus**. The EE never talks to them except by
relay through IOP software (SIFMAN + SIFCMD + service IRX).

DetPS2 keeps a single `Sif` object as the shared mailbox + DMA engine. Two address windows
map onto it:

| Side | Window | Access path |
|------|--------|-------------|
| **EE** | `0x1000F200`–`0x1000F2FF` | `MmioBus` → `Sif.ReadRegister` / `WriteRegister` |
| **IOP** | `0x1D000000`–`0x1D0000FF` (ps2tek) | `SystemMemory.IopRead32` / `IopWrite32` → same `Sif` |

Shared RAM for bulk transfers:

| Side | Address of IOP RAM |
|------|--------------------|
| **EE** | `0x1C000000` + offset (`SystemMemory.IOP_RAM_BASE`) |
| **IOP** | physical `0x00000000` + offset (`IopRead*` / `IopWrite*`) |

Same `_iopRam` array — DMA that lands in IOP RAM is immediately visible to a stepping IOP core.

---

## 2. Register map (mailbox)

Offsets relative to both EE `0x1000F200` and IOP `0x1D000000`:

| Off | Name | Direction | Role in DetPS2 |
|-----|------|-----------|----------------|
| `+0x00` | **MSCOM** | EE → IOP | EE posts command; `SendCommand` also enqueues + raises SIF INTC |
| `+0x10` | **SMCOM** | IOP → EE | IOP reply word (`WriteSmCom` / `IopPostMailboxReply`) |
| `+0x20` | **MSFLAG** | EE → IOP | EE flag bits (`SifSetReg(SIF_REG_MSFLAG)`) |
| `+0x30` | **SMFLAG** | IOP → EE | Boot/status bits; EE **W1C** via `SifSetReg(SIF_REG_SMFLAG)` |
| `+0x40` | **Status** | both | Busy / done / RPC pending bits (simplified) |
| `+0x50` | LastRpcResult | EE | Test / legacy simplified RPC |
| `+0x60` | RPC submit | EE | Queues simplified `SifRpcPacket` address |

### SMFLAG boot bits (ps2sdk `sifdma.h`)

| Bit | Constant | Who posts (real) | HLE stand-in |
|-----|----------|------------------|--------------|
| `0x10000` | `SIF_STAT_SIFINIT` | SIFMAN / SIFINIT | `Sif.ApplySifInit` / `PresentIopBootReady` |
| `0x20000` | `SIF_STAT_CMDINIT` | SIFCMD | `ApplyCmdInit` |
| `0x40000` | `SIF_STAT_BOOTEND` | EESYNC `SyncEE` | `PostBootEnd` |

See also `docs/bios-ports/SIFINIT_EESYNC.md`.

### Software regs (EE `SifGetReg` / `SifSetReg`, not raw MMIO)

| Id | Name | Notes |
|----|------|-------|
| 1 | `SIF_REG_MAINADDR` | EE receive buffer (CHANGE_SADDR) |
| 2 | `SIF_REG_SUBADDR` | IOP cmd buffer (`DefaultIopSifCmdBufAddr = 0x1F000`) |
| 3 | `SIF_REG_MSFLAG` | mirrors `Sif.MsFlag` |
| 4 | `SIF_REG_SMFLAG` | live `Sif.SmFlag` (W1C on write) |
| `0x80000000+n` | SYSREG | `SUBADDR` / `MAINADDR` / `RPCINIT` software copies |

---

## 3. EE → IOP path

```text
EE game / sceSif*
    │
    ├─ SifSetReg(MSFLAG / MAINADDR / …)     ──► SonyKernelHle 0x79 ──► Sif.MsFlag / _sifRegs
    │
    ├─ SifSetDma(SifDmaTransfer_t[])        ──► SonyKernelHle.PerformSifSetDma
    │       attr bit0 = 0  → EE→IOP
    │       │
    │       ▼
    │   Sif.Sif1EeToIop(eeSrc, iopDst, size)
    │       │  byte copy EE RDRAM → IOP RAM (NormalizeIopAddr)
    │       ▼
    │   IOP RAM visible to Iop.IopRead*  (and future SIFMAN/SIFCMD IRX)
    │
    └─ optional: SIFCMD packet in DMA payload
            │
            ▼
        HleSifCmdFromEe  (today: pure C# — BIND/CALL queued to _realRpcQueue)
            │
            ▼  (WP-20 target)
        executing SIFCMD/SIFMAN on IOP R3000
```

### Channel naming

| Name | Direction | DetPS2 API | EE DMAC ch |
|------|-----------|------------|------------|
| **SIF1** | EE → IOP | `Sif.Sif1EeToIop` | DMAC channel 6 |
| **SIF0** | IOP → EE | `Sif.Sif0IopToEe` | DMAC channel 5 |

`Dmac.DeliverSegment` hooks SIF0/SIF1 when games program channel CHCR/MADR/TADR/QWC.

---

## 4. IOP → EE path (reply)

```text
IOP (future SIFMAN / SIFCMD IRX, or HLE today)
    │
    ├─ Mailbox reply
    │     Sif.IopPostMailboxReply(smCom, smFlagBits)
    │     or IopWrite32(0x1D000010, smCom) / WriteSmCom
    │         → SmCom / SmFlag updated
    │         → Intc.Raise(Sif)  so EE handlers / WaitSema can progress
    │
    ├─ Bulk SIF0 DMA
    │     Sif.Sif0IopToEe(iopSrc, eeDst, size)
    │         → EE RDRAM holds reply bytes
    │         → EE DMAC-5 / SIF INTC (real games often AddDmacHandler(5))
    │
    └─ SIFCMD reverse packet (HLE today)
          SonyKernelHle.DeliverIopSifCmdToEe(cid, …)
              → writes 24B header+payload into MAINADDR buffer
              → EE _SifCmdIntHandler-style consumer
```

**Contract for WP-20:** executing SIFMAN may program IOP DMAC SIF0/SIF1 registers
(`IopDmacManHost` already exposes helpers). The **byte result** must still land through
`Sif.DoDmaTransfer` (or equivalent) so EE RDRAM and IOP RAM stay coherent. HLE DMA engine
underneath is OK; pure service HLE in `RealSifRpc` is **debt** under `LITERAL_IRX=1`.

---

## 5. Real RPC queue (async EE↔IOP)

Retail `sifrpc.c` BIND (`0x80000009`) / CALL (`0x8000000A`) / RDATA (`0x8000000C`) packets:

1. EE `SifSetDma` → `PerformSifSetDma` copies args EE→IOP, then `SubmitRealRpc(addr, gen)`.
2. **Not** answered in the same EE instruction.
3. `SonyKernelHle.DrainRealRpcQueue` (ambient `Ps2System` tick + opportunistic mid-slice) drains
   only packets with `generation < current`.
4. `RealSifRpc` completes and synthesizes **RPC_END** (`0x80000008`) side effects (packet free +
   `SignalSema` on client `sema_id`).

Under **literal IRX**, BIND/CALL should eventually be consumed by **executing SIFCMD** after
SIFMAN DMA; `RealSifRpc` remains fallback when `DETPS2_LITERAL_IRX=0` or until service IRX owns
the sid (WP-49 fail-fast).

---

## 6. Pure-HLE bypass points (flag under `LITERAL_IRX=1`)

These paths **do not** run IOP IRX. They are bridge / bisect debt:

| Site | What it does | Flag |
|------|--------------|------|
| `Sif.Step` | Simplified 16-byte `SifRpcPacket` → `IopModuleHost.Dispatch` | Comment + optional `DETPS2_TRACE_SIF_HLE` |
| `Sif.PlantEeSifReadySlots` | Writes `0x00778800` ready table | Comment — needed until live SIF0→handler |
| `Sif.Reset` / `PresentIopBootReady` | Pre-sets SMFLAG boot bits | Comment — until SIFMAN/SIFCMD/EESYNC exec |
| `SonyKernelHle.HleSifCmdFromEe` | INIT/RESET/SET_SREG without IOP core | Owned by T7/T4 bridge; WP-20 shrinks |
| `SonyKernelHle.DeliverIopSifCmdToEe` | Fakes IOP→EE SIFCMD into MAINADDR | Reply path stand-in for WP-19 |
| `RealSifRpc` handlers | Service HLE (FILEIO/PAD/CDVD…) | WP-30/WP-49 demote when IRX owns sid |

`Sif.LiteralIrxMode` is true unless `DETPS2_LITERAL_IRX=0`.

---

## 7. Exit tests (WP-19)

| Smoke | Covers |
|-------|--------|
| `Sif_DmaRoundTrip_UpdatesMemory` | SIF1 EE→IOP + SIF0 IOP→EE byte copy + cmd queue |
| `Sif_Bridge_MailboxAndDmaVisibleToIop` | EE mailbox write visible via IOP window; IOP reply visible to EE; DMA via IOP `IopRead*` |
| `BiosHle_SifInitEeSyncContracts` | SMFLAG / SUBADDR / reboot (SIFINIT+EESYNC) |

```powershell
# From detps2/
dotnet run --project Tests -- --filter Sif_
# or full smoke
dotnet run --project Tests
```

---

## 8. Next (WP-20)

1. Prefer **executing SIFMAN** for sifcmd transport (HLE `DoDmaTransfer` OK underneath).  
2. SIFCMD BIND/CALL drain from IOP instruction stream when module text is live.  
3. Keep `DeliverIopSifCmdToEe` / `RealSifRpc` only where IRX is absent or `LITERAL_IRX=0`.

---

## 9. File map

| File | Role |
|------|------|
| `Sif.cs` | DMA, mailbox regs, real-RPC queue, HLE Step, LITERAL_IRX flags |
| `SystemMemory.cs` | `IOP_SIF_BASE`, `IopRead*`/`IopWrite*` → `Sif` |
| `MmioBus.cs` | EE `0x1000F200` window |
| `Dmac.cs` | SIF0/SIF1 segment delivery |
| `SonyKernelHle.cs` | `SifSetDma` / `SifGetReg` / SIFCMD HLE + IOP→EE deliver |
| `RealSifRpc.cs` | Service HLE (not T4 primary; bridge only) |
| `IopDmacManHost.cs` | IOP-side SIF0/SIF1 channel helpers for future SIFMAN |
