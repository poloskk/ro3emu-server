<#
.SYNOPSIS
  Builds the RO3Emu Offline package (dist/RO3Emu-Offline.zip) at a version and, with -Publish, makes it the newest
  release of poloskk/ro3emu-server, which the RO3EmuPlay launcher offers as its update (src/Ro3Emu.Play/Updater.cs).
.DESCRIPTION
  The package is RO3Emu-Offline/RO3Emu/: RO3Emu.exe, RO3EmuPlay.exe (with the art of tools/release/launcher-art.ps1
  when it was made), the server, the fake players, the client DLLs of an RO3Emu folder that has run once (they are
  built against the game), README.txt (README-windows.txt and README-offline.txt), LICENSE.txt and
  RO3Emu.settings.json (that folder's settings without its game folder). Nothing of the game folder goes in.
  Stop RO3Emu first: a build while the server runs can drop its players. Runs in Windows PowerShell 5.1.
.EXAMPLE
  tools/release/offline.ps1 -Version 1.0.1
  tools/release/offline.ps1 -Version 1.0.1 -Publish -Notes notes.md
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Install = "",
    [switch]$Publish,
    [string]$Notes = "",
    [string]$Repository = "poloskk/ro3emu-server"
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if (-not $Install) { $Install = Join-Path $root "..\RO3Emu" }
$Install = (Resolve-Path $Install).Path
if (-not ($Version -match '^\d+\.\d+\.\d+$')) { throw "Version must be like 1.0.1" }
$running = Get-Process ro3, Ro3Emu.Server, Ro3Emu.Population, RO3EmuPlay -ErrorAction SilentlyContinue
if ($running) { throw "Stop RO3Emu first (running: $(($running | Select-Object -ExpandProperty ProcessName -Unique) -join ', '))." }

$work = Join-Path ([IO.Path]::GetTempPath()) ("ro3emu-offline-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$stage = Join-Path $work "RO3Emu-Offline\RO3Emu"
New-Item -ItemType Directory -Force $stage | Out-Null
Write-Host "staging in $work"

function Publish([string]$project, [string]$out) {
    & dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained -nologo -v q -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded "-p:Version=$Version" -p:IsTransformWebConfigDisabled=true `
        -p:StaticWebAssetsEnabled=false -o $out
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $project" }
    Get-ChildItem $out -Include *.pdb, *.xml -Recurse | Remove-Item
}
Publish "src\Ro3Emu.Setup" $stage
Publish "src\Ro3Emu.Play" $stage
Remove-Item (Join-Path $stage "RO3Emu.runtimeconfig.json") -ErrorAction SilentlyContinue
Publish "src\Ro3Emu.Server" (Join-Path $stage "server")
Publish "src\Ro3Emu.Population" (Join-Path $stage "population")
Get-ChildItem (Join-Path $stage "population") -Include "appsettings*.json", "*.runtimeconfig.json" -Recurse | Remove-Item

$client = Join-Path $stage "client"
New-Item -ItemType Directory $client | Out-Null
foreach ($dll in "Ro3Emu.ClientHooks.dll", "Ro3Emu.Loc.dll") { Copy-Item (Join-Path $Install "client\$dll") $client }
$readme = (Get-Content (Join-Path $PSScriptRoot "README-windows.txt") -Raw) + (Get-Content (Join-Path $PSScriptRoot "README-offline.txt") -Raw)
[IO.File]::WriteAllText((Join-Path $stage "README.txt"), $readme)
Copy-Item (Join-Path $root "LICENSE") (Join-Path $stage "LICENSE.txt")
# The settings without this PC's game folder (each player's own is found on the first start).
$settings = Get-Content (Join-Path $Install "RO3Emu.settings.json") -Raw | ConvertFrom-Json
$settings.PSObject.Properties.Remove("ClientDir")
$settings.PSObject.Properties.Remove("InstalledSequence")
$settings | ConvertTo-Json | Set-Content (Join-Path $stage "RO3Emu.settings.json") -Encoding ASCII

# Exactly these files; the client DLLs carry only RO3Emu's own resources (Ro3Emu.Loc.dll none of the game's texts).
$expected = "LICENSE.txt", "README.txt", "RO3Emu.exe", "RO3Emu.settings.json", "RO3EmuPlay.exe", "client/Ro3Emu.ClientHooks.dll",
    "client/Ro3Emu.Loc.dll", "population/Ro3Emu.Population.exe", "server/Ro3Emu.Server.exe", "server/appsettings.json",
    "server/appsettings.Development.json"
$files = Get-ChildItem $stage -Recurse -File | ForEach-Object { $_.FullName.Substring($stage.Length + 1).Replace("\", "/") } | Sort-Object
if (Compare-Object $files ($expected | Sort-Object)) { throw "unexpected package contents: $($files -join ', ')" }
$loc = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $client "Ro3Emu.Loc.dll"))).GetManifestResourceNames()
if (($loc -join ",") -ne "en_overrides.tsv") { throw "Ro3Emu.Loc.dll embeds game texts: $($loc -join ', ')" }

# The zip, with forward slashes (Windows PowerShell's own zip writes backslashes, which some tools misread).
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null
$zipPath = Join-Path $dist "RO3Emu-Offline.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath }
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($rel in $files) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $stage $rel), "RO3Emu-Offline/RO3Emu/$rel",
            [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $zip.Dispose() }
Remove-Item -Recurse -Force $work
Write-Host ("{0}  {1:N1} MB, version {2}" -f $zipPath, ((Get-Item $zipPath).Length / 1MB), $Version)

if ($Publish) {
    $gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
    if (-not $gh) { $gh = "C:\Program Files\GitHub CLI\gh.exe" }
    $ghArgs = @("release", "create", "v$Version", $zipPath, "-R", $Repository, "--target", "feat/population", "--title", "RO3Emu Offline $Version")
    $ghArgs += if ($Notes) { @("--notes-file", $Notes) } else { @("--notes", "Download RO3Emu-Offline.zip, or press Update in RO3EmuPlay.") }
    & $gh @args
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
}
