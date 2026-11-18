using BlueStar.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace BlueStar.Infrastructure.Tests.Linux;

public class AddBlueStarTests
{
    [Fact]
    public void AddBlueStar_ResolvesWhatTheHeadlessLaunchNeeds()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<IDebugLogService>().Object);
        services.AddBlueStar();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IInstanceManager>().Should().NotBeNull();
        provider.GetRequiredService<IGameLauncher>().Should().NotBeNull();
        provider.GetRequiredService<ISteamStatusService>().Should().NotBeNull();
    }
}
