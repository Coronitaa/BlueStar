using System.Text.Json.Serialization;

namespace BlueStar.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SnapshotCompleteness
{
    Complete,
    PartialDelta,
    Inferred
}
