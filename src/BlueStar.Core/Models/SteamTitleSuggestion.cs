namespace BlueStar.Core.Models;

/// <summary>
/// One product Steam's title autocomplete offers for a partial name.
/// </summary>
/// <remarks>
/// The faceted store search matches whole words: <c>term=phasmo</c> answers with nothing at all,
/// while <c>term=phasmophobia</c> answers with seventeen products. Autocomplete is the endpoint
/// that does match prefixes, so it is what turns half a title into something the search can be
/// run with.
/// </remarks>
/// <param name="AppId">The product's AppID.</param>
/// <param name="Name">Its full store name.</param>
public sealed record SteamTitleSuggestion(uint AppId, string Name);
