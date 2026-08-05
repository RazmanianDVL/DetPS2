# Playing DetPS2 (BIOS + ISO)

Work from the **Grok worktree** (not necessarily your original clone):

```text
C:\Users\user\.grok\worktrees\windows-detps2\detps2
```

## Launch

```powershell
cd C:\Users\user\.grok\worktrees\windows-detps2\detps2
pwsh ./launch.ps1
```

Or run the exe directly after a build:

```powershell
.\src\DetPS2.Desktop\bin\Release\net9.0\DetPS2.Desktop.exe
```

A window should open. If it closes immediately, something crashed on startup — re-run `launch.ps1` and read any red error text.

## First-time setup (all inside the app — no manual config edit)

Settings auto-save to:

```text
%LocalAppData%\DetPS2\config.json
```

On the **left Media Library panel**:

1. Click **📁 Choose media folder…** → pick the directory with your ISOs/ELFs.  
2. Click **💾 Choose BIOS file…** → pick your legal PS2 BIOS.  
3. Select a title in the list → **▶ Boot selected game** (or double-click).  
4. Emulation should auto-run; **Pause** = F6, **Run** = F5.

Toolbar buttons **Media Folder** / **Set BIOS** / **Boot** do the same things.

You should **not** need to hand-edit `config.json` for normal use.

### Memory cards (later)

Config reserves `MemCardPath`, defaulting to:

- `{MediaFolder}\memcards\slot1.ps2` when a media folder is set  
- else `%LocalAppData%\DetPS2\memcards\slot1.ps2`  

Full memcard integration is not required for first boots.

## What to expect

- Boot is **direct**: read `SYSTEM.CNF` → load BOOT.ELF into the EE.  
- Many retail discs need more HLE/accuracy; you may get a black screen or hang.  
- Use the log + **Last boot** / **EE PC** in the right panel when reporting issues.  
- **CSO** files may appear in the list but are **not** bootable yet (use ISO).

## Controllers

**Library → Controllers (P1/P2)…** or **🎮 Controllers P1/P2…**:

- Detects **XInput** (Xbox), **HID DualShock 4 / DualSense**, and **guitar-class** devices (Riffmaster, GH/RB guitars via VID/name)
- Assign a **device** per player
- **Controller type** per player:
  - **Standard** — normal DualShock-style pad
  - **Guitar Hero / Riffmaster** — frets → R2/○/△/✕/□, strum → D-pad U/D, whammy → right stick Y  
    (so you can leave the physical guitar selected and flip P1 into GH mode without re-plugging)
- Saved in AppData

Keyboard still works for P1 when device = “Keyboard only”:

| Keys | Action |
|------|--------|
| WASD / Arrows | D-pad |
| Z / X / C / J | Cross / Circle / Triangle / Square |
| Enter / Shift | Start / Select |
| Q / E | L1 / R1 |

## Network (UNC) media folders

If the folder picker cannot reach a NAS share:

1. **Library → Enter Network/UNC Path…** (or **🌐 Enter network path**)
2. Type e.g. `\\NAS\PS2\ISOs` or a mapped drive `Z:\Games`
3. Rescan if you add more discs later

Large ISOs (**3–5 GB+**) load by **streaming** (not into a 2 GB byte array).

## Media check

On boot, DetPS2 reads `SYSTEM.CNF`, looks for a PS2 serial (SLUS/SLES/…), and computes a **quick hash** (not full redump MD5 of the whole disc). Optional online serial-list cache when network is available.

## Legal

Use only BIOS and game dumps you legally own. DetPS2 does not ship copyrighted media.
