using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Pilewise.Services;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Velopack;

namespace Pilewise;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack's install/update/uninstall hooks (which exit right away)
        // and finishes applying a downloaded update. A no-op when not installed via Velopack.
        VelopackApp.Build().Run();

        if (SyncCommandLine.IsHeadless(args))
        {
            Environment.ExitCode = RunHeadless(args);
            return;
        }

        // --disable-gpu-compositing: fixes a black-screen issue in this windowing setup.
        // --disable-logging --log-level=3: silence harmless Chromium teardown noise on the console
        //   (e.g. "Failed to unregister class Chrome_WidgetWin_0. Error = 1412"); --disable-logging alone
        //   still lets ERROR lines through, level 3 keeps only fatal ones.
        Environment.SetEnvironmentVariable(
            "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--disable-gpu-compositing --disable-logging --log-level=3");

        // One window per user: a second start (shortcut while it sits in the tray) shows the
        // running one instead, so two auto-syncs never run side by side. A login start
        // (--minimized) just leaves quietly.
        using var instance = SingleInstance.TryAcquire(SingleInstance.CurrentUserName);
        if (instance == null)
        {
            if (!args.Contains(AutoStart.MinimizedArgument))
                SingleInstance.SignalExisting(SingleInstance.CurrentUserName, TimeSpan.FromSeconds(3));
            return;
        }

        App.Instance = instance;
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    private static int RunHeadless(string[] args)
    {
        // A WinExe has no console of its own; borrow the one it was started from (if any) so the
        // output shows up there. Must happen before anything touches Console.
        if (OperatingSystem.IsWindows()) AttachConsole(AttachParentProcess);

        var command = SyncCommandLine.Parse(args);
        if (command.Options == null)
        {
            Console.WriteLine(command.Message);
            return command.ExitCode;
        }

        using var services = AppServices.Build();
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        var sync = new HeadlessSync(
            services.GetRequiredService<ISecretStore>(),
            services.GetRequiredService<IStateStore>(),
            services.GetRequiredService<IGameLibraryService>(),
            services.GetRequiredService<NotionGameExporter>(),
            services.GetRequiredService<GoogleSheetsGameExporter>(),
            services.GetRequiredService<ILogService>(),
            Console.Out,
            services.GetRequiredService<StoreSelection>(),
            services.GetRequiredService<SyncLog>(),
            linux: services.GetRequiredService<SteamLinuxLookup>(),
            completed: services.GetRequiredService<CompletedGames>());
        return sync.RunAsync(command.Options, cancel.Token).GetAwaiter().GetResult();
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}