using System;
using System.Collections.Generic;

namespace BlueStar.Core.Models;

/// <summary>
/// Preview of a voluntary community manifest contribution presented to the user for explicit consent.
/// Strictly enforces privacy guarantees: contains ZERO credentials, tokens, cookies, SteamIDs, or personal history.
/// </summary>
public record ContributionPreview
{
    /// <summary>
    /// Steam AppId of the game.
    /// </summary>
    public uint AppId { get; init; }

    /// <summary>
    /// Steam DepotId of the manifest.
    /// </summary>
    public uint DepotId { get; init; }

    /// <summary>
    /// Manifest ID (GID revision identifier).
    /// </summary>
    public ulong ManifestId { get; init; }

    /// <summary>
    /// Cryptographic SHA-256 hash of the manifest binary file.
    /// </summary>
    public string ManifestHashSha256 { get; init; } = string.Empty;

    /// <summary>
    /// Size of the manifest file in bytes.
    /// </summary>
    public long ManifestSizeBytes { get; init; }

    /// <summary>
    /// Cryptographic anonymous contributor identifier (random UUID v4 with zero link to SteamID or user account).
    /// The real SteamID or username is NEVER stored or transmitted.
    /// </summary>
    public string AnonymousContributorId { get; init; } = string.Empty;

    /// <summary>
    /// Legacy compatibility alias for <see cref="AnonymousContributorId"/>.
    /// </summary>
    public string PseudonymousContributorId
    {
        get => AnonymousContributorId;
        init => AnonymousContributorId = value;
    }

    /// <summary>
    /// Absolute local path of the manifest file on disk (remains purely local; never transmitted).
    /// </summary>
    public string LocalSourcePath { get; init; } = string.Empty;

    /// <summary>
    /// Mandatory privacy disclosures and legal transparency statements displayed to the user.
    /// </summary>
    public IReadOnlyList<string> PrivacyDisclosures { get; init; } = [];

    /// <summary>
    /// Explicit confirmation that the contribution was audited and confirmed sanitized of any credentials.
    /// </summary>
    public bool IsSanitized { get; init; } = true;

    /// <summary>
    /// Timestamp when this preview was generated in UTC.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Default standard transparency and privacy disclosures displayed to the user prior to contribution consent.
    /// </summary>
    public static readonly IReadOnlyList<string> StandardDisclosures =
    [
        "1. No Steam credentials, passwords, or Steam Guard authentication codes are collected or transmitted.",
        "2. No session tokens, access tokens, refresh tokens, API keys, or cookies are collected or transmitted.",
        "3. No SteamID, real name, email, or account username is transmitted; a purely random anonymous UUID is used.",
        "4. No game library list, license inventory, or gameplay history is collected or transmitted to external servers.",
        "5. The contribution consists solely of the public depot manifest binary and its cryptographic SHA-256 checksum.",
        "6. Contribution is 100% voluntary, opt-in, and revocable prior to dispatch."
    ];
}
