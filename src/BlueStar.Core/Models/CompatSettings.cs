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
    /// <summary>umu keyword picked when no runner is chosen: Proton fetched and kept up to date by umu itself.</summary>
    public const string UmuProtonAuto = "UMU-Proton";

    /// <summary>umu keyword for the latest GE-Proton, also fetched by umu.</summary>
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
}
