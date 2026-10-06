using System.Collections.Concurrent;
using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Core.Settings;
using DungeonsModLoader.Nexus.Api;
using DungeonsModLoader.Nexus.Auth;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Nexus.Updates;

/// <inheritdoc cref="IModUpdateChecker"/>
public sealed class ModUpdateChecker : IModUpdateChecker, IDisposable
{
    /// <summary>Startup checks are skipped when the last one is younger than this (spec: once an hour).</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(1);

    /// <summary><c>updated.json?period=1m</c> covers this much; older installs fall back to the mod's own timestamp.</summary>
    private static readonly TimeSpan UpdatedWindow = TimeSpan.FromDays(28);

    private readonly INexusApiClient _client;
    private readonly INexusSession _session;
    private readonly IModService _mods;
    private readonly ISettingsStore _settings;
    private readonly ILogger<ModUpdateChecker> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, NexusModUpdate> _updates = new();
    private bool _disposed;

    public ModUpdateChecker(INexusApiClient client, INexusSession session, IModService mods, ISettingsStore settings, ILogger<ModUpdateChecker> logger)
    {
        _client = client;
        _session = session;
        _mods = mods;
        _settings = settings;
        _logger = logger;
        LastCheckUtc = settings.Current.LastModUpdateCheckUtc;
        _mods.Changed += OnModsChanged;
    }

    public IReadOnlyDictionary<Guid, NexusModUpdate> Updates => _updates;

    public DateTimeOffset? LastCheckUtc { get; private set; }

    public bool IsChecking { get; private set; }

    public string? LastError { get; private set; }

    public event EventHandler? Changed;

    public async Task<IReadOnlyList<NexusModUpdate>> CheckAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!_session.HasApiKey)
        {
            _logger.LogDebug("Update check skipped: no Nexus API key");
            return Array.Empty<NexusModUpdate>();
        }

        if (!force && LastCheckUtc is { } last && last > DateTimeOffset.UtcNow - MinimumInterval)
        {
            _logger.LogDebug("Update check skipped: last check at {Last:u} is less than an hour old", last);
            return _updates.Values.ToList();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetChecking(true, null);
            var targets = NexusMods();
            if (targets.Count == 0)
            {
                _logger.LogInformation("Update check: no Nexus mods installed");
                await RecordCheckTimeAsync(cancellationToken).ConfigureAwait(false);
                return Array.Empty<NexusModUpdate>();
            }

            IReadOnlyDictionary<long, NexusUpdatedMod> recentlyUpdated;
            try
            {
                recentlyUpdated = (await _client.GetUpdatedAsync(NexusUpdatePeriod.Month, refresh: force, cancellationToken).ConfigureAwait(false))
                    .GroupBy(u => u.ModId)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(u => u.LatestFileUpdate).First());
            }
            catch (NexusException ex)
            {
                _logger.LogWarning("Update check could not ask Nexus for recent changes: {Message}", ex.Message);
                SetChecking(false, ex.Message);
                throw;
            }

            var found = new List<NexusModUpdate>();
            foreach (var info in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var update = await CheckOneAsync(info, recentlyUpdated, force, cancellationToken).ConfigureAwait(false);
                    if (update is not null)
                    {
                        found.Add(update);
                    }
                }
                catch (NexusRateLimitException ex)
                {
                    _logger.LogWarning("Update check stopped early: {Message}", ex.Message);
                    SetChecking(false, ex.Message);
                    throw;
                }
                catch (NexusException ex)
                {
                    _logger.LogWarning("Update check for '{Mod}' failed: {Message}", info.Entry.DisplayName, ex.Message);
                }
            }

            await RecordCheckTimeAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Update check finished: {Updates} update(s) for {Count} Nexus mod(s)", found.Count, targets.Count);
            return found;
        }
        finally
        {
            SetChecking(false, LastError);
            _gate.Release();
            RaiseChanged();
        }
    }

    public async Task<NexusModUpdate?> CheckModAsync(Guid modId, CancellationToken cancellationToken = default)
    {
        if (!_session.HasApiKey)
        {
            throw new NexusAuthException(System.Net.HttpStatusCode.Unauthorized, "Add your Nexus Mods API key in Settings to check for updates.");
        }

        var info = _mods.Find(modId) ?? throw new ModNotFoundException($"No installed mod has the id {modId}.");
        if (info.Entry.Source != ModSource.Nexus || info.Entry.NexusModId is null)
        {
            throw new NexusException($"'{info.Entry.DisplayName}' did not come from Nexus Mods, so there is nothing to check.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetChecking(true, null);
            var files = await _client.GetFilesAsync(info.Entry.NexusModId.Value, refresh: true, cancellationToken).ConfigureAwait(false);
            return Record(info, files);
        }
        catch (NexusException ex)
        {
            SetChecking(false, ex.Message);
            throw;
        }
        finally
        {
            SetChecking(false, LastError);
            _gate.Release();
            RaiseChanged();
        }
    }

    public void Clear(Guid modId)
    {
        if (_updates.TryRemove(modId, out _))
        {
            RaiseChanged();
        }
    }

    private async Task<NexusModUpdate?> CheckOneAsync(ModInfo info, IReadOnlyDictionary<long, NexusUpdatedMod> recentlyUpdated, bool force, CancellationToken cancellationToken)
    {
        var entry = info.Entry;
        var nexusModId = entry.NexusModId!.Value;

        bool worthFetchingFiles;
        if (recentlyUpdated.TryGetValue(nexusModId, out var recent))
        {
            worthFetchingFiles = recent.LatestFileUpdate > entry.UpdatedAt.AddMinutes(-1) || force;
        }
        else if (entry.UpdatedAt < DateTimeOffset.UtcNow - UpdatedWindow)
        {
            // Installed before the window updated.json covers: one cheap request tells whether anything changed since.
            var mod = await _client.GetModAsync(nexusModId, refresh: false, cancellationToken).ConfigureAwait(false);
            worthFetchingFiles = mod.UpdatedAt > entry.UpdatedAt.AddMinutes(-1) || force;
        }
        else
        {
            worthFetchingFiles = force;
        }

        if (!worthFetchingFiles)
        {
            if (_updates.TryRemove(entry.Id, out _))
            {
                _logger.LogDebug("'{Mod}' no longer has a pending update", entry.DisplayName);
            }

            return null;
        }

        var files = await _client.GetFilesAsync(nexusModId, refresh: force, cancellationToken).ConfigureAwait(false);
        return Record(info, files);
    }

    private NexusModUpdate? Record(ModInfo info, NexusFileList files)
    {
        var entry = info.Entry;
        var newer = UpdateResolver.FindNewerFile(entry.NexusFileId, files);
        if (newer is null || newer.FileId == entry.NexusFileId)
        {
            _updates.TryRemove(entry.Id, out _);
            return null;
        }

        var update = new NexusModUpdate(entry.Id, entry.NexusModId!.Value, entry.NexusFileId, entry.Version, newer, DateTimeOffset.UtcNow);
        _updates[entry.Id] = update;
        _logger.LogInformation("Update available for '{Mod}': file {Old} -> {New} ({Version})", entry.DisplayName, entry.NexusFileId, newer.FileId, update.NewVersion);
        return update;
    }

    private List<ModInfo> NexusMods() =>
        _mods.Mods.Where(m => m.Entry.Source == ModSource.Nexus && m.Entry.NexusModId is not null).ToList();

    private async Task RecordCheckTimeAsync(CancellationToken cancellationToken)
    {
        LastCheckUtc = DateTimeOffset.UtcNow;
        _settings.Current.LastModUpdateCheckUtc = LastCheckUtc;
        try
        {
            await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The update check time could not be saved");
        }
    }

    /// <summary>Drops updates for mods that were removed or already updated to the suggested file.</summary>
    private void OnModsChanged(object? sender, EventArgs e)
    {
        if (_disposed || _updates.IsEmpty)
        {
            return;
        }

        var changed = false;
        foreach (var (modId, update) in _updates.ToArray())
        {
            var info = _mods.Find(modId);
            if (info is null || info.Entry.Source != ModSource.Nexus || info.Entry.NexusFileId == update.NewFile.FileId)
            {
                changed |= _updates.TryRemove(modId, out _);
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private void SetChecking(bool checking, string? error)
    {
        IsChecking = checking;
        LastError = error;
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A Changed handler of the update checker threw");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mods.Changed -= OnModsChanged;
        _gate.Dispose();
    }
}
