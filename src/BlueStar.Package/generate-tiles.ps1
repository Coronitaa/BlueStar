<#
.SYNOPSIS
    Generates MSIX tile and unplated shell images from the BlueStar icon.
    Creates both standard tile assets and unplated targetsize assets for transparent taskbar icons.

.DESCRIPTION
    Creates all required tile sizes and unplated targetsize variations for the Microsoft Store / MSIX package.
    Uses bluestar.png as source and renders transparent 32bpp ARGB PNGs.
#>

$ErrorActionPreference = "Stop"

$sourceIcon = Join-Path $PSScriptRoot "..\BlueStar.App\Assets\bluestar.png"
$imagesDir  = Join-Path $PSScriptRoot "Images"

if (-not (Test-Path $imagesDir)) {
    New-Item -ItemType Directory -Path $imagesDir -Force | Out-Null
}

# Required MSIX tile sizes and unplated taskbar/shell target sizes
$tiles = [ordered]@{
    # Base tile assets
    "Square44x44Logo.png"   = @{ Width = 44;  Height = 44;  Padding = 0 }
    "Square71x71Logo.png"   = @{ Width = 71;  Height = 71;  Padding = 4 }
    "SmallTile.png"         = @{ Width = 71;  Height = 71;  Padding = 4 }
    "Square150x150Logo.png" = @{ Width = 150; Height = 150; Padding = 15 }
    "Square310x310Logo.png" = @{ Width = 310; Height = 310; Padding = 30 }
    "Wide310x150Logo.png"   = @{ Width = 310; Height = 150; Padding = 15 }
    "StoreLogo.png"         = @{ Width = 50;  Height = 50;  Padding = 0 }
    "SplashScreen.png"      = @{ Width = 620; Height = 300; Padding = 50 }
    "LockScreenLogo.png"    = @{ Width = 24;  Height = 24;  Padding = 0 }

    # Unplated Taskbar / Start Menu / Shell TargetSize Assets (CRITICAL for full transparency without backplate)
    "Square44x44Logo.targetsize-16_altform-unplated.png" = @{ Width = 16;  Height = 16;  Padding = 0 }
    "Square44x44Logo.targetsize-20_altform-unplated.png" = @{ Width = 20;  Height = 20;  Padding = 0 }
    "Square44x44Logo.targetsize-24_altform-unplated.png" = @{ Width = 24;  Height = 24;  Padding = 0 }
    "Square44x44Logo.targetsize-30_altform-unplated.png" = @{ Width = 30;  Height = 30;  Padding = 0 }
    "Square44x44Logo.targetsize-32_altform-unplated.png" = @{ Width = 32;  Height = 32;  Padding = 0 }
    "Square44x44Logo.targetsize-36_altform-unplated.png" = @{ Width = 36;  Height = 36;  Padding = 0 }
    "Square44x44Logo.targetsize-40_altform-unplated.png" = @{ Width = 40;  Height = 40;  Padding = 0 }
    "Square44x44Logo.targetsize-44_altform-unplated.png" = @{ Width = 44;  Height = 44;  Padding = 0 }
    "Square44x44Logo.targetsize-48_altform-unplated.png" = @{ Width = 48;  Height = 48;  Padding = 0 }
    "Square44x44Logo.targetsize-64_altform-unplated.png" = @{ Width = 64;  Height = 64;  Padding = 0 }
    "Square44x44Logo.targetsize-256_altform-unplated.png" = @{ Width = 256; Height = 256; Padding = 0 }

    # Light Unplated variants (for Windows Light theme taskbar)
    "Square44x44Logo.targetsize-16_altform-lightunplated.png" = @{ Width = 16;  Height = 16;  Padding = 0 }
    "Square44x44Logo.targetsize-24_altform-lightunplated.png" = @{ Width = 24;  Height = 24;  Padding = 0 }
    "Square44x44Logo.targetsize-32_altform-lightunplated.png" = @{ Width = 32;  Height = 32;  Padding = 0 }
    "Square44x44Logo.targetsize-48_altform-lightunplated.png" = @{ Width = 48;  Height = 48;  Padding = 0 }
    "Square44x44Logo.targetsize-256_altform-lightunplated.png" = @{ Width = 256; Height = 256; Padding = 0 }

    # Standard Plated targetsize variants
    "Square44x44Logo.targetsize-16.png" = @{ Width = 16;  Height = 16;  Padding = 0 }
    "Square44x44Logo.targetsize-24.png" = @{ Width = 24;  Height = 24;  Padding = 0 }
    "Square44x44Logo.targetsize-32.png" = @{ Width = 32;  Height = 32;  Padding = 0 }
    "Square44x44Logo.targetsize-48.png" = @{ Width = 48;  Height = 48;  Padding = 0 }
    "Square44x44Logo.targetsize-256.png" = @{ Width = 256; Height = 256; Padding = 0 }
}

Write-Host "Generating MSIX tile and unplated shell images..." -ForegroundColor Cyan

if (Test-Path $sourceIcon) {
    try {
        Add-Type -AssemblyName System.Drawing

        $srcImage = [System.Drawing.Image]::FromFile((Resolve-Path $sourceIcon).Path)

        foreach ($tile in $tiles.GetEnumerator()) {
            $destPath = Join-Path $imagesDir $tile.Key
            $w = $tile.Value.Width
            $h = $tile.Value.Height
            $padding = if ($tile.Value.ContainsKey("Padding")) { $tile.Value.Padding } else { 0 }

            $bitmap = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

            # Transparent background (no solid backplate/platter)
            $graphics.Clear([System.Drawing.Color]::Transparent)

            # Center the icon with specified padding
            $drawW = $w - (2 * $padding)
            $drawH = $h - (2 * $padding)
            $iconSize = [Math]::Min($drawW, $drawH)
            $x = ($w - $iconSize) / 2
            $y = ($h - $iconSize) / 2

            $graphics.DrawImage($srcImage, [int]$x, [int]$y, [int]$iconSize, [int]$iconSize)

            $bitmap.Save($destPath, [System.Drawing.Imaging.ImageFormat]::Png)
            $graphics.Dispose()
            $bitmap.Dispose()

            Write-Host "  Created: $($tile.Key) (${w}x${h})" -ForegroundColor Green
        }

        $srcImage.Dispose()
        Write-Host "`nAll tile and unplated images generated successfully!" -ForegroundColor Green
    }
    catch {
        Write-Warning "System.Drawing failed: $_"
        Write-Host "Creating placeholder images..." -ForegroundColor Yellow
        foreach ($tile in $tiles.GetEnumerator()) {
            $destPath = Join-Path $imagesDir $tile.Key
            Copy-Item -Path $sourceIcon -Destination $destPath -Force
            Write-Host "  Copied source as placeholder: $($tile.Key)" -ForegroundColor Yellow
        }
    }
} else {
    Write-Error "Source icon not found at: $sourceIcon"
    Write-Host "Please ensure bluestar.png exists in src\BlueStar.App\Assets\" -ForegroundColor Red
    exit 1
}
