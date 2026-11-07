using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pilewise.Services;

public sealed class SingleInstance : IDisposable
{
    public const string ShowMessage = "show";

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listener;
    private bool _disposed;

    private SingleInstance(Mutex mutex, string name)
    {
        _mutex = mutex;
        _pipeName = PipeName(name);
        _listener = Task.Run(ListenAsync);
    }

    /// <summary>Raised (on a background thread) when another start asked to show the window.</summary>
    public event Action? ShowRequested;

    public static string NameFor(string user)
    {
        var safe = new string(user.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(32).ToArray());
        return "Pilewise-" + (safe.Length > 0 ? safe : "user");
    }

    public static string CurrentUserName => NameFor(Environment.UserName);

    public static string PipeName(string name) => name + "-pipe";

    /// <summary>
    /// Becomes the running instance, or returns null when another one already is. Must be disposed
    /// on the thread that acquired it (the mutex is thread-owned).
    /// </summary>
    public static SingleInstance? TryAcquire(string name)
    {
        var mutex = new Mutex(false, name + "-lock");
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner crashed; the mutex is ours now.
            owned = true;
        }

        if (owned) return new SingleInstance(mutex, name);
        mutex.Dispose();
        return null;
    }

    public static bool SignalExisting(string name, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(name), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            using var writer = new StreamWriter(client);
            writer.WriteLine(ShowMessage);
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line?.Trim() == ShowMessage) ShowRequested?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A client hung up mid-message, or the pipe is briefly busy: keep listening.
                try
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stop.Cancel();
        try
        {
            _listener.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The listener only ends by cancellation; nothing to report on shutdown.
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released from another thread than the owner; the OS frees it on exit anyway.
        }

        _mutex.Dispose();
        _stop.Dispose();
    }
}
