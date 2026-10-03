namespace DungeonsModLoader.Core.Settings;

/// <summary>Loads and atomically saves <see cref="AppSettings"/>.</summary>
public interface ISettingsStore
{
    /// <summary>The in-memory settings. Defaults until <see cref="LoadAsync"/> has run; never null.</summary>
    AppSettings Current { get; }

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised after a successful save (on the saving thread).</summary>
    event EventHandler? Saved;
}
