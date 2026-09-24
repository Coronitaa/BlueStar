# BlueStar Search Engine Audit (FASE 0 — Baseline & Real Code Verification)

**Fecha**: 2026-09-23  
**Auditor**: Principal Systems & Architecture Engineer  
**Rama**: `feature/search-engine-refactor`  
**Solución**: `BlueStar.sln` (.NET 8.0)  
**Baseline Test Results**: 387 pruebas pasadas (Core: 77, Infrastructure: 310, 0 fallidas)  
**Git Working Tree**: Limpio (HEAD: `746c6cb feat(search): complete refactor of search and browse engine`)

---

## 1. Resumen Ejecutivo de la Auditoría

El análisis estricto contra el código fuente real revela una disparidad crítica entre los componentes declarados en commits previos y su integración y comportamiento efectivo en tiempo de ejecución:

1. **Explore con query vacía sigue dependiendo de peticiones HTTP a Steam**:
   En `SearchPipeline.cs:74-77`, si no hay término de búsqueda, se ejecuta `ExecuteStoreBrowseAsync`, el cual realiza una petición HTTP directa a `https://store.steampowered.com/search/results/`. El catálogo local SQLite sólo se utiliza como fallback si Steam arroja una excepción de red.
2. **El catálogo SQLite local NO es un catálogo completo**:
   En una instalación limpia, la base de datos ni siquiera existe en `%APPDATA%\BlueStar\catalog\catalog.sqlite`. Sólo se puebla de forma reactiva con los resultados que Steam devuelve durante búsquedas y navegaciones.
3. **El servicio de snapshot (`SteamCatalogSnapshotService`) está desconectado y es parcial**:
   No está registrado en el contenedor de dependencias (`App.xaml.cs`) ni es invocado por ninguna parte de la aplicación. Además, su método de sincronización fuerza `AppType = "Game"` para todas las entradas de `GetAppList` e ignora tags, géneros, categorías y fechas de lanzamiento.
4. **Filtros aplicados sobre un subconjunto parcial de 50 elementos**:
   Los filtros de contenido y rating se evalúan en memoria en `BrowseViewModel.cs` sobre la colección `_fetched`. Si Steam devuelve 50 juegos y ninguno tiene rating >= 90%, el usuario ve 0 resultados, ignorando los cientos o miles de juegos calificados presentes en el índice local.
5. **Ordenamiento por fecha de lanzamiento es conceptualmente incorrecto**:
   `LocalCatalogRepository.cs:536` ordena por `a.last_modified` cuando se solicita `released`. No existe una columna de marca de tiempo (`release_date_utc` o `release_timestamp`). `ReleaseDate != LastModified`.
6. **Detector de anomalías recibe payloads falsificados**:
   `SearchPipeline.cs:396` y `SearchPipeline.cs:494` llaman a `_validator.ValidateResponse(..., "{\"results_html\":\"ok\"}", ...)` pasando un JSON artificial porque el payload real de Steam es descartado en `SteamCatalogSearchService.GetEnvelopeAsync`.
7. **Virtualización ausente y tope artificial `MaxMaterialized = 200` activo**:
   `VirtualizingWrapPanel.cs` existe en el proyecto, pero `BrowseView.xaml` usa un `WrapPanel` estándar no virtualizado dentro de un `ScrollViewer`. En consecuencia, `BrowseViewModel.cs:205` mantiene `MaxMaterialized = 200`, impidiendo la navegación del catálogo más allá de 200 elementos.
8. **Inundación de red por tags**:
   `SteamTagCatalogService.ResolveCountsAsync` ejecuta una solicitud HTTP de sondeo a Steam por cada tag visible, comprometiendo la cuota de peticiones.
9. **SingleFlight existe pero no está integrado**:
   `BlueStar.Core.Helpers.SingleFlight` tiene pruebas unitarias pero no se utiliza en `SteamCatalogSearchService` ni en `SteamStoreApiClient` (que usa una implementación separada llamada `RequestCoordinator`).

---

## 2. Diagramas del Flujo Actual

### 2.1 Flujo de Navegación / Búsqueda Actual (UI → Steam / Local)

```
[UI: BrowseView.xaml]
      │
      ▼ (Two-way binding: SearchQuery, Filters, SelectedSort, SelectedStoreList)
[BrowseViewModel.cs]
      │
      ▼ (req: SearchRequest)
[SearchPipeline.cs]
      │
      ├─────────────────────────────────────────────────────────────────────────────┐
      │ (A) ¿Query vacía?                                                          │ (B) ¿Tiene término de texto?
      ▼                                                                             ▼
[ExecuteStoreBrowseAsync]                                                  [LocalCatalogRepository.cs]
      │                                                                             │
      ├─► Consulta HTTP a Steam Search API (50 items)                               ├─► SQLite FTS5 (apps_fts)
      │   │                                                                         │
      │   ├─► Éxito: items parseados de HTML                                        ├─► ¿totalCount > 0?
      │   │   - Validador con fake JSON '{"results_html":"ok"}'                     │   ├─► SÍ: Devuelve resultados locales
      │   │   - Background upsert a SQLite                                          │   │
      │   │   - Sort local sobre 50 items (si es curated pool)                      │   └─► NO: [ExecuteSteamFallbackSearchAsync]
      │   │                                                                         │       - Consulta HTTP a Steam
      │   └─► Fallo de Red: Fallback a SQLite LocalCatalogRepository               │       - Background upsert a SQLite
      ▼                                                                             ▼
[BrowseViewModel._fetched] ◄────────────────────────────────────────────────────────┘
      │
      ▼ (Filtros en memoria: PassesLocalFilters sobre los items devueltos)
      │  - PassesRating (evalúa ReviewPercent sobre los <= 50 items recibidos)
      │  - HasDrm / HasExternalLauncher / Adult / DLC
      ▼
[BrowseViewModel.Results]
      │
      ▼ (ItemsControl con WrapPanel estándar - NO VIRTUALIZADO)
[Visual Tree de WPF] (Tope forzado: MaxMaterialized = 200)
```

---

## 3. Origen Real de los Resultados por Escenario

| Escenario | Origen Real de los Datos | Mecanismo | Fallas Identificadas |
|---|---|---|---|
| **a) Query vacía (Explore)** | **Steam HTTP Store Search** | Petición HTTP a `store.steampowered.com/search/results/`. Si falla la red, recurre a SQLite como fallback. | Dependencia de red; sólo se obtienen los primeros 50 elementos del pool de Steam; no usa el catálogo local completo como fuente primaria. |
| **b) Query con texto** | **SQLite FTS5 / Steam HTTP Fallback** | Consulta FTS5 en tabla `apps_fts`. Si hay coincidencias, devuelve local. Si hay 0, consulta Steam Store Search. | Funciona si la app ya fue cacheada; si el índice local está vacío o incompleto, consulta Steam. |
| **c) Query es AppID numérico** | **SQLite -> MetadataProvider -> DepotId -> Steam Search** | Primero busca en `LocalCatalogRepository.GetByAppIdAsync`. Si no existe, llama a `IMetadataProvider.GetMetadataAsync(appId)`. | Buen fallback, pero si la app no está en SQLite depende de la disponibilidad del endpoint de appdetails. |
| **d) Cambio de Filtro** | **Steam HTTP o SQLite + In-Memory Post-Filter** | Si la query está vacía, repite la llamada HTTP a Steam con facets. Luego `BrowseViewModel.RebuildVisible` aplica `PassesLocalFilters` sobre `_fetched`. | **Crítico**: Si la primera página de Steam de 50 items no contiene juegos que cumplan el filtro (ej. rating >= 90%), BlueStar muestra 0 resultados aunque en el catálogo haya miles. |
| **e) Cambio de Sort** | **Steam HTTP o SQLite ORDER BY** | Para query vacía, envía `sort_by` a Steam (o reordena los 50 items localmente si es curated pool). Para query con texto, aplica `ORDER BY` en SQLite. | **Crítico**: `ORDER BY a.last_modified` se usa para `released`. Steam a menudo ignora `Reviews_ASC` o `Name_DESC` respondiendo HTTP 200 con la misma lista sin ser detectado debidamente. |
| **f) Load More (Paginación)** | **Steam HTTP (start=N) o SQLite OFFSET** | Incrementa `Start = _fetched.Count`. Si la query está vacía, hace otra petición HTTP a Steam. | Provoca 1 HTTP request por cada página cuando se navega sin texto. Limitado rígidamente por `MaxMaterialized = 200`. |

---

## 4. Auditoría de Datos y Base de Datos Local

### 4.1 Estado Real de SQLite
- **Ubicación prevista**: `%APPDATA%\BlueStar\catalog\catalog.sqlite`.
- **Existencia en instalación real**: **NO EXISTE** inicialmente. SQLite no contiene un catálogo completo, sino únicamente una base de datos creada bajo demanda que acumula resultados descubiertos en búsquedas previas.
- **Estado de Snapshot**:
  - `SteamCatalogSnapshotService` **nunca se ejecuta**. No hay inyección de dependencias en `App.xaml.cs`.
  - No existe URL de distribución configurada para `catalog-manifest.json` o snapshots zst/sqlite.
  - No existe pipeline de descarga, validación SHA256 ni sustitución atómica activo en la aplicación.

### 4.2 Inventario de Campos en `LocalCatalogRepository` (`apps` table)

| Campo Requerido | Presencia en `apps` | Tipo / Detalle Técnico | Limitación Actual |
|---|---|---|---|
| **AppID** | SÍ | `app_id INTEGER PRIMARY KEY` | Correcto. |
| **Name** | SÍ | `name TEXT NOT NULL`, `normalized_name`, `compact_name` | Correcto. Indexado con FTS5. |
| **Type** | PARCIAL | `app_type TEXT NOT NULL` | Presente, pero `SteamCatalogSnapshotService` hardcodea `"Game"` para todas las apps al sincronizar. |
| **Tags** | PARCIAL | `tag_ids TEXT` | Columna existe (formato `,{id},`), pero los snapshots desde `GetAppList` la dejan vacía. |
| **Genres** | **NO** | Ausente en esquema | No existe columna para géneros. |
| **Categories** | **NO** | Ausente en esquema | No existe columna para categorías de Steam. |
| **ReleaseDate** | INCORRECTO | `release_date_text TEXT` | Solo guarda texto legible (ej. "Oct 24, 2023"). No hay timestamp numérico. El sort usa `last_modified`. |
| **ReviewPercent** | SÍ | `review_percent INTEGER` | Presente (0-100). |
| **ReviewCount** | SÍ | `review_count INTEGER` | Presente. |
| **Price** | PARCIAL | `price_text TEXT` | Texto libre (ej. "$19.99"). Sort en SQLite hace conversiones de string complejas y propensas a fallos. |
| **Discount** | SÍ | `discount_percent INTEGER` | Presente (0-100). |
| **PlayerCount** | **NO** | Ausente en esquema | No existe almacenamiento de jugadores concurrentes. |
| **Platforms** | SÍ | `has_windows`, `has_mac`, `has_linux INTEGER` | Presente. |
| **DRM** | SÍ | `has_drm INTEGER` | Presente. |
| **ExternalLauncher** | SÍ | `has_external_launcher INTEGER` | Presente. |
| **DLC** | PARCIAL | `positive_reviews`, `negative_reviews` | No se almacena recuento de DLC ni relación padre/hijo en la tabla `apps`. |

---

## 5. Auditoría de UI: Virtualización y Ciclo de Vida de Búsqueda

### 5.1 Estado de Virtualización
- El control `VirtualizingWrapPanel.cs` existe en `src/BlueStar.App/Controls/VirtualizingWrapPanel.cs`.
- Sin embargo, en `src/BlueStar.App/Views/BrowseView.xaml:908-933`:
  ```xml
  <ItemsControl ItemsSource="{Binding Results}">
      <ItemsControl.Style>
          <Style TargetType="ItemsControl">
              <Setter Property="ItemTemplate" Value="{StaticResource ResultCardTemplate}"/>
              <Setter Property="ItemsPanel">
                  <Setter.Value>
                      <ItemsPanelTemplate>
                          <WrapPanel Orientation="Horizontal"/>
                      </ItemsPanelTemplate>
                  </Setter.Value>
              </Setter>
          </Style>
      </ItemsControl.Style>
  </ItemsControl>
  ```
- **Conclusión**: El visual tree de WPF materializa **cada una de las tarjetas** en memoria. Por este motivo, `BrowseViewModel.cs:205` impone `private const int MaxMaterialized = 200;` cortando artificialmente los resultados.

### 5.2 Ciclo de Vida de `SearchState` y Parpadeo (Flicker)
- En `BrowseViewModel.RunSearchAsync(reset: true)`:
  - `_fetched.Clear()` borra los datos internos.
  - Si `Results.Count == 0`, `SearchState = LoadingInitial` (muestra esqueletos).
  - Si `Results.Count > 0`, `SearchState = Refreshing` (mantiene resultados previos y muestra un spinner).
  - Al completar: `SearchState = Results.Count > 0 ? SearchState.ShowingResults : SearchState.Empty;`
  - Esto evita el parpadeo en búsquedas con resultados, pero si un filtro elimina los 50 elementos de la página de Steam, pasa a `Empty` de forma errónea.

---

## 6. Inventario de Componentes y Estado de Integración

| Componente | Estado de Integración | Observaciones |
|---|---|---|
| `BrowseViewModel` | IMPLEMENTADO Y USADO | Mantiene `MaxMaterialized=200`, filtra client-side sobre lote parcial, procesa cola de counts HTTP. |
| `BrowseView.xaml` | IMPLEMENTADO Y USADO | No usa `VirtualizingWrapPanel`; usa `WrapPanel` estándar. |
| `SearchPipeline` | IMPLEMENTADO Y USADO | Browse sin query va directo a Steam; usa payload falso para validación. |
| `LocalCatalogRepository` | IMPLEMENTADO Y USADO | FTS5 funcional, pero falta esquema de fecha numérica, tags completos y completitud de catálogo. |
| `SteamCatalogSnapshotService` | IMPLEMENTADO PERO NO INTEGRADO | No registrado en DI, sin URL de distribución, no se ejecuta en arranque. |
| `SteamCatalogSearchService` | IMPLEMENTADO Y USADO | Descarta payload JSON crudo en `GetEnvelopeAsync`. |
| `SteamRequestGate` / `DomainRateGovernor` | IMPLEMENTADO Y USADO | Circuit breaker por dominio y límite de tasa implementados con tests. |
| `SingleFlight` | IMPLEMENTADO PERO NO INTEGRADO | Clase en Core con pruebas unitarias, pero huérfana en el pipeline de búsqueda. |
| `RatingEngine` | PARCIAL | Cálculo Wilson implementado pero no conectado al ranking de búsqueda; solo se usa `GetReviewSummary`. |
| `VirtualizingWrapPanel` | IMPLEMENTADO PERO NO INTEGRADO | Clase en Controls, pero nunca instanciada en BrowseView.xaml. |
| `SteamResponseValidator` | PARCIAL | La lógica existe, pero recibe payloads hardcodeados `"{\"results_html\":\"ok\"}"`. |

---

## 7. Plan de Acción Técnico Hacia la Arquitectura Objetivo

Para cumplir la especificación completa, el pipeline de Explore debe evolucionar hacia:

```
CATÁLOGO MAESTRO (Snapshot SQLite + Manifest)
        │
        ▼
ÍNDICE LOCAL (SQLite FTS5 + Índices Numéricos de Rating/Release/Price/Tags)
        │
        ▼
EVALUADOR DE COMPLETITUD (CatalogCompleteness)
        │  ├─► IdentityComplete
        │  ├─► MetadataComplete
        │  └─► Local Authoritative vs Fallback Mode
        ▼
PIPELINE DE BÚSQUEDA:
ParseQuery → ResolveIntent → CandidateUniverse (Local) → Filter → Sort → Pagination
        │
        ▼
FALLBACK A STEAM (Solo cuando la query es desconocida o metadata esencial no está indexada)
        │
        ▼
ENRIQUECIMIENTO LAZY PRIORIZADO (P0: Detalle activo, P1: Filtros, P2: Visible)
        │
        ▼
VISTA WPF (VirtualizingWrapPanel real conectado, sin límite artificial de 200)
```
