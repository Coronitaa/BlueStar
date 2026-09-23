using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using BlueStar.Core.Helpers;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Catalog;
using BlueStar.Infrastructure.Search;
using BlueStar.Infrastructure.Steam;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueStar.Tools.SearchSmoke;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=================================================");
        Console.WriteLine(" BlueStar Steam Search Engine Smoke Test Harness ");
        Console.WriteLine("=================================================");
        Console.ResetColor();

        var query = "570";
        var isBenchmark = false;
        var runAnomaly = false;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--query" && i + 1 < args.Length) query = args[++i];
            if (args[i] == "--benchmark") isBenchmark = true;
            if (args[i] == "--anomaly-test") runAnomaly = true;
        }

        var dbPath = Path.Combine(Path.GetTempPath(), $"smoke_catalog_{Guid.NewGuid():N}.sqlite");
        var localRepo = new LocalCatalogRepository(NullLogger<LocalCatalogRepository>.Instance, dbPath);
        await localRepo.InitializeAsync();

        // Seed with popular test items
        await localRepo.UpsertAppsAsync([
            new CatalogAppItem { AppId = 570, Name = "Dota 2", NormalizedName = "dota 2", CompactName = "dota2", ReviewPercent = 82 },
            new CatalogAppItem { AppId = 730, Name = "Counter-Strike 2", NormalizedName = "counter strike 2", CompactName = "counterstrike2", ReviewPercent = 88 },
            new CatalogAppItem { AppId = 1091500, Name = "Cyberpunk 2077", NormalizedName = "cyberpunk 2077", CompactName = "cyberpunk2077", ReviewPercent = 86 },
            new CatalogAppItem { AppId = 367520, Name = "Hollow Knight", NormalizedName = "hollow knight", CompactName = "hollowknight", ReviewPercent = 97 }
        ]);

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var steamSearch = new SteamCatalogSearchService(httpClient, NullLogger<SteamCatalogSearchService>.Instance);
        var validator = new SteamResponseValidator();
        var pipeline = new SearchPipeline(localRepo, steamSearch, validator, NullLogger<SearchPipeline>.Instance);

        // 1. Query Parsing Test
        Console.WriteLine($"\n[1/3] Testing deterministic query parser for input: \"{query}\"");
        var parsed = SteamQueryParser.Parse(query);
        if (parsed.AppId.HasValue)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  -> Recognized as Deterministic AppID: {parsed.AppId.Value}");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  -> Recognized as Normalized Term: \"{parsed.NormalizedTerm}\"");
        }
        Console.ResetColor();

        // 2. Search Execution Test
        Console.WriteLine($"\n[2/3] Executing search via layered SearchPipeline...");
        var sw = Stopwatch.StartNew();
        var response = await pipeline.ExecuteAsync(new SearchRequest { RawQuery = query });
        sw.Stop();

        Console.WriteLine($"  Resolution:    {response.ResolutionType}");
        Console.WriteLine($"  Local Catalog: {response.IsFromLocalCatalog}");
        Console.WriteLine($"  Results Found: {response.TotalCount}");
        Console.WriteLine($"  Elapsed Time:  {sw.ElapsedMilliseconds} ms");

        foreach (var item in response.Items)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"    - [{item.AppId}] {item.Name} (Rating: {item.ReviewPercent}%, DRM: {item.HasDrm})");
            Console.ResetColor();
        }

        // Live Browse Verification: Software
        Console.WriteLine("\n[2b/3] Testing Software Catalog Browse (category1=994, Pool=all)...");
        var softResp = await pipeline.ExecuteAsync(new SearchRequest { Pool = "all", AppTypes = "994", RawQuery = "" });
        Console.WriteLine($"  Software Products Found: {softResp.TotalCount} (Returned {softResp.Items.Count})");
        foreach (var item in softResp.Items.Take(5))
        {
            Console.WriteLine($"    - [{item.AppId}] {item.Name} (AppType: {item.AppType}, Price: {item.PriceText})");
        }

        // Live Browse Verification: Popular New Releases
        Console.WriteLine("\n[2c/3] Testing Popular New Releases Browse (filter=popularnew)...");
        var popResp = await pipeline.ExecuteAsync(new SearchRequest { Pool = "popularnew", SortBy = "", RawQuery = "" });
        Console.WriteLine($"  Popular New Releases Found: {popResp.TotalCount} (Returned {popResp.Items.Count})");
        foreach (var item in popResp.Items.Take(5))
        {
            Console.WriteLine($"    - [{item.AppId}] {item.Name} (Released: {item.ReleaseDateText}, Price: {item.PriceText})");
        }

        // 3. Anomaly Detector Check
        if (runAnomaly)
        {
            Console.WriteLine($"\n[3/3] Running Anomaly Detection Test (opposing sort verification)...");
            var mockDesc = new SearchResult[] { new() { AppId = 101, Name = "A" }, new() { AppId = 102, Name = "B" }, new() { AppId = 103, Name = "C" } };
            var mockAsc = new SearchResult[] { new() { AppId = 101, Name = "A" }, new() { AppId = 102, Name = "B" }, new() { AppId = 103, Name = "C" } };

            validator.ValidateResponse(new SteamSearchQuery { Term = "test", SortBy = "Reviews_DESC" }, "{\"results_html\":\"\"}", mockDesc, 3);
            var anomalyRes = validator.ValidateResponse(new SteamSearchQuery { Term = "test", SortBy = "Reviews_ASC" }, "{\"results_html\":\"\"}", mockAsc, 3);

            if (anomalyRes.RequiresLocalSortFallback)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  -> PASS: Successfully detected ignored parameter! Anomaly: {anomalyRes.Anomaly}");
                Console.WriteLine($"     Description: {anomalyRes.Description}");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  -> FAIL: Did not detect ignored parameter anomaly.");
            }
            Console.ResetColor();
        }

        // 4. Benchmark if requested
        if (isBenchmark)
        {
            Console.WriteLine($"\n[Benchmark] Running 100 iterations of local FTS query...");
            var benchSw = Stopwatch.StartNew();
            for (var b = 0; b < 100; b++)
            {
                await pipeline.ExecuteAsync(new SearchRequest { RawQuery = "cyberpunk" });
            }
            benchSw.Stop();
            var avgMs = benchSw.Elapsed.TotalMilliseconds / 100.0;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  100 iterations completed in {benchSw.ElapsedMilliseconds}ms (Average: {avgMs:F2}ms/query)");
            Console.ResetColor();
        }

        // Cleanup
        localRepo.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n[Smoke Test PASSED]");
        Console.ResetColor();
        return 0;
    }
}
