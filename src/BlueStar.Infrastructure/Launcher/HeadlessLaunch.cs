using BlueStar.Core.Interfaces;

namespace BlueStar.Infrastructure.Launcher;

/// <summary>
/// <c>BlueStar --launch &lt;instanceId&gt;</c>: launches one instance without the UI and stays alive until the game
/// exits. Steam shortcuts and .desktop files point here, because Steam's "playing" status and gamescope focus
/// need the game inside the process tree Steam started.
/// </summary>
public static class HeadlessLaunch
{
    public const string Switch = "--launch";

    /// <summary>Pipe message a running BlueStar UI receives after a headless session, so it reloads that instance.</summary>
    public const string InstanceChangedMessage = "bluestar:instance-changed:";

    public const int ExitOk = 0;
    public const int ExitLaunchFailed = 1;
    public const int ExitBadId = 2;
    public const int ExitNotFound = 3;

    public static async Task<int> RunAsync(
        IInstanceManager instances,
        IGameLauncher launcher,
        string idArg,
        TextWriter log,
        Action<Guid>? notifyUi = null,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(idArg, out var id))
        {
            await log.WriteLineAsync($"Invalid instance id: {idArg}");
            return ExitBadId;
        }

        var instance = await instances.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (instance == null)
        {
            await log.WriteLineAsync($"No instance with id {id}");
            return ExitNotFound;
        }

        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnState(object? sender, (Guid InstanceId, bool IsRunning) e)
        {
            if (e.InstanceId == id && !e.IsRunning)
                exited.TrySetResult();
        }

        launcher.RunningStateChanged += OnState;
        try
        {
            var result = await launcher.LaunchAsync(instance, line => log.WriteLine(line), ct).ConfigureAwait(false);
            if (!result.Success)
            {
                await log.WriteLineAsync(result.Message);
                return ExitLaunchFailed;
            }

            if (launcher.IsRunning(id))
                await exited.Task.WaitAsync(ct).ConfigureAwait(false);

            notifyUi?.Invoke(id);
            return ExitOk;
        }
        finally
        {
            launcher.RunningStateChanged -= OnState;
        }
    }
}
