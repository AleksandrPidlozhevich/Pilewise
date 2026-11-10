using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Pilewise.Services;

/// <summary>
/// <see cref="IUpdateService"/> backed by Velopack, reading stable releases from the project's GitHub
/// Releases. Velopack picks the release channel (win-x64, osx-arm64, …) the app was packaged with,
/// so each platform only sees its own packages.
/// </summary>
public sealed class VelopackUpdateService : IUpdateService
{
    private const string RepoUrl = "https://github.com/AleksandrPidlozhevich/Pilewise";

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, false));
    private readonly ILogService _log;
    private UpdateInfo? _pending;

    public VelopackUpdateService(ILogService log)
    {
        _log = log;
    }

    public string CurrentVersion =>
        _manager.IsInstalled && _manager.CurrentVersion is { } installed
            ? installed.ToString()
            : AssemblyVersion();

    public bool IsInstalled => _manager.IsInstalled;

    public string? LastCheckError { get; private set; }

    public async Task<string?> CheckForUpdateAsync()
    {
        if (!_manager.IsInstalled) return null;

        try
        {
            _pending = await _manager.CheckForUpdatesAsync();
            LastCheckError = null;
            var version = _pending?.TargetFullRelease.Version.ToString();
            if (version is not null) _log.WriteInfo($"Update available: {version} (running {CurrentVersion}).");
            return version;
        }
        catch (Exception ex)
        {
            // Offline / GitHub rate limit: not worth bothering the user about.
            _log.WriteWarning($"Update check failed: {ex.Message}");
            LastCheckError = ex.Message;
            return null;
        }
    }

    public async Task DownloadAndRestartAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (_pending is null) return;

        await _manager.DownloadUpdatesAsync(_pending, p => progress?.Report(p), ct);
        _log.WriteInfo($"Restarting into {_pending.TargetFullRelease.Version}.");
        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }

    private static string AssemblyVersion()
    {
        var info = typeof(VelopackUpdateService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return info?.Split('+')[0] ?? "0.0.0";
    }
}