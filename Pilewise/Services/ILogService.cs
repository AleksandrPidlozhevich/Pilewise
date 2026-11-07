using System;

namespace Pilewise.Services;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public interface ILogService
{
    void WriteLog(string message, LogLevel level = LogLevel.Info);

    void WriteInfo(string message);

    void WriteWarning(string message);

    void WriteError(string message);

    void WriteException(Exception ex, string additionalMessage = "");
}
