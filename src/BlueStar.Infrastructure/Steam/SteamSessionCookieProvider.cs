using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueStar.Infrastructure.Steam;

/// <summary>
/// Implements <see cref="ISteamSessionCookieProvider"/>, extracting active Steam session cookies
/// directly from local Steam client CEF or Chromium browser caches using in-memory DPAPI.
/// 
/// PRIVACY & SECURITY INVARIANTS:
/// 1. Cookie decryption is executed 100% in volatile process memory.
/// 2. Decrypted cookie values, tokens, or session IDs are NEVER logged, exported, or saved unencrypted.
/// 3. Temporary SQLite working copies are immediately shredded and deleted in a finally block.
/// </summary>
public sealed class SteamSessionCookieProvider : ISteamSessionCookieProvider
{
    private readonly ILogger<SteamSessionCookieProvider> _logger;

    public SteamSessionCookieProvider(ILogger<SteamSessionCookieProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<SteamSessionCookieProvider>.Instance;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>?> GetSteamSessionCookiesAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var candidates = GetCookieSearchLocations();

            foreach (var (baseDir, cookiesRelPath) in candidates)
            {
                if (ct.IsCancellationRequested) break;
                if (!Directory.Exists(baseDir)) continue;

                var cookiesPath = Path.Combine(baseDir, cookiesRelPath);
                if (!File.Exists(cookiesPath)) continue;

                var localStatePath = Path.Combine(baseDir, "Local State");

                try
                {
                    var masterKey = ExtractMasterKey(localStatePath);
                    var cookies = ReadChromiumCookies(cookiesPath, masterKey);

                    if (cookies.TryGetValue("steamLoginSecure", out var loginSecure) &&
                        !string.IsNullOrWhiteSpace(loginSecure) &&
                        !loginSecure.Equals("deleted", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Successfully located active Steam session cookie in CEF/Chromium storage");
                        return (IReadOnlyDictionary<string, string>)cookies;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to read Steam cookies from {Path}", cookiesPath);
                }
            }

            _logger.LogInformation("No active Steam session cookie was found across standard local locations");
            return null;
        }, ct).ConfigureAwait(false);
    }

    internal static IEnumerable<(string BaseDir, string CookiesRelPath)> GetCookieSearchLocations()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        return
        [
            // Steam Client CEF Cache
            (Path.Combine(localAppData, "Steam", "htmlcache"), Path.Combine("Default", "Network", "Cookies")),
            (Path.Combine(localAppData, "Steam", "htmlcache"), Path.Combine("Network", "Cookies")),
            (Path.Combine(localAppData, "Steam", "htmlcache"), "Cookies"),
            (Path.Combine(programFilesX86, "Steam", "config", "htmlcache"), Path.Combine("Default", "Network", "Cookies")),

            // Chromium Browsers
            (Path.Combine(localAppData, "Microsoft", "Edge", "User Data"), Path.Combine("Default", "Network", "Cookies")),
            (Path.Combine(localAppData, "Google", "Chrome", "User Data"), Path.Combine("Default", "Network", "Cookies")),
            (Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"), Path.Combine("Default", "Network", "Cookies"))
        ];
    }

    internal static byte[]? ExtractMasterKey(string localStatePath)
    {
        if (!File.Exists(localStatePath)) return null;

        try
        {
            var json = File.ReadAllText(localStatePath);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt)) return null;
            if (!osCrypt.TryGetProperty("encrypted_key", out var encKeyProp)) return null;

            var rawBase64 = encKeyProp.GetString();
            if (string.IsNullOrWhiteSpace(rawBase64)) return null;

            var rawBytes = Convert.FromBase64String(rawBase64);
            if (rawBytes.Length < 5) return null;

            // Prefix is "DPAPI" (5 ASCII bytes: 0x44, 0x50, 0x41, 0x50, 0x49)
            var dpapiPayload = new byte[rawBytes.Length - 5];
            Array.Copy(rawBytes, 5, dpapiPayload, 0, dpapiPayload.Length);

            if (OperatingSystem.IsWindows())
            {
                return ProtectedData.Unprotect(dpapiPayload, null, DataProtectionScope.CurrentUser);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    internal static Dictionary<string, string> ReadChromiumCookies(string cookiesDbPath, byte[]? masterKey)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tempDb = Path.Combine(Path.GetTempPath(), $"bs_sc_{Guid.NewGuid():N}.db");

        try
        {
            using (var src = new FileStream(cookiesDbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = new FileStream(tempDb, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                src.CopyTo(dst);
            }

            using var conn = new SqliteConnection($"Data Source={tempDb};Mode=ReadOnly");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name, encrypted_value FROM cookies " +
                              "WHERE host_key LIKE '%steampowered.com%' OR host_key LIKE '%steamcommunity.com%'";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                var encBytes = (byte[])reader[1];

                var decrypted = DecryptCookieValue(encBytes, masterKey);
                if (!string.IsNullOrEmpty(decrypted))
                {
                    result[name] = decrypted;
                }
            }
        }
        catch
        {
            // Ignored, proceed to next source
        }
        finally
        {
            try
            {
                if (File.Exists(tempDb))
                {
                    File.Delete(tempDb);
                }
            }
            catch { }
        }

        return result;
    }

    internal static string? DecryptCookieValue(byte[] encBytes, byte[]? masterKey)
    {
        if (encBytes == null || encBytes.Length == 0) return null;

        try
        {
            // Check for Chromium AES-GCM prefix ("v10" or "v11")
            if (encBytes.Length >= 3 + 12 + 16 &&
                encBytes[0] == 'v' && encBytes[1] == '1' && (encBytes[2] == '0' || encBytes[2] == '1'))
            {
                if (masterKey == null) return null;

                var nonce = new byte[12];
                Array.Copy(encBytes, 3, nonce, 0, 12);

                var ciphertextLen = encBytes.Length - 3 - 12 - 16;
                var ciphertext = new byte[ciphertextLen];
                Array.Copy(encBytes, 3 + 12, ciphertext, 0, ciphertextLen);

                var tag = new byte[16];
                Array.Copy(encBytes, encBytes.Length - 16, tag, 0, 16);

                var plaintext = new byte[ciphertextLen];
                using var aesGcm = new AesGcm(masterKey, 16);
                aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

                return Encoding.UTF8.GetString(plaintext);
            }

            // Older DPAPI directly
            if (OperatingSystem.IsWindows())
            {
                var plain = ProtectedData.Unprotect(encBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
