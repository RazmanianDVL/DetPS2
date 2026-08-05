using Avalonia.Controls;
using DetPS2.Core;
using System;

namespace DetPS2.Desktop.Options;

/// <summary>
/// General options: library path display, auto-run, media verify.
/// BIOS dump selection lives in the host-built "extras" panel (OptionsWindow.BuildGeneralHost),
/// not on this page directly — native HLE remains the default; a dump is opt-in only.
/// Host (UI-1 shell) should call <see cref="LoadFrom"/> / <see cref="ApplyTo"/> around show/save.
/// </summary>
public partial class OptionsGeneralPage : UserControl
{
    public OptionsGeneralPage()
    {
        InitializeComponent();
    }

    public void LoadFrom(EmulatorConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (GamesFolderText != null)
            GamesFolderText.Text = string.IsNullOrWhiteSpace(cfg.GamesFolder)
                ? "(not set — use Set library path on the main window)"
                : cfg.GamesFolder;
        if (AutoRunCheck != null)
            AutoRunCheck.IsChecked = cfg.AutoRunAfterBoot;
        if (VerifyMediaCheck != null)
            VerifyMediaCheck.IsChecked = cfg.VerifyMediaOnBoot;
    }

    public void ApplyTo(EmulatorConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (AutoRunCheck != null)
            cfg.AutoRunAfterBoot = AutoRunCheck.IsChecked == true;
        if (VerifyMediaCheck != null)
            cfg.VerifyMediaOnBoot = VerifyMediaCheck.IsChecked == true;
    }
}
