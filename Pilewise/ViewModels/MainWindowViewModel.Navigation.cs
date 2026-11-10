using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pilewise.ViewModels;

public enum MainTab
{
    Sources,
    Export,
    Automation,
    Settings
}

public partial class MainWindowViewModel
{
    [ObservableProperty] private int selectedTab = (int)MainTab.Export;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidebarExpanded), nameof(SidebarWidth), nameof(SidebarToggleTip))]
    private bool isSidebarCollapsed;

    public bool IsSidebarExpanded => !IsSidebarCollapsed;

    public double SidebarWidth => IsSidebarCollapsed ? 64 : 210;

    public string SidebarToggleTip => IsSidebarCollapsed ? "Expand the menu" : "Collapse the menu to icons";

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
    }

    [RelayCommand]
    private void GoToSources()
    {
        SelectedTab = (int)MainTab.Sources;
    }

    [RelayCommand]
    private void GoToExport()
    {
        SelectedTab = (int)MainTab.Export;
    }

    public static string ShortcutsText
    {
        get
        {
            var cmd = System.OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";
            return $"Shortcuts: {cmd}1–{cmd}4 switch pages · {cmd}L opens the library · in the library, " +
                   $"{cmd}F jumps to the search and Esc clears it (or closes the window).";
        }
    }

    private void ChooseStartTab(bool firstRun)
    {
        SelectedTab = (int)(firstRun ? MainTab.Sources : MainTab.Export);
    }

    private const string NeedsSource = "Needs a source";

    public string CsvStatus => HasSource ? "Ready" : NeedsSource;

    public string NotionStatus =>
        !HasSource ? NeedsSource
        : CanExportNotion ? "Ready"
        : "Needs token & database ID";

    public string SheetsStatus =>
        !HasSource ? NeedsSource
        : CanExportSheets ? "Ready"
        : "Needs a table name";

    public bool HasNoSource => !HasSource;

    /// <summary>0 = follow the system, 1 = light, 2 = dark (saved as <c>Theme: Light / Dark</c>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystemTheme), nameof(IsLightTheme), nameof(IsDarkTheme),
        nameof(ThemeThumbMargin),
        nameof(ThemeTip))]
    private int themeIndex;

    partial void OnThemeIndexChanged(int value)
    {
        if (Application.Current is { } app) app.RequestedThemeVariant = ThemeVariantFor(value);
    }

    public bool IsSystemTheme => ThemeIndex == 0;

    public bool IsLightTheme => ThemeIndex == 1;

    public bool IsDarkTheme => ThemeIndex == 2;

    public const double ThemeOptionWidth = 36;

    public Thickness ThemeThumbMargin => new(ThemeIndex * ThemeOptionWidth, 0, 0, 0);

    [RelayCommand]
    private void UseSystemTheme()
    {
        ThemeIndex = 0;
    }

    [RelayCommand]
    private void UseLightTheme()
    {
        ThemeIndex = 1;
    }

    [RelayCommand]
    private void UseDarkTheme()
    {
        ThemeIndex = 2;
    }

    [RelayCommand]
    private void CycleTheme()
    {
        ThemeIndex = (ThemeIndex + 1) % 3;
    }

    public string ThemeTip => ThemeIndex switch
    {
        1 => "Theme: light (click for dark)",
        2 => "Theme: dark (click for system)",
        _ => "Theme: system (click for light)"
    };

    private static ThemeVariant ThemeVariantFor(int index)
    {
        return index switch
        {
            1 => ThemeVariant.Light,
            2 => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    private string? ThemeSetting => ThemeIndex switch
    {
        1 => "Light",
        2 => "Dark",
        _ => null
    };

    private static int ThemeIndexFor(string? setting)
    {
        return setting?.Trim().ToLowerInvariant() switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0
        };
    }
}