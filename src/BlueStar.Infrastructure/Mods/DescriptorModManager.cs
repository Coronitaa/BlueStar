using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Mods;

/// <summary>
/// Generic mod manager for games that keep one folder per mod with a manifest file and an enabled-list
/// in a config file. Game specifics live in <see cref="ModDescriptor"/> data (ModDescriptors.json).
/// A mod's Id is its manifest path relative to the install folder, the same string the enabled list stores.
/// </summary>
public sealed class DescriptorModManager : IDescriptorModManager
{
    private const long MaxUncompressedBytes = 4L * 1024 * 1024 * 1024;
    private readonly ILogger<DescriptorModManager> _logger;
    private readonly IReadOnlyList<ModDescriptor> _descriptors;

    public string Id => "descriptor";
    public string DisplayName => "Manifest-based mod manager";

    public DescriptorModManager(ILogger<DescriptorModManager> logger)
        : this(logger, LoadEmbeddedDescriptors()) { }

    internal DescriptorModManager(ILogger<DescriptorModManager> logger, IReadOnlyList<ModDescriptor> descriptors)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _descriptors = descriptors;
    }

    public static IReadOnlyList<ModDescriptor> LoadEmbeddedDescriptors()
    {
        using var stream = typeof(DescriptorModManager).Assembly.GetManifestResourceStream("ModDescriptors.json");
        if (stream == null) return [];
        return JsonSerializer.Deserialize<List<ModDescriptor>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    public ModDescriptor? FindDescriptor(GameInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.InstallPath)) return null;
        return _descriptors.FirstOrDefault(d =>
            (instance.AppId != 0 && d.AppIds.Contains(instance.AppId)) ||
            d.Executables.Any(e => File.Exists(Path.Combine(instance.InstallPath, e))));
    }

    public bool IsSupported(GameInstance instance) => FindDescriptor(instance) != null;

    public bool CanInstall(GameInstance instance, string sourcePath) =>
        FindDescriptor(instance) != null &&
        (Directory.Exists(sourcePath) || (File.Exists(sourcePath) && sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)));

    public string GetModsDirectory(GameInstance instance)
    {
        var d = FindDescriptor(instance);
        return d == null || d.ModFolders.Count == 0 ? string.Empty : Path.GetFullPath(Path.Combine(instance.InstallPath, d.ModFolders[0]));
    }

    // ---- list ----

    public Task<IReadOnlyList<ModItem>> GetInstalledModsAsync(GameInstance instance, CancellationToken ct = default)
    {
        var d = FindDescriptor(instance);
        var result = new List<ModItem>();
        if (d != null)
        {
            var enabled = d.EnableList == null ? null : ReadEnabled(instance, d.EnableList);
            foreach (var folder in d.ModFolders)
            {
                var root = Path.Combine(instance.InstallPath, folder);
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    ct.ThrowIfCancellationRequested();
                    var manifest = Path.Combine(dir, d.Manifest.FileName);
                    if (!File.Exists(manifest)) continue;
                    var info = ReadManifest(manifest, d.Manifest);
                    var id = ModId(instance, manifest);
                    result.Add(new ModItem
                    {
                        Id = id,
                        Name = info.Name ?? Path.GetFileName(dir),
                        Version = info.Version,
                        FilePath = dir,
                        IsEnabled = enabled == null || enabled.Contains(Norm(id)),
                        SizeBytes = DirSize(dir),
                        Category = d.DisplayName
                    });
                }
            }
        }
        return Task.FromResult<IReadOnlyList<ModItem>>(result);
    }

    // ---- install ----

    public Task<bool> InstallModAsync(GameInstance instance, string sourceFilePath, CancellationToken ct = default)
    {
        var d = FindDescriptor(instance);
        if (d == null || d.ModFolders.Count == 0) return Task.FromResult(false);
        try
        {
            var modsRoot = Path.GetFullPath(Path.Combine(instance.InstallPath, d.ModFolders[0]));
            Directory.CreateDirectory(modsRoot);
            return Task.FromResult(Directory.Exists(sourceFilePath)
                ? InstallFolder(d, sourceFilePath, modsRoot)
                : InstallZip(d, sourceFilePath, modsRoot));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mod install from {Source} failed", sourceFilePath);
            return Task.FromResult(false);
        }
    }

    private bool InstallFolder(ModDescriptor d, string source, string modsRoot)
    {
        var manifestName = d.Manifest.FileName;
        var rootDir = new[] { source }.Concat(Directory.EnumerateDirectories(source))
            .FirstOrDefault(p => File.Exists(Path.Combine(p, manifestName)));
        if (rootDir == null) { _logger.LogWarning("No {Manifest} in {Source}", manifestName, source); return false; }

        var name = ReadManifest(Path.Combine(rootDir, manifestName), d.Manifest).Name;
        var folderName = rootDir == source ? name ?? Path.GetFileName(source.TrimEnd('\\', '/')) : Path.GetFileName(rootDir);
        var target = TargetDir(modsRoot, folderName);
        if (target == null || Directory.Exists(target)) return false;

        try { CopyTree(rootDir, target); }
        catch { TryDelete(target); throw; }
        return true;
    }

    private bool InstallZip(ModDescriptor d, string zipPath, string modsRoot)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.Where(e => e.Name.Length > 0)
            .Select(e => (Entry: e, Parts: e.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)))
            .ToList();

        // Path traversal and absolute paths anywhere in the archive reject the whole package.
        if (entries.Any(e => e.Parts.Any(p => p == ".." || p.Contains(':')) || e.Entry.FullName.StartsWith('/') || e.Entry.FullName.StartsWith('\\')))
        {
            _logger.LogWarning("Rejected {Zip}: unsafe entry path", zipPath);
            return false;
        }
        if (entries.Sum(e => e.Entry.Length) > MaxUncompressedBytes) return false;

        var manifest = entries
            .Where(e => e.Parts.Length <= 2 && e.Parts[^1].Equals(d.Manifest.FileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Parts.Length)
            .Select(e => new { e.Entry, e.Parts })
            .FirstOrDefault();
        if (manifest == null) { _logger.LogWarning("No {Manifest} in {Zip}", d.Manifest.FileName, zipPath); return false; }

        var prefixLen = manifest.Parts.Length - 1;
        string? manifestName;
        using (var ms = manifest.Entry.Open())
            manifestName = ReadManifest(ms, d.Manifest).Name;

        var folderName = prefixLen == 0 ? manifestName ?? Path.GetFileNameWithoutExtension(zipPath) : manifest.Parts[0];
        var target = TargetDir(modsRoot, folderName);
        if (target == null || Directory.Exists(target)) return false;

        try
        {
            foreach (var (entry, parts) in entries)
            {
                if (parts.Length <= prefixLen || (prefixLen == 1 && parts[0] != manifest.Parts[0])) continue;
                var dest = Path.GetFullPath(Path.Combine(new[] { target }.Concat(parts.Skip(prefixLen)).ToArray()));
                if (!dest.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unsafe entry path");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest);
            }
        }
        catch { TryDelete(target); throw; }
        return true;
    }

    // ---- enable / disable ----

    public Task<bool> ToggleModAsync(GameInstance instance, string modId, bool isEnabled, CancellationToken ct = default)
    {
        var d = FindDescriptor(instance);
        if (d?.EnableList == null) return Task.FromResult(false);
        try
        {
            return Task.FromResult(EditEnabledList(instance, d.EnableList, modId, isEnabled));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update {File}", d.EnableList.File);
            return Task.FromResult(false);
        }
    }

    // ---- uninstall ----

    public Task<bool> UninstallModAsync(GameInstance instance, string modId, CancellationToken ct = default)
    {
        var d = FindDescriptor(instance);
        if (d == null) return Task.FromResult(false);
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(instance.InstallPath, modId)));
            var parent = dir == null ? null : Path.GetDirectoryName(dir);
            // Only direct children of a configured mod folder may be deleted.
            var isModDir = parent != null && d.ModFolders.Any(f =>
                string.Equals(Path.GetFullPath(Path.Combine(instance.InstallPath, f)).TrimEnd('\\', '/'), parent.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (!isModDir) return Task.FromResult(false);

            if (d.EnableList != null) EditEnabledList(instance, d.EnableList, modId, false);
            if (Directory.Exists(dir)) Directory.Delete(dir!, recursive: true);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Uninstall of {Mod} failed", modId);
            return Task.FromResult(false);
        }
    }

    // ---- helpers ----

    private static string ModId(GameInstance instance, string manifestPath) =>
        Path.GetRelativePath(instance.InstallPath, manifestPath).Replace('\\', '/');

    private static string Norm(string? p) => (p ?? "").Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    private static string? PathOf(XElement item, ModEnableListDescriptor el) =>
        item.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(el.PathAttribute, StringComparison.OrdinalIgnoreCase))?.Value;

    private static IEnumerable<XElement> Items(XElement? container, ModEnableListDescriptor el) =>
        (container?.Elements() ?? []).Where(e => e.Name.LocalName.Equals(el.ItemElement, StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> ReadEnabled(GameInstance instance, ModEnableListDescriptor el)
    {
        var set = new HashSet<string>();
        var file = Path.Combine(instance.InstallPath, el.File);
        if (!File.Exists(file)) return set;
        try
        {
            foreach (var item in Items(FindContainer(XDocument.Load(file), el, create: false), el))
                set.Add(Norm(PathOf(item, el)));
        }
        catch (XmlException) { }
        return set;
    }

    private static XElement? FindContainer(XDocument doc, ModEnableListDescriptor el, bool create)
    {
        XElement? cur = doc.Root;
        foreach (var part in el.ListElementPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur == null) return null;
            var next = cur.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (next == null && create) { next = new XElement(part); cur.Add(next); }
            cur = next;
        }
        return cur;
    }

    private static bool EditEnabledList(GameInstance instance, ModEnableListDescriptor el, string modId, bool enable)
    {
        var file = Path.Combine(instance.InstallPath, el.File);
        if (!File.Exists(file)) return false;

        // Whitespace and comments are preserved so everything the launcher does not own stays untouched.
        var doc = XDocument.Load(file, LoadOptions.PreserveWhitespace);
        var container = FindContainer(doc, el, create: enable);
        var key = Norm(modId);
        var matches = Items(container, el).Where(e => Norm(PathOf(e, el)) == key).ToList();

        if (enable == (matches.Count > 0)) return true; // already in the requested state
        if (enable) container!.Add(new XElement(el.ItemElement, new XAttribute(el.PathAttribute, modId)));
        else foreach (var m in matches) m.Remove();

        File.Copy(file, file + ".bluestar.bak", overwrite: true);
        var tmp = file + ".bluestar.tmp";
        using (var w = XmlWriter.Create(tmp, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(HasBom(file)),
            OmitXmlDeclaration = doc.Declaration == null
        }))
            doc.Save(w);
        File.Move(tmp, file, overwrite: true);
        return true;
    }

    private static bool HasBom(string file)
    {
        using var fs = File.OpenRead(file);
        var b = new byte[3];
        return fs.Read(b, 0, 3) == 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
    }

    private (string? Name, string? Version) ReadManifest(string path, ModManifestDescriptor m)
    {
        try { using var s = File.OpenRead(path); return ReadManifest(s, m); }
        catch (IOException ex) { _logger.LogDebug(ex, "Unreadable manifest {Path}", path); return (null, null); }
    }

    private static (string? Name, string? Version) ReadManifest(Stream s, ModManifestDescriptor m)
    {
        try
        {
            if (m.Format.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(s);
                string? Get(string? f) => f != null && doc.RootElement.TryGetProperty(f, out var v) ? v.ToString() : null;
                return (Get(m.NameField), Get(m.VersionField));
            }
            var root = XDocument.Load(s).Root;
            if (root == null || (!string.IsNullOrEmpty(m.RootElement) && !root.Name.LocalName.Equals(m.RootElement, StringComparison.OrdinalIgnoreCase)))
                return (null, null);
            string? Attr(string? f) => f == null ? null : root.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(f, StringComparison.OrdinalIgnoreCase))?.Value;
            return (Attr(m.NameField), Attr(m.VersionField));
        }
        catch (Exception ex) when (ex is XmlException or JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Folder under <paramref name="modsRoot"/> for a mod name, or null when the name cannot be a safe single folder.</summary>
    private static string? TargetDir(string modsRoot, string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length == 0) return null;
        var full = Path.GetFullPath(Path.Combine(modsRoot, clean));
        return string.Equals(Path.GetDirectoryName(full), modsRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static void CopyTree(string src, string dst)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src, "*", opts))
        {
            var dest = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
    }

    private static long DirSize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (IOException) { return 0; }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
    }
}
