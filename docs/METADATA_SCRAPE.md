# Metadata & box-art scrape (v0)

Scaffold for library serials + optional cover art. Owned by UI-2; Core types live under `src/DetPS2.Core/Metadata/`.

## Cache layout

```
%LocalAppData%\DetPS2\metadata\{serial}\box.jpg
```

- `{serial}` is normalized via `MediaVerify.NormalizeSerial` (e.g. `SLUS_210.87`).
- Override root with `EmulatorConfig.MetadataCacheDir` (empty = default above).
- API: `LocalBoxArtCache.TryGet(serial)` → path or null; `Save(serial, bytes)` → path.

## Config (`EmulatorConfig`)

| Field | Default | Meaning |
|-------|---------|---------|
| `ScrapeBoxArt` | `false` | Allow network fetch after serial identify |
| `ScraperProvider` | `"LocalOnly"` | `LocalOnly` or `SerialHttp` |
| `MetadataCacheDir` | `""` | Custom cache root; empty → LocalAppData |

## Per-game (`GameSettings`)

| Field | Meaning |
|-------|---------|
| `Serial` | Normalized disc serial if known |
| `BoxArtPath` | Absolute path to box image |
| `TitleOverride` | Optional display title |

## Types

| Type | Role |
|------|------|
| `GameMetadata` | Resolved serial / title / box path / provider |
| `IBoxArtScraper` | `FetchAsync(serial)` → image bytes or null |
| `SerialBoxArtScraper` | Best-effort HTTPS to community cover repos (no API key) |
| `NullBoxArtScraper` | Always null (local cache only) |
| `LocalBoxArtCache` | Disk cache + optional minimal JPEG placeholder bytes |
| `LibraryMetadataService` | `EnsureSerialAndEnqueueAsync(path)` — non-blocking scrape |

## Behaviour (v0)

1. `EnsureSerialAndEnqueueAsync` runs `MediaVerify.Identify` off-thread, writes `GameSettings.Serial`.
2. If `box.jpg` exists in cache → set `BoxArtPath` and return.
3. If `ScrapeBoxArt` and provider is `SerialHttp` → `SerialBoxArtScraper.FetchAsync` (public raw GitHub cover URLs). Miss/failure → **path left empty** (no forced placeholder unless host calls enqueue with `writePlaceholder: true`).
4. Never block the UI thread on network; use async + `Task.Run` for disc IO.

## Hosting Options pages (UI-1)

UserControls under `src/DetPS2.Desktop/Options/`:

- `OptionsGeneralPage` — BIOS, auto-run, verify media
- `OptionsGraphicsPage` — present mode, frame limit / FPS
- `OptionsMetadataPage` — scrape toggle, provider, cache dir

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
meta.MetadataUpdated += m => { /* refresh tile art on UI thread */ };
await meta.EnsureSerialAndEnqueueAsync(game.Path, game);
// game.Serial / game.BoxArtPath updated when cache hit or scrape completes
```

Library tile art: bind `GameSettings.BoxArtPath` (or `TitleOverride`) when the cover Image is wired; placeholder emoji remains until path is non-empty.

## Safety

- No API keys in-tree. `SerialHttp` is best-effort only; 404/timeout → null.
- Online scrape is **opt-in** (`ScrapeBoxArt = false` by default).
- Do not hash whole multi-GB discs for covers; serial identify reuses `MediaVerify`.
