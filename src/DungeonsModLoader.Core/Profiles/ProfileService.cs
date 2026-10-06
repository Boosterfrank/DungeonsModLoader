using System.Collections.Immutable;
using System.Text.Json;
using DungeonsModLoader.Core.Install;
using DungeonsModLoader.Core.Mods;
using DungeonsModLoader.Core.Settings;
using DungeonsModLoader.Core.Storage;
using Microsoft.Extensions.Logging;

namespace DungeonsModLoader.Core.Profiles;

/// <inheritdoc cref="IProfileService"/>
public sealed class ProfileService : IProfileService, IDisposable
{
    public const string DefaultProfileName = "Default";
    private const int MaxNameLength = 60;

    private readonly AppPaths _paths;
    private readonly IModService _mods;
    private readonly ISettingsStore _settings;
    private readonly ILogger<ProfileService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Stored> _profiles = new(StringComparer.OrdinalIgnoreCase);

    private ImmutableArray<ProfileInfo> _snapshot = ImmutableArray<ProfileInfo>.Empty;
    private string _active = DefaultProfileName;

    /// <summary>True while a switch is applying moves: the resulting mod-store events must not be mirrored half-way.</summary>
    private bool _mirrorSuspended;
    private bool _disposed;

    public ProfileService(AppPaths paths, IModService mods, ISettingsStore settings, ILogger<ProfileService> logger)
    {
        _paths = paths;
        _mods = mods;
        _settings = settings;
        _logger = logger;
        _mods.Changed += OnModsChanged;
    }

    public IReadOnlyList<ProfileInfo> Profiles => _snapshot;

    public string ActiveProfileName => _active;

    public bool IsInitialized { get; private set; }

    public event EventHandler? Changed;

    public ProfileInfo? Find(string name)
    {
        foreach (var profile in _snapshot)
        {
            if (string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }

        return null;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _profiles.Clear();
            Directory.CreateDirectory(_paths.ProfilesDirectory);

            foreach (var file in Directory.EnumerateFiles(_paths.ProfilesDirectory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await LoadFileAsync(file, cancellationToken).ConfigureAwait(false);
            }

            if (!_profiles.ContainsKey(DefaultProfileName))
            {
                _logger.LogInformation("No profiles found; creating '{Name}' from the mods enabled right now", DefaultProfileName);
                await StoreNewAsync(new Profile { Name = DefaultProfileName, Mods = CurrentEnabledMods() }, cancellationToken).ConfigureAwait(false);
            }

            var wanted = _settings.Current.ActiveProfile;
            if (string.IsNullOrWhiteSpace(wanted) || !_profiles.ContainsKey(wanted))
            {
                if (!string.IsNullOrWhiteSpace(wanted))
                {
                    _logger.LogWarning("The active profile '{Name}' from the settings does not exist; falling back to '{Default}'", wanted, DefaultProfileName);
                }

                wanted = DefaultProfileName;
            }

            _active = _profiles[wanted].Profile.Name;
            if (!string.Equals(_settings.Current.ActiveProfile, _active, StringComparison.Ordinal))
            {
                _settings.Current.ActiveProfile = _active;
                await TrySaveSettingsAsync(cancellationToken).ConfigureAwait(false);
            }

            // Disk wins: whatever is enabled right now is what the active profile means.
            await MirrorActiveCoreAsync(cancellationToken).ConfigureAwait(false);
            IsInitialized = true;
            RefreshSnapshot();
            _logger.LogInformation("Profiles ready: {Count} profile(s), active '{Active}'", _profiles.Count, _active);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    private async Task LoadFileAsync(string file, CancellationToken cancellationToken)
    {
        Profile? profile;
        try
        {
            profile = await AtomicJsonFile.ReadAsync<Profile>(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Profile file {File} is not valid JSON; setting it aside", file);
            Quarantine(file);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Profile file {File} could not be read; skipping it", file);
            return;
        }

        if (profile is null || string.IsNullOrWhiteSpace(profile.Name))
        {
            _logger.LogWarning("Profile file {File} has no name; skipping it", file);
            return;
        }

        profile.Name = profile.Name.Trim();
        profile.Mods ??= new List<ProfileMod>();
        profile.Mods.RemoveAll(m => m is null || string.IsNullOrWhiteSpace(m.FolderName));
        foreach (var mod in profile.Mods)
        {
            mod.DisplayName = string.IsNullOrWhiteSpace(mod.DisplayName) ? mod.FolderName : mod.DisplayName;
        }

        if (_profiles.ContainsKey(profile.Name))
        {
            _logger.LogWarning("Profile file {File} repeats the name '{Name}'; skipping it", file, profile.Name);
            return;
        }

        _profiles[profile.Name] = new Stored(profile, file);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Create / duplicate / rename / delete
    // ---------------------------------------------------------------------------------------------------------------

    public async Task<ProfileInfo> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        var trimmed = ValidateName(name);
        Profile profile;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNameFree(trimmed);
            profile = new Profile { Name = trimmed, Mods = CurrentEnabledMods() };
            await StoreNewAsync(profile, cancellationToken).ConfigureAwait(false);
            RefreshSnapshot();
            _logger.LogInformation("Created profile '{Name}' with {Count} mod(s)", trimmed, profile.Mods.Count);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
        return Find(profile.Name)!;
    }

    public async Task<ProfileInfo> DuplicateAsync(string sourceName, string newName, CancellationToken cancellationToken = default)
    {
        var trimmed = ValidateName(newName);
        Profile profile;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = Require(sourceName);
            EnsureNameFree(trimmed);
            profile = new Profile
            {
                Name = trimmed,
                Mods = source.Profile.Mods.Select(Clone).ToList(),
            };
            await StoreNewAsync(profile, cancellationToken).ConfigureAwait(false);
            RefreshSnapshot();
            _logger.LogInformation("Duplicated profile '{Source}' as '{Name}'", source.Profile.Name, trimmed);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
        return Find(profile.Name)!;
    }

    public async Task RenameAsync(string name, string newName, CancellationToken cancellationToken = default)
    {
        var trimmed = ValidateName(newName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = Require(name);
            if (string.Equals(stored.Profile.Name, trimmed, StringComparison.Ordinal))
            {
                return;
            }

            var sameProfile = string.Equals(stored.Profile.Name, trimmed, StringComparison.OrdinalIgnoreCase);
            if (!sameProfile)
            {
                EnsureNameFree(trimmed);
            }

            var previousName = stored.Profile.Name;
            var previousPath = stored.FilePath;
            var wasActive = string.Equals(_active, previousName, StringComparison.Ordinal);

            stored.Profile.Name = trimmed;
            stored.Profile.UpdatedAt = DateTimeOffset.UtcNow;
            var newPath = sameProfile ? previousPath : UniqueFilePath(trimmed);
            try
            {
                await AtomicJsonFile.WriteAsync(newPath, stored.Profile, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stored.Profile.Name = previousName;
                throw new ProfileException("The profile could not be saved. Make sure the app data folder is writable and try again.", ex);
            }

            if (!string.Equals(newPath, previousPath, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(previousPath);
            }

            _profiles.Remove(previousName);
            stored.FilePath = newPath;
            _profiles[trimmed] = stored;

            if (wasActive)
            {
                _active = trimmed;
                _settings.Current.ActiveProfile = trimmed;
                await TrySaveSettingsAsync(cancellationToken).ConfigureAwait(false);
            }

            RefreshSnapshot();
            _logger.LogInformation("Renamed profile '{Old}' to '{New}'", previousName, trimmed);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = Require(name);
            if (string.Equals(stored.Profile.Name, _active, StringComparison.OrdinalIgnoreCase))
            {
                throw new ProfileException($"'{stored.Profile.Name}' is the active profile. Switch to another profile before deleting it.");
            }

            if (_profiles.Count <= 1)
            {
                throw new ProfileException("The last profile cannot be deleted.");
            }

            try
            {
                if (File.Exists(stored.FilePath))
                {
                    File.Delete(stored.FilePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ProfileException("The profile file could not be deleted. Close any program using it and try again.", ex);
            }

            _profiles.Remove(stored.Profile.Name);
            RefreshSnapshot();
            _logger.LogInformation("Deleted profile '{Name}'", stored.Profile.Name);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Switch
    // ---------------------------------------------------------------------------------------------------------------

    public async Task SwitchAsync(string name, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = Require(name);
            var desired = Resolve(stored.Profile.Mods).Select(entry => entry.Id).ToHashSet();
            _logger.LogInformation("Switching to profile '{Name}' ({Count} of {Listed} listed mods installed)", stored.Profile.Name, desired.Count, stored.Profile.Mods.Count);

            _mirrorSuspended = true;
            try
            {
                await _mods.ApplyEnabledStatesAsync(desired, progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _mirrorSuspended = false;
            }

            if (!string.Equals(_active, stored.Profile.Name, StringComparison.Ordinal))
            {
                _active = stored.Profile.Name;
                _settings.Current.ActiveProfile = _active;
                await TrySaveSettingsAsync(cancellationToken).ConfigureAwait(false);
            }

            // Record what actually happened (missing mods, imported references now resolved to ids).
            await MirrorActiveCoreAsync(cancellationToken).ConfigureAwait(false);
            RefreshSnapshot();
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Export / import
    // ---------------------------------------------------------------------------------------------------------------

    public async Task ExportAsync(string name, string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ProfileExportDocument document;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = Require(name);
            document = new ProfileExportDocument
            {
                Name = stored.Profile.Name,
                ExportedBy = $"{AppInfo.DisplayName} {AppInfo.Version}",
                Mods = stored.Profile.Mods.Select(m => m.WithoutId()).ToList(),
            };
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await AtomicJsonFile.WriteAsync(filePath, document, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Exported profile '{Name}' ({Count} mods) to {File}", document.Name, document.Mods.Count, filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProfileException($"The profile could not be written to {filePath}.", ex);
        }
    }

    public async Task<ProfileImportResult> ImportAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ProfileExportDocument? document;
        try
        {
            document = await AtomicJsonFile.ReadAsync<ProfileExportDocument>(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new ProfileException("This file is not a profile export. Pick a .json file that was exported from the Profiles page.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProfileException("The file could not be read. Make sure it exists and is not in use.", ex);
        }

        if (document is null || !string.Equals(document.Format, ProfileExportDocument.FormatName, StringComparison.Ordinal))
        {
            throw new ProfileException("This file is not a profile export. Pick a .json file that was exported from the Profiles page.");
        }

        var baseName = string.IsNullOrWhiteSpace(document.Name) ? Path.GetFileNameWithoutExtension(filePath) : document.Name.Trim();
        if (baseName.Length > MaxNameLength)
        {
            baseName = baseName[..MaxNameLength].TrimEnd();
        }

        if (baseName.Length == 0)
        {
            baseName = "Imported profile";
        }

        var mods = (document.Mods ?? new List<ProfileMod>())
            .Where(m => m is not null && !string.IsNullOrWhiteSpace(m.FolderName))
            .Select(m => m.WithoutId())
            .ToList();
        foreach (var mod in mods)
        {
            mod.DisplayName = string.IsNullOrWhiteSpace(mod.DisplayName) ? mod.FolderName : mod.DisplayName;
        }

        Profile profile;
        List<ProfileMod> missing;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var name = FolderNameSanitizer.MakeUnique(baseName, candidate => _profiles.ContainsKey(candidate));
            profile = new Profile { Name = name, Mods = mods };
            await StoreNewAsync(profile, cancellationToken).ConfigureAwait(false);
            missing = mods.Where(m => ResolveOne(m) is null).ToList();
            RefreshSnapshot();
            _logger.LogInformation("Imported profile '{Name}' from {File}: {Count} mods, {Missing} not installed", name, filePath, mods.Count, missing.Count);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
        return new ProfileImportResult(Find(profile.Name)!, missing);
    }

    public IReadOnlyList<ProfileMod> FindMissingMods(string name)
    {
        var profile = Find(name);
        if (profile is null)
        {
            return Array.Empty<ProfileMod>();
        }

        return profile.Mods.Where(m => ResolveOne(m) is null).ToList();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Mirroring the active profile
    // ---------------------------------------------------------------------------------------------------------------

    private void OnModsChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _ = MirrorAsync();
    }

    private async Task MirrorAsync()
    {
        var raise = false;
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!IsInitialized || _mirrorSuspended)
                {
                    return;
                }

                if (await MirrorActiveCoreAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    RefreshSnapshot();
                    raise = true;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The active profile could not be updated from the mod list");
            return;
        }

        if (raise)
        {
            RaiseChanged();
        }
    }

    /// <summary>Writes the currently enabled mods into the active profile; true when it changed. Runs under the gate.</summary>
    private async Task<bool> MirrorActiveCoreAsync(CancellationToken cancellationToken)
    {
        if (!_mods.IsInitialized || !_profiles.TryGetValue(_active, out var stored))
        {
            return false;
        }

        var current = CurrentEnabledMods();
        if (SameMods(stored.Profile.Mods, current))
        {
            return false;
        }

        var previousMods = stored.Profile.Mods;
        var previousUpdated = stored.Profile.UpdatedAt;
        stored.Profile.Mods = current;
        stored.Profile.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await AtomicJsonFile.WriteAsync(stored.FilePath, stored.Profile, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Profile '{Name}' now lists {Count} enabled mod(s)", stored.Profile.Name, current.Count);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stored.Profile.Mods = previousMods;
            stored.Profile.UpdatedAt = previousUpdated;
            _logger.LogWarning(ex, "Profile '{Name}' could not be saved", stored.Profile.Name);
            return false;
        }
    }

    private List<ProfileMod> CurrentEnabledMods() =>
        _mods.Mods
            .Where(m => m.IsEnabled)
            .Select(m => ProfileMod.From(m.Entry))
            .OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(m => m.FolderName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool SameMods(List<ProfileMod> a, List<ProfileMod> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        static string Key(ProfileMod m) =>
            string.Join("|", m.ModId?.ToString("N"), m.FolderName.ToLowerInvariant(), m.DisplayName, m.Source, m.NexusModId, m.NexusFileId, m.Version);

        var left = a.Select(Key).OrderBy(k => k, StringComparer.Ordinal);
        var right = b.Select(Key).OrderBy(k => k, StringComparer.Ordinal);
        return left.SequenceEqual(right, StringComparer.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Resolving profile entries to installed mods
    // ---------------------------------------------------------------------------------------------------------------

    private List<ModEntry> Resolve(IEnumerable<ProfileMod> mods)
    {
        var result = new List<ModEntry>();
        foreach (var mod in mods)
        {
            var match = ResolveOne(mod);
            if (match is not null && !result.Contains(match))
            {
                result.Add(match);
            }
        }

        return result;
    }

    /// <summary>Installed mod a profile entry refers to: by id, then Nexus mod id, then folder name, then display name.</summary>
    private ModEntry? ResolveOne(ProfileMod mod)
    {
        var installed = _mods.Mods;
        if (mod.ModId is { } id)
        {
            var byId = installed.FirstOrDefault(m => m.Entry.Id == id);
            if (byId is not null)
            {
                return byId.Entry;
            }
        }

        if (mod.NexusModId is { } nexusId)
        {
            var byNexus = installed.FirstOrDefault(m => m.Entry.NexusModId == nexusId);
            if (byNexus is not null)
            {
                return byNexus.Entry;
            }
        }

        var byFolder = installed.FirstOrDefault(m => string.Equals(m.Entry.FolderName, mod.FolderName, StringComparison.OrdinalIgnoreCase));
        if (byFolder is not null)
        {
            return byFolder.Entry;
        }

        return installed.FirstOrDefault(m => string.Equals(m.Entry.DisplayName, mod.DisplayName, StringComparison.OrdinalIgnoreCase))?.Entry;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    private static string ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ProfileException("The profile name cannot be empty.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new ProfileException($"The profile name is too long (at most {MaxNameLength} characters).");
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new ProfileException("The profile name contains characters that cannot be used.");
        }

        return trimmed;
    }

    private void EnsureNameFree(string name)
    {
        if (_profiles.TryGetValue(name, out var taken))
        {
            throw new ProfileException($"A profile named '{taken.Profile.Name}' already exists.");
        }
    }

    private Stored Require(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !_profiles.TryGetValue(name.Trim(), out var stored))
        {
            throw new ProfileException($"There is no profile named '{name}'.");
        }

        return stored;
    }

    /// <summary>Writes a new profile to its own file and registers it. Runs under the gate.</summary>
    private async Task StoreNewAsync(Profile profile, CancellationToken cancellationToken)
    {
        var path = UniqueFilePath(profile.Name);
        try
        {
            await AtomicJsonFile.WriteAsync(path, profile, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProfileException("The profile could not be saved. Make sure the app data folder is writable and try again.", ex);
        }

        _profiles[profile.Name] = new Stored(profile, path);
    }

    /// <summary>A free <c>profiles\&lt;sanitized name&gt;.json</c> path.</summary>
    private string UniqueFilePath(string profileName)
    {
        var baseName = FolderNameSanitizer.Sanitize(profileName, "Profile");
        var unique = FolderNameSanitizer.MakeUnique(baseName, candidate => File.Exists(Path.Combine(_paths.ProfilesDirectory, candidate + ".json")));
        return Path.Combine(_paths.ProfilesDirectory, unique + ".json");
    }

    private static ProfileMod Clone(ProfileMod mod) => new()
    {
        ModId = mod.ModId,
        FolderName = mod.FolderName,
        DisplayName = mod.DisplayName,
        Source = mod.Source,
        NexusModId = mod.NexusModId,
        NexusFileId = mod.NexusFileId,
        Version = mod.Version,
    };

    private async Task TrySaveSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The active profile could not be saved to the settings");
        }
    }

    private void RefreshSnapshot()
    {
        _snapshot = _profiles.Values
            .OrderBy(s => string.Equals(s.Profile.Name, DefaultProfileName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(s => s.Profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new ProfileInfo(
                s.Profile.Name,
                string.Equals(s.Profile.Name, _active, StringComparison.Ordinal),
                s.Profile.CreatedAt,
                s.Profile.UpdatedAt,
                s.Profile.Mods.Select(Clone).ToList()))
            .ToImmutableArray();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A Changed handler of the profile service threw");
        }
    }

    private void Quarantine(string file)
    {
        try
        {
            File.Move(file, file + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not quarantine {File}", file);
        }
    }

    private void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete {File}", file);
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

    private sealed class Stored
    {
        public Stored(Profile profile, string filePath)
        {
            Profile = profile;
            FilePath = filePath;
        }

        public Profile Profile { get; }

        public string FilePath { get; set; }
    }
}
