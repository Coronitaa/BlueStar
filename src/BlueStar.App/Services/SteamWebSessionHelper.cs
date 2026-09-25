using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;

namespace BlueStar.App.Services;

/// <summary>
/// Facilitates Steam session inheritance and login management for embedded WebView2 store panels.
/// </summary>
public static class SteamWebSessionHelper
{
    private static readonly string[] SteamCookieDomains =
    [
        ".steampowered.com",
        "store.steampowered.com",
        "steamcommunity.com",
        ".steamcommunity.com"
    ];

    /// <summary>
    /// Checks whether the WebView2 instance already contains an active Steam login session cookie.
    /// </summary>
    public static async Task<bool> HasActiveSteamSessionAsync(CoreWebView2 webView)
    {
        if (webView == null) return false;

        try
        {
            var cookies = await webView.CookieManager.GetCookiesAsync("https://store.steampowered.com");
            foreach (var cookie in cookies)
            {
                if (cookie.Name.Equals("steamLoginSecure", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(cookie.Value) &&
                    !cookie.Value.Equals("deleted", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore WebView2 exceptions
        }

        return false;
    }

    /// <summary>
    /// Attempts to discover and import Steam login cookies from the local Steam client CEF cache or browsers,
    /// injecting them into the WebView2 CookieManager if available.
    /// </summary>
    public static async Task<bool> TrySyncSteamCookiesAsync(CoreWebView2 webView, bool force = false)
    {
        if (webView == null) return false;

        // If already authenticated and not forced, nothing to sync
        if (!force && await HasActiveSteamSessionAsync(webView).ConfigureAwait(false))
        {
            return true;
        }

        // Candidates: Steam CEF cache, Edge, Brave, Chrome
        var candidates = new (string BaseDir, string CookiesRelPath)[]
        {
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam", "htmlcache"),
             Path.Combine("Default", "Network", "Cookies")),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam", "htmlcache"),
             Path.Combine("Network", "Cookies")),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam", "htmlcache"),
             "Cookies"),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "config", "htmlcache"),
             Path.Combine("Default", "Network", "Cookies")),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data"),
             Path.Combine("Default", "Network", "Cookies")),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BraveSoftware", "Brave-Browser", "User Data"),
             Path.Combine("Default", "Network", "Cookies")),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data"),
             Path.Combine("Default", "Network", "Cookies")),
        };

        var importedAny = false;

        foreach (var (baseDir, cookiesRelPath) in candidates)
        {
            if (!Directory.Exists(baseDir)) continue;

            var cookiesPath = Path.Combine(baseDir, cookiesRelPath);
            if (!File.Exists(cookiesPath)) continue;

            var localStatePath = Path.Combine(baseDir, "Local State");

            // Read cookies on background thread to prevent UI stutter
            var cookies = await Task.Run(() =>
            {
                var key = ExtractMasterKey(localStatePath);
                return ReadChromiumCookies(cookiesPath, key);
            }).ConfigureAwait(true);

            if (cookies.Count == 0) continue;

            // Injected on UI thread (STA thread required by CoreWebView2 COM)
            foreach (var (host, name, path, val, isSecure, isHttpOnly) in cookies)
            {
                try
                {
                    var c = webView.CookieManager.CreateCookie(name, val, host, path);
                    c.IsSecure = isSecure;
                    c.IsHttpOnly = isHttpOnly;
                    webView.CookieManager.AddOrUpdateCookie(c);
                    importedAny = true;
                }
                catch
                {
                    // Ignore individual cookie injection errors
                }
            }

            if (await HasActiveSteamSessionAsync(webView).ConfigureAwait(true))
            {
                return true;
            }
        }

        return importedAny;
    }

    private static byte[]? ExtractMasterKey(string localStatePath)
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

            // Prefix is "DPAPI" (5 bytes)
            var dpapiPayload = new byte[rawBytes.Length - 5];
            Array.Copy(rawBytes, 5, dpapiPayload, 0, dpapiPayload.Length);

            return ProtectedData.Unprotect(dpapiPayload, null, DataProtectionScope.CurrentUser);
        }
        catch
        {
            return null;
        }
    }

    private static List<(string Host, string Name, string Path, string Value, bool IsSecure, bool IsHttpOnly)> ReadChromiumCookies(
        string cookiesDbPath, byte[]? masterKey)
    {
        var result = new List<(string, string, string, string, bool, bool)>();
        var tempDb = Path.Combine(Path.GetTempPath(), $"bs_ck_{Guid.NewGuid():N}.db");

        try
        {
            // Attempt to copy the database file. If locked exclusively by the browser, this will throw.
            using (var src = new FileStream(cookiesDbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = new FileStream(tempDb, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                src.CopyTo(dst);
            }

            using var conn = new SqliteConnection($"Data Source={tempDb};Mode=ReadOnly");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT host_key, name, path, encrypted_value, is_secure, is_httponly FROM cookies " +
                              "WHERE host_key LIKE '%steampowered.com%' OR host_key LIKE '%steamcommunity.com%'";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var host = reader.GetString(0);
                var name = reader.GetString(1);
                var path = reader.GetString(2);
                var encBytes = (byte[])reader[3];
                var isSecure = reader.GetInt32(4) != 0;
                var isHttpOnly = reader.GetInt32(5) != 0;

                var decrypted = DecryptCookieValue(encBytes, masterKey);
                if (!string.IsNullOrEmpty(decrypted))
                {
                    result.Add((host, name, path, decrypted, isSecure, isHttpOnly));
                }
            }
        }
        catch
        {
            // File locked or read failed
        }
        finally
        {
            try
            {
                if (File.Exists(tempDb)) File.Delete(tempDb);
            }
            catch { }
        }

        return result;
    }

    private static string? DecryptCookieValue(byte[] encBytes, byte[]? masterKey)
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
            var plain = ProtectedData.Unprotect(encBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }
}
