<#
.SYNOPSIS
  Makes the launcher window's art from the player's own game: src/Ro3Emu.Play/art/background.jpg (the login screen's
  login_bg_01), art/logo.png and src/Ro3Emu.Play/ro3.ico (the login logo login_logo_img_04; ro3.exe itself carries
  only Unity's default icon). These are client files: they stay out of git, and a build without them draws the page
  in CSS alone.
.DESCRIPTION
  The two textures come from the client hook's texture dump. Start the game once with RO3EMU_DUMPTEX=IMG in its
  environment (the hook dumps the login screen's images into <client>\ro3emu\dump every few seconds), e.g.
    $env:RO3EMU_DUMPTEX = "IMG"; .\RO3Emu.exe start --bots 0
  then close it at the login screen. Run with Windows PowerShell 5.1 (System.Drawing).
.EXAMPLE
  tools/release/launcher-art.ps1 -Client "C:\Games\Genesis\ROO_PC"
#>
param(
    [Parameter(Mandatory = $true)][string]$Client,
    [string]$Out = ""
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
# Windows PowerShell 5.1 has no $PSScriptRoot while it binds parameter defaults.
if (-not $Out) { $Out = Join-Path $PSScriptRoot "..\..\src\Ro3Emu.Play" }
$dump = Join-Path $Client "ro3emu\dump"
$bg = Join-Path $dump "login_bg_01.png"
$logo = Join-Path $dump "login_logo_img_04.png"
foreach ($f in $bg, $logo) { if (-not (Test-Path $f)) { throw "Missing ${f}: dump the login screen first (see Get-Help $PSCommandPath)." } }
$art = Join-Path $Out "art"
New-Item -ItemType Directory -Force $art | Out-Null

function Resize([System.Drawing.Image]$image, [int]$width, [int]$height) {
    $bmp = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($image, 0, 0, $width, $height)
    $g.Dispose()
    return $bmp
}

# The background, as a JPEG small enough to embed.
$source = [System.Drawing.Image]::FromFile($bg)
$wide = Resize $source 1600 ([int](1600 * $source.Height / $source.Width))
$jpeg = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq "image/jpeg"
$quality = New-Object System.Drawing.Imaging.EncoderParameters 1
$quality.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 85L
$wide.Save((Join-Path $art "background.jpg"), $jpeg, $quality)
$wide.Dispose(); $source.Dispose()

# The logo for the page, and the icon: PNG images of each size in one .ico (Windows Vista and later read them).
$source = [System.Drawing.Image]::FromFile($logo)
$page = Resize $source 512 512
$page.Save((Join-Path $art "logo.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$page.Dispose()
$sizes = 256, 128, 64, 48, 32, 24, 16
$images = foreach ($size in $sizes) {
    $bmp = Resize $source $size $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$source.Dispose()
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $edge = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] } # 0 means 256
    $w.Write([byte]$edge); $w.Write([byte]$edge); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $w.Write($image) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $Out "ro3.ico"), $ico.ToArray())
Write-Host "Wrote $art\background.jpg, $art\logo.png and $Out\ro3.ico"
