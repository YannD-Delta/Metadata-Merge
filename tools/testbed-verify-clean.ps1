# Checks the library clean-up on the testbed (scenario "clean": tools\testbed.ps1 -Reset -Setup -Scenario clean, start twice, wait for the dump).
#   tools\testbed-verify-clean.ps1            after the pass          tools\testbed-verify-clean.ps1 -Restored   after a start with -Undo (the pass undone from its journal, Playnite open)
# The fake library gives 3 games whose stored values include "other add-ons" values that must survive (see tools\testbed\Fakes.cs).
param([switch]$Restored)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'local-paths.ps1')
$tb = Get-LocalPath testbed
$state = Get-Content (Join-Path $tb 'dump.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$out = Join-Path $tb 'clean'
$failed = 0
function Check([bool]$ok, [string]$what) { if ($ok) { Write-Host "PASS $what" } else { Write-Host "FAIL $what"; $script:failed++ } }
function Same($actual, $expected) { ((@($actual) | Sort-Object) -join '|') -eq ((@($expected) | Sort-Object) -join '|') }
$games = @($state.games | Where-Object { $_.name -in 'Alpha Quest', 'Beta Strike', 'Gamma Ray' } | Sort-Object name)
Check ($games.Count -eq 3) 'the 3 fake games are there'
$keepTags = @('[UPS] No Music', '[HLTB] 12 h', 'Jeu solo', 'Genres: Monde ouvert')
$keepFeatures = @('Wand', 'Wand disponible', 'Nexus Mods')

if ($Restored) {
    Check (Same $games[0].genres @('Aventure', 'Indie')) "undo: Alpha Quest genres are back ($($games[0].genres -join ', '))"
    Check (Same $games[1].genres @('Adventure', 'Indépendant')) "undo: Beta Strike genres are back ($($games[1].genres -join ', '))"
    Check (@($games[0].tags) -contains '[POV] 1re personne') 'undo: the dropped [POV] tag is back'
    Check (@($games[0].features) -contains 'Singleplayer' -and @($games[0].features) -contains 'Co-Op Extras: FREE') 'undo: the renamed and dropped features are back'
    Check (@($games[0].developers) -contains 'Aspyr' -and @($games[1].developers) -contains 'Aspyr Media') 'undo: both developer spellings are back'
    Check (@($state.lists.tags) -contains '[POV] 1re personne' -and @($state.lists.genres) -contains 'Aventure') 'undo: the database lists are back'
    foreach ($t in $keepTags) { Check (@($games[0].tags) -contains $t) "undo: $t still there" }
} else {
    foreach ($f in 'clean-dry.txt', 'clean-apply.txt', 'clean-dry2.txt') { Check (Test-Path (Join-Path $out $f)) "$f was written" }
    if (Test-Path (Join-Path $out 'clean-error.txt')) { Write-Host (Get-Content (Join-Path $out 'clean-error.txt') -Raw) }
    $dry = Get-Content (Join-Path $out 'clean-dry.txt') -Raw
    $apply = Get-Content (Join-Path $out 'clean-apply.txt') -Raw
    $dry2 = Get-Content (Join-Path $out 'clean-dry2.txt') -Raw
    Check ($dry -match 'Genres: 0 value\(s\) renamed, 2 merged into another, 0 dropped, 3 game\(s\) affected') "dry run report: genres $(($dry -split "`n" | Where-Object { $_ -like 'Genres:*' }) -join '')"
    Check ($dry -match 'Features: .* 1 dropped') 'dry run report: one feature dropped'
    Check ($dry -match 'Tags: 0 value\(s\) renamed, 0 merged into another, 1 dropped, 3 game\(s\) affected') 'dry run report: one tag dropped'
    Check ($dry -match 'Developers: 0 value\(s\) renamed, 1 merged into another') 'dry run report: developers merged'
    Check ($apply -match 'APPLIED:' -and $apply -match 'JOURNAL:') 'the pass was applied and its undo journal written'
    Check ($dry2 -match 'Nothing to change') 'a second run changes nothing (idempotent)'
    Check (Same $games[0].genres @('Adventure', 'Indie')) "Alpha Quest genres: Aventure merged into Adventure ($($games[0].genres -join ', '))"
    Check (Same $games[1].genres @('Adventure', 'Indie')) "Beta Strike genres: Indépendant became Indie ($($games[1].genres -join ', '))"
    Check (Same $games[2].genres @('Adventure')) "Gamma Ray genres: Aventure and Adventure are one value ($($games[2].genres -join ', '))"
    foreach ($g in $games) {
        Check ((Same $g.features ($keepFeatures + 'Solo'))) "$($g.name): features Singleplayer became Solo, the drop expression removed the Co-Op Extras one, Wand / Wand disponible / Nexus Mods untouched ($($g.features -join ', '))"
        Check (Same $g.tags ($keepTags)) "$($g.name): the [POV] tag was dropped, every other tag ([UPS], [HLTB], Steam ones) is untouched ($($g.tags -join ', '))"
        Check (Same $g.developers @('Aspyr Media')) "$($g.name): developers Aspyr and Aspyr Media are one ($($g.developers -join ', '))"
        Check (Same $g.publishers @('EA')) "$($g.name): publisher EA untouched (no rule) ($($g.publishers -join ', '))"
    }
    Check (-not (@($state.lists.genres) -contains 'Aventure') -and -not (@($state.lists.genres) -contains 'Indépendant')) 'database list of genres: the merged items are gone'
    Check (-not (@($state.lists.tags) -contains '[POV] 1re personne') -and (@($state.lists.tags) -contains '[UPS] No Music')) 'database list of tags: only the dropped one is gone'
    Check (-not (@($state.lists.features) -contains 'Singleplayer') -and -not (@($state.lists.features) -contains 'Co-Op Extras: FREE')) 'database list of features cleaned'
    Check (Same ($state.lists.companies) @('Aspyr Media', 'EA')) "database list of companies ($($state.lists.companies -join ', '))"
    $journal = Get-ChildItem (Join-Path $tb 'data\ExtensionsData\b8e2f4a7-3c19-4d65-9a0b-6f7d1e2c5a84\clean-journals') -Filter 'clean-*.json' -ErrorAction SilentlyContinue | Select-Object -First 1
    Check ($null -ne $journal) 'an undo journal was written before the pass'
    if ($journal) {
        $j = Get-Content $journal.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        Check ($j.Done -eq $true -and @($j.Games).Count -eq 3) "the journal is complete ($(@($j.Games).Count) games, renamed $(@($j.Renamed).Count), removed $(@($j.Removed).Count))"
    }
}
Write-Host ''
if ($failed -gt 0) { Write-Host "$failed check(s) failed"; exit 1 } else { Write-Host 'all checks passed' }


