using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Represents the dynamic multi-provider availability state for an individual depot manifest.
/// Separate from the static manifest identity and from installed instance state.
/// </summary>
public record ManifestAvailability
{
    /// <summary>
    /// Gets the Steam Depot ID.
    /// </summary>
    public uint DepotId { get; init; }

    /// <summary>
    /// Gets the Steam Manifest ID.
    /// </summary>
    public ulong ManifestId { get; init; }

    /// <summary>
    /// Gets a value indicating whether this manifest is already available in the local persistent cache.
    /// </summary>
    public bool IsCachedLocally { get; init; }

    /// <summary>
    /// Gets the list of available provider routes capable of supplying this manifest.
    /// </summary>
    public IReadOnlyList<ManifestSourceRoute> Routes { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether a 64-char decryption key is known for this depot.
    /// </summary>
    public bool HasDecryptionKey { get; init; }

    /// <summary>
    /// Gets a value indicating whether this manifest can be acquired (either cached or has healthy routes).
    /// </summary>
    public bool CanAcquire => IsCachedLocally || Routes.Count > 0;
}

/// <summary>
/// Represents the aggregate availability across all depots for a specific <see cref="GameVersion"/>.
/// </summary>
public record VersionAvailability
{
    /// <summary>
    /// Gets the Steam AppID.
    /// </summary>
    public uint AppId { get; init; }

    /// <summary>
    /// Gets the build identifier.
    /// </summary>
    public string BuildId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the branch name.
    /// </summary>
    public string BranchName { get; init; } = "public";

    /// <summary>
    /// Gets availability per depot.
    /// </summary>
    public IReadOnlyDictionary<uint, ManifestAvailability> Depots { get; init; } = new Dictionary<uint, ManifestAvailability>();

    /// <summary>
    /// Gets the list of available fixes or components for this game/build (e.g. "ReFix", "OnlineFix").
    /// </summary>
    public IReadOnlyList<string> AvailableComponents { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether all depot manifests are known and acquirable.
    /// </summary>
    public bool ManifestsKnown => Depots.Count > 0 && Depots.Values.All(d => d.CanAcquire);

    /// <summary>
    /// Gets a value indicating whether all depot decryption keys are known.
    /// </summary>
    public bool KeysComplete => Depots.Count > 0 && Depots.Values.All(d => d.HasDecryptionKey);

    /// <summary>
    /// Gets a value indicating whether every required depot in this version is available to download.
    /// </summary>
    public bool IsFullyAvailable => Depots.Count > 0 && Depots.Values.All(d => d.CanAcquire && d.HasDecryptionKey);

    /// <summary>
    /// Gets the list of depots that are currently missing acquisition routes.
    /// </summary>
    public IReadOnlyList<uint> MissingManifestDepots =>
        Depots.Where(d => !d.Value.CanAcquire).Select(d => d.Key).ToList().AsReadOnly();

    /// <summary>
    /// Gets the list of depots that are currently missing decryption keys.
    /// </summary>
    public IReadOnlyList<uint> MissingKeyDepots =>
        Depots.Where(d => !d.Value.HasDecryptionKey).Select(d => d.Key).ToList().AsReadOnly();

    /// <summary>
    /// Gets the list of depots that are currently missing routes or keys.
    /// </summary>
    public IReadOnlyList<uint> IncompleteDepots =>
        Depots.Where(d => !d.Value.CanAcquire || !d.Value.HasDecryptionKey).Select(d => d.Key).ToList().AsReadOnly();
}

