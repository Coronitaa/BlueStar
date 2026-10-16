namespace BlueStar.Core.Models;

/// <summary>
/// Result of an end-to-end harvest and voluntary community contribution.
/// </summary>
public sealed record ContributionHarvestReceipt
{
    public bool IsSuccess { get; init; }

    public string? ErrorMessage { get; init; }

    public PublicationReceipt? PublicationReceipt { get; init; }

    public HarvestResult? HarvestResult { get; init; }

    public int StardustAwarded => PublicationReceipt?.StardustCredited ?? 0;

    public string Status => PublicationReceipt?.Status ?? (IsSuccess ? "Harvested" : "Failed");
}
