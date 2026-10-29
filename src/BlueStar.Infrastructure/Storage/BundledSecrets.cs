using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlueStar.Infrastructure.Storage;

/// <summary>
/// API keys that release builds ship with (DepotBox and the mod providers). build-release.ps1 encrypts them into
/// <see cref="FileName"/> next to the executable with a random per-build key that only exists inside this assembly
/// (AssemblyMetadata "BundledSecretsKey"), so they are never written in plain text to settings.json, the user's
/// AppData or the repository. They are only decrypted in memory.
/// </summary>
public sealed record BundledSecrets
{
    /// <summary>Encrypted secrets file shipped next to BlueStar.exe.</summary>
    public const string FileName = "bluestar.dat";

    private const string KeyMetadataName = "BundledSecretsKey";
    private const int IvLength = 16;
    private const int MacLength = 32;

    [JsonPropertyName("defaultApiKey")] public string? DefaultApiKey { get; init; }
    [JsonPropertyName("curseForge")] public string? CurseForge { get; init; }
    [JsonPropertyName("nexusMods")] public string? NexusMods { get; init; }
    [JsonPropertyName("modIo")] public string? ModIo { get; init; }
    [JsonPropertyName("thunderstore")] public string? Thunderstore { get; init; }
    [JsonPropertyName("modIoApiUrl")] public string? ModIoApiUrl { get; init; }

    /// <summary>Loads the secrets shipped with this build, or null for builds without them (e.g. development).</summary>
    public static BundledSecrets? Load(string? baseDirectory = null)
    {
        var key = BuildKey();
        if (key != null)
        {
            var path = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, FileName);
            if (File.Exists(path))
            {
                try
                {
                    var secrets = Decrypt(File.ReadAllText(path).Trim(), key);
                    if (secrets != null)
                    {
                        return secrets;
                    }
                }
                catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException)
                {
                    // Fall back to modkeys.json below
                }
            }
        }

        // Development fallback: check for modkeys.json in app directory or parent directories
        return LoadFromModKeysJson(baseDirectory);
    }

    private static BundledSecrets? LoadFromModKeysJson(string? baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            return SearchUpwardsForModKeys(baseDirectory);
        }

        var found = SearchUpwardsForModKeys(AppContext.BaseDirectory);
        if (found != null) return found;

        found = SearchUpwardsForModKeys(Directory.GetCurrentDirectory());
        if (found != null) return found;

        return SearchUpwardsForModKeys(AppDomain.CurrentDomain.BaseDirectory);
    }

    private static BundledSecrets? SearchUpwardsForModKeys(string startDirectory)
    {
        try
        {
            var dir = Path.GetFullPath(startDirectory);
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
            {
                var candidate = Path.Combine(dir, "modkeys.json");
                if (File.Exists(candidate))
                {
                    var json = File.ReadAllText(candidate);
                    return JsonSerializer.Deserialize<BundledSecrets>(json);
                }
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
        }
        catch
        {
            // Ignore dev read errors
        }
        return null;
    }

    /// <summary>
    /// Decrypts base64(iv | AES-256-CBC ciphertext | HMAC-SHA256(iv | ciphertext)). The 64-byte key holds the AES key
    /// followed by the HMAC key. Throws <see cref="CryptographicException"/> when the MAC does not match.
    /// </summary>
    internal static BundledSecrets? Decrypt(string payload, byte[] key)
    {
        if (key.Length != 64) throw new CryptographicException("Invalid bundled secrets key.");

        var data = Convert.FromBase64String(payload);
        if (data.Length < IvLength + 16 + MacLength) throw new CryptographicException("Bundled secrets are truncated.");

        var signed = data.AsSpan(0, data.Length - MacLength);
        var mac = data.AsSpan(data.Length - MacLength);
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key.AsSpan(32, 32), signed), mac))
        {
            throw new CryptographicException("Bundled secrets failed verification.");
        }

        using var aes = Aes.Create();
        aes.Key = key[..32];
        var plain = aes.DecryptCbc(signed[IvLength..], signed[..IvLength], PaddingMode.PKCS7);
        return JsonSerializer.Deserialize<BundledSecrets>(Encoding.UTF8.GetString(plain));
    }

    /// <summary>Counterpart of <see cref="Decrypt"/>; build-release.ps1 does the same in PowerShell.</summary>
    internal static string Encrypt(BundledSecrets secrets, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key[..32];
        var iv = RandomNumberGenerator.GetBytes(IvLength);
        var cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(secrets)), iv, PaddingMode.PKCS7);
        var signed = iv.Concat(cipher).ToArray();
        return Convert.ToBase64String(signed.Concat(HMACSHA256.HashData(key.AsSpan(32, 32), signed)).ToArray());
    }

    private static byte[]? BuildKey()
    {
        var value = typeof(BundledSecrets).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == KeyMetadataName)?.Value;
        if (string.IsNullOrWhiteSpace(value)) return null;

        try
        {
            var key = Convert.FromBase64String(value);
            return key.Length == 64 ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
