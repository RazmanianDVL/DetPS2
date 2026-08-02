using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DetPS2.Core;
using DetPS2.Desktop.Options;
using System;
using System.Threading.Tasks;

namespace DetPS2.Desktop;

/// <summary>
/// Host callbacks so Options can change config without owning the emulator session.
/// Implemented by MainWindow; category pages live under Options/ (UI-2 / PAD).
/// </summary>
public interface IOptionsHost
{
    EmulatorConfig Config { get; }
    FrameLimiter FrameLimit { get; }
    HostGamepadService Gamepads { get; }
    string CurrentSpeedMode { get; set; }
    ulong CyclesPerTick { get; set; }
    string SessionLogPath { get; }
    string SessionLogDir { get; }
    string PresentModeLabel { get; set; }

    void PersistConfig();
    void Log(string message);
    void OnLibraryPathsChanged();
    void LoadBiosPath(string path);
    Task<string?> PromptLibraryPathAsync(string initial);
    void ApplyFolderScan(string path);
    void ApplyPresentModeFromConfig();
    void ApplyFrameLimitFromConfig();
    void OpenLogFolder();
}

/// <summary>
/// Options shell: left category list + ContentControl host.
/// Wires Options/* pages for all main categories.
/// </summary>
public partial class OptionsWindow : Window
{
    private readonly IOptionsHost _host;
    private string _activeCategory = "General";

    private OptionsGeneralPage? _generalPage;
    private OptionsGraphicsPage? _graphicsPage;
    private OptionsControllersPage? _controllersPage;
    private OptionsMetadataPage? _metadataPage;
    private OptionsEmulationPage? _emulationPage;
    private OptionsAudioPage? _audioPage;
    private OptionsAdvancedPage? _advancedPage;

    // General extras: library path (not yet on OptionsGeneralPage itself)
    private TextBlock? _libraryExtraLabel;

    public OptionsWindow()
    {
        // Designer / Avalonia loader entry; not used at runtime without host.
        _host = null!;
        InitializeComponent();
    }

    public OptionsWindow(IOptionsHost host, string initialCategory = "General")
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        InitializeComponent();
        Opened += (_, __) => SelectCategory(initialCategory);
    }

    public void SelectCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
            category = "General";
        _activeCategory = category;

        if (CategoryList != null)
        {
            for (int i = 0; i < CategoryList.Items.Count; i++)
            {
                if (CategoryList.Items[i] is ListBoxItem item &&
                    string.Equals(item.Tag as string, category, StringComparison.OrdinalIgnoreCase))
                {
                    CategoryList.SelectedIndex = i;
                    break;
                }
            }
        }

        ShowCategory(category);
    }

    private void OnCategorySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_host == null) return;
        if (CategoryList?.SelectedItem is ListBoxItem item && item.Tag is string tag)
        {
            // Flush previous page into config before switching
            ApplyActivePage(persist: false);
            _activeCategory = tag;
            ShowCategory(tag);
        }
    }

    private void ShowCategory(string category)
    {
        if (ContentHost == null || _host == null) return;

        ContentHost.Content = category switch
        {
            "General" => BuildGeneralHost(),
            "Graphics" => BuildGraphicsPage(),
            "Controllers" => BuildControllersPage(),
            "Metadata" => BuildMetadataPage(),
            "Emulation" => BuildEmulationPage(),
            "Audio" => BuildAudioPage(),
            "Advanced" => BuildAdvancedPage(),
            _ => BuildPlaceholder(category)
        };
    }

    private Control BuildPlaceholder(string name)
    {
        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = name,
                    FontSize = 20,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White
                },
                new TextBlock
                {
                    Text = "No options page for this category yet.",
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0xAA)),
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
    }

    private Control BuildGeneralHost()
    {
        _generalPage = new OptionsGeneralPage();
        _generalPage.LoadFrom(_host.Config);
        // BIOS dump picker removed: Desktop boots via LoadBiosNative (commercial HLE).

        // Library path controls (toolbar removed from main — live here)
        _libraryExtraLabel = new TextBlock
        {
            Text = _host.Config.HasGamesFolder ? _host.Config.GamesFolder : "No library path set",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC))
        };
        var setLib = new Button
        {
            Content = "Set library path…",
            Padding = new Thickness(12, 6),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        setLib.Click += async (_, __) =>
        {
            string? path = await _host.PromptLibraryPathAsync(_host.Config.GamesFolder);
            if (string.IsNullOrWhiteSpace(path)) return;
            _host.ApplyFolderScan(path);
            if (_libraryExtraLabel != null)
                _libraryExtraLabel.Text = _host.Config.HasGamesFolder ? _host.Config.GamesFolder : path;
            _generalPage?.LoadFrom(_host.Config);
        };
        var rescan = new Button
        {
            Content = "Rescan library",
            Padding = new Thickness(12, 6),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        rescan.Click += (_, __) =>
        {
            if (!_host.Config.HasGamesFolder)
            {
                _host.Log("No media folder set — choose one first");
                return;
            }
            _host.ApplyFolderScan(_host.Config.GamesFolder);
            _generalPage?.LoadFrom(_host.Config);
        };

        var extras = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(16, 0, 16, 16),
            Children =
            {
                new Separator(),
                new TextBlock { Text = "Library path", FontWeight = FontWeight.SemiBold },
                _libraryExtraLabel,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { setLib, rescan }
                },
                new TextBlock
                {
                    Text = "Emulation speed → Options → Emulation. Virtual HDD → Options → Advanced.",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x88)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0)
                }
            }
        };

        return new StackPanel
        {
            Children =
            {
                _generalPage,
                extras
            }
        };
    }

    private Control BuildGraphicsPage()
    {
        _graphicsPage = new OptionsGraphicsPage();
        _graphicsPage.LoadFrom(_host.Config);
        _graphicsPage.ApplyRendererRequested += (_, __) =>
        {
            // Apply only graphics fields + present mode immediately (user clicked Apply renderer).
            _graphicsPage.ApplyTo(_host.Config);
            _host.PresentModeLabel = _host.Config.PresentMode;
            _host.ApplyPresentModeFromConfig();
            _host.ApplyFrameLimitFromConfig();
            _host.PersistConfig();
            _host.Log($"Renderer set to {_host.Config.PresentMode} (applied now)");
            _graphicsPage.RefreshBackendStatus();
        };
        return _graphicsPage;
    }

    private Control BuildControllersPage()
    {
        _controllersPage = new OptionsControllersPage(_host.Config, _host.Gamepads);
        return _controllersPage;
    }

    private Control BuildMetadataPage()
    {
        _metadataPage = new OptionsMetadataPage();
        _metadataPage.LoadFrom(_host.Config);
        return _metadataPage;
    }

    private Control BuildEmulationPage()
    {
        _emulationPage = new OptionsEmulationPage();
        _emulationPage.LoadFrom(_host.Config, _host.CurrentSpeedMode);
        return _emulationPage;
    }

    private Control BuildAudioPage()
    {
        _audioPage = new OptionsAudioPage();
        _audioPage.LoadFrom(_host.Config);
        return _audioPage;
    }

    private Control BuildAdvancedPage()
    {
        _advancedPage = new OptionsAdvancedPage();
        _advancedPage.LoadFrom(_host.Config, _host.SessionLogPath);
        _advancedPage.OpenLogFolderRequested += () => _host.OpenLogFolder();
        return _advancedPage;
    }

    private void ApplyActivePage(bool persist)
    {
        if (_host == null) return;

        try
        {
            _generalPage?.ApplyTo(_host.Config);
            _graphicsPage?.ApplyTo(_host.Config);
            _controllersPage?.ApplyToConfig(_host.Config);
            _metadataPage?.ApplyTo(_host.Config);
            _emulationPage?.ApplyTo(_host.Config);
            _audioPage?.ApplyTo(_host.Config);
            _advancedPage?.ApplyTo(_host.Config);

            // Speed lives on Emulation page (cycles-per-tick via host).
            _emulationPage?.ApplySpeedToHost(_host);

            // Runtime mirrors from config
            _host.ApplyFrameLimitFromConfig();
            _host.ApplyPresentModeFromConfig();

            if (persist)
            {
                _host.PersistConfig();
                _host.Log($"Options applied (category={_activeCategory}, speed={_host.CurrentSpeedMode})");
            }
        }
        catch (Exception ex)
        {
            _host.Log("Options apply warning: " + ex.Message);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        ApplyActivePage(persist: true);
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_host != null)
            ApplyActivePage(persist: true);
        base.OnClosing(e);
    }
}
