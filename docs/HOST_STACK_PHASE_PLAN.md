# Host stack phase plan — GPU present · Library UI · Controllers

**Status:** ACTIVE — host-product track (parallel to Soft-GS GFX / post-MENU play)  
**Doctrine:** Soft-GS = emulation / determinism / claim truth · host GPU = display only  
**Related:** [GRAPHICS_PIPELINE_PHASE_PLAN.md](GRAPHICS_PIPELINE_PHASE_PLAN.md) · [POST_MENU_PHASE_PLAN.md](POST_MENU_PHASE_PLAN.md) · [CORRECTNESS.md](CORRECTNESS.md)  
**Parent approved plan:** session host-stack plan (Vulkan / D3D11 / D3D12 + library UI + full controllers)

---

## 0. Soft-GS truth doctrine (non-negotiable)

| Law | Meaning |
|-----|---------|
| **Soft-GS is truth** | Claims, scoreboard, netplay hashes, `blocker-trace` / `px` use CPU Soft-GS (`Gs.GetPresentSpan` / Soft-GS counters). |
| **Host GPU is display** | Vulkan / D3D11 / D3D12 upload Soft-GS BGRA → texture → upscale → swapchain. They do **not** invent pixels when `px=0`. |
| **No host-cheat presentation** | No FFmpeg logos, no synthetic branded UI, no “pretty lie” overlays as MENU. See [CORRECTNESS.md](CORRECTNESS.md). |
| **Present toggles must not change det** | Switching PresentMode never changes Soft-GS raster math or claim hashes (**H-DET**). |

**Honest scope split (keep separate in docs and UI):**

1. **Host present (this plan, Track A)** — Soft-GS FB → GPU → window.  
2. **GS fidelity (existing GFX plan)** — Path/TEX/DISPFB so Soft-GS has real pixels.  
3. **GPU Soft-GS assist / hardware GS** — **out of v1** (optional GP-5 later, dual-path required).

**Product wording (Options UI):**  
“Graphics API: how frames are **shown** on your GPU. Emulation accuracy still uses software GS.”

---

## 1. Tracks (A / B / C)

| Track | Goal | Seats |
|-------|------|--------|
| **A. Host GPU present** | Real **Vulkan**, **D3D11**, **D3D12** (Auto prefers D3D12→D3D11→Vulkan→Software on Windows) | **GPU-A**, **GPU-B**, **GPU-C** |
| **B. Desktop UI** | Main = **game library only**; every setting under **Options**; box-art scrape | **UI-1**, **UI-2** (2 permanent) |
| **C. Controllers** | Full device matrix + **per-button remap** | **PAD-1**, **PAD-2** |

```text
DetPS2.Core          Soft-GS truth / PadInput semantic / config models
        │ BGRA span
        ▼
DetPS2.Present       IHostSwapPresenter: Software | Vulkan | D3D11 | D3D12
        │ HWND / surface
        ▼
DetPS2.Desktop       Library shell + Options + game window + host input
DetPS2.Metadata      (optional) scrapers + art cache
```

---

## 2. Agent seats

| Seat | Codename | Track | Owns (write) | Does not own |
|------|----------|-------|--------------|--------------|
| **UI-1** | **SHELL** | B | `MainWindow`, Options host, game grid/list, empty-state / first-run | Scrapers, GPU backends, Soft-GS |
| **UI-2** | **META+OPTIONS** | B | Options page content, metadata/scrape, art cache, config fields for meta | Main chrome redesign, present device code |
| **GPU-A** | **PRESENT-CORE** | A | `DetPS2.Present` contracts, Auto probe, **D3D11** | Soft-GS raster, Avalonia chrome |
| **GPU-B** | **VULKAN** | A | **Vulkan** native path (Silk.NET or Vortice.Vulkan) | Title quirks, UI shell |
| **GPU-C** | **D3D12+QoP** | A | **D3D12**, upscale/vsync/aspect QoP | Soft-GS math |
| **PAD-1** | **BIND-ENGINE** | C | `InputBindingTable`, defaults, `HostGamepad` poll apply | Remap chrome, Soft-GS |
| **PAD-2** | **REMAP-UI** | C | Options → Controllers, capture dialog, profiles / GH layout | Soft-GS, GPU present |

**S10 (GFX-DISPLAY)** from the Soft-GS plan coordinates Soft-GS→present handoff only; it does not re-implement host backends.

**T0 merge rules**

- UI merges must not touch `Gs.cs` / `Gif.cs`.  
- Present merges must not change Soft-GS pixel math.  
- Input merges must not invent WaitSema / title pad assists.  
- **UI-1 + UI-2 stay on UI** for this campaign (not reassigned to title ports).

---

## 3. Track A — GPU present (phases)

| Phase | Focus | Exit |
|-------|--------|------|
| **GP-0** | `PresentBackend` enum, Auto order, capability probe, config persist | UI lists backends; Auto safe on GPU-less CI |
| **GP-1** | **D3D11** HWND swapchain + Soft-GS BGRA upload (Windows first) | Game window via D3D11 when selected |
| **GP-2** | **Vulkan** instance/device/swapchain; `VulkanDeviceReady` honest | Vulkan presents same Soft-GS; CLI can force Software |
| **GP-3** | **D3D12** queue/fence; Auto first choice when capable | Fallbacks documented |
| **GP-4** | Integer/bilinear scale, aspect, vsync, screenshot (Soft-GS PPM + last GPU frame) | QoP under Options → Graphics |
| **GP-5** | Optional GPU Soft-GS assist / partial HW GS | **Not v1** — only after G-GFX floors + dual-path |

**Packages (suggested):** Vortice.Direct3D11/12 + DXGI; Silk.NET.Vulkan. Avoid SharpDX.

---

## 4. Track B — UI (2 agents)

### Product rules

1. **Main screen = games only** (grid/list + art).  
2. **Every option under Options** — no BIOS/log/settings clutter on home.  
3. **Box art scraping** — cache + offline placeholders; network opt-in.

### Shell IA (target)

```text
MainWindow
├── Options ▾  General | Graphics | Controllers | Emulation | Audio | Netplay | Metadata | Advanced
├── Content: game grid ONLY (tile = art + title + serial)
└── Minimal status (optional FPS / running title) — no log dump
```

| Phase | Owner | Exit |
|-------|--------|------|
| **UI-0** shell | UI-1 | Main shows only games; Options host with categories |
| **UI-1** pages | UI-2 | Graphics/Emulation/Audio wired; Soft-GS ≠ API labeled |
| **UI-2** metadata | UI-2 (+ UI-1 tiles) | Serial scrape + cache; ≥80% art when network allowed |
| **UI-3** polish | both | Keyboard nav, search filter, first-run library+BIOS |

**Scrape order:** local cache → user override → online (ScreenScraper / TheGamesDB / similar; document ToS) → placeholder. Default **ask** before first network scrape.

---

## 5. Track C — Controllers matrix

```text
Host event → InputBindingTable (per player/profile) → PadInput → SIO2 / PADMAN
```

| Device | Strategy | v1 notes |
|--------|----------|----------|
| Keyboard | Avalonia/Win32 keys | Full remap |
| Xbox 360 / One / Series | XInput | Canonical map; Series via XInput v1.4 |
| DualShock 4 | HID (+ XInput if remapped by OS) | Touchpad click → Select; gyro **out** |
| DualSense | HID | Adaptive triggers / gyro **out** |
| PDP Riffmaster | XInput and/or HID guitar | `ControllerProfile.GuitarHero` + fret remap |

| Phase | Focus |
|-------|--------|
| **IN-0** | Binding engine + default maps for all kinds |
| **IN-1** | Remap UI (capture, reset, P1/P2, GH layout) |
| **IN-2** | Device completeness + hotplug |
| **IN-3** | Rumble / cosmetic extras (stretch) |

---

## 6. Milestones M0–M7

| Milestone | Weeks | Done when |
|-----------|-------|-----------|
| **M0** Spec freeze | 0.5 | This plan in-repo; epic/issues opened |
| **M1** UI shell | 1–1.5 | Main = games only; Options categories open |
| **M2** Binding engine | 1 | All listed devices poll via bindings |
| **M3** D3D11 present | 1.5–2 | Game window D3D11 path live |
| **M4** Remap + box art v1 | 2 | Capture binds; serial scrape + cache |
| **M5** Vulkan present | 2 | Honest `VulkanDeviceReady` on Win10+ dGPU |
| **M6** D3D12 + Auto | 2–3 | Auto prefers D3D12 when available |
| **M7** Hardening | 1–2 | Device-lost, hotplug, offline scrape, desktop publish |

**Optimistic:** ~10–12 weeks focused. **With Soft-GS parallel:** present still useful as soon as Soft-GS has `px`.

---

## 7. Verification gates

| Gate | Criteria |
|------|----------|
| **H-UI-1** | Main: no Settings toolbar / log column / status sidebar; library + menu only |
| **H-UI-2** | Every former setting reachable under Options → … |
| **H-META-1** | Offline placeholders; online art for known serials (e.g. Vexx + Deception) in cache |
| **H-IN-1** | Each device class maps Cross/Start/D-pad/sticks into `PadInput` |
| **H-IN-2** | Remap Cross → other button; survives restart |
| **H-IN-3** | Riffmaster frets change pad under Guitar profile |
| **H-GPU-1** | D3D11 non-black when Soft-GS `px>0` (homebrew GS or proven title) |
| **H-GPU-2** | Vulkan same |
| **H-GPU-3** | D3D12 same; Auto order verified by forcing fail |
| **H-DET-1** | Soft-GS claim hash / `px` unchanged when PresentMode toggles |

*(Shorthand used in charters: **H-UI**, **H-META**, **H-IN**, **H-GPU**, **H-DET** for the families above.)*

---

## 8. Non-goals v1

- Metal / OpenGL as primary backends (Vulkan covers non-Windows later).  
- DualSense adaptive triggers / gyro aiming.  
- Network scrapers without user consent.  
- Replacing Soft-GS with GPU as claim truth.  
- Full hardware GS (PCSX2-class) or partial GIF→GPU raster without dual Soft-GS validation.  
- HDR present.  
- Touch / mobile UI.  
- Multi-key chord remaps; multitap P3/P4 unless Core multitap already wired.

---

## 9. Risks (short)

| Risk | Mitigation |
|------|------------|
| User expects “GPU GS” = full HW GS | UI labels; Soft-GS doctrine; GP-5 gated |
| Avalonia + native HWND fight | Exclusive game window for GPU present |
| Soft-GS still black | Present cannot fix; keep S8–S10 concurrent |
| D3D12 complexity | Ship D3D11 first; Auto fallback |
| HID fragility | XInput when OS already maps; raw HID secondary |

---

## 10. Success definition

Open DetPS2 → **only games with box art** → **Options** for every setting → pick **Vulkan / D3D11 / D3D12 / Auto** → play with **DS4 / DualSense / Xbox 360 / One / Series / Riffmaster / Keyboard** with **full remaps** → frames on a **real GPU swapchain**, while **Soft-GS remains the emulation truth path**.

---

*Host stack is product surface. Soft-GS and IRX remain the correctness spine. T0 keeps host seats off Gs/Gif math and keeps UI-1/UI-2 on library + Options until H-UI / H-META green.*
