using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace BlueStar.Infrastructure.Workshop;

/// <summary>
/// Runs the DepotDownloader command line (used for Workshop pubfile downloads) in its own process.
/// BlueStar.exe hosts it: it re-launches itself with <see cref="HostSwitch"/> and runs the CLI of the
/// BlueStar.DepotDownloader library it already ships, on its own self-contained .NET runtime. A standalone
/// DepotDownloaderMod.exe is only a fallback, because it needs a .NET Runtime installed on the machine.
/// </summary>
public static class DepotDownloaderHost
{
    /// <summary>Command-line switch that makes BlueStar.exe run the DepotDownloader CLI instead of the app.</summary>
    public const string HostSwitch = "--depotdownloader";

    /// <summary>BlueStar.exe, set by the app at startup. Null when running outside the app (e.g. tests).</summary>
    public static string? HostExecutablePath { get; set; }

    private static bool CanHost => !string.IsNullOrEmpty(HostExecutablePath) && File.Exists(HostExecutablePath);

    /// <summary>Whether DepotDownloader can be started, through the host or the standalone executable.</summary>
    public static bool IsAvailable(string? standaloneExePath) =>
        CanHost || (!string.IsNullOrEmpty(standaloneExePath) && File.Exists(standaloneExePath));

    public static ProcessStartInfo CreateStartInfo(string? standaloneExePath, string arguments)
    {
        if (CanHost)
        {
            return NewStartInfo(HostExecutablePath!, $"{HostSwitch} {arguments}", Path.GetTempPath());
        }

        if (string.IsNullOrEmpty(standaloneExePath) || !File.Exists(standaloneExePath))
        {
            throw new FileNotFoundException("DepotDownloader is not available.", standaloneExePath);
        }

        var psi = NewStartInfo(standaloneExePath, arguments, Path.GetDirectoryName(standaloneExePath)!);

        // Point the standalone tool at a runtime shipped next to BlueStar only if one really is there (host/fxr layout).
        if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "host", "fxr")))
        {
            psi.EnvironmentVariables["DOTNET_ROOT"] = AppContext.BaseDirectory;
        }

        return psi;
    }

    /// <summary>
    /// Runs the DepotDownloader CLI in the current process (the host side of <see cref="HostSwitch"/>) and returns its exit code.
    /// </summary>
    public static int RunCommandLine(string[] args)
    {
        var result = ResolveCommandLineEntryPoint().Invoke(null, [args]);
        return result switch
        {
            System.Threading.Tasks.Task<int> task => task.GetAwaiter().GetResult(),
            int code => code,
            _ => 0
        };
    }

    // The CLI entry point stayed in the library when it was turned into one; it is not public API.
    internal static MethodInfo ResolveCommandLineEntryPoint() =>
        typeof(DepotDownloader.DepotDownloadEngine).Assembly
            .GetType("DepotDownloader.Program")?
            .GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [typeof(string[])])
        ?? throw new MissingMethodException("DepotDownloader.Program", "Main");

    private static ProcessStartInfo NewStartInfo(string fileName, string arguments, string workingDirectory) => new()
    {
        FileName = fileName,
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = workingDirectory
    };
}
