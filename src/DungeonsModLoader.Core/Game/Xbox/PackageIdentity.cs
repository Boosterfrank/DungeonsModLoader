namespace DungeonsModLoader.Core.Game.Xbox;

/// <summary>
/// Helpers for packaged-app identifiers. A PackageFullName is
/// <c>Name_Version_Architecture_ResourceId_PublisherId</c> (the ResourceId is usually empty, giving a double
/// underscore) or the older 4-part <c>Name_Version_Architecture_PublisherId</c>; the PackageFamilyName is
/// <c>Name_PublisherId</c>; the AppUserModelId is <c>PackageFamilyName!ApplicationId</c>.
/// </summary>
public static class PackageIdentity
{
    /// <summary>Package name (the part before the first underscore), or <c>null</c>.</summary>
    public static string? GetName(string? packageFullName)
    {
        if (string.IsNullOrWhiteSpace(packageFullName))
        {
            return null;
        }

        var underscore = packageFullName.IndexOf('_');
        return underscore <= 0 ? packageFullName.Trim() : packageFullName[..underscore];
    }

    /// <summary>
    /// Derives the PackageFamilyName (<c>Name_PublisherId</c>) from a PackageFullName. Accepts the 5-part and
    /// 4-part forms and returns a 2-part input unchanged (it already is a family name). Returns <c>null</c> for
    /// anything else.
    /// </summary>
    public static string? GetFamilyName(string? packageFullName)
    {
        if (string.IsNullOrWhiteSpace(packageFullName))
        {
            return null;
        }

        var parts = packageFullName.Trim().Split('_');
        if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
        {
            return packageFullName.Trim();
        }

        if (parts.Length < 4 || parts[0].Length == 0 || parts[^1].Length == 0)
        {
            return null;
        }

        return parts[0] + "_" + parts[^1];
    }

    /// <summary>Builds <c>PackageFamilyName!ApplicationId</c>, or <c>null</c> when either part is unknown.</summary>
    public static string? GetAppUserModelId(string? packageFullName, string? applicationId)
    {
        var family = GetFamilyName(packageFullName);
        if (family is null || string.IsNullOrWhiteSpace(applicationId))
        {
            return null;
        }

        return family + "!" + applicationId.Trim();
    }
}
