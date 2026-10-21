# Packs the built plugin into an installable MetadataMerge.pext (a zip of the content of the build folder) under <local root>\build\release.
#   tools\pack.ps1            (run tools\build.ps1 -Target plugin and the tests first)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'local-paths.ps1')
$built = Join-Path (Get-LocalPath build) 'MetadataMerge'
if (-not (Test-Path (Join-Path $built 'MetadataMerge.dll'))) { throw 'build the plugin first: tools\build.ps1 -Target plugin' }
$release = Join-Path (Get-LocalPath build) 'release'
New-Item -ItemType Directory -Force $release | Out-Null
$zip = Join-Path $release 'MetadataMerge.zip'
$pext = Join-Path $release 'MetadataMerge.pext'
foreach ($f in $zip, $pext) { if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force } }
# no .pdb in the package
$stage = Join-Path $release 'stage'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
Copy-Item -LiteralPath $built -Destination $stage -Recurse
Get-ChildItem $stage -Recurse -Filter *.pdb | Remove-Item -Force
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Move-Item -LiteralPath $zip -Destination $pext
Remove-Item -LiteralPath $stage -Recurse -Force
Write-Host "packed $pext ($((Get-Item $pext).Length) bytes)"
