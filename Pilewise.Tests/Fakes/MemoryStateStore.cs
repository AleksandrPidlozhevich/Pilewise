using System.Collections.Generic;
using Pilewise.Services;

namespace Pilewise.Tests.Fakes;

public sealed class MemoryStateStore : IStateStore
{
    private readonly Dictionary<string, string> _values = new();

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