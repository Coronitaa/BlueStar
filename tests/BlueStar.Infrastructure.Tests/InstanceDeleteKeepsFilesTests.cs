using BlueStar.Core.Models;
using BlueStar.Infrastructure.Instance;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public sealed class InstanceDeleteKeepsFilesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bs-delete-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // "Remove from BlueStar" without "delete files" must never touch the game: for a Steam import that folder is the
    // user's own Steam install.
    [Fact]
    public async Task DeleteAsync_ForgetsTheInstance_ButKeepsTheGameFolder()
    {
        var game = Directory.CreateDirectory(Path.Combine(_root, "steamapps", "common", "My Game")).FullName;
        File.WriteAllText(Path.Combine(game, "Game.exe"), "MZ");
        var manager = new InstanceManager(NullLogger<InstanceManager>.Instance, rootPath: Path.Combine(_root, "instances"));
        var created = await manager.CreateAsync(new GameInstance { Name = "My Game", InstallPath = game, Origin = InstanceOrigin.Steam });

        (await manager.DeleteAsync(created.Id)).Should().BeTrue();

        (await manager.GetByIdAsync(created.Id)).Should().BeNull();
        File.Exists(Path.Combine(game, "Game.exe")).Should().BeTrue();
    }
}
