# Metadata & box-art scrape (v1)

Library serials + optional cover art, flat and 3D. Owned by UI-2; Core types live under
`src/DetPS2.Core/Metadata/`.

## Cache layout

```
%LocalAppData%\DetPS2\metadata\{serial}\box.jpg     (flat / front cover)
%LocalAppData%\DetPS2\metadata\{serial}\box3d.png   (3D case render, optional)
```

- `{serial}` is normalized via `MediaVerify.NormalizeSerial` (e.g. `SLUS_210.87`).
- Override root with `EmulatorConfig.MetadataCacheDir` (empty = default above).
- API: `LocalBoxArtCache.TryGet(serial, kind)` → path or null; `Save(serial, bytes, kind)` → path,
  where `kind` is `BoxArtKind.Flat` (default) or `BoxArtKind.ThreeD`.

## Config (`EmulatorConfig`)

| Field | Default | Meaning |
|-------|---------|---------|
| `ScrapeBoxArt` | `false` | Master on/off for any network fetch after serial identify |
| `ScraperProvider` | `"LocalOnly"` | Primary provider: `LocalOnly` or `SerialHttp` |
| `Scrape3DBoxArt` | `false` | Also fetch/cache a 3D box-art render alongside the flat cover |
| `UseLibretroThumbnails` | `false` | Additive: also try libretro-thumbnails (free, title-indexed) |
| `UseScreenScraper` | `false` | Additive: also try screenscraper.fr (free account required) |
| `ScreenScraperUser` / `ScreenScraperPassword` | `""` | Your own ScreenScraper account (ssid/sspassword) — required for `UseScreenScraper` to activate |
| `ScreenScraperDevId` / `ScreenScraperDevPassword` | `""` | Optional developer id/password for a higher personal rate limit |
| `MetadataCacheDir` | `""` | Custom cache root; empty → LocalAppData |

Every source is **opt-in** and tried in order — primary provider, then LibretroThumbnails,
then ScreenScraper — for each art kind independently; the first hit for a kind wins and later
sources aren't tried for that kind.

## Per-game (`GameSettings`)

| Field | Meaning |
|-------|---------|
| `Serial` | Normalized disc serial if known |
| `BoxArtPath` | Absolute path to flat/front cover image |
| `BoxArt3DPath` | Absolute path to 3D case render image |
| `TitleOverride` | Optional display title |

## Types

| Type | Role |
|------|------|
| `BoxArtKind` | `Flat` \| `ThreeD` — which asset a scrape/cache call is for |
| `GameMetadata` | Resolved serial / title / flat + 3D box paths / provider |
| `IBoxArtScraper` | `FetchAsync(serial, titleHint, kind)` → image bytes or null; `SupportsKind(kind)` |
| `SerialBoxArtScraper` | `xlenore/ps2-covers` — serial-indexed, free, no account. Flat + 3D. |
| `LibretroThumbnailsScraper` | `libretro-thumbnails/Sony_-_PlayStation_2` — title-indexed, free, no account. Flat only. |
| `ScreenScraperBoxArtScraper` | `screenscraper.fr` API — title-indexed (best-effort `romnom` search), requires the user's own free account. Flat + 3D. |
| `NullBoxArtScraper` | Always null (local cache only) |
| `LocalBoxArtCache` | Disk cache (flat + 3D) + optional minimal JPEG placeholder bytes |
| `LibraryMetadataService` | `EnsureSerialAndEnqueueAsync(path)` — non-blocking scrape across all configured sources/kinds |

## Sources — what they are, auth, and terms

None of these are ROM/ISO distribution sites — they're cover-art / game-database services.
No API key or account credential is ever embedded in this repo; anything a source requires is
read from the user's own `EmulatorConfig` (filled in under **Options → Metadata**).

### `xlenore/ps2-covers` (provider id `SerialHttp`, primary provider option)

- github.com/xlenore/ps2-covers — community-maintained PS2 cover collection, fetched as public
  raw GitHub HTTPS (`raw.githubusercontent.com`). No account, no key, no rate-limit registration.
- Serial-indexed: `covers/default/{SERIAL}.jpg` (flat), `covers/3d/{SERIAL}.png` (3D case).
  **Verified live against the repo** (2026-08-02) — the flat set is `.jpg`, the 3D set is
  `.png`; a prior version of this scraper requested `.jpg` for the 3D path too and silently
  404'd on every attempt.
- Underlying art is copyright of each game's original publisher, as with any cover-art
  aggregator; this project only fetches on the user's behalf for personal display, same as
  RetroArch/PCSX2-adjacent tools already do against this same repo.

### `libretro-thumbnails` (opt-in toggle `UseLibretroThumbnails`)

- github.com/libretro-thumbnails/Sony_-_PlayStation_2 — RetroArch's public thumbnail set.
  Public raw GitHub HTTPS, no account/key.
- **Title-indexed**, No-Intro-style naming (`Named_Boxarts/{Title} ({Region}).png`) — not
  serial-indexed, so this scraper builds a handful of candidate filenames from the game's
  known display title plus a region guess from the serial prefix (SLUS/SCUS→USA,
  SLES/SCES→Europe, SLPS/SCPS/SLPM→Japan, SLKA→Korea) and tries each. Lower hit rate than
  `SerialHttp` since it depends on the title closely matching No-Intro naming, but independent
  coverage — useful as a fallback when the primary source has no entry for a title.
- Flat covers only; this set has no 3D box renders.

### `screenscraper.fr` (opt-in toggle `UseScreenScraper`)

- A dedicated, actively-maintained retro-game scraping database with metadata, screenshots,
  and both flat (`box-2D`) and rendered 3D (`box-3D`) box art — the only source wired here with
  genuine 3D box renders for a large fraction of the PS2 catalog.
- **Requires the user's own free account** (register at screenscraper.fr; no cost). An
  optional developer id/password pair raises the personal rate limit further but is not
  required to use the source at all — per this project's "no API keys in-tree" rule, neither
  the account credentials nor a dev id/password are ever bundled; both live only in the user's
  local `config.json`, entered via Options → Metadata.
- Terms: personal, registered, rate-limited use is the intended usage pattern for this API;
  it is not a bulk-redistribution license. This project queries one title at a time, on
  demand, exactly as a normal end-user scrape client would.
- Matching: ScreenScraper's `jeuInfos.php` matches primarily by ROM hash or filename
  (`romnom`) against `systemeid=58` (PlayStation 2); this project doesn't have a cheap ISO
  hash available, so it searches by the best-known display title — best-effort, same as the
  other sources here.
- API shape used (verified against publicly available ScreenScraper client source, not
  guessed): `GET jeuInfos.php?devid=&devpassword=&softname=&ssid=&sspassword=&systemeid=58&romnom=&output=json`
  → `{"response":{"jeu":{"medias":[{"type":"box-2D"|"box-3D","region":"us"|...,"url":"..."}]}}}`.

## Behaviour (v1)

1. `EnsureSerialAndEnqueueAsync` runs `MediaVerify.Identify` off-thread, writes `GameSettings.Serial`.
2. For each art kind (flat always; 3D when `Scrape3DBoxArt`): if already cached → use it and
   move on.
3. If `ScrapeBoxArt` and still missing → `LibraryMetadataService.ResolveScrapers()` tries each
   configured source in order (primary provider → LibretroThumbnails → ScreenScraper), skipping
   any that don't support the requested kind (`IBoxArtScraper.SupportsKind`), until one returns
   bytes. Miss/failure on every source → **path left empty** (no forced placeholder unless the
   host calls enqueue with `writePlaceholder: true`).
4. Never block the UI thread on network; use async + `Task.Run` for disc IO.

## Hosting Options pages (UI-1)

UserControls under `src/DetPS2.Desktop/Options/`:

- `OptionsGeneralPage` — BIOS, auto-run, verify media
- `OptionsGraphicsPage` — present mode, frame limit / FPS
- `OptionsMetadataPage` — scrape toggle, 3D toggle, provider + additional sources, ScreenScraper
  credentials, cache dir

**Already wired:** `OptionsWindow` (`IOptionsHost`) hosts these via `BuildGeneralHost` / `BuildGraphicsPage` / `BuildMetadataPage`. Menu: **Options → Graphics / Metadata / General**. On category switch and Close, `ApplyActivePage` calls each page’s `ApplyTo` / `ApplyToConfig` then `PersistConfig`.

Standalone (no shell):

```csharp
var page = new OptionsMetadataPage();
page.LoadFrom(config);
// … show in dialog …
page.ApplyTo(config);
config.Save(configPath);
```

Library hook after scan/boot (box art + serial):

```csharp
using var meta = new LibraryMetadataService(config);
meta.MetadataUpdated += m => { /* refresh tile art on UI thread; m.BoxArtPath / m.BoxArt3DPath */ };
await meta.EnsureSerialAndEnqueueAsync(game.Path, game);
// game.Serial / game.BoxArtPath / game.BoxArt3DPath updated when cache hit or scrape completes
```

Library tile art: bind `GameSettings.BoxArtPath` (or `TitleOverride`) when the cover Image is
wired; placeholder emoji remains until path is non-empty. `BoxArt3DPath` is available on the
same model for a future 3D-case display treatment — not yet wired into the library grid itself.

## Safety

- No API keys or account credentials in-tree. Every credential field defaults to empty and is
  read only from the user's own local config.
- `SerialHttp` / `LibretroThumbnails` are best-effort only; 404/timeout → null, never throws.
- `ScreenScraper` is inactive (returns null immediately) until the user supplies their own
  account in Options → Metadata.
- Online scrape is **opt-in** (`ScrapeBoxArt = false` by default); 3D and each additional
  source are separately opt-in on top of that.
- Do not hash whole multi-GB discs for covers; serial identify reuses `MediaVerify`.
