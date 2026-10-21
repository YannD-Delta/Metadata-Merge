# Metadata Merge: design and verified facts

## Problem
Playnite keeps, per field, the first source of an ordered list that returns a valid value (`MetadataDownloader.ProcessField`) and stops. The owner wants a source that, placed first, unions the other sources configured for the same field.

## Facts checked (Playnite 10.63, SDK 6.18; downloader source read on GitHub, SDK by reflection)
- `MetadataPlugin` (abstract: `Id`, `Name`, `SupportedFields`, `GetMetadataProvider(MetadataRequestOptions)`) returns an `OnDemandMetadataProvider` (virtual `GetGenres`, `GetDevelopers`, ..., abstract `AvailableFields`, `Dispose`).
- Playnite's downloader creates ONE provider per plugin per game (`MetadataRequestOptions(game, true)`, background mode), reuses it for every field of that game, disposes it in a `finally`. It skips a plugin for a field not in `SupportedFields`, and a provider whose `AvailableFields` lacks the field.
- Other plugins are reachable through `PlayniteApi.Addons.Plugins` (`List<Plugin>`), `MetadataPlugin.GetMetadataProvider` works the same way for us.
- The source with the empty GUID is the "official store": the game's library plugin `GetMetadataDownloader().GetMetadata(game)` (`GameMetadata`); skipped for games with no library.
- The per-field source order is NOT in the SDK API (`IPlayniteSettingsAPI` has no metadata settings): it is `config.json` > `MetadataSettings.<Field>.Sources` (list of plugin GUIDs; field names `Genre`, `Developer`, `Publisher`, `Tag`, `Feature`, `Links`, `Series`, `Platform`, `Region`, `AgeRating`, ...). It only reflects the dialog when saved as default.
- `GetMetadataFieldArgs.CancelToken` has no public setter (reflection needed to pass a token to another provider).
- Playnite allows one instance per Windows session (global mutex `PlayniteInstaceMutex`): a testbed cannot run beside the real Playnite.

## Decisions
- Core without Playnite types (`ISource`, `ISourceSession`, `GameMerge`, `MergeEngine`) so the merge is unit-tested with fake sources; a thin glue maps SDK types to items.
- One `GameMerge` per provider (so per game): first request fetches all enabled fields from each source in parallel (prefetch, default), later fields are answered from the snapshot. Optional per-field rounds.
- Time limit per round (default 30 s) with a linked cancellation token; a late source is abandoned (its session is released when it returns), a failed or late source is not asked again for that game.
- Sources of a field: those after the extension in Playnite's saved list (all others if the extension is not in the saved list), else an own ordered list; own list is also the fallback.
- Sources are asked in background mode like Playnite does: no selection dialog from a worker thread, each source's own first/best match.
- Dedupe key: loose by default (case, accents, spacing, punctuation, `&`=and; companies also lose legal forms), links by address (scheme, www, slash, fragment ignored), ids and platform spec ids by value. First spelling in priority order wins. Per-source cap applies after the source's own dedupe; total cap at the end. Renaming rules `alias=name`, empty right side drops a value.
- Platforms: name and spec id are never matched against each other (needs the database; left out).
- Errors: per-source and per-field isolation, log lines, ONE notification (updated, debounced) naming the failing sources, statistics on the settings page.

## Not done on purpose
- No writing of its own, no new Playnite items, no network access of its own.
- No reading of the dialog's unsaved choice (not reachable without internals).

## Images (0.2.0)
- Facts checked: a provider returns a `MetadataFile` (a path or web address, or the bytes) from `GetCoverImage` / `GetBackgroundImage` / `GetIcon`; Playnite only checks it is not null, then stores it (cover and icon through `AddFile`, a background as the address when "download backgrounds immediately" is off). The store (empty id) gives `GameMetadata.CoverImage` etc. Nothing is downloaded by the provider call itself unless the provider does it.
- Decision: candidates are measured by their header (core `ImageHeader`, `ImageReader`), chosen by `ImageChooser` (no I/O), and the source's own `MetadataFile` is handed back untouched (`Item.Tag`).
- Rules: reject below the minimum and off the expected proportion first; prefer the largest within the cap; above the cap only the smallest; between comparable sizes (15 % of pixels) the format order (JPEG, PNG, WebP, GIF, ICO, BMP), then source order; nothing acceptable gives null so Playnite continues with its own list; nothing measurable gives the first candidate.
- Not done: no resizing, no re-encoding, no download of losers, no use of image content (matching quality is the sources' job).

## Library clean-up (0.3.0)
- Facts checked: Playnite replaces a game's whole list when the first source answers (never merges) and `SkipExistingValues` only processes empty lists; the database lists are `IItemCollection<T>` (Genres, Companies = developers and publishers, Tags, Features, Series, Platforms, Regions, AgeRatings) with `Update`, `Remove`, `Add`, and games hold id lists; `BufferedUpdate()` buffers a block of writes; Playnite keeps `games.db` and the other LiteDB files locked while it runs (a shared-read copy fails with a sharing violation): no file backup from inside the process.
- Design: `CleanPlanner` (pure; snapshot in, plan out), `CleanRunner` (plan written through `ILibraryWriter`), `CleanJournal` / `CleanUndo` (state before saved before the first write, undo through `ILibraryAccess`), `PlayniteLibrary` / `PlayniteWriter` (the adapter, run on the UI thread in one buffered update), `CleanService` (dry run, confirmation, re-plan at write time and refuse if the plan changed, undo).
- Rules: only values that match a rule or the drop expression (loose keys as in merging); rules followed to a fixed point (loops reported); an untouched item that already has the target name is the survivor (renamed to the rule's wording only when it is not used by the other company field); platforms and regions are protected from deletion.
