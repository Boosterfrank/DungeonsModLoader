using System.Xml;
using System.Xml.Linq;

namespace DungeonsModLoader.Core.Game.Xbox;

/// <summary>
/// The parts of <c>MicrosoftGame.config</c> (the GDK package manifest found in the game root) that identify a
/// packaged install: identity, display name and the executable ids used to build an AppUserModelId.
/// Every property is optional; missing nodes yield <c>null</c> or an empty list.
/// </summary>
public sealed class MicrosoftGameConfig
{
    public const string FileName = "MicrosoftGame.config";

    /// <summary><c>Identity/@Name</c>, e.g. <c>Microsoft.MinecraftDungeons2</c>.</summary>
    public string? IdentityName { get; init; }

    /// <summary><c>Identity/@Publisher</c>, e.g. <c>CN=Microsoft Corporation, ...</c>.</summary>
    public string? IdentityPublisher { get; init; }

    /// <summary><c>Identity/@Version</c>, e.g. <c>1.1.1.0</c>.</summary>
    public string? IdentityVersion { get; init; }

    /// <summary><c>ShellVisuals/@DefaultDisplayName</c>, e.g. <c>Minecraft Dungeons II</c>.</summary>
    public string? DefaultDisplayName { get; init; }

    /// <summary><c>ExecutableList/Executable/@Id</c> values in document order; the first one is the main executable.</summary>
    public IReadOnlyList<string> ExecutableIds { get; init; } = Array.Empty<string>();

    /// <summary><c>ExecutableList/Executable/@Name</c> values (file names) in document order.</summary>
    public IReadOnlyList<string> ExecutableNames { get; init; } = Array.Empty<string>();

    /// <summary>Id of the main executable (the first one listed), or <c>null</c>.</summary>
    public string? MainExecutableId => ExecutableIds.Count > 0 ? ExecutableIds[0] : null;

    /// <summary>Parses the XML text. Throws <see cref="XmlException"/> on malformed XML; tolerates missing nodes.</summary>
    public static MicrosoftGameConfig Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var document = XDocument.Parse(xml.TrimStart('﻿'));
        return FromDocument(document);
    }

    /// <summary>Loads and parses the file. Throws on I/O or XML errors.</summary>
    public static MicrosoftGameConfig Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return FromDocument(XDocument.Load(stream));
    }

    /// <summary>Loads and parses the file, or returns <c>null</c> when it is missing, unreadable or malformed.</summary>
    public static MicrosoftGameConfig? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) ? Load(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException)
        {
            return null;
        }
    }

    private static MicrosoftGameConfig FromDocument(XDocument document)
    {
        var game = document.Root;
        if (game is null)
        {
            return new MicrosoftGameConfig();
        }

        var identity = Child(game, "Identity");
        var visuals = Child(game, "ShellVisuals");
        var executables = Child(game, "ExecutableList")?.Elements().Where(e => NameIs(e, "Executable")).ToList()
            ?? new List<XElement>();

        return new MicrosoftGameConfig
        {
            IdentityName = Attr(identity, "Name"),
            IdentityPublisher = Attr(identity, "Publisher"),
            IdentityVersion = Attr(identity, "Version"),
            DefaultDisplayName = Attr(visuals, "DefaultDisplayName"),
            ExecutableIds = executables.Select(e => Attr(e, "Id")).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToList(),
            ExecutableNames = executables.Select(e => Attr(e, "Name")).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).ToList(),
        };
    }

    // The file normally has no namespace, but match by local name so a namespaced variant still parses.
    private static bool NameIs(XElement element, string localName)
        => string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

    private static XElement? Child(XElement parent, string localName) => parent.Elements().FirstOrDefault(e => NameIs(e, localName));

    private static string? Attr(XElement? element, string name)
    {
        var attribute = element?.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
        var value = attribute?.Value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
