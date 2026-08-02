using Avalonia.Media.Imaging;
using DetPS2.Core;
using System;
using System.IO;

namespace DetPS2.Desktop;

/// <summary>Library tile view-model: game settings + optional box-art bitmap.</summary>
public sealed class LibraryItemVm
{
    public LibraryItemVm(GameSettings game)
    {
        Game = game ?? throw new ArgumentNullException(nameof(game));
        ReloadCover();
    }

    public GameSettings Game { get; }
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Game.TitleOverride) ? Game.TitleOverride! : Game.DisplayName;
    public string Path => Game.Path;
    public string Subtitle =>
        !string.IsNullOrWhiteSpace(Game.Serial) ? Game.Serial! : Game.Path;
    public Bitmap? Cover { get; private set; }
    public bool HasCover => Cover != null;
    public bool ShowPlaceholder => Cover == null;

    public void ReloadCover()
    {
        Cover = null;
        string? path = Game.BoxArtPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        try
        {
            // Load a copy so file can be overwritten by scraper later.
            using var fs = File.OpenRead(path);
            Cover = new Bitmap(fs);
        }
        catch
        {
            Cover = null;
        }
    }
}
