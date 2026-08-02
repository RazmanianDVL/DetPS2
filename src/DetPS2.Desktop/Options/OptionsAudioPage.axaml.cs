using Avalonia.Controls;
using DetPS2.Core;
using System;

namespace DetPS2.Desktop.Options;

/// <summary>
/// Options → Audio: host output on/off and volume. Status is informational.
/// </summary>
public partial class OptionsAudioPage : UserControl
{
    public OptionsAudioPage()
    {
        InitializeComponent();
        if (VolumeSlider != null)
            VolumeSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property.Name == nameof(Slider.Value))
                    UpdateVolumeLabel();
            };
    }

    public void LoadFrom(EmulatorConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (EnableHostAudioCheck != null)
            EnableHostAudioCheck.IsChecked = cfg.EnableHostAudio;
        if (VolumeSlider != null)
            VolumeSlider.Value = Math.Clamp(cfg.AudioVolume, 0, 100);
        UpdateVolumeLabel();
        if (StatusText != null)
            StatusText.Text = "Audio samples when SPU2 produces them";
    }

    public void ApplyTo(EmulatorConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (EnableHostAudioCheck != null)
            cfg.EnableHostAudio = EnableHostAudioCheck.IsChecked == true;
        if (VolumeSlider != null)
            cfg.AudioVolume = (int)Math.Clamp(VolumeSlider.Value, 0, 100);
    }

    private void UpdateVolumeLabel()
    {
        if (VolumeValueText == null || VolumeSlider == null) return;
        VolumeValueText.Text = ((int)Math.Clamp(VolumeSlider.Value, 0, 100)).ToString();
    }
}
