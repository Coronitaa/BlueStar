# BlueStar Search Engine Audit (FASE 0 — Baseline)

**Fecha**: 2026-09-23  
**Auditor**: Principal Software Engineer  
**Rama**: `main`  
**Solución**: `BlueStar.sln` (.NET 8.0)  
**Resultado Baseline de Tests**: 282 pasados / 282 totales (Core: 35, Infrastructure: 247, 0 fallidos)

---

## 1. Arquitectura Actual del Motor de Búsqueda / Explore

### 1.1 Diagrama de Flujo de Datos

```
[UI: BrowseView.xaml]
      │
      ▼ (Two-way binding: SearchQuery, Filters, Sort, Paging)
[ViewModel: BrowseViewModel.cs]
      │
      ├───────────────────────────────┬───────────────────────────────┐
      │ (Faceted search query)         │ (Lazy tag/metadata resolve)   │ (Catalog building)
      ▼                               ▼                               ▼
[ISteamCatalogSearchService]     [IMetadataProvider]          [ISteamTagCatalogService]
[SteamCatalogSearchService.cs]   [SteamStoreApiClient.cs]     [SteamTagCatalogService.cs]
      │                               │                               │
      ├───────────────────────────────┴───────────────────────────────┘
      ▼ (L1 Memory + L2 File cache)
[ICacheService / FileCacheService.cs]
      │ (Cache miss)
      ▼ (Semaphore & 1600ms pace)
[SteamRequestGate.cs]
      │
      ▼ (HTTP GET)
[Steam Web / Store Endpoints]
  - https://store.steampowered.com/search/results/
  - https://store.steampowered.com/api/featuredcategories/
  - https://store.steampowered.com/api/storesearch/
  - https://store.steampowered.com/api/appdetails
```

### 1.2 Componentes Identificados y Roles

1. **`BrowseView.xaml` / `BrowseView.xaml.cs`**:
   - Vista WPF con barra de búsqueda con debounce, selectores de orden (`SelectedSort`) y lista de tienda (`SelectedStoreList`), chips de filtros activos y panel lateral de filtros (`FilterGroups`).
   - Contenedor de resultados implementado con un `ItemsControl` estándar cuyo panel es un `WrapPanel` (modo grid) o `StackPanel` (modo lista). No dispone de virtualización de UI: cada elemento visual instanciado permanece vivo en memoria.
   - Estado vacío controlado mediante `MultiDataTrigger` sobre `IsSearching == False`, `IsLoadingMore == False` y `Results.Count == 0`.
   - Estado de carga mostrado mediante esqueletos (`SkeletonSlots`).

2. **`BrowseViewModel.cs`**:
   - Orquesta la búsqueda, gestión de filtros (`FilterGroups`), histórico de uso (`_facetUsage`), paginación por scroll infinito (`LoadMoreCommand`) y cola de enriquecimiento en segundo plano (`_enrichQueue`).
   - Implementa un sistema de "escalera" (`_ladder` y `SearchRung`) que genera combinaciones de tags si la búsqueda con todos los tags seleccionados no devuelve resultados.
   - Resuelve sugerencias de títulos llamando a `SuggestTitlesAsync` cuando no hay coincidencias directas.
   - Limita de manera artificial los resultados a 200 (`MaxMaterialized = 200`) expresamente debido a la falta de virtualización en el `WrapPanel`.

3. **`ISteamCatalogSearchService` / `SteamCatalogSearchService.cs`**:
   - Consume el endpoint HTML/JSON no documentado de Steam: `https://store.steampowered.com/search/results/?query=&term=...&category1=...&infinite=1&json=1`.
   - Parsea el fragmento `results_html` mediante expresiones regulares compiladas (`RowRegex`, `TitleRegex`, `ReviewRegex`, etc.).
   - Parsea `total_count` para la paginación.
   - Proporciona sondeo de conteo de productos (`GetMatchCountAsync`), eventos activos (`GetStoreEventsAsync`) y autocompletado (`SuggestTitlesAsync`).

4. **`SteamSearchQuery.cs`**:
   - Modela los parámetros de búsqueda enviados a Steam: `Term`, `AppTypes`, `SortBy`, `StoreList`, `Start`, `Count`, `Facets`, `RestrictToAppIds`.
   - Genera la URL con `ToUrl()`.

5. **`SteamStoreFacets.cs`**:
   - Define constantes y metadatos de tipos de aplicación, listas curadas, opciones de ordenamiento y facetas (jugadores, SO, accesibilidad, controles, etc.).

6. **`SteamRequestGate.cs`**:
   - Mecanismo estático de limitación de tasa con un `SemaphoreSlim(1, 1)` y espaciado de 1600ms entre solicitudes para `store.steampowered.com`.
   - Registra bloqueos consecutivos ante 429/403 con enfriamiento exponencial de 3 a 20 minutos.

7. **`ICacheService` / `FileCacheService.cs`**:
   - Cache de dos niveles: L1 en memoria (`ConcurrentDictionary`) con tope de 1000 entradas y L2 en disco guardando archivos JSON bajo `%APPDATA%/BlueStar/cache/{sha256}.json`.

8. **`IMetadataProvider` / `SteamStoreApiClient.cs`**:
   - Cliente para `https://store.steampowered.com/api/appdetails?appids={appId}`.
   - Provee datos enriquecidos: soporte de SO, DLC count, imágenes, descripción DRM, cuenta de terceros, géneros y descriptores NSFW.

---

## 2. Problemas Críticos Encontrados

| ID | Problema | Severidad | Archivo(s) Afectado(s) | Evidencia en Código |
|---|---|---|---|---|
| **BUG-01** | **Flicker severo en transiciones de búsqueda** | Alta | `BrowseViewModel.cs`, `BrowseView.xaml` | `BrowseViewModel.cs:655`: `if (Results.Count > 0) Results = [];`. `BrowseView.xaml:941`: `Condition Results.Count == 0 && IsSearching == False`. Al iniciar búsqueda se vacía la lista y se disparan triggers visuales antagónicos antes y durante la carga. |
| **BUG-02** | **Búsqueda por AppID inexistente / rota** | Alta | `BrowseViewModel.cs`, `SteamSearchQuery.cs`, `SteamCatalogSearchService.cs` | No existe detector ni parser para AppID numérico, ni prefijos `appid:`, `app:`, URLs de Steam o `steam://`. Se envían como `term=123` a Store Search, que devuelve juegos arbitrarios o vacíos en vez del juego exacto. |
| **BUG-03** | **API engañosa / código muerto: `RestrictToAppIds`** | Media | `SteamSearchQuery.cs` | `SteamSearchQuery.cs:53` define `RestrictToAppIds`, pero en `ToUrl()` (líneas 60-162) **nunca se serializa ni llega a la solicitud**. |
| **BUG-04** | **Confusión semántica grave: `HasExternalLauncher = HasDrm`** | Alta | `BrowseViewModel.cs`, `SteamStoreApiClient.cs` | `BrowseViewModel.cs:1230`: `item.HasExternalLauncher = item.HasDrm;`. Además, en `SteamStoreApiClient.cs:516`, `ext_user_account_notice` enciende `hasDrm = true`. Son conceptos distintos (launcher de terceros vs DRM anti-tamper). |
| **BUG-05** | **Dependencia absoluta de Steam Store Search HTML** | Crítica | `SteamCatalogSearchService.cs` | Si Steam está caído, bloqueado (403/429) o cambia la estructura de `results_html`, la búsqueda deja de funcionar completamente. No existe catálogo local maestro. |
| **BUG-06** | **Falta de virtualización UI y tope artificial de 200 items** | Alta | `BrowseView.xaml`, `BrowseViewModel.cs` | `BrowseView.xaml:888` usa `WrapPanel` sin virtualizar; cada card se materializa en el visual tree de WPF. Por eso en `BrowseViewModel.cs:192` se impone `MaxMaterialized = 200`. |
| **BUG-07** | **Ratings filtrados solo client-side sobre la página actual** | Alta | `BrowseViewModel.cs`, `SteamStoreFacets.cs` | `SteamStoreFacets.cs:328` marca Ratings como `LocalPostFilter`. `BrowseViewModel.cs:1073` los evalúa sólo sobre los 50 elementos devueltos por Steam. Si la primera página no tiene juegos 95%+, la vista queda vacía aunque existan miles de juegos elegibles. |
| **BUG-08** | **Sorts no soportados por Steam generan resultados engañosos** | Alta | `SteamStoreFacets.cs`, `BrowseViewModel.cs` | Steam ignora tokens como `Reviews_ASC`, `Name_DESC`, `Released_ASC`. Steam responde HTTP 200 con la lista desordenada o idéntica. BlueStar no detecta la anomalía ni ordena localmente. |
| **BUG-09** | **Liberación prematura del lease de limitación de tasa** | Alta | `SteamStoreApiClient.cs` | `SteamStoreApiClient.cs:73`: `lease.Dispose()` se invoca antes de hacer la llamada HTTP `_http.GetAsync()`. El semáforo se libera mientras el request sigue en curso, permitiendo múltiples requests concurrentes contra Steam. |
| **BUG-10** | **Falta de SingleFlight y Negative Caching en `ICacheService`** | Media | `FileCacheService.cs` | Peticiones concurrentes para el mismo AppID disparan múltiples requests HTTP reales idénticos. Respuestas vacías o fallidas no se cachean negativamente, martillando el rate limit. |
| **BUG-11** | **SteamRequestGate único para dominios distintos** | Media | `SteamRequestGate.cs` | `store.steampowered.com` y `api.steampowered.com` comparten la misma lógica y no tienen circuit breaker real ni jitter. |

---

## 3. Hipótesis de Causa Raíz

1. **Arquitectura orientada a páginas web externas en lugar de datos de catálogo**:
   Explore fue concebido originalmente como un visor rápido sobre `store.steampowered.com/search/results/`. Al crecer en funcionalidades (filtros DRM, rating, adult content), se intentó parchar con post-filtros locales aplicados sobre cada lote de 50 resultados, lo que destruye la coherencia de la paginación y el ordenamiento.

2. **Acoplamiento del estado visual al recuento de colecciones**:
   El parpadeo (flicker) surge porque el XAML vincula la visibilidad del "Empty State" directamente a `Results.Count == 0`, mientras que el ViewModel vacía la colección en `reset = true` antes de iniciar la petición asíncrona. La ausencia de una máquina de estados explícita (`SearchState`) provoca transiciones efímeras no deseadas.

3. **Asunciones sobre la fiabilidad semántica de HTTP 200 en Steam**:
   El código asume que si Steam responde HTTP 200 con HTML válido, el ordenamiento y los filtros solicitados fueron respetados, cuando en la práctica Steam descarta silenciosamente parámetros no reconocidos y devuelve la lista por defecto.

---

## 4. Estado de los Tests

### Tests Existentes
- **`BlueStar.Core.Tests`**: 35 tests (validación de modelos de instancias, rutas, accesos directos, orígenes de instancias).
- **`BlueStar.Infrastructure.Tests`**: 247 tests (parsers de DepotBox Lua, zip manifests, cache de archivos básico, emuladores, detección de motor, integración DepotDownloader).

### Tests Faltantes en el Subsistema de Búsqueda
- **`SteamCatalogSearchService`**: 0 tests.
- **`BrowseViewModel`**: 0 tests.
- **`QueryParser` / Detección de AppID**: 0 tests.
- **Ordenamiento y filtros de facetas**: 0 tests.
- **Detector de anomalías de Steam**: 0 tests.
- **Detección de 429 / 403 / Circuit breaker**: 0 tests.
- **SingleFlight / Negative caching**: 0 tests.
- **Virtualización / Integración offline**: 0 tests.

---

## 5. Estrategia de Solución

1. **Fase 1**: Resolver bugs urgentes (AppID parser determinista, corrección de `RestrictToAppIds`, eliminación de flicker con `SearchState`, separación de `HasExternalLauncher` y `HasDrm`).
2. **Fase 2-3**: Catálogo local en SQLite + FTS5 con snapshot offline y normalizador de texto.
3. **Fase 4-5**: Pipeline de búsqueda por capas (`ParseQuery` → `ResolveIntent` → `LocalCandidateSearch` → `Filter` → `Sort` → `Page` → `Enrich`) con soporte para 2 dropdowns consistentes.
4. **Fase 6**: `SteamResponseValidator` y `SteamResponseFingerprint` para detección de anomalías y degradación elegante.
5. **Fase 7-9**: Almacenamiento local de ratings (wilson/bayesian + steam original), enrichment lazy con prioridades, cache con L1/L2, SingleFlight, stale-while-revalidate y negative cache.
6. **Fase 10-12**: Rate Governor centralizado por dominio (`store` y `api`), Circuit Breaker, pools cacheados para StoreLists.
7. **Fase 13-15**: Virtualización en WPF (`VirtualizingWrapPanel`), SearchCoordinator con debounce seguro y soporte 100% offline.
8. **Fase 16-19**: Suite exhaustiva de pruebas unitarias/integración con fixtures, herramienta de smoke tests en vivo y documentación completa.
