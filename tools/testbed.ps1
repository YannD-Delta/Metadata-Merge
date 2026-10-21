# A throw-away Playnite with its own user-data folder and fake sources. Nothing here touches a real library.
# Everything lives under <local root>\testbed (tools\local-paths.ps1): data\ (Playnite user data), fakes.log, dump.json.
#   tools\testbed.ps1 -Setup   create the config (Metadata Merge first in the source lists of some fields), build and deploy the extension
#                              plus the dev-only fakes (4 fake metadata plugins, a fake library of 3 games, a state dumper) into data\Extensions
#   tools\testbed.ps1 -Start   launch Playnite on that data (refuses when another Playnite is running: only ONE instance is allowed)
#   tools\testbed.ps1 -Stop    stop ONLY the processes started on this data folder
#   tools\testbed.ps1 -Reset   stop, then delete data\ (a fresh library on the next -Setup)
# -Scenario clean (with -Setup): the library games arrive with values ALREADY stored (no metadata download), rules are set, and the dumper runs the library clean-up
# (dry run, the pass, a second dry run) through the extension, then dumps; tools\testbed-verify-clean.ps1 checks it. -SkipClean (with -Start) only dumps; -Undo (with -Start) undoes the last pass from its journal, then dumps.
# The fake library imports its games at startup; with "download metadata on import" Playnite then runs its own metadata download
# with the source lists of the testbed config, which is exactly the path the extension is built for. tools\testbed-verify.ps1 checks the result.
# The Playnite program files come from PLAYNITE_TESTBED_DIR (a copy of an install), else <parent of the local root>\testbed\Playnite, else %LOCALAPPDATA%\Playnite.
# They are only ever started with --userdatadir pointing at the testbed data, never against %APPDATA%\Playnite.
param([switch]$Setup, [switch]$Start, [switch]$Stop, [switch]$Reset, [int]$DumpDelay = 60, [string]$Language = 'en_US', [ValidateSet('merge', 'clean')][string]$Scenario = 'merge', [switch]$SkipClean, [switch]$Undo)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'local-paths.ps1')
$tb = Get-LocalPath testbed
$data = Join-Path $tb 'data'
$dump = Join-Path $tb 'dump.json'
$callLog = Join-Path $tb 'fakes.log'
$imgDir = Join-Path $tb 'images'
$imgLog = Join-Path $tb 'images.log'
$imgPid = Join-Path $tb 'imgserver.pid'
$imgPort = 18765
$scenarioFile = Join-Path $tb 'scenario.txt'
$cleanOut = Join-Path $tb 'clean'
$self = 'b8e2f4a7-3c19-4d65-9a0b-6f7d1e2c5a84'
$store = '00000000-0000-0000-0000-000000000000'
$ids = @{ A = 'a1a1a1a1-0000-4000-8000-00000000000a'; B = 'b2b2b2b2-0000-4000-8000-00000000000b'; Slow = 'c3c3c3c3-0000-4000-8000-00000000000c'; Broken = 'd4d4d4d4-0000-4000-8000-00000000000d' }

function Get-PlayniteDir {
    if ($env:PLAYNITE_TESTBED_DIR) { return $env:PLAYNITE_TESTBED_DIR }
    $copy = Join-Path (Split-Path (Get-LocalRoot) -Parent) 'testbed\Playnite'
    if (Test-Path (Join-Path $copy 'Playnite.DesktopApp.exe')) { return $copy }
    return (Join-Path $env:LOCALAPPDATA 'Playnite')
}

function Stop-ImageServer {
    if (Test-Path -LiteralPath $imgPid) {
        $id = [int](Get-Content $imgPid -Raw)
        $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$id" -ErrorAction SilentlyContinue
        # only the process started for this testbed (its command line names the server script)
        if ($proc -and $proc.CommandLine -and $proc.CommandLine.Contains('imgserver.py')) { Write-Host "stopping image server $id"; Stop-Process -Id $id -Force }
        Remove-Item -LiteralPath $imgPid -Force
    }
}

# Real small pictures of the sizes the fake sources offer, each followed by 2 MB of trailing bytes (ignored by decoders): a client that
# downloaded a whole loser would show it in images.log.
function New-TestImages {
    Add-Type -AssemblyName System.Drawing
    New-Item -ItemType Directory -Force $imgDir | Out-Null
    $list = @(
        @('a-cover-600x900.png', 600, 900, 'png'), @('b-cover-1000x1500.jpg', 1000, 1500, 'jpeg'), @('store-cover-1600x900.png', 1600, 900, 'png'),
        @('a-back-3840x2160.png', 3840, 2160, 'png'), @('b-back-3840x2160.jpg', 3840, 2160, 'jpeg'), @('store-back-7680x4320.jpg', 7680, 4320, 'jpeg'),
        @('a-icon-256.png', 256, 256, 'png'), @('b-icon-32.png', 32, 32, 'png'))
    foreach ($i in $list) {
        $bmp = New-Object System.Drawing.Bitmap $i[1], $i[2], ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::SteelBlue)
        $g.FillEllipse([System.Drawing.Brushes]::Orange, [int]($i[1] / 4), [int]($i[2] / 4), [int]($i[1] / 2), [int]($i[2] / 2)); $g.Dispose()
        $fmt = if ($i[3] -eq 'png') { [System.Drawing.Imaging.ImageFormat]::Png } else { [System.Drawing.Imaging.ImageFormat]::Jpeg }
        $path = Join-Path $imgDir $i[0]
        $bmp.Save($path, $fmt); $bmp.Dispose()
        $fs = [IO.File]::OpenWrite($path); $fs.Seek(0, 'End') | Out-Null; $fs.Write((New-Object byte[] (2MB)), 0, 2MB); $fs.Close()
    }
}

function Stop-Testbed {
    Get-CimInstance Win32_Process -Filter "Name='Playnite.DesktopApp.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains($data) } |
        ForEach-Object { Write-Host "stopping testbed process $($_.ProcessId)"; Stop-Process -Id $_.ProcessId -Force }
}

if ($Stop) { Stop-Testbed; Stop-ImageServer }

if ($Reset) {
    Stop-Testbed; Stop-ImageServer
    if (Test-Path -LiteralPath $imgDir) { Remove-Item -LiteralPath $imgDir -Recurse -Force }
    if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
    foreach ($f in $dump, $callLog, $imgLog) { if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force } }
    Write-Host 'testbed data removed'
}

if ($Setup) {
    New-Item -ItemType Directory -Force $data | Out-Null
    function Field($sources) { @{ Import = $true; Sources = @($sources) } }
    $off = @{ Import = $false; Sources = @() }
    $config = Join-Path $data 'config.json'
    # Metadata Merge is FIRST for genres, developers, tags, features and links; the other fields are off.
    @{
        FirstTimeWizardComplete = $true; Language = $Language; CheckForProgramUpdates = 0; CheckForAddonUpdates = 0
        CheckForLibraryUpdates = 1; CheckForEmulatedLibraryUpdates = 0; DiscordPresenceEnabled = $false
        DownloadMetadataOnImport = ($Scenario -eq 'merge')
        MetadataSettings = @{
            SkipExistingValues = $false
            Genre = (Field @($self, $ids.A, $ids.B, $ids.Slow, $ids.Broken, $store))
            Developer = (Field @($self, $ids.A, $ids.B, $store))
            Tag = (Field @($self, $ids.A, $ids.B))
            Feature = (Field @($self, $ids.A, $ids.B))
            Links = (Field @($self, $ids.A, $ids.B))
            CoverImage = (Field @($self, $ids.A, $ids.B, $store))
            BackgroundImage = (Field @($self, $ids.A, $ids.B, $store))
            Icon = (Field @($self, $ids.A, $ids.B, $store))
            Name = $off; ReleaseDate = $off; Publisher = $off; Description = $off
            CriticScore = $off; CommunityScore = $off; AgeRating = $off; Series = $off; Region = $off; Platform = $off; InstallSize = $off
        }
    } | ConvertTo-Json -Depth 6 | Set-Content $config -Encoding UTF8
    # the extension's own settings: a short timeout so the slow fake is cut quickly, one log line per source and game, a source paused after 2 failed games,
    # and one extra parameter on three list fields (tags kept only when 2 sources agree, a developer excluded by expression, features of 14 characters at most)
    $settingsDir = Join-Path $data "ExtensionsData\$self"
    New-Item -ItemType Directory -Force $settingsDir | Out-Null
    if ($Scenario -eq 'clean') {
        $Scenario | Set-Content $scenarioFile
        # rules of the clean-up scenario (the owner's kind of rules); the tags and features of other add-ons have no rule
        @{ Data = @{ Fields = @{
            Genres = @{ Synonyms = "Aventure=Adventure`nIndépendant=Indie" }
            Features = @{ Synonyms = 'Singleplayer=Solo'; ExcludeRegex = '^Co-Op Extras:.*$' }
            Tags = @{ ExcludeRegex = '^\[(POV|Style)\].*$' }
            Developers = @{ Synonyms = 'Aspyr=Aspyr Media' } } } } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $settingsDir 'config.json') -Encoding UTF8
    } else {
    if (Test-Path -LiteralPath $scenarioFile) { Remove-Item -LiteralPath $scenarioFile -Force }
    # the three image fields are off by default: switched on here, with the default size rules (cover max 1000x1500, background max 3840x2160, icon min 64)
    @{ Data = @{ TimeoutSeconds = 2; VerboseLog = $true; BreakerFailures = 2; Fields = @{
        CoverImage = @{ Enabled = $true }; BackgroundImage = @{ Enabled = $true }; Icon = @{ Enabled = $true }
        Tags = @{ MinAgreeing = 2 }; Developers = @{ ExcludeRegex = '^Store Dev$' }; Features = @{ MaxValueLength = 14 } } } } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $settingsDir 'config.json') -Encoding UTF8
    }

    & (Join-Path $PSScriptRoot 'build.ps1') -Target plugin
    $built = Join-Path (Get-LocalPath build) 'MetadataMerge'
    $ext = Join-Path $data 'Extensions'
    $dest = Join-Path $ext 'MetadataMerge'
    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    New-Item -ItemType Directory -Force $ext | Out-Null
    Copy-Item -LiteralPath $built -Destination $dest -Recurse

    New-TestImages
    $pn = Get-PlayniteDir
    $fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
    $csc = $env:METADATAMERGE_CSC; if (-not $csc) { $csc = Join-Path (Split-Path $root -Parent) 'Playnite\tools\roslyn\csc.exe' }
    if (-not (Test-Path $csc)) { $csc = Join-Path $fw 'csc.exe' }
    $fakes = @(
        @{ Name = 'MMFakeA'; Define = 'FAKE_A'; Type = 'MetadataProvider'; Guid = $ids.A },
        @{ Name = 'MMFakeB'; Define = 'FAKE_B'; Type = 'MetadataProvider'; Guid = $ids.B },
        @{ Name = 'MMFakeSlow'; Define = 'FAKE_SLOW'; Type = 'MetadataProvider'; Guid = $ids.Slow },
        @{ Name = 'MMFakeBroken'; Define = 'FAKE_BROKEN'; Type = 'MetadataProvider'; Guid = $ids.Broken },
        @{ Name = 'MMFakeLibrary'; Define = 'FAKE_LIBRARY'; Type = 'GameLibrary'; Guid = 'e5e5e5e5-0000-4000-8000-00000000000e' },
        @{ Name = 'MMDump'; Define = 'DUMP'; Type = 'GenericPlugin'; Guid = 'f6f6f6f6-0000-4000-8000-00000000000f' }
    )
    foreach ($f in $fakes) {
        $dir = Join-Path $ext $f.Name
        if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
        New-Item -ItemType Directory -Force $dir | Out-Null
        & $csc -nologo -target:library "-define:$($f.Define)" "-out:$(Join-Path $dir "$($f.Name).dll")" "-r:$(Join-Path $pn 'Playnite.SDK.dll')" "-r:$(Join-Path $fw 'WPF\PresentationFramework.dll')" "-r:$(Join-Path $fw 'WPF\PresentationCore.dll')" "-r:$(Join-Path $fw 'WPF\WindowsBase.dll')" -r:System.dll -r:System.Core.dll -r:System.Web.Extensions.dll -r:System.Drawing.dll (Join-Path $PSScriptRoot 'testbed\Fakes.cs')
        if ($LASTEXITCODE -ne 0) { throw "$($f.Name) failed to build" }
        "Id: $($f.Name)_$($f.Guid)`nName: $($f.Name) (dev only)`nAuthor: dev`nVersion: 0.0.1`nModule: $($f.Name).dll`nType: $($f.Type)`n" | Set-Content (Join-Path $dir 'extension.yaml') -Encoding UTF8
    }
    Write-Host "extensions deployed to $ext"
}

if ($Start) {
    $running = Get-Process -Name 'Playnite.DesktopApp' -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host 'Another Playnite is running (only one instance is allowed, a second start would silently hand over to it):'
        $running | ForEach-Object { Write-Host "  pid $($_.Id)  $($_.Path)" }
        throw 'refusing to start'
    }
    $exe = Join-Path (Get-PlayniteDir) 'Playnite.DesktopApp.exe'
    if (-not (Test-Path $exe)) { throw "Playnite not found: $exe" }
    if (-not (Test-Path $data)) { throw 'testbed not set up: run tools\testbed.ps1 -Setup' }
    $scn = if (Test-Path -LiteralPath $scenarioFile) { (Get-Content $scenarioFile -Raw).Trim() } else { 'merge' }
    $env:MM_SCENARIO = $scn; $env:MM_CLEAN_OUT = $cleanOut; $env:MM_CLEAN_SKIP = if ($SkipClean) { '1' } else { '0' }; $env:MM_CLEAN_UNDO = if ($Undo) { '1' } else { '0' }
    if (-not $SkipClean -and -not $Undo -and (Test-Path -LiteralPath $cleanOut)) { Remove-Item -LiteralPath $cleanOut -Recurse -Force }
    $env:MM_FAKE_LOG = $callLog; $env:MM_DUMP = $dump; $env:MM_DUMP_DELAY = "$DumpDelay"; $env:MM_IMG_BASE = "http://127.0.0.1:$imgPort/"
    foreach ($f in $dump, $callLog, $imgLog) { if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force } }
    Stop-ImageServer
    if (Test-Path -LiteralPath $imgDir) {
        $server = Start-Process -FilePath 'python' -ArgumentList @('-I', "`"$(Join-Path $PSScriptRoot 'testbed\imgserver.py')`"", "`"$imgDir`"", "$imgPort", "`"$imgLog`"") -WindowStyle Hidden -PassThru
        $server.Id | Set-Content $imgPid
        Start-Sleep -Seconds 1
    }
    Start-Process -FilePath $exe -ArgumentList "--userdatadir `"$data`"" -WorkingDirectory (Split-Path $exe)
    Write-Host "testbed started (dump in ~$DumpDelay s: $dump)"
}
