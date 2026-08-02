using Avalonia.Controls;
using Avalonia.Interactivity;
using DetPS2.Core;
using DetPS2.Present;
using System;
using System.Text;

namespace DetPS2.Desktop.Options;

/// <summary>
/// Options → Graphics: choose host rendering backend (D3D11/12, Vulkan, OpenGL, Software, Auto).
/// Soft-GS remains emulation truth; this only picks the display path.
/// </summary>
public partial class OptionsGraphicsPage : UserControl
{
    /// <summary>Raised when user clicks Apply renderer now (host should ApplyTo + Persist + ApplyPresentMode).</summary>
    public event EventHandler? ApplyRendererRequested;

    public double SessionUpscale { get; private set; } = 1.0;

    public OptionsGraphicsPage()
    {
        InitializeComponent();
        OpenedOrLoadedRefresh();
    }

    private void OpenedOrLoadedRefresh()
    {
        // Status probe is cheap enough to run when the control is constructed / loaded.
        AttachedToVisualTree += (_, __) => RefreshBackendStatus();
    }

    public void LoadFrom(EmulatorConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        SelectPresentMode(string.IsNullOrWhiteSpace(cfg.PresentMode) ? "Software" : cfg.PresentMode);

        if (FrameLimitCheck != null)
            FrameLimitCheck.IsChecked = cfg.DefaultFrameLimit;
        if (TargetFpsBox != null)
            TargetFpsBox.Value = cfg.DefaultTargetFps is > 0 and <= 240 ? cfg.DefaultTargetFps : 60;
        if (UpscaleBox != null)
            UpscaleBox.Value = 1.0m;
        SessionUpscale = 1.0;

        UpdateSelectionHint();
        RefreshBackendStatus();
    }

    public void ApplyTo(EmulatorConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        cfg.PresentMode = GetSelectedPresentMode();

        if (FrameLimitCheck != null)
            cfg.DefaultFrameLimit = FrameLimitCheck.IsChecked == true;
        if (TargetFpsBox?.Value is decimal fps)
            cfg.DefaultTargetFps = (int)Math.Clamp((double)fps, 15, 240);

        if (UpscaleBox?.Value is decimal up)
            SessionUpscale = Math.Clamp((double)up, 1.0, 4.0);
    }

    /// <summary>Current combo selection tag (Auto / D3D12 / …).</summary>
    public string GetSelectedPresentMode()
    {
        if (PresentModeCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            !string.IsNullOrWhiteSpace(tag))
            return tag.Trim();
        return "Software";
    }

    private void SelectPresentMode(string mode)
    {
        if (PresentModeCombo == null) return;
        mode = string.IsNullOrWhiteSpace(mode) ? "Software" : mode.Trim();
        // Map legacy labels
        if (string.Equals(mode, "GPU", StringComparison.OrdinalIgnoreCase))
            mode = "D3D11";

        int idx = 0;
        for (int i = 0; i < PresentModeCombo.ItemCount; i++)
        {
            if (PresentModeCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, mode, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }
        PresentModeCombo.SelectedIndex = idx;
    }

    private void OnPresentModeSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdateSelectionHint();

    private void OnApplyRendererClick(object? sender, RoutedEventArgs e) =>
        ApplyRendererRequested?.Invoke(this, EventArgs.Empty);

    private void OnRefreshStatusClick(object? sender, RoutedEventArgs e) =>
        RefreshBackendStatus();

    private void UpdateSelectionHint()
    {
        if (SelectionHintText == null) return;
        string mode = GetSelectedPresentMode();
        SelectionHintText.Text = mode switch
        {
            "Auto" => "Auto tries GPU present, but Soft-GS stays on Avalonia until GPU PresentCount advances with pixels.",
            "Software" => "Recommended: Avalonia WriteableBitmap always shows Soft-GS pixels (reliable).",
            "D3D11" => "D3D11 swapchain when proven; falls back to Avalonia if Present fails or Soft-GS has no pixels yet.",
            "D3D12" => "D3D12 swapchain when proven; falls back to Avalonia if Present fails or Soft-GS has no pixels yet.",
            "Vulkan" => "Vulkan when proven; falls back to Avalonia if Present fails or Soft-GS has no pixels yet.",
            "OpenGL" => "OpenGL (WGL) when proven; falls back to Avalonia if Present fails or Soft-GS has no pixels yet.",
            _ => $"Selected: {mode}"
        };
    }

    public void RefreshBackendStatus()
    {
        if (BackendStatusText == null) return;
        try
        {
            BackendStatusText.Text = ProbeBackends();
        }
        catch (Exception ex)
        {
            BackendStatusText.Text = "Probe failed: " + ex.Message;
        }
    }

    /// <summary>Quick availability probe (no HWND for OpenGL full path).</summary>
    public static string ProbeBackends()
    {
        var sb = new StringBuilder();

        try
        {
            using var d3d12 = new D3D12SwapPresenter();
            sb.AppendLine(d3d12.DeviceCreated
                ? $"D3D12   available  (FL={d3d12.FeatureLevel})"
                : $"D3D12   unavailable  {TrimErr(d3d12.Stats.LastError)}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("D3D12   error: " + ex.Message);
        }

        try
        {
            using var d3d11 = new D3D11SwapPresenter();
            sb.AppendLine(d3d11.DeviceCreated
                ? $"D3D11   available  (FL={d3d11.FeatureLevel})"
                : $"D3D11   unavailable  {TrimErr(d3d11.Stats.LastError)}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("D3D11   error: " + ex.Message);
        }

        try
        {
            using var vk = new VulkanSwapPresenter();
            sb.AppendLine(vk.DeviceReady
                ? $"Vulkan  available  ({vk.DeviceName ?? "device"})"
                : $"Vulkan  unavailable  {TrimErr(vk.LastError ?? vk.Stats.LastError)}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("Vulkan  error: " + ex.Message);
        }

        sb.AppendLine("OpenGL  available after game window attach (WGL)");
        sb.Append("Software always available (Avalonia)");
        return sb.ToString();
    }

    private static string TrimErr(string? e) =>
        string.IsNullOrWhiteSpace(e) ? "" : "(" + (e.Length > 60 ? e[..60] + "…" : e) + ")";
}
