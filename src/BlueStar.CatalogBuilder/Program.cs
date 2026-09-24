using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Catalog;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueStar.CatalogBuilder;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("   BlueStar Steam Catalog Builder & Snapshotter   ");
        Console.WriteLine("==================================================");

        string outputDir = Path.GetFullPath("catalog-dist");
        string? apiKey = Environment.GetEnvironmentVariable("STEAM_API_KEY");
        string? fromExisting = null;
        int? limit = null;
        int version = int.Parse(DateTime.UtcNow.ToString("yyyyMMdd"));
        string downloadUrl = "https://github.com/Coronitaa/BlueStar/releases/latest/download/catalog.sqlite.zst";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) outputDir = Path.GetFullPath(args[++i]);
            else if (args[i] == "--api-key" && i + 1 < args.Length) apiKey = args[++i];
            else if (args[i] == "--from-existing" && i + 1 < args.Length) fromExisting = Path.GetFullPath(args[++i]);
            else if (args[i] == "--limit" && i + 1 < args.Length && int.TryParse(args[++i], out var lim)) limit = lim;
            else if (args[i] == "--version" && i + 1 < args.Length && int.TryParse(args[++i], out var ver)) version = ver;
            else if (args[i] == "--download-url" && i + 1 < args.Length) downloadUrl = args[++i];
        }

        Directory.CreateDirectory(outputDir);
        string sqlitePath = Path.Combine(outputDir, "catalog.sqlite");
        string zstdPath = Path.Combine(outputDir, "catalog.sqlite.zst");
        string manifestPath = Path.Combine(outputDir, "catalog-manifest.json");

        if (File.Exists(sqlitePath)) File.Delete(sqlitePath);
        if (File.Exists(zstdPath)) File.Delete(zstdPath);
        if (File.Exists(manifestPath)) File.Delete(manifestPath);

        int totalApps = 0;

        if (!string.IsNullOrWhiteSpace(fromExisting))
        {
            if (!File.Exists(fromExisting))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: Archivo de base de datos no encontrado: {fromExisting}");
                Console.ResetColor();
                return 1;
            }

            Console.WriteLine($"[1/5] Clonando base de datos existente desde: {fromExisting}");
            File.Copy(fromExisting, sqlitePath, overwrite: true);

            var repo = new LocalCatalogRepository(NullLogger<LocalCatalogRepository>.Instance, sqlitePath);
            await repo.InitializeAsync().ConfigureAwait(false);
            totalApps = await repo.GetCountAsync().ConfigureAwait(false);
            Console.WriteLine($"✓ Base clonada con {totalApps:N0} apps existentes.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Aviso: No se proporcionó una Steam Web API Key.");
                Console.WriteLine("Valve retiró el endpoint público sin autenticación (ISteamApps/GetAppList/v2).");
                Console.WriteLine("Para descargar el catálogo completo de 150.000+ juegos directamente de Valve en segundos,");
                Console.WriteLine("se requiere una clave gratuita de Steam (https://steamcommunity.com/dev/apikey).");
                Console.WriteLine();
                Console.WriteLine("Uso:");
                Console.WriteLine("  dotnet run --project src/BlueStar.CatalogBuilder -- --api-key <TU_STEAM_KEY>");
                Console.WriteLine("  O empaquetar una base existente:");
                Console.WriteLine("  dotnet run --project src/BlueStar.CatalogBuilder -- --from-existing <PATH_TO_SQLITE>");
                Console.ResetColor();
                return 1;
            }

            Console.WriteLine($"[1/5] Inicializando nueva base de catálogo en: {sqlitePath}");
            var repo = new LocalCatalogRepository(NullLogger<LocalCatalogRepository>.Instance, sqlitePath);
            await repo.InitializeAsync().ConfigureAwait(false);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BlueStar-CatalogBuilder/1.0 (+https://github.com/Coronitaa/BlueStar)");

            Console.WriteLine("[2/5] Sincronizando catálogo desde Steam IStoreService/GetAppList/v1...");
            var sw = Stopwatch.StartNew();
            totalApps = await SteamCatalogSnapshotService.SyncFromSteamWebToRepositoryAsync(http, repo, apiKey, startAppId: 0).ConfigureAwait(false);
            sw.Stop();
            Console.WriteLine($"✓ Ingestados {totalApps:N0} juegos en {sw.Elapsed.TotalSeconds:F1}s.");
        }

        Console.WriteLine("[3/5] Estableciendo metadatos, optimizando SQLite e índices FTS5...");
        {
            var repo = new LocalCatalogRepository(NullLogger<LocalCatalogRepository>.Instance, sqlitePath);
            await repo.InitializeAsync().ConfigureAwait(false);
            await repo.SetMetadataAsync("snapshot_version", version.ToString()).ConfigureAwait(false);
            await repo.SetMetadataAsync("identity_complete", "1").ConfigureAwait(false);
            await repo.SetMetadataAsync("expected_app_count", totalApps.ToString()).ConfigureAwait(false);
            await repo.SetMetadataAsync("last_sync_at", DateTimeOffset.UtcNow.ToString("O")).ConfigureAwait(false);
        }

        // Optimize SQLite and rebuild FTS
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var csb = new SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = SqliteOpenMode.ReadWrite };
        await using (var conn = new SqliteConnection(csb.ToString()))
        {
            await conn.OpenAsync().ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO apps_fts(apps_fts) VALUES('rebuild');
                PRAGMA optimize;
                VACUUM;
            """;
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var uncompressedBytes = new FileInfo(sqlitePath).Length;
        Console.WriteLine($"✓ Base SQLite optimizada. Tamaño: {uncompressedBytes / (1024.0 * 1024.0):F2} MB");

        Console.WriteLine("[4/5] Comprimiendo con Zstandard (Nivel 19)...");
        var compSw = Stopwatch.StartNew();
        await SteamCatalogSnapshotService.CompressToZstdAsync(sqlitePath, zstdPath, compressionLevel: 19).ConfigureAwait(false);
        compSw.Stop();

        var compressedBytes = new FileInfo(zstdPath).Length;
        var ratio = (1.0 - ((double)compressedBytes / uncompressedBytes)) * 100.0;
        Console.WriteLine($"✓ Comprimido Zstandard generado en {compSw.Elapsed.TotalSeconds:F1}s.");
        Console.WriteLine($"  Tamaño: {compressedBytes / (1024.0 * 1024.0):F2} MB ({ratio:F1}% de reducción)");

        Console.WriteLine("[5/5] Calculando SHA-256 y generando catalog-manifest.json...");
        string sha256;
        await using (var stream = File.OpenRead(zstdPath))
        {
            var hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
            sha256 = Convert.ToHexString(hash).ToLowerInvariant();
        }

        var manifest = new CatalogManifest
        {
            Version = version,
            GeneratedAt = DateTimeOffset.UtcNow,
            TotalApps = totalApps,
            Sha256 = sha256,
            DownloadUrl = downloadUrl,
            CompressedSizeBytes = compressedBytes,
            FormatVersion = "1.0"
        };

        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(manifestPath, manifestJson).ConfigureAwait(false);

        Console.WriteLine($"✓ Manifest generado en: {manifestPath}");
        Console.WriteLine($"  - Versión: {version}");
        Console.WriteLine($"  - Total Apps: {totalApps:N0}");
        Console.WriteLine($"  - SHA256: {sha256}");
        Console.WriteLine($"  - URL de descarga: {downloadUrl}");
        Console.WriteLine("==================================================");
        Console.WriteLine("COMPILACIÓN DE SNAPSHOT FINALIZADA CON ÉXITO");
        Console.WriteLine("==================================================");
        return 0;
    }
}
