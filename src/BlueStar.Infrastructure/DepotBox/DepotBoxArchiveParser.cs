using System.IO.Compression;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.DepotBox;

/// <summary>
/// Parses DepotBox ZIP archives containing Lua metadata and Steam manifest files.
/// </summary>
public sealed class DepotBoxArchiveParser : IDepotBoxArchiveParser
{
    private readonly ILogger<DepotBoxArchiveParser> _logger;
    private readonly DepotBoxLuaParser _luaParser;

    /// <summary>
    /// Initializes a new instance of the <see cref="DepotBoxArchiveParser"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="luaParser">Lua metadata parser.</param>
    public DepotBoxArchiveParser(
        ILogger<DepotBoxArchiveParser> logger,
        DepotBoxLuaParser luaParser)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _luaParser = luaParser ?? throw new ArgumentNullException(nameof(luaParser));
    }

    /// <inheritdoc />
    public async Task<DepotBoxArchive> ParseAsync(string zipFilePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipFilePath);

        if (!File.Exists(zipFilePath))
            throw new FileNotFoundException("DepotBox archive not found.", zipFilePath);

        _logger.LogInformation("Parsing DepotBox archive: {Path}", zipFilePath);

        using var zipStream = new FileStream(zipFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        // Find the .lua file
        var luaEntry = archive.Entries.FirstOrDefault(e =>
            e.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));

        if (luaEntry is null)
            throw new InvalidDataException($"No .lua metadata file found in archive: {zipFilePath}");

        // Read and parse the Lua content
        using var luaStream = luaEntry.Open();
        using var reader = new StreamReader(luaStream);
        var luaContent = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        var result = _luaParser.Parse(luaContent);

        // Validate that referenced manifest files exist in the archive
        var archiveFiles = archive.Entries
            .Select(e => e.Name.ToLowerInvariant())
            .ToHashSet();

        var missingManifests = new List<string>();
        foreach (var game in result.Games)
        {
            foreach (var depot in game.Depots)
            {
                if (depot.ManifestFileName is not null &&
                    !archiveFiles.Contains(depot.ManifestFileName.ToLowerInvariant()))
                {
                    // Check with path prefix (nested folders)
                    var found = archive.Entries.Any(e =>
                        e.FullName.EndsWith(depot.ManifestFileName, StringComparison.OrdinalIgnoreCase));

                    if (!found)
                    {
                        missingManifests.Add(depot.ManifestFileName);
                    }
                }
            }
        }

        if (missingManifests.Count > 0)
        {
            _logger.LogWarning("Missing manifest files in archive: {Missing}",
                string.Join(", ", missingManifests));
        }

        _logger.LogInformation("Successfully parsed archive: AppId={AppId}, {GameCount} games, {DepotCount} total depots",
            result.AppId, result.Games.Count, result.Games.Sum(g => g.Depots.Count));

        return result;
    }

    /// <inheritdoc />
    public Task<DepotBoxArchive> ParseLuaAsync(string luaContent, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(luaContent);
        ct.ThrowIfCancellationRequested();

        var result = _luaParser.Parse(luaContent);
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ExtractManifestsAsync(
        string zipFilePath, string targetDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        if (!File.Exists(zipFilePath))
            throw new FileNotFoundException("DepotBox archive not found.", zipFilePath);

        Directory.CreateDirectory(targetDirectory);

        _logger.LogInformation("Extracting manifests from {Archive} to {Target}", zipFilePath, targetDirectory);

        using var zipStream = new FileStream(zipFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var extractedPaths = new List<string>();

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (!entry.Name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                continue;

            var targetPath = Path.Combine(targetDirectory, entry.Name);

            using (var entryStream = entry.Open())
            using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await entryStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            }

            try
            {
                File.SetLastWriteTimeUtc(targetPath, entry.LastWriteTime.UtcDateTime);
            }
            catch { }

            extractedPaths.Add(targetPath);

            _logger.LogDebug("Extracted manifest: {FileName} ({Size} bytes)", entry.Name, entry.Length);
        }

        _logger.LogInformation("Extracted {Count} manifest files to {Target}", extractedPaths.Count, targetDirectory);

        return extractedPaths.AsReadOnly();
    }
}
