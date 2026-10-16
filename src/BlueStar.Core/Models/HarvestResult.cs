namespace BlueStar.Core.Models;

/// <summary>
/// Result of a manifest harvest operation.
/// </summary>
public sealed record HarvestResult
{
    public bool IsSuccess { get; init; }

    public string? ManifestFilePath { get; init; }

    public string? ErrorMessage { get; init; }

    public string ProviderName { get; init; } = string.Empty;

    public static HarvestResult Success(string filePath, string providerName) =>
        new()
        {
            IsSuccess = true,
            ManifestFilePath = filePath,
            ProviderName = providerName
        };

    public static HarvestResult Failed(string error, string providerName) =>
        new()
        {
            IsSuccess = false,
            ErrorMessage = error,
            ProviderName = providerName
        };
}
