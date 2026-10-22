using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Helpers;

/// <summary>
/// Types of Steam library grid artworks.
/// </summary>
public enum SteamArtworkType
{
    /// <summary>
    /// Vertical poster artwork shown in the main Steam library grid (recommended: 600x900 px, 2:3 ratio).
    /// </summary>
    Capsule,

    /// <summary>
    /// Wide banner displayed as header background in the game detail view (recommended: 1920x620 px).
    /// </summary>
    Hero,

    /// <summary>
    /// Transparent title logo overlaid on top of the hero banner (recommended: PNG up to 1280x720 px).
    /// </summary>
    Logo,

    /// <summary>
    /// Horizontal banner for classic/grid view, Big Picture, and recent games (recommended: 460x215 or 920x430 px).
    /// </summary>
    Header,

    /// <summary>
    /// Square client and shortcut icon (recommended: 32x32 to 256x256 px).
    /// </summary>
    Icon,

    /// <summary>
    /// Wide horizontal banner for instance header and library card display (recommended: 460x215 or 920x430 px).
    /// </summary>
    InstanceBanner,

    /// <summary>
    /// Square logo/icon displayed in the sidebar recent navigation tabs and shortcuts (recommended: 256x256 px).
    /// </summary>
    InstanceLogo
}

/// <summary>
/// Result of validating artwork dimensions against Steam specifications.
/// </summary>
public record ArtworkDimensionValidation(
    bool IsValid,
    bool IsRecommended,
    int ActualWidth,
    int ActualHeight,
    int RecommendedWidth,
    int RecommendedHeight,
    string RatioDescription,
    string? WarningMessage);

/// <summary>
/// Provides artwork discovery, downloading from Steam PICS/CDN, caching per instance,
/// dimension validation, and deployment to Steam's userdata grid directories.
/// </summary>
public static class InstanceArtworkService
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" }
        }
    };

    /// <summary>
    /// Event raised when an artwork for an instance is updated or downloaded.
    /// </summary>
    public static event EventHandler<(Guid InstanceId, SteamArtworkType Type)>? ArtworkChanged;



    /// <summary>

    /// Optional platform-specific delegate to extract icons from game executables into destination .ico/.png.

    /// Signature: (exePath, destinationIcoPath) -> success

    /// </summary>

    public static Func<string, string, bool>? ExecutableIconExtractor { get; set; }

    /// <summary>
    /// Gets the root instances directory.
    /// </summary>
    public static string GetInstancesRootPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "instances");
    }

    /// <summary>
    /// Gets the artwork directory path for a specific game instance.
    /// Creates the directory if it does not exist.
    /// </summary>
    public static string GetArtworkDirectory(Guid instanceId)
    {
        var dir = Path.Combine(GetInstancesRootPath(), instanceId.ToString(), "artworks");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Gets the canonical file name for an artwork type without extension.
    /// </summary>
    public static string GetCanonicalFileName(SteamArtworkType type) => type switch
    {
        SteamArtworkType.Capsule => "capsule",
        SteamArtworkType.Hero => "hero",
        SteamArtworkType.Logo => "logo",
        SteamArtworkType.Header => "header",
        SteamArtworkType.Icon => "icon",
        SteamArtworkType.InstanceBanner => "instance_banner",
        SteamArtworkType.InstanceLogo => "instance_logo",
        _ => "artwork"
    };

    /// <summary>
    /// Gets the recommended dimensions for an artwork type.
    /// </summary>
    public static (int Width, int Height, string RatioDesc) GetRecommendedDimensions(SteamArtworkType type) => type switch
    {
        SteamArtworkType.Capsule => (600, 900, "2:3 vertical"),
        SteamArtworkType.Hero => (1920, 620, "~3.1:1 panorámico"),
        SteamArtworkType.Logo => (1280, 720, "16:9 con transparencia"),
        SteamArtworkType.Header => (460, 215, "~2.14:1 horizontal"),
        SteamArtworkType.Icon => (256, 256, "1:1 cuadrado"),
        SteamArtworkType.InstanceBanner => (460, 215, "~2.14:1 banner de instancia"),
        SteamArtworkType.InstanceLogo => (256, 256, "1:1 logo / nav"),
        _ => (512, 512, "1:1")
    };

    /// <summary>
    /// Gets the existing artwork file path for a specific slot on an instance if it exists on disk.
    /// </summary>
    public static string? GetArtworkFilePath(Guid instanceId, SteamArtworkType type)
    {
        var dir = Path.Combine(GetInstancesRootPath(), instanceId.ToString(), "artworks");
        if (!Directory.Exists(dir)) return null;

        var baseName = GetCanonicalFileName(type);
        string[] candidateExtensions = type switch
        {
            SteamArtworkType.Icon => [".ico", ".png", ".jpg", ".jpeg", ".webp"],
            SteamArtworkType.InstanceLogo => [".png", ".ico", ".webp", ".jpg", ".jpeg"],
            SteamArtworkType.Logo => [".png", ".webp", ".jpg", ".jpeg"],
            SteamArtworkType.InstanceBanner => [".jpg", ".png", ".jpeg", ".webp"],
            _ => [".jpg", ".png", ".jpeg", ".webp"]
        };

        foreach (var ext in candidateExtensions)
        {
            var path = Path.Combine(dir, $"{baseName}{ext}");
            if (File.Exists(path))
            {
                return path;
            }
        }

        // Fallbacks for instance banner <-> header, and instance logo <-> icon
        if (type == SteamArtworkType.InstanceBanner)
        {
            foreach (var ext in candidateExtensions)
            {
                var fallback = Path.Combine(dir, $"header{ext}");
                if (File.Exists(fallback)) return fallback;
            }
        }
        else if (type == SteamArtworkType.Header)
        {
            foreach (var ext in candidateExtensions)
            {
                var fallback = Path.Combine(dir, $"instance_banner{ext}");
                if (File.Exists(fallback)) return fallback;
            }
        }
        else if (type == SteamArtworkType.InstanceLogo)
        {
            foreach (var ext in candidateExtensions)
            {
                var fallback = Path.Combine(dir, $"icon{ext}");
                if (File.Exists(fallback)) return fallback;
            }
        }
        else if (type == SteamArtworkType.Icon)
        {
            foreach (var ext in candidateExtensions)
            {
                var fallback = Path.Combine(dir, $"instance_logo{ext}");
                if (File.Exists(fallback)) return fallback;
            }
        }

        return null;
    }

    /// <summary>
    /// Inspects image byte headers to extract width and height without external graphic dependencies.
    /// Supports PNG, JPEG, WEBP, GIF, BMP, and ICO.
    /// </summary>
    public static (int Width, int Height) GetImageDimensions(byte[] data)
    {
        if (data == null || data.Length < 10) return (0, 0);

        try
        {
            // 1. PNG: 89 50 4E 47 0D 0A 1A 0A
            if (data.Length >= 24 &&
                data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
                data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
            {
                int width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
                int height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
                return (width, height);
            }

            // 2. GIF: GIF87a or GIF89a
            if (data.Length >= 10 &&
                data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8')
            {
                int width = data[6] | (data[7] << 8);
                int height = data[8] | (data[9] << 8);
                return (width, height);
            }

            // 3. BMP: 'BM'
            if (data.Length >= 26 && data[0] == 'B' && data[1] == 'M')
            {
                int width = data[18] | (data[19] << 8) | (data[20] << 16) | (data[21] << 24);
                int height = Math.Abs(data[22] | (data[23] << 8) | (data[24] << 16) | (data[25] << 24));
                return (width, height);
            }

            // 4. ICO: 00 00 01 00
            if (data.Length >= 8 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0)
            {
                int width = data[6] == 0 ? 256 : data[6];
                int height = data[7] == 0 ? 256 : data[7];
                return (width, height);
            }

            // 5. WEBP: RIFF....WEBP
            if (data.Length >= 30 &&
                data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' &&
                data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
            {
                // VP8 lossy: VP8 (0x20)
                if (data[12] == 'V' && data[13] == 'P' && data[14] == '8' && data[15] == ' ')
                {
                    int width = (data[26] | (data[27] << 8)) & 0x3fff;
                    int height = (data[28] | (data[29] << 8)) & 0x3fff;
                    return (width, height);
                }
                // VP8L lossless: VP8L
                if (data.Length >= 25 && data[12] == 'V' && data[13] == 'P' && data[14] == '8' && data[15] == 'L')
                {
                    int b0 = data[21], b1 = data[22], b2 = data[23], b3 = data[24];
                    int width = 1 + (((b1 & 0x3F) << 8) | b0);
                    int height = 1 + (((b3 & 0xF) << 10) | (b2 << 2) | ((b1 & 0xC0) >> 6));
                    return (width, height);
                }
                // VP8X extended: VP8X
                if (data.Length >= 30 && data[12] == 'V' && data[13] == 'P' && data[14] == '8' && data[15] == 'X')
                {
                    int width = 1 + (data[24] | (data[25] << 8) | (data[26] << 16));
                    int height = 1 + (data[27] | (data[28] << 8) | (data[29] << 16));
                    return (width, height);
                }
            }

            // 6. JPEG: Starts with FF D8
            if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
            {
                int idx = 2;
                while (idx < data.Length - 8)
                {
                    if (data[idx] != 0xFF)
                    {
                        idx++;
                        continue;
                    }

                    byte marker = data[idx + 1];
                    idx += 2;

                    // Standalone markers
                    if (marker == 0xD8 || marker == 0xD9 || marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7))
                    {
                        continue;
                    }

                    if (idx + 2 > data.Length) break;
                    int length = (data[idx] << 8) | data[idx + 1];
                    if (length < 2) break;

                    // SOF markers: SOF0 (0xC0), SOF1 (0xC1), SOF2 (0xC2), SOF3 (0xC3), SOF5-SOF7, SOF9-SOF11, SOF13-SOF15
                    if ((marker >= 0xC0 && marker <= 0xC3) || (marker >= 0xC5 && marker <= 0xC7) ||
                        (marker >= 0xC9 && marker <= 0xCB) || (marker >= 0xCD && marker <= 0xCF))
                    {
                        if (idx + 7 <= data.Length)
                        {
                            int height = (data[idx + 3] << 8) | data[idx + 4];
                            int width = (data[idx + 5] << 8) | data[idx + 6];
                            return (width, height);
                        }
                    }

                    idx += length;
                }
            }
        }
        catch { }

        return (0, 0);
    }

    /// <summary>
    /// Reads dimensions of an image file on disk.
    /// </summary>
    public static (int Width, int Height) GetImageFileDimensions(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return (0, 0);
        try
        {
            var header = new byte[Math.Min(131072, (int)new FileInfo(filePath).Length)];
            using (var stream = File.OpenRead(filePath))
            {
                int read = stream.Read(header, 0, header.Length);
                if (read < header.Length)
                {
                    Array.Resize(ref header, read);
                }
            }
            return GetImageDimensions(header);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Validates an image's dimensions against Steam's recommended specifications for the specified artwork slot.
    /// </summary>
    public static ArtworkDimensionValidation ValidateDimensions(SteamArtworkType type, int width, int height, string? extension = null)
    {
        var (recW, recH, ratioDesc) = GetRecommendedDimensions(type);

        if (width <= 0 || height <= 0)
        {
            return new ArtworkDimensionValidation(
                IsValid: false,
                IsRecommended: false,
                ActualWidth: width,
                ActualHeight: height,
                RecommendedWidth: recW,
                RecommendedHeight: recH,
                RatioDescription: ratioDesc,
                WarningMessage: "No se pudieron determinar las dimensiones de la imagen.");
        }

        double ratio = (double)width / height;
        double recRatio = (double)recW / recH;

        bool isRecommended = Math.Abs(width - recW) < 5 && Math.Abs(height - recH) < 5;

        switch (type)
        {
            case SteamArtworkType.Capsule:
            {
                // Vertical portrait 2:3 (~0.667)
                if (width > height)
                {
                    return new ArtworkDimensionValidation(
                        IsValid: false,
                        IsRecommended: false,
                        ActualWidth: width,
                        ActualHeight: height,
                        RecommendedWidth: recW,
                        RecommendedHeight: recH,
                        RatioDescription: ratioDesc,
                        WarningMessage: $"La imagen es horizontal ({width}×{height}), pero la portada de Steam debe ser vertical (relación 2:3, ej. {recW}×{recH} px). Puede verse deformada o recortada.");
                }

                if (ratio < 0.50 || ratio > 0.82)
                {
                    return new ArtworkDimensionValidation(
                        IsValid: false,
                        IsRecommended: false,
                        ActualWidth: width,
                        ActualHeight: height,
                        RecommendedWidth: recW,
                        RecommendedHeight: recH,
                        RatioDescription: ratioDesc,
                        WarningMessage: $"La relación de aspecto ({width}×{height}) difiere de la proporción 2:3 vertical recomendada ({recW}×{recH} px).");
                }

                if (width < 300 || height < 450)
                {
                    return new ArtworkDimensionValidation(
                        IsValid: true,
                        IsRecommended: false,
                        ActualWidth: width,
                        ActualHeight: height,
                        RecommendedWidth: recW,
                        RecommendedHeight: recH,
                        RatioDescription: ratioDesc,
                        WarningMessage: $"La resolución ({width}×{height} px) es baja para la portada vertical. Se recomienda al menos 600×900 px para alta nitidez.");
                }

                break;
            }

            case SteamArtworkType.Hero:
            {
                // Wide landscape banner ~3.1:1 (~3.097)
                if (height >= width)
                {
                    return new ArtworkDimensionValidation(
                        IsValid: false,
                        IsRecommended: false,
                        ActualWidth: width,
                        ActualHeight: height,
                        RecommendedWidth: recW,
                        RecommendedHeight: recH,
                        RatioDescription: ratioDesc,
                        WarningMessage: $"La imagen es vertical o cuadrada ({width}×{height}), pero el fondo Hero debe ser un banner panorámico ancho ({recW}×{recH} px).");
                }

                if (ratio < 2.2 || ratio > 4.2)
                {
                    return new ArtworkDimensionValidation(
                        IsValid: false,
                        IsRecommended: false,
                        ActualWidth: width,
                        ActualHeight: height,
                        RecommendedWidth: recW,
                        RecommendedHeight: recH,
                        RatioDescription: ratioDesc,
                        WarningMessage: $"La proporción panorámica de la imagen ({width}×{height}) difiere del formato 1920×620 px recomendado.");
                }

                if (width < 960 || height < 310)
                {
                    return new ArtworkDimensionValidation(
                        IsValid: true,
                        IsRecommended: false,
                        ActualWidth: width,
                        ActualHeight: height,
                        RecommendedWidth: recW,
                        RecommendedHeight: recH,
                        RatioDescription: ratioDesc,
                        WarningMessage: $"La resolución ({width}×{height} px) es menor a la recomendada de 1920×620 px.");
                }

                break;
            }

            case SteamArtworkType.Logo:
            {
                // Transparent logo
                bool isPng = string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase);
                if (!isPng && !string.IsNullOrWhiteSpace(extension))
                {
                    return new ArtworkDimensionValidation(
                        IsValid: true,
                        IsRecommended: false,
                        ActualWidth: width,
                        ActualHeight: height,
                        RecommendedWidth: recW,
                        RecommendedHeight: recH,
                        RatioDescription: ratioDesc,
                        WarningMessage: "Se recomienda formato PNG con canal alfa transparente para que el fondo del juego se aprecie correctamente.");
                }

                if (width > 2560 || height > 1440)
                {
                    return new ArtworkDimensionValidation(
                        IsValid: true,
                        IsRecommended: false,
                        ActualWidth: width,
                        ActualHeight: height,
                        RecommendedWidth: recW,
                        RecommendedHeight: recH,
                        RatioDescription: ratioDesc,
                        WarningMessage: "La imagen del logo es inusualmente grande. Podría ralentizar la carga.");
                }

                break;
            }

            case SteamArtworkType.Header:
            {
                // Wide grid 460x215 or 920x430 (~2.14:1)
                if (height >= width)
                {
                    return new ArtworkDimensionValidation(
                        IsValid: false,
                        IsRecommended: false,
                        ActualWidth: width,
                        ActualHeight: height,
                        RecommendedWidth: recW,
                        RecommendedHeight: recH,
                        RatioDescription: ratioDesc,
                        WarningMessage: $"La portada ancha debe ser horizontal ({recW}×{recH} o 920×430 px), pero la imagen seleccionada es vertical ({width}×{height}).");
                }

                if (ratio < 1.6 || ratio > 2.6)
                {
                    return new ArtworkDimensionValidation(
                        IsValid: false,
                        IsRecommended: false,
                        ActualWidth: width,
                        ActualHeight: height,
                        RecommendedWidth: recW,
                        RecommendedHeight: recH,
                        RatioDescription: ratioDesc,
                        WarningMessage: $"La relación de aspecto ({width}×{height}) difiere del formato horizontal 460×215 / 920×430 px.");
                }

                break;
            }

            case SteamArtworkType.Icon:
            case SteamArtworkType.InstanceLogo:
            {
                // Square icon
                if (ratio < 0.85 || ratio > 1.18)
                {
                    return new ArtworkDimensionValidation(
                        IsValid: false,
                        IsRecommended: false,
                        ActualWidth: width,
                        ActualHeight: height,
                        RecommendedWidth: recW,
                        RecommendedHeight: recH,
                        RatioDescription: ratioDesc,
                        WarningMessage: $"El ícono debería ser cuadrado (1:1, ej. 256×256 px), pero tiene dimensiones {width}×{height} px.");
                }

                break;
            }
        }

        return new ArtworkDimensionValidation(
            IsValid: true,
            IsRecommended: isRecommended,
            ActualWidth: width,
            ActualHeight: height,
            RecommendedWidth: recW,
            RecommendedHeight: recH,
            RatioDescription: ratioDesc,
            WarningMessage: null);
    }

    private static string? ExtractAssetRelPath(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString();
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("english", out var en) && en.ValueKind == JsonValueKind.String)
                return en.GetString();
            if (element.TryGetProperty("en", out var en2) && en2.ValueKind == JsonValueKind.String)
                return en2.GetString();
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString();
            }
        }
        return null;
    }

    /// <summary>
    /// Downloads Steam grid artworks (capsule, hero, logo, header, icon) for an instance,
    /// saves them in the instance's artworks directory, and synchronizes them with Steam's grid folder if applicable.
    /// </summary>
    public static async Task<bool> DownloadArtworkForInstanceAsync(
        Guid instanceId,
        uint appId,
        string? customHeaderUrl = null,
        string? customCapsuleUrl = null,
        string? targetExePath = null,
        string? shortcutName = null,
        CancellationToken ct = default)
    {
        if (appId == 0) return false;

        var artworkDir = GetArtworkDirectory(instanceId);
        var http = SharedHttpClient;

        async Task<byte[]?> DownloadFirstAvailable(IEnumerable<string?> urls)
        {
            foreach (var url in urls)
            {
                if (string.IsNullOrWhiteSpace(url)) continue;
                try
                {
                    var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                        if (bytes != null && bytes.Length > 100)
                        {
                            return bytes;
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        string? cmdCapsuleRel = null;
        string? cmdHeroRel = null;
        string? cmdLogoRel = null;
        string? cmdHeaderRel = null;
        string? cmdClientIcon = null;

        // 1. Query SteamCMD PICS API
        try
        {
            var cmdUrl = $"https://api.steamcmd.net/v1/info/{appId}";
            var cmdJson = await http.GetStringAsync(cmdUrl, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(cmdJson);
            var appKey = appId.ToString();
            if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
                dataEl.TryGetProperty(appKey, out var appEntry))
            {
                JsonElement commonEl = default;
                if (appEntry.TryGetProperty("common", out var c1)) commonEl = c1;
                else if (appEntry.TryGetProperty("appinfo", out var ai) && ai.TryGetProperty("common", out var c2)) commonEl = c2;

                if (commonEl.ValueKind == JsonValueKind.Object)
                {
                    if (commonEl.TryGetProperty("library_assets_full", out var laf) && laf.ValueKind == JsonValueKind.Object)
                    {
                        if (laf.TryGetProperty("library_capsule", out var lc))
                        {
                            if (lc.TryGetProperty("image2x", out var i2x)) cmdCapsuleRel = ExtractAssetRelPath(i2x);
                            if (string.IsNullOrWhiteSpace(cmdCapsuleRel) && lc.TryGetProperty("image", out var i1x)) cmdCapsuleRel = ExtractAssetRelPath(i1x);
                        }

                        if (laf.TryGetProperty("library_hero", out var lh))
                        {
                            if (lh.TryGetProperty("image2x", out var i2x)) cmdHeroRel = ExtractAssetRelPath(i2x);
                            if (string.IsNullOrWhiteSpace(cmdHeroRel) && lh.TryGetProperty("image", out var i1x)) cmdHeroRel = ExtractAssetRelPath(i1x);
                        }

                        if (laf.TryGetProperty("library_logo", out var ll))
                        {
                            if (ll.TryGetProperty("image2x", out var i2x)) cmdLogoRel = ExtractAssetRelPath(i2x);
                            if (string.IsNullOrWhiteSpace(cmdLogoRel) && ll.TryGetProperty("image", out var i1x)) cmdLogoRel = ExtractAssetRelPath(i1x);
                        }

                        if (laf.TryGetProperty("library_header", out var lhd))
                        {
                            if (lhd.TryGetProperty("image2x", out var i2x)) cmdHeaderRel = ExtractAssetRelPath(i2x);
                            if (string.IsNullOrWhiteSpace(cmdHeaderRel) && lhd.TryGetProperty("image", out var i1x)) cmdHeaderRel = ExtractAssetRelPath(i1x);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(cmdHeaderRel) && commonEl.TryGetProperty("header_image", out var hi))
                    {
                        cmdHeaderRel = ExtractAssetRelPath(hi);
                    }

                    if (commonEl.TryGetProperty("clienticon", out var ci) && ci.ValueKind == JsonValueKind.String)
                    {
                        cmdClientIcon = ci.GetString();
                    }
                    else if (commonEl.TryGetProperty("icon", out var ico) && ico.ValueKind == JsonValueKind.String)
                    {
                        cmdClientIcon = ico.GetString();
                    }
                }
            }
        }
        catch { }

        var cdns = new[]
        {
            $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}",
            $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}",
            $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}",
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}",
            $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}"
        };

        // 2. Download and save Capsule (600x900)
        var verticalUrls = new List<string?>();
        if (!string.IsNullOrWhiteSpace(cmdCapsuleRel))
        {
            foreach (var cdn in cdns) verticalUrls.Add($"{cdn}/{cmdCapsuleRel}");
        }
        foreach (var cdn in cdns)
        {
            verticalUrls.Add($"{cdn}/library_600x900_2x.jpg");
            verticalUrls.Add($"{cdn}/library_600x900.jpg");
            verticalUrls.Add($"{cdn}/library_capsule_2x.jpg");
            verticalUrls.Add($"{cdn}/library_capsule.jpg");
        }
        if (!string.IsNullOrWhiteSpace(customCapsuleUrl)) verticalUrls.Add(customCapsuleUrl);

        var verticalData = await DownloadFirstAvailable(verticalUrls).ConfigureAwait(false);
        if (verticalData != null && verticalData.Length > 100)
        {
            await SaveSlotFileAsync(artworkDir, SteamArtworkType.Capsule, verticalData, ".jpg", ct).ConfigureAwait(false);
            ArtworkChanged?.Invoke(null, (instanceId, SteamArtworkType.Capsule));
        }

        // 3. Download and save Hero (1920x620)
        var heroUrls = new List<string?>();
        if (!string.IsNullOrWhiteSpace(cmdHeroRel))
        {
            foreach (var cdn in cdns) heroUrls.Add($"{cdn}/{cmdHeroRel}");
        }
        foreach (var cdn in cdns)
        {
            heroUrls.Add($"{cdn}/library_hero_2x.jpg");
            heroUrls.Add($"{cdn}/library_hero.jpg");
        }

        var heroData = await DownloadFirstAvailable(heroUrls).ConfigureAwait(false);
        if (heroData != null && heroData.Length > 100)
        {
            await SaveSlotFileAsync(artworkDir, SteamArtworkType.Hero, heroData, ".jpg", ct).ConfigureAwait(false);
            ArtworkChanged?.Invoke(null, (instanceId, SteamArtworkType.Hero));
        }

        // 4. Download and save Logo (transparent PNG)
        var logoUrls = new List<string?>();
        if (!string.IsNullOrWhiteSpace(cmdLogoRel))
        {
            foreach (var cdn in cdns) logoUrls.Add($"{cdn}/{cmdLogoRel}");
        }
        foreach (var cdn in cdns)
        {
            logoUrls.Add($"{cdn}/logo_2x.png");
            logoUrls.Add($"{cdn}/logo.png");
        }

        var logoData = await DownloadFirstAvailable(logoUrls).ConfigureAwait(false);
        if (logoData != null && logoData.Length > 100)
        {
            await SaveSlotFileAsync(artworkDir, SteamArtworkType.Logo, logoData, ".png", ct).ConfigureAwait(false);
            ArtworkChanged?.Invoke(null, (instanceId, SteamArtworkType.Logo));
        }

        // 5. Download and save Header (wide grid)
        var headerUrls = new List<string?>();
        if (!string.IsNullOrWhiteSpace(cmdHeaderRel))
        {
            foreach (var cdn in cdns) headerUrls.Add($"{cdn}/{cmdHeaderRel}");
        }
        foreach (var cdn in cdns)
        {
            headerUrls.Add($"{cdn}/header_2x.jpg");
            headerUrls.Add($"{cdn}/header.jpg");
            headerUrls.Add($"{cdn}/library_header_2x.jpg");
            headerUrls.Add($"{cdn}/library_header.jpg");
        }
        if (!string.IsNullOrWhiteSpace(customHeaderUrl)) headerUrls.Add(customHeaderUrl);

        var headerData = await DownloadFirstAvailable(headerUrls).ConfigureAwait(false);
        if (headerData != null && headerData.Length > 100)
        {
            await SaveSlotFileAsync(artworkDir, SteamArtworkType.Header, headerData, ".jpg", ct).ConfigureAwait(false);
            ArtworkChanged?.Invoke(null, (instanceId, SteamArtworkType.Header));
        }

        // 6. Download and save Icon (.ico / .png / .jpg)
        var iconUrls = new List<string?>();
        if (!string.IsNullOrWhiteSpace(cmdClientIcon))
        {
            iconUrls.Add($"https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{appId}/{cmdClientIcon}.ico");
            iconUrls.Add($"https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{appId}/{cmdClientIcon}.png");
            iconUrls.Add($"https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{appId}/{cmdClientIcon}.jpg");
            iconUrls.Add($"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/{appId}/{cmdClientIcon}.ico");
            iconUrls.Add($"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/{appId}/{cmdClientIcon}.png");
            iconUrls.Add($"https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/{appId}/{cmdClientIcon}.ico");
            iconUrls.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/{cmdClientIcon}.ico");
        }
        foreach (var cdn in cdns)
        {
            iconUrls.Add($"{cdn}/capsule_sm_120.jpg");
            iconUrls.Add($"{cdn}/icon.jpg");
        }

        var iconData = await DownloadFirstAvailable(iconUrls).ConfigureAwait(false);
        if (iconData != null && iconData.Length > 100)
        {
            var ext = (iconData.Length > 4 && iconData[0] == 0 && iconData[1] == 0 && iconData[2] == 1 && iconData[3] == 0) ? ".ico"
                    : (iconData.Length > 8 && iconData[0] == 0x89 && iconData[1] == 0x50) ? ".png" : ".jpg";
            await SaveSlotFileAsync(artworkDir, SteamArtworkType.Icon, iconData, ext, ct).ConfigureAwait(false);
            ArtworkChanged?.Invoke(null, (instanceId, SteamArtworkType.Icon));
        }
        else if (!string.IsNullOrWhiteSpace(targetExePath) && File.Exists(targetExePath) && ExecutableIconExtractor != null)
        {
            try
            {
                var icoPath = Path.Combine(artworkDir, "icon.ico");
                if (ExecutableIconExtractor(targetExePath, icoPath))
                {
                    ArtworkChanged?.Invoke(null, (instanceId, SteamArtworkType.Icon));
                }
            }
            catch { }
        }

        // 7. Deploy to Steam grid if target executable and shortcut name are known
        if (!string.IsNullOrWhiteSpace(targetExePath) && !string.IsNullOrWhiteSpace(shortcutName))
        {
            await DeployArtworksToSteamGridAsync(instanceId, targetExePath, shortcutName, ct).ConfigureAwait(false);
        }

        return true;
    }

    private static async Task SaveSlotFileAsync(string artworkDir, SteamArtworkType type, byte[] data, string extension, CancellationToken ct = default)
    {
        var baseName = GetCanonicalFileName(type);
        // Remove previous files for this slot with other extensions
        string[] exts = [".jpg", ".jpeg", ".png", ".webp", ".ico"];
        foreach (var e in exts)
        {
            var oldFile = Path.Combine(artworkDir, $"{baseName}{e}");
            if (File.Exists(oldFile) && !string.Equals(e, extension, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(oldFile); } catch { }
            }
        }

        var dest = Path.Combine(artworkDir, $"{baseName}{extension}");
        await File.WriteAllBytesAsync(dest, data, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves a custom user-provided artwork for an instance and synchronizes with Steam grid if applicable.
    /// </summary>
    public static async Task SaveCustomArtworkAsync(
        Guid instanceId,
        SteamArtworkType type,
        byte[] imageData,
        string extension,
        string? targetExePath = null,
        string? shortcutName = null,
        CancellationToken ct = default)
    {
        if (imageData == null || imageData.Length == 0) return;

        var artworkDir = GetArtworkDirectory(instanceId);
        if (!extension.StartsWith('.')) extension = "." + extension;

        await SaveSlotFileAsync(artworkDir, type, imageData, extension, ct).ConfigureAwait(false);
        ArtworkChanged?.Invoke(null, (instanceId, type));

        if (!string.IsNullOrWhiteSpace(targetExePath) && !string.IsNullOrWhiteSpace(shortcutName))
        {
            await DeployArtworkSlotToSteamGridAsync(type, imageData, extension, targetExePath, shortcutName, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deploys all cached artworks for an instance into Steam's userdata/userId/config/grid/ directory
    /// for every detected Steam user profile.
    /// </summary>
    public static async Task<bool> DeployArtworksToSteamGridAsync(
        Guid instanceId,
        string targetExePath,
        string shortcutName,
        CancellationToken ct = default)
    {
        var steamPath = ShortcutHelper.GetSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath)) return false;

        var userdataDir = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdataDir)) return false;

        var userFolders = Directory.GetDirectories(userdataDir)
            .Where(d => Path.GetFileName(d).All(char.IsDigit))
            .ToList();

        if (userFolders.Count == 0) return false;

        uint shortcutAppId32 = ShortcutHelper.ComputeCrc32(targetExePath + shortcutName) | 0x80000000;

        foreach (var type in Enum.GetValues<SteamArtworkType>())
        {
            var path = GetArtworkFilePath(instanceId, type);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                var ext = Path.GetExtension(path);
                await DeployArtworkSlotToSteamGridAsync(type, bytes, ext, targetExePath, shortcutName, ct).ConfigureAwait(false);
            }
        }

        return true;
    }

    /// <summary>
    /// Writes a specific artwork into Steam's userdata/userId/config/grid/ directory for all users.
    /// </summary>
    public static async Task DeployArtworkSlotToSteamGridAsync(
        SteamArtworkType type,
        byte[] data,
        string extension,
        string targetExePath,
        string shortcutName,
        CancellationToken ct = default)
    {
        if (data == null || data.Length == 0) return;

        var steamPath = ShortcutHelper.GetSteamPath();
        if (string.IsNullOrWhiteSpace(steamPath)) return;

        var userdataDir = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdataDir)) return;

        var userFolders = Directory.GetDirectories(userdataDir)
            .Where(d => Path.GetFileName(d).All(char.IsDigit))
            .ToList();

        uint shortcutAppId32 = ShortcutHelper.ComputeCrc32(targetExePath + shortcutName) | 0x80000000;
        ulong appId64 = ((ulong)shortcutAppId32 << 32) | 0x02000000;
        string id32 = shortcutAppId32.ToString();
        string idSigned32 = unchecked((int)shortcutAppId32).ToString();
        string id64 = appId64.ToString();

        var ext = extension.ToLowerInvariant();
        if (!ext.StartsWith('.')) ext = "." + ext;

        string[] targetFileNames = type switch
        {
            SteamArtworkType.Capsule =>
            [
                $"{id32}p{ext}", $"{id32}_p{ext}",
                $"{idSigned32}p{ext}", $"{idSigned32}_p{ext}",
                $"{id64}p{ext}", $"{id64}_p{ext}"
            ],
            SteamArtworkType.Hero =>
            [
                $"{id32}_hero{ext}", $"{id32}hero{ext}",
                $"{idSigned32}_hero{ext}", $"{idSigned32}hero{ext}",
                $"{id64}_hero{ext}", $"{id64}hero{ext}"
            ],
            SteamArtworkType.Logo =>
            [
                $"{id32}_logo{ext}", $"{id32}logo{ext}",
                $"{idSigned32}_logo{ext}", $"{idSigned32}logo{ext}",
                $"{id64}_logo{ext}", $"{id64}logo{ext}"
            ],
            SteamArtworkType.Header =>
            [
                $"{id32}{ext}", $"{id32}_header{ext}", $"{id32}_grid{ext}",
                $"{idSigned32}{ext}", $"{idSigned32}_header{ext}", $"{idSigned32}_grid{ext}",
                $"{id64}{ext}", $"{id64}_header{ext}", $"{id64}_grid{ext}"
            ],
            SteamArtworkType.Icon =>
            [
                $"{id32}_icon{ext}", $"{id32}{ext}",
                $"{idSigned32}_icon{ext}", $"{idSigned32}{ext}",
                $"{id64}_icon{ext}", $"{id64}{ext}"
            ],
            _ => []
        };

        foreach (var userFolder in userFolders)
        {
            var gridDir = Path.Combine(userFolder, "config", "grid");
            try
            {
                Directory.CreateDirectory(gridDir);
                foreach (var fileName in targetFileNames)
                {
                    var destPath = Path.Combine(gridDir, fileName);
                    await File.WriteAllBytesAsync(destPath, data, ct).ConfigureAwait(false);
                }
            }
            catch { }
        }
    }
}
