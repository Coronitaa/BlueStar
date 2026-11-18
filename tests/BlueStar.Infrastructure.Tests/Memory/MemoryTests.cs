using System.Runtime.CompilerServices;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Compat;
using BlueStar.Infrastructure.Instance;
using BlueStar.Infrastructure.Launcher;
using BlueStar.Infrastructure.Steam;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace BlueStar.Infrastructure.Tests.Memory;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MemoryCollection
{
    public const string Name = "Memory";
}

/// <summary>
/// Leak checks that run on Windows and Linux: repeat a workload, then compare the managed heap after a full GC
/// between an early and a late checkpoint. Growth must stay under a budget far below what a per-iteration leak
/// would add. Not parallelised: other tests would move the heap.
/// </summary>
[Collection(MemoryCollection.Name)]
[Trait("Category", "Memory")]
public sealed class MemoryTests : IDisposable
{
    private const long OneMb = 1024 * 1024;
    private readonly ITestOutputHelper _out;
    private readonly string _root = Directory.CreateTempSubdirectory("bs-mem-").FullName;

    public MemoryTests(ITestOutputHelper output) => _out = output;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static long SettledHeap()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private long Report(string name, long warm, long end)
    {
        var growth = end - warm;
        _out.WriteLine($"[memory] {name}: warm {warm / 1024} KB -> end {end / 1024} KB (growth {growth / 1024} KB) on {Environment.OSVersion.Platform}");
        return growth;
    }

    private static string ShortLivedExe() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.SystemDirectory, "whoami.exe")
        : "/bin/true";

    [Fact]
    public async Task GameLauncher_RepeatedLaunches_ReleaseProcessesAndHandlers()
    {
        var launcher = new GameLauncherService(NullLogger<GameLauncherService>.Instance);
        var instance = new GameInstance { Name = "Mem", InstallPath = _root, ExecutablePath = ShortLivedExe() };

        async Task RunOnce()
        {
            var exited = new TaskCompletionSource();
            void OnState(object? s, (Guid InstanceId, bool IsRunning) e) { if (!e.IsRunning) exited.TrySetResult(); }
            launcher.RunningStateChanged += OnState;
            var result = await launcher.LaunchAsync(instance);
            result.Success.Should().BeTrue(result.Message);
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(20));
            launcher.RunningStateChanged -= OnState;
        }

        for (var i = 0; i < 10; i++) await RunOnce();
        var warm = SettledHeap();
        for (var i = 0; i < 60; i++) await RunOnce();
        var growth = Report("GameLauncher x60", warm, SettledHeap());

        launcher.IsRunning(instance.Id).Should().BeFalse();
        growth.Should().BeLessThan(2 * OneMb);
    }

    [Fact]
    public async Task HeadlessLaunch_AlwaysUnsubscribesFromTheLauncher()
    {
        var launcher = new CountingLauncher();
        var instance = new GameInstance { Name = "H", InstallPath = _root };
        var instances = new Mock<IInstanceManager>();
        instances.Setup(m => m.GetByIdAsync(instance.Id, It.IsAny<CancellationToken>())).ReturnsAsync(instance);

        for (var i = 0; i < 200; i++)
            (await HeadlessLaunch.RunAsync(instances.Object, launcher, instance.Id.ToString(), TextWriter.Null)).Should().Be(HeadlessLaunch.ExitOk);

        launcher.Subscribers.Should().Be(0);
    }

    [Fact]
    public void SteamStatusService_DisposedInstances_AreCollected()
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        static WeakReference CreateAndDispose()
        {
            var service = new SteamStatusService(NullLogger<SteamStatusService>.Instance);
            service.Dispose();
            return new WeakReference(service);
        }

        var refs = Enumerable.Range(0, 50).Select(_ => CreateAndDispose()).ToList();
        SettledHeap();

        refs.Count(r => r.IsAlive).Should().Be(0, "a disposed service must not stay rooted by its polling timer");
    }

    [Fact]
    public async Task InstanceManager_RepeatedLibraryLoads_DoNotGrow()
    {
        var root = Path.Combine(_root, "instances");
        var manager = new InstanceManager(NullLogger<InstanceManager>.Instance, rootPath: root);
        for (var i = 0; i < 40; i++)
            await manager.CreateAsync(new GameInstance { Name = $"Game {i}", InstallPath = Path.Combine(_root, "g", i.ToString()) });

        for (var i = 0; i < 20; i++) (await manager.GetAllAsync()).Should().HaveCount(40);
        var warm = SettledHeap();
        for (var i = 0; i < 300; i++) await manager.GetAllAsync();
        var growth = Report("InstanceManager.GetAll x300 (40 instances)", warm, SettledHeap());

        growth.Should().BeLessThan(2 * OneMb);
    }

    [Fact]
    public void CompatLauncher_Planning_DoesNotGrow()
    {
        var exe = Path.Combine(_root, "Game.exe");
        File.WriteAllText(exe, "");
        var health = new CompatHealth(true, false, true);
        var instance = new GameInstance { Name = "C", InstallPath = _root, AppId = 480, LaunchArguments = "-a \"b c\"" };
        var prefix = new CompatPrefix("/p", "/p");

        void Plan(int n)
        {
            for (var i = 0; i < n; i++)
                CompatLauncher.Plan(new CompatSettings(), instance, exe, prefix, "/steam", "/usr/bin/umu-run", health, []);
        }

        Plan(500);
        var warm = SettledHeap();
        Plan(5_000);
        var growth = Report("CompatLauncher.Plan x5000", warm, SettledHeap());

        growth.Should().BeLessThan(OneMb);
    }

    private sealed class CountingLauncher : IGameLauncher
    {
        private EventHandler<(Guid InstanceId, bool IsRunning)>? _running;
        public int Subscribers => _running?.GetInvocationList().Length ?? 0;

        public event EventHandler<(Guid InstanceId, bool IsRunning)>? RunningStateChanged
        {
            add => _running += value;
            remove => _running -= value;
        }

        public event EventHandler<(Guid InstanceId, string LogLine)>? LogReceived { add { } remove { } }

        public Task<GameLaunchResult> LaunchAsync(GameInstance instance, Action<string>? onLog = null, CancellationToken ct = default) =>
            Task.FromResult(new GameLaunchResult(true, "ok"));

        public Task<bool> KillAsync(Guid instanceId) => Task.FromResult(true);
        public bool IsRunning(Guid instanceId) => false;
        public int? GetProcessId(Guid instanceId) => null;
    }
}
