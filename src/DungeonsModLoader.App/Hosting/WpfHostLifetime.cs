using Microsoft.Extensions.Hosting;

namespace DungeonsModLoader.App.Hosting;

/// <summary>
/// Host lifetime for a WPF process. The WPF application owns start-up and shutdown, so the host neither waits for
/// a start signal nor registers console/ProcessExit handlers (the default ConsoleLifetime would, and could hold
/// process exit for the whole shutdown timeout).
/// </summary>
internal sealed class WpfHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
