using Avalonia.Controls;
using DetPS2.Core;
using System;

namespace DetPS2.Desktop.Options;

/// <summary>
/// Options → Emulation: speed (cycles/tick), auto-run, JIT, run-ahead.
/// Host applies speed via <see cref="ApplySpeedToHost"/>; config fields via <see cref="ApplyTo"/>.
/// </summary>
public partial class OptionsEmulationPage : UserControl
{
    public OptionsEmulationPage()
    {
        InitializeComponent();
    }

    public void LoadFrom(EmulatorConfig cfg, string speedMode)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        SelectSpeed(speedMode);

        if (AutoRunCheck != null)
            AutoRunCheck.IsChecked = cfg.AutoRunAfterBoot;
        if (EnableJitCheck != null)
            EnableJitCheck.IsChecked = cfg.EnableJit;
        if (RunAheadBox != null)
            RunAheadBox.Value = Math.Clamp(cfg.RunAheadFrames, 0, 4);
    }

    public void ApplyTo(EmulatorConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (AutoRunCheck != null)
            cfg.AutoRunAfterBoot = AutoRunCheck.IsChecked == true;
        if (EnableJitCheck != null)
            cfg.EnableJit = EnableJitCheck.IsChecked == true;
        if (RunAheadBox?.Value is decimal ra)
            cfg.RunAheadFrames = (int)Math.Clamp((double)ra, 0, 4);
    }

    /// <summary>Current speed label: Slow | Normal | Fast | Unlimited.</summary>
    public string SelectedSpeedMode
    {
        get
        {
            if (SpeedCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
                !string.IsNullOrWhiteSpace(tag))
                return tag.Trim();
            return "Normal";
        }
    }

    /// <summary>
    /// Write cycles-per-tick + speed mode onto the options host.
    /// Fast = 25M (default Desktop bring-up). Unlimited = 50M and always skips host WaitFrame.
    /// </summary>
    public void ApplySpeedToHost(IOptionsHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        switch (SelectedSpeedMode)
        {
            case "Slow":
                host.CyclesPerTick = 300_000;
                host.CurrentSpeedMode = "Slow";
                break;
            case "Fast":
                host.CyclesPerTick = 25_000_000;
                host.CurrentSpeedMode = "Fast";
                break;
            case "Unlimited":
                host.CyclesPerTick = 50_000_000;
                host.CurrentSpeedMode = "Unlimited";
                break;
            default:
                host.CyclesPerTick = 6_000_000;
                host.CurrentSpeedMode = "Normal";
                break;
        }
    }

    private void SelectSpeed(string mode)
    {
        if (SpeedCombo == null) return;
        mode = string.IsNullOrWhiteSpace(mode) ? "Normal" : mode.Trim();
        int idx = 1; // Normal
        for (int i = 0; i < SpeedCombo.ItemCount; i++)
        {
            if (SpeedCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, mode, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }
        SpeedCombo.SelectedIndex = idx;
    }
}
