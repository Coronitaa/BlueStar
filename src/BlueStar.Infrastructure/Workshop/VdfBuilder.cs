using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BlueStar.Core.Interfaces;

namespace BlueStar.Infrastructure.Workshop;

/// <summary>
/// Builder and serializer for Steam Workshop manifest files (appworkshop_<AppId>.vdf)
/// conforming strictly to Valve KeyValues / VDF format.
/// </summary>
public static class VdfBuilder
{
    /// <summary>
    /// Generates the standard Steam Workshop VDF text content for an AppID and list of installed items.
    /// </summary>
    public static string GenerateAppWorkshopVdf(uint appId, IEnumerable<WorkshopItemInfo> items)
    {
        var itemList = items?.ToList() ?? [];
        long totalSize = itemList.Sum(i => (long)i.FileSizeBytes);
        long lastUpdated = itemList.Count > 0
            ? itemList.Max(i => i.UpdatedAt?.ToUnixTimeSeconds() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var sb = new StringBuilder();
        sb.AppendLine("\"AppWorkshop\"");
        sb.AppendLine("{");
        sb.AppendLine($"\t\"appid\"\t\t\"{appId}\"");
        sb.AppendLine($"\t\"SizeOnDisk\"\t\t\"{totalSize}\"");
        sb.AppendLine("\t\"NeedsUpdate\"\t\t\"0\"");
        sb.AppendLine($"\t\"TimeLastUpdated\"\t\t\"{lastUpdated}\"");
        sb.AppendLine("\t\"WorkshopItemsInstalled\"");
        sb.AppendLine("\t{");

        foreach (var item in itemList)
        {
            long itemUpdated = item.UpdatedAt?.ToUnixTimeSeconds() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            sb.AppendLine($"\t\t\"{item.PublishedFileId}\"");
            sb.AppendLine("\t\t{");
            sb.AppendLine($"\t\t\t\"size\"\t\t\"{item.FileSizeBytes}\"");
            sb.AppendLine($"\t\t\t\"timeupdated\"\t\t\"{itemUpdated}\"");
            sb.AppendLine("\t\t\t\"manifest\"\t\t\"0\"");
            sb.AppendLine("\t\t}");
        }

        sb.AppendLine("\t}");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Writes the generated appworkshop_<AppId>.vdf to the instance's steamapps/workshop directory.
    /// </summary>
    public static string WriteAppWorkshopFile(string instancePath, uint appId, IEnumerable<WorkshopItemInfo> items)
    {
        if (string.IsNullOrWhiteSpace(instancePath)) throw new ArgumentNullException(nameof(instancePath));

        var workshopDir = Path.Combine(instancePath, "steamapps", "workshop");
        Directory.CreateDirectory(workshopDir);

        var vdfPath = Path.Combine(workshopDir, $"appworkshop_{appId}.vdf");
        var content = GenerateAppWorkshopVdf(appId, items);
        File.WriteAllText(vdfPath, content, Encoding.UTF8);

        return vdfPath;
    }
}
