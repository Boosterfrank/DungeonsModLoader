namespace DungeonsModLoader.Core.Install;

/// <summary>
/// Result of inspecting an install source: the extracted (staged) content, the mod file sets found in it, grouped
/// by folder, and the suggested display name. Dispose it to delete the staging folder (after install or cancel).
/// </summary>
public sealed class InstallPlan : IDisposable
{
    private readonly Action? _cleanup;
    private bool _disposed;

    public InstallPlan(
        InstallSource source,
        string stagingRoot,
        IReadOnlyList<InstallCandidate> candidates,
        IReadOnlyList<string> extraFiles,
        IReadOnlyList<string> documentationFiles,
        string suggestedName,
        string? suggestedVersion,
        IReadOnlyList<string> warnings,
        Action? cleanup)
    {
        Source = source;
        StagingRoot = stagingRoot;
        Candidates = candidates;
        ExtraFiles = extraFiles;
        DocumentationFiles = documentationFiles;
        SuggestedName = suggestedName;
        SuggestedVersion = suggestedVersion;
        Warnings = warnings;
        _cleanup = cleanup;
    }

    public InstallSource Source { get; }

    /// <summary>Folder holding the package content (wrapper folders already stripped). Package-relative paths are relative to it.</summary>
    public string StagingRoot { get; }

    /// <summary>Folders that contain file sets, root first. Empty when the package contains no mod files at all.</summary>
    public IReadOnlyList<InstallCandidate> Candidates { get; }

    /// <summary>Every file set in the package, in candidate order.</summary>
    public IReadOnlyList<ModFileSet> AllFileSets => Candidates.SelectMany(c => c.FileSets).ToList();

    /// <summary>
    /// Package-relative paths of files that are neither part of a file set nor documentation (textures, configs,
    /// data folders). When present the package structure is preserved on install instead of flattened.
    /// </summary>
    public IReadOnlyList<string> ExtraFiles { get; }

    /// <summary>Package-relative paths of readme-like files (.txt, .md, .nfo, .url, .pdf, .html); kept next to the mod files.</summary>
    public IReadOnlyList<string> DocumentationFiles { get; }

    /// <summary>True when the package ships data files besides the pak sets, so its folder layout must be kept.</summary>
    public bool PreserveStructure => ExtraFiles.Count > 0;

    /// <summary>Display name derived from the archive/folder/file name (Nexus version suffixes stripped).</summary>
    public string SuggestedName { get; }

    /// <summary>Version parsed from the archive name, if any ("1.2.0").</summary>
    public string? SuggestedVersion { get; }

    /// <summary>Non-blocking problems: incomplete sets, ignored files, and so on.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>True when the package has no valid mod files and cannot be installed.</summary>
    public bool IsEmpty => !AllFileSets.Any(set => set.IsValid);

    /// <summary>True when the user must choose: more than one file set, or sets in several folders.</summary>
    public bool RequiresChoice => Candidates.Count > 1 || AllFileSets.Count > 1;

    /// <summary>The file sets selected by default (valid sets of the default candidate).</summary>
    public IReadOnlyList<ModFileSet> DefaultSelection =>
        Candidates.Where(c => c.IsDefault).SelectMany(c => c.FileSets).Where(s => s.IsValid).ToList();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cleanup?.Invoke();
    }
}
