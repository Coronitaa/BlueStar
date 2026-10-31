using System;
using System.Collections.Generic;

namespace BlueStar.Core.Helpers;

/// <summary>
/// Describes a Steam download region / server entry with localized display names.
/// </summary>
public sealed record SteamDownloadServerEntry(int CellId, string NameEs, string NameEn, string RegionGroup);

/// <summary>
/// Catalog of validated Steam content server regions (Cell IDs) used for downloads in DepotDownloader.
/// </summary>
public static class SteamDownloadServerCatalog
{
    public static readonly IReadOnlyList<SteamDownloadServerEntry> Entries = new List<SteamDownloadServerEntry>
    {
        new(0, "Automático (Más rápido / Recomendado)", "Automatic (Fastest / Recommended)", "Auto"),

        // South America
        new(116, "Argentina (Buenos Aires)", "Argentina (Buenos Aires)", "South America"),
        new(79, "Brasil (São Paulo)", "Brazil (São Paulo)", "South America"),
        new(120, "Chile (Santiago)", "Chile (Santiago)", "South America"),
        new(131, "Colombia (Bogotá)", "Colombia (Bogota)", "South America"),
        new(141, "Perú (Lima)", "Peru (Lima)", "South America"),

        // North America
        new(80, "México (Ciudad de México)", "Mexico (Mexico City)", "North America"),
        new(1, "EE.UU. - Chicago (Centro)", "US - Chicago (Central)", "North America"),
        new(2, "EE.UU. - Washington DC (Este)", "US - Washington DC (East)", "North America"),
        new(3, "EE.UU. - Nueva York (Noreste)", "US - New York (Northeast)", "North America"),
        new(12, "EE.UU. - Atlanta (Sureste)", "US - Atlanta (Southeast)", "North America"),
        new(28, "EE.UU. - Dallas (Sur)", "US - Dallas (South)", "North America"),
        new(10, "EE.UU. - Los Ángeles (Oeste)", "US - Los Angeles (West)", "North America"),
        new(6, "EE.UU. - Seattle (Noroeste)", "US - Seattle (Northwest)", "North America"),
        new(30, "Canadá - Toronto / Montreal", "Canada - Toronto / Montreal", "North America"),

        // Europe
        new(67, "España (Madrid)", "Spain (Madrid)", "Europe"),
        new(5, "Alemania (Fráncfort)", "Germany (Frankfurt)", "Europe"),
        new(4, "Reino Unido (Londres)", "UK (London)", "Europe"),
        new(14, "Francia (París)", "France (Paris)", "Europe"),
        new(15, "Países Bajos (Ámsterdam)", "Netherlands (Amsterdam)", "Europe"),
        new(40, "Italia (Roma / Milán)", "Italy (Rome / Milan)", "Europe"),
        new(69, "Polonia (Varsovia)", "Poland (Warsaw)", "Europe"),
        new(38, "Suecia (Estocolmo)", "Sweden (Stockholm)", "Europe"),

        // Asia & Oceania
        new(43, "Japón (Tokio)", "Japan (Tokyo)", "Asia & Oceania"),
        new(44, "Corea del Sur (Seúl)", "South Korea (Seoul)", "Asia & Oceania"),
        new(9, "Hong Kong", "Hong Kong", "Asia & Oceania"),
        new(45, "Singapur", "Singapore", "Asia & Oceania"),
        new(47, "Australia (Sídney)", "Australia (Sydney)", "Asia & Oceania"),
        new(48, "Australia (Melbourne)", "Australia (Melbourne)", "Asia & Oceania"),
    };

    public static string GetDisplayName(int cellId, string language)
    {
        bool isEs = language.StartsWith("es", StringComparison.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            if (entry.CellId == cellId)
            {
                return isEs ? entry.NameEs : entry.NameEn;
            }
        }

        return isEs
            ? $"Personalizado (Cell ID {cellId})"
            : $"Custom (Cell ID {cellId})";
    }
}
