using DungeonsModLoader.Core.Mods;

namespace DungeonsModLoader.Core.Install;

/// <summary>
/// The install pipeline for local sources (archives, loose files, folders); Nexus downloads feed the same pipeline
/// in milestone 5. <see cref="InspectAsync"/> extracts and analyses; <see cref="InstallAsync"/> copies the chosen
/// files into a new mod folder (enabled) and records the mod in the manifest.
/// </summary>
public interface IModInstaller
{
    /// <summary>
    /// Extracts the source to a staging folder under the app's temp directory and finds its mod file sets.
    /// Throws <see cref="InstallPackageException"/> for an unreadable/unsupported archive. The returned plan
    /// owns the staging folder; dispose it when done.
    /// </summary>
    Task<InstallPlan> InspectAsync(InstallSource source, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs the selected file sets as a new local mod: flattened into the mod folder when the package contains
    /// only pak sets, otherwise with the package's folder structure preserved. The new mod is enabled and recorded
    /// in the manifest. Throws <see cref="InstallConflictException"/> when the folder exists and the request says
    /// <see cref="ConflictResolution.Ask"/>, <see cref="InstallPackageException"/> when nothing valid is selected,
    /// <see cref="ModAccessDeniedException"/> when Windows refuses access.
    /// </summary>
    Task<ModEntry> InstallAsync(InstallRequest request, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default);
}
