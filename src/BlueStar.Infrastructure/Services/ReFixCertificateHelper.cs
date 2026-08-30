using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Verifies and installs the ReFix Trusted Engine digital code-signing certificate in Windows Certificate Store.
/// Ensures Windows Defender, SmartScreen, and OS execution policies trust ReFix binaries.
/// </summary>
public static class ReFixCertificateHelper
{
    public const string CertificateThumbprint = "FFCAA75925694E424A7F2F07FD7AE6C06A43BC54";
    public const string CertificateSubjectName = "ReFix Trusted Engine";

    // Embedded Base64 certificate payload for standalone reliability
    private const string EmbeddedCertBase64 =
        "MIIDQDCCAiigAwIBAgIQGuzOPfMoR51HY56ymhjv8jANBgkqhkiG9w0BAQsFADA4MRcwFQYDVQQKDA5SZUZpeCBTZWN1cml0eTEdMBsGA1UEAwwUUmVGaXggVHJ1c3RlZCBFbmdpbmUwHhcNMjYwNzI3MjMxMzQwWhcNMjcwNzI3MjMzMzQwWjA4MRcwFQYDVQQKDA5SZUZpeCBTZWN1cml0eTEdMBsGA1UEAwwUUmVGaXggVHJ1c3RlZCBFbmdpbmUwggEiMA0GCSqGSIb3DQEBAQUAA4IBDwAwggEKAoIBAQDWUO6R+55Py59V1OwtHj+rfM2ioHGirzklex7LaW5M3pN/l8rNkIVKJeK4MXD6V8uv71Ult5xIoa9YmmCVRWFulgnIDrv0pncoEwwjkRoO0piekIghwnAf6uCJJaSVUxdqtf/Fx/NbsOEqqWTHCpansXWHZLQF/2p+IpRn66K0JzSzIA8/uWsobP/L8kNpotTqhnBq1oQrmiAP9PnJZt7vjFbaGi4pyYwjT6zG4MytgAk+87ShX24entHMRmQ3WRlKaVt1VrFomM0h9b/WPfaQ9YvVVl+sTjvQEv8ccBvJPDODmgD1ImWxaikmCBCNAElf/Sjqkt1OyXNr4DS6Lk1RAgMBAAGjRjBEMA4GA1UdDwEB/wQEAwIHgDATBgNVHSUEDDAKBggrBgEFBQcDAzAdBgNVHQ4EFgQUiBr9FvmSdT3zaAdaFUldccAHPaowDQYJKoZIhvcNAQELBQADggEBAGn275z2gowNdds4NFQNZrIZPQY6jTxtZ9aLdPjRL4NMhcRZsEpSc+2mmJOTuhZo1I/HnseCYclNJ2XABkoAsPJMb1CL7YVZgn/lvoNFlAH9Xhp33YzXxsfDtMAxE+iDXEf7ZzfDJgbRJ6t0sebuIrVCHhUgflurThedUw0RPG2Aak+jmWQGTq57jYY+AzrxOuHPXcQeHI5+W5JjtejMy5mSWzZDooe+O3Sx1HDW5K2PeME8ldjKsx9QTPVMHNL0srpRbaYoeHnNaODuFu+3OLDXfj+7Gyitoz1qYxRZlMm9ptrYydTNaRNLg4SGgcey6/FhcrXGHpfeSQUhIDSLHUQ=";

    /// <summary>
    /// Checks whether the ReFix Trusted Engine certificate is currently installed in the system.
    /// </summary>
    public static bool IsCertificateInstalled()
    {
        if (!OperatingSystem.IsWindows()) return true;

        var storeLocations = new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine };
        var storeNames = new[] { StoreName.Root, StoreName.TrustedPublisher, StoreName.CertificateAuthority };

        foreach (var location in storeLocations)
        {
            foreach (var name in storeNames)
            {
                try
                {
                    using var store = new X509Store(name, location);
                    store.Open(OpenFlags.ReadOnly);
                    var certs = store.Certificates.Find(X509FindType.FindByThumbprint, CertificateThumbprint, validOnly: false);
                    if (certs.Count > 0)
                    {
                        return true;
                    }

                    var bySubject = store.Certificates.Find(X509FindType.FindBySubjectName, CertificateSubjectName, validOnly: false);
                    if (bySubject.Count > 0)
                    {
                        return true;
                    }
                }
                catch
                {
                    // Ignore store access errors
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Ensures that the ReFix digital certificate is installed in the system certificate store.
    /// If not present, installs it into the CurrentUser Root and TrustedPublisher stores.
    /// </summary>
    public static async Task<bool> EnsureCertificateInstalledAsync(ILogger? logger = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return true;

        if (IsCertificateInstalled())
        {
            logger?.LogDebug("ReFix digital certificate is already installed in the system.");
            return true;
        }

        logger?.LogInformation("ReFix certificate not found in store. Installing ReFix Trusted Engine signature...");

        try
        {
            var rawBytes = Convert.FromBase64String(EmbeddedCertBase64);
            using var cert = new X509Certificate2(rawBytes);

            // 1. Try installing into CurrentUser stores via .NET X509Store
            foreach (var storeName in new[] { StoreName.Root, StoreName.TrustedPublisher })
            {
                try
                {
                    using var store = new X509Store(storeName, StoreLocation.CurrentUser);
                    store.Open(OpenFlags.ReadWrite);
                    store.Add(cert);
                    store.Close();
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Could not add ReFix certificate to CurrentUser store {Store}", storeName);
                }
            }

            if (IsCertificateInstalled())
            {
                logger?.LogInformation("Successfully installed ReFix digital certificate into CurrentUser store.");
                return true;
            }

            // 2. Fallback to certutil / temp file
            var tempCertPath = Path.Combine(Path.GetTempPath(), "ReFix_Trusted_Engine.cer");
            await File.WriteAllBytesAsync(tempCertPath, rawBytes, ct).ConfigureAwait(false);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "certutil.exe",
                    Arguments = $"-addstore -user -f \"Root\" \"{tempCertPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                }
            }
            finally
            {
                try { if (File.Exists(tempCertPath)) File.Delete(tempCertPath); } catch { }
            }

            var verified = IsCertificateInstalled();
            if (verified)
            {
                logger?.LogInformation("ReFix signature successfully verified and installed via certutil.");
            }
            else
            {
                logger?.LogWarning("ReFix certificate installation completed without error but verification returned false.");
            }
            return verified;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to install ReFix digital signature.");
            return false;
        }
    }
}
