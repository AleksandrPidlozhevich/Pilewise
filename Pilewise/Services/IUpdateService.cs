using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.Services;

public interface IUpdateService
{
    string CurrentVersion { get; }

    bool IsInstalled { get; }

    string? LastCheckError { get; }

    /// <summary>
    /// Returns the newer version available for this platform, or null when up to date, when the app
    /// wasn't installed through Velopack (e.g. <c>dotnet run</c>), or when the check fails.
    /// </summary>
    Task<string?> CheckForUpdateAsync();

    Task DownloadAndRestartAsync(IProgress<int>? progress = null, CancellationToken ct = default);
}
