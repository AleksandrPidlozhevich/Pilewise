using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Pilewise.Services;

/// <summary>
/// <see cref="ILogService"/> that appends to a daily log file in a <c>Pilewise_Log</c> folder in the
/// per-user data folder (<see cref="AppPaths"/>). Writes a start banner on construction; thread-safe via
/// a single lock. Logging never throws: a line that can't be written is dropped.
/// </summary>
public sealed class FileLogService : ILogService
{
    private const string LogFolderName = "Pilewise_Log";
    private const string ShortDateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private const string Rule = "====================================================================================";
    private const int WriteAttempts = 3;

    /// <summary>Folder holding the daily log files (opened by the "Logs" link).</summary>
    public static string LogDirectory => Path.Combine(AppPaths.DataDirectory, LogFolderName);

    private readonly object _lock = new();
    private readonly string _directory;

    public FileLogService() : this(LogDirectory)
    {
    }

    /// <param name="directory">Where the daily files go (tests pass a temporary folder).</param>
    public FileLogService(string directory)
    {
        _directory = directory;
        Append($"{Rule}{Environment.NewLine}Pilewise_start_{DateTime.Now.ToString(TimeFormat)}{Environment.NewLine}{Rule}");
    }

    /// <summary>Today's file: chosen per line, so an app left running in the tray rolls over at midnight.</summary>
    public string CurrentFilePath => Path.Combine(_directory, $"{DateTime.Now.ToString(ShortDateFormat)}_Pilewise.log");

    public void WriteLog(string message, LogLevel level = LogLevel.Info) =>
        Append($"[{DateTime.Now.ToString(TimeFormat)}] [{level}] {message}");

    public void WriteInfo(string message) => WriteLog(message);

    public void WriteWarning(string message) => WriteLog(message, LogLevel.Warning);

    public void WriteError(string message) => WriteLog(message, LogLevel.Error);

    // ToString: the type, message, inner exceptions and stack traces, all of which help in a bug report.
    public void WriteException(Exception ex, string additionalMessage = "") =>
        WriteLog(string.IsNullOrEmpty(additionalMessage) ? ex.ToString() : $"{additionalMessage}\n{ex}", LogLevel.Error);

    private void Append(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + Environment.NewLine);
        lock (_lock)
        {
            for (var attempt = 1; attempt <= WriteAttempts; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(_directory);
                    // Shared: the app and a `--sync` run from Task Scheduler append to the same file.
                    using var stream = new FileStream(CurrentFilePath, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);
                    stream.Write(bytes);
                    return;
                }
                catch (IOException) when (attempt < WriteAttempts)
                {
                    Thread.Sleep(20 * attempt);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return;
                }
            }
        }
    }
}
