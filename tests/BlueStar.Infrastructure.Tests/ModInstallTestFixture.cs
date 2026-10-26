using System;
using System.IO;
using BlueStar.Core.Models;

namespace BlueStar.Infrastructure.Tests;

/// <summary>
/// Temporary game folder, staging folder and BlueStar data folder for mod install tests.
/// </summary>
internal sealed class ModInstallTestFixture : IDisposable
{
    public ModInstallTestFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "bluestar_modtests_" + Guid.NewGuid().ToString("N"));
        GameDir = Directory.CreateDirectory(Path.Combine(Root, "game")).FullName;
        PackagesDir = Directory.CreateDirectory(Path.Combine(Root, "packages")).FullName;
        DataDir = Directory.CreateDirectory(Path.Combine(Root, "data")).FullName;
        StagingDir = Path.Combine(Root, "staging");
    }

    public string Root { get; }
    public string GameDir { get; }
    public string PackagesDir { get; }
    public string DataDir { get; }
    public string StagingDir { get; }

    public GameInstance Instance(EngineType engine = EngineType.Generic) => new()
    {
        Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Name = "Test Game",
        InstallPath = GameDir,
        Engine = new EngineInfo { Id = engine.ToString().ToLowerInvariant(), Name = engine.ToString(), Type = engine }
    };

    /// <summary>Creates a file in the game folder.</summary>
    public string GameFile(string relativePath, string content = "game")
    {
        var full = Path.Combine(GameDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void GameFolder(string relativePath) => Directory.CreateDirectory(Path.Combine(GameDir, relativePath));

    /// <summary>Creates a package folder with the given files and returns its path.</summary>
    public string Package(string name, params (string Path, string Content)[] files)
    {
        var dir = Path.Combine(PackagesDir, name);
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return dir;
    }

    public string ReadGame(string relativePath) => File.ReadAllText(Path.Combine(GameDir, relativePath));

    public bool GameHas(string relativePath) => File.Exists(Path.Combine(GameDir, relativePath));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
