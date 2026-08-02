using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DetPS2.Core;
using System;

namespace DetPS2.Desktop.Options;

/// <summary>
/// Options → Advanced: log folder, virtual HDD, verify media.
/// </summary>
public partial class OptionsAdvancedPage : UserControl
{
    private string _hddPath = "";

    public OptionsAdvancedPage()
    {
        InitializeComponent();
    }

    /// <summary>Host should open the session log directory (e.g. <c>IOptionsHost.OpenLogFolder</c>).</summary>
    public event Action? OpenLogFolderRequested;

    public void LoadFrom(EmulatorConfig cfg, string sessionLogPath)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (SessionLogPathText != null)
            SessionLogPathText.Text = string.IsNullOrWhiteSpace(sessionLogPath)
                ? "(no session log path)"
                : sessionLogPath;

        if (EnableHddCheck != null)
            EnableHddCheck.IsChecked = cfg.EnableVirtualHdd;
        _hddPath = cfg.VirtualHddPath ?? "";
        UpdateHddPathLabel();
        if (HddSizeBox != null)
            HddSizeBox.Value = cfg.VirtualHddSizeMb is > 0 and <= 131072 ? cfg.VirtualHddSizeMb : 8192;
        if (VerifyMediaCheck != null)
            VerifyMediaCheck.IsChecked = cfg.VerifyMediaOnBoot;
    }

    public void ApplyTo(EmulatorConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (EnableHddCheck != null)
            cfg.EnableVirtualHdd = EnableHddCheck.IsChecked == true;
        cfg.VirtualHddPath = _hddPath ?? "";
        if (HddSizeBox?.Value is decimal mb)
            cfg.VirtualHddSizeMb = (int)Math.Clamp((double)mb, 128, 131072);
        if (VerifyMediaCheck != null)
            cfg.VerifyMediaOnBoot = VerifyMediaCheck.IsChecked == true;
    }

    private void OnOpenLogFolderClick(object? sender, RoutedEventArgs e) =>
        OpenLogFolderRequested?.Invoke();

    private async void OnPickHddClick(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select or create virtual HDD image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("HDD image") { Patterns = new[] { "*.raw", "*.img", "*.hdd", "*.bin" } },
                new FilePickerFileType("All") { Patterns = new[] { "*.*" } }
            }
        });
        if (files == null || files.Count == 0) return;
        _hddPath = files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
        UpdateHddPathLabel();
    }

    private void OnClearHddClick(object? sender, RoutedEventArgs e)
    {
        _hddPath = "";
        UpdateHddPathLabel();
    }

    private void UpdateHddPathLabel()
    {
        if (HddPathText == null) return;
        HddPathText.Text = string.IsNullOrWhiteSpace(_hddPath)
            ? "No virtual HDD file selected"
            : _hddPath;
    }
}
