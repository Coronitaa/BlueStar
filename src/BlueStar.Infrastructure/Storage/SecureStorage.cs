using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using BlueStar.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Storage;

/// <summary>
/// Secure storage implementation using Windows DPAPI (Data Protection API).
/// Stores encrypted values in the user's application data directory.
/// </summary>
#pragma warning disable CA1416
[SupportedOSPlatform("windows")]
public sealed class SecureStorage : ISecureStorage
{
    private readonly string _storagePath;
    private readonly ILogger<SecureStorage> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecureStorage"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public SecureStorage(ILogger<SecureStorage> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "secure");
        Directory.CreateDirectory(_storagePath);
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var filePath = GetFilePath(key);
        if (!File.Exists(filePath))
            return null;

        try
        {
            var encryptedBytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
            var decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decryptedBytes);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt secure storage entry for key hash: {KeyHash}",
                GetKeyHash(key));
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to read secure storage file for key hash: {KeyHash}",
                GetKeyHash(key));
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        var plainBytes = Encoding.UTF8.GetBytes(value);
        var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);

        var filePath = GetFilePath(key);
        await File.WriteAllBytesAsync(filePath, encryptedBytes, ct).ConfigureAwait(false);

        _logger.LogDebug("Stored secure entry for key hash: {KeyHash}", GetKeyHash(key));
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        var filePath = GetFilePath(key);
        if (!File.Exists(filePath))
            return Task.FromResult(false);

        File.Delete(filePath);
        _logger.LogDebug("Deleted secure entry for key hash: {KeyHash}", GetKeyHash(key));
        return Task.FromResult(true);
    }

    private string GetFilePath(string key) =>
        Path.Combine(_storagePath, $"{GetKeyHash(key)}.dat");

    private static string GetKeyHash(string key)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
