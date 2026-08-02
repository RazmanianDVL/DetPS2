using System.Runtime.InteropServices;
using DetPS2.Present;

/// <summary>
/// Host present harness: probes D3D11 + Vulkan device readiness and optionally
/// runs a full Vulkan Acquire→upload→Present path against a temporary HWND.
/// Usage: dotnet run --project src/DetPS2.Present -c Release
///        set DETPS2_VK_VALIDATE=1 for Vulkan validation layers when installed.
/// </summary>
static class PresentDeviceHarness
{
    static int Main(string[] args)
    {
        Console.WriteLine("DetPS2.Present device harness");
        Console.WriteLine($"  DETPS2_VK_VALIDATE={Environment.GetEnvironmentVariable(VulkanSwapPresenter.ValidateEnvVar) ?? "(unset)"}");
        Console.WriteLine();

        // --- Vulkan ---
        Console.WriteLine("[Vulkan]");
        using (var vk = new VulkanSwapPresenter())
        {
            Console.WriteLine("  " + vk.Describe());
            if (vk.DeviceReady)
            {
                var pixels = new uint[16 * 16];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = 0xFF00_80FFu; // AARRGGBB magenta-ish
                vk.Present(pixels, 16, 16);
                Console.WriteLine($"  After Present (device-only): count={vk.PresentCount} gpu={vk.GpuPresentCount} bytes={vk.BytesUploaded} status={vk.Status}");

                // Full path: temporary Win32 window → surface/swapchain → GPU present.
                if (TryCreateTempWindow(320, 240, out nint hwnd))
                {
                    try
                    {
                        bool attached = vk.AttachWindow(hwnd, 320, 240);
                        Console.WriteLine($"  AttachWindow hwnd=0x{hwnd:X} → {attached} SwapchainReady={vk.SwapchainReady}");
                        Console.WriteLine($"  " + vk.Describe());

                        if (attached && vk.SwapchainReady)
                        {
                            for (int f = 0; f < 3; f++)
                            {
                                for (int i = 0; i < pixels.Length; i++)
                                    pixels[i] = (uint)(0xFF000000u | (uint)((f * 40 + i) & 0xFF) << 8);
                                vk.Present(pixels, 16, 16);
                            }
                            // Resize / OUT_OF_DATE path
                            vk.Resize(400, 300);
                            vk.Present(pixels, 16, 16);

                            Console.WriteLine($"  After GPU presents: PresentCount={vk.PresentCount} GpuPresentCount={vk.GpuPresentCount} recreates={vk.SwapchainRecreates}");
                            Console.WriteLine($"  status={vk.Status}");
                            if (vk.LastError is not null)
                                Console.WriteLine($"  LastError={vk.LastError}");
                        }
                        else
                        {
                            Console.WriteLine($"  Swapchain not ready: {vk.LastError ?? vk.Status}");
                        }
                    }
                    finally
                    {
                        DestroyWindow(hwnd);
                    }
                }
                else
                {
                    Console.WriteLine("  (temp HWND create failed; device-only path only)");
                }
            }
            else
            {
                vk.Present(stackalloc uint[4], 2, 2);
                Console.WriteLine("  Present no-op (device not ready) OK");
            }

            Console.WriteLine($"RESULT Vulkan DeviceReady={vk.DeviceReady} PresentCount={vk.PresentCount} GpuPresentCount={vk.GpuPresentCount}");
            if (!vk.DeviceReady && vk.LastError is not null)
                Console.WriteLine($"RESULT Vulkan LastError={vk.LastError}");
        }

        Console.WriteLine();

        // --- D3D12 (preferred Auto; device without HWND is partial) ---
        Console.WriteLine("[D3D12]");
        {
            bool deviceCreated;
            bool deviceReadyAfterAttach = false;
            ulong presentCount = 0;
            string lastError = "";

            using (var d3d12 = new D3D12SwapPresenter())
            {
                deviceCreated = d3d12.DeviceCreated;
                Console.WriteLine($"  FeatureLevel={d3d12.FeatureLevel} DeviceCreated={deviceCreated} DeviceReady={d3d12.DeviceReady} (swap needs AttachWindow)");
                Console.WriteLine($"  Stats.LastError={d3d12.Stats.LastError}");
                // Present without swap is a no-op (must not throw).
                d3d12.Present(stackalloc uint[4], 2, 2);

                if (deviceCreated && TryCreateTempWindow(320, 240, out nint hwnd12))
                {
                    try
                    {
                        bool attached = d3d12.AttachWindow(hwnd12, 320, 240);
                        deviceReadyAfterAttach = d3d12.DeviceReady;
                        Console.WriteLine($"  AttachWindow hwnd=0x{hwnd12:X} → {attached} DeviceReady={deviceReadyAfterAttach}");
                        if (attached && deviceReadyAfterAttach)
                        {
                            var pixels = new uint[16 * 16];
                            for (int f = 0; f < 3; f++)
                            {
                                for (int i = 0; i < pixels.Length; i++)
                                    pixels[i] = (uint)(0xFF000000u | (uint)((f * 80 + i) & 0xFF) << 16);
                                d3d12.Present(pixels, 16, 16);
                            }
                            d3d12.Resize(400, 300);
                            d3d12.Present(pixels, 16, 16);
                            presentCount = d3d12.Stats.PresentCount;
                            Console.WriteLine($"  After Present: count={presentCount} bytes={d3d12.Stats.BytesUploaded} err={d3d12.Stats.LastError}");
                        }
                        else
                        {
                            Console.WriteLine($"  Attach failed: {d3d12.Stats.LastError}");
                        }
                        lastError = d3d12.Stats.LastError;
                    }
                    finally
                    {
                        DestroyWindow(hwnd12);
                    }
                }
            }

            // Separate HWND: Auto attach must prefer D3D12 when DeviceReady after attach.
            if (deviceCreated && TryCreateTempWindow(320, 240, out nint hwndAuto))
            {
                try
                {
                    using var autoAttached = PresentBackendFactory.CreateAndAttach(hwndAuto, 320, 240, PresentBackend.Auto);
                    Console.WriteLine($"  CreateAndAttach Auto → {autoAttached.Name} DeviceReady={autoAttached.DeviceReady}");
                    Console.WriteLine($"RESULT AutoAttach Backend={autoAttached.Backend} DeviceReady={autoAttached.DeviceReady}");
                }
                finally
                {
                    DestroyWindow(hwndAuto);
                }
            }

            Console.WriteLine($"RESULT D3D12 deviceCreated={deviceCreated} DeviceReady={deviceReadyAfterAttach} PresentCount={presentCount}");
            if (!string.IsNullOrEmpty(lastError))
                Console.WriteLine($"RESULT D3D12 LastError={lastError}");
        }

        Console.WriteLine();

        // --- D3D11 (GPU-A) ---
        Console.WriteLine("[D3D11]");
        {
            bool deviceCreated;
            bool deviceReadyAfterAttach = false;
            ulong presentCount = 0;
            string lastError = "";

            using var d3d = new D3D11SwapPresenter();
            deviceCreated = d3d.DeviceCreated;
            Console.WriteLine($"  FeatureLevel={d3d.FeatureLevel} DeviceCreated={deviceCreated} DeviceReady={d3d.DeviceReady} (swap needs AttachWindow)");
            Console.WriteLine($"  Stats.LastError={d3d.Stats.LastError}");
            // Present without swap is a no-op (must not throw).
            d3d.Present(stackalloc uint[4], 2, 2);

            if (deviceCreated && TryCreateTempWindow(320, 240, out nint hwnd11))
            {
                try
                {
                    bool attached = d3d.AttachWindow(hwnd11, 320, 240);
                    deviceReadyAfterAttach = d3d.DeviceReady;
                    Console.WriteLine($"  AttachWindow hwnd=0x{hwnd11:X} → {attached} DeviceReady={deviceReadyAfterAttach}");
                    if (attached && deviceReadyAfterAttach)
                    {
                        var pixels = new uint[16 * 16];
                        for (int f = 0; f < 3; f++)
                        {
                            for (int i = 0; i < pixels.Length; i++)
                                pixels[i] = (uint)(0xFF000000u | (uint)((f * 80 + i) & 0xFF) << 8);
                            d3d.Present(pixels, 16, 16);
                        }
                        d3d.Resize(400, 300);
                        d3d.Present(pixels, 16, 16);
                        presentCount = d3d.Stats.PresentCount;
                        Console.WriteLine($"  After Present: count={presentCount} bytes={d3d.Stats.BytesUploaded} err={d3d.Stats.LastError}");
                    }
                    else
                    {
                        Console.WriteLine($"  Attach failed: {d3d.Stats.LastError}");
                    }
                    lastError = d3d.Stats.LastError;
                }
                finally
                {
                    DestroyWindow(hwnd11);
                }
            }

            Console.WriteLine($"RESULT D3D11 deviceCreated={deviceCreated} DeviceReady={deviceReadyAfterAttach} PresentCount={presentCount}");
            if (!string.IsNullOrEmpty(lastError))
                Console.WriteLine($"RESULT D3D11 LastError={lastError}");
        }

        Console.WriteLine();

        // --- OpenGL (WGL) ---
        Console.WriteLine("[OpenGL]");
        if (TryCreateTempWindow(320, 240, out nint hwndGl))
        {
            try
            {
                using var gl = new OpenGLSwapPresenter();
                bool attached = gl.AttachWindow(hwndGl, 320, 240);
                Console.WriteLine($"  AttachWindow → {attached} DeviceReady={gl.DeviceReady}");
                Console.WriteLine($"  Renderer={gl.Renderer ?? "?"} Version={gl.Version ?? "?"}");
                Console.WriteLine($"  Stats.LastError={gl.Stats.LastError}");
                if (attached && gl.DeviceReady)
                {
                    var pixels = new uint[16 * 16];
                    for (int f = 0; f < 3; f++)
                    {
                        for (int i = 0; i < pixels.Length; i++)
                            pixels[i] = (uint)(0xFF000000u | (uint)((f * 50 + i) & 0xFF));
                        gl.Present(pixels, 16, 16);
                    }
                    gl.Resize(400, 300);
                    gl.Present(pixels, 16, 16);
                    Console.WriteLine($"  After Present: count={gl.Stats.PresentCount} bytes={gl.Stats.BytesUploaded}");
                }
                Console.WriteLine($"RESULT OpenGL DeviceReady={gl.DeviceReady} PresentCount={gl.Stats.PresentCount}");
            }
            finally
            {
                DestroyWindow(hwndGl);
            }
        }
        else
        {
            Console.WriteLine("  (temp HWND create failed)");
            Console.WriteLine("RESULT OpenGL DeviceReady=False");
        }

        Console.WriteLine();

        // --- Factory Auto (no HWND) — prefers D3D12 when DeviceCreated ---
        using (var auto = PresentBackendFactory.Create(PresentBackend.Auto))
        {
            Console.WriteLine($"[Auto] selected={auto.Name} DeviceReady={auto.DeviceReady}");
            Console.WriteLine($"RESULT Auto Backend={auto.Backend}");
        }

        return 0;
    }

    private static bool TryCreateTempWindow(int w, int h, out nint hwnd)
    {
        hwnd = 0;
        try
        {
            string className = "DetPS2VkHarness" + Environment.ProcessId;
            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = className,
            };
            ushort atom = RegisterClassW(ref wc);
            if (atom == 0 && Marshal.GetLastWin32Error() != 1410) // already exists
                return false;

            hwnd = CreateWindowExW(
                0,
                className,
                "DetPS2 Vulkan Harness",
                WS_POPUP | WS_VISIBLE,
                0, 0, w, h,
                0, 0, wc.hInstance, 0);

            if (hwnd == 0)
                return false;

            // Pump a few messages so the window is realized for DXGI/Vulkan surface.
            for (int i = 0; i < 8; i++)
            {
                if (PeekMessageW(out MSG msg, 0, 0, 0, 1))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            }
            return true;
        }
        catch
        {
            hwnd = 0;
            return false;
        }
    }

    private static readonly WndProc s_wndProc = static (hWnd, msg, wParam, lParam) =>
        DefWindowProcW(hWnd, msg, wParam, lParam);

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;

    private delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int X, int Y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref MSG lpMsg);
}
