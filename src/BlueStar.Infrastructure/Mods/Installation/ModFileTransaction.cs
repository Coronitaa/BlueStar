using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BlueStar.Core.Models;

namespace BlueStar.Infrastructure.Mods.Installation;

/// <summary>
/// Journaled set of file changes inside a game folder. The first time a path is touched its previous
/// state is saved, so <see cref="Rollback"/> puts the folder back exactly as it was.
/// </summary>
internal sealed class ModFileTransaction : IDisposable
{
    private readonly string _root;
    private readonly string _backupDirectory;
    private readonly string _journalDirectory;
    private readonly Dictionary<string, string?> _preImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _createdDirectories = [];
    private readonly List<string> _newBackups = [];
    private bool _completed;

    /// <param name="root">Game install folder.</param>
    /// <param name="backupDirectory">Persistent backups of game files this mod replaces.</param>
    /// <param name="journalDirectory">Scratch folder for pre-images, deleted when the transaction ends.</param>
    public ModFileTransaction(string root, string backupDirectory, string journalDirectory)
    {
        _root = Path.GetFullPath(root);
        _backupDirectory = backupDirectory;
        _journalDirectory = journalDirectory;
    }

    /// <summary>Folders created by this transaction, relative to the root, parents first.</summary>
    public IReadOnlyList<string> CreatedDirectories => _createdDirectories;

    /// <summary>Resolves a relative destination and rejects anything that escapes the game folder.</summary>
    public string FullPath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidOperationException($"Mod destination must be relative: '{relativePath}'.");

        var full = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Mod destination escapes the game folder: '{relativePath}'.");

        return full;
    }

    public string BackupPath(string relativePath) => Path.Combine(_backupDirectory, relativePath);

    /// <summary>
    /// Copies <paramref name="sourcePath"/> to <paramref name="relativePath"/>. When the destination is a game file
    /// (not owned by this mod) and has no backup yet, it is backed up so uninstall can restore it.
    /// </summary>
    public InstalledModFile Copy(string sourcePath, string relativePath, bool ownedByThisMod, bool previouslyReplacedExisting = false)
    {
        var destination = FullPath(relativePath);
        CreateParentDirectories(destination);
        SavePreImage(relativePath, destination);

        var replacedExisting = previouslyReplacedExisting;
        if (File.Exists(destination) && !ownedByThisMod)
        {
            var backup = BackupPath(relativePath);
            if (!File.Exists(backup))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(destination, backup);
                _newBackups.Add(backup);
            }

            replacedExisting = true;
        }

        var (size, hash) = CopyWithHash(sourcePath, destination);
        return new InstalledModFile(relativePath, size, hash) { ReplacedExisting = replacedExisting };
    }

    /// <summary>Deletes a mod file and, when it had replaced a game file, puts the original back.</summary>
    public void Remove(string relativePath, bool restoreBackup)
    {
        var destination = FullPath(relativePath);
        SavePreImage(relativePath, destination);

        if (File.Exists(destination)) File.Delete(destination);

        var backup = BackupPath(relativePath);
        if (restoreBackup && File.Exists(backup))
        {
            File.Copy(backup, destination);
        }
    }

    /// <summary>Keeps the changes and discards the journal.</summary>
    public void Commit()
    {
        _completed = true;
        DeleteJournal();
    }

    /// <summary>Restores every touched file and removes folders and backups this transaction created.</summary>
    public void Rollback()
    {
        if (_completed) return;
        _completed = true;

        foreach (var (relativePath, preImage) in _preImages)
        {
            var destination = FullPath(relativePath);
            if (File.Exists(destination)) File.Delete(destination);
            if (preImage != null) File.Copy(preImage, destination);
        }

        foreach (var backup in _newBackups.Where(File.Exists))
        {
            File.Delete(backup);
        }

        RemoveEmptyDirectories(_root, _createdDirectories);
        DeleteJournal();
    }

    public void Dispose()
    {
        if (!_completed) Rollback();
    }

    /// <summary>Deletes the given folders (relative to <paramref name="root"/>) when empty, deepest first.</summary>
    public static void RemoveEmptyDirectories(string root, IEnumerable<string> relativeDirectories)
    {
        foreach (var dir in relativeDirectories.OrderByDescending(d => d.Length))
        {
            var full = Path.Combine(root, dir);
            if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
            {
                Directory.Delete(full);
            }
        }
    }

    private void SavePreImage(string relativePath, string destination)
    {
        if (_preImages.ContainsKey(relativePath)) return;

        if (!File.Exists(destination))
        {
            _preImages[relativePath] = null;
            return;
        }

        var preImage = Path.Combine(_journalDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(preImage)!);
        File.Copy(destination, preImage, overwrite: true);
        _preImages[relativePath] = preImage;
    }

    private void CreateParentDirectories(string destination)
    {
        var missing = new Stack<string>();
        var dir = Path.GetDirectoryName(destination);
        while (dir != null && dir.Length > _root.Length && !Directory.Exists(dir))
        {
            missing.Push(dir);
            dir = Path.GetDirectoryName(dir);
        }

        while (missing.Count > 0)
        {
            var created = missing.Pop();
            Directory.CreateDirectory(created);
            _createdDirectories.Add(Path.GetRelativePath(_root, created));
        }
    }

    /// <summary>Removes <paramref name="directory"/> when it exists and has nothing left in it.</summary>
    public static void DeleteIfEmpty(string? directory)
    {
        if (directory != null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }

    private static (long Size, string Sha256) CopyWithHash(string sourcePath, string destination)
    {
        using var input = File.OpenRead(sourcePath);
        using var output = File.Create(destination);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
            total += read;
        }

        return (total, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
    }

    private void DeleteJournal()
    {
        try
        {
            if (Directory.Exists(_journalDirectory)) Directory.Delete(_journalDirectory, recursive: true);
            DeleteIfEmpty(Path.GetDirectoryName(_journalDirectory));
        }
        catch (IOException)
        {
            // A leftover scratch folder is harmless; it lives outside the game folder.
        }
    }
}
