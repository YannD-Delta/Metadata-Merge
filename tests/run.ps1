# Builds and runs a test harness. Usage: tests\run.ps1 [name-filter] [-Ui]
#   (default) the core tests, plain C#          -Ui  the plugin views and view models in a plain WPF host (needs the Playnite SDK dll)
param([string]$Filter, [switch]$Ui)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'tools\local-paths.ps1')
$env:METADATAMERGE_REPO = $root   # the UI tests read the Localization dictionaries from the repository; this process only
if ($Ui) {
    & (Join-Path $root 'tools\build.ps1') -Target ui
    $exe = Join-Path (Get-LocalPath build) 'ui-tests.exe'
} else {
    & (Join-Path $root 'tools\build.ps1') -Target tests
    $exe = Join-Path (Get-LocalPath build) 'tests.exe'
}
if ($Filter) { & $exe $Filter } else { & $exe }
exit $LASTEXITCODE
