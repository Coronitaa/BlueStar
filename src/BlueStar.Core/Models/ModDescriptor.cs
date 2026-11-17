using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Data-only description of how a game stores mods. Adding mod support for another game that follows the
/// "one folder per mod, with a manifest file" convention is a new entry in ModDescriptors.json, not new code.
/// </summary>
public sealed record ModDescriptor
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";

    /// <summary>Steam app ids this descriptor applies to.</summary>
    public IReadOnlyList<uint> AppIds { get; init; } = [];

    /// <summary>Files relative to the install folder; if any exists the descriptor also applies (non-Steam copies).</summary>
    public IReadOnlyList<string> Executables { get; init; } = [];

    /// <summary>Mod folders relative to the install folder. Each child folder is a mod; the first one receives installs.</summary>
    public IReadOnlyList<string> ModFolders { get; init; } = [];

    public ModManifestDescriptor Manifest { get; init; } = new();

    /// <summary>Where the list of enabled mods lives. Null means mods cannot be toggled.</summary>
    public ModEnableListDescriptor? EnableList { get; init; }
}

public sealed record ModManifestDescriptor
{
    /// <summary>Manifest file inside each mod folder, for example filelist.xml.</summary>
    public string FileName { get; init; } = "";

    /// <summary>"xml" (attributes of the root element) or "json" (top-level properties).</summary>
    public string Format { get; init; } = "xml";

    /// <summary>Expected root element name for xml manifests; empty accepts any.</summary>
    public string? RootElement { get; init; }

    public string NameField { get; init; } = "name";
    public string? VersionField { get; init; }
    public string? IdField { get; init; }
}

public sealed record ModEnableListDescriptor
{
    /// <summary>Config file relative to the install folder.</summary>
    public string File { get; init; } = "";

    /// <summary>Only "xml-path-list" is supported: an element holding one child per enabled mod manifest path.</summary>
    public string Format { get; init; } = "xml-path-list";

    /// <summary>Slash separated element path from the root, for example contentpackages/regularpackages.</summary>
    public string ListElementPath { get; init; } = "";

    public string ItemElement { get; init; } = "package";
    public string PathAttribute { get; init; } = "path";
}
