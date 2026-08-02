using Avalonia.Controls;
using Avalonia.Media.Imaging;
using System;
using System.IO;

namespace DetPS2.Desktop.Views;

/// <summary>
/// Simple library tile: title + path + optional cover art (placeholder when no path).
/// </summary>
public partial class GameLibraryTile : UserControl
{
    public GameLibraryTile()
    {
        InitializeComponent();
    }

    public void Bind(string displayName, string path, string? coverArtPath = null)
    {
        if (TitleText != null) TitleText.Text = displayName ?? "";
        if (PathText != null) PathText.Text = path ?? "";
        SetCoverArt(coverArtPath);
    }

    public void SetCoverArt(string? coverArtPath)
    {
        if (ArtImage == null || PlaceholderGlyph == null) return;
        if (!string.IsNullOrWhiteSpace(coverArtPath) && File.Exists(coverArtPath))
        {
            try
            {
                ArtImage.Source = new Bitmap(coverArtPath);
                ArtImage.IsVisible = true;
                PlaceholderGlyph.IsVisible = false;
                return;
            }
            catch
            {
                // fall through to placeholder
            }
        }
        ArtImage.Source = null;
        ArtImage.IsVisible = false;
        PlaceholderGlyph.IsVisible = true;
    }
}
