# Metadata Merge (Playnite 10 extension)

A Playnite metadata source that **adds up** the results of several other metadata sources instead of keeping only the first one that answers.

Playnite keeps, for each field, the first source of its list that returns a value, and stops there. With `Tags = PCGamingWiki, then the store`, the tags of IGDB or Steam are never used. Put **Metadata Merge first** in the source list of a field and it asks the other sources of that field at the same time, then returns the union of what they found (no duplicates, in the order of the list). Playnite applies that result as usual: the extension writes nothing itself.

- Fields: genres, developers, publishers, features, tags, links, series, platforms (regions and age ratings are opt-in).
- **Images (0.2.0, opt-in)**: cover, background and icon. The extension looks at the size of every candidate **before anything is downloaded**, picks the best one and returns only that one (see "Images" below).
- Per field: on or off, **union** or **first non-empty** (Playnite's own rule), which sources, a limit per source, a total limit, renaming rules.
- Each source is asked **once per game** (one parallel round for all the enabled fields), with a time limit and error isolation: a source that fails or is late is left out for that game, the others are kept, and nothing blocks the Playnite window.
- Values are compared loosely (case, accents, spaces, hyphens, `&`/`and`, company forms like Inc./Ltd/GmbH): "Co-op" and "Coop", "Valve" and "Valve Corporation" count as one. The spelling of the first source in the list is kept.
- **Library clean-up (0.3.0)**: applies the renaming rules and "names to drop" to the values ALREADY stored in the library, report first, one buffered update, undo journal, undo while Playnite runs (see below).
- A notification (one, updated) and the Playnite log tell which source failed or timed out.
- English and French.

## Install

Download `MetadataMerge.pext` and open it with Playnite (or drop it on the window), then restart Playnite. Playnite 10.x, Windows.

## Use

1. In Playnite: Library, **Download metadata**, choose "Configure fields" and the games.
2. For a field (say Genres), put **Metadata Merge** at the **top** of the list of sources, and keep the sources you want merged after it (IGDB, PCGamingWiki, Steam Store, the official store...).
3. Tick **"Save as default setting"** if you want the extension to follow the list from then on (see "What the extension can and cannot see" below), then download.

Add-ons, Extensions, **Metadata Merge** opens the settings: each field can be unfolded to change its mode, its sources, its limits and its renaming rules. The Diagnostics box at the bottom lists the installed sources, the order used for each field and the statistics (calls, errors, timeouts, average time per source).

### How the sources of a field are chosen

- **The sources of Playnite's list** (default): the sources that come *after* Metadata Merge in the list Playnite has saved for the field. If Metadata Merge is not in the saved list (you only chose it in the dialog without saving), all the other sources of the saved list are used.
- **My own list**: the sources you tick in the settings, in the order shown (arrows). It is also the fallback when Playnite's list gives nothing.
- The official store (the metadata of the game's own library, "Magasin officiel" in French) is a source like the others.
- A source that is in the list but not installed is skipped and reported once.

### Renaming rules

One rule per line, `alias=name`, matched with the same loose comparison:

```
Role-playing (RPG)=RPG
Hack and slash/Beat 'em up=Action
Early Access=
```

The last rule drops the value. Lines starting with `#` are comments.

## What a normal Playnite "Download metadata" does to existing values

This is what Playnite itself does (read in its downloader, version 10); it matters before you put Metadata Merge first in a list:

- A list field (genres, developers, publishers, features, tags, series, platforms, regions, age ratings, links) is **replaced, never added to**: when the first source of the list that returns something answers, its values become the WHOLE list of the game. A source that returns nothing leaves the game's list as it is.
- "**Ignore existing values**" (skip existing): a field is only processed when the game's list for that field is **empty**; the sources are not even asked for the others. The games that already have values keep them untouched.
- With Metadata Merge first, the "answer" is the union of its sources, and it still **replaces** the game's list.

**Warning: do not re-download Tags or Features on the whole library with Metadata Merge first (and "ignore existing values" off).** The tags and features that other add-ons wrote ("[UPS] ...", "[HLTB] ...", "[GGDeals] ...", "[Install Drive] ...", "[Lien manquant]", the features "Wand", "Nexus Mods"...) are in the same lists: they would be **replaced** by what the sources return. That is why the clean-up pass below exists: it fixes the values already stored without re-downloading anything and touches only values that match a rule.

## Library clean-up (apply the rules to what is already stored)

The renaming rules and "names to drop" of a field normally apply to what the sources return **from now on**. To apply them to the **values already in the library** (genres, developers, publishers, features, tags, series, platforms, regions, age ratings), use **Metadata Merge, Library clean-up** (settings button or main menu "Metadata Merge"). It never runs by itself.

1. **Dry run first.** A report says, per field: values renamed, merged into another, dropped, games affected, a sample of before/after lines and the biggest items; the full report is saved in the extension's data folder (`clean-reports`). Nothing is changed until you confirm.
2. **Only matching values are touched.** A value is changed only if it matches a renaming rule or the drop expression of its field (rules are followed to the end, so a second run changes nothing). Every other value, such as the tags and features written by other add-ons, stays exactly as it is, with the same item and the same games.
3. **How a change is made.** A renamed value is renamed in place (same item, same games); if an item with the target name already exists, the games are moved to it and the emptied item is deleted; a dropped value is removed from its games and its item deleted. Developers and publishers share one company list: a company that is also used in the other field is not renamed under it (a new item takes the games of the field being cleaned). Platforms and regions are never deleted (emulators may refer to them), only renamed or emptied.
4. **One write.** Everything is written in ONE buffered database update, on the Playnite database API.
5. **Undo, with Playnite open.** Playnite keeps its database files locked while it runs (verified: they cannot be copied), so the safety net is an **undo journal**: before the first write the extension saves, in its data folder (`clean-journals`), the state before of everything it will change (renamed names, deleted items with their ids, the lists of the games); the same file is completed at the end. **Metadata Merge, Undo the last clean-up** puts everything back: deleted items return with their old ids, names are renamed back, game lists are restored; a game whose list was edited after the pass is left alone for that list. An interrupted pass can be undone from the journal written before it started. For a copy of the database FILES, close Playnite and copy the library folder yourself (the usual full backup).
6. **Idempotent.** A second pass finds nothing to do.

Rules can be typed per field or imported from a file (settings, Fields, "Import rules from a file..."): `[Genres]` then one `alias=name` per line, `[Features: drop]` then one regular expression per line (several lines are alternatives); only the fields and parts present in the file are replaced. "Export the rules..." writes the current ones in the same format.

## All the parameters

Everything is in Add-ons, Extensions, Metadata Merge. Each field is unfolded separately; the defaults are the ones in brackets. **No field has any cap by default** (no limit per source, no limit in total, no agreement threshold, no length limit): a limit only exists if you set one.

| Where | Parameter |
| --- | --- |
| Every field | on/off (list fields on, regions, age ratings and images off); which sources: Playnite's list (the sources after Metadata Merge) or an own ordered list |
| List fields | mode: union (default) / first non-empty; most values per source; most values in total; kept only if given by at least N sources; order: as given / alphabetical / most agreed first; name writing: as written / capitalise lower-case words / lower / upper; longest name kept; names to drop (regular expression); renaming rules (`alias=name`, empty right side drops) |
| Image fields | smallest size (cover 300x450, background 1280x720, icon 64x64); largest wanted size (1000x1500, 3840x2160, 512x512); fewest pixels; heaviest file in KB (known from the Range answer); expected ratio (2:3, 16:9, 1:1, or your own like `21:9`); ratio tolerance (15 %); reject wrong proportions or use them only as a last resort; above the largest size: smallest one / never / take the largest; unreadable size: first one / never; comparable-size limit (15 %); format preference (`jpeg,png,webp,gif,ico,bmp`) |
| General | time limit per source (30 s); one round per game for all fields (on); loose comparison (on); notification on failures (on); one log line per game and source (off); Playnite configuration file (empty) |
| Rules | import / export of a rules file for all fields at once; library clean-up (report, apply, undo) |
| Advanced | bytes read to measure a picture (64 KB); time limit for the pictures of a game (general one); image decisions in the log (on) and kept in the diagnostics (40); **pause a source after N games failed in a row** (5, 0 = never) and for how long (10 min) |

The pause protects long batches: a source that is down is not asked again for a while instead of costing its full time limit on every game of a 1 800-game run. It comes back by itself, is flagged in the diagnostics and counted in the notification.

## Images (cover, background, icon)

Off by default (turn each field on in the settings, then put Metadata Merge first in that field's list like for the others). For each game and field, every source of the list is asked for its image, then:

1. **Measuring without downloading.** A web address is measured with an HTTP `Range` request for the first 64 KB (a second, wider request only when a JPEG has a big profile before its frame header); the connection is dropped as soon as the size is read. A picture a source already holds in memory, or a local file, is measured directly. Formats read from the header: PNG, JPEG (baseline and progressive), GIF, WebP (lossy, lossless, extended), ICO, BMP. A failing, slow or unreadable candidate is "size unknown" and never blocks the others.
2. **Choosing.** Candidates that are too small, or whose width/height ratio is off the expected one (cover 2:3, background 16:9, icon 1:1, tolerance 15 % by default), are rejected first. Among the rest, the **largest within the cap** wins (defaults: cover 1000x1500, background 3840x2160, icon 512x512; minimum cover 300x450, background 1280x720, icon 64x64). Above the cap an image is only used when nothing smaller fits (the smallest is taken). Between **comparable sizes** (within 15 % of the pixel count) the format preference decides, **JPEG first** (it decodes faster than PNG, and much faster than WebP in WPF), then the source order. If no size can be read at all, the first source's image is returned, like Playnite would.
3. **Returning.** Only the winner's own file object goes back to Playnite, which downloads it as usual. The losers are never downloaded. When nothing is acceptable the extension returns nothing and Playnite goes on to the next source of its list.

Every decision (candidates with size, source and verdict, the winner and why) is written to the Playnite log and shown in the Diagnostics box of the settings.

Limits to know:
- The extension decides on what the sources **offer**. A source that downloads the picture itself and hands back the bytes has already paid the download; one that returns only an address costs a header read. Which of the real providers (IGDB, IGN, Steam, Xbox, PCGamingWiki, the official store) give an address or bytes, and whether one address serves several sizes, depends on each plugin and was not verified.
- A wrong game match brings wrong pictures too: merging does not fix the matching of the sources.
- Playnite stores a background as a web address when "download backgrounds immediately" is off; the winner is then still the one chosen here.
- An image optimizer that runs afterwards (resize/recompress) cannot enlarge: a better source image only helps if its own cap is at least as big as the chosen size (e.g. a cover cap of 400 px hides a 1000x1500 choice).

## What the extension can and cannot see

- The Playnite SDK does not expose the per-field source order, so the extension reads Playnite's `config.json` (**read only**, `MetadataSettings`, per field `Sources`). The choice made in the "Download metadata" dialog only reaches that file when saved as the default; until then the extension sees the previous list. Use "My own list" if you prefer not to depend on it.
- Each source is asked the way Playnite itself asks it for an automatic download (background mode): a source never opens a selection dialog, and keeps its own matching (first or best search result). The extension inherits the matching limits of every source.
- Platforms are compared by their specification id only (a platform given as a name by one source and as an id by another is not recognised as the same).
- Fields other than the ones above (name, description, dates, scores) are not merged: they are left to Playnite's normal rule.

## Build, test, pack (developers)

Windows, PowerShell 7, the Roslyn `csc.exe` of the sibling Playnite repository (or `METADATAMERGE_CSC`) and `Playnite.SDK.dll` of an installed Playnite (read only).

```powershell
pwsh -NoProfile -File tests/run.ps1          # core tests, plain C#
pwsh -NoProfile -File tests/run.ps1 -Ui      # plugin, provider, settings page: real SDK base classes + a fake API in a WPF host
pwsh -NoProfile -File tools/build.ps1 -Target plugin
pwsh -NoProfile -File tools/pack.ps1         # MetadataMerge.pext
```

Build output and the testbed live outside the repository (`tools/local-paths.ps1`). `tools/testbed.ps1` sets up a throw-away Playnite with fake sources and a fake library; `tools/testbed-verify.ps1` checks what Playnite's own download did. It never touches a real Playnite or library, and needs every other Playnite closed (Playnite allows one instance).

## License

MIT, see [LICENSE](LICENSE).
