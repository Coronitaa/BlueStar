using System.Text.Json;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Instance;

/// <summary>
/// Manages game instances persisted as JSON files in a configurable root directory.
/// </summary>
public sealed class InstanceManager : IInstanceManager
{
    private readonly string _rootPath;
    private readonly ILogger<InstanceManager> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="InstanceManager"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="rootPath">Optional custom root path. Defaults to %APPDATA%\BlueStar\instances.</param>
    public InstanceManager(ILogger<InstanceManager> logger, string? rootPath = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rootPath = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "instances");
        Directory.CreateDirectory(_rootPath);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GameInstance>> GetAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var instances = new List<GameInstance>();

            if (!Directory.Exists(_rootPath))
                return instances.AsReadOnly();

            foreach (var dir in Directory.GetDirectories(_rootPath))
            {
                ct.ThrowIfCancellationRequested();

                var instanceFile = Path.Combine(dir, "instance.json");
                if (!File.Exists(instanceFile))
                    continue;

                try
                {
                    var json = await File.ReadAllTextAsync(instanceFile, ct).ConfigureAwait(false);
                    var instance = JsonSerializer.Deserialize<GameInstance>(json, JsonOptions);
                    if (instance is not null)
                        instances.Add(instance);
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    _logger.LogWarning(ex, "Failed to read instance from {Path}", instanceFile);
                }
            }

            _logger.LogDebug("Loaded {Count} instances from {Root}", instances.Count, _rootPath);
            return instances.AsReadOnly();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<GameInstance?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var instanceFile = Path.Combine(_rootPath, id.ToString(), "instance.json");
            if (!File.Exists(instanceFile))
                return null;

            var json = await File.ReadAllTextAsync(instanceFile, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<GameInstance>(json, JsonOptions);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<GameInstance> CreateAsync(GameInstance instance, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var newInstance = instance with
            {
                Id = instance.Id == Guid.Empty ? Guid.NewGuid() : instance.Id,
                CreatedAt = now,
                UpdatedAt = now
            };

            var instanceDir = Path.Combine(_rootPath, newInstance.Id.ToString());
            Directory.CreateDirectory(instanceDir);

            var json = JsonSerializer.Serialize(newInstance, JsonOptions);
            await File.WriteAllTextAsync(
                Path.Combine(instanceDir, "instance.json"), json, ct).ConfigureAwait(false);

            _logger.LogInformation("Created instance {Id}: {Name} (AppId={AppId})",
                newInstance.Id, newInstance.Name, newInstance.AppId);

            return newInstance;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<GameInstance> UpdateAsync(GameInstance instance, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var instanceDir = Path.Combine(_rootPath, instance.Id.ToString());
            if (!Directory.Exists(instanceDir))
                throw new InvalidOperationException($"Instance {instance.Id} not found.");

            var updatedInstance = instance with { UpdatedAt = DateTimeOffset.UtcNow };
            var json = JsonSerializer.Serialize(updatedInstance, JsonOptions);
            await File.WriteAllTextAsync(
                Path.Combine(instanceDir, "instance.json"), json, ct).ConfigureAwait(false);

            _logger.LogInformation("Updated instance {Id}: {Name}", instance.Id, instance.Name);
            return updatedInstance;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var instanceDir = Path.Combine(_rootPath, id.ToString());
            if (!Directory.Exists(instanceDir))
                return false;

            Directory.Delete(instanceDir, recursive: true);
            _logger.LogInformation("Deleted instance {Id}", id);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }
}
