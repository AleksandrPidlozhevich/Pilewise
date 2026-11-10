using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.Services;

/// <summary>
/// Stand-ins for the Avalonia previewer (the design-time view model constructors): the IDE runs
/// several previewer hosts, and they must not read or write the user's encrypted settings or log.
/// </summary>
internal sealed class DesignTimeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];

    public string? Load(string key)
    {
        return _values.GetValueOrDefault(key);
    }

    public void Save(string key, string value)
    {
        _values[key] = value;
    }

    public void Delete(string key)
    {
        _values.Remove(key);
    }
}

internal sealed class DesignTimeStateStore : IStateStore
{
    private readonly Dictionary<string, string> _values = [];

    public string? Load(string key)
    {
        return _values.GetValueOrDefault(key);
    }

    public void Save(string key, string value)
    {
        _values[key] = value;
    }

    public void Delete(string key)
    {
        _values.Remove(key);
    }
}

/// <summary>
/// No update checks in the previewer: Velopack's <c>UpdateManager</c> throws unless
/// <c>VelopackApp.Build().Run()</c> ran, which only <c>Program.Main</c> does.
/// </summary>
internal sealed class DesignTimeUpdateService : IUpdateService
{
    public string CurrentVersion =>
        typeof(DesignTimeUpdateService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ??
        "0.0.0";

    public bool IsInstalled => false;

    public string? LastCheckError => null;

    public Task<string?> CheckForUpdateAsync()
    {
        return Task.FromResult<string?>(null);
    }

    public Task DownloadAndRestartAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}

internal sealed class DesignTimeLog : ILogService
{
    public void WriteLog(string message, LogLevel level = LogLevel.Info)
    {
    }

    public void WriteInfo(string message)
    {
    }

    public void WriteWarning(string message)
    {
    }

    public void WriteError(string message)
    {
    }

    public void WriteException(Exception ex, string additionalMessage = "")
    {
    }
}