using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DetPS2.Core;
using DetPS2.Core.Metadata;
using System;
using System.IO;

namespace DetPS2.Desktop.Options;

/// <summary>
/// Metadata / box-art scrape options. Host wires
/// <see cref="LibraryMetadataService"/> separately for library enqueue.
/// </summary>
public partial class OptionsMetadataPage : UserControl
{
    public OptionsMetadataPage()
    {
        InitializeComponent();
        if (CacheDirBox != null)
            CacheDirBox.TextChanged += (_, __) => UpdateResolvedLabel();
    }

    public void LoadFrom(EmulatorConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (ScrapeBoxArtCheck != null)
            ScrapeBoxArtCheck.IsChecked = cfg.ScrapeBoxArt;

        if (ProviderCombo != null)
        {
            string p = string.IsNullOrWhiteSpace(cfg.ScraperProvider)
                ? NullBoxArtScraper.ProviderId
                : cfg.ScraperProvider;
            int idx = 0;
            for (int i = 0; i < ProviderCombo.ItemCount; i++)
            {
                if (ProviderCombo.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Tag as string, p, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }
            ProviderCombo.SelectedIndex = idx;
        }

        if (CacheDirBox != null)
            CacheDirBox.Text = cfg.MetadataCacheDir ?? "";

        UpdateResolvedLabel();
        if (StatusText != null)
        {
            string root = ResolveCacheRoot(cfg.MetadataCacheDir);
            int count = 0;
            try
            {
                if (Directory.Exists(root))
                    count = Directory.GetDirectories(root).Length;
            }
            catch { /* ignore */ }
            StatusText.Text = $"Cache root: {root}\nCached serial folders: {count}";
        }
    }

    public void ApplyTo(EmulatorConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (ScrapeBoxArtCheck != null)
            cfg.ScrapeBoxArt = ScrapeBoxArtCheck.IsChecked == true;

        if (ProviderCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            cfg.ScraperProvider = tag;
        else
            cfg.ScraperProvider = NullBoxArtScraper.ProviderId;

        if (CacheDirBox != null)
            cfg.MetadataCacheDir = (CacheDirBox.Text ?? "").Trim();
    }

    private void OnUseDefaultCacheClick(object? sender, RoutedEventArgs e)
    {
        if (CacheDirBox != null)
            CacheDirBox.Text = "";
        UpdateResolvedLabel();
    }

    private async void OnBrowseCacheClick(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Metadata cache folder",
            AllowMultiple = false
        });
        if (folders == null || folders.Count == 0) return;
        string path = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
        if (CacheDirBox != null)
            CacheDirBox.Text = path;
        UpdateResolvedLabel();
    }

    private void UpdateResolvedLabel()
    {
        if (ResolvedCacheText == null) return;
        string custom = CacheDirBox?.Text?.Trim() ?? "";
        ResolvedCacheText.Text = "Resolved: " + ResolveCacheRoot(custom);
    }

    private static string ResolveCacheRoot(string? custom) =>
        string.IsNullOrWhiteSpace(custom) ? LocalBoxArtCache.DefaultRoot() : custom.Trim();
}
