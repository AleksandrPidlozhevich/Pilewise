using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Pilewise.Models;
using Pilewise.Services;
using Pilewise.ViewModels;
using Pilewise.Views;
using Xunit;

namespace Pilewise.Tests.Ui;

/// <summary>
/// Renders every window headlessly in both themes (snapshots in <c>ui-snapshots/</c>) and checks layout
/// and keyboard behaviour that unit tests of the view models can't see.
/// </summary>
public class UiTests
{
    public static TheoryData<string> Windows => new()
    {
        "library", "history", "notion-preview", "duplicates-notion", "duplicates-sheets", "error",
        "main-sources", "main-export", "main-automation", "main-settings", "main-collapsed"
    };

    [Theory]
    [MemberData(nameof(Windows))]
    public async Task Window_RendersInBothThemes_WithoutBindingErrors(string name)
    {
        var errors = await UiSession.RunAsync(() =>
        {
            var found = new List<string>();
            foreach (var (variant, suffix) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                UiSession.UseTheme(variant);
                UiSession.ClearBindingErrors();
                var window = UiSession.Show(Create(name));
                Assert.True(UiSession.Snapshot(window, $"{name}-{suffix}"), $"{name} produced no frame");
                found.AddRange(UiSession.BindingErrors.Select(e => $"{name}-{suffix}: {e}"));
                window.Close();
            }

            return found;
        });

        // Full messages (Assert.Empty would cut them off).
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public Task Library_ColumnHeaders_LineUpWithTheRows() => UiSession.RunAsync(() =>
    {
        var window = UiSession.Show(Create("library"));
        try
        {
            foreach (var (header, column) in new[] { ("PLATFORM", 1), ("PLAYTIME", 2), ("LAST PLAYED", 3), ("ID", 4) })
            {
                var headerBlock = Descendants<TextBlock>(window).Single(t => t.Text == header);
                var firstRow = Descendants<ListBoxItem>(window).First();
                var cell = Descendants<Grid>(firstRow).First().Children.Single(c => Grid.GetColumn(c) == column);

                Assert.InRange(X(cell, window) - X(headerBlock, window), -2, 2);
            }
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// At the smallest window size, nothing on any page may stick out of its panel (a row that
    /// doesn't wrap, a field too wide…). Checks the logical tree, so a control's own template parts
    /// (e.g. the text inside a scrolled text box) don't count.
    /// </summary>
    [Theory]
    [InlineData(MainTab.Sources)]
    [InlineData(MainTab.Export)]
    [InlineData(MainTab.Automation)]
    [InlineData(MainTab.Settings)]
    public async Task MinimumWidth_NothingSticksOutOfItsPanel(MainTab tab)
    {
        var problems = await UiSession.RunAsync(() =>
        {
            var window = MainWindowOn(tab);
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            UiSession.Show(window);
            UiSession.Snapshot(window, $"min-width-{tab}");
            try
            {
                return OverflowsOutOfPanels(window);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public Task Export_NotionFields_SitOnOneRow() => UiSession.RunAsync(() =>
    {
        var window = UiSession.Show(Create("main-export"));
        try
        {
            var token = Descendants<TextBox>(window).Single(t => t.PlaceholderText == "Notion integration token");
            var database = Descendants<TextBox>(window).Single(t => t.PlaceholderText == "Database ID");

            Assert.Equal(Y(token, window), Y(database, window), 1);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Shortcuts_CommandNumber_SwitchesPage() => UiSession.RunAsync(() =>
    {
        var window = UiSession.Show(Create("main-sources"));
        try
        {
            var viewModel = (MainWindowViewModel)window.DataContext!;

            window.KeyPress(Key.D3, CommandModifier, PhysicalKey.Digit3, "3");
            UiSession.Pump();
            Assert.Equal((int)MainTab.Automation, viewModel.SelectedTab);

            window.KeyPress(Key.D1, CommandModifier, PhysicalKey.Digit1, "1");
            UiSession.Pump();
            Assert.Equal((int)MainTab.Sources, viewModel.SelectedTab);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Library_Escape_ClearsTheSearch_ThenCloses() => UiSession.RunAsync(() =>
    {
        var window = UiSession.Show(Create("library"));
        var library = (LibraryViewModel)window.DataContext!;
        library.SearchText = "hades";
        UiSession.Pump();

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        UiSession.Pump();
        Assert.True(string.IsNullOrEmpty(library.SearchText));
        Assert.True(window.IsVisible);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        UiSession.Pump();
        Assert.False(window.IsVisible);
    });

    [Fact]
    public Task Dialog_Escape_ClosesWithFalse_AndConfirmClosesWithTrue() => UiSession.RunAsync(() =>
    {
        var owner = UiSession.Show(new Window());
        try
        {
            var escaped = new DuplicatesWindow { DataContext = Duplicates("duplicates-notion") };
            var escapedResult = escaped.ShowDialog<bool>(owner);
            UiSession.Pump();
            escaped.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            UiSession.Pump();
            Assert.True(escapedResult.IsCompleted);
            Assert.False(escapedResult.Result);

            var confirmed = new DuplicatesWindow { DataContext = Duplicates("duplicates-notion") };
            var confirmedResult = confirmed.ShowDialog<bool>(owner);
            UiSession.Pump();
            Descendants<Button>(confirmed).Single(b => b.Classes.Contains(DialogWindow.ConfirmClass))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            UiSession.Pump();
            Assert.True(confirmedResult.IsCompleted);
            Assert.True(confirmedResult.Result);
        }
        finally
        {
            owner.Close();
        }
    });

    [Fact]
    public Task Sidebar_Collapsed_ShowsIconsOnly() => UiSession.RunAsync(() =>
    {
        var window = UiSession.Show(Create("main-collapsed"));
        try
        {
            var navTexts = Descendants<TextBlock>(window)
                .Where(t => t.Text is "Sources" or "Export" or "Automation" or "Settings" &&
                            t.FindAncestorOfType<ListBoxItem>() != null)
                .ToList();

            Assert.Equal(4, navTexts.Count);
            Assert.All(navTexts, t => Assert.False(t.IsVisible));
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// Regenerates the README's screenshots (made-up data, no real keys or ids) when
    /// <c>PILEWISE_README_SCREENSHOTS</c> names the output folder, e.g. <c>docs/images</c>; otherwise it
    /// does nothing. Each window is saved as <c>&lt;name&gt;.png</c> (light) and <c>&lt;name&gt;-dark.png</c>.
    /// </summary>
    [Fact]
    public Task ReadmeScreenshots_WhenAsked()
    {
        var folder = Environment.GetEnvironmentVariable("PILEWISE_README_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(folder)) return Task.CompletedTask;

        var shots = new (string Name, Func<Window> Create)[]
        {
            ("main-window", () => ReadmeMainWindow(MainTab.Export)),
            ("main-sources", () => ReadmeMainWindow(MainTab.Sources)),
            ("main-automation", () => ReadmeMainWindow(MainTab.Automation)),
            ("library", () => new LibraryWindow { DataContext = ReadmeLibrary(), Width = 900, Height = 880 }),
            ("notion-preview", () => new NotionExportPreviewWindow { DataContext = ReadmeNotionPreview() })
        };

        return UiSession.RunAsync(() =>
        {
            foreach (var (name, create) in shots)
            foreach (var (variant, suffix) in new[] { (ThemeVariant.Light, ""), (ThemeVariant.Dark, "-dark") })
            {
                UiSession.UseTheme(variant);
                var window = UiSession.Show(create());
                Assert.True(UiSession.SaveFrame(window, System.IO.Path.Combine(folder, $"{name}{suffix}.png")));
                window.Close();
            }
        });
    }

    private static MainWindow ReadmeMainWindow(MainTab tab)
    {
        var viewModel = new MainWindowViewModel
        {
            SelectedTab = (int)tab,
            SteamApiKey = "0123456789ABCDEF0123456789ABCDEF",
            SteamId = "76561198000000000",
            NotionToken = "ntn_example_integration_token",
            DbId = "0123456789abcdef0123456789abcdef",
            GoogleSheetsTableName = "My game backlog",
            CsvLastSync = "Last sync: today 14:20",
            NotionLastSync = "Last sync: today 14:20",
            NotionProgress = "Completed 137 of 386 (35%)",
            AutoSyncIntervalIndex = 2,
            AutoSyncNotion = true,
            AutoSyncSheets = true
        };
        viewModel.Epic.IsConnected = true;
        viewModel.Gog.IsConnected = true;
        return new MainWindow { DataContext = viewModel, Width = 900, Height = 640 };
    }

    private static NotionExportPreviewViewModel ReadmeNotionPreview()
    {
        var created = new[]
        {
            ("Hades", "Steam"), ("Alan Wake 2", "Epic"), ("Cyberpunk 2077", "GOG"), ("Starfield", "Xbox/Game Pass"),
            ("Control", "Epic"), ("Celeste", "Steam"), ("Forza Horizon 5", "Xbox"), ("Disco Elysium", "GOG")
        };
        var plan = new NotionExportPlan(
            new GameLibraryResult([], false, false, false, false, false, false),
            [],
            created.Select(g => new NotionPageCreate(g.Item1, g.Item2, new object())).ToList(),
            [
                new NotionPageUpdate("p1", "The Witcher 3: Wild Hunt", "Steam · Steam:292030",
                    "Steam/GOG · Steam:292030; GOG:1207664643", new object()),
                new NotionPageUpdate("p2", "Hollow Knight", "Steam · Steam:367520",
                    "Steam/Epic · Steam:367520; Epic:ns/hk", new object())
            ],
            378,
            [new NotionGoneGame("Refunded Game", "Steam")],
            true);
        return new NotionExportPreviewViewModel(plan);
    }

    private static LibraryViewModel ReadmeLibrary()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var rows = new[]
        {
            Row("Steam/GOG", "The Witcher 3: Wild Hunt", "Steam:292030; GOG:1207664643", 6200),
            Row("Steam", "Portal 2", "Steam:620", 750),
            Row("Steam", "Hades", "Steam:1145360", 0),
            Row("Steam/Epic", "Hollow Knight", "Steam:367520; Epic:ns/hk", 1840),
            Row("Epic", "Alan Wake 2", "Epic:ns/cat"),
            Row("GOG", "Disco Elysium", "GOG:1771589310", 2100),
            Row("Xbox/Game Pass", "Starfield", "Xbox:1234", 300),
            Row("Steam", "Stardew Valley", "Steam:413150", 9100),
            Row("Epic", "Control", "Epic:ns/control", 960),
            Row("GOG", "Cyberpunk 2077", "GOG:1423049311"),
            Row("Steam", "Celeste", "Steam:504230", 640),
            Row("Xbox", "Forza Horizon 5", "Xbox:5678", 1250)
        };
        var backlog = new BacklogSnapshot("Notion", DateTimeOffset.Now, 4, 12,
            ["Steam:620", "Steam:413150", "Steam:504230", "GOG:1771589310"]);
        return new LibraryViewModel(rows, [Row("Steam", "Test Server", "Steam:1")], new IgnoredGames(), backlog,
            new LibraryActions(_ => { }, _ => Task.CompletedTask, _ => Task.FromResult<BacklogSnapshot?>(backlog), "Notion"),
            addedOn: r => r.Name is "Hades" or "Cyberpunk 2077" ? today : null);
    }

    // ---- windows with sample data ---------------------------------------------------------------

    private static Window Create(string name) => name switch
    {
        "library" => new LibraryWindow { DataContext = Library() },
        "history" => new SyncLogWindow { DataContext = new SyncLogViewModel() },
        "notion-preview" => new NotionExportPreviewWindow { DataContext = new NotionExportPreviewViewModel() },
        "duplicates-notion" or "duplicates-sheets" => new DuplicatesWindow { DataContext = Duplicates(name) },
        "error" => new ErrorWindow("Export error in Notion",
            "Could not find database with ID: 1234abcd.\nMake sure the database is shared with your integration."),
        "main-sources" => MainWindowOn(MainTab.Sources),
        "main-export" => MainWindowOn(MainTab.Export),
        "main-automation" => MainWindowOn(MainTab.Automation),
        "main-settings" => MainWindowOn(MainTab.Settings),
        "main-collapsed" => MainWindowOn(MainTab.Export, collapsed: true),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
    };

    private static MainWindow MainWindowOn(MainTab tab, bool collapsed = false)
    {
        var viewModel = new MainWindowViewModel { SelectedTab = (int)tab, IsSidebarCollapsed = collapsed };
        return new MainWindow { DataContext = viewModel, Width = 900, Height = 640 };
    }

    private static LibraryViewModel Library()
    {
        var rows = new[]
        {
            Row("Steam/GOG", "The Witcher 3: Wild Hunt", "Steam:292030; GOG:1207664643", 6200),
            Row("Steam", "Portal 2", "Steam:620", 750),
            Row("Steam", "Hades", "Steam:1145360", 0),
            Row("Epic", "Alan Wake 2", "Epic:ns/cat"),
            Row("GOG", "Disco Elysium", "GOG:1771589310"),
            Row("Xbox/Game Pass", "Starfield", "Xbox:1234", 300)
        };
        var backlog = new BacklogSnapshot("Notion", DateTimeOffset.Now, 1, 6, ["Steam:620"]);
        return new LibraryViewModel(rows, [Row("Steam", "Test Server", "Steam:1")], new IgnoredGames(), backlog,
            new LibraryActions(_ => { }, _ => Task.CompletedTask, _ => Task.FromResult<BacklogSnapshot?>(backlog), "Notion"),
            addedOn: r => r.Name == "Hades" ? DateOnly.FromDateTime(DateTime.Today) : null);
    }

    private static DuplicatesViewModel Duplicates(string name)
    {
        if (name == "duplicates-sheets")
        {
            var rows = new[]
            {
                new SheetRow(1, Row("Steam", "Hades", "Steam:1145360"), "Steam", "Steam:1145360", false),
                new SheetRow(7, Row("Epic", "Hades", "Epic:ns/cat"), "Epic", "Epic:ns/cat", true)
            };
            return DuplicatesViewModel.ForSheets(SheetDuplicateFinder.Find(rows));
        }

        var pages = new[]
        {
            Page("old", "Steam", "Witcher 3", "Steam:292030", -30),
            Page("new", "GOG", "Witcher 3", "GOG:1207664643", -1)
        };
        return DuplicatesViewModel.ForNotion(NotionDuplicateFinder.Find(pages));
    }

    private static NotionGameRow Page(string id, string platform, string name, string gameId, int days) =>
        new(Row(platform, name, gameId), id, new HashSet<string>([platform]), gameId,
            CreatedTime: DateTimeOffset.Now.AddDays(days));

    private static CsvGameExportRow Row(string platform, string name, string id, int? minutes = null)
    {
        var row = CsvGameExportService.CreateRow(platform, name, id);
        row.PlaytimeMinutes = minutes;
        return row;
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static List<string> OverflowsOutOfPanels(Window window)
    {
        var problems = new List<string>();
        foreach (var panel in Descendants<Border>(window).Where(b => b.Classes.Contains("panel") && b.IsEffectivelyVisible))
        {
            var panelRight = Right(panel, window);
            foreach (var control in panel.GetLogicalDescendants().OfType<Control>()
                         .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0))
            {
                var right = Right(control, window);
                if (right > panelRight + 1)
                    problems.Add($"{control.GetType().Name} \"{Label(control)}\" ends at x={right:0}, its panel at x={panelRight:0}");
            }
        }

        return problems;
    }

    private static string Label(Control control) => control switch
    {
        ContentControl { Content: string text } => text,
        TextBlock { Text: { } text } => text,
        TextBox { PlaceholderText: { } placeholder } => placeholder,
        _ => control.Name ?? ""
    };

    private static double Right(Visual visual, Visual relativeTo) =>
        visual.TranslatePoint(new Point(visual.Bounds.Width, 0), relativeTo)!.Value.X;

    // The headless platform's command key (Ctrl; tests run on Windows / Linux CI).
    private static RawInputModifiers CommandModifier =>
        OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static IEnumerable<T> Descendants<T>(Visual root) => root.GetVisualDescendants().OfType<T>();

    private static double X(Visual visual, Visual relativeTo) => visual.TranslatePoint(default, relativeTo)!.Value.X;

    private static double Y(Visual visual, Visual relativeTo) => visual.TranslatePoint(default, relativeTo)!.Value.Y;
}
