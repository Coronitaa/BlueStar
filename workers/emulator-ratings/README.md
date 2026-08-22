# BlueStar Emulator Ratings Cloudflare Worker

API serverless en Cloudflare Workers para el sistema de puntuaciones y votación comunitaria de emuladores en **BlueStar Launcher**.

## Estructura de Endpoints

- `GET /health`: Estado del servicio.
- `GET /ratings?appId={appId}`: Devuelve las valoraciones de emuladores para el juego correspondiente.
  ```json
  {
    "refix_valve": { "positive": 49, "negative": 2 },
    "refix_goldberg": { "positive": 36, "negative": 3 }
  }
  ```
- `POST /vote`: Registra un voto de la comunidad.
  ```json
  // Request Body:
  {
    "appId": 286160,
    "optionId": "refix_valve",
    "isPositive": true
  }
  ```

## Despliegue con Wrangler

1. Iniciar sesión en Cloudflare:
   ```bash
   npx wrangler login
   ```
2. Crear el namespace de Cloudflare KV:
   ```bash
   npx wrangler kv namespace create RATINGS_KV
   ```
3. Copiar el `id` generado y pegarlo en `wrangler.toml` en el campo `id = "..."`.
4. Desplegar el Worker a Cloudflare:
   ```bash
   npx wrangler deploy
   ```