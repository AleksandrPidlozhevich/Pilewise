using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Pilewise.Services;

/// <summary>
/// Keeps each state key in its own text file under <c>State</c> in the data folder. One file per key,
/// so the app and a command-line sync running at the same time don't overwrite each other's keys.
/// </summary>
/// <remarks>
/// Older builds kept this state in the secret store (one macOS Keychain item per key, each of which
/// can prompt for access). A key missing here is moved over from there the first time it's read:
/// written to its file, then deleted from the secret store. Only keys the app actually reads are
/// migrated, so nothing is touched in the Keychain up front.
/// </remarks>
public sealed class FileStateStore : IStateStore
{
    private const string FolderName = "State";

    private readonly string _folder;
    private readonly ILogService _log;
    private readonly ISecretStore? _legacy;
    private readonly HashSet<string> _checkedLegacy = [];
    private readonly object _gate = new();

    public FileStateStore(ILogService log, ISecretStore? legacy = null, string? folder = null)
    {
        _log = log;
        _legacy = legacy;
        _folder = folder ?? Path.Combine(AppPaths.DataDirectory, FolderName);
    }

    public string? Load(string key)
    {
        lock (_gate)
        {
            var path = PathOf(key);
            try
            {
                if (File.Exists(path)) return File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _log.WriteWarning($"StateStore: failed to read '{key}': {ex.Message}");
                return null;
            }

            return MigrateFromLegacy(key);
        }
    }

    public void Save(string key, string value)
    {
        lock (_gate)
        {
            // Whatever the secret store still holds for this key is older than this value.
            if (Write(key, value) && _legacy != null && _checkedLegacy.Add(key)) _legacy.Delete(key);
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            try
            {
                File.Delete(PathOf(key));
            }
            catch (Exception ex)
            {
                _log.WriteWarning($"StateStore: failed to delete '{key}': {ex.Message}");
            }

            // Otherwise the next Load would bring the old value back from there.
            if (_legacy != null && _checkedLegacy.Add(key)) _legacy.Delete(key);
        }
    }

    // Once per key and run: a key that isn't in the secret store either stays unknown until saved.
    private string? MigrateFromLegacy(string key)
    {
        if (_legacy == null || !_checkedLegacy.Add(key)) return null;

        var value = _legacy.Load(key);
        if (value == null) return null;

        if (Write(key, value))
        {
            _legacy.Delete(key);
            _log.WriteInfo($"StateStore: moved '{key}' out of the secret store.");
        }

        return value;
    }

    private bool Write(string key, string value)
    {
        var path = PathOf(key);
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(_folder);
            // Write aside, then swap in: a crash mid-write leaves the previous file intact.
            File.WriteAllText(temp, value, Encoding.UTF8);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _log.WriteWarning($"StateStore: failed to write '{key}': {ex.Message}");
            return false;
        }
    }

    private string PathOf(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (var c in key)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return Path.Combine(_folder, sb + ".txt");
    }
}
