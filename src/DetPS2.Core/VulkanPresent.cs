using System;
using System.Threading;

namespace DetPS2.Core;

/// <summary>
/// Phase 44/50: <b>software upscale present path</b> (Vulkan-shaped API).
/// Honest status: there is <b>no native Vulkan device</b> in-tree
/// (<see cref="VulkanDeviceReady"/> is always false). This class:
/// - stages software GS FB (Det truth remains GS software)
/// - performs CPU bilinear upscale into a display buffer
/// - drains GsCommandBuffer with deterministic join before hash
/// Native Silk.NET/Vortice device is Completeness Campaign Phase 52.
/// </summary>
public sealed class VulkanFramePresenter : IFramePresenter
{
    public string Name => _vulkanReady ? "Vulkan" : "SoftwareUpscale";
    public bool BackendReady => true;
    /// <summary>Always false until a real Vulkan device is wired (Phase 52).</summary>
    public bool VulkanDeviceReady => _vulkanReady;
    public uint[]? TextureRgba { get; private set; }
    public uint[]? DisplayBuffer { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int DisplayWidth { get; private set; }
    public int DisplayHeight { get; private set; }
    public ulong PresentCount { get; private set; }
    public ulong BytesUploaded { get; private set; }
    public ulong WorkerDrains { get; private set; }
    public float Scale { get; set; } = 1f;
    public bool BilinearUpscale { get; set; } = true;

    private bool _vulkanReady;
    private readonly object _lock = new();
    private readonly GsCommandBuffer _ownedQueue = new();
    private int _pendingPresents;

    public VulkanFramePresenter()
    {
        // Attempt "device" init — without native deps we mark software path.
        // Hook for future Silk.NET / Vortice Vulkan here.
        _vulkanReady = TryInitVulkanDevice();
    }

    private static bool TryInitVulkanDevice()
    {
        // Phase 50 honesty: no native Vulkan package — always software upscale.
        // Phase 52: return true after vkCreateDevice succeeds.
        return false;
    }

    public void Present(ReadOnlySpan<uint> framebuffer, int width, int height)
    {
        lock (_lock)
        {
            Width = width;
            Height = height;
            int n = framebuffer.Length;
            if (TextureRgba == null || TextureRgba.Length != n)
                TextureRgba = new uint[n];
            framebuffer.CopyTo(TextureRgba);
            BytesUploaded += (ulong)n * 4;

            float s = Scale <= 0 ? 1f : Scale;
            DisplayWidth = Math.Max(1, (int)(width * s));
            DisplayHeight = Math.Max(1, (int)(height * s));
            int dn = DisplayWidth * DisplayHeight;
            if (DisplayBuffer == null || DisplayBuffer.Length != dn)
                DisplayBuffer = new uint[dn];

            if (BilinearUpscale && (DisplayWidth != width || DisplayHeight != height))
                UpscaleBilinear(TextureRgba, width, height, DisplayBuffer, DisplayWidth, DisplayHeight);
            else
                NearestCopy(TextureRgba, width, height, DisplayBuffer, DisplayWidth, DisplayHeight);

            PresentCount++;
            _pendingPresents++;
        }
    }

    /// <summary>Worker-style drain of command buffer (join before Det hash).</summary>
    public void DrainCommands(GsCommandBuffer cmds)
    {
        int n = cmds.Drain(c =>
        {
            if (c.Opcode == GsCommandBuffer.Op.SetScale)
                Scale = Math.Max(c.ScaleX, c.ScaleY);
        });
        if (n > 0) WorkerDrains++;
        Interlocked.Exchange(ref _pendingPresents, 0);
    }

    public void Reset()
    {
        lock (_lock)
        {
            TextureRgba = null;
            DisplayBuffer = null;
            PresentCount = BytesUploaded = WorkerDrains = 0;
            Width = Height = DisplayWidth = DisplayHeight = 0;
            _pendingPresents = 0;
            _ownedQueue.Reset();
        }
    }

    private static void NearestCopy(uint[] src, int sw, int sh, uint[] dst, int dw, int dh)
    {
        for (int y = 0; y < dh; y++)
        {
            int sy = y * sh / dh;
            for (int x = 0; x < dw; x++)
            {
                int sx = x * sw / dw;
                dst[y * dw + x] = src[sy * sw + sx];
            }
        }
    }

    private static void UpscaleBilinear(uint[] src, int sw, int sh, uint[] dst, int dw, int dh)
    {
        for (int y = 0; y < dh; y++)
        {
            float v = (y + 0.5f) * sh / dh - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(v), 0, sh - 1);
            int y1 = Math.Min(y0 + 1, sh - 1);
            float fy = v - y0;
            for (int x = 0; x < dw; x++)
            {
                float u = (x + 0.5f) * sw / dw - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(u), 0, sw - 1);
                int x1 = Math.Min(x0 + 1, sw - 1);
                float fx = u - x0;
                uint c00 = src[y0 * sw + x0];
                uint c10 = src[y0 * sw + x1];
                uint c01 = src[y1 * sw + x0];
                uint c11 = src[y1 * sw + x1];
                dst[y * dw + x] = Bilerp(c00, c10, c01, c11, fx, fy);
            }
        }
    }

    private static uint Bilerp(uint c00, uint c10, uint c01, uint c11, float fx, float fy)
    {
        static byte L(byte a, byte b, float t) => (byte)(a + (b - a) * t);
        static byte C(uint c, int s) => (byte)((c >> s) & 0xFF);
        byte r = L(L(C(c00, 16), C(c10, 16), fx), L(C(c01, 16), C(c11, 16), fx), fy);
        byte g = L(L(C(c00, 8), C(c10, 8), fx), L(C(c01, 8), C(c11, 8), fx), fy);
        byte b = L(L(C(c00, 0), C(c10, 0), fx), L(C(c01, 0), C(c11, 0), fx), fy);
        byte a = L(L(C(c00, 24), C(c10, 24), fx), L(C(c01, 24), C(c11, 24), fx), fy);
        return (uint)((a << 24) | (r << 16) | (g << 8) | b);
    }
}

/// <summary>
/// Phase 52: host-accelerated present — parallel CPU upscale of software GS FB.
/// Honest: not native Vulkan/D3D device; Det truth remains software GS hash.
/// Faster multi-core blit/upscale for Desktop Perf mode.
/// </summary>
public sealed class AcceleratedFramePresenter : IFramePresenter
{
    public string Name => Parallel ? "AcceleratedParallel" : "Accelerated";
    public bool BackendReady => true;
    public bool NativeGpuDevice => false;
    public uint[]? TextureRgba { get; private set; }
    public uint[]? DisplayBuffer { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int DisplayWidth { get; private set; }
    public int DisplayHeight { get; private set; }
    public ulong PresentCount { get; private set; }
    public ulong BytesUploaded { get; private set; }
    public float Scale { get; set; } = 1f;
    public bool Parallel { get; set; } = true;
    public bool Bilinear { get; set; } = true;
    public int LastWorkerCount { get; private set; }

    public void Present(ReadOnlySpan<uint> framebuffer, int width, int height)
    {
        Width = width;
        Height = height;
        int n = framebuffer.Length;
        if (TextureRgba == null || TextureRgba.Length != n)
            TextureRgba = new uint[n];
        framebuffer.CopyTo(TextureRgba);
        BytesUploaded += (ulong)n * 4;

        float s = Scale <= 0 ? 1f : Scale;
        DisplayWidth = Math.Max(1, (int)(width * s));
        DisplayHeight = Math.Max(1, (int)(height * s));
        int dn = DisplayWidth * DisplayHeight;
        if (DisplayBuffer == null || DisplayBuffer.Length != dn)
            DisplayBuffer = new uint[dn];

        if (DisplayWidth == width && DisplayHeight == height)
        {
            Array.Copy(TextureRgba, DisplayBuffer, n);
            LastWorkerCount = 1;
        }
        else if (Bilinear)
            UpscaleParallel(TextureRgba, width, height, DisplayBuffer, DisplayWidth, DisplayHeight, Parallel);
        else
            NearestParallel(TextureRgba, width, height, DisplayBuffer, DisplayWidth, DisplayHeight, Parallel);

        PresentCount++;
    }

    private void UpscaleParallel(uint[] src, int sw, int sh, uint[] dst, int dw, int dh, bool parallel)
    {
        int workers = parallel ? Math.Max(1, Environment.ProcessorCount) : 1;
        LastWorkerCount = workers;
        if (workers == 1)
        {
            UpscaleRows(src, sw, sh, dst, dw, dh, 0, dh);
            return;
        }
        int rowsPer = (dh + workers - 1) / workers;
        System.Threading.Tasks.Parallel.For(0, workers, w =>
        {
            int y0 = w * rowsPer;
            int y1 = Math.Min(dh, y0 + rowsPer);
            if (y0 < y1)
                UpscaleRows(src, sw, sh, dst, dw, dh, y0, y1);
        });
    }

    private static void UpscaleRows(uint[] src, int sw, int sh, uint[] dst, int dw, int dh, int yStart, int yEnd)
    {
        for (int y = yStart; y < yEnd; y++)
        {
            float v = (y + 0.5f) * sh / dh - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(v), 0, sh - 1);
            int y1 = Math.Min(y0 + 1, sh - 1);
            float fy = v - y0;
            for (int x = 0; x < dw; x++)
            {
                float u = (x + 0.5f) * sw / dw - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(u), 0, sw - 1);
                int x1 = Math.Min(x0 + 1, sw - 1);
                float fx = u - x0;
                uint c00 = src[y0 * sw + x0];
                uint c10 = src[y0 * sw + x1];
                uint c01 = src[y1 * sw + x0];
                uint c11 = src[y1 * sw + x1];
                dst[y * dw + x] = BilerpAccel(c00, c10, c01, c11, fx, fy);
            }
        }
    }

    private void NearestParallel(uint[] src, int sw, int sh, uint[] dst, int dw, int dh, bool parallel)
    {
        int workers = parallel ? Math.Max(1, Environment.ProcessorCount) : 1;
        LastWorkerCount = workers;
        System.Threading.Tasks.Parallel.For(0, dh, y =>
        {
            int sy = y * sh / dh;
            int row = y * dw;
            int srow = sy * sw;
            for (int x = 0; x < dw; x++)
                dst[row + x] = src[srow + x * sw / dw];
        });
    }

    private static uint BilerpAccel(uint c00, uint c10, uint c01, uint c11, float fx, float fy)
    {
        static byte L(byte a, byte b, float t) => (byte)(a + (b - a) * t);
        static byte C(uint c, int s) => (byte)((c >> s) & 0xFF);
        byte r = L(L(C(c00, 16), C(c10, 16), fx), L(C(c01, 16), C(c11, 16), fx), fy);
        byte g = L(L(C(c00, 8), C(c10, 8), fx), L(C(c01, 8), C(c11, 8), fx), fy);
        byte b = L(L(C(c00, 0), C(c10, 0), fx), L(C(c01, 0), C(c11, 0), fx), fy);
        byte a = L(L(C(c00, 24), C(c10, 24), fx), L(C(c01, 24), C(c11, 24), fx), fy);
        return (uint)((a << 24) | (r << 16) | (g << 8) | b);
    }

    public void Reset()
    {
        TextureRgba = DisplayBuffer = null;
        PresentCount = BytesUploaded = 0;
        Width = Height = DisplayWidth = DisplayHeight = 0;
        LastWorkerCount = 0;
    }
}
