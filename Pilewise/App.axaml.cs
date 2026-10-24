using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Pilewise.Services;
using Pilewise.ViewModels;
using Pilewise.Views;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Pilewise;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _quitting;

    /// <summary>Set by <c>Program.Main</c>: another start asks this one to show its window.</summary>
    public static SingleInstance? Instance { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            var services = AppServices.Build();
            HookUnhandledExceptions(services.GetRequiredService<ILogService>(), services.GetRequiredService<IDialogService>());
            var window = services.GetRequiredService<MainWindow>();
            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            window.DataContext = viewModel;
            _window = window;

            // The app lives until "Quit" (tray) or a real close: closing to the tray only hides the window.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Closing += (_, e) =>
            {
                window.SavePlacement();
                viewModel.SaveOnExit();

                // Only a user close goes to the tray; logoff / shutdown / Quit really close.
                if (!_quitting && viewModel.KeepsRunningOnClose &&
                    e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic)
                {
                    e.Cancel = true;
                    window.Hide();
                }
            };
            window.Closed += (_, _) => desktop.Shutdown();

            viewModel.IsTrayAvailable = TraySupport.IsAvailable();
            CreateTrayIcon(viewModel);
            viewModel.ShowWindowRequested += ShowMainWindow;
            if (Instance != null) Instance.ShowRequested += () => Dispatcher.UIThread.Post(ShowMainWindow);

            // Started at login (--minimized): stay in the tray. Assigning MainWindow after start-up
            // keeps the lifetime from showing it, while dialogs can still find it. Without a tray
            // the window opens normally, or there'd be no way to reach it.
            if (viewModel.IsTrayAvailable && desktop.Args?.Contains(AutoStart.MinimizedArgument) == true)
                Dispatcher.UIThread.Post(() => desktop.MainWindow = window);
            else
                desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Last line of defence: an error that escapes a command or event handler (e.g. a file locked by
    // Excel) is logged and shown instead of closing the app — and with it the tray and auto-sync.
    private static void HookUnhandledExceptions(ILogService log, IDialogService dialogs)
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            log.WriteException(e.Exception, "Unhandled UI error");
            e.Handled = true;
            _ = dialogs.ShowErrorAsync("Unexpected error", e.Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.WriteException(e.Exception, "Unobserved task error");
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) log.WriteException(ex, "Fatal error");
        };
    }

    // Tray icon on every platform (Linux needs a desktop with StatusNotifier / AppIndicator support):
    // click to open; menu Open / Sync now / Sync history / Update to vX (when one is out) / Quit.
    // The tooltip shows a newer version and the last result, and a failed (background) sync adds a
    // red dot to the icon until a later result replaces it.
    private void CreateTrayIcon(MainWindowViewModel viewModel)
    {
        var normalIcon = new WindowIcon(AssetLoader.Open(AppIconUri));
        WindowIcon? warningIcon = null;

        var open = new NativeMenuItem("Open Pilewise");
        open.Click += (_, _) => ShowMainWindow();
        var syncNow = new NativeMenuItem("Sync now");
        syncNow.Click += async (_, _) => await viewModel.SyncNowAsync();
        var history = new NativeMenuItem("Sync history…");
        history.Click += (_, _) => viewModel.ShowSyncHistory();
        // Opens the window first, so the download progress (and its Cancel) can be seen.
        var update = new NativeMenuItem { IsVisible = false };
        update.Click += async (_, _) =>
        {
            ShowMainWindow();
            await viewModel.InstallUpdateCommand.ExecuteAsync(null);
        };
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();

        _tray = new TrayIcon
        {
            Icon = normalIcon,
            ToolTipText = "Pilewise",
            Menu = new NativeMenu { Items = { open, syncNow, history, update, new NativeMenuItemSeparator(), quit } }
        };
        _tray.Clicked += (_, _) => ShowMainWindow();

        void RefreshUpdate()
        {
            update.Header = viewModel.UpdateButtonText;
            update.IsVisible = viewModel.IsUpdateAvailable;
            RefreshTooltip();
        }

        void RefreshTooltip()
        {
            if (_tray != null)
                _tray.ToolTipText = TraySupport.Tooltip(viewModel.ResultMessage, viewModel.AvailableUpdateVersion);
        }

        viewModel.PropertyChanged += (_, e) =>
        {
            if (_tray == null) return;

            if (e.PropertyName is nameof(MainWindowViewModel.AvailableUpdateVersion))
            {
                RefreshUpdate();
                return;
            }

            if (e.PropertyName is not nameof(MainWindowViewModel.ResultMessage)) return;

            RefreshTooltip();
            // Results starting with ⚠ are failures (see MainWindowViewModel.RunSyncTargetsAsync).
            var failed = viewModel.ResultMessage?.StartsWith('⚠') == true;
            _tray.Icon = failed ? warningIcon ??= WarningIcon() ?? normalIcon : normalIcon;
        };

        TrayIcon.SetIcons(this, [_tray]);
        // The startup check may already have finished.
        RefreshUpdate();
    }

    private void ShowMainWindow()
    {
        if (_window == null || _desktop == null) return;
        _desktop.MainWindow ??= _window;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void Quit()
    {
        _quitting = true;
        if (_window is { IsVisible: true }) _window.Close();
        else
        {
            if (_window?.DataContext is MainWindowViewModel viewModel) viewModel.SaveOnExit();
            _desktop?.Shutdown();
        }
    }

    private static readonly Uri AppIconUri = new("avares://Pilewise/Assets/Pilewise.ico");

    // The app icon with a red dot in the corner, drawn once when the first failure needs it; null if
    // drawing fails (the plain icon is used then — a tray badge isn't worth a crash).
    private static WindowIcon? WarningIcon()
    {
        try
        {
            return new WindowIcon(DrawWarningBitmap());
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    public static Bitmap DrawWarningBitmap()
    {
        using var source = new Bitmap(AssetLoader.Open(AppIconUri));
        var target = new RenderTargetBitmap(new PixelSize(32, 32));
        using (var context = target.CreateDrawingContext())
        {
            context.DrawImage(source, new Rect(0, 0, 32, 32));
            context.DrawEllipse(Brushes.White, null, new Point(24, 24), 8, 8);
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), null, new Point(24, 24), 6.5, 6.5);
        }

        return target;
    }
}
