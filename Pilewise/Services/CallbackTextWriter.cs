using System;
using System.IO;
using System.Text;

namespace Pilewise.Services;

public sealed class CallbackTextWriter(Action<string> onLine) : TextWriter
{
    private readonly StringBuilder _pending = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (value == '\n')
        {
            Flush();
            return;
        }

        if (value != '\r') _pending.Append(value);
    }

    public override void WriteLine(string? value)
    {
        Flush();
        onLine(value ?? string.Empty);
    }

    public override void Flush()
    {
        if (_pending.Length == 0) return;
        var line = _pending.ToString();
        _pending.Clear();
        onLine(line);
    }
}