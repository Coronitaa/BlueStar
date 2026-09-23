# BlueStar Steam Search Engine: Operations Playbook

## 1. Local Catalog Management

### Location & Files
The catalog database and cached metadata are stored locally per user:
- SQLite Database: `%AppData%\BlueStar\catalog.db`
- WAL and Shared Memory: `%AppData%\BlueStar\catalog.db-wal`, `catalog.db-shm`
- Disk Cache: `%AppData%\BlueStar\cache\*.json`

### Snapshot Synchronization
The catalog snapshot service (`SteamCatalogSnapshotService`) can import pre-built, compressed SQLite catalog snapshots without requiring runtime API keys:
1. `CheckSnapshotUpdateAsync`: Reads the remote manifest (`catalog_manifest.json`) containing version, app count, and SHA-256 hash.
2. `DownloadAndApplySnapshotAsync`: Downloads the compressed snapshot, verifies its SHA-256 checksum, and applies it to `%AppData%\BlueStar\catalog.db`.
3. If offline or no network connection is available, the service automatically falls back to the existing local database without throwing exceptions.

---

## 2. Rate Limiting & Cooldown Operations

### Monitoring Circuit Breaker State
The `DomainRateGovernor` monitors HTTP response codes. If Steam responds with HTTP 429 (Too Many Requests) or HTTP 403 (Forbidden), the circuit breaker transitions from `Closed` to `Open`.

In `Open` state:
- Interactive requests immediately fall back to the offline local SQLite catalog.
- Background tasks (such as facet count probes or background enrichment) pause until `CooldownUntil` expires.
- The UI displays non-intrusive cached indicators rather than halting application usage.

To check breaker status programmatically:
```csharp
var isBlocked = SteamRequestGate.IsCoolingDown;
var until = SteamRequestGate.CooldownUntil;
```

---

## 3. Cache Maintenance & Pruning

The multi-tier cache (`FileCacheService`) automatically manages memory and disk footprints:
- **L1 (In-Memory)**: Limited to 1,000 active entries with TTL tracking and automatic pruning.
- **L2 (Disk)**: Stored as formatted JSON in `%AppData%\BlueStar\cache\`.
- **Scheduled Sweeping**: `SweepExpiredEntriesAsync()` scans disk entries non-blocking and removes expired cache items.

To manually clear cache via code:
```csharp
await cacheService.ClearAsync();
```

---

## 4. Diagnostics & Smoke Testing

### Running the CLI Smoke Test Tool
The diagnostic CLI tool (`tools/steam-search-smoke`) allows developers and operators to test the engine without launching the WPF application:

```bash
# Verify AppID deterministic resolution and latency
dotnet run --project tools/steam-search-smoke/steam-search-smoke.csproj -- --query "570"

# Verify title search and run 100-query benchmark
dotnet run --project tools/steam-search-smoke/steam-search-smoke.csproj -- --query "hollow knight" --benchmark

# Verify Steam anomaly detector against opposing sort tokens
dotnet run --project tools/steam-search-smoke/steam-search-smoke.csproj -- --anomaly-test
```
