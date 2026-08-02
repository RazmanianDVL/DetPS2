using System;
using System.Runtime.InteropServices;
using System.Threading;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;

namespace DetPS2.Present;

/// <summary>
/// Direct3D 12 host presenter: device (FL12_0 → FL11_0 → WARP) + HWND flip-discard
/// swap chain; Soft-GS packed <c>0xAARRGGBB</c> uploaded via upload heap → DEFAULT texture
/// → backbuffer CopyResource / CopyTextureRegion → Present.
/// If device or swap-chain creation fails, <see cref="DeviceReady"/> is false and
/// <see cref="Present"/> is a no-op (never throws to callers).
/// Soft-GS remains determinism truth — this only displays.
/// </summary>
public sealed unsafe class D3D12SwapPresenter : IHostSwapPresenter
{
    private const int BufferCount = 2;

    private readonly object _lock = new();
    private IDXGIFactory4? _factory;
    private ID3D12Device? _device;
    private ID3D12CommandQueue? _queue;
    private ID3D12CommandAllocator? _cmdAlloc;
    private ID3D12GraphicsCommandList? _cmdList;
    private ID3D12Fence? _fence;
    private ManualResetEvent? _fenceEvent;
    private ulong _fenceValue;
    private IDXGISwapChain3? _swapChain;
    private ID3D12Resource? _upload;
    private ID3D12Resource? _gpuTexture;
    private ResourceStates _textureState = ResourceStates.Common;
    private PlacedSubresourceFootPrint _uploadFootprint;
    private ulong _uploadBytes;
    private nint _hwnd;
    private int _outW;
    private int _outH;
    private int _stageW;
    private int _stageH;
    private bool _deviceOk;
    private bool _swapOk;
    private bool _disposed;

    public PresentBackend Backend => PresentBackend.D3D12;
    public string Name => "D3D12";
    public bool DeviceReady => _deviceOk && _swapOk;
    public bool WindowAttached => _hwnd != 0 && _swapChain != null;
    public UpscaleMode UpscaleMode { get; set; } = UpscaleMode.None;
    public PresentStats Stats { get; } = new() { BackendName = "D3D12", DeviceReady = false };

    /// <summary>Feature level selected at device create (0 if not created).</summary>
    public FeatureLevel FeatureLevel { get; private set; }

    /// <summary>True when <c>D3D12CreateDevice</c> succeeded (swap chain may still be pending).</summary>
    public bool DeviceCreated => _deviceOk && _device != null;

    public D3D12SwapPresenter()
    {
        // Eager device create so Auto factory can probe DeviceCreated before attach.
        // Swap chain still requires AttachWindow(hwnd, …).
        TryCreateDevice();
    }

    public bool AttachWindow(nint hwnd, int width, int height)
    {
        if (hwnd == 0 || width <= 0 || height <= 0)
        {
            Stats.LastError = "D3D12: invalid hwnd/size";
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
                Stats.LastError = "D3D12 AttachWindow: " + ex.Message;
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
                WaitForGpu_NoLock();

                var hr = _swapChain.ResizeBuffers(
                    0,
                    (uint)width,
                    (uint)height,
                    Format.Unknown,
                    SwapChainFlags.None);

                if (hr.Failure)
                {
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
                Stats.LastError = "D3D12 Resize: " + ex.Message;
            }
        }
    }

    public void Present(ReadOnlySpan<uint> framebuffer, int width, int height)
    {
        // Never throw to callers when not ready.
        if (!_deviceOk || !_swapOk || _device == null || _queue == null || _swapChain == null)
            return;
        if (width <= 0 || height <= 0 || framebuffer.Length < width * height)
            return;

        lock (_lock)
        {
            if (_disposed || !_deviceOk || !_swapOk ||
                _device == null || _queue == null || _cmdAlloc == null ||
                _cmdList == null || _swapChain == null || _fence == null)
                return;

            try
            {
                WaitForGpu_NoLock();

                EnsureStaging_NoLock(width, height);
                if (_upload == null || _gpuTexture == null)
                    return;

                UploadStaging_NoLock(framebuffer, width, height);

                _cmdAlloc.Reset();
                _cmdList.Reset(_cmdAlloc);

                // upload heap is GENERIC_READ; copy into DEFAULT texture then to backbuffer.
                if (_textureState != ResourceStates.CopyDest)
                {
                    _cmdList.ResourceBarrierTransition(
                        _gpuTexture, _textureState, ResourceStates.CopyDest);
                    _textureState = ResourceStates.CopyDest;
                }

                var srcLoc = new TextureCopyLocation(_upload, _uploadFootprint);
                var dstLoc = new TextureCopyLocation(_gpuTexture, 0);
                _cmdList.CopyTextureRegion(dstLoc, 0, 0, 0, srcLoc, null);

                _cmdList.ResourceBarrierTransition(
                    _gpuTexture, ResourceStates.CopyDest, ResourceStates.CopySource);
                _textureState = ResourceStates.CopySource;

                uint bbIndex = _swapChain.CurrentBackBufferIndex;
                using ID3D12Resource backBuffer = _swapChain.GetBuffer<ID3D12Resource>(bbIndex);

                _cmdList.ResourceBarrierTransition(
                    backBuffer, ResourceStates.Present, ResourceStates.CopyDest);

                CopyToBackBuffer_NoLock(backBuffer, width, height);

                _cmdList.ResourceBarrierTransition(
                    backBuffer, ResourceStates.CopyDest, ResourceStates.Present);

                _cmdList.Close();
                _queue.ExecuteCommandList(_cmdList);

                ulong signal = ++_fenceValue;
                _queue.Signal(_fence, signal);

                // 0 = no vsync wait (host UI owns pacing).
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
                Stats.LastError = "D3D12 Present: " + ex.Message;
                try
                {
                    // Best-effort re-close so the next Present can Reset.
                    _cmdList?.Close();
                }
                catch
                {
                    // ignored
                }
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
            try { WaitForGpu_NoLock(); } catch { /* ignore teardown races */ }
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
        if (_deviceOk && _device != null && _queue != null && _cmdList != null)
            return true;

        DestroyDevice_NoLock();

        try
        {
            _factory = CreateDXGIFactory2<IDXGIFactory4>(debug: false);

            ID3D12Device? device = null;
            FeatureLevel fl = 0;

            // Prefer FL12_0, then FL11_0 on the default hardware adapter.
            Result hr = D3D12CreateDevice(IntPtr.Zero, FeatureLevel.Level_12_0, out device);
            if (hr.Success && device != null)
            {
                fl = FeatureLevel.Level_12_0;
            }
            else
            {
                hr = D3D12CreateDevice(IntPtr.Zero, FeatureLevel.Level_11_0, out device);
                if (hr.Success && device != null)
                    fl = FeatureLevel.Level_11_0;
            }

            // WARP fallback for headless / broken GPU drivers.
            if (device == null || hr.Failure)
            {
                using IDXGIAdapter warp = _factory.EnumWarpAdapter<IDXGIAdapter>();
                hr = D3D12CreateDevice(warp, FeatureLevel.Level_11_0, out device);
                if (hr.Success && device != null)
                    fl = FeatureLevel.Level_11_0;
            }

            if (hr.Failure || device == null)
            {
                Stats.LastError = "D3D12CreateDevice failed: " + hr;
                Stats.DeviceReady = false;
                _deviceOk = false;
                DestroyDevice_NoLock();
                return false;
            }

            _device = device;
            FeatureLevel = fl;

            _queue = _device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
            _cmdAlloc = _device.CreateCommandAllocator(CommandListType.Direct);
            _cmdList = _device.CreateCommandList<ID3D12GraphicsCommandList>(
                CommandListType.Direct, _cmdAlloc, null);
            // Start closed; Present resets before recording.
            _cmdList.Close();

            _fence = _device.CreateFence(0, FenceFlags.None);
            _fenceValue = 0;
            _fenceEvent = new ManualResetEvent(false);

            _deviceOk = true;
            Stats.LastError = "";
            return true;
        }
        catch (Exception ex)
        {
            Stats.LastError = "D3D12 device: " + ex.Message;
            Stats.DeviceReady = false;
            _deviceOk = false;
            DestroyDevice_NoLock();
            return false;
        }
    }

    private bool CreateSwapChain_NoLock(nint hwnd, int width, int height)
    {
        if (_device == null || _queue == null || _factory == null)
            return false;

        try
        {
            var desc = new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = BufferCount,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.None,
            };

            // D3D12: CreateSwapChainForHwnd takes the command queue, not the device.
            using IDXGISwapChain1 sc1 = _factory.CreateSwapChainForHwnd(_queue, hwnd, desc);
            _factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
            _swapChain = sc1.QueryInterface<IDXGISwapChain3>();

            _outW = width;
            _outH = height;
            return _swapChain != null;
        }
        catch (Exception ex)
        {
            Stats.LastError = "D3D12 CreateSwapChain: " + ex.Message;
            _swapChain = null;
            return false;
        }
    }

    private void DestroySwapChain_NoLock()
    {
        try { WaitForGpu_NoLock(); } catch { /* ignore */ }
        _swapChain?.Dispose();
        _swapChain = null;
        _swapOk = false;
    }

    private void DestroyDevice_NoLock()
    {
        _cmdList?.Dispose();
        _cmdList = null;
        _cmdAlloc?.Dispose();
        _cmdAlloc = null;
        _queue?.Dispose();
        _queue = null;
        _fence?.Dispose();
        _fence = null;
        if (_fenceEvent != null)
        {
            _fenceEvent.Dispose();
            _fenceEvent = null;
        }
        _fenceValue = 0;
        _device?.Dispose();
        _device = null;
        _factory?.Dispose();
        _factory = null;
        _deviceOk = false;
        FeatureLevel = 0;
        _textureState = ResourceStates.Common;
    }

    private void WaitForGpu_NoLock()
    {
        if (_queue == null || _fence == null || _fenceEvent == null)
            return;

        ulong v = ++_fenceValue;
        _queue.Signal(_fence, v);
        if (_fence.CompletedValue < v)
        {
            _fenceEvent.Reset();
            _fence.SetEventOnCompletion(v, _fenceEvent);
            _fenceEvent.WaitOne(5000);
        }
    }

    // ── staging upload ───────────────────────────────────────────────────

    private void EnsureStaging_NoLock(int width, int height)
    {
        if (_device == null) return;
        if (_upload != null && _gpuTexture != null && _stageW == width && _stageH == height)
            return;

        DestroyStaging_NoLock();

        var texDesc = ResourceDescription.Texture2D(
            Format.B8G8R8A8_UNorm,
            (uint)width,
            (uint)height);

        var layouts = new PlacedSubresourceFootPrint[1];
        var numRows = new uint[1];
        var rowSizes = new ulong[1];
        _device.GetCopyableFootprints(texDesc, 0, 1, 0, layouts, numRows, rowSizes, out ulong totalBytes);

        _uploadFootprint = layouts[0];
        _uploadBytes = totalBytes;

        _upload = _device.CreateCommittedResource(
            HeapType.Upload,
            ResourceDescription.Buffer(totalBytes),
            ResourceStates.GenericRead);

        _gpuTexture = _device.CreateCommittedResource(
            HeapType.Default,
            texDesc,
            ResourceStates.Common);
        _textureState = ResourceStates.Common;

        _stageW = width;
        _stageH = height;
    }

    private void DestroyStaging_NoLock()
    {
        _upload?.Dispose();
        _upload = null;
        _gpuTexture?.Dispose();
        _gpuTexture = null;
        _stageW = _stageH = 0;
        _uploadBytes = 0;
        _uploadFootprint = default;
        _textureState = ResourceStates.Common;
    }

    private void UploadStaging_NoLock(ReadOnlySpan<uint> framebuffer, int width, int height)
    {
        if (_upload == null) return;

        int srcStride = width * 4;
        uint rowPitch = _uploadFootprint.Footprint.RowPitch;
        int mapBytes = (int)Math.Min(_uploadBytes, int.MaxValue);

        // Map upload heap (CPU-visible). Soft-GS pack is 0xAARRGGBB LE == B8G8R8A8_UNorm.
        Span<byte> mapped = _upload.Map<byte>(0, mapBytes);
        try
        {
            fixed (uint* srcPtr = &MemoryMarshal.GetReference(framebuffer))
            fixed (byte* dstBase0 = mapped)
            {
                byte* srcBase = (byte*)srcPtr;
                byte* dstBase = dstBase0 + (long)_uploadFootprint.Offset;

                if (rowPitch == (uint)srcStride)
                {
                    long bytes = (long)srcStride * height;
                    Buffer.MemoryCopy(srcBase, dstBase, bytes, bytes);
                }
                else
                {
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(
                            srcBase + (long)y * srcStride,
                            dstBase + (long)y * rowPitch,
                            rowPitch,
                            srcStride);
                    }
                }
            }
        }
        finally
        {
            _upload.Unmap(0, null);
        }
    }

    private void CopyToBackBuffer_NoLock(ID3D12Resource backBuffer, int srcW, int srcH)
    {
        if (_cmdList == null || _gpuTexture == null) return;

        ResourceDescription bbDesc = backBuffer.Description;
        if (bbDesc.Width == (ulong)srcW && bbDesc.Height == (uint)srcH)
        {
            _cmdList.CopyResource(backBuffer, _gpuTexture);
            return;
        }

        int copyW = Math.Min(srcW, (int)bbDesc.Width);
        int copyH = Math.Min(srcH, (int)bbDesc.Height);
        if (copyW <= 0 || copyH <= 0) return;

        // UpscaleMode is reserved for a future GPU blit pass; skeleton crops/copies top-left.
        _ = UpscaleMode;
        var box = new Box(0, 0, 0, copyW, copyH, 1);
        var src = new TextureCopyLocation(_gpuTexture, 0);
        var dst = new TextureCopyLocation(backBuffer, 0);
        _cmdList.CopyTextureRegion(dst, 0, 0, 0, src, box);
    }
}
