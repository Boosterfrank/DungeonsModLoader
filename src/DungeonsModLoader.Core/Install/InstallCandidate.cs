namespace DungeonsModLoader.Core.Install;

/// <summary>
/// One folder of the package that contains mod file sets: the package root ("Main") or a sub folder such as
/// "Option A". The picker shows candidates as groups and lets the user choose the file sets to install.
/// </summary>
/// <param name="RelativeDirectory">Package-relative folder ('/' separators; empty for the root).</param>
/// <param name="Name">Display name of the group ("Main" for the root, otherwise the folder path).</param>
/// <param name="FileSets">The file sets directly inside this folder.</param>
/// <param name="IsDefault">Pre-selected in the picker (the root, or the first folder when the root has no sets).</param>
public sealed record InstallCandidate(string RelativeDirectory, string Name, IReadOnlyList<ModFileSet> FileSets, bool IsDefault);
