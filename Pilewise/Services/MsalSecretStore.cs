using Microsoft.Identity.Client.Extensions.Msal;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Pilewise.Services;

public sealed class MsalSecretStore : ISecretStore
{
    private readonly string _folder;
    private readonly ILogService _log;
    private readonly ConcurrentDictionary<string, Storage> _byKey = new();

    public MsalSecretStore(ILogService log, string? folder = null)
    {
        _log = log;
        _folder = folder ?? Path.Combine(AppPaths.DataDirectory, FolderName);
        if (!Directory.Exists(_folder))
        {
            Directory.CreateDirectory(_folder);
            if (folder is null) MigrateLegacyFolder();
        }
    }

    private const string FolderName = "SecretStorage";

    // macOS ties each Keychain item to the binary that created it; any other binary reading it gets
    // an access prompt. A Debug build (run from the IDE) is a different binary from the installed app,
    // so it keeps its own items rather than claiming, or prompting for, the installed app's.
#if DEBUG
    private const string KeychainPrefix = "Pilewise-dev.";
#else
    private const string KeychainPrefix = "Pilewise.";
#endif

    // Older builds kept the store next to the exe. Copy it once into the per-user data folder so
    // users keep their sessions (on Windows the DPAPI-encrypted .bin files are the secrets).
    private void MigrateLegacyFolder()
    {
        var legacy = Path.Combine(AppPaths.ExeDirectory, FolderName);
        if (!Directory.Exists(legacy)) return;

        try
        {
            foreach (var file in Directory.GetFiles(legacy))
                File.Copy(file, Path.Combine(_folder, Path.GetFileName(file)), false);
            _log.WriteInfo($"SecretStore: migrated legacy store from '{legacy}'.");
        }
        catch (Exception ex)
        {
            _log.WriteWarning($"SecretStore: failed to migrate legacy store: {ex.Message}");
        }
    }

    public string? Load(string key)
    {
        try
        {
            var bytes = GetStorage(key).ReadData();
            return bytes is { Length: > 0 } ? Encoding.UTF8.GetString(bytes) : null;
        }
        catch (Exception ex)
        {
            _log.WriteWarning($"SecretStore: failed to read '{key}': {ex.Message}");
            return null;
        }
    }

    public void Save(string key, string value)
    {
        try
        {
            GetStorage(key).WriteData(Encoding.UTF8.GetBytes(value));
        }
        catch (Exception ex)
        {
            _log.WriteWarning($"SecretStore: failed to write '{key}': {ex.Message}");
        }
    }

    public void Delete(string key)
    {
        try
        {
            GetStorage(key).Clear();
        }
        catch (Exception ex)
        {
            _log.WriteWarning($"SecretStore: failed to delete '{key}': {ex.Message}");
        }
    }

    private Storage GetStorage(string key)
    {
        return _byKey.GetOrAdd(key, BuildStorage);
    }

    private Storage BuildStorage(string key)
    {
        var fileName = SafeFileName(key) + ".bin";

        var props = new StorageCreationPropertiesBuilder(fileName, _folder)
            .WithMacKeyChain($"{KeychainPrefix}{key}", key)
            .WithLinuxKeyring(
                $"com.pilewise.secrets.{key}",
                "default",
                $"Pilewise secret: {key}",
                new KeyValuePair<string, string>("Version", "1"),
                new KeyValuePair<string, string>("Key", key))
            .Build();

        return Storage.Create(props);
    }

    private static string SafeFileName(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (var c in key)
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8];
        return $"{sb}.{hash}";
    }
}