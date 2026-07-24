# Rollback Netplay Protocol (DetPS2)

**Status**: Spec for Phase 34 (scaffolding after Phase 33 snapshots).  
**Mode**: **Det only** — software GS truth, bit-identical EE (interp or parity-tested JIT).

## Goals

- GGPO-style predict / confirm / rollback  
- Default window **R = 8** frames (configurable 4–12)  
- Per-frame desync hash  
- LAN then WAN  

## Frame loop

1. Predict local input for frame `f`.  
2. Send input for `f` to peer.  
3. Advance simulation one frame quantum with predicted remote input.  
4. Save delta snapshot at `f`.  
5. When remote input for `f-k` arrives and differs:  
   - `LoadStateAtFrame(f-k)`  
   - Re-apply confirmed inputs through `f`  
   - Present latest frame  

## Desync hash (wire)

Include at least:

- `MasterCycles`  
- EE `PC`  
- Pad buttons (merged)  
- Checksum of RDRAM pages marked dirty (or full FNV of selected regions)  

Mismatch → stop session, dump JSON (inputs, hashes, build id, PC).

## Transport

- UDP for input frames  
- Reliable channel for handshake / session config  
- See `NetplayFrameMsg` evolution for wire layout  

## Non-goals

- Perf/HW-GS mode on the wire  
- Host-time in core rollback path  
