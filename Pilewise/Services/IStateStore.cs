namespace Pilewise.Services;

public interface IStateStore
{
    string? Load(string key);

    void Save(string key, string value);

    void Delete(string key);
}