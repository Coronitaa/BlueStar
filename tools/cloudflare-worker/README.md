# BlueStar Cloudflare Worker & DepotBox Webhook Setup

This Cloudflare Worker provides serverless backend endpoints for:
1. **BlueStar Real Telemetry Collection**: Tracks instances added by BlueStar users.
2. **DepotBox Webhook Ingestion**: Receives newly added games, updated builds, and API usage logs from DepotBox.
3. **Sliding-Window Aggregations**: Computes 7-day "Trending on BlueStar" and all-time "Most Added in BlueStar".
4. **Steam Rankings Proxy & Cache**: Fast edge caching for the 4 Steam rankings categories.

---

## 📋 URLs for DepotBox Webhook Configuration

In your **DepotBox API Configuration** panel, set the following URLs:

| Field in DepotBox | Webhook URL to enter | Description |
|---|---|---|
| **Added Game Webhook URL** | `https://api.bluestar.workers.dev/api/webhooks/depotbox/added` | Receives new games added to DepotBox |
| **Updated Game Webhook URL** | `https://api.bluestar.workers.dev/api/webhooks/depotbox/updated` | Receives build and manifest updates |
| **API usage logs webhook URL** | `https://api.bluestar.workers.dev/api/webhooks/depotbox/logs` | Ingests usage/download activity to calculate popularity |

*(Replace `api.bluestar.workers.dev` with your actual Cloudflare Worker domain).*

---

## 🚀 Deployment Instructions

### 1. Install Wrangler CLI
```bash
npm install -g wrangler
```

### 2. Login to Cloudflare
```bash
wrangler login
```

### 3. Create KV Namespaces
```bash
wrangler kv:namespace create STATS_KV
wrangler kv:namespace create DEPOTBOX_KV
```

Copy the generated IDs and configure `wrangler.toml`:
```toml
[[kv_namespaces]]
binding = "STATS_KV"
id = "<YOUR_STATS_KV_ID>"

[[kv_namespaces]]
binding = "DEPOTBOX_KV"
id = "<YOUR_DEPOTBOX_KV_ID>"
```

### 4. Deploy to Cloudflare
```bash
wrangler deploy
```

---

## 🛰️ API Endpoints Summary

- `POST /api/stats/report-instance` - Record instance addition from desktop client (`{ appId, name }`)
- `POST /api/webhooks/depotbox/logs` - Process DepotBox usage logs to compute 7-day and all-time popularity
- `POST /api/webhooks/depotbox/added` - Ingest new game additions from DepotBox
- `POST /api/webhooks/depotbox/updated` - Ingest game updates/patches from DepotBox
- `GET /api/stats/trending` - Real 7-day trending games added on BlueStar
- `GET /api/stats/most-played` - Real all-time most added game instances on BlueStar
- `GET /api/feed/depotbox/added` - Feed of new games from DepotBox webhook
- `GET /api/feed/depotbox/updated` - Feed of updated games from DepotBox webhook
- `GET /api/steam/lists?type=most_played|trending|top_sellers|top_rated` - Edge-cached Steam ranking lists
