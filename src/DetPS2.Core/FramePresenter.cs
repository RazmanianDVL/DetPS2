using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Presentation backend abstraction (Phases 11/19).
/// Software GS remains the determinism source of truth; presenters only display.
/// </summary>
public interface IFramePresenter
{
    string Name { get; }
    void Present(ReadOnlySpan<uint> framebuffer, int width, int height);
    void Reset();
}

/// <summary>Default presenter: copies to a CPU-side snapshot (UI / tests pull from here).</summary>
public sealed class SoftwareFramePresenter : IFramePresenter
{
    public string Name => "Software";
    public uint[]? LastFrame { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public ulong PresentCount { get; private set; }

    public void Present(ReadOnlySpan<uint> framebuffer, int width, int height)
    {
        Width = width;
        Height = height;
        if (LastFrame == null || LastFrame.Length != framebuffer.Length)
            LastFrame = new uint[framebuffer.Length];
        framebuffer.CopyTo(LastFrame);
        PresentCount++;
    }

    public void Reset()
    {
        LastFrame = null;
        PresentCount = 0;
        Width = Height = 0;
    }

    /// <summary>
    /// Write last software present snapshot as PPM (display path). Soft-GS claim truth
    /// remains <see cref="Gs.SaveFramebufferAsPPM"/> / <c>--dump-softgs</c>.
    /// </summary>
    public bool TryDumpLastFramePpm(string path)
    {
        if (LastFrame == null || Width <= 0 || Height <= 0) return false;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        using var writer = new StreamWriter(path);
        writer.WriteLine("P3");
        writer.WriteLine($"{Width} {Height}");
        writer.WriteLine("255");
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                uint p = LastFrame[y * Width + x];
                writer.WriteLine($"{(p >> 16) & 0xFF} {(p >> 8) & 0xFF} {p & 0xFF}");
            }
        }
        return true;
    }
}

/// <summary>
/// GPU-style presenter (Phase 19): stages software FB into a "GPU texture" buffer
/// and tracks upload stats. Real Vulkan/OpenGL can replace the staging step later
/// without changing <see cref="PresentPipeline"/> consumers.
/// Determinism always hashes software GS, never this buffer.
/// </summary>
public sealed class GpuFramePresenter : IFramePresenter
{
    public string Name => "GPU";
    public bool BackendReady { get; private set; } = true;
    public uint[]? TextureRgba { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public ulong PresentCount { get; private set; }
    public ulong BytesUploaded { get; private set; }
    public ulong UploadCount { get; private set; }
    public float ScaleX { get; private set; } = 1f;
    public float ScaleY { get; private set; } = 1f;
    public int ScaledWidth { get; private set; }
    public int ScaledHeight { get; private set; }

    /// <summary>Simulated GPU upload: copy FB into texture staging.</summary>
    public void Present(ReadOnlySpan<uint> framebuffer, int width, int height)
    {
        Width = width;
        Height = height;
        ScaledWidth = Math.Max(1, (int)(width * ScaleX));
        ScaledHeight = Math.Max(1, (int)(height * ScaleY));
        int n = framebuffer.Length;
        if (TextureRgba == null || TextureRgba.Length != n)
            TextureRgba = new uint[n];

        // Stage as-is (ARGB from GS). Real backend: glTexSubImage / vkCmdCopyBufferToImage.
        framebuffer.CopyTo(TextureRgba);
        BytesUploaded += (ulong)n * 4;
        UploadCount++;
        PresentCount++;
        BackendReady = true;
    }

    public void ApplyDisplayScale(float sx, float sy)
    {
        ScaleX = sx <= 0 ? 1f : sx;
        ScaleY = sy <= 0 ? 1f : sy;
        ScaledWidth = Math.Max(1, (int)(Width * ScaleX));
        ScaledHeight = Math.Max(1, (int)(Height * ScaleY));
    }

    public void Reset()
    {
        TextureRgba = null;
        PresentCount = 0;
        BytesUploaded = 0;
        UploadCount = 0;
        Width = Height = 0;
        ScaledWidth = ScaledHeight = 0;
        ScaleX = ScaleY = 1f;
        BackendReady = true;
    }

    /// <summary>Sample staged pixel for tests (not used for determinism).</summary>
    public uint Sample(int x, int y)
    {
        if (TextureRgba == null || x < 0 || y < 0 || x >= Width || y >= Height)
            return 0;
        return TextureRgba[y * Width + x];
    }
}

/// <summary>Selects active presenter; always fed from software GS.</summary>
public sealed class PresentPipeline
{
    public IFramePresenter Active { get; private set; }
    public SoftwareFramePresenter Software { get; } = new();
    public GpuFramePresenter Gpu { get; } = new();
    public VulkanFramePresenter Vulkan { get; } = new();
    public AcceleratedFramePresenter Accelerated { get; } = new();
    public GsCommandBuffer CommandBuffer { get; } = new();

    /// <summary>
    /// When true (default for determinism tests), FB hash path always uses software GS;
    /// GPU present may still run for display but never becomes the truth source.
    /// </summary>
    public bool DeterminismMode { get; set; } = true;

    public PresentMode Mode { get; private set; } = PresentMode.Software;
    /// <summary>Perf path: enqueue present commands for GPU drain.</summary>
    public bool UseCommandBuffer { get; set; }

    public PresentPipeline() => Active = Software;

    public void UseSoftware()
    {
        Mode = PresentMode.Software;
        Active = Software;
    }

    public void UseGpu()
    {
        Mode = PresentMode.Gpu;
        Active = Gpu;
    }

    public void UseVulkan()
    {
        Mode = PresentMode.Vulkan;
        Active = Vulkan;
    }

    /// <summary>Phase 52: parallel CPU upscale present (Perf path; Det hash still software).</summary>
    public void UseAccelerated()
    {
        Mode = PresentMode.Accelerated;
        Active = Accelerated;
    }

    public void SetMode(PresentMode mode)
    {
        switch (mode)
        {
            case PresentMode.Software: UseSoftware(); break;
            case PresentMode.Gpu: UseGpu(); break;
            case PresentMode.Vulkan: UseVulkan(); break;
            case PresentMode.Accelerated: UseAccelerated(); break;
        }
    }

    public void PresentFromGs(Gs gs)
    {
        // Soft-GS truth: GetPresentSpan composites natural DISPFB (or FRAME residual) when
        // local IMAGE is present (GX-041). No DISPFB plant — only software-programmed regs.
        var span = gs.GetPresentSpan();
        int w = gs.FramebufferWidth;
        int h = gs.FramebufferHeight;

        // Always keep software snapshot when in determinism mode (hash path).
        if (DeterminismMode && Active != Software)
            Software.Present(span, w, h);

        Active.Present(span, w, h);

        if (UseCommandBuffer || Mode is PresentMode.Gpu or PresentMode.Vulkan or PresentMode.Accelerated)
        {
            CommandBuffer.EnqueuePresent();
            if (Active is VulkanFramePresenter vk)
                vk.DrainCommands(CommandBuffer);
            else
            {
                CommandBuffer.Drain(c =>
                {
                    if (c.Opcode == GsCommandBuffer.Op.Present && Active is GpuFramePresenter gpu)
                        gpu.ApplyDisplayScale(CommandBuffer.DisplayScaleX, CommandBuffer.DisplayScaleY);
                    if (c.Opcode == GsCommandBuffer.Op.SetScale && Active is AcceleratedFramePresenter acc)
                        acc.Scale = Math.Max(c.ScaleX, c.ScaleY);
                });
            }
        }
    }

    /// <summary>Determinism-safe FB hash: always software GS pixels.</summary>
    public ulong HashDeterministic(Gs gs) => RegressionFixtures.HashFramebuffer(gs);

    public void Reset()
    {
        Software.Reset();
        Gpu.Reset();
        Vulkan.Reset();
        Accelerated.Reset();
        CommandBuffer.Reset();
        DeterminismMode = true;
        UseCommandBuffer = false;
        UseSoftware();
    }
}

public enum PresentMode
{
    Software = 0,
    Gpu = 2,
    Vulkan = 3,
    Accelerated = 4
}
