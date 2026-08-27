# BlueStar Cloudflare Worker & DepotBox Webhook Setup

Este Cloudflare Worker proporciona el backend serverless para:
1. **Telemetría de Instancias de BlueStar**: Registra silenciosamente las instancias creadas por usuarios de BlueStar para calcular popularidad y tendencias.
2. **Recepción de Webhooks de DepotBox (Sin Bots)**: Ingiere juegos recién agregados, builds actualizadas y logs de uso directamente desde el panel de DepotBox.
3. **Agregaciones en Tiempo Real**: Calcula las categorías *Trending on BlueStar* (últimos 7 días) y *Most Added in BlueStar* (histórico global).
4. **Reenvío Opcional a Discord**: Si se configura `DISCORD_WEBHOOK_URL`, reenvía los webhooks a tu canal de Discord automáticamente.
5. **Caché Edge de Steam Rankings**: Provee respuestas ultra-rápidas para las 4 categorías de Steam.

---

## 📋 URLs para la Configuración de Webhooks en DepotBox

En tu panel de DepotBox (**Edit API Details**), pega las siguientes URLs:

| Campo en DepotBox | URL a ingresar | Descripción |
|---|---|---|
| **Added Game Webhook URL** | `https://bluestar-api-worker.blustar.workers.dev/api/webhooks/depotbox/added` | Alimenta la categoría *New Games in DepotBox* |
| **Updated Game Webhook URL** | `https://bluestar-api-worker.blustar.workers.dev/api/webhooks/depotbox/updated` | Alimenta la categoría *Updated Games in DepotBox* |
| **API usage logs webhook URL** | `https://bluestar-api-worker.blustar.workers.dev/api/webhooks/depotbox/logs` | Ingesta logs de actividad para calcular popularidad |

*(Si despliegas tu propio worker personalizado, reemplaza `bluestar-api-worker.blustar.workers.dev` con tu subdominio de Cloudflare Workers).*

---

## 🚀 Instrucciones de Despliegue en Cloudflare

### Opción 1: Despliegue Rápido vía Wrangler CLI

1. **Iniciar sesión en Cloudflare**:
   ```bash
   npx wrangler login
   ```
2. **Crear los Namespaces KV** (solo la primera vez):
   ```bash
   npx wrangler kv:namespace create STATS_KV
   npx wrangler kv:namespace create DEPOTBOX_KV
   ```
   Copia los IDs generados en tu archivo `wrangler.toml`.

3. **Desplegar**:
   ```bash
   npx wrangler deploy
   ```

---

## 🛰️ Resumen de Endpoints del Worker

- `POST /api/stats/report-instance` - Reporta adición de instancia desde el cliente (`{ appId, name }`)
- `POST /api/webhooks/depotbox/logs` - Procesa logs de uso de DepotBox
- `POST /api/webhooks/depotbox/added` - Ingesta nuevos juegos de DepotBox vía webhook
- `POST /api/webhooks/depotbox/updated` - Ingesta actualizaciones/parches de DepotBox vía webhook
- `GET  /api/stats/trending` - Juegos tendencia en BlueStar (últimos 7 días)
- `GET  /api/stats/most-played` - Juegos más agregados en BlueStar (histórico)
- `GET  /api/feed/depotbox/added` - Feed de nuevos juegos de DepotBox
- `GET  /api/feed/depotbox/updated` - Feed de juegos actualizados de DepotBox
- `GET  /api/steam/lists?type=most_played|trending|top_sellers|top_rated` - Rankings cacheados de Steam

