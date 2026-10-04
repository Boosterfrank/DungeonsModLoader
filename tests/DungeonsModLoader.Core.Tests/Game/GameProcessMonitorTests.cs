using System.Diagnostics;
using DungeonsModLoader.Core.Game;
using Microsoft.Extensions.Logging.Abstractions;

namespace DungeonsModLoader.Core.Tests.Game;

public class GameProcessMonitorTests
{
    private static readonly string CurrentProcessName = Process.GetCurrentProcess().ProcessName;

    /// <summary>Folder of the running test host: a process "inside" this root is the test host itself.</summary>
    private static readonly string CurrentProcessRoot =
        Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName)!;

    private const string NonsenseName = "dml-no-such-process-7f3a9c";

    [Fact]
    public async Task Idle_monitor_reports_not_running()
    {
        using var monitor = Create();

        Assert.False(monitor.IsGameRunning);
        await monitor.RefreshAsync();
        Assert.False(monitor.IsGameRunning);
    }

    [Fact]
    public async Task Refresh_detects_the_current_test_process()
    {
        using var monitor = Create();
        monitor.Start(Install(CurrentProcessRoot, CurrentProcessName));

        await monitor.RefreshAsync();

        Assert.True(monitor.IsGameRunning);
    }

    [Fact]
    public async Task A_process_with_the_same_name_outside_the_install_root_does_not_count()
    {
        // The first Minecraft Dungeons ships the same executable names; only processes under the root count.
        using var monitor = Create();
        var otherRoot = Path.Combine(Path.GetTempPath(), "dml-other-root-" + Guid.NewGuid().ToString("N"));
        monitor.Start(Install(otherRoot, CurrentProcessName));

        await monitor.RefreshAsync();

        Assert.False(monitor.IsGameRunning);
    }

    [Fact]
    public async Task Refresh_with_a_nonsense_name_reports_not_running()
    {
        using var monitor = Create();
        monitor.Start(Install(CurrentProcessRoot, NonsenseName));

        await monitor.RefreshAsync();

        Assert.False(monitor.IsGameRunning);
    }

    [Fact]
    public async Task GameRunningChanged_fires_on_transition_only()
    {
        using var monitor = Create();
        var events = new List<bool>();
        var becameRunning = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.GameRunningChanged += (_, running) =>
        {
            lock (events)
            {
                events.Add(running);
            }

            if (running)
            {
                becameRunning.TrySetResult(true);
            }
        };

        monitor.Start(Install(CurrentProcessRoot, NonsenseName));
        await monitor.RefreshAsync();
        await monitor.RefreshAsync();
        Assert.False(monitor.IsGameRunning);
        lock (events)
        {
            Assert.Empty(events); // false -> false is not a change
        }

        monitor.Start(Install(CurrentProcessRoot, CurrentProcessName)); // restart replaces the previous loop
        await monitor.RefreshAsync();
        await becameRunning.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(monitor.IsGameRunning);
        lock (events)
        {
            Assert.Equal([true], events);
        }

        monitor.Stop();
        Assert.False(monitor.IsGameRunning);
        lock (events)
        {
            Assert.Equal([true, false], events);
        }
    }

    [Fact]
    public async Task Background_loop_detects_without_an_explicit_refresh()
    {
        using var monitor = new GameProcessMonitor(NullLogger<GameProcessMonitor>.Instance, TimeSpan.FromMilliseconds(50));
        var becameRunning = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.GameRunningChanged += (_, running) =>
        {
            if (running)
            {
                becameRunning.TrySetResult(true);
            }
        };

        monitor.Start(Install(CurrentProcessRoot, CurrentProcessName));

        await becameRunning.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(monitor.IsGameRunning);
    }

    [Fact]
    public async Task Stop_wins_over_a_poll_that_was_already_in_flight()
    {
        // Start a fast loop against the live test process, then stop it repeatedly while polls are in flight:
        // after Stop() the monitor must stay "not running" and never publish a late "running" result.
        using var monitor = new GameProcessMonitor(NullLogger<GameProcessMonitor>.Instance, TimeSpan.FromMilliseconds(5));
        var lateRunningEvents = 0;
        var stopped = false;
        monitor.GameRunningChanged += (_, running) =>
        {
            if (running && Volatile.Read(ref stopped))
            {
                Interlocked.Increment(ref lateRunningEvents);
            }
        };

        for (var i = 0; i < 20; i++)
        {
            Volatile.Write(ref stopped, false);
            monitor.Start(Install(CurrentProcessRoot, CurrentProcessName));
            await Task.Delay(Random.Shared.Next(0, 12));
            Volatile.Write(ref stopped, true);
            monitor.Stop();
            Assert.False(monitor.IsGameRunning);
            await Task.Delay(30);
            Assert.False(monitor.IsGameRunning);
        }

        Assert.Equal(0, lateRunningEvents);
    }

    [Fact]
    public async Task Stop_and_dispose_are_safe_to_call_repeatedly_and_refresh_after_stop_is_a_no_op()
    {
        var monitor = Create();
        monitor.Start(Install(CurrentProcessRoot, CurrentProcessName));
        await monitor.RefreshAsync();
        Assert.True(monitor.IsGameRunning);

        monitor.Stop();
        monitor.Stop();
        Assert.False(monitor.IsGameRunning);
        await monitor.RefreshAsync();
        Assert.False(monitor.IsGameRunning);

        monitor.Dispose();
        monitor.Dispose();
        Assert.Throws<ObjectDisposedException>(() => monitor.Start(Install(CurrentProcessRoot, CurrentProcessName)));
    }

    [Fact]
    public void Empty_executable_list_falls_back_to_the_known_names()
    {
        using var monitor = Create();

        monitor.Start(new GameInstallation { Root = "C:\\x", Source = GameSource.Manual, ExecutableNames = Array.Empty<string>() });

        Assert.False(monitor.IsGameRunning);
    }

    [Fact]
    public void Invalid_interval_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameProcessMonitor(NullLogger<GameProcessMonitor>.Instance, TimeSpan.Zero));
    }

    private static GameProcessMonitor Create() => new(NullLogger<GameProcessMonitor>.Instance, TimeSpan.FromSeconds(30));

    private static GameInstallation Install(string root, params string[] names)
        => new() { Root = root, Source = GameSource.Manual, ExecutableNames = names };
}
