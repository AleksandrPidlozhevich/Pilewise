using Pilewise.Services;
using Pilewise.Tests.Fakes;

namespace Pilewise.Tests;

public class FileStateStoreTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "pilewise-state-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    [Fact]
    public void Save_IsReadBack_ByANewInstance()
    {
        new FileStateStore(new SilentLog(), folder: _folder).Save("window", "1,2,800,600,False");

        Assert.Equal("1,2,800,600,False", new FileStateStore(new SilentLog(), folder: _folder).Load("window"));
        Assert.True(File.Exists(Path.Combine(_folder, "window.txt")));
    }

    [Fact]
    public void Load_UnknownKey_IsNull()
    {
        Assert.Null(new FileStateStore(new SilentLog(), folder: _folder).Load("sync-log"));
    }

    [Fact]
    public void Delete_RemovesTheFile()
    {
        var store = new FileStateStore(new SilentLog(), folder: _folder);
        store.Save("backlog", "[]");

        store.Delete("backlog");

        Assert.Null(store.Load("backlog"));
    }

    [Fact]
    public void Load_MovesAnOldSecretStoreValue_IntoTheFile()
    {
        var legacy = new CountingSecrets { ["last-version"] = "2.0.0" };

        Assert.Equal("2.0.0", new FileStateStore(new SilentLog(), legacy, _folder).Load("last-version"));

        Assert.Null(legacy.Load("last-version"));
        var afterwards = new FileStateStore(new SilentLog(), new CountingSecrets(), _folder);
        Assert.Equal("2.0.0", afterwards.Load("last-version"));
    }

    [Fact]
    public void Migration_OnlyTouchesTheKeysThatAreRead_OncePerRun()
    {
        var legacy = new CountingSecrets { ["window"] = "w", ["ignored-games"] = "Steam:1" };
        var store = new FileStateStore(new SilentLog(), legacy, _folder);

        store.Load("sync-log");
        store.Load("sync-log");

        Assert.Equal(["sync-log"], legacy.Loaded);
        Assert.Equal("Steam:1", legacy.Load("ignored-games"));
    }

    [Fact]
    public void Save_DropsTheOldSecretStoreValue()
    {
        var legacy = new CountingSecrets { ["first-seen"] = "old" };
        var store = new FileStateStore(new SilentLog(), legacy, _folder);

        store.Save("first-seen", "new");

        Assert.Null(legacy.Load("first-seen"));
        Assert.Equal("new", new FileStateStore(new SilentLog(), legacy, _folder).Load("first-seen"));
    }

    [Fact]
    public void Delete_DoesNotBringBackTheOldSecretStoreValue()
    {
        var legacy = new CountingSecrets { ["last-sync"] = "old" };

        new FileStateStore(new SilentLog(), legacy, _folder).Delete("last-sync");

        Assert.Null(new FileStateStore(new SilentLog(), legacy, _folder).Load("last-sync"));
    }

    private sealed class CountingSecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];

        public List<string> Loaded { get; } = [];

        public string this[string key]
        {
            set => _values[key] = value;
        }

        public string? Load(string key)
        {
            Loaded.Add(key);
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
}