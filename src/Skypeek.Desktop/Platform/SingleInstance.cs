using System.IO.Pipes;

namespace Skypeek.Desktop.Platform;

/// <summary>
/// One instance per user and vault. A second launch asks the running one to show itself. A separate vault
/// (SKYPEEK_HOME) gets its own instance so a test vault can run next to the real one. Named mutexes and pipes work on
/// every platform (.NET uses shared-memory files and Unix domain sockets outside Windows).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string Name = BuildName();

    private static string BuildName()
    {
        var name = $"Skypeek.{Environment.UserName}";
        if (Environment.GetEnvironmentVariable("SKYPEEK_HOME") is { Length: > 0 } home)
        {
            var path = Path.GetFullPath(home);
            if (OperatingSystem.IsWindows())
                path = path.ToLowerInvariant();
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path));
            name += "." + Convert.ToHexString(hash)[..12];
        }
        return name;
    }

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(true, $@"Local\{Name}", out var created);
        if (created)
            return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    public static void SignalExisting(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            using var writer = new StreamWriter(client);
            writer.Write(message);
        }
        catch
        {
            // The running instance may be shutting down.
        }
    }

    public void Listen(Action<string> onMessage)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var reader = new StreamReader(server);
                    onMessage(await reader.ReadToEndAsync(_cts.Token));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    await Task.Delay(500);
                }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // released on another thread
        }
        _mutex.Dispose();
    }
}
