using DungeonsModLoader.Core.Storage;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Core.Settings;

/// <summary><see cref="ISettingsStore"/> backed by <c>settings.json</c> in the app data folder.</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly AppPaths _paths;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsStore(AppPaths paths, ILogger<JsonSettingsStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public AppSettings Current { get; private set; } = new();

    public event EventHandler? Saved;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await AtomicJsonFile.ReadAsync<AppSettings>(_paths.SettingsFile, cancellationToken).ConfigureAwait(false);
            Current = loaded ?? new AppSettings();
            _logger.LogDebug("Settings loaded from {Path} (first run completed: {FirstRun})", _paths.SettingsFile, Current.FirstRunCompleted);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable settings file must not brick the app: start from defaults and keep the bad
            // file next to it for diagnosis.
            _logger.LogWarning(ex, "Settings could not be read from {Path}; starting with defaults", _paths.SettingsFile);
            TryQuarantine(_paths.SettingsFile);
            Current = new AppSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicJsonFile.WriteAsync(_paths.SettingsFile, Current, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Settings saved to {Path}", _paths.SettingsFile);
        }
        finally
        {
            _gate.Release();
        }

        Saved?.Invoke(this, EventArgs.Empty);
    }

    private void TryQuarantine(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Move(path, path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"), overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not quarantine {Path}", path);
        }
    }
}
