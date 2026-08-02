using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Buffer = Silk.NET.Vulkan.Buffer;
using Image = Silk.NET.Vulkan.Image;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace DetPS2.Present;

/// <summary>
/// Real Vulkan device path (Silk.NET.Vulkan) implementing <see cref="IHostSwapPresenter"/>.
/// Soft-GS remains determinism truth; this class is host display only.
///
/// Lifecycle:
/// 1. ctor → CreateInstance → pick physical device → create logical device + command pool
/// 2. <see cref="AttachWindow"/> → Win32 surface + swapchain + frame sync
/// 3. <see cref="Present"/> → host-visible staging buffer upload → transition/copy/blit →
///    AcquireNextImage → submit → QueuePresentKHR (CPU stage only when no swapchain)
///
/// Env: <c>DETPS2_VK_VALIDATE=1</c> enables <c>VK_LAYER_KHRONOS_validation</c> when available.
/// </summary>
public sealed unsafe class VulkanSwapPresenter : IHostSwapPresenter
{
    public const string ValidationLayerName = "VK_LAYER_KHRONOS_validation";
    public const string ValidateEnvVar = "DETPS2_VK_VALIDATE";
    private const int MaxFramesInFlight = 2;

    private static readonly string[] s_validationLayers = { ValidationLayerName };
    private static readonly string[] s_deviceExtensions = { KhrSwapchain.ExtensionName };

    private readonly object _lock = new();
    private Vk? _vk;
    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Silk.NET.Vulkan.Queue _graphicsQueue;
    private Silk.NET.Vulkan.Queue _presentQueue;
    private uint _graphicsFamily;
    private uint _presentFamily;

    private ExtDebugUtils? _debugUtils;
    private DebugUtilsMessengerEXT _debugMessenger;
    private bool _validationEnabled;
    private bool _deviceOk;

    private KhrSurface? _khrSurface;
    private KhrWin32Surface? _khrWin32;
    private KhrSwapchain? _khrSwapchain;
    private SurfaceKHR _surface;
    private SwapchainKHR _swapchain;
    private Image[]? _swapchainImages;
    private Format _swapchainFormat;
    private Extent2D _swapchainExtent;

    // Per-frame sync + command buffers (double-buffer)
    private CommandPool _commandPool;
    private CommandBuffer[]? _commandBuffers;
    private Semaphore[]? _imageAvailable;
    private Semaphore[]? _renderFinished;
    private Fence[]? _inFlightFences;
    private Fence[]? _imagesInFlight; // per swapchain image
    private int _currentFrame;

    // Host-visible staging buffer for Soft-GS BGRA
    private Buffer _stagingBuffer;
    private DeviceMemory _stagingMemory;
    private void* _stagingMapped;
    private ulong _stagingCapacity;
    private int _stagingBufW;
    private int _stagingBufH;

    // Device-local upload image (TransferDst|TransferSrc) for copy + optional blit scale
    private Image _uploadImage;
    private DeviceMemory _uploadImageMemory;
    private int _uploadImageW;
    private int _uploadImageH;
    private ImageLayout _uploadImageLayout;

    private nint _hwnd;
    private int _windowW;
    private int _windowH;

    private uint[]? _stagingBgra;
    private bool _disposed;

    public PresentBackend Backend => PresentBackend.Vulkan;
    public string Name => DeviceReady ? "Vulkan" : "Vulkan(not-ready)";
    public UpscaleMode UpscaleMode { get; set; } = UpscaleMode.None;
    public PresentStats Stats { get; } = new() { BackendName = "Vulkan", DeviceReady = false };

    /// <summary>
    /// Logical device created successfully. Staging Present works without a swapchain.
    /// On-screen present requires <see cref="SwapchainReady"/>.
    /// </summary>
    public bool DeviceReady => _deviceOk;

    /// <summary>True after successful <see cref="AttachWindow"/> (surface created).</summary>
    public bool WindowAttached => _hwnd != 0 && _surface.Handle != 0;

    public bool SurfaceReady => _surface.Handle != 0;
    public bool SwapchainReady => _swapchain.Handle != 0;
    public bool ValidationEnabled => _validationEnabled;
    public string? DeviceName { get; private set; }
    public string Status { get; private set; } = "uninitialized";
    public string? LastError { get; private set; }

    public int StagingWidth { get; private set; }
    public int StagingHeight { get; private set; }
    public ulong PresentCount => Stats.PresentCount;
    public ulong BytesUploaded => Stats.BytesUploaded;
    public ulong UploadCount => Stats.UploadCount;
    public ulong SwapchainRecreates { get; private set; }
    public ulong GpuPresentCount { get; private set; }

    /// <summary>CPU-side staging of last Present (0xAARRGGBB words from Soft-GS).</summary>
    public ReadOnlySpan<uint> StagingBgra =>
        _stagingBgra is null ? ReadOnlySpan<uint>.Empty : _stagingBgra.AsSpan(0, Math.Max(0, StagingWidth * StagingHeight));

    public VulkanSwapPresenter(bool? enableValidation = null)
    {
        bool wantValidate = enableValidation
            ?? string.Equals(Environment.GetEnvironmentVariable(ValidateEnvVar), "1", StringComparison.Ordinal);
        try
        {
            InitDevice(wantValidate);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Stats.LastError = ex.Message;
            Status = "init-failed";
            _deviceOk = false;
            Stats.DeviceReady = false;
            TearDownDeviceUnlocked();
        }
    }

    public bool AttachWindow(nint hwnd, int width, int height)
    {
        if (hwnd == 0 || width <= 0 || height <= 0)
        {
            LastError = "Vulkan: invalid hwnd/size";
            Stats.LastError = LastError;
            return false;
        }

        lock (_lock)
        {
            if (_disposed) return false;
            if (!_deviceOk || _vk is null)
            {
                Status = "attach-skipped-no-device";
                Stats.LastError = "Vulkan: no device";
                return false;
            }

            width = Math.Max(1, width);
            height = Math.Max(1, height);
            _hwnd = hwnd;
            _windowW = width;
            _windowH = height;

            try
            {
                DestroySwapchainUnlocked();
                DestroySurfaceUnlocked();
                CreateWin32SurfaceUnlocked(hwnd);
                if (!EnsurePresentQueueUnlocked())
                {
                    Status = "attach-no-present-queue";
                    Stats.LastError = "Vulkan: no present queue for surface";
                    return false;
                }

                CreateOrRecreateSwapchainUnlocked(width, height);
                Stats.OutputWidth = width;
                Stats.OutputHeight = height;
                Stats.DeviceReady = _deviceOk;
                Stats.LastError = LastError ?? "";
                Status = SwapchainReady
                    ? $"attached hwnd=0x{hwnd:X} {width}x{height} swapchain"
                    : $"attached hwnd=0x{hwnd:X} surface-only (swapchain failed)";
                return SurfaceReady;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Stats.LastError = "Vulkan AttachWindow: " + ex.Message;
                Status = "attach-failed";
                return false;
            }
        }
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        lock (_lock)
        {
            if (_disposed || !_deviceOk || !SurfaceReady) return;
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (width == _windowW && height == _windowH && SwapchainReady)
                return;

            _windowW = width;
            _windowH = height;
            try
            {
                CreateOrRecreateSwapchainUnlocked(width, height);
                Stats.OutputWidth = width;
                Stats.OutputHeight = height;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Stats.LastError = "Vulkan Resize: " + ex.Message;
            }
        }
    }

    /// <summary>
    /// Stage Soft-GS pixels and, when swapchain is ready, upload via host-visible buffer,
    /// copy/blit to the acquired swapchain image, submit, and present.
    /// Safe no-op when device is not ready. Never throws.
    /// </summary>
    public void Present(ReadOnlySpan<uint> framebuffer, int width, int height)
    {
        if (!_deviceOk || width <= 0 || height <= 0 || framebuffer.IsEmpty)
            return;
        if (framebuffer.Length < width * height)
            return;

        lock (_lock)
        {
            if (_disposed || !_deviceOk || _vk is null) return;

            try
            {
                int n = width * height;
                if (_stagingBgra is null || _stagingBgra.Length < n)
                    _stagingBgra = new uint[n];
                framebuffer.Slice(0, n).CopyTo(_stagingBgra);
                StagingWidth = width;
                StagingHeight = height;

                Stats.SourceWidth = width;
                Stats.SourceHeight = height;
                Stats.BytesUploaded += (ulong)n * 4;
                Stats.UploadCount++;
                Stats.PresentCount++;
                Stats.DeviceReady = true;

                if (!SwapchainReady || _khrSwapchain is null || _swapchainImages is null)
                {
                    Status = $"present#{Stats.PresentCount} staged {width}x{height} (device only)";
                    return;
                }

                if (!EnsureStagingResourcesUnlocked(width, height))
                {
                    Status = $"present#{Stats.PresentCount} staged {width}x{height} (staging alloc fail)";
                    return;
                }

                // Upload Soft-GS BGRA into host-visible staging buffer.
                UploadStagingBufferUnlocked(framebuffer, width, height);

                if (!GpuPresentUnlocked(width, height))
                {
                    // CPU stage still counted; GPU path failed softly.
                    Status = $"present#{Stats.PresentCount} staged {width}x{height} (gpu fail: {LastError})";
                    return;
                }

                GpuPresentCount++;
                Status = $"present#{Stats.PresentCount} gpu#{GpuPresentCount} {width}x{height}→{_swapchainExtent.Width}x{_swapchainExtent.Height}";
                Stats.LastError = LastError ?? "";
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Stats.LastError = "Vulkan Present: " + ex.Message;
                Status = $"present-error: {ex.Message}";
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _stagingBgra = null;
            StagingWidth = StagingHeight = 0;
            GpuPresentCount = 0;
            Stats.ClearCounters();
            Stats.DeviceReady = _deviceOk;
            Stats.LastError = LastError ?? "";
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            TearDownDeviceUnlocked();
            _deviceOk = false;
            Stats.DeviceReady = false;
            Status = "disposed";
        }
        GC.SuppressFinalize(this);
    }

    // ── init ──────────────────────────────────────────────────────────────

    private void InitDevice(bool wantValidate)
    {
        _vk = Vk.GetApi();
        _validationEnabled = wantValidate && CheckValidationLayerSupport();

        CreateInstanceUnlocked();
        if (_validationEnabled)
            SetupDebugMessengerUnlocked();

        if (!PickPhysicalDeviceUnlocked())
            throw new InvalidOperationException("no suitable Vulkan physical device");

        CreateLogicalDeviceUnlocked();
        CreateCommandPoolUnlocked();
        _deviceOk = _device.Handle != 0;
        Stats.DeviceReady = _deviceOk;
        Stats.BackendName = "Vulkan";
        Status = _deviceOk
            ? $"device-ready name={DeviceName} validate={_validationEnabled}"
            : "device-not-ready";
    }

    private void CreateInstanceUnlocked()
    {
        var vk = _vk!;
        ApplicationInfo appInfo = new()
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)Marshal.StringToHGlobalAnsi("DetPS2"),
            ApplicationVersion = new Version32(0, 5, 2),
            PEngineName = (byte*)Marshal.StringToHGlobalAnsi("DetPS2.Present"),
            EngineVersion = new Version32(0, 5, 2),
            ApiVersion = Vk.Version12
        };

        var extensions = new List<string>
        {
            KhrSurface.ExtensionName,
            KhrWin32Surface.ExtensionName
        };
        if (_validationEnabled)
            extensions.Add(ExtDebugUtils.ExtensionName);

        InstanceCreateInfo createInfo = new()
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions.ToArray())
        };

        if (_validationEnabled)
        {
            createInfo.EnabledLayerCount = (uint)s_validationLayers.Length;
            createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(s_validationLayers);
        }

        Result r = vk.CreateInstance(in createInfo, null, out _instance);
        Marshal.FreeHGlobal((nint)appInfo.PApplicationName);
        Marshal.FreeHGlobal((nint)appInfo.PEngineName);
        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
        if (_validationEnabled)
            SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);

        if (r != Result.Success)
            throw new InvalidOperationException($"vkCreateInstance failed: {r}");

        if (!vk.TryGetInstanceExtension(_instance, out _khrSurface))
            throw new NotSupportedException("VK_KHR_surface not available");
        if (!vk.TryGetInstanceExtension(_instance, out _khrWin32))
            throw new NotSupportedException("VK_KHR_win32_surface not available");
    }

    private void SetupDebugMessengerUnlocked()
    {
        if (!_validationEnabled || _vk is null) return;
        if (!_vk.TryGetInstanceExtension(_instance, out _debugUtils) || _debugUtils is null)
            return;

        DebugUtilsMessengerCreateInfoEXT ci = new()
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                              | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                          | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                          | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = (DebugUtilsMessengerCallbackFunctionEXT)DebugCallback
        };
        _debugUtils.CreateDebugUtilsMessenger(_instance, in ci, null, out _debugMessenger);
    }

    private static uint DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT types,
        DebugUtilsMessengerCallbackDataEXT* pCallbackData,
        void* pUserData)
    {
        string msg = pCallbackData is null
            ? "(null)"
            : Marshal.PtrToStringAnsi((nint)pCallbackData->PMessage) ?? "(null)";
        Console.Error.WriteLine($"[DetPS2.VK] {severity}: {msg}");
        return Vk.False;
    }

    private bool PickPhysicalDeviceUnlocked()
    {
        var vk = _vk!;
        var devices = vk.GetPhysicalDevices(_instance);
        PhysicalDevice best = default;
        int bestScore = -1;
        string? bestName = null;

        foreach (var dev in devices)
        {
            if (!HasGraphicsQueue(dev))
                continue;
            if (!CheckDeviceExtensionSupport(dev))
                continue;

            vk.GetPhysicalDeviceProperties(dev, out var props);
            int score = props.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 300,
                PhysicalDeviceType.IntegratedGpu => 200,
                PhysicalDeviceType.VirtualGpu => 100,
                _ => 50
            };
            if (score > bestScore)
            {
                bestScore = score;
                best = dev;
                bestName = Marshal.PtrToStringAnsi((nint)props.DeviceName) ?? "unknown";
            }
        }

        if (best.Handle == 0)
            return false;

        _physicalDevice = best;
        DeviceName = bestName;
        _graphicsFamily = FindGraphicsQueueFamily(best)
            ?? throw new InvalidOperationException("graphics queue missing after filter");
        _presentFamily = _graphicsFamily;
        return true;
    }

    private void CreateLogicalDeviceUnlocked()
    {
        var vk = _vk!;
        float priority = 1f;
        DeviceQueueCreateInfo queueInfo = new()
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _graphicsFamily,
            QueueCount = 1,
            PQueuePriorities = &priority
        };

        PhysicalDeviceFeatures features = new();
        DeviceCreateInfo createInfo = new()
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
            PEnabledFeatures = &features,
            EnabledExtensionCount = (uint)s_deviceExtensions.Length,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(s_deviceExtensions)
        };

        if (_validationEnabled)
        {
            createInfo.EnabledLayerCount = (uint)s_validationLayers.Length;
            createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(s_validationLayers);
        }

        Result r = vk.CreateDevice(_physicalDevice, in createInfo, null, out _device);
        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
        if (_validationEnabled)
            SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);

        if (r != Result.Success)
            throw new InvalidOperationException($"vkCreateDevice failed: {r}");

        vk.GetDeviceQueue(_device, _graphicsFamily, 0, out _graphicsQueue);
        _presentQueue = _graphicsQueue;

        if (!vk.TryGetDeviceExtension(_instance, _device, out _khrSwapchain))
            throw new NotSupportedException("VK_KHR_swapchain not available on device");
    }

    private void CreateCommandPoolUnlocked()
    {
        var vk = _vk!;
        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _graphicsFamily
        };
        Result r = vk.CreateCommandPool(_device, in poolInfo, null, out _commandPool);
        if (r != Result.Success)
            throw new InvalidOperationException($"vkCreateCommandPool failed: {r}");
    }

    // ── surface / swapchain ───────────────────────────────────────────────

    private void CreateWin32SurfaceUnlocked(nint hwnd)
    {
        if (_khrWin32 is null)
            throw new InvalidOperationException("Win32 surface extension missing");

        nint hinstance = GetModuleHandleW(null);
        Win32SurfaceCreateInfoKHR ci = new()
        {
            SType = StructureType.Win32SurfaceCreateInfoKhr,
            Hinstance = hinstance,
            Hwnd = hwnd
        };

        Result r = _khrWin32.CreateWin32Surface(_instance, in ci, null, out _surface);
        if (r != Result.Success)
            throw new InvalidOperationException($"vkCreateWin32SurfaceKHR failed: {r}");
    }

    private bool EnsurePresentQueueUnlocked()
    {
        if (_khrSurface is null || _surface.Handle == 0)
            return false;

        uint count = 0;
        _vk!.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref count, null);
        var props = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = props)
            _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref count, p);

        uint? present = null;
        for (uint i = 0; i < count; i++)
        {
            _khrSurface.GetPhysicalDeviceSurfaceSupport(_physicalDevice, i, _surface, out Bool32 support);
            if (support)
            {
                if (i == _graphicsFamily)
                {
                    present = i;
                    break;
                }
                present ??= i;
            }
        }

        if (present is null)
            return false;

        _presentFamily = present.Value;
        _vk.GetDeviceQueue(_device, _presentFamily, 0, out _presentQueue);
        if (_presentFamily != _graphicsFamily)
        {
            LastError = $"present family {_presentFamily} != graphics {_graphicsFamily}; multi-queue recreate TODO";
            Stats.LastError = LastError;
        }
        return true;
    }

    private void CreateOrRecreateSwapchainUnlocked(int width, int height)
    {
        if (_khrSurface is null || _khrSwapchain is null || _surface.Handle == 0 || _vk is null)
            return;

        DestroySwapchainUnlocked();

        var support = QuerySwapchainSupport();
        if (support.Formats.Length == 0 || support.PresentModes.Length == 0)
        {
            LastError = "swapchain support incomplete";
            Stats.LastError = LastError;
            return;
        }

        // Device was created with graphics family only.
        if (_graphicsFamily != _presentFamily)
        {
            LastError = $"swapchain deferred: present family {_presentFamily} != graphics {_graphicsFamily}";
            Stats.LastError = LastError;
            return;
        }

        SurfaceFormatKHR surfaceFormat = ChooseSurfaceFormat(support.Formats);
        PresentModeKHR presentMode = ChoosePresentMode(support.PresentModes);
        Extent2D extent = ChooseExtent(support.Capabilities, (uint)width, (uint)height);

        if (extent.Width == 0 || extent.Height == 0)
        {
            LastError = "swapchain extent is zero (window minimized?)";
            Stats.LastError = LastError;
            return;
        }

        uint imageCount = support.Capabilities.MinImageCount + 1;
        if (support.Capabilities.MaxImageCount > 0 && imageCount > support.Capabilities.MaxImageCount)
            imageCount = support.Capabilities.MaxImageCount;

        SwapchainCreateInfoKHR ci = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = support.Capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
            OldSwapchain = default
        };

        Result r = _khrSwapchain.CreateSwapchain(_device, in ci, null, out _swapchain);
        if (r != Result.Success)
        {
            LastError = $"vkCreateSwapchainKHR failed: {r}";
            Stats.LastError = LastError;
            _swapchain = default;
            return;
        }

        _khrSwapchain.GetSwapchainImages(_device, _swapchain, ref imageCount, null);
        _swapchainImages = new Image[imageCount];
        fixed (Image* imgs = _swapchainImages)
            _khrSwapchain.GetSwapchainImages(_device, _swapchain, ref imageCount, imgs);

        _swapchainFormat = surfaceFormat.Format;
        _swapchainExtent = extent;
        SwapchainRecreates++;

        CreateFrameSyncUnlocked(imageCount);
        LastError = null;
        Stats.LastError = "";
    }

    private void CreateFrameSyncUnlocked(uint swapchainImageCount)
    {
        var vk = _vk!;
        DestroyFrameSyncUnlocked();

        _commandBuffers = new CommandBuffer[MaxFramesInFlight];
        _imageAvailable = new Semaphore[MaxFramesInFlight];
        _renderFinished = new Semaphore[MaxFramesInFlight];
        _inFlightFences = new Fence[MaxFramesInFlight];
        _imagesInFlight = new Fence[swapchainImageCount];
        _currentFrame = 0;

        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = MaxFramesInFlight
        };
        fixed (CommandBuffer* pCmd = _commandBuffers)
        {
            Result ar = vk.AllocateCommandBuffers(_device, in allocInfo, pCmd);
            if (ar != Result.Success)
                throw new InvalidOperationException($"vkAllocateCommandBuffers failed: {ar}");
        }

        SemaphoreCreateInfo semInfo = new() { SType = StructureType.SemaphoreCreateInfo };
        FenceCreateInfo fenceInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit
        };

        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            if (vk.CreateSemaphore(_device, in semInfo, null, out _imageAvailable[i]) != Result.Success
                || vk.CreateSemaphore(_device, in semInfo, null, out _renderFinished[i]) != Result.Success
                || vk.CreateFence(_device, in fenceInfo, null, out _inFlightFences[i]) != Result.Success)
            {
                throw new InvalidOperationException("failed to create frame sync objects");
            }
        }
    }

    private SwapchainSupport QuerySwapchainSupport()
    {
        var details = new SwapchainSupport();
        _khrSurface!.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out details.Capabilities);

        uint formatCount = 0;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, ref formatCount, null);
        details.Formats = formatCount == 0 ? Array.Empty<SurfaceFormatKHR>() : new SurfaceFormatKHR[formatCount];
        if (formatCount != 0)
        {
            fixed (SurfaceFormatKHR* p = details.Formats)
                _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, ref formatCount, p);
        }

        uint modeCount = 0;
        _khrSurface.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, ref modeCount, null);
        details.PresentModes = modeCount == 0 ? Array.Empty<PresentModeKHR>() : new PresentModeKHR[modeCount];
        if (modeCount != 0)
        {
            fixed (PresentModeKHR* p = details.PresentModes)
                _khrSurface.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, ref modeCount, p);
        }

        return details;
    }

    private static SurfaceFormatKHR ChooseSurfaceFormat(SurfaceFormatKHR[] formats)
    {
        // Prefer UNORM so Soft-GS BGRA host buffer matches CmdCopyImage without swizzle.
        foreach (var f in formats)
        {
            if (f.Format == Format.B8G8R8A8Unorm
                && f.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                return f;
        }
        foreach (var f in formats)
        {
            if (f.Format == Format.B8G8R8A8Unorm)
                return f;
        }
        foreach (var f in formats)
        {
            if (f.Format is Format.B8G8R8A8Srgb
                && f.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                return f;
        }
        return formats[0];
    }

    private static PresentModeKHR ChoosePresentMode(PresentModeKHR[] modes)
    {
        foreach (var m in modes)
        {
            if (m == PresentModeKHR.MailboxKhr)
                return m;
        }
        return PresentModeKHR.FifoKhr;
    }

    private static Extent2D ChooseExtent(SurfaceCapabilitiesKHR caps, uint width, uint height)
    {
        if (caps.CurrentExtent.Width != uint.MaxValue)
            return caps.CurrentExtent;
        return new Extent2D
        {
            Width = Math.Clamp(width, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
            Height = Math.Clamp(height, caps.MinImageExtent.Height, caps.MaxImageExtent.Height)
        };
    }

    // ── staging buffer / upload image ─────────────────────────────────────

    private bool EnsureStagingResourcesUnlocked(int width, int height)
    {
        if (_vk is null || !_deviceOk) return false;
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        ulong needed = (ulong)width * (ulong)height * 4UL;
        if (_stagingBuffer.Handle == 0 || _stagingCapacity < needed)
        {
            DestroyStagingBufferUnlocked();
            if (!CreateStagingBufferUnlocked(needed))
                return false;
            _stagingBufW = width;
            _stagingBufH = height;
        }
        else
        {
            _stagingBufW = width;
            _stagingBufH = height;
        }

        if (_uploadImage.Handle == 0 || _uploadImageW != width || _uploadImageH != height)
        {
            DestroyUploadImageUnlocked();
            if (!CreateUploadImageUnlocked(width, height))
                return false;
        }

        return true;
    }

    private bool CreateStagingBufferUnlocked(ulong size)
    {
        var vk = _vk!;
        size = Math.Max(size, 256);

        BufferCreateInfo bufInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive
        };

        Result r = vk.CreateBuffer(_device, in bufInfo, null, out _stagingBuffer);
        if (r != Result.Success)
        {
            LastError = $"vkCreateBuffer staging: {r}";
            Stats.LastError = LastError;
            return false;
        }

        vk.GetBufferMemoryRequirements(_device, _stagingBuffer, out var memReq);
        if (!AllocateMemoryUnlocked(memReq,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _stagingMemory))
        {
            vk.DestroyBuffer(_device, _stagingBuffer, null);
            _stagingBuffer = default;
            return false;
        }

        vk.BindBufferMemory(_device, _stagingBuffer, _stagingMemory, 0);
        r = vk.MapMemory(_device, _stagingMemory, 0, size, 0, ref _stagingMapped);
        if (r != Result.Success)
        {
            LastError = $"vkMapMemory staging: {r}";
            Stats.LastError = LastError;
            DestroyStagingBufferUnlocked();
            return false;
        }

        _stagingCapacity = size;
        return true;
    }

    private bool CreateUploadImageUnlocked(int width, int height)
    {
        var vk = _vk!;
        // Match swapchain format when ready so CmdCopyImage is legal; Soft-GS bytes stay BGRA LE.
        Format imgFormat = _swapchainFormat != default ? _swapchainFormat : Format.B8G8R8A8Unorm;
        if (imgFormat is not (Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb or Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb))
            imgFormat = Format.B8G8R8A8Unorm;

        ImageCreateInfo imgInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = imgFormat,
            Extent = new Extent3D { Width = (uint)width, Height = (uint)height, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };

        Result r = vk.CreateImage(_device, in imgInfo, null, out _uploadImage);
        if (r != Result.Success)
        {
            LastError = $"vkCreateImage upload: {r}";
            Stats.LastError = LastError;
            return false;
        }

        vk.GetImageMemoryRequirements(_device, _uploadImage, out var memReq);
        if (!AllocateMemoryUnlocked(memReq, MemoryPropertyFlags.DeviceLocalBit, out _uploadImageMemory))
        {
            // Fall back to host-visible if device-local unavailable for this type.
            if (!AllocateMemoryUnlocked(memReq, MemoryPropertyFlags.HostVisibleBit, out _uploadImageMemory))
            {
                vk.DestroyImage(_device, _uploadImage, null);
                _uploadImage = default;
                return false;
            }
        }

        vk.BindImageMemory(_device, _uploadImage, _uploadImageMemory, 0);
        _uploadImageW = width;
        _uploadImageH = height;
        _uploadImageLayout = ImageLayout.Undefined;
        return true;
    }

    private bool AllocateMemoryUnlocked(MemoryRequirements req, MemoryPropertyFlags props, out DeviceMemory memory)
    {
        memory = default;
        var vk = _vk!;
        vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var memProps);

        uint typeIndex = uint.MaxValue;
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((req.MemoryTypeBits & (1u << (int)i)) == 0)
                continue;
            if ((memProps.MemoryTypes[(int)i].PropertyFlags & props) == props)
            {
                typeIndex = i;
                break;
            }
        }

        if (typeIndex == uint.MaxValue)
        {
            LastError = $"no memory type for flags {props}";
            Stats.LastError = LastError;
            return false;
        }

        MemoryAllocateInfo alloc = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = typeIndex
        };
        Result r = vk.AllocateMemory(_device, in alloc, null, out memory);
        if (r != Result.Success)
        {
            LastError = $"vkAllocateMemory: {r}";
            Stats.LastError = LastError;
            memory = default;
            return false;
        }
        return true;
    }

    private void UploadStagingBufferUnlocked(ReadOnlySpan<uint> framebuffer, int width, int height)
    {
        if (_stagingMapped is null) return;
        int n = width * height;
        long bytes = (long)n * 4;
        fixed (uint* src = &MemoryMarshal.GetReference(framebuffer))
        {
            System.Buffer.MemoryCopy(src, _stagingMapped, (long)_stagingCapacity, bytes);
        }
    }

    // ── GPU present path ──────────────────────────────────────────────────

    private bool GpuPresentUnlocked(int srcW, int srcH)
    {
        var vk = _vk!;
        var khr = _khrSwapchain!;
        if (_commandBuffers is null || _imageAvailable is null || _renderFinished is null
            || _inFlightFences is null || _imagesInFlight is null || _swapchainImages is null)
            return false;

        int frame = _currentFrame;
        Fence fence = _inFlightFences[frame];

        // Wait for previous use of this frame slot.
        vk.WaitForFences(_device, 1, in fence, true, ulong.MaxValue);

        uint imageIndex = 0;
        Result acq = khr.AcquireNextImage(_device, _swapchain, ulong.MaxValue, _imageAvailable[frame], default, ref imageIndex);
        if (acq is Result.ErrorOutOfDateKhr)
        {
            CreateOrRecreateSwapchainUnlocked(_windowW, _windowH);
            return false;
        }
        if (acq is not (Result.Success or Result.SuboptimalKhr))
        {
            LastError = $"vkAcquireNextImageKHR: {acq}";
            Stats.LastError = LastError;
            return false;
        }

        // If a previous frame is still using this swapchain image, wait on its fence.
        if (_imagesInFlight[imageIndex].Handle != 0)
        {
            Fence imgFence = _imagesInFlight[imageIndex];
            vk.WaitForFences(_device, 1, in imgFence, true, ulong.MaxValue);
        }
        _imagesInFlight[imageIndex] = fence;

        vk.ResetFences(_device, 1, in fence);

        CommandBuffer cmd = _commandBuffers[frame];
        vk.ResetCommandBuffer(cmd, 0);

        if (!RecordPresentCommandsUnlocked(cmd, imageIndex, srcW, srcH))
            return false;

        Semaphore waitSem = _imageAvailable[frame];
        Semaphore signalSem = _renderFinished[frame];
        PipelineStageFlags waitStage = PipelineStageFlags.TransferBit;

        SubmitInfo submit = new()
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSem,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSem
        };

        Result sr = vk.QueueSubmit(_graphicsQueue, 1, in submit, fence);
        if (sr != Result.Success)
        {
            LastError = $"vkQueueSubmit: {sr}";
            Stats.LastError = LastError;
            return false;
        }

        SwapchainKHR sc = _swapchain;
        PresentInfoKHR presentInfo = new()
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSem,
            SwapchainCount = 1,
            PSwapchains = &sc,
            PImageIndices = &imageIndex
        };

        Result pr = khr.QueuePresent(_presentQueue, in presentInfo);
        if (pr is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
        {
            CreateOrRecreateSwapchainUnlocked(_windowW, _windowH);
            // Present may still have been partially OK for Suboptimal; treat as soft success for Suboptimal.
            if (pr == Result.ErrorOutOfDateKhr)
                return false;
        }
        else if (pr != Result.Success)
        {
            LastError = $"vkQueuePresentKHR: {pr}";
            Stats.LastError = LastError;
            return false;
        }

        _currentFrame = (frame + 1) % MaxFramesInFlight;
        LastError = null;
        return true;
    }

    private bool RecordPresentCommandsUnlocked(CommandBuffer cmd, uint imageIndex, int srcW, int srcH)
    {
        var vk = _vk!;
        Image swapImage = _swapchainImages![imageIndex];

        CommandBufferBeginInfo begin = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        if (vk.BeginCommandBuffer(cmd, in begin) != Result.Success)
        {
            LastError = "vkBeginCommandBuffer failed";
            Stats.LastError = LastError;
            return false;
        }

        // 1) Staging buffer → upload image (BGRA Soft-GS)
        TransitionImageUnlocked(cmd, _uploadImage, _uploadImageLayout, ImageLayout.TransferDstOptimal,
            0, AccessFlags.TransferWriteBit,
            PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
        _uploadImageLayout = ImageLayout.TransferDstOptimal;

        BufferImageCopy region = new()
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            ImageOffset = new Offset3D { X = 0, Y = 0, Z = 0 },
            ImageExtent = new Extent3D { Width = (uint)srcW, Height = (uint)srcH, Depth = 1 }
        };
        vk.CmdCopyBufferToImage(cmd, _stagingBuffer, _uploadImage, ImageLayout.TransferDstOptimal, 1, in region);

        TransitionImageUnlocked(cmd, _uploadImage, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal,
            AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit);
        _uploadImageLayout = ImageLayout.TransferSrcOptimal;

        // 2) Swapchain image → TransferDst
        TransitionImageUnlocked(cmd, swapImage, ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
            0, AccessFlags.TransferWriteBit,
            PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);

        bool sizeMatch = srcW == (int)_swapchainExtent.Width && srcH == (int)_swapchainExtent.Height;
        bool doBlit = !sizeMatch && UpscaleMode is UpscaleMode.Nearest or UpscaleMode.Bilinear;

        if (sizeMatch)
        {
            ImageCopy copy = new()
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                Extent = new Extent3D { Width = (uint)srcW, Height = (uint)srcH, Depth = 1 }
            };
            vk.CmdCopyImage(cmd, _uploadImage, ImageLayout.TransferSrcOptimal,
                swapImage, ImageLayout.TransferDstOptimal, 1, in copy);
        }
        else if (doBlit)
        {
            // Clear then blit-scale Soft-GS → swapchain.
            ClearSwapchainUnlocked(cmd, swapImage);
            ImageBlit blit = new()
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };
            blit.SrcOffsets[0] = new Offset3D { X = 0, Y = 0, Z = 0 };
            blit.SrcOffsets[1] = new Offset3D { X = srcW, Y = srcH, Z = 1 };
            blit.DstOffsets[0] = new Offset3D { X = 0, Y = 0, Z = 0 };
            blit.DstOffsets[1] = new Offset3D
            {
                X = (int)_swapchainExtent.Width,
                Y = (int)_swapchainExtent.Height,
                Z = 1
            };

            Filter filter = UpscaleMode == UpscaleMode.Bilinear ? Filter.Linear : Filter.Nearest;
            vk.CmdBlitImage(cmd, _uploadImage, ImageLayout.TransferSrcOptimal,
                swapImage, ImageLayout.TransferDstOptimal, 1, in blit, filter);
        }
        else
        {
            // Crop / top-left copy (UpscaleMode.None)
            ClearSwapchainUnlocked(cmd, swapImage);
            uint copyW = Math.Min((uint)srcW, _swapchainExtent.Width);
            uint copyH = Math.Min((uint)srcH, _swapchainExtent.Height);
            if (copyW > 0 && copyH > 0)
            {
                ImageCopy copy = new()
                {
                    SrcSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = 0,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    },
                    DstSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = 0,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    },
                    Extent = new Extent3D { Width = copyW, Height = copyH, Depth = 1 }
                };
                vk.CmdCopyImage(cmd, _uploadImage, ImageLayout.TransferSrcOptimal,
                    swapImage, ImageLayout.TransferDstOptimal, 1, in copy);
            }
        }

        // 3) Swapchain image → PresentSrc
        TransitionImageUnlocked(cmd, swapImage, ImageLayout.TransferDstOptimal, ImageLayout.PresentSrcKhr,
            AccessFlags.TransferWriteBit, AccessFlags.MemoryReadBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit);

        if (vk.EndCommandBuffer(cmd) != Result.Success)
        {
            LastError = "vkEndCommandBuffer failed";
            Stats.LastError = LastError;
            return false;
        }
        return true;
    }

    private void ClearSwapchainUnlocked(CommandBuffer cmd, Image swapImage)
    {
        var range = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1
        };
        ClearColorValue clear = new()
        {
            Float32_0 = 0f,
            Float32_1 = 0f,
            Float32_2 = 0f,
            Float32_3 = 1f
        };
        _vk!.CmdClearColorImage(cmd, swapImage, ImageLayout.TransferDstOptimal, &clear, 1, in range);
    }

    private void TransitionImageUnlocked(
        CommandBuffer cmd,
        Image image,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        AccessFlags srcAccess,
        AccessFlags dstAccess,
        PipelineStageFlags srcStage,
        PipelineStageFlags dstStage)
    {
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess
        };
        _vk!.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, in barrier);
    }

    // ── teardown ──────────────────────────────────────────────────────────

    private void TearDownDeviceUnlocked()
    {
        if (_vk is not null && _device.Handle != 0)
            _vk.DeviceWaitIdle(_device);

        DestroySwapchainUnlocked();
        DestroyUploadImageUnlocked();
        DestroyStagingBufferUnlocked();
        DestroySurfaceUnlocked();

        if (_vk is not null && _device.Handle != 0 && _commandPool.Handle != 0)
        {
            _vk.DestroyCommandPool(_device, _commandPool, null);
            _commandPool = default;
        }

        if (_vk is not null && _device.Handle != 0)
        {
            _vk.DestroyDevice(_device, null);
            _device = default;
        }

        if (_validationEnabled && _debugUtils is not null && _debugMessenger.Handle != 0)
        {
            _debugUtils.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
            _debugMessenger = default;
        }

        if (_vk is not null && _instance.Handle != 0)
        {
            _vk.DestroyInstance(_instance, null);
            _instance = default;
        }

        _khrSwapchain?.Dispose();
        _khrSwapchain = null;
        _khrWin32?.Dispose();
        _khrWin32 = null;
        _khrSurface?.Dispose();
        _khrSurface = null;
        _debugUtils?.Dispose();
        _debugUtils = null;
        _vk?.Dispose();
        _vk = null;
        _stagingBgra = null;
        _hwnd = 0;
        _commandBuffers = null;
    }

    private void DestroySwapchainUnlocked()
    {
        if (_vk is not null && _device.Handle != 0)
            _vk.DeviceWaitIdle(_device);

        DestroyFrameSyncUnlocked();

        if (_khrSwapchain is not null && _device.Handle != 0 && _swapchain.Handle != 0)
            _khrSwapchain.DestroySwapchain(_device, _swapchain, null);

        _swapchain = default;
        _swapchainImages = null;
    }

    private void DestroyFrameSyncUnlocked()
    {
        if (_vk is null || _device.Handle == 0)
        {
            _commandBuffers = null;
            _imageAvailable = null;
            _renderFinished = null;
            _inFlightFences = null;
            _imagesInFlight = null;
            return;
        }

        // Command buffers are freed with the pool on full teardown; on swapchain recreate
        // free them explicitly so we can re-allocate.
        if (_commandBuffers is not null && _commandPool.Handle != 0)
        {
            fixed (CommandBuffer* p = _commandBuffers)
                _vk.FreeCommandBuffers(_device, _commandPool, (uint)_commandBuffers.Length, p);
        }
        _commandBuffers = null;

        if (_imageAvailable is not null)
        {
            foreach (var s in _imageAvailable)
            {
                if (s.Handle != 0)
                    _vk.DestroySemaphore(_device, s, null);
            }
        }
        if (_renderFinished is not null)
        {
            foreach (var s in _renderFinished)
            {
                if (s.Handle != 0)
                    _vk.DestroySemaphore(_device, s, null);
            }
        }
        if (_inFlightFences is not null)
        {
            foreach (var f in _inFlightFences)
            {
                if (f.Handle != 0)
                    _vk.DestroyFence(_device, f, null);
            }
        }

        _imageAvailable = null;
        _renderFinished = null;
        _inFlightFences = null;
        _imagesInFlight = null;
        _currentFrame = 0;
    }

    private void DestroyStagingBufferUnlocked()
    {
        if (_vk is null || _device.Handle == 0)
        {
            _stagingBuffer = default;
            _stagingMemory = default;
            _stagingMapped = null;
            _stagingCapacity = 0;
            return;
        }

        if (_stagingMapped is not null && _stagingMemory.Handle != 0)
        {
            _vk.UnmapMemory(_device, _stagingMemory);
            _stagingMapped = null;
        }
        if (_stagingBuffer.Handle != 0)
        {
            _vk.DestroyBuffer(_device, _stagingBuffer, null);
            _stagingBuffer = default;
        }
        if (_stagingMemory.Handle != 0)
        {
            _vk.FreeMemory(_device, _stagingMemory, null);
            _stagingMemory = default;
        }
        _stagingCapacity = 0;
        _stagingBufW = _stagingBufH = 0;
    }

    private void DestroyUploadImageUnlocked()
    {
        if (_vk is null || _device.Handle == 0)
        {
            _uploadImage = default;
            _uploadImageMemory = default;
            _uploadImageW = _uploadImageH = 0;
            _uploadImageLayout = ImageLayout.Undefined;
            return;
        }

        if (_uploadImage.Handle != 0)
        {
            _vk.DestroyImage(_device, _uploadImage, null);
            _uploadImage = default;
        }
        if (_uploadImageMemory.Handle != 0)
        {
            _vk.FreeMemory(_device, _uploadImageMemory, null);
            _uploadImageMemory = default;
        }
        _uploadImageW = _uploadImageH = 0;
        _uploadImageLayout = ImageLayout.Undefined;
    }

    private void DestroySurfaceUnlocked()
    {
        if (_khrSurface is not null && _instance.Handle != 0 && _surface.Handle != 0)
            _khrSurface.DestroySurface(_instance, _surface, null);
        _surface = default;
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private bool CheckValidationLayerSupport()
    {
        uint layerCount = 0;
        _vk!.EnumerateInstanceLayerProperties(ref layerCount, null);
        if (layerCount == 0) return false;
        var layers = new LayerProperties[layerCount];
        fixed (LayerProperties* p = layers)
            _vk.EnumerateInstanceLayerProperties(ref layerCount, p);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in layers)
        {
            string? n = Marshal.PtrToStringAnsi((nint)layer.LayerName);
            if (n is not null) names.Add(n);
        }
        return s_validationLayers.All(names.Contains);
    }

    private bool CheckDeviceExtensionSupport(PhysicalDevice device)
    {
        uint count = 0;
        _vk!.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, null);
        var available = new ExtensionProperties[count];
        fixed (ExtensionProperties* p = available)
            _vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, p);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ext in available)
        {
            string? n = Marshal.PtrToStringAnsi((nint)ext.ExtensionName);
            if (n is not null) names.Add(n);
        }
        return s_deviceExtensions.All(names.Contains);
    }

    private bool HasGraphicsQueue(PhysicalDevice device) => FindGraphicsQueueFamily(device).HasValue;

    private uint? FindGraphicsQueueFamily(PhysicalDevice device)
    {
        uint count = 0;
        _vk!.GetPhysicalDeviceQueueFamilyProperties(device, ref count, null);
        var props = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = props)
            _vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, p);

        for (uint i = 0; i < count; i++)
        {
            if (props[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                return i;
        }
        return null;
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("VulkanSwapPresenter { DeviceReady=").Append(DeviceReady);
        sb.Append(", SurfaceReady=").Append(SurfaceReady);
        sb.Append(", SwapchainReady=").Append(SwapchainReady);
        sb.Append(", Validation=").Append(_validationEnabled);
        sb.Append(", DeviceName=").Append(DeviceName ?? "(none)");
        sb.Append(", PresentCount=").Append(Stats.PresentCount);
        sb.Append(", GpuPresentCount=").Append(GpuPresentCount);
        sb.Append(", BytesUploaded=").Append(Stats.BytesUploaded);
        sb.Append(", SwapchainRecreates=").Append(SwapchainRecreates);
        sb.Append(", Status=").Append(Status);
        if (LastError is not null)
            sb.Append(", LastError=").Append(LastError);
        sb.Append(" }");
        return sb.ToString();
    }

    private struct SwapchainSupport
    {
        public SurfaceCapabilitiesKHR Capabilities;
        public SurfaceFormatKHR[] Formats;
        public PresentModeKHR[] PresentModes;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string? lpModuleName);
}
