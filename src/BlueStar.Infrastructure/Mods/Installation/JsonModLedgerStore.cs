using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Mods.Installation;

/// <summary>
/// Stores each instance's mod ledger as instances/&lt;id&gt;/mods.json, next to instance.json.
/// </summary>
public sealed class JsonModLedgerStore : IModLedgerStore
{
    private const string LedgerFileName = "mods.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _instancesRoot;
    private readonly ILogger<JsonModLedgerStore> _logger;

    public JsonModLedgerStore(ILogger<JsonModLedgerStore> logger, string? instancesRoot = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _instancesRoot = instancesRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "instances");
    }

    /// <inheritdoc />
    public string GetInstanceDataDirectory(Guid instanceId) => Path.Combine(_instancesRoot, instanceId.ToString());

    /// <inheritdoc />
    public async Task<ModLedger> LoadAsync(Guid instanceId, CancellationToken ct = default)
    {
        var path = LedgerPath(instanceId);
        if (!File.Exists(path)) return ModLedger.Empty;

        try
        {
            await using var stream = File.OpenRead(path);
            var ledger = await JsonSerializer.DeserializeAsync<ModLedger>(stream, JsonOptions, ct).ConfigureAwait(false);
            return ledger ?? ModLedger.Empty;
        }
        catch (JsonException ex)
        {
            // Keep the unreadable file for inspection instead of silently overwriting it on the next save.
            var corruptPath = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            _logger.LogError(ex, "Mod ledger for instance {InstanceId} is unreadable; moved to {Path}", instanceId, corruptPath);
            File.Move(path, corruptPath, overwrite: true);
            return ModLedger.Empty;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(Guid instanceId, ModLedger ledger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        var path = LedgerPath(instanceId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, ledger, JsonOptions, ct).ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private string LedgerPath(Guid instanceId) => Path.Combine(GetInstanceDataDirectory(instanceId), LedgerFileName);
}
