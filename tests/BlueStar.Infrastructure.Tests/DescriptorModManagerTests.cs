using System.IO.Compression;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Mods;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

/// <summary>Runs against a scratch copy shaped like a Barotrauma install (BOM, comments, empty regularpackages).</summary>
public sealed class DescriptorModManagerTests : IDisposable
{
    private const string Config =
        "﻿<config\n  language=\"English\">\n  <contentpackages>\n    <!--Vanilla-->\n    <corepackage\n      path=\"Content/ContentPackages/Vanilla.xml\" />\n    <regularpackages />\n  </contentpackages>\n  <serverfilters />\n</config>";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bs-desc-" + Guid.NewGuid().ToString("N"));
    private readonly DescriptorModManager _manager = new(NullLogger<DescriptorModManager>.Instance, DescriptorModManager.LoadEmbeddedDescriptors());
    private readonly GameInstance _game;

    public DescriptorModManagerTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Barotrauma.exe"), "");
        File.WriteAllText(Path.Combine(_root, "config_player.xml"), Config);
        _game = new GameInstance { Name = "Barotrauma", InstallPath = _root };
    }

    public void Dispose() => Directory.Delete(_root, true);

    private string MakeModFolder(string parent, string folder, string name, string version = "1.0")
    {
        var dir = Path.Combine(parent, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "filelist.xml"),
            $"<contentpackage name=\"{name}\" modversion=\"{version}\" gameversion=\"1.0\"><Item file=\"a.xml\"/></contentpackage>");
        File.WriteAllText(Path.Combine(dir, "a.xml"), "<Items/>");
        return dir;
    }

    private string MakeZip(params (string Name, string Content)[] entries)
    {
        var zip = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var w = new StreamWriter(z.CreateEntry(name).Open());
            w.Write(content);
        }
        return zip;
    }

    private const string Manifest = "<contentpackage name=\"Cool Mod\" modversion=\"2.1\"/>";

    [Fact]
    public async Task Lists_mods_with_name_version_and_enabled_state()
    {
        MakeModFolder(Path.Combine(_root, "LocalMods"), "A", "Alpha", "1.2");
        MakeModFolder(Path.Combine(_root, "WorkshopMods", "Installed"), "123", "Beta");
        await _manager.ToggleModAsync(_game, "LocalMods/A/filelist.xml", true);

        var mods = await _manager.GetInstalledModsAsync(_game);

        mods.Should().HaveCount(2);
        var alpha = mods.Single(m => m.Name == "Alpha");
        alpha.Version.Should().Be("1.2");
        alpha.IsEnabled.Should().BeTrue();
        mods.Single(m => m.Name == "Beta").IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Toggle_edits_the_list_keeps_unknown_xml_and_backs_up()
    {
        MakeModFolder(Path.Combine(_root, "LocalMods"), "A", "Alpha");
        var cfg = Path.Combine(_root, "config_player.xml");

        (await _manager.ToggleModAsync(_game, "LocalMods/A/filelist.xml", true)).Should().BeTrue();

        var text = File.ReadAllText(cfg);
        text.Should().Contain("<!--Vanilla-->").And.Contain("<serverfilters />").And.Contain("Content/ContentPackages/Vanilla.xml");
        text.Should().Contain("LocalMods/A/filelist.xml");
        File.ReadAllBytes(cfg).Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        File.ReadAllText(cfg + ".bluestar.bak").Should().Be(Config.TrimStart((char)0xFEFF));

        (await _manager.ToggleModAsync(_game, "LocalMods/A/filelist.xml", false)).Should().BeTrue();
        File.ReadAllText(cfg).Should().NotContain("LocalMods/A");
    }

    [Fact]
    public async Task Install_from_zip_with_root_manifest_uses_manifest_name_and_does_not_enable()
    {
        var zip = MakeZip(("filelist.xml", Manifest), ("Items/x.xml", "<Items/>"));

        (await _manager.InstallModAsync(_game, zip)).Should().BeTrue();

        File.Exists(Path.Combine(_root, "LocalMods", "Cool Mod", "Items", "x.xml")).Should().BeTrue();
        (await _manager.GetInstalledModsAsync(_game)).Single().IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Install_from_zip_with_wrapping_folder_keeps_folder_name()
    {
        var zip = MakeZip(("Wrapped/filelist.xml", Manifest), ("Wrapped/a.xml", "<a/>"), ("Other/junk.txt", "x"));

        (await _manager.InstallModAsync(_game, zip)).Should().BeTrue();

        Directory.Exists(Path.Combine(_root, "LocalMods", "Wrapped")).Should().BeTrue();
        Directory.Exists(Path.Combine(_root, "LocalMods", "Other")).Should().BeFalse();
    }

    [Fact]
    public async Task Install_rejects_path_traversal_and_missing_manifest()
    {
        var evil = MakeZip(("filelist.xml", Manifest), ("../evil.txt", "x"));
        var noManifest = MakeZip(("readme.txt", "x"));

        (await _manager.InstallModAsync(_game, evil)).Should().BeFalse();
        (await _manager.InstallModAsync(_game, noManifest)).Should().BeFalse();

        File.Exists(Path.Combine(_root, "evil.txt")).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "LocalMods", "Cool Mod")).Should().BeFalse();
    }

    [Fact]
    public async Task Install_from_folder_copies_and_refuses_duplicates()
    {
        var src = MakeModFolder(Path.Combine(_root, "scratch"), "FolderMod", "Folder Mod");

        (await _manager.InstallModAsync(_game, src)).Should().BeTrue();
        (await _manager.InstallModAsync(_game, src)).Should().BeFalse();

        File.Exists(Path.Combine(_root, "LocalMods", "Folder Mod", "a.xml")).Should().BeTrue();
    }

    [Fact]
    public async Task Uninstall_removes_folder_and_enable_entry_but_refuses_outside_paths()
    {
        MakeModFolder(Path.Combine(_root, "LocalMods"), "A", "Alpha");
        await _manager.ToggleModAsync(_game, "LocalMods/A/filelist.xml", true);

        (await _manager.UninstallModAsync(_game, "../Barotrauma.exe")).Should().BeFalse();
        (await _manager.UninstallModAsync(_game, "LocalMods/A/filelist.xml")).Should().BeTrue();

        Directory.Exists(Path.Combine(_root, "LocalMods", "A")).Should().BeFalse();
        File.ReadAllText(Path.Combine(_root, "config_player.xml")).Should().NotContain("LocalMods/A");
        File.Exists(Path.Combine(_root, "Barotrauma.exe")).Should().BeTrue();
    }

    [Fact]
    public void Games_without_a_descriptor_are_not_supported()
    {
        var other = new GameInstance { Name = "Other", InstallPath = Path.Combine(_root, "scratch") };
        _manager.IsSupported(other).Should().BeFalse();
    }
}
