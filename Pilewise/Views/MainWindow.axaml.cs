using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Pilewise.Models;
using Pilewise.Services;
using Pilewise.ViewModels;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Pilewise.Views;

public partial class MainWindow : Window
{
    private readonly IStoreClientFactory? _clients;
    private readonly ILogService? _log;
    private readonly IStateStore? _state;

    // Named controls from MainWindow.axaml.
    private readonly Border _dropOverlay;
    private readonly Grid _root;

    // Parameterless ctor for the XAML loader / previewer (no pre-warm without DI dependencies).
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _dropOverlay = this.Named<Border>("DropOverlay");
        _root = this.Named<Grid>("Root");

        // A soft fade-in instead of an instant flash when the theme changes.
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged += (_, _) => FadeIn();

        // Drop a game list / settings file anywhere on the window.
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => _dropOverlay.IsVisible = false);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Tunnel, so the shortcuts also work while a text field has the focus.
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
    }

    public MainWindow(IStoreClientFactory clients, ILogService log, IStateStore state) : this()
    {
        _clients = clients;
        _log = log;
        _state = state;
        RestorePlacement();
        if (OperatingSystem.IsWindows()) _ = PrewarmGogLoginAsync(AddPrewarmWebView());
    }

    // Hidden pre-warm WebView2 (Windows only): warms up the runtime at startup, and a few seconds in
    // also loads the GOG login page so the real Connect GOG window opens from cache. Native controls
    // ignore Avalonia's Opacity and paint on top of everything, so it can't simply be made
    // transparent — instead it's parked above the client area. Not on macOS: WKWebView needs no
    // runtime warm-up, and a 1x1 off-window WKWebView created before the first layout crashes
    // AppKit ("Invalid view geometry: y is NaN").
    private NativeWebView AddPrewarmWebView()
    {
        var webView = new NativeWebView
        {
            Width = 1,
            Height = 1,
            Margin = new Thickness(0, -100, 0, 0),
            IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Source = new Uri("about:blank")
        };
        Grid.SetColumn(webView, 1);
        _root.Children.Insert(0, webView);
        return webView;
    }

    // Reopens where it was closed (size, position, maximized), unless that spot is off every screen
    // now (e.g. a monitor was unplugged): then the remembered size is kept and the window is centred.
    private void RestorePlacement()
    {
        try
        {
            var saved = WindowPlacement.Parse(_state!.Load(WindowPlacement.StateKey));
            if (saved == null) return;

            var screens = Screens.All
                .Select(s => new ScreenArea(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height))
                .ToList();
            var scaling = Screens.Primary?.Scaling ?? 1;
            var (width, height, keepPosition) = saved.Fit(screens, scaling, MinWidth, MinHeight);

            Width = width;
            Height = height;
            if (keepPosition)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint(saved.X, saved.Y);
            }

            if (saved.Maximized) WindowState = WindowState.Maximized;
        }
        catch (Exception ex)
        {
            _log?.WriteWarning($"Couldn't restore the window placement: {ex.Message}");
        }
    }

    /// <summary>Remembers the size / position / maximized state (called when the window closes or hides).</summary>
    public void SavePlacement()
    {
        // A minimized window has no useful bounds: keep what was saved before.
        if (_state == null || !IsVisible || WindowState == WindowState.Minimized) return;
        try
        {
            var maximized = WindowState == WindowState.Maximized;
            // A maximized window's bounds are the screen's; keep the last normal size instead.
            var previous = WindowPlacement.Parse(_state.Load(WindowPlacement.StateKey));
            var placement = maximized && previous != null
                ? previous with { Maximized = true }
                : new WindowPlacement(Position.X, Position.Y, Width, Height, maximized);
            _state.Save(WindowPlacement.StateKey, placement.Serialize());
        }
        catch (Exception ex)
        {
            _log?.WriteWarning($"Couldn't save the window placement: {ex.Message}");
        }
    }

    // Drops the content to faint without animating, then lets the Opacity transition bring it back.
    private void FadeIn()
    {
        var transitions = _root.Transitions;
        _root.Transitions = null;
        _root.Opacity = 0.25;
        _root.Transitions = transitions;
        Dispatcher.UIThread.Post(() => _root.Opacity = 1, DispatcherPriority.Render);
    }

    // Ctrl+1…4 (⌘ on macOS): switch page. Ctrl+L: open the library.
    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;

        int? page = e.Key switch
        {
            Key.D1 or Key.NumPad1 => (int)MainTab.Sources,
            Key.D2 or Key.NumPad2 => (int)MainTab.Export,
            Key.D3 or Key.NumPad3 => (int)MainTab.Automation,
            Key.D4 or Key.NumPad4 => (int)MainTab.Settings,
            _ => null
        };
        if (page is { } index && Shortcuts.IsCommand(e, this, e.Key))
        {
            viewModel.SelectedTab = index;
            e.Handled = true;
        }
        else if (Shortcuts.IsCommand(e, this, Key.L) && viewModel.ViewLibraryCommand.CanExecute(null))
        {
            viewModel.ViewLibraryCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var acceptsFile = e.DataTransfer.Contains(DataFormat.File) &&
                          DataContext is MainWindowViewModel { Progress.IsBusy: false };
        e.DragEffects = acceptsFile ? DragDropEffects.Copy : DragDropEffects.None;
        _dropOverlay.IsVisible = acceptsFile;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        _dropOverlay.IsVisible = false;
        if (DataContext is not MainWindowViewModel viewModel) return;

        // One file at a time: the first file of the drop (folders are ignored).
        var file = Array.Find(e.DataTransfer.TryGetFiles() ?? [], item => item is IStorageFile) as IStorageFile;
        if (file == null) return;

        try
        {
            await viewModel.OpenDroppedFileAsync(file);
        }
        catch (Exception ex)
        {
            // async void: never let an exception escape to the dispatcher.
            _log?.WriteException(ex, "Error handling a dropped file");
        }
    }

    // A few seconds after startup, quietly load the GOG login page into the hidden 1x1 pre-warm
    // WebView. It shares the WebView2 profile/cache with the real login window, so Connect GOG then
    // opens from cache. Invisible to the user; failures are non-fatal.
    private async Task PrewarmGogLoginAsync(NativeWebView webView)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            await Dispatcher.UIThread.InvokeAsync(
                () => { webView.Source = _clients!.CreateGog().LoginRequestUri; });
        }
        catch (Exception ex)
        {
            _log!.WriteWarning($"GOG login pre-warm failed: {ex.Message}");
        }
    }
}
