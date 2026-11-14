using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BlueStar.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Storage;

/// <summary>
/// Linux <see cref="ISecureStorage"/>. Uses the desktop keyring (Secret Service: GNOME Keyring, KWallet) through
/// libsecret's <c>secret-tool</c> when it is installed. Without it, values go to plain files readable only by the
/// user (mode 0600): there is no DPAPI equivalent, and inventing an encryption key stored next to the data
/// would only look safer.
/// </summary>
public sealed class LinuxSecureStorage : ISecureStorage
{
    private const string Attribute = "bluestar-key";

    private readonly ILogger<LinuxSecureStorage> _logger;
    private readonly string _storagePath;
    private readonly string? _secretTool;
    private bool _warnedPlain;

    public LinuxSecureStorage(ILogger<LinuxSecureStorage> logger)
        : this(logger,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueStar", "secure"),
            FindOnPath("secret-tool"))
    {
    }

    public LinuxSecureStorage(ILogger<LinuxSecureStorage> logger, string storagePath, string? secretTool)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _storagePath = storagePath;
        _secretTool = secretTool;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var hash = Hash(key);

        if (_secretTool != null)
        {
            var (code, output) = await RunSecretToolAsync(ct, null, "lookup", Attribute, hash).ConfigureAwait(false);
            if (code == 0)
                return output;
        }

        var file = FilePath(hash);
        return File.Exists(file) ? await File.ReadAllTextAsync(file, ct).ConfigureAwait(false) : null;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        var hash = Hash(key);

        if (_secretTool != null)
        {
            var (code, _) = await RunSecretToolAsync(ct, value, "store", "--label", $"BlueStar {hash[..8]}", Attribute, hash).ConfigureAwait(false);
            if (code == 0)
            {
                DeleteFile(hash); // keep a single copy, in the keyring
                return;
            }
            _logger.LogWarning("Secret Service rejected the value (no keyring unlocked?); storing it in a 0600 file instead");
        }

        if (!_warnedPlain)
        {
            _logger.LogWarning("No Secret Service available: secrets are stored unencrypted in {Path} (mode 0600)", _storagePath);
            _warnedPlain = true;
        }

        Directory.CreateDirectory(_storagePath);
        var file = FilePath(hash);
        await using (var stream = new FileStream(file, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(value), ct).ConfigureAwait(false);
        }
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); // also when the file already existed
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var hash = Hash(key);
        var deleted = false;

        if (_secretTool != null)
            deleted = (await RunSecretToolAsync(ct, null, "clear", Attribute, hash).ConfigureAwait(false)).ExitCode == 0;

        return DeleteFile(hash) || deleted;
    }

    private bool DeleteFile(string hash)
    {
        var file = FilePath(hash);
        if (!File.Exists(file))
            return false;
        File.Delete(file);
        return true;
    }

    private string FilePath(string hash) => Path.Combine(_storagePath, hash + ".secret");

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private async Task<(int ExitCode, string Output)> RunSecretToolAsync(CancellationToken ct, string? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo(_secretTool!)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi)!;
            if (stdin != null)
                await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return (process.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.LogWarning(ex, "secret-tool could not be started");
            return (-1, string.Empty);
        }
    }

    internal static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, name))
            .FirstOrDefault(File.Exists);
}
