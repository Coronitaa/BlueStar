# Estado Real del Motor de Búsqueda y Catálogo de BlueStar (Fase 0 — Auditoría Baseline)

> **Criterio rector de auditoría:**
> **IMPLEMENTADO ≠ INTEGRADO ≠ FUNCIONANDO ≠ VERIFICADO END-TO-END**
>
> Este documento describe la **realidad técnica verificada contra el código y runtime**, y no las intenciones ni afirmaciones de reportes previos.

---

## 1. Resumen Ejecutivo de la Auditoría

A pesar de que reportes anteriores (`docs/search-engine-final-report.md`) afirmaron que la arquitectura estaba "COMPLETED" y los 411 tests unitarios pasan, la auditoría profunda del código fuente y del entorno de ejecución revela fallas estructurales graves:

1. **Explore All depende de Steam Store Search:** Cuando la base de datos local no alcanza una completitud autoritativa o cuando la consulta es vacía, el sistema delega en `ISteamCatalogSearchService` mediante `https://store.steampowered.com/search/results/`, recibiendo únicamente la primera página de 50 resultados de Steam.
2. **Sort y Paginación locales actúan sobre los 50 resultados de Steam:** En lugar de ordenar el universo completo de juegos de Steam, `SearchPipeline.cs:462` ejecuta `ApplyLocalSort` sobre los 50 elementos devueltos por el HTTP request de Steam, reportando un `TotalCount` engañoso de ~30.000.
3. **Pérdida masiva de filtros entre UI y SearchRequest:** `BrowseViewModel.cs:702-713` **NO propaga** los filtros de Calificación (`MinRatingPercent`, `MaxRatingPercent`), Plataforma (`HasWindows`, `HasMac`, `HasLinux`), DRM (`NoDrm`), Launcher externo (`NoExternalLauncher`), Etiquetas excluidas (`ExcludedTagIds`), ni Descuentos (`DiscountedOnly`) al `SearchRequest`. Estos filtros se evalúan en memoria en WPF (`BrowseViewModel.cs:1247-1273`) sobre el lote parcial de 50 elementos ya descargados, produciendo listas vacías artificiales.
4. **`SteamCatalogSnapshotService` está huérfano:** La clase existe en `src/BlueStar.Infrastructure/Catalog/SteamCatalogSnapshotService.cs`, pero **no está registrada en el contenedor de dependencias (`App.xaml.cs`)**, no tiene URL de distribución remota configurada, no se ejecuta en el arranque del cliente y no posee ningún test unitario o de integración.
5. **Completitud arbitraria por conteo (`>= 100`):** `CatalogCompleteness.cs:24` y `LocalCatalogRepository.cs:803` declaran `IdentityComplete = total >= 100` y `IsAuthoritative = IdentityComplete && TotalIndexedApps >= 100`. Con solo 101 juegos, el sistema asume erróneamente que conoce todo el catálogo de Steam (~100.000+ juegos).
6. **Virtualización inexistente y límite artificial `MaxMaterialized = 500`:** 
   - `src/BlueStar.App/Controls/VirtualizingWrapPanel.cs` es una clase vacía de 12 líneas que hereda de `WrapPanel` sin implementar `VirtualizingPanel`.
   - `BrowseView.xaml:915` ni siquiera usa dicha clase: utiliza un `<WrapPanel Orientation="Horizontal"/>` estándar no virtualizado dentro de un `<ScrollViewer>`.
   - Como consecuencia directa, `BrowseViewModel.cs:204` mantiene `private const int MaxMaterialized = 500;`, cortando artificialmente la navegación del usuario.
7. **Inundación de red por contadores de tags:** `BrowseViewModel.cs:561-570` encola cada tag visible y dispara **1 petición HTTP a Steam por cada tag** (`GetMatchCountAsync`), a pesar de que `LocalCatalogRepository.cs:816` ya dispone de `GetTagCountsAsync` para computar los conteos en SQLite en milisegundos con 0 requests.
8. **Base de datos real en runtime raquítica:** La base de datos SQLite real en `%APPDATA%\BlueStar\catalog.db` contiene únicamente **1.053 registros**, de los cuales el 100% tiene `review_count = NULL`, 416 tienen `release_date_utc = NULL` y `last_modified = 0`.
9. **Payload artificial en validación:** `SearchPipeline.cs:444` y `542` pasan `"{\"results_html\":\"ok\"}"` como sustituto artificial cuando `RawPayload` es nulo (por ejemplo, en items provenientes de cache).
10. **Anomalías detectadas pero no resueltas:** `SteamResponseValidator.cs` detecta cuando Steam ignora el orden (e.g. `Reviews_ASC` vs `Reviews_DESC`), pero `SearchPipeline.cs` solo anota el mensaje de anomalía sin redirigir la consulta al universo autoritativo local para resolver el orden real.

---

## 2. Línea Base del Entorno

- **Branch actual:** `feature/search-engine-refactor`
- **Estado de Git:** Clean working tree.
- **Compilación (`dotnet build`):** Exitosa (0 advertencias, 0 errores).
- **Ejecución de Tests (`dotnet test`):**
  - `BlueStar.Core.Tests`: 77 superados, 0 errores.
  - `BlueStar.Infrastructure.Tests`: 334 superados, 0 errores.
  - Total: 411 tests pasando.
  - *Diagnóstico:* Los tests actuales pasan porque prueban componentes en aislamiento con fakes o aserciones complacientes, pero **no verifican la integración end-to-end** entre `BrowseViewModel`, `SearchPipeline`, `LocalCatalogRepository` y la UI de WPF.

---

## 3. Auditoría de Archivos Clave

| Componente | Ruta | Estado Real | Problema Identificado |
|---|---|---|---|
| `BrowseViewModel` | `src/BlueStar.App/ViewModels/BrowseViewModel.cs` | IMPLEMENTADO Y USADO | No mapea filtros clave a `SearchRequest`. Mantiene `MaxMaterialized = 500`. Encola peticiones HTTP 1-a-1 para conteo de tags. Filtra en memoria client-side sobre 50 items. |
| `BrowseView.xaml` | `src/BlueStar.App/Views/BrowseView.xaml` | IMPLEMENTADO Y USADO | Usa `WrapPanel` estándar dentro de `ScrollViewer`. No virtualiza. Materializa todos los elementos en memoria. |
| `VirtualizingWrapPanel` | `src/BlueStar.App/Controls/VirtualizingWrapPanel.cs` | IMPLEMENTADO PERO NO INTEGRADO / FALSO | Es un cascarón vacío: `class VirtualizingWrapPanel : WrapPanel { }`. No implementa virtualización y la vista no lo referencia. |
| `SearchPipeline` | `src/BlueStar.Infrastructure/Search/SearchPipeline.cs` | IMPLEMENTADO Y USADO | Llama a Steam Store Search en query vacía si catálogo no es "autoritativo". Aplica sort sobre los primeros 50 de Steam. Inyecta `{"results_html":"ok"}` artificial en validación. No resuelve anomalías de sort. |
| `SearchRequest` | `src/BlueStar.Core/Models/SearchPipelineModels.cs` | IMPLEMENTADO | Contiene campos como `MinRatingPercent`, `HasWindows`, `NoDrm`, etc., pero el ViewModel los deja en `null`. |
| `QueryParser` (`SteamQueryParser`) | `src/BlueStar.Core/Helpers/SteamQueryParser.cs` | IMPLEMENTADO Y FUNCIONAL | Parsea `appid:`, `app:`, URLs de Steam y términos normalizados correctamente. |
| `LocalCatalogRepository` | `src/BlueStar.Infrastructure/Catalog/LocalCatalogRepository.cs` | IMPLEMENTADO Y PARCIAL | FTS5 y tablas existen. Sin embargo, `GetCompletenessAsync` infiere completitud con `total >= 100`. |
| `SteamCatalogSnapshotService` | `src/BlueStar.Infrastructure/Catalog/SteamCatalogSnapshotService.cs` | IMPLEMENTADO PERO HUÉRFANO | No registrado en DI (`App.xaml.cs`). No se invoca en ningún lugar de la aplicación. No soporta descompresión `.zst`. |
| `SteamCatalogSearchService` | `src/BlueStar.Infrastructure/Steam/SteamCatalogSearchService.cs` | IMPLEMENTADO Y USADO | Cliente HTTP hacia store.steampowered.com. `CachedPage` descarta `RawPayload`. |
| `SteamResponseValidator` | `src/BlueStar.Infrastructure/Steam/SteamResponseValidator.cs` | IMPLEMENTADO | Detecta anomalías pero `SearchPipeline` no las resuelve sobre el catálogo local. |
| `SteamRequestGate` | `src/BlueStar.Infrastructure/Steam/SteamRequestGate.cs` | IMPLEMENTADO | Limita concurrencia y maneja backoff de 429/403. |
| `CatalogCompleteness` | `src/BlueStar.Core/Models/CatalogCompleteness.cs` | IMPLEMENTADO PERO DEFECTUOSO | `IsAuthoritative => IdentityComplete && TotalIndexedApps >= 100;`. Umbral arbitrario sin noción del catálogo real de Steam. |

---

## 4. Estado de SQLite en Runtime

Se inspeccionó la base de datos real del usuario ubicada en `%APPDATA%\BlueStar\catalog.db` (581.632 bytes):
- **Total de registros en `apps`:** **1.053 apps** (Steam tiene más de 100.000).
- **Tipos de App:**
  - `Game`: 821
  - `Software`: 229
  - `Application`: 3
- **Campos nulos / incompletos:**
  - `review_count`: **0 registros con datos** (100% NULL).
  - `review_percent`: Poblado, pero sin review_count no se puede calcular Wilson ni separar Rating de Reviews.
  - `release_date_utc`: 637 con fecha, **416 NULL**.
  - `last_modified`: **0** en todos los registros existentes.
- **Manifest / Versión:** Inexistente. No hay tabla de metadatos de sincronización ni registro de versión de snapshot en SQLite.

---

## 5. Auditoría del Ciclo de Vida del Snapshot

| Etapa | Estado | Detalle |
|---|---|---|
| **Generación** | Parcial | `SteamCatalogSnapshotService.SyncFromSteamWebToRepositoryAsync` implementa paginación sobre `IStoreService/GetAppList/v1`, pero no existe CLI ni workflow que emita `catalog-manifest.json` y `catalog-vN.sqlite.zst`. |
| **Distribución** | Inexistente | No hay endpoint, bucket ni CDN configurado para la distribución de snapshots versionados. |
| **Descarga** | Huérfano | `CheckAndUpdateSnapshotAsync` existe en código pero nunca se ejecuta ni se inyecta en el runtime. |
| **Importación** | Huérfano | `LocalCatalogRepository.ImportSnapshotAsync` tiene validación `quick_check` y reemplazo atómico, pero nadie lo llama en runtime. |
| **Activación** | Inexistente | La app siempre arranca contra el archivo local previo sin verificar versiones. |

---

## 6. Flujo Real de Explore por Escenarios (A - J)

### A. Query Vacía (Explore All)
1. `BrowseViewModel.RunSearchAsync(reset: true)` crea `SearchRequest` con `RawQuery = ""`, `Pool = "all"`.
2. `SearchPipeline.ExecuteAsync` detecta término vacío y llama a `ExecuteStoreBrowseAsync`.
3. `ExecuteStoreBrowseAsync` evalúa `_localRepo.GetCompletenessAsync()`.
4. Como `TotalIndexedApps >= 100`, cree erróneamente que es autoritativo y consulta `_localRepo.QueryAsync`.
5. Si la base local no estuviera presente o si se usa un pool curado (`popularnew`, etc.), llama inmediatamente a `SteamCatalogSearchService.SearchAsync`.
6. En caso de llamar a Steam: devuelve 50 items, aplica sort local sobre esos 50 y fija `TotalCount = 30000`.
7. En la UI, `MaxMaterialized = 500` impide avanzar más allá de 500 elementos.

### B. Query "Portal"
1. `SteamQueryParser.Parse("Portal")` produce `NormalizedTerm = "portal"`.
2. `SearchPipeline` ejecuta `_localRepo.QueryAsync` con FTS5 (`apps_fts MATCH 'portal*'`).
3. Si está en local, retorna los resultados locales. Si la base local estuviera vacía, cae en fallback a Steam (`ExecuteSteamFallbackSearchAsync`) y guarda en background en SQLite.

### C. Query "570"
1. `SteamQueryParser.Parse("570")` lo identifica como `AppId = 570`.
2. `SearchPipeline.ResolveAppIdIntentAsync`:
   - Busca en `_localRepo.GetByAppIdAsync(570)`.
   - Si no está, llama a `_metadataProvider.GetMetadataAsync(570)` (appdetails oficial de Steam).
   - Si no está, prueba como DepotID.
   - Si no está, busca en Steam Catalog Search.
3. El resultado se persiste en SQLite.

### D. Query "appid:570"
1. `SteamQueryParser.Parse("appid:570")` extrae el prefijo `appid:` y obtiene `AppId = 570`.
2. Sigue exactamente el mismo flujo determinista que el caso C.

### E. Cambiar Sort (e.g. Nombre ASC / Reviews DESC)
1. El usuario cambia el ComboBox de Sort.
2. `BrowseViewModel.RunSearchAsync(reset: true)` pasa `SortBy = "name"` o `"reviews"`, `Descending = true/false`.
3. Si se resuelve en local: SQLite ejecuta `ORDER BY a.name ASC LIMIT 50`.
4. Si se resuelve contra Steam: Steam devuelve 50 resultados, y `SearchPipeline.ApplyLocalSort` reordena **solamente esos 50 resultados**, en lugar de traer los primeros 50 globales.
5. Para `Reviews_ASC`: Steam ignora el parámetro ASC y devuelve los mismos juegos que DESC. `SteamResponseValidator` detecta la anomalía, pero el pipeline **no ejecuta una consulta autoritativa local** para obtener los juegos de menor rating reales.

### F. Cambiar Rating (e.g. Rating >= 95%)
1. El usuario hace click en el filtro de rating en el panel lateral.
2. `FilterGroupViewModel` activa la opción `"rating_min_95"`.
3. `BrowseViewModel.RunSearchAsync`: **NO INCLUYE** `MinRatingPercent` en `SearchRequest`.
4. `SearchPipeline` recibe `MinRatingPercent = null`.
5. SQLite o Steam devuelven resultados sin filtrar por rating.
6. `BrowseViewModel.RebuildVisible` ejecuta `PassesRating(item, rating)` en memoria de WPF sobre los 50 elementos recibidos.
7. Si ninguno de los 50 items tiene >= 95%, la UI muestra "No results found", ignorando que en la base de datos pueden existir miles de juegos que cumplen el criterio.

### G. Cambiar Tags (Include / Exclude)
1. **Include:** Se agrega el ID a `_selectedTagIds` y se envía en `SearchRequest.IncludedTagIds`. SQLite filtra con `tag_ids LIKE '%,ID,%'`.
2. **Exclude (Click derecho / Ban):** El tag pasa a estado `Exclude`. `BrowseViewModel.RunSearchAsync` **NUNCA envía `ExcludedTagIds`** a `SearchRequest`. El filtro de exclusión se pierde por completo.
3. **Contadores de Tags:** Al abrir un grupo de tags, `RequestCountsFor` encola cada tag. `DrainCountQueueAsync` ejecuta **1 petición HTTP a Steam por cada tag**, congestionando la red.

### H. Cambiar Plataforma (Windows / macOS / Linux)
1. El usuario selecciona la plataforma en el panel lateral.
2. `BrowseViewModel.RunSearchAsync`: **NO ASIGNA** `HasWindows`, `HasMac`, ni `HasLinux` en `SearchRequest`.
3. La consulta se ejecuta sin restricciones de plataforma.
4. El filtro es completamente ignorado.

### I. Load More (Paginación)
1. El usuario hace scroll hacia el final.
2. Se activa `LoadMoreAsync()` -> `RunSearchAsync(reset: false)`.
3. `req.Start = _fetched.Count`.
4. Si `_fetched.Count >= MaxMaterialized` (500), `HasMoreResults` se vuelve `false` y **se bloquea la navegación**.
5. Si no se ha alcanzado el tope, solicita la siguiente página. Si está en local, ejecuta `OFFSET 50 LIMIT 50`. Si está en Steam, hace una petición HTTP `start=50`.

### J. Refresh
1. El usuario refresca o tipea una nueva búsqueda.
2. `_searchGeneration++`. `_fetched.Clear()`.
3. Si `Results.Count > 0`, se coloca en `SearchState = Refreshing` para no borrar inmediatamente las tarjetas y evitar el parpadeo de pantalla negra.
4. Al llegar el nuevo lote, `RebuildVisible` actualiza la colección observable `Results`.

---

## 7. Matriz de Propagación de Filtros (UI → SearchRequest → Pipeline → Repo)

| Filtro en UI | ¿Sale de UI? | ¿Llega a SearchRequest? | ¿Llega a SearchPipeline? | ¿Llega a LocalRepo SQL? | Estado |
|---|---|---|---|---|---|
| **SearchQuery (Texto)** | Sí | Sí (`RawQuery`) | Sí | Sí (`MATCH` FTS5) | **CONECTADO** |
| **AppType (Games/Software)** | Sí | Sí (`AppTypes`) | Sí | Sí (`LOWER(app_type)`) | **CONECTADO** |
| **Sort By (Campo)** | Sí | Sí (`SortBy`) | Sí | Sí (`ORDER BY`) | **CONECTADO** |
| **Sort Direction (ASC/DESC)** | Sí | Sí (`Descending`) | Sí | Sí (`ASC/DESC`) | **CONECTADO** |
| **Store List (Pool)** | Sí | Sí (`Pool`) | Sí | No aplicable (Pool live) | **CONECTADO** |
| **Included Tags** | Sí | Sí (`IncludedTagIds`) | Sí | Sí (`LIKE %,id,%`) | **CONECTADO** |
| **Excluded Tags (Ban)** | Sí en UI | **NO (Se descarta)** | No (`null`) | No implementado en query | **ROTO / DESCONECTADO** |
| **Rating (Min/Max)** | Sí en UI | **NO (Se descarta)** | No (`null`) | Filtrado en memoria WPF | **ROTO / DESCONECTADO** |
| **Platform (Win/Mac/Linux)** | Sí en UI | **NO (Se descarta)** | No (`null`) | No implementado en query | **ROTO / DESCONECTADO** |
| **No DRM** | Sí en UI | **NO (Se descarta)** | No (`null`) | Filtrado en memoria WPF | **ROTO / DESCONECTADO** |
| **No 3rd-Party Launcher** | Sí en UI | **NO (Se descarta)** | No (`null`) | Filtrado en memoria WPF | **ROTO / DESCONECTADO** |
| **Discounted Only (Specials)** | Sí en UI | **NO (Se descarta)** | No (`null`) | Filtrado en memoria WPF | **ROTO / DESCONECTADO** |
| **NSFW / Adult** | Settings | Sí (`HideAdult`) | Sí | Sí (`is_nsfw = 0`) | **CONECTADO** |

---

## 8. Diagnóstico de la Virtualización de UI

- **Archivo inspeccionado:** `src/BlueStar.App/Controls/VirtualizingWrapPanel.cs`
  ```csharp
  public class VirtualizingWrapPanel : WrapPanel
  {
  }
  ```
  Es una clase sin lógica que hereda del `WrapPanel` estándar de WPF. No implementa `VirtualizingPanel`, no gestiona `ItemContainerGenerator` ni recicla contenedores visuales.
- **Archivo inspeccionado:** `src/BlueStar.App/Views/BrowseView.xaml:907-933`
  ```xml
  <ItemsControl ItemsSource="{Binding Results}">
      <ItemsControl.Style>
          <Style TargetType="ItemsControl">
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
  La vista utiliza un `WrapPanel` común.
- **Contenedor:** El `ItemsControl` está envuelto directamente en un `<ScrollViewer Grid.Column="0" x:Name="ResultsScroller">`. Sin `VirtualizingPanel.IsVirtualizing="True"` y sin un panel verdaderamente virtualizado que implemente `IScrollInfo`, WPF materializa el 100% de los elementos visuales en el árbol visual, consumiendo cientos de megabytes de RAM si la lista crece.
- **Tope artificial:** Por esta razón exacta, `BrowseViewModel.cs:204` mantiene `private const int MaxMaterialized = 500;`.

---

## 9. Plan de Acción Derivado de la Auditoría

Para resolver definitivamente el motor de búsqueda y Explore de acuerdo con el objetivo final:

1. **Fase 1:** Construir tests de regresión que reproduzcan el catálogo de 100.000 apps y demuestren cada uno de los fallos auditados (Explore All local con FakeSteam fallando si se le llama, UI filter propagation, resolución de anomalías, etc.).
2. **Fase 2 y 3:** Integrar el modelo de catálogo completo (`IStoreService/GetAppList`), snapshots versionados con checksum SHA256 y soporte Zstandard, registrando `ICatalogSnapshotService` en runtime (`App.xaml.cs`) con fallback offline robusto.
3. **Fase 4:** Reemplazar el umbral arbitrario `COUNT >= 100` por un modelo explícito de completitud con `ExpectedAppCount`, `IndexedAppCount`, `SnapshotVersion` y coberturas por dimensión (`RatingCoverage`, `ReleaseDateCoverage`, etc.).
4. **Fase 5 a 7:** Garantizar que Explore All con query vacía sea 100% local (0 llamadas a Steam), con separación estricta entre Pool, Candidates, Filter, Sort y Pagination.
5. **Fase 8 y 9:** Propagar todos los filtros visibles desde `BrowseViewModel` hasta `SearchRequest`, y eliminar las peticiones HTTP individuales para contadores de tags usando SQLite local.
6. **Fase 11 y 12:** Desacoplar `ReleaseDate` de `LastModified` y `Rating` de `ReviewCount`.
7. **Fase 15 a 17:** Implementar Sort y Pagination globales en SQLite, asegurando que `TotalCount` refleje la cantidad real de coincidencias filtradas en el índice.
8. **Fase 19 y 20:** Eliminar payloads artificiales (`{"results_html":"ok"}`) y resolver anomalías redirigiendo a la consulta autoritativa local.
9. **Fase 24 y 25:** Implementar virtualización real y remover el tope de `MaxMaterialized = 500`.
10. **Fase 27 a 33:** Validar end-to-end con suite de tests exhaustiva y generar el reporte final documentando las métricas verificadas.
