using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Workshop;

/// <summary>
/// Downloads Workshop items with Valve's SteamCMD logged in anonymously (the method WorkshopDL relies on).
/// SteamCMD is fetched from Valve's CDN on first use into %AppData%\BlueStar\tools\steamcmd and must carry
/// Valve's code signature. Steam only serves anonymous Workshop downloads for games that allow them.
/// </summary>
public sealed partial class SteamCmdWorkshopClient
{
    public const string SteamCmdZipUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

    private readonly HttpClient _http;
    private readonly ILogger<SteamCmdWorkshopClient> _logger;
    private readonly string _steamCmdDir;
    // Static: the client is a transient typed HttpClient, but there is one SteamCMD folder per machine.
    private static readonly SemaphoreSlim _gate = new(1, 1);

    public SteamCmdWorkshopClient(HttpClient http, ILogger<SteamCmdWorkshopClient> logger, string? steamCmdDirectory = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _steamCmdDir = steamCmdDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "tools", "steamcmd");
    }

    private string SteamCmdExe => Path.Combine(_steamCmdDir, "steamcmd.exe");

    /// <summary>
    /// Downloads the item into SteamCMD's workshop folder and moves it to <paramref name="destinationDirectory"/>.
    /// SteamCMD runs one download at a time.
    /// </summary>
    public async Task<(bool Ok, string? Error, bool NeedsOwnedAccount)> DownloadAsync(
        uint appId, ulong publishedFileId, string destinationDirectory, IProgress<double>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ready = await EnsureSteamCmdAsync(progress, ct).ConfigureAwait(false);
            if (!ready.Ok) return (false, ready.Error, false);

            progress?.Report(30.0);
            var output = await RunAsync(
                $"+login anonymous +workshop_download_item {appId} {publishedFileId} validate +quit", ct).ConfigureAwait(false);

            var result = ParseDownloadResult(output, publishedFileId);
            if (!result.Ok || result.ContentPath == null)
            {
                _logger.LogWarning("SteamCMD could not download Workshop item {Id} of AppID {AppId}: {Error}", publishedFileId, appId, result.Error);
                return (false, result.Error, result.NeedsOwnedAccount);
            }

            MoveContent(result.ContentPath, destinationDirectory);
            progress?.Report(95.0);
            return (true, null, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads SteamCMD's verdict for <paramref name="publishedFileId"/>, e.g.
    /// <c>Success. Downloaded item 123 to "C:\...\content\480\123" (4096 bytes)</c> or
    /// <c>ERROR! Download item 123 failed (Access Denied).</c>
    /// </summary>
    public static (bool Ok, string? ContentPath, string? Error, bool NeedsOwnedAccount) ParseDownloadResult(string output, ulong publishedFileId)
    {
        var success = Regex.Match(output, $@"Success\. Downloaded item {publishedFileId} to ""([^""]+)""", RegexOptions.IgnoreCase);
        if (success.Success) return (true, success.Groups[1].Value, null, false);

        var failure = Regex.Match(output, $@"ERROR! Download item {publishedFileId} failed \(([^)]+)\)", RegexOptions.IgnoreCase);
        if (failure.Success)
        {
            var reason = failure.Groups[1].Value.Trim();
            // Anonymous logins get "Access Denied" or a bare "Failure" for games whose Workshop needs a license.
            var needsOwnedAccount = reason.Equals("Access Denied", StringComparison.OrdinalIgnoreCase) ||
                                    reason.Equals("Failure", StringComparison.OrdinalIgnoreCase);
            var message = needsOwnedAccount
                ? $"Steam only serves this item to an account that owns the game ({reason})."
                : $"SteamCMD could not download the item ({reason}).";
            return (false, null, message, needsOwnedAccount);
        }

        return (false, null, "SteamCMD did not report a result for this item.", false);
    }

    private async Task<(bool Ok, string? Error)> EnsureSteamCmdAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (!File.Exists(SteamCmdExe))
        {
            _logger.LogInformation("Downloading SteamCMD from Valve into {Dir}", _steamCmdDir);
            progress?.Report(8.0);
            Directory.CreateDirectory(_steamCmdDir);
            var zipPath = Path.Combine(_steamCmdDir, "steamcmd.zip");
            try
            {
                using (var response = await _http.GetAsync(SteamCmdZipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var output = File.Create(zipPath);
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                }

                ZipFile.ExtractToDirectory(zipPath, _steamCmdDir, overwriteFiles: true);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                _logger.LogWarning(ex, "Could not download SteamCMD");
                return (false, "SteamCMD could not be downloaded from Valve.");
            }
            finally
            {
                if (File.Exists(zipPath)) File.Delete(zipPath);
            }
        }

        if (!IsSignedByValve(SteamCmdExe))
        {
            _logger.LogWarning("Refusing to run {Path}: it is not signed by Valve", SteamCmdExe);
            File.Delete(SteamCmdExe);
            return (false, "The downloaded SteamCMD is not signed by Valve and was removed.");
        }

        return (true, null);
    }

    /// <summary>
    /// True when Windows accepts the file's Authenticode signature and the signer is Valve. Valve's SteamCMD
    /// certificate expired in 2015 but the signature is timestamped, which WinVerifyTrust honours (X509Chain does not).
    /// </summary>
    private static bool IsSignedByValve(string path)
    {
        if (!WinTrust.IsSignatureTrusted(path)) return false;

        try
        {
#pragma warning disable SYSLIB0057 // The signer certificate of a signed PE file has no replacement loader in .NET 8.
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false).Equals("Valve", StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private async Task<string> RunAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = SteamCmdExe,
            Arguments = arguments,
            WorkingDirectory = _steamCmdDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var output = new StringBuilder();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        lock (output) return output.ToString();
    }

    private static void MoveContent(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, overwrite: true);
        }

        // SteamCMD keeps its own copy otherwise, doubling the disk space each item takes.
        Directory.Delete(sourceDirectory, recursive: true);
    }
}
