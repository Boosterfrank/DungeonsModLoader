using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DungeonsModLoader.Nexus.Api;

// Raw wire shapes. v1 REST uses snake_case; v2 GraphQL uses camelCase. Everything is mapped to the public models in
// NexusModels.cs by the static Map methods below, so the rest of the app never sees these classes.

internal static class JsonSettings
{
    public static JsonSerializerOptions V1 { get; } = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static JsonSerializerOptions V2 { get; } = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

// ------------------------------------------------------------------------------------------------------------------
// v1
// ------------------------------------------------------------------------------------------------------------------

internal sealed class V1ModInfo
{
    [JsonPropertyName("mod_id")] public long ModId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("picture_url")] public string? PictureUrl { get; set; }
    [JsonPropertyName("mod_downloads")] public long Downloads { get; set; }
    [JsonPropertyName("mod_unique_downloads")] public long UniqueDownloads { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("endorsement_count")] public long Endorsements { get; set; }
    [JsonPropertyName("created_timestamp")] public long CreatedTimestamp { get; set; }
    [JsonPropertyName("updated_timestamp")] public long UpdatedTimestamp { get; set; }
    [JsonPropertyName("author")] public string? Author { get; set; }
    [JsonPropertyName("uploaded_by")] public string? UploadedBy { get; set; }
    [JsonPropertyName("contains_adult_content")] public bool AdultContent { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("available")] public bool Available { get; set; } = true;
    [JsonPropertyName("category_id")] public long CategoryId { get; set; }

    public NexusMod Map() => new(
        ModId,
        string.IsNullOrWhiteSpace(Name) ? $"Mod {ModId}" : Name.Trim(),
        Summary?.Trim() ?? string.Empty,
        Description,
        Blank(Author),
        Blank(UploadedBy),
        Blank(Version),
        Blank(PictureUrl),
        ThumbnailFor(PictureUrl),
        (int)Math.Clamp(Endorsements, 0, int.MaxValue),
        (int)Math.Clamp(Downloads, 0, int.MaxValue),
        DateTimeOffset.FromUnixTimeSeconds(CreatedTimestamp),
        DateTimeOffset.FromUnixTimeSeconds(UpdatedTimestamp),
        Status ?? "published",
        Available,
        AdultContent,
        null,
        false);

    /// <summary>v1 carries only the full picture; Nexus serves a thumbnail at the same path with "/thumbnails" inserted.</summary>
    internal static string? ThumbnailFor(string? pictureUrl)
    {
        if (string.IsNullOrWhiteSpace(pictureUrl))
        {
            return null;
        }

        const string marker = "/images/";
        var index = pictureUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0 || pictureUrl.Contains("/images/thumbnails/", StringComparison.OrdinalIgnoreCase))
        {
            return pictureUrl;
        }

        return pictureUrl[..(index + marker.Length)] + "thumbnails/" + pictureUrl[(index + marker.Length)..];
    }

    internal static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed class V1FileList
{
    [JsonPropertyName("files")] public List<V1FileInfo> Files { get; set; } = new();
    [JsonPropertyName("file_updates")] public List<V1FileUpdate> FileUpdates { get; set; } = new();

    public NexusFileList Map() => new(
        Files.Where(f => f is not null).Select(f => f.Map()).ToList(),
        FileUpdates.Where(u => u is not null).Select(u => u.Map()).ToList());
}

internal sealed class V1FileInfo
{
    [JsonPropertyName("file_id")] public long FileId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("category_id")] public int CategoryId { get; set; }
    [JsonPropertyName("category_name")] public string? CategoryName { get; set; }
    [JsonPropertyName("is_primary")] public bool IsPrimary { get; set; }
    [JsonPropertyName("size_kb")] public long SizeKb { get; set; }
    [JsonPropertyName("size_in_bytes")] public long? SizeInBytes { get; set; }
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
    [JsonPropertyName("uploaded_timestamp")] public long UploadedTimestamp { get; set; }
    [JsonPropertyName("mod_version")] public string? ModVersion { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("changelog_html")] public string? ChangelogHtml { get; set; }

    public NexusFile Map() => new(
        FileId,
        string.IsNullOrWhiteSpace(Name) ? FileName ?? $"File {FileId}" : Name.Trim(),
        Version?.Trim() ?? string.Empty,
        CategoryMapping.FromId(CategoryId, CategoryName),
        IsPrimary,
        SizeInBytes ?? SizeKb * 1024,
        V1ModInfo.Blank(FileName),
        DateTimeOffset.FromUnixTimeSeconds(UploadedTimestamp),
        V1ModInfo.Blank(ModVersion),
        V1ModInfo.Blank(Description),
        V1ModInfo.Blank(ChangelogHtml));
}

internal sealed class V1FileUpdate
{
    [JsonPropertyName("old_file_id")] public long OldFileId { get; set; }
    [JsonPropertyName("new_file_id")] public long NewFileId { get; set; }
    [JsonPropertyName("old_file_name")] public string? OldFileName { get; set; }
    [JsonPropertyName("new_file_name")] public string? NewFileName { get; set; }
    [JsonPropertyName("uploaded_timestamp")] public long UploadedTimestamp { get; set; }

    public NexusFileUpdate Map() => new(OldFileId, NewFileId, OldFileName, NewFileName, DateTimeOffset.FromUnixTimeSeconds(UploadedTimestamp));
}

internal sealed class V1DownloadLink
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("short_name")] public string? ShortName { get; set; }
    [JsonPropertyName("URI")] public string? Uri { get; set; }

    public NexusDownloadLink? Map() =>
        System.Uri.TryCreate(Uri, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http")
            ? new NexusDownloadLink(Name ?? "Nexus Mods", ShortName ?? Name ?? "Nexus", uri)
            : null;
}

internal sealed class V1UpdatedMod
{
    [JsonPropertyName("mod_id")] public long ModId { get; set; }
    [JsonPropertyName("latest_file_update")] public long LatestFileUpdate { get; set; }
    [JsonPropertyName("latest_mod_activity")] public long LatestModActivity { get; set; }

    public NexusUpdatedMod Map() => new(ModId, DateTimeOffset.FromUnixTimeSeconds(LatestFileUpdate), DateTimeOffset.FromUnixTimeSeconds(LatestModActivity));
}

internal static class V1Validate
{
    /// <summary>Reads the validate response by hand: the premium flags appear as <c>is_premium</c> and, in old docs, <c>is_premium?</c>.</summary>
    public static NexusUser Map(JsonElement root)
    {
        return new NexusUser(
            ReadLong(root, "user_id"),
            ReadString(root, "name") ?? "Nexus user",
            ReadBool(root, "is_premium") ?? ReadBool(root, "is_premium?") ?? false,
            ReadBool(root, "is_supporter") ?? ReadBool(root, "is_supporter?") ?? false,
            ReadString(root, "email"),
            ReadString(root, "profile_url"));
    }

    private static long ReadLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? ReadBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }
}

internal static class CategoryMapping
{
    public static NexusFileCategory FromId(int id, string? name)
    {
        if (id is >= 1 and <= 7)
        {
            return (NexusFileCategory)id;
        }

        return FromName(name);
    }

    public static NexusFileCategory FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return NexusFileCategory.Unknown;
        }

        var upper = name.Trim().ToUpperInvariant();
        if (upper.StartsWith("MAIN", StringComparison.Ordinal)) return NexusFileCategory.Main;
        if (upper.StartsWith("UPDATE", StringComparison.Ordinal) || upper.StartsWith("PATCH", StringComparison.Ordinal)) return NexusFileCategory.Update;
        if (upper.StartsWith("OPTION", StringComparison.Ordinal)) return NexusFileCategory.Optional;
        if (upper.StartsWith("OLD", StringComparison.Ordinal)) return NexusFileCategory.OldVersion;
        if (upper.StartsWith("MISC", StringComparison.Ordinal)) return NexusFileCategory.Miscellaneous;
        if (upper.StartsWith("REMOVED", StringComparison.Ordinal) || upper.StartsWith("DELETED", StringComparison.Ordinal)) return NexusFileCategory.Removed;
        if (upper.StartsWith("ARCHIVED", StringComparison.Ordinal)) return NexusFileCategory.Archived;
        return NexusFileCategory.Unknown;
    }
}

// ------------------------------------------------------------------------------------------------------------------
// v2 GraphQL
// ------------------------------------------------------------------------------------------------------------------

internal sealed class GqlResponse<T>
    where T : class
{
    public T? Data { get; set; }
    public List<GqlError>? Errors { get; set; }
}

internal sealed class GqlError
{
    public string? Message { get; set; }
}

internal sealed class GqlModsData
{
    public GqlModPage? Mods { get; set; }
}

internal sealed class GqlModPage
{
    public int TotalCount { get; set; }
    public List<GqlMod> Nodes { get; set; } = new();
}

internal sealed class GqlModData
{
    public GqlMod? Mod { get; set; }
    public List<GqlModFile>? ModFiles { get; set; }
}

internal sealed class GqlMod
{
    public long ModId { get; set; }
    public string? Name { get; set; }
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string? Version { get; set; }
    public long Endorsements { get; set; }
    public long Downloads { get; set; }
    public string? PictureUrl { get; set; }
    public string? ThumbnailUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? Status { get; set; }
    public bool AdultContent { get; set; }
    public bool DirectDownloadEnabled { get; set; }
    public GqlUser? Uploader { get; set; }
    public GqlCategory? ModCategory { get; set; }
    public bool LegacyModRequirementsEnabled { get; set; }
    public GqlModRequirements? ModRequirements { get; set; }

    public NexusMod Map() => new(
        ModId,
        string.IsNullOrWhiteSpace(Name) ? $"Mod {ModId}" : Name.Trim(),
        Summary?.Trim() ?? string.Empty,
        Description,
        V1ModInfo.Blank(Author) ?? V1ModInfo.Blank(Uploader?.Name),
        V1ModInfo.Blank(Uploader?.Name),
        V1ModInfo.Blank(Version),
        V1ModInfo.Blank(PictureUrl),
        V1ModInfo.Blank(ThumbnailUrl) ?? V1ModInfo.ThumbnailFor(PictureUrl),
        (int)Math.Clamp(Endorsements, 0, int.MaxValue),
        (int)Math.Clamp(Downloads, 0, int.MaxValue),
        CreatedAt,
        UpdatedAt,
        Status ?? "published",
        !string.Equals(Status, "hidden", StringComparison.OrdinalIgnoreCase) && !string.Equals(Status, "removed", StringComparison.OrdinalIgnoreCase),
        AdultContent,
        V1ModInfo.Blank(ModCategory?.Name),
        DirectDownloadEnabled);
}

internal sealed class GqlUser
{
    public string? Name { get; set; }
    public long MemberId { get; set; }
}

internal sealed class GqlCategory
{
    public long CategoryId { get; set; }
    public string? Name { get; set; }
}

internal sealed class GqlModRequirements
{
    public GqlRequirementPage? NexusRequirements { get; set; }
    public List<GqlDlcRequirement>? DlcRequirements { get; set; }
}

internal sealed class GqlRequirementPage
{
    public int TotalCount { get; set; }
    public List<GqlRequirement> Nodes { get; set; } = new();
}

internal sealed class GqlRequirement
{
    public string? Id { get; set; }
    public string? GameId { get; set; }
    public string? ModId { get; set; }
    public string? ModName { get; set; }
    public string? Notes { get; set; }
    public string? Url { get; set; }
    public bool ExternalRequirement { get; set; }

    public NexusRequirement Map()
    {
        long? modId = !ExternalRequirement && long.TryParse(ModId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
        var name = V1ModInfo.Blank(ModName) ?? (modId is null ? "Unknown requirement" : $"Mod {modId}");
        return new NexusRequirement(modId, name, V1ModInfo.Blank(Url), V1ModInfo.Blank(Notes), ExternalRequirement);
    }
}

internal sealed class GqlDlcRequirement
{
    public GqlGameExpansion? GameExpansion { get; set; }
    public string? Notes { get; set; }
}

internal sealed class GqlGameExpansion
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}

internal sealed class GqlModFile
{
    public long FileId { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Category { get; set; }
    public int CategoryId { get; set; }
    public long Size { get; set; }
    public long? SizeInBytes { get; set; }
    public long Date { get; set; }
    public string? Description { get; set; }
    public int Primary { get; set; }
    public List<string>? ChangelogText { get; set; }

    public NexusFile Map() => new(
        FileId,
        string.IsNullOrWhiteSpace(Name) ? $"File {FileId}" : Name.Trim(),
        Version?.Trim() ?? string.Empty,
        CategoryMapping.FromId(CategoryId, Category),
        Primary != 0,
        SizeInBytes ?? Size * 1024,
        null,
        DateTimeOffset.FromUnixTimeSeconds(Date),
        null,
        V1ModInfo.Blank(Description),
        ChangelogText is { Count: > 0 } ? string.Join("<br/>", ChangelogText) : null);
}
