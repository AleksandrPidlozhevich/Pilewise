using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Pilewise.Models;
using Pilewise.Services;
using Pilewise.ViewModels;
using Pilewise.Views;

namespace Pilewise;

public static class AppServices
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ILogService, FileLogService>();
        services.AddSingleton<ISecretStore, MsalSecretStore>();
        services.AddSingleton<IStateStore>(sp =>
            new FileStateStore(sp.GetRequiredService<ILogService>(), sp.GetRequiredService<ISecretStore>()));
        services.AddSingleton<IStoreClientFactory, StoreClientFactory>();
        services.AddSingleton<IDialogService, WindowDialogService>();
        services.AddSingleton<IWebAuthenticator>(_ => new EmbeddedWebViewAuthenticator(MainWindow));
        services.AddSingleton<CsvFileService>();
        services.AddSingleton<IUpdateService, VelopackUpdateService>();
        services.AddSingleton<IAutoStart, AutoStart>();

        services.AddSingleton<StoreSelection>();
        services.AddSingleton<IgnoredGames>(sp => new IgnoredGames(sp.GetRequiredService<IStateStore>()));
        services.AddSingleton<BacklogProgress>(sp => new BacklogProgress(sp.GetRequiredService<IStateStore>()));
        services.AddSingleton<SyncLog>(sp => new SyncLog(sp.GetRequiredService<IStateStore>()));
        services.AddSingleton<FirstSeenGames>(sp => new FirstSeenGames(sp.GetRequiredService<IStateStore>()));
        services.AddSingleton<CompletedGames>(sp => new CompletedGames(sp.GetRequiredService<IStateStore>()));
        services.AddSingleton<IGameLibraryService, GameLibraryService>();
        services.AddSingleton<SteamLinuxLookup>();
        services.AddSingleton<INotionGateway, NotionGateway>();
        services.AddSingleton<NotionGameExporter>();
        services.AddSingleton<GoogleSheetsGameExporter>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    // Resolved lazily: the main window doesn't exist yet when the container is built.
    private static TopLevel? MainWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
