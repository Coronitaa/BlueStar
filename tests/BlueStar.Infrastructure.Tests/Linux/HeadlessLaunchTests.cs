using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Launcher;
using FluentAssertions;
using Moq;
using Xunit;

namespace BlueStar.Infrastructure.Tests.Linux;

public class HeadlessLaunchTests
{
    private readonly Mock<IInstanceManager> _instances = new();
    private readonly Mock<IGameLauncher> _launcher = new();

    [Fact]
    public async Task RunAsync_RejectsBadId()
    {
        var code = await HeadlessLaunch.RunAsync(_instances.Object, _launcher.Object, "not-a-guid", TextWriter.Null);

        code.Should().Be(HeadlessLaunch.ExitBadId);
    }

    [Fact]
    public async Task RunAsync_ReportsMissingInstance()
    {
        var code = await HeadlessLaunch.RunAsync(_instances.Object, _launcher.Object, Guid.NewGuid().ToString(), TextWriter.Null);

        code.Should().Be(HeadlessLaunch.ExitNotFound);
    }

    [Fact]
    public async Task RunAsync_WaitsForExitThenNotifiesUi()
    {
        var instance = new GameInstance { Id = Guid.NewGuid(), Name = "Test", InstallPath = "/games/test" };
        _instances.Setup(m => m.GetByIdAsync(instance.Id, It.IsAny<CancellationToken>())).ReturnsAsync(instance);
        var running = true;
        _launcher.Setup(l => l.IsRunning(instance.Id)).Returns(() => running);
        _launcher.Setup(l => l.LaunchAsync(instance, It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GameLaunchResult(true, "ok", 1));
        Guid? notified = null;

        var run = HeadlessLaunch.RunAsync(_instances.Object, _launcher.Object, instance.Id.ToString(), TextWriter.Null, id => notified = id);
        await Task.Delay(50);
        run.IsCompleted.Should().BeFalse("the game is still running");
        notified.Should().BeNull();

        running = false;
        _launcher.Raise(l => l.RunningStateChanged += null, _launcher.Object, (instance.Id, false));

        (await run.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(HeadlessLaunch.ExitOk);
        notified.Should().Be(instance.Id);
    }

    [Fact]
    public async Task RunAsync_ReturnsFailure_WhenLaunchFails()
    {
        var instance = new GameInstance { Id = Guid.NewGuid(), Name = "Test", InstallPath = "/games/test" };
        _instances.Setup(m => m.GetByIdAsync(instance.Id, It.IsAny<CancellationToken>())).ReturnsAsync(instance);
        _launcher.Setup(l => l.LaunchAsync(instance, It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GameLaunchResult(false, "no exe"));

        var code = await HeadlessLaunch.RunAsync(_instances.Object, _launcher.Object, instance.Id.ToString(), TextWriter.Null);

        code.Should().Be(HeadlessLaunch.ExitLaunchFailed);
    }
}
