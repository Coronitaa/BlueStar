using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using BlueStar.Infrastructure.DepotBox;
using BlueStar.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueStar.Infrastructure.Tests;

public sealed class DepotBoxKeyImporterTests : IDisposable
{
    private static readonly string AppKey = new('a', 64);
    private static readonly string DepotKey = "0123456789ABCDEF".PadRight(64, 'b');

    private static readonly string Lua = $"""
        -- MECCHA CHAMELEON
        addappid(4704690, 1, "{AppKey}")
        addappid(4704691, 1, "{DepotKey}") -- Windows depot
        setManifestid(4704691, "7668828481613758819", 3661402193)
        addappid(228989, 1, "not-a-key")
        addappid(228990)
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bluestar_keyimport_" + Guid.NewGuid().ToString("N"));
    private readonly DepotKeyRepository _repository;
    private readonly DepotBoxLuaParser _luaParser = new(NullLogger<DepotBoxLuaParser>.Instance);
    private readonly DepotBoxKeyImporter _importer;

    public DepotBoxKeyImporterTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new DepotKeyRepository(NullLogger<DepotKeyRepository>.Instance, Path.Combine(_root, "depotkeys.json"));
        _importer = new DepotBoxKeyImporter(_repository, NullLogger<DepotBoxKeyImporter>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task ParseLuaAsync_StoresTheAppLevelKey_ThatDecryptsWorkshopContent()
    {
        // Arrange
        var parser = new DepotBoxArchiveParser(NullLogger<DepotBoxArchiveParser>.Instance, _luaParser, _importer);

        // Act
        await parser.ParseLuaAsync(Lua);

        // Assert
        (await _repository.GetKeyAsync(4704690)).Should().Be(AppKey);
        (await _repository.GetKeyAsync(4704691)).Should().BeEquivalentTo(DepotKey);
    }

    [Fact]
    public async Task ImportAsync_SkipsMissingAndMalformedKeys()
    {
        // Act
        var count = await _importer.ImportAsync(_luaParser.Parse(Lua));

        // Assert
        count.Should().Be(2);
        (await _repository.GetKeyAsync(228989)).Should().BeNull();
        (await _repository.GetKeyAsync(228990)).Should().BeNull();
    }

    [Fact]
    public async Task ImportFolderAsync_CollectsKeysFromArchivesAlreadyOnDisk()
    {
        // Arrange: a DepotBox archive downloaded by an earlier BlueStar version, plus a broken file
        var archives = Directory.CreateDirectory(Path.Combine(_root, "archives")).FullName;
        using (var zip = ZipFile.Open(Path.Combine(archives, "4704690.zip"), ZipArchiveMode.Create))
        {
            await using var writer = new StreamWriter(zip.CreateEntry("4704690.lua").Open());
            await writer.WriteAsync(Lua);
        }
        await File.WriteAllTextAsync(Path.Combine(archives, "broken.zip"), "not a zip");

        // Act
        var count = await _importer.ImportFolderAsync(archives, _luaParser);

        // Assert
        count.Should().Be(2);
        (await _repository.GetKeyAsync(4704690)).Should().Be(AppKey);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("zz00000000000000000000000000000000000000000000000000000000000000", false)]
    [InlineData("ddf5e0ab00000000000000000000000000000000000000000000000000000000", true)]
    public void IsDepotKey_AcceptsOnly64HexCharacters(string? key, bool expected)
    {
        DepotBoxKeyImporter.IsDepotKey(key).Should().Be(expected);
    }
}
