namespace BlueStar.Core.Models;

/// <summary>How a Windows game is started on Linux.</summary>
public enum CompatLaunchStrategy
{
    /// <summary>umu-launcher: Proton inside the Steam Linux Runtime, without Steam. Default.</summary>
    Umu,

    /// <summary>Proton's own <c>proton</c> script. Used automatically when umu cannot run.</summary>
    ProtonDirect,

    /// <summary>Let Steam start it (needs a Steam shortcut or a Steam-owned game). Opt-in.</summary>
    ThroughSteam,
}

/// <summary>Linux compatibility (Wine/Proton) settings: global on AppSettings, optionally overridden per instance.</summary>
public sealed record CompatSettings
{
    /// <summary>"UMU-Proton (auto)" in Settings: PROTONPATH left unset, so umu fetches and updates UMU-Proton itself.</summary>
    public const string UmuProtonAuto = "UMU-Proton";

    /// <summary>umu codename (PROTONPATH=GE-Proton) for the latest GE-Proton, fetched by umu.</summary>
    public const string GeProtonLatest = "GE-Proton";

    public CompatLaunchStrategy Strategy { get; init; } = CompatLaunchStrategy.Umu;

    /// <summary>A Proton folder, or one of the umu keywords. Null = <see cref="UmuProtonAuto"/>.</summary>
    public string? Runner { get; init; }

    public IReadOnlyDictionary<string, string> ExtraEnvironment { get; init; } = new Dictionary<string, string>();

    /// <summary>Extra WINEDLLOVERRIDES entries, e.g. <c>d3d9=n,b</c>, joined with ';'.</summary>
    public string? ExtraDllOverrides { get; init; }

    /// <summary>
    /// The per-instance override wins field by field: runner when set, environment merged (instance keys win),
    /// DLL overrides appended. The strategy stays global.
    /// </summary>
    public CompatSettings WithOverride(CompatSettings? instanceOverride)
    {
        if (instanceOverride is null)
            return this;

        var env = new Dictionary<string, string>(ExtraEnvironment);
        foreach (var (key, value) in instanceOverride.ExtraEnvironment)
            env[key] = value;

        var overrides = string.Join(';', new[] { ExtraDllOverrides, instanceOverride.ExtraDllOverrides }
            .Where(o => !string.IsNullOrWhiteSpace(o)));

        return this with
        {
            Runner = string.IsNullOrWhiteSpace(instanceOverride.Runner) ? Runner : instanceOverride.Runner,
            ExtraEnvironment = env,
            ExtraDllOverrides = overrides.Length == 0 ? null : overrides,
        };
    }

    /// <summary>"KEY=value" per line; blank lines and # comments skipped. <paramref name="bad"/> is the first invalid line.</summary>
    public static Dictionary<string, string> ParseEnvironment(string text, out string? bad)
    {
        bad = null;
        var env = new Dictionary<string, string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                bad = line;
                return env;
            }
            env[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return env;
    }
}
