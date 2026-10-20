using System.Text.Json.Serialization;

namespace BlueStar.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DateCertainty
{
    Verified,
    Estimated,
    Unknown
}
