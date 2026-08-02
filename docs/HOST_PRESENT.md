# Host present API (`DetPS2.Present`)

Soft-GS remains the **determinism source of truth**. Host presenters only **display** Soft-GS pixels; they never replace GS hashes or raster math.

## Project

| Item | Value |
|------|--------|
| Project | `src/DetPS2.Present/DetPS2.Present.csproj` |
| TFM | `net9.0` |
| Unsafe | enabled |
| Packages | `Vortice.Direct3D11` / `DXGI` 3.8.3, `Silk.NET.Vulkan` (+ KHR/EXT) 2.23.0 |

Independent of `DetPS2.Core` (no project reference). Desktop (or other hosts) reference Present and feed `Gs.GetPresentSpan()`.

## Contracts

### `PresentBackend`

`Auto` · `Software` · `Vulkan` · `D3D11` · `D3D12` · `OpenGL`

### `UpscaleMode`

`None` · `Nearest` · `Bilinear` (host-side only; Det hash unchanged)

### `PresentStats`

Counters: `PresentCount`, `UploadCount`, `BytesUploaded`, source/output sizes, `DeviceReady`, `BackendName`, `LastError`.

### `IHostSwapPresenter`

| Member | Role |
|--------|------|
| `Backend` / `Name` | Selected backend |
| `DeviceReady` | Native device + swap chain OK; if false, `Present` is a no-op (no throw) |
| `WindowAttached` | Successful `AttachWindow` |
| `AttachWindow(hwnd, w, h)` | Bind HWND, create swap chain / output |
| `Resize(w, h)` | Resize output |
| `Present(span, w, h)` | Upload Soft-GS `0xAARRGGBB` (BGRA LE) and present |
| `Reset()` / `Dispose()` | Counters / native teardown |

## Implementations

| Class | Status |
|-------|--------|
| `SoftwareSwapPresenter` | Always ready; CPU copy into `LastFrame` |
| `D3D11SwapPresenter` | D3D11 device + HWND swap chain + staging upload |
| `D3D12SwapPresenter` | D3D12 device + HWND swap chain + upload heap |
| `VulkanSwapPresenter` | Silk.NET Vulkan: device + Win32 surface + GPU present |
| `OpenGLSwapPresenter` | Windows WGL + opengl32: texture upload + fullscreen blit + `SwapBuffers` |

### Vulkan (`VulkanSwapPresenter`) details

| Step | API | Notes |
|------|-----|--------|
| Init | ctor | `vkCreateInstance` → pick GPU → `vkCreateDevice` → `DeviceReady` |
| Window | `AttachWindow(hwnd,w,h)` | `VK_KHR_win32_surface` + swapchain create/recreate stubs |
| Frame | `Present(span,w,h)` | Stages host buffer when `DeviceReady`; no-op if not. GPU blit TODO when `SwapchainReady`. |
| Env | `DETPS2_VK_VALIDATE=1` | Enables `VK_LAYER_KHRONOS_validation` when installed |

Core honesty: `DetPS2.Core.VulkanFramePresenter.VulkanDeviceReady` stays **false** (software upscale only) so CLI smokes remain dep-free.

### Auto order (`PresentBackendFactory.CreateAndAttach`)

1. **D3D12**  
2. **D3D11**  
3. **Vulkan**  
4. **OpenGL** (needs HWND / WGL)  
5. **Software** (always)

```csharp
// Device only (attach later):
var presenter = PresentBackendFactory.Create(PresentBackend.Auto);

// Device + HWND:
var presenter = PresentBackendFactory.CreateAndAttach(hwnd, width, height, PresentBackend.Auto);
```

## Desktop: Soft-GS → Avalonia BGRA

Soft-GS present span is packed **`0xAARRGGBB`** per `uint` (LE memory **B,G,R,A**). That matches Avalonia `PixelFormat.Bgra8888` and DXGI `B8G8R8A8_UNorm` — **no channel swizzle** on blit. Desktop forces `A=0xFF` so `AlphaFormat.Opaque` never treats RGB as transparent black.

| Path | Behavior |
|------|----------|
| `EmulationWorker` + `PresentSnapshot` | UI blits worker Soft-GS snapshot only (no mid-`RunFor` race). Avalonia always visible. |
| `PresentFrame(system)` | Live Soft-GS; may dual-feed host GPU. **Never hides Avalonia** unless GPU exclusive is proven (`PresentCount` advanced, no new error, Soft-GS has pixels). |
| V-flip | Optional: env `DETPS2_VFLIP=1` or `GameDisplayWindow.SetFlipY(true)` if a composite is inverted (diagnose with Deception PPM). Soft-GS and Avalonia are both top-left; default is no flip. |
| Unit check | `DetPS2.Desktop --present-selftest` |

Packing helper: `SoftGsAvaloniaBlit` (Desktop).

## Desktop: `GameDisplayWindow` → `AttachWindow`

Today `GameDisplayWindow` blits Soft-GS into an Avalonia `WriteableBitmap` (CPU). To use host swap present:

1. Reference `DetPS2.Present` from `DetPS2.Desktop`.
2. Obtain a **native HWND** for the game surface (or a child/host panel):

```csharp
// After the window is shown / has a platform handle:
var topLevel = TopLevel.GetTopLevel(this);
var handle = topLevel?.TryGetPlatformHandle()?.Handle ?? nint.Zero;
// Prefer a dedicated present HWND if you host a child Win32 surface.
```

3. Create + attach once (e.g. on `Opened` or first present):

```csharp
_hostPresent = PresentBackendFactory.CreateAndAttach(
    hwnd: handle,
    width: Math.Max(1, (int)ClientSize.Width),
    height: Math.Max(1, (int)ClientSize.Height),
    backend: PresentBackend.Auto);

// Or force D3D11:
// _hostPresent = PresentBackendFactory.Create(PresentBackend.D3D11);
// _hostPresent.AttachWindow(handle, w, h);
```

4. Each UI tick, feed Soft-GS (same source as today):

```csharp
var fb = system.Gs.GetPresentSpan();
int w = system.Gs.FramebufferWidth;
int h = system.Gs.FramebufferHeight;
if (_hostPresent.DeviceReady)
    _hostPresent.Present(fb, w, h);
else
{
    // Fallback: existing WriteableBitmap path
    PresentFrameCpu(system);
}
```

5. On resize: `_hostPresent.Resize(newW, newH)`.
6. On close: `_hostPresent.Dispose()`.

**Notes**

- Avalonia’s own surface and a D3D11 swap chain on the **same** top-level HWND can fight for presentation. Prefer a dedicated child HWND / interop host for D3D11, or keep Software/WriteableBitmap until that host exists.
- Pixel format: Soft-GS `uint` pack is `0xAARRGGBB` LE ≡ DXGI `B8G8R8A8_UNorm` memory order — D3D11 uploads without swizzle.
- If `D3D11CreateDevice` or swap-chain create fails, `DeviceReady=false` and `Present` does nothing (no throw). Check `Stats.LastError`.

## Build / harness

```powershell
dotnet build src/DetPS2.Present/DetPS2.Present.csproj -c Release
dotnet run --project src/DetPS2.Present -c Release
# optional validation layers:
$env:DETPS2_VK_VALIDATE = "1"
dotnet run --project src/DetPS2.Present -c Release
```

Harness prints `RESULT Vulkan DeviceReady=true|false` for the host machine.

## Non-goals (this scaffold)

- No changes to Soft-GS raster math  
- No Avalonia `MainWindow` rewrite  
- No full GPU blit/queue present yet (Vulkan stages CPU-side; swapchain stub ready for merge)  
- No Core `PresentMode` enum change (Present stays independent; Desktop wires later)
