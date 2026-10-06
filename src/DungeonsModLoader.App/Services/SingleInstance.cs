using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DungeonsModLoader.Core;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.App.Services;

/// <summary>
/// One running copy per data folder. A second launch (typically the browser opening an <c>nxm://</c> link)
/// forwards its arguments to the first copy over a named pipe and exits.
/// </summary>
public static class SingleInstance
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    public static string MutexNameFor(string dataRoot) => @"Local\DungeonsModLoader-" + Hash(dataRoot);

    public static string PipeNameFor(string dataRoot) => "DungeonsModLoader-" + Hash(dataRoot);

    /// <summary>
    /// Tries to become the primary instance. Returns the mutex to hold for the process lifetime, or null when
    /// another instance already runs; <paramref name="forwarded"/> then tells whether it received the arguments.
    /// </summary>
    public static Mutex? TryAcquire(string dataRoot, IReadOnlyList<string> args, out bool forwarded)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexNameFor(dataRoot), out var createdNew);
        if (createdNew)
        {
            forwarded = false;
            return mutex;
        }

        mutex.Dispose();
        forwarded = TryForward(PipeNameFor(dataRoot), args);
        return null;
    }

    private static bool TryForward(string pipeName, IReadOnlyList<string> args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect((int)ConnectTimeout.TotalMilliseconds);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(args));
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Hash(string value)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
    }
}

/// <summary>Receives the arguments forwarded by later launches (see <see cref="SingleInstance"/>).</summary>
public interface ISingleInstanceServer : IDisposable
{
    /// <summary>Starts listening; <paramref name="onArguments"/> runs for every forwarded launch (on a thread-pool thread).</summary>
    void Start(Func<string[], Task> onArguments);
}

public sealed class SingleInstanceServer : ISingleInstanceServer
{
    private readonly string _pipeName;
    private readonly ILogger<SingleInstanceServer> _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loop;

    public SingleInstanceServer(AppPaths paths, ILogger<SingleInstanceServer> logger)
    {
        _pipeName = SingleInstance.PipeNameFor(paths.Root);
        _logger = logger;
    }

    public void Start(Func<string[], Task> onArguments)
    {
        ArgumentNullException.ThrowIfNull(onArguments);
        if (_loop is not null)
        {
            return;
        }

        _loop = Task.Run(() => ListenAsync(onArguments, _lifetime.Token));
        _logger.LogDebug("Listening for forwarded launches on pipe {Pipe}", _pipeName);
    }

    private async Task ListenAsync(Func<string[], Task> onArguments, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                string[]? args;
                try
                {
                    args = JsonSerializer.Deserialize<string[]>(line);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Ignoring a malformed forwarded launch");
                    continue;
                }

                if (args is { Length: > 0 })
                {
                    _logger.LogInformation("Received {Count} forwarded argument(s) from another launch", args.Length);
                    await onArguments(args).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The single-instance pipe failed; retrying");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A forwarded launch could not be handled");
            }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
