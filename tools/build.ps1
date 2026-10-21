# Builds the Metadata Merge plugin, the test harnesses, or all.
#   tools\build.ps1 [-Target plugin|tests|ui|all]
#     plugin -> <local root>\build\MetadataMerge\ (MetadataMerge.dll, extension.yaml, Localization\)
#     tests  -> <local root>\build\tests.exe     (the core, plain C#, no Playnite)
#     ui     -> <local root>\build\ui-tests.exe  (the plugin views and view models in a plain WPF host, no Playnite process)
# Local root: tools\local-paths.ps1.
# Compiler: METADATAMERGE_CSC, else the Roslyn csc.exe of the sibling Playnite repo (..\Playnite\tools\roslyn, read-only), else the legacy csc.
# Playnite.SDK.dll comes from PLAYNITE_DIR, else %LOCALAPPDATA%\Playnite (read only: the application is never started from there).
param([ValidateSet('plugin', 'tests', 'ui', 'all')][string]$Target = 'all')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'local-paths.ps1')
$out = Get-LocalPath build -Create
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$playnite = if ($env:PLAYNITE_DIR) { $env:PLAYNITE_DIR } else { Join-Path $env:LOCALAPPDATA 'Playnite' }

$csc = $env:METADATAMERGE_CSC
if (-not $csc) { $csc = Join-Path (Split-Path $root -Parent) 'Playnite\tools\roslyn\csc.exe' }
if (-not (Test-Path $csc)) {
    Write-Warning 'Roslyn csc.exe not found: using the legacy C# 5 compiler (set METADATAMERGE_CSC)'
    $csc = Join-Path $fw 'csc.exe'
}

function Invoke-Csc([string[]]$CompilerArgs) {
    & $csc @CompilerArgs
    if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
}
function Get-Sources([string]$Dir) { Get-ChildItem $Dir -Filter *.cs -Recurse | ForEach-Object FullName }

$src = Join-Path $root 'src'
$baseRefs = @('System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Runtime.Serialization.dll', 'System.Web.Extensions.dll') | ForEach-Object { "-r:$_" }

if ($Target -in 'tests', 'all') {
    $sources = @(Get-Sources (Join-Path $src 'Core')) + @(Get-ChildItem (Join-Path $root 'tests') -Filter *.cs | ForEach-Object FullName)
    Invoke-Csc (@('-nologo', '-target:exe', '-debug', "-out:$(Join-Path $out 'tests.exe')") + $baseRefs + $sources)
    Write-Host "built $(Join-Path $out 'tests.exe')"
}

if ($Target -in 'plugin', 'ui', 'all') {
    $wpf = Join-Path $fw 'WPF'
    $sdkRefs = @(
        (Join-Path $playnite 'Playnite.SDK.dll'),
        (Join-Path $wpf 'PresentationFramework.dll'), (Join-Path $wpf 'PresentationCore.dll'),
        (Join-Path $wpf 'WindowsBase.dll'), (Join-Path $fw 'System.Xaml.dll'), 'Microsoft.CSharp.dll'
    ) | ForEach-Object { "-r:$_" }
    # XAML views are embedded and loaded at runtime (csc cannot compile BAML): MetadataMerge.<folder>.<file>
    # (the Localization dictionaries are not embedded: Playnite loads them from the Localization folder next to the dll)
    $locDir = Join-Path $src 'Localization'
    $resources = Get-ChildItem $src -Filter *.xaml -Recurse | Where-Object { $_.DirectoryName -ne $locDir } | ForEach-Object {
        $rel = $_.FullName.Substring($src.Length + 1).Replace([IO.Path]::DirectorySeparatorChar, '.')
        "-resource:$($_.FullName),MetadataMerge.$rel"
    }

    if ($Target -in 'ui', 'all') {
        $sources = @(Get-Sources $src) + @(Join-Path $root 'tests\Harness.cs') + @(Join-Path $root 'tests\Fakes.cs') + @(Join-Path $root 'tests\ImageHeaderTests.cs') + @(Get-ChildItem (Join-Path $root 'tests\ui') -Filter *.cs | ForEach-Object FullName)
        Invoke-Csc (@('-nologo', '-target:exe', '-debug', "-out:$(Join-Path $out 'ui-tests.exe')", "-r:$(Join-Path $playnite 'Newtonsoft.Json.dll')") + $baseRefs + $sdkRefs + $resources + $sources)
        # the harness loads Playnite.SDK.dll from next to the exe
        Copy-Item (Join-Path $playnite 'Playnite.SDK.dll') $out -Force
        Copy-Item (Join-Path $playnite 'Newtonsoft.Json.dll') $out -Force   # Playnite's serializer (used by the settings round-trip tests)
        Write-Host "built $(Join-Path $out 'ui-tests.exe')"
    }

    if ($Target -in 'plugin', 'all') {
        $dest = Join-Path $out 'MetadataMerge'
        New-Item -ItemType Directory -Force $dest | Out-Null
        Invoke-Csc (@('-nologo', '-target:library', '-debug', "-out:$(Join-Path $dest 'MetadataMerge.dll')") + $baseRefs + $sdkRefs + $resources + @(Get-Sources $src))
        Copy-Item (Join-Path $root 'extension.yaml') $dest -Force
        New-Item -ItemType Directory -Force (Join-Path $dest 'Localization') | Out-Null
        Copy-Item (Join-Path $locDir '*.xaml') (Join-Path $dest 'Localization') -Force
        Write-Host "built $(Join-Path $dest 'MetadataMerge.dll')"
    }
}
