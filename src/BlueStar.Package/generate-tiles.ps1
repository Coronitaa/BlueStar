<#
.SYNOPSIS
    Generates placeholder MSIX tile images from the existing BlueStar icon.
    Run this script once to create the required PNG assets for the MSIX package.

.DESCRIPTION
    Creates all required tile sizes for the Microsoft Store MSIX package.
    Uses the existing bluestar.png as source and creates properly sized PNGs.
    If System.Drawing is not available, creates properly tagged placeholder files.
#>

$ErrorActionPreference = "Stop"

$sourceIcon = Join-Path $PSScriptRoot "..\BlueStar.App\Assets\bluestar.png"
$imagesDir  = Join-Path $PSScriptRoot "Images"

if (-not (Test-Path $imagesDir)) {
    New-Item -ItemType Directory -Path $imagesDir -Force | Out-Null
}

# Required MSIX tile sizes
$tiles = @{
    "Square44x44Logo.png"   = @{ Width = 44;  Height = 44  }
    "Square71x71Logo.png"   = @{ Width = 71;  Height = 71  }  # SmallTile
    "SmallTile.png"         = @{ Width = 71;  Height = 71  }
    "Square150x150Logo.png" = @{ Width = 150; Height = 150 }
    "Square310x310Logo.png" = @{ Width = 310; Height = 310 }
    "Wide310x150Logo.png"   = @{ Width = 310; Height = 150 }
    "StoreLogo.png"         = @{ Width = 50;  Height = 50  }
    "SplashScreen.png"      = @{ Width = 620; Height = 300 }
    "LockScreenLogo.png"    = @{ Width = 24;  Height = 24  }
}

Write-Host "Generating MSIX tile images..." -ForegroundColor Cyan

if (Test-Path $sourceIcon) {
    try {
        Add-Type -AssemblyName System.Drawing

        $srcImage = [System.Drawing.Image]::FromFile((Resolve-Path $sourceIcon).Path)

        foreach ($tile in $tiles.GetEnumerator()) {
            $destPath = Join-Path $imagesDir $tile.Key
            $w = $tile.Value.Width
            $h = $tile.Value.Height

            $bitmap = New-Object System.Drawing.Bitmap($w, $h)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

            # Dark background matching app theme
            $graphics.Clear([System.Drawing.Color]::FromArgb(255, 26, 26, 46))

            # Center the icon with padding
            $padding = [Math]::Max(2, [Math]::Floor($w * 0.1))
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
        Write-Host "`nAll tile images generated successfully!" -ForegroundColor Green
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
