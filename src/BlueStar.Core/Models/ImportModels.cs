namespace BlueStar.Core.Models;

/// <summary>
/// Model representing a game found via the Steam Store Search API.
/// </summary>
public sealed record SteamStoreSearchItem(
    uint AppId,
    string Name,
    string? TinyImage,
    string HeaderImageUrl)
{
    public string FormattedThumbnail => !string.IsNullOrWhiteSpace(TinyImage)
        ? TinyImage
        : HeaderImageUrl;
}

/// <summary>
/// Preview item for a depot discovered inside a DepotBox ZIP archive.
/// </summary>
public sealed record DepotPreviewItem(
    ulong DepotId,
    string Name,
    string? ManifestFileName);

/// <summary>
/// Preview item for a DLC discovered inside a DepotBox ZIP archive.
/// </summary>
public sealed record DlcPreviewItem(
    uint AppId,
    string Name);
