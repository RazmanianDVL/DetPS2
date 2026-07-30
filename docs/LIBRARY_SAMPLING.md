# Library sampling (open fleet)

**Policy:** The games listed in `user-media.json` / title media configs are a **starting set**, not a lock.  
**Priority push:** Mortal Kombat: Shaolin Monks (MENU YES).  
**Everything else:** pull from the legal dump library when a dialect would teach SHARED HLE for many titles.

## Library path (this machine)

```text
\\192.168.0.17\ND\Emulation\Playstation 2
```

(~353 files, ~1 TB). Prefer **UNC** media JSON — **do not copy ISOs to C:**.

## When to pull a new title

| Reason | Example pull |
|--------|----------------|
| Close an engine family | MK Armageddon after DA/Deception |
| Sample another studio dialect | SotC / Ico / Jak / Ratchet next to GoW |
| Stress a thin HLE surface | MGS3 (heavy), Black, Soulcalibur |
| Play! GameConfig has a patch for a serial we care about | Scan `C:\Windows\Play\GameConfig.xml` |
| PCSX2/PINE comparison needs a simpler first-party boot | Ico, Sly 1 |

## When not to

- Already ~6 concurrent scouts and no free slot  
- Title only duplicates a wall we already understand without new ABI  
- C: free &lt; ~50 GB or RAM/CPU saturated  

## Current strategic adds (2026-07-30)

| Title | Why |
|-------|-----|
| **MK Armageddon** | Midway trilogy free-rider (PAK/MSL/family assists) |
| **Shadow of the Colossus** | Sony first-party dialect next to GoW (render-heavy, not MK menu shape) |

## Oracles (every title)

1. DetPS2 traces  
2. **Play!** `C:\Windows\Play` HLE + GameConfig  
3. **PCSX2 + PINE** when behavior unclear  
4. Elgato / PPM when assets actually draw  

See `docs/PLAY_HLE_ORACLE.md`.

## Media JSON

Create `user-media-<id>.json` with UNC `path` + shared `biosPath`. Keep gitignored local configs out of commits when they embed secrets or absolute-only personal paths; UNC library paths are fine to document.

## Wiki (mandatory when you pull a title)

Per [Compatibility Database](https://github.com/RazmanianDVL/DetPS2/wiki/Compatibility-Database.) and user policy:

1. **Create a dedicated wiki page** for that game **when it is pulled** (not after MENU YES).  
2. Format: `# Title - Date` → `## Current progress` → `## Active issues` → `## Fixed issues`.  
3. Link it from [Commercial-Titles](https://github.com/RazmanianDVL/DetPS2/wiki/Commercial-Titles) and [Home](https://github.com/RazmanianDVL/DetPS2/wiki) scoreboards.  
4. **Update the page after every scout report** (wall, metrics, Play!/PINE notes, commits).  
5. Close GitHub issues only when that title’s wall is truly done; keep Active/Fixed in sync.
