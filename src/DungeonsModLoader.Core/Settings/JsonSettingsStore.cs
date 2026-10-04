using System.Text.Json;
using DungeonsModLoader.Core.Storage;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Core.Settings;

/// <summary>
/// <see cref="ISettingsStore"/> backed by <c>settings.json</c> in the app data folder. Unparseable JSON is set
/// aside (<c>settings.json.corrupt-&lt;timestamp&gt;</c>) and defaults are used; a file that merely cannot be read right
/// now (locked, access denied) is retried briefly and then reported, so a valid settings file is never discarded.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private const int ReadAttempts = 4;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(250);

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
            AppSettings? loaded;
            try
            {
                loaded = await ReadWithRetryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // A corrupt settings file must not brick the app: start from defaults and keep the bad file next to
                // it for diagnosis.
                _logger.LogWarning(ex, "Settings at {Path} are not valid JSON; starting with defaults", _paths.SettingsFile);
                TryQuarantine(_paths.SettingsFile);
                loaded = null;
            }

            Current = loaded ?? new AppSettings();
            _logger.LogDebug("Settings loaded from {Path} (first run completed: {FirstRun})", _paths.SettingsFile, Current.FirstRunCompleted);
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

    private async Task<AppSettings?> ReadWithRetryAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await AtomicJsonFile.ReadAsync<AppSettings>(_paths.SettingsFile, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadAttempts)
                {
                    _logger.LogError(ex, "Settings at {Path} could not be read after {Attempts} attempts", _paths.SettingsFile, attempt);
                    throw;
                }

                _logger.LogDebug(ex, "Settings at {Path} could not be read (attempt {Attempt}); retrying", _paths.SettingsFile, attempt);
                await Task.Delay(ReadRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
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
