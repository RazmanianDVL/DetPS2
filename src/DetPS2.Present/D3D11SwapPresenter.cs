using System;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace DetPS2.Present;

/// <summary>
/// Direct3D 11 host presenter skeleton: creates a device + HWND swap chain and uploads
/// Soft-GS BGRA (packed <c>0xAARRGGBB</c>) via a staging texture.
/// If device or swap-chain creation fails, <see cref="DeviceReady"/> is false and
/// <see cref="Present"/> is a no-op (never throws to callers).
/// Soft-GS remains determinism truth — this only displays.
/// </summary>
public sealed unsafe class D3D11SwapPresenter : IHostSwapPresenter
{
    private static readonly FeatureLevel[] s_featureLevels =
    {
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    };

    private readonly object _lock = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _staging;
    private ID3D11Texture2D? _gpuTexture;
    private nint _hwnd;
    private int _outW;
    private int _outH;
    private int _stageW;
    private int _stageH;
    private bool _deviceOk;
    private bool _swapOk;
    private bool _disposed;

    public PresentBackend Backend => PresentBackend.D3D11;
    public string Name => "D3D11";
    public bool DeviceReady => _deviceOk && _swapOk;
    public bool WindowAttached => _hwnd != 0 && _swapChain != null;
    public UpscaleMode UpscaleMode { get; set; } = UpscaleMode.None;
    public PresentStats Stats { get; } = new() { BackendName = "D3D11", DeviceReady = false };

    /// <summary>Feature level selected at device create (0 if not created).</summary>
    public FeatureLevel FeatureLevel { get; private set; }

    /// <summary>True when <c>D3D11CreateDevice</c> succeeded (swap chain may still be pending).</summary>
    public bool DeviceCreated => _deviceOk && _device != null;

    public D3D11SwapPresenter()
    {
        // Eager device create so Auto factory can probe DeviceReady after attach.
        // Swap chain still requires AttachWindow(hwnd, …).
        TryCreateDevice();
    }

    public bool AttachWindow(nint hwnd, int width, int height)
    {
        if (hwnd == 0 || width <= 0 || height <= 0)
        {
            Stats.LastError = "D3D11: invalid hwnd/size";
            return false;
        }

        lock (_lock)
        {
            if (_disposed) return false;

            if (!_deviceOk && !TryCreateDevice_NoLock())
                return false;

            try
            {
                DestroySwapChain_NoLock();
                if (!CreateSwapChain_NoLock(hwnd, width, height))
                    return false;

                _hwnd = hwnd;
                _outW = width;
                _outH = height;
                Stats.OutputWidth = width;
                Stats.OutputHeight = height;
                Stats.DeviceReady = true;
                Stats.LastError = "";
                _swapOk = true;
                return true;
            }
            catch (Exception ex)
            {
                _swapOk = false;
                Stats.DeviceReady = false;
                Stats.LastError = "D3D11 AttachWindow: " + ex.Message;
                DestroySwapChain_NoLock();
                return false;
            }
        }
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        lock (_lock)
        {
            if (_disposed || !_deviceOk || _swapChain == null || _hwnd == 0)
                return;

            try
            {
                _context?.ClearState();
                _context?.Flush();

                // Release any views that hold backbuffer refs (none held long-term here).
                var hr = _swapChain.ResizeBuffers(
                    0,
                    (uint)width,
                    (uint)height,
                    Format.Unknown,
                    SwapChainFlags.None);

                if (hr.Failure)
                {
                    // Rebuild swap chain on hard failure.
                    DestroySwapChain_NoLock();
                    if (!CreateSwapChain_NoLock(_hwnd, width, height))
                        return;
                }

                _outW = width;
                _outH = height;
                Stats.OutputWidth = width;
                Stats.OutputHeight = height;
                _swapOk = true;
                Stats.DeviceReady = true;
            }
            catch (Exception ex)
            {
                _swapOk = false;
                Stats.DeviceReady = false;
                Stats.LastError = "D3D11 Resize: " + ex.Message;
            }
        }
    }

    public void Present(ReadOnlySpan<uint> framebuffer, int width, int height)
    {
        // Never throw to callers when not ready.
        if (!_deviceOk || !_swapOk || _device == null || _context == null || _swapChain == null)
            return;
        if (width <= 0 || height <= 0 || framebuffer.Length < width * height)
            return;

        lock (_lock)
        {
            if (_disposed || !_deviceOk || !_swapOk || _device == null || _context == null || _swapChain == null)
                return;

            try
            {
                EnsureStaging_NoLock(width, height);
                if (_staging == null || _gpuTexture == null)
                    return;

                // Soft-GS pack is 0xAARRGGBB LE == B8G8R8A8_UNorm memory order.
                UploadStaging_NoLock(framebuffer, width, height);

                _context.CopyResource(_gpuTexture, _staging);

                using ID3D11Texture2D backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
                CopyToBackBuffer_NoLock(backBuffer, width, height);

                // 1 = no vsync wait (host UI owns pacing); 0 would block on vblank.
                _swapChain.Present(0, PresentFlags.None);

                int n = width * height;
                Stats.SourceWidth = width;
                Stats.SourceHeight = height;
                Stats.BytesUploaded += (ulong)n * 4;
                Stats.UploadCount++;
                Stats.PresentCount++;
                Stats.DeviceReady = true;
            }
            catch (Exception ex)
            {
                // Swallow: DeviceReady may flip false; callers keep Soft-GS path.
                Stats.LastError = "D3D11 Present: " + ex.Message;
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Stats.ClearCounters();
            Stats.DeviceReady = DeviceReady;
            Stats.LastError = DeviceReady ? "" : Stats.LastError;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            DestroyStaging_NoLock();
            DestroySwapChain_NoLock();
            DestroyDevice_NoLock();
            _hwnd = 0;
            Stats.DeviceReady = false;
        }
        GC.SuppressFinalize(this);
    }

    // ── device / swap chain ──────────────────────────────────────────────

    private void TryCreateDevice()
    {
        lock (_lock)
            TryCreateDevice_NoLock();
    }

    private bool TryCreateDevice_NoLock()
    {
        if (_deviceOk && _device != null && _context != null)
            return true;

        DestroyDevice_NoLock();

        try
        {
            Result hr = D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                s_featureLevels,
                out ID3D11Device? device,
                out FeatureLevel fl,
                out ID3D11DeviceContext? context);

            if (hr.Failure || device == null || context == null)
            {
                // WARP fallback for headless / broken GPU drivers.
                hr = D3D11CreateDevice(
                    null,
                    DriverType.Warp,
                    DeviceCreationFlags.BgraSupport,
                    s_featureLevels,
                    out device,
                    out fl,
                    out context);
            }

            if (hr.Failure || device == null || context == null)
            {
                Stats.LastError = "D3D11CreateDevice failed: " + hr;
                Stats.DeviceReady = false;
                _deviceOk = false;
                return false;
            }

            _device = device;
            _context = context;
            FeatureLevel = fl;
            _deviceOk = true;
            Stats.LastError = "";
            return true;
        }
        catch (Exception ex)
        {
            Stats.LastError = "D3D11 device: " + ex.Message;
            Stats.DeviceReady = false;
            _deviceOk = false;
            DestroyDevice_NoLock();
            return false;
        }
    }

    private bool CreateSwapChain_NoLock(nint hwnd, int width, int height)
    {
        if (_device == null)
            return false;

        try
        {
            using IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
            using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();

            var desc = new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.None,
            };

            _swapChain = factory.CreateSwapChainForHwnd(_device, hwnd, desc);
            factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);

            _outW = width;
            _outH = height;
            return _swapChain != null;
        }
        catch (Exception ex)
        {
            Stats.LastError = "D3D11 CreateSwapChain: " + ex.Message;
            _swapChain = null;
            return false;
        }
    }

    private void DestroySwapChain_NoLock()
    {
        _swapChain?.Dispose();
        _swapChain = null;
        _swapOk = false;
    }

    private void DestroyDevice_NoLock()
    {
        _context?.ClearState();
        _context?.Flush();
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        _deviceOk = false;
        FeatureLevel = 0;
    }

    // ── staging upload ───────────────────────────────────────────────────

    private void EnsureStaging_NoLock(int width, int height)
    {
        if (_device == null) return;
        if (_staging != null && _gpuTexture != null && _stageW == width && _stageH == height)
            return;

        DestroyStaging_NoLock();

        var stagingDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None,
        };

        var gpuDesc = stagingDesc;
        gpuDesc.Usage = ResourceUsage.Default;
        gpuDesc.BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget;
        gpuDesc.CPUAccessFlags = CpuAccessFlags.None;

        _staging = _device.CreateTexture2D(stagingDesc);
        _gpuTexture = _device.CreateTexture2D(gpuDesc);
        _stageW = width;
        _stageH = height;
    }

    private void DestroyStaging_NoLock()
    {
        _staging?.Dispose();
        _staging = null;
        _gpuTexture?.Dispose();
        _gpuTexture = null;
        _stageW = _stageH = 0;
    }

    private void UploadStaging_NoLock(ReadOnlySpan<uint> framebuffer, int width, int height)
    {
        if (_context == null || _staging == null) return;

        MappedSubresource mapped = _context.Map(_staging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int srcStride = width * 4;
            int dstPitch = (int)mapped.RowPitch;
            byte* dstBase = (byte*)mapped.DataPointer;
            fixed (uint* srcPtr = &MemoryMarshal.GetReference(framebuffer))
            {
                byte* srcBase = (byte*)srcPtr;
                if (dstPitch == srcStride)
                {
                    Buffer.MemoryCopy(srcBase, dstBase, (long)srcStride * height, (long)srcStride * height);
                }
                else
                {
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(
                            srcBase + (long)y * srcStride,
                            dstBase + (long)y * dstPitch,
                            dstPitch,
                            srcStride);
                    }
                }
            }
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }

    private void CopyToBackBuffer_NoLock(ID3D11Texture2D backBuffer, int srcW, int srcH)
    {
        if (_context == null || _gpuTexture == null) return;

        // Prefer full CopyResource when sizes match; otherwise top-left CopySubresourceRegion.
        Texture2DDescription bbDesc = backBuffer.Description;
        if (bbDesc.Width == (uint)srcW && bbDesc.Height == (uint)srcH)
        {
            _context.CopyResource(backBuffer, _gpuTexture);
            return;
        }

        int copyW = Math.Min(srcW, (int)bbDesc.Width);
        int copyH = Math.Min(srcH, (int)bbDesc.Height);
        if (copyW <= 0 || copyH <= 0) return;

        // UpscaleMode is reserved for a future GPU blit pass; skeleton crops/copies top-left.
        _ = UpscaleMode;
        var box = new Vortice.Mathematics.Box(0, 0, 0, copyW, copyH, 1);
        _context.CopySubresourceRegion(backBuffer, 0, 0, 0, 0, _gpuTexture, 0, box);
    }
}
