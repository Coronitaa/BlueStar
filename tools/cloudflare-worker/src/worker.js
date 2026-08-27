/**
 * BlueStar Cloudflare Worker
 * ─────────────────────────────────────────────────────────────────────────────
 * Real-time community statistics & webhook receiver for BlueStar & DepotBox.
 *
 * Capabilities:
 * 1. Collects BlueStar instance creations (/api/stats/report-instance)
 * 2. Parses DepotBox API usage logs (/api/webhooks/depotbox/logs)
 * 3. Calculates 7-day "Trending on BlueStar" and all-time "Most Added in BlueStar"
 * 4. Ingests DepotBox "Added Game" and "Updated Game" webhooks
 * 5. Serves cached Steam Rankings for the 4 Steam categories
 */

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    const method = request.method;

    if (method === "OPTIONS") {
      return new Response(null, {
        headers: {
          "Access-Control-Allow-Origin": "*",
          "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
          "Access-Control-Allow-Headers": "Content-Type, Authorization",
        }
      });
    }

    const corsHeaders = {
      "Access-Control-Allow-Origin": "*",
      "Content-Type": "application/json",
      "Cache-Control": "no-cache"
    };

    try {
      // ═════════════════════════════════════════════════════════════════════
      // 1. BLUESTAR INSTANCE TELEMETRY & LOGS INGESTION
      // ═════════════════════════════════════════════════════════════════════

      // Telemetry: Desktop client adds an instance
      if (url.pathname === "/api/stats/report-instance" && method === "POST") {
        const body = await request.json().catch(() => ({}));
        const appId = Number(body.appId);
        const name = body.name || `App ${appId}`;

        if (appId > 0 && env.STATS_KV) {
          await recordInstanceEvent(env.STATS_KV, appId, name);
        }

        return new Response(JSON.stringify({ success: true, appId, name }), { headers: corsHeaders });
      }

      // Webhook: DepotBox API Usage Logs
      if (url.pathname === "/api/webhooks/depotbox/logs" && method === "POST") {
        const body = await request.json().catch(() => ({}));
        const extracted = extractGamesFromLog(body);

        if (env.STATS_KV && extracted.length > 0) {
          for (const item of extracted) {
            await recordInstanceEvent(env.STATS_KV, item.appId, item.name);
          }
        }

        return new Response(JSON.stringify({ success: true, processedCount: extracted.length }), { headers: corsHeaders });
      }

      // ═════════════════════════════════════════════════════════════════════
      // 2. DEPOTBOX NEW & UPDATED GAME WEBHOOKS
      // ═════════════════════════════════════════════════════════════════════

      if (url.pathname === "/api/webhooks/depotbox/added" && method === "POST") {
        const body = await request.json().catch(() => ({}));
        const item = extractSingleGameWebhook(body, "New Depot Added");

        if (env.DEPOTBOX_KV && item) {
          await pushToFeed(env.DEPOTBOX_KV, "feed_depotbox_added", item);
        }

        return new Response(JSON.stringify({ success: true, item }), { headers: corsHeaders });
      }

      if (url.pathname === "/api/webhooks/depotbox/updated" && method === "POST") {
        const body = await request.json().catch(() => ({}));
        const item = extractSingleGameWebhook(body, "Build Updated");

        if (env.DEPOTBOX_KV && item) {
          await pushToFeed(env.DEPOTBOX_KV, "feed_depotbox_updated", item);
        }

        return new Response(JSON.stringify({ success: true, item }), { headers: corsHeaders });
      }

      // ═════════════════════════════════════════════════════════════════════
      // 3. BLUESTAR COMMUNITY STATS ENDPOINTS (7-Day & All-Time)
      // ═════════════════════════════════════════════════════════════════════

      // Trending on BlueStar: Instances added in the last 7 days
      if (url.pathname === "/api/stats/trending") {
        const results = await getTrendingStats(env.STATS_KV);
        return new Response(JSON.stringify({ count: results.length, results }), { headers: corsHeaders });
      }

      // Most Added on BlueStar: All-time top instances
      if (url.pathname === "/api/stats/most-played" || url.pathname === "/api/stats/most-added") {
        const results = await getMostAddedStats(env.STATS_KV);
        return new Response(JSON.stringify({ count: results.length, results }), { headers: corsHeaders });
      }

      // ═════════════════════════════════════════════════════════════════════
      // 4. DEPOTBOX FEEDS FOR CLIENTS
      // ═════════════════════════════════════════════════════════════════════

      if (url.pathname === "/api/feed/depotbox/added") {
        const results = await getFeed(env.DEPOTBOX_KV, "feed_depotbox_added");
        return new Response(JSON.stringify({ count: results.length, results }), { headers: corsHeaders });
      }

      if (url.pathname === "/api/feed/depotbox/updated") {
        const results = await getFeed(env.DEPOTBOX_KV, "feed_depotbox_updated");
        return new Response(JSON.stringify({ count: results.length, results }), { headers: corsHeaders });
      }

      // ═════════════════════════════════════════════════════════════════════
      // 5. STEAM RANKINGS (For the 4 Steam categories only)
      // ═════════════════════════════════════════════════════════════════════

      if (url.pathname === "/api/steam/lists" || url.pathname === "/api/steamdb/lists") {
        const type = url.searchParams.get("type") || "most_played";
        const results = await fetchSteamRankings(type);
        return new Response(JSON.stringify({ type, count: results.length, results }), {
          headers: { ...corsHeaders, "Cache-Control": "public, max-age=1800" }
        });
      }

      // Root Status
      return new Response(JSON.stringify({
        service: "BlueStar Community & Webhooks Worker",
        status: "healthy",
        version: "2.0.0",
        timestamp: new Date().toISOString(),
        endpoints: [
          "POST /api/stats/report-instance",
          "POST /api/webhooks/depotbox/logs",
          "POST /api/webhooks/depotbox/added",
          "POST /api/webhooks/depotbox/updated",
          "GET  /api/stats/trending",
          "GET  /api/stats/most-played",
          "GET  /api/feed/depotbox/added",
          "GET  /api/feed/depotbox/updated",
          "GET  /api/steam/lists?type=most_played|trending|top_sellers|top_rated"
        ]
      }), { headers: corsHeaders });

    } catch (err) {
      return new Response(JSON.stringify({ error: err.message }), { status: 500, headers: corsHeaders });
    }
  }
};

// ── RECORDING TELEMETRY & STATS IN KV ──────────────────────────────────────────

async function recordInstanceEvent(kv, appId, name) {
  if (!appId || appId <= 0) return;
  const now = Date.now();
  const key = `instance_${appId}`;

  try {
    const raw = await kv.get(key);
    let record = raw ? JSON.parse(raw) : { appId, name, timestamps: [], totalCount: 0 };

    if (name) record.name = name;
    record.totalCount = (record.totalCount || 0) + 1;
    record.timestamps = [...(record.timestamps || []), now];

    // Keep timestamps from the last 14 days to prevent storage bloat
    const fourteenDaysAgo = now - (14 * 24 * 60 * 60 * 1000);
    record.timestamps = record.timestamps.filter(ts => ts >= fourteenDaysAgo);

    await kv.put(key, JSON.stringify(record), { expirationTtl: 60 * 60 * 24 * 90 }); // 90 days retention
  } catch {}
}

async function getTrendingStats(kv) {
  if (!kv) return [];
  const now = Date.now();
  const sevenDaysAgo = now - (7 * 24 * 60 * 60 * 1000);
  const items = [];

  try {
    const listResult = await kv.list({ prefix: "instance_" });
    for (const key of listResult.keys.slice(0, 100)) {
      const raw = await kv.get(key.name);
      if (raw) {
        const record = JSON.parse(raw);
        const weeklyEvents = (record.timestamps || []).filter(ts => ts >= sevenDaysAgo);
        if (weeklyEvents.length > 0) {
          items.push({
            appId: record.appId,
            name: record.name || `App ${record.appId}`,
            appType: "Game",
            hasWindows: true,
            version: `${weeklyEvents.length} added this week`,
            weeklyScore: weeklyEvents.length,
            totalCount: record.totalCount || weeklyEvents.length,
            headerImageUrl: `https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/${record.appId}/header.jpg`
          });
        }
      }
    }
  } catch {}

  items.sort((a, b) => b.weeklyScore - a.weeklyScore);
  return deduplicate(items);
}

async function getMostAddedStats(kv) {
  if (!kv) return [];
  const items = [];

  try {
    const listResult = await kv.list({ prefix: "instance_" });
    for (const key of listResult.keys.slice(0, 100)) {
      const raw = await kv.get(key.name);
      if (raw) {
        const record = JSON.parse(raw);
        if ((record.totalCount || 0) > 0) {
          items.push({
            appId: record.appId,
            name: record.name || `App ${record.appId}`,
            appType: "Game",
            hasWindows: true,
            version: `${record.totalCount} community instances`,
            totalCount: record.totalCount,
            headerImageUrl: `https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/${record.appId}/header.jpg`
          });
        }
      }
    }
  } catch {}

  items.sort((a, b) => b.totalCount - a.totalCount);
  return deduplicate(items);
}

// ── DEPOTBOX WEBHOOK EXTRACTORS ───────────────────────────────────────────────

function extractSingleGameWebhook(body, defaultVersion) {
  if (!body) return null;

  if (body.appId) {
    return {
      appId: Number(body.appId),
      name: body.name || `App ${body.appId}`,
      version: body.buildId ? `Build ${body.buildId}` : defaultVersion,
      appType: body.appType || "Game",
      hasWindows: true,
      headerImageUrl: `https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/${body.appId}/header.jpg`
    };
  }

  if (body.embeds && Array.isArray(body.embeds) && body.embeds.length > 0) {
    const embed = body.embeds[0];
    const title = embed.title || embed.description || "";
    let appId = 0;
    const match = title.match(/(\d{3,9})/);
    if (match) appId = parseInt(match[1], 10);

    if (appId > 0) {
      return {
        appId,
        name: title.replace(/\[.*?\]|\(.*?\)/g, "").trim() || `App ${appId}`,
        version: defaultVersion,
        appType: "Game",
        hasWindows: true,
        headerImageUrl: `https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/${appId}/header.jpg`
      };
    }
  }

  return null;
}

function extractGamesFromLog(body) {
  const result = [];
  if (!body) return result;

  // If payload contains an array of log events or a single log object
  const entries = Array.isArray(body.logs) ? body.logs : [body];
  for (const entry of entries) {
    if (entry.appId) {
      result.push({
        appId: Number(entry.appId),
        name: entry.name || `App ${entry.appId}`
      });
    } else if (entry.message) {
      const match = entry.message.match(/(\d{3,9})/);
      if (match) {
        result.push({
          appId: parseInt(match[1], 10),
          name: entry.name || `App ${match[1]}`
        });
      }
    }
  }

  return result;
}

async function pushToFeed(kv, feedKey, item) {
  try {
    const raw = await kv.get(feedKey);
    let list = raw ? JSON.parse(raw) : [];
    list = [item, ...list.filter(x => x.appId !== item.appId)].slice(0, 50);
    await kv.put(feedKey, JSON.stringify(list));
  } catch {}
}

async function getFeed(kv, feedKey) {
  if (!kv) return [];
  try {
    const raw = await kv.get(feedKey);
    return raw ? JSON.parse(raw) : [];
  } catch {
    return [];
  }
}

// ── STEAM RANKINGS FETCHER (Proxy & Cache) ───────────────────────────────────

async function fetchSteamRankings(type) {
  try {
    const res = await fetch("https://store.steampowered.com/api/featuredcategories", {
      headers: { "User-Agent": "BlueStar/1.0" }
    });
    if (res.ok) {
      const data = await res.json();
      let rawItems = [];
      if (type === "top_sellers" && data.top_sellers?.items) {
        rawItems = data.top_sellers.items;
      } else if (type === "trending" && data.specials?.items) {
        rawItems = data.specials.items;
      } else if (type === "top_rated" && data.new_releases?.items) {
        rawItems = data.new_releases.items;
      } else if (data.top_sellers?.items) {
        rawItems = data.top_sellers.items;
      }

      if (rawItems.length > 0) {
        const mapped = rawItems.map(item => ({
          appId: item.id,
          name: item.name,
          appType: "Game",
          hasWindows: Boolean(item.windows_available),
          hasLinux: Boolean(item.linux_available),
          hasMac: Boolean(item.mac_available),
          headerImageUrl: item.header_image || `https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/${item.id}/header.jpg`
        }));
        return deduplicate(mapped);
      }
    }
  } catch {}

  return [];
}

function deduplicate(items) {
  const seen = new Set();
  const res = [];
  for (const item of items) {
    if (item && item.appId && !seen.has(item.appId)) {
      seen.add(item.appId);
      res.push(item);
    }
  }
  return res;
}
