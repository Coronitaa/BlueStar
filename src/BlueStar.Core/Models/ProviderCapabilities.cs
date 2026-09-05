using System;

namespace BlueStar.Core.Models;

/// <summary>
/// Declares the technical capabilities and supported operations of a provider.
/// Providers expose capabilities so the engine can route operations intelligently.
/// </summary>
[Flags]
public enum ProviderCapabilities
{
    None = 0,

    /// <summary>Can search for games and retrieve catalog metadata.</summary>
    CatalogSearch = 1 << 0,

    /// <summary>Can discover manifest revisions and depot IDs for an AppID.</summary>
    ManifestDiscovery = 1 << 1,

    /// <summary>Can download individual .manifest binary files directly.</summary>
    ManifestDownload = 1 << 2,

    /// <summary>Can download multi-depot zip archives.</summary>
    ArchiveDownload = 1 << 3,

    /// <summary>Can provide depot decryption AES keys.</summary>
    DepotKeys = 1 << 4,

    /// <summary>Can discover formal builds, release dates, and branch mappings.</summary>
    BuildDiscovery = 1 << 5,

    /// <summary>Can list game fixes, bypasses, or multiplayer emulators.</summary>
    FixCatalog = 1 << 6,

    /// <summary>Can download game fixes or multiplayer emulators.</summary>
    FixDownload = 1 << 7,

    /// <summary>Can provide curated build recommendations and compatibility ratings.</summary>
    CurationAdvice = 1 << 8
}
