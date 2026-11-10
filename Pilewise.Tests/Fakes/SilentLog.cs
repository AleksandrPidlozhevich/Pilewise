using System;
using Pilewise.Services;

namespace Pilewise.Tests.Fakes;

public sealed class SilentLog : ILogService
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