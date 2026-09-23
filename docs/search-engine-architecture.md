# BlueStar Steam Search Engine: Architecture Specification

## 1. Architectural Philosophy
The previous implementation of the Explore screen relied entirely on live requests to Steam's store HTML search endpoint (`https://store.steampowered.com/search/results/`). This caused critical vulnerabilities:
1. **Search Blindness**: Games missing from Steam's current promotional or search lists were undiscoverable.
2. **Rate Limit Vulnerability**: Facet count probes and multi-rung ladders exhausted IP quotas, causing HTTP 429/403 blocks.
3. **UI Flicker & Jitter**: Clearing observable collections during background network requests produced severe screen flicker.
4. **Server Non-Determinism**: Steam silently dropped sort tokens (such as `Reviews_ASC` or `Name_DESC`), returning default relevance listings.

The refactored architecture adopts an **Offline-First Layered Pipeline**:

```
[ User Input / Toolbar / Facet Interaction ]
                     │
                     ▼
             [ SteamQueryParser ]
                     │
         ┌───────────┴───────────┐
         ▼                       ▼
  [ AppID Intent ]        [ Term / Facet Intent ]
         │                       │
         ▼                       ▼
[ Local Catalog Repo ]   [ Local Candidate Search (SQLite FTS5) ]
 (GetByAppIdAsync)        (QueryAsync with Filters + Sort + Paging)
         │                       │
     ┌───┴───┐               ┌───┴───┐
     ▼       ▼               ▼       ▼
   [Hit]   [Miss]          [Hit]   [Miss]
     │       │               │       │
     │       ▼               │       ▼
     │   [Steam Store]       │   [Steam Store Search]
     │   (Fallback)          │   (Fallback + Validator)
     │       │               │       │
     │       ▼               │       ▼
     │  [Upsert App]         │  [Upsert Batch]
     │       │               │       │
     └───────┬───────────────┴───────┘
             ▼
    [ SearchResponse ]
             │
             ▼
    [ BrowseViewModel ] ───► [ VirtualizingWrapPanel / List ]
             │
             ▼
    [ Progressive Lazy Enrichment (P0-P3) ]
```

---

## 2. Core Components

### 2.1 SteamQueryParser (`BlueStar.Core.Helpers.SteamQueryParser`)
Pre-compiled regular expressions extract canonical Steam identities before executing queries:
- `PureNumericRegex`: `^\s*(?<appid>\d{1,10})\s*$`
- `PrefixedAppIdRegex`: `^\s*app(?:id)?(?:\s*[:=]\s*|\s+)(?<appid>\d{1,10})\s*$`
- `WebUrlAppIdRegex`: `(?:https?://)?(?:store\.steampowered|steamcommunity)\.com/app/(?<appid>\d{1,10})(?:[/?#]|$)`
- `ProtocolUriAppIdRegex`: `steam://(?:rungameid|app|install|run)/(?<appid>\d{1,10})(?:[/?#]|$)`

### 2.2 Local SQLite & FTS5 Repository (`BlueStar.Infrastructure.Catalog.LocalCatalogRepository`)
- **Storage Engine**: SQLite 3 with WAL (`Write-Ahead Logging`), synchronous = NORMAL, and page size = 4096.
- **Full Text Search**: FTS5 virtual table with `unicode61 remove_diacritics 2` tokenization.
- **Normalization**: `DeterministicNormalizer` strips diacritics, lowercases text, and creates compacted alphanumeric keys (`counterstrike2`, `cyberpunk2077`) to match hyphenated, punctuated, and spaced queries identically.
- **Faceted Queries**: Filters (OS, Rating, DRM, Launcher, NSFW, Discount, Tags, RestrictToAppIds) and sorts (`name`, `released`, `reviews`, `reviewcount`, `price`, `appid`) execute at the SQLite level across the entire candidate universe rather than an arbitrary 50-row window.

### 2.3 Layered Search Pipeline (`BlueStar.Infrastructure.Search.SearchPipeline`)
Orchestrates request execution:
1. `ParseQuery`: Extracts AppID or normalized term.
2. `ResolveIntent`: Routes AppIDs directly to local SQLite or single-app Steam fallback.
3. `LocalCandidateSearch`: Queries SQLite FTS5. If candidate count > 0, returns instant local response (`SearchResolutionType.ExactName`, `Prefix`, or `FullText`).
4. `SteamFallback`: If candidate count == 0 or browsing curated lists (`popularnew`, `globaltopsellers`), executes remote store search.
5. `AnomalyDetection`: `SteamResponseValidator` inspects the response. If Steam quietly dropped the requested sort order, `ApplyLocalSort` enforces the sort locally in memory.
6. `BackgroundCache`: Discovered apps are asynchronously upserted into the local database to accelerate future queries.

### 2.4 Domain-Isolated Rate Governor (`BlueStar.Infrastructure.Steam.DomainRateGovernor`)
Provides independent quotas and circuit breakers:
- **`store.steampowered.com`**: 1500ms minimum interval, pacing interactive vs background requests.
- **`api.steampowered.com`**: 500ms minimum interval, independent concurrency quota.
- **Circuit Breaker**: Trips on HTTP 429/403 with exponential backoff (2 min, 4 min, 8 min, 16 min, up to 32 min) plus $\pm 20\%$ randomized jitter to avoid retry storms.

### 2.5 SingleFlight Concurrency (`BlueStar.Core.Helpers.SingleFlight`)
Deduplicates concurrent in-flight requests for identical cache keys or query parameters. Eliminates cache stampedes and redundant network calls when multiple UI controls query simultaneously.

### 2.6 WPF UI Virtualization (`BlueStar.App.Controls.VirtualizingWrapPanel`)
Implements `VirtualizingPanel` and `IScrollInfo`. Calculates responsive grid layouts based on container dimensions, realizing only visible cards ($\approx 10-15$ containers) and recycling them as the user scrolls. Reduces UI tree overhead from hundreds of instantiated UserControls to a small, fixed pool.
