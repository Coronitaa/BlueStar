using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BlueStar.Core.Models;

public sealed class BuildHistoryPage
{
    [JsonPropertyName("appId")]
    public uint AppId { get; init; }

    [JsonPropertyName("branch")]
    public string Branch { get; init; } = "public";

    [JsonPropertyName("page")]
    public int Page { get; init; } = 1;

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; } = 20;

    [JsonPropertyName("totalItems")]
    public int TotalItems { get; init; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<BuildSnapshot> Items { get; init; } = Array.Empty<BuildSnapshot>();
}
