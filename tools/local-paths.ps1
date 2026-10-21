# Where the heavy, per-PC folders live (build\ testbed\ samples\ exchange\): a LOCAL ROOT outside the repository.
# Dot-source it from a script:   . (Join-Path $PSScriptRoot 'local-paths.ps1')
#   Get-LocalRoot                      the local root
#   Get-LocalPath build|testbed|samples|exchange [-Create]   a folder under it
# Order: 1. $env:METADATAMERGE_LOCAL if set (absolute path)
#        2. $env:MODULA_LOCAL\metadata-merge if MODULA_LOCAL points to an existing folder (same non-synced convention as the Playnite repo)
#        3. %USERPROFILE%\Documents\Playnite\metadata-merge if %USERPROFILE%\Documents\Playnite exists
#        4. <repo>\.local, with a warning (git-ignored)
$script:LocalPathsPublic = $false   # $true in the public copy written by tools\export-public.py (this exact line): no per-PC fallbacks

function Get-LocalRoot {
    [CmdletBinding()]
    param()
    $repo = Split-Path $PSScriptRoot -Parent
    $own = "$env:METADATAMERGE_LOCAL".Trim().Trim('"').Trim()
    if ($own -match '^[A-Za-z]:[\\/]' -or $own -match '^[\\/]{2}[^\\/]+[\\/][^\\/]+') { return $own.TrimEnd('\', '/') }
    $shared = "$env:MODULA_LOCAL".Trim().Trim('"').Trim()
    if (-not $shared) { try { $shared = "$([Environment]::GetEnvironmentVariable('MODULA_LOCAL', 'User'))".Trim().Trim('"').Trim() } catch { $shared = '' } }
    if (-not $script:LocalPathsPublic -and $shared -match '^[A-Za-z]:[\\/]' -and (Test-Path -LiteralPath $shared)) { return (Join-Path $shared.TrimEnd('\', '/') 'metadata-merge') }
    $docs = Join-Path $env:USERPROFILE 'Documents\Playnite'
    if (-not $script:LocalPathsPublic -and (Test-Path -LiteralPath $docs)) { return (Join-Path $docs 'metadata-merge') }
    Write-Warning 'METADATAMERGE_LOCAL is not set: using <repo>\.local (git-ignored)'
    return (Join-Path $repo '.local')
}

function Get-LocalPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][ValidateSet('build', 'testbed', 'samples', 'exchange')][string]$Name, [switch]$Create)
    $p = Join-Path (Get-LocalRoot) $Name
    if ($Create) { New-Item -ItemType Directory -Force -Path $p | Out-Null }
    return $p
}
