# God of War (USA) — commercial title port

| Field | Value |
|-------|--------|
| **Title** | God of War (USA) |
| **user-media id** | `god-of-war` |
| **Serial / BOOT2** | `SCUS_973.99` |
| **ISO** | `C:/Users/xxraz/Downloads/GodofWar(USA).iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-god-of-war.json` |
| **Worktree** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-gow-w9` |
| **Branch** | `agent/menu-gow-w9` |
| **ROMDIR gate** | **CLOSED** |
| **Status** | **Wave-9 residual:** type-2 FULL-stream + fill enter `0x27E0CC` (streamPast=True) + full Fedo **R_SHELL** LoadWad bind restored; still **MENU NO** — Soft-GS **px=0 gifPath3=0** (FRAME_1=0; Path3MaskedByVif held) |
| **Last updated** | 2026-07-31 |

### MENU gate

**first-gs-interactive** = Soft-GS **px>0 non-black** + pad interactive surface — **not** MK MAINMENU.

### Wave-9 evidence (agent/menu-gow-w9)

#### Claim 100M (SEMA_STALL_YIELD OFF) — Soft-GS

```
@100M: PC=0x26C3A4 (stream-work mid-pack) px=0 gifPath2=1082 gifPath3=0 dmac=28
       cdvdSectors=692 sifBytes~22k syscalls~75k
       type-2: plant gates @37M; WaitSema→fill 0x27E0CC @40.2M s1=slot0;
               complete @41.7M streamPast=True epi=False resWas=0x8101006F→0
       R_SHELL.WAD full 0xBAA95 @0x01E00000 Fedo magic 0x4665646F OK
       TIT1E1_2.VPK @0x01D00000; LoadWad bind + streamObj* =0x100 magic
       Path3MaskedByVif + high-TADR END held
```

Wave-9 assist changes (on w8b FULL-stream base; wave-8 LoadWad was lost in 8b):

1. **Restore full R_SHELL** host extract (`maxBytes=0xC0000`, TOC size `0xBAA95`) + TIT1.
2. **Restore `TrySeedLoadWadBind` / `TryPreType2FileIo`** — table `+0x800` payload ptrs, stream slot, name scratch, `*0x2AC7D0` flip/kick enable.
3. **streamObj layout fix** — disasm `0x26C2C4` requires `*obj==0x100` magic (payload at +4); wrong `*obj=payload` → poison `0x2A1360` UnknownSyscall.
4. **Do not zero `*0x2A1358` after LoadWad seed** (w5b null-skip undid bind).
5. **No PostWait rewind** after type-2 mid-body (WaitSema leaf was force-rewinding every 200k).
6. **Remove soft-ok** on real fill helpers `0x282208` / `0x282710` / `0x281E30` / `0x27DCC8` / `0x27DED8` (they arm stream work; soft-ok left FRAME_1=0).
7. **WaitSema→fill plant** `SavedPc=0x27E0CC` + `s1=slot0@0x2A3318`; do not stomp fill PC when s1≠0x310000 (was IsWorkerFramePoison→PostWait).
8. **Sticky streamPast** + complete only ≥1.5M after fill enter (claim100g same-cycle complete fixed).
9. Path3MaskedByVif + high-TADR END **not** ungated. StartThread classic (no Haven $ra plant).

Rejected:

- Mid flip-kick jump `0x140A04` → `0x1838A4` spin.
- Force-post worker type-3/4.
- Inventing PATH3 GIF packets / fake Soft-GS pixels.
- Ungating Path3MaskedByVif.
- WaitSema global fabricate / SEMA_STALL_YIELD ON.

### How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / MOD_LOAD IRX | **Yes** (binds=10) |
| CDVD IRX-only 142 | **Broken** → **cdvd=692** |
| Worker cmd type=2 soft-success | **Yes** (streamPast=True @41.7M) |
| PART1.PAK / TOC FILEIO open | **Yes** (pre+post type-2) |
| R_SHELL Fedo host extract (full) | **Yes** (`shellOk=True`) |
| LoadWad bind seed | **Yes** (0x100 magic + table +0x800) |
| Type-2 fill enter 0x27E0CC | **Yes** (planted; sticky streamPast) |
| Stream-work path 0x26Cxxx | **Yes** (final PC mid-pack) |
| **gifPath3** | **No** (gifPath2=1082) |
| Soft-GS px>0 | **No** (FRAME_1=0) |
| Interactive title surface | **No** |

### Wall / next

1. Worker queue still empty after type-2 (`*0x310384=0`) — nothing enqueues type-3+/shell draw cmd.
2. Fill helpers now run natural but do not arm FRAME/PATH3 (slot/graph still incomplete for decode→PRIM).
3. Stream-work `0x26C3xx` packs bytes but never issues GIF; keep Path3MaskedByVif.
4. Soft-GS px>0 non-black then pad.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-gow-w9
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-gow-w9/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
```
