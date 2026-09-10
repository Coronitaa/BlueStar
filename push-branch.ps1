<#
    BlueStar — prepara y sube la rama con los cambios de la revision 1.3.

    Uso:
        cd "C:\Users\Valen\Documents\BLUESTAR 1.3"
        powershell -ExecutionPolicy Bypass -File .\push-branch.ps1

    El script NO sube nada sin preguntar. Compila primero y aborta si falla.
#>

[CmdletBinding()]
param(
    [string]$Branch = "feat/instance-ui-overhaul",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repo

function Step($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Warn($t) { Write-Host $t -ForegroundColor Yellow }
function Die($t)  { Write-Host $t -ForegroundColor Red; exit 1 }

# ── 0. Comprobaciones ────────────────────────────────────────────────────────
Step "Estado del repositorio"
git rev-parse --is-inside-work-tree *> $null
if ($LASTEXITCODE -ne 0) { Die "No estas dentro de un repositorio git." }

$base = (git rev-parse --abbrev-ref HEAD).Trim()
Write-Host "Rama actual: $base"
git status --short

# ── 1. Borrar restos de la pantalla de carga experimental ────────────────────
Step "Eliminando restos del splash experimental"
foreach ($f in @(
    "src\BlueStar.App\Views\SplashWindow.xaml",
    "src\BlueStar.App\Views\SplashWindow.xaml.cs",
    "src\BlueStar.App\Assets\splash.png"
)) {
    if (Test-Path $f) {
        git rm -q --ignore-unmatch --cached $f 2>$null | Out-Null
        Remove-Item -Force $f
        Write-Host "  borrado: $f"
    }
}

# ── 2. Compilar ──────────────────────────────────────────────────────────────
if (-not $SkipBuild) {
    Step "Compilando (dotnet build)"
    dotnet build .\src\BlueStar.App\BlueStar.App.csproj -c Debug -v minimal
    if ($LASTEXITCODE -ne 0) { Die "La compilacion fallo. No se creo ninguna rama." }
    Write-Host "Compilacion correcta." -ForegroundColor Green
} else {
    Warn "Compilacion omitida (-SkipBuild)."
}

# ── 3. Submodulo DepotDownloader ─────────────────────────────────────────────
Step "Revisando el submodulo src/BlueStar.DepotDownloader"
Push-Location "src\BlueStar.DepotDownloader"
$subDirty = (git status --porcelain)
if ($subDirty) {
    Warn "El submodulo tiene cambios sin commitear:"
    Write-Host $subDirty
    Warn "Tiene que subirse por separado a Coronitaa/DepotDownloaderMod ANTES de esta rama."
    Warn "Mira el bloque 'Submodulo' de las instrucciones. Continuo sin tocarlo."
} else {
    Write-Host "Submodulo limpio."
}
Pop-Location

# ── 4. Crear la rama ─────────────────────────────────────────────────────────
Step "Creando la rama $Branch"
git rev-parse --verify $Branch *> $null
if ($LASTEXITCODE -eq 0) {
    Warn "La rama $Branch ya existe. Cambiando a ella."
    git checkout $Branch
} else {
    git checkout -b $Branch
}
if ($LASTEXITCODE -ne 0) { Die "No se pudo crear/cambiar a la rama." }

# ── 5. Commit ────────────────────────────────────────────────────────────────
Step "Preparando el commit"
git add -A
git reset -q -- push-branch.ps1 2>$null

$staged = (git diff --cached --name-only)
if (-not $staged) { Die "No hay nada que commitear." }
Write-Host "Archivos incluidos:"
Write-Host $staged

$msgFile = Join-Path $env:TEMP "bluestar-commit-msg.txt"
$msg = @'
feat(instances): rediseno de la vista de instancia, modos de version y correcciones

UI
- Nueva navegacion por rail de iconos compacto; cabecera fija (breadcrumb, hero
  y tarjeta de descarga) con el contenido desplazandose de forma independiente.
- Pestana Overview rehecha: ficha del juego con Metacritic, galeria real de
  screenshots de la tienda, tags de Steam agrupados por categoria, notas de
  parche, medidor de almacenamiento en disco y acciones rapidas.
- Seccion de version reorganizada en cuatro modos: Ultima (actualizable, elige
  solo depots compatibles con el SO y excluye DLCs), Recomendada (build curada,
  visible solo si existe), Version anterior (rollback usando los manifests en
  cache de la instancia) y Personalizada (seleccion manual de manifests por
  build y depot, con su interfaz rehecha).
- Mini biblioteca de builds personalizadas guardadas, seleccionables desde chips.
- Se elimina el Modo Tecnico: la pestana Personalizada lo reemplaza y las
  herramientas de importacion y fuentes de build quedan siempre visibles.
- Mejor feedback durante descarga e instalacion.
- Textos y elementos adaptados al contenedor y al tamano de la ventana.
- Traducciones completas ES/EN para toda la interfaz nueva.
- Nuevos tokens de color y estilos de boton: PlayButton / PlayHeroButton en verde
  agua (#14B8A6) para las acciones de lanzar el juego, y UpdateButton en ambar
  (#F59E0B) para "Check for Updates", en linea con los avisos de actualizacion
  que ya usaban ese color en las tarjetas de Inicio y Biblioteca. El boton
  conserva el azul de acento cuando su accion es Instalar.

Version
- BlueStar 1.3.0 -> 1.4.0 (Directory.Build.props y los valores de respaldo de
  AboutViewModel y SettingsViewModel).

Correcciones
- Al actualizar un juego se desinstalan y reinstalan los emuladores y el DLC
  unlocker, para que la instalacion no quede corrupta.
- Las descargas detenidas en 0 ya no se reportan como "cancelada por el usuario".
- Cambiar el directorio de instalacion crea una subcarpeta propia del juego y
  actualiza la ruta del ejecutable.
- Nueva opcion por instancia para desactivar la busqueda de actualizaciones.
- "Latest on Steam" ya no se queda permanentemente en "Checking...": el valor
  centinela dejo de cachearse como si fuera una respuesta valida.
- Abrir y cerrar el modal de actualizaciones ya no marca el juego como
  actualizado ni bloquea futuras actualizaciones.
- Los depots de Windows ya no se etiquetan con otro sistema operativo.
- Los screenshots ya no colapsaban todos a la misma clave de cache (se veia
  siempre el banner).
- El contenedor del modal de depots ya no corta la informacion de la version.
- Se restaura StartupUri en App.xaml: la aplicacion arrancaba sin ventana y
  quedaba corriendo en segundo plano.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_0188a3j9RDQye4BzsSUirE4w
'@
[System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding($false)))

git commit -F $msgFile
if ($LASTEXITCODE -ne 0) { Die "El commit fallo." }
Write-Host "Commit creado." -ForegroundColor Green

# ── 6. Push ──────────────────────────────────────────────────────────────────
Step "Subir a GitHub"
$ans = Read-Host "Subir '$Branch' a origin (https://github.com/Coronitaa/BlueStar)? [s/N]"
if ($ans -notmatch '^[sSyY]') {
    Warn "No se subio nada. Cuando quieras: git push -u origin $Branch"
    exit 0
}

git push -u origin $Branch
if ($LASTEXITCODE -ne 0) { Die "El push fallo." }

Write-Host "`nListo. Abri el Pull Request aca:" -ForegroundColor Green
Write-Host "  https://github.com/Coronitaa/BlueStar/compare/$base...$Branch?expand=1"
