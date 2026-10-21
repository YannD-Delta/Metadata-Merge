# Checks what Playnite's own metadata download did on the testbed (run after tools\testbed.ps1 -Start and the dump delay).
#   tools\testbed-verify.ps1
# Expected, for each of the 3 fake games (see tools\testbed\Fakes.cs and the config written by tools\testbed.ps1):
#   Genre     MM > A, B, Slow (too slow), Broken (fails), official store -> Action, RPG, Indie, Adventure
#   Developer MM > A, B, store  -> Studio One Inc. (the store developer is dropped by ExcludeRegex)
#   Tag       MM > A, B -> Co-op (MinAgreeing 2: only the tag both sources give)
#   Feature   MM > A, B -> Single Player (MaxValueLength 14 drops Controller Support)         Links MM > A, B -> Wiki, Forum
#   Slow and Broken fail on games 1 and 2 (BreakerFailures 2): they are paused and not asked at all for game 3
#   Images (version 0.2): the candidates are measured by their header over HTTP (Range), only the winner is downloaded, by Playnite:
#     Cover      A 600x900 png, B 1000x1500 jpeg, store 1600x900 (wrong proportions)       -> B
#     Background A 3840x2160 png, B 3840x2160 jpeg, store 7680x4320 (above the cap)        -> B (jpeg preferred between comparable sizes)
#     Icon       A 256x256 png, B 32x32 (below the minimum)                                  -> A
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'local-paths.ps1')
$tb = Get-LocalPath testbed
$dumpPath = Join-Path $tb 'dump.json'
$logPath = Join-Path $tb 'fakes.log'
if (-not (Test-Path $dumpPath)) { throw "no dump yet: $dumpPath" }
$state = Get-Content $dumpPath -Raw -Encoding UTF8 | ConvertFrom-Json
$log = if (Test-Path $logPath) { Get-Content $logPath -Encoding UTF8 } else { @() }
$failed = 0
function Check([bool]$ok, [string]$what) { if ($ok) { Write-Host "PASS $what" } else { Write-Host "FAIL $what"; $script:failed++ } }
function Same($actual, $expected) { ((@($actual) | Sort-Object) -join '|') -eq ((@($expected) | Sort-Object) -join '|') }
function Count([string]$pattern) { @($log | Where-Object { $_ -match $pattern }).Count }

$games = @($state.games | Where-Object { $_.name -in 'Alpha Quest', 'Beta Strike', 'Gamma Ray' })
Check ($games.Count -eq 3) 'the fake library imported its 3 games'
foreach ($g in $games) {
    Check (Same $g.genres @('Action', 'RPG', 'Indie', 'Adventure')) "$($g.name): genres are the union of A, B and the official store ($($g.genres -join ', '))"
    Check (Same $g.developers @('Studio One Inc.')) "$($g.name): developers merged ('Studio One' and 'Studio One Inc.' are one) and the excluded one is dropped ($($g.developers -join ', '))"
    Check (Same $g.tags @('Co-op')) "$($g.name): only the tag both sources give is kept, 'co-op' is 'Co-op' ($($g.tags -join ', '))"
    Check (Same $g.features @('Single Player')) "$($g.name): features merged, 'Singleplayer' is 'Single Player', the 18-character one is dropped ($($g.features -join ', '))"
    Check ((@($g.links).Count -eq 2) -and (Same ($g.links | ForEach-Object { $_.name }) @('Wiki', 'Forum'))) "$($g.name): links merged, the same page under two spellings is one ($(($g.links | ForEach-Object { $_.name }) -join ', '))"
}
# each source is created once per game even though 5 fields are asked, and Playnite does not go on to the next sources after Metadata Merge answered
foreach ($s in 'A', 'B') { Check ((Count "^\S+ $s create ") -eq 3) "source ${s}: provider created once per game (3 games), got $(Count "^\S+ $s create ")" }
foreach ($s in 'Slow', 'Broken') { Check ((Count "^\S+ $s create ") -eq 2) "source ${s}: failed on games 1 and 2, then paused: provider created twice, not for the 3rd game, got $(Count "^\S+ $s create ")" }
Check ((Count '^\S+ A (Genres|Tags|Developers|Features|Links) ') -eq 15) "source A answered 5 fields x 3 games, got $(Count '^\S+ A (Genres|Tags|Developers|Features|Links) ')"
Check ((Count '^\S+ A dispose ') -eq 3) 'source A disposed once per game'
Check ((Count '^\S+ Store metadata ') -eq 3) "the official store was read once per game, got $(Count '^\S+ Store metadata ')"
Check ((Count 'background=True') -ge 10) 'sources are asked in background mode'
# ---- images
$imgLogPath = Join-Path $tb 'images.log'
$imgLog = if (Test-Path $imgLogPath) { Get-Content $imgLogPath -Encoding UTF8 } else { @() }
function ImgReq([string]$file) { @($imgLog | Where-Object { $_ -like "*/$file *" }) }
function Sent($line) { [int](($line -split 'sent=')[1] -split ' ')[0] }
foreach ($g in $games) {
    Check ($g.cover -like '1000x1500 jpeg*') "$($g.name): cover is the 1000x1500 JPEG of source B ($($g.cover))"
    Check (($g.background -like '3840x2160 jpeg*') -or ($g.background -like 'url http://127.0.0.1:*/b-back-3840x2160.jpg')) "$($g.name): background is the 3840x2160 JPEG of source B, not the PNG nor the 8K one ($($g.background))"
    Check ($g.icon -like '256x256 png*') "$($g.name): icon is the 256x256 PNG of source A ($($g.icon))"
}
foreach ($loser in 'a-cover-600x900.png', 'store-cover-1600x900.png', 'a-back-3840x2160.png', 'store-back-7680x4320.jpg', 'a-icon-256.png', 'b-icon-32.png', 'b-cover-1000x1500.jpg', 'b-back-3840x2160.jpg') {
    $reqs = ImgReq $loser
    $probes = @($reqs | Where-Object { $_ -match 'range=bytes=0-65535' })
    Check ($probes.Count -eq 3) "$loser measured once per game with a Range request of the first 64 KB (3 games), got $($probes.Count)"
    Check (@($probes | Where-Object { (Sent $_) -gt 70000 }).Count -eq 0) "$loser header reads stayed under 70 KB each"
}
foreach ($loser in 'a-cover-600x900.png', 'store-cover-1600x900.png', 'a-back-3840x2160.png', 'store-back-7680x4320.jpg', 'b-icon-32.png') {
    $whole = @(ImgReq $loser | Where-Object { (Sent $_) -gt 200000 })
    Check ($whole.Count -eq 0) "$loser (a loser) was never downloaded: no request above 200 KB"
}
foreach ($winner in 'b-cover-1000x1500.jpg', 'a-icon-256.png') {
    $whole = @(ImgReq $winner | Where-Object { (Sent $_) -gt 2000000 })
    Check ($whole.Count -ge 1) "$winner (the winner) was downloaded whole by Playnite ($($whole.Count) request(s))"
}
$extLog = Join-Path (Join-Path $tb 'data') 'extensions.log'
$decisions = if (Test-Path $extLog) { @(Get-Content $extLog -Encoding UTF8 | Where-Object { $_ -match 'Metadata Merge: .* (CoverImage|BackgroundImage|Icon): ' }) } else { @() }
Check ($decisions.Count -eq 9) "one logged decision per image field and game (9), got $($decisions.Count)"
Check (@($decisions | Where-Object { $_ -match 'CoverImage: MM Fake B 1000x1500 JPEG' }).Count -eq 3) 'decision log: cover winner B 1000x1500 JPEG'
Check (@($decisions | Where-Object { $_ -match 'BackgroundImage: MM Fake B 3840x2160 JPEG' }).Count -eq 3) 'decision log: background winner B 3840x2160 JPEG'
Check (@($decisions | Where-Object { $_ -match 'Official store [0-9x ]*PNG: rejected: ratio' }).Count -eq 3) 'decision log: the 1600x900 store cover was rejected for its proportions'
$extLog2 = Join-Path (Join-Path $tb 'data') 'extensions.log'
$paused = if (Test-Path $extLog2) { @(Get-Content $extLog2 -Encoding UTF8 | Where-Object { $_ -match 'MM Fake (Slow|Broken) failed 2 games in a row: paused' }) } else { @() }
Check ($paused.Count -eq 2) "the log says the slow and the broken source were paused (2 lines), got $($paused.Count)"
$problem = @($state.notifications | Where-Object { $_.id -eq 'MetadataMerge.Problems' })
Check (($problem.Count -eq 1) -and ($problem[0].text -match 'MM Fake Slow') -and ($problem[0].text -match 'MM Fake Broken')) 'one notification names the slow and the broken source'
Write-Host ''
if ($failed -gt 0) { Write-Host "$failed check(s) failed"; exit 1 } else { Write-Host 'all checks passed' }
