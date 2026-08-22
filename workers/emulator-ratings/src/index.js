/**
 * BlueStar Emulator Ratings & Community Voting Service
 * Built for Cloudflare Workers & Cloudflare KV
 */

const CORS_HEADERS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
  "Access-Control-Allow-Headers": "Content-Type, Authorization, X-Requested-With",
  "Content-Type": "application/json; charset=utf-8"
};

export default {
  async fetch(request, env, ctx) {
    // Handle CORS preflight
    if (request.method === "OPTIONS") {
      return new Response(null, {
        status: 204,
        headers: CORS_HEADERS
      });
    }

    const url = new URL(request.url);
    const path = url.pathname.toLowerCase();

    try {
      // ── Health Check ──
      if (path === "/" || path === "/health") {
        return new Response(JSON.stringify({
          status: "online",
          service: "bluestar-emulator-ratings",
          timestamp: new Date().toISOString()
        }), {
          status: 200,
          headers: CORS_HEADERS
        });
      }

      // ── GET /ratings?appId=12345 ──
      if (request.method === "GET" && (path === "/ratings" || path === "/api/ratings")) {
        const appIdStr = url.searchParams.get("appId") || url.searchParams.get("appid");
        if (!appIdStr) {
          return new Response(JSON.stringify({ error: "Missing required parameter 'appId'" }), {
            status: 400,
            headers: CORS_HEADERS
          });
        }

        const appId = parseInt(appIdStr, 10);
        if (isNaN(appId) || appId <= 0) {
          return new Response(JSON.stringify({ error: "Invalid 'appId'" }), {
            status: 400,
            headers: CORS_HEADERS
          });
        }

        const ratings = await getRatingsForApp(env, appId);
        return new Response(JSON.stringify(ratings), {
          status: 200,
          headers: CORS_HEADERS
        });
      }

      // ── POST /vote ──
      if (request.method === "POST" && (path === "/vote" || path === "/api/vote")) {
        let body;
        try {
          body = await request.json();
        } catch {
          return new Response(JSON.stringify({ error: "Invalid JSON body" }), {
            status: 400,
            headers: CORS_HEADERS
          });
        }

        const { appId, optionId, isPositive } = body;
        if (!appId || !optionId || typeof isPositive !== "boolean") {
          return new Response(JSON.stringify({
            error: "Missing or invalid payload. Expected { appId: number, optionId: string, isPositive: boolean }"
          }), {
            status: 400,
            headers: CORS_HEADERS
          });
        }

        const updated = await recordVote(env, appId, optionId, isPositive);
        return new Response(JSON.stringify({
          success: true,
          appId,
          optionId,
          ratings: updated
        }), {
          status: 200,
          headers: CORS_HEADERS
        });
      }

      // 404 Route Not Found
      return new Response(JSON.stringify({ error: "Endpoint not found" }), {
        status: 404,
        headers: CORS_HEADERS
      });
    } catch (err) {
      return new Response(JSON.stringify({
        error: "Internal server error",
        message: err.message
      }), {
        status: 500,
        headers: CORS_HEADERS
      });
    }
  }
};

/**
 * Retrieves ratings for an AppID from KV (with baseline seed fallbacks).
 */
async function getRatingsForApp(env, appId) {
  const kvKey = `ratings:${appId}`;
  
  if (env.RATINGS_KV) {
    try {
      const stored = await env.RATINGS_KV.get(kvKey, { type: "json" });
      if (stored) return stored;
    } catch (e) {
      console.warn("KV read failed:", e);
    }
  }

  // Baseline defaults if not yet voted
  return {
    "refix_valve": {
      "positive": 0,
      "negative": 0
    },
    "refix_goldberg": {
      "positive": 0,
      "negative": 0
    }
  };
}

/**
 * Records a positive or negative vote in KV.
 */
async function recordVote(env, appId, optionId, isPositive) {
  const kvKey = `ratings:${appId}`;
  let currentRatings = await getRatingsForApp(env, appId);

  if (!currentRatings[optionId]) {
    currentRatings[optionId] = { positive: 0, negative: 0 };
  }

  if (isPositive) {
    currentRatings[optionId].positive = (currentRatings[optionId].positive || 0) + 1;
  } else {
    currentRatings[optionId].negative = (currentRatings[optionId].negative || 0) + 1;
  }

  if (env.RATINGS_KV) {
    try {
      await env.RATINGS_KV.put(kvKey, JSON.stringify(currentRatings));
    } catch (e) {
      console.error("KV write failed:", e);
    }
  }

  return currentRatings;
}