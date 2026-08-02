using System;

namespace DetPS2.Present;

/// <summary>
/// Creates <see cref="IHostSwapPresenter"/> instances.
/// Auto order: D3D12 → D3D11 → Vulkan → OpenGL → Software.
/// Soft-GS remains truth; factory only picks a display backend.
/// </summary>
public static class PresentBackendFactory
{
    public static IHostSwapPresenter Create(PresentBackend backend = PresentBackend.Auto)
    {
        return backend switch
        {
            PresentBackend.Software => new SoftwareSwapPresenter(),
            PresentBackend.Vulkan => new VulkanSwapPresenter(),
            PresentBackend.D3D11 => new D3D11SwapPresenter(),
            PresentBackend.D3D12 => new D3D12SwapPresenter(),
            PresentBackend.OpenGL => new OpenGLSwapPresenter(),
            PresentBackend.Auto => CreateAuto(),
            _ => new SoftwareSwapPresenter(),
        };
    }

    /// <summary>
    /// Auto without HWND: prefer APIs that can prove a device exists.
    /// OpenGL needs HWND for WGL, so it is only selected in <see cref="CreateAndAttach"/>.
    /// </summary>
    public static IHostSwapPresenter CreateAuto()
    {
        {
            var d3d12 = new D3D12SwapPresenter();
            if (d3d12.DeviceCreated)
                return d3d12;
            d3d12.Dispose();
        }

        {
            var d3d11 = new D3D11SwapPresenter();
            if (d3d11.DeviceCreated)
                return d3d11;
            d3d11.Dispose();
        }

        {
            var vk = new VulkanSwapPresenter();
            if (vk.DeviceReady)
                return vk;
            vk.Dispose();
        }

        // OpenGL requires AttachWindow — not creatable here.
        return new SoftwareSwapPresenter();
    }

    /// <summary>
    /// Create and attach to HWND. Auto: D3D12 → D3D11 → Vulkan → OpenGL → Software.
    /// </summary>
    public static IHostSwapPresenter CreateAndAttach(
        nint hwnd,
        int width,
        int height,
        PresentBackend backend = PresentBackend.Auto)
    {
        if (backend != PresentBackend.Auto)
        {
            var p = Create(backend);
            p.AttachWindow(hwnd, width, height);
            return p;
        }

        {
            var d3d12 = new D3D12SwapPresenter();
            if (d3d12.AttachWindow(hwnd, width, height) && d3d12.DeviceReady)
                return d3d12;
            d3d12.Dispose();
        }

        {
            var d3d11 = new D3D11SwapPresenter();
            if (d3d11.AttachWindow(hwnd, width, height) && d3d11.DeviceReady)
                return d3d11;
            d3d11.Dispose();
        }

        {
            var vk = new VulkanSwapPresenter();
            if (vk.AttachWindow(hwnd, width, height) && vk.DeviceReady)
                return vk;
            vk.Dispose();
        }

        {
            var gl = new OpenGLSwapPresenter();
            if (gl.AttachWindow(hwnd, width, height) && gl.DeviceReady)
                return gl;
            gl.Dispose();
        }

        var sw = new SoftwareSwapPresenter();
        sw.AttachWindow(hwnd, width, height);
        return sw;
    }
}
