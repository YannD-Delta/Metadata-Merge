# Changelog

## 0.3.0

- **Library clean-up**: applies the renaming rules and the "names to drop" expression of each field to the values already stored (genres, developers, publishers, features, tags, series, platforms, regions, age ratings): rename in place, merge into the existing item, drop, delete emptied items. Dry-run report first (per field counts, before/after samples, biggest items), confirmation, one buffered database update, only matching values touched (other add-ons' values stay as they are), rule chains followed to the end so a second run changes nothing, protected platforms and regions, shared company list handled. Settings button and main menu entries; never automatic.
- **Undo journal and undo**: the state before is saved before the first write (an interrupted pass can be undone) and completed at the end; "Undo the last clean-up" brings back deleted items with their ids, renames back, restores the games' lists and leaves a game edited since alone. Works while Playnite runs (Playnite locks its database files, so a file copy is not possible from an extension).
- **Rules files**: import / export of renaming rules and drop expressions for several fields at once (`[Genres]`, `[Features: drop]` sections).
- **Statistics per source and field**: the Diagnostics box says how many values each source gave for each field and in how many games (which source really returns tags, genres...).
- README: what a normal Playnite download does to existing values (lists are replaced; "ignore existing values" only fills empty lists) and the warning about re-downloading tags/features with Metadata Merge first. Tests that no field has any cap by default.

## 0.2.0

- Image fields: cover, background and icon (off by default). Every candidate is measured from its file header before anything is downloaded (HTTP Range for addresses, direct read for bytes and local files; PNG, JPEG, GIF, WebP, ICO, BMP), then one is chosen and returned: wrong proportions and thumbnails rejected, largest within a cap (a smaller one when everything is above it), JPEG preferred between comparable sizes, source order breaks ties. Only the winner is downloaded, by Playnite.
- Per image field: minimum size, maximum size, proportion tolerance, comparable-size limit, format preference.
- The image decisions are logged and listed in the settings Diagnostics box.
- The settings page lists the three image fields with their size rules.
- More parameters. List fields: kept only if given by at least N sources, order (as given, alphabetical, most agreed first), name writing (capitalise, lower, upper), longest name, names to drop by regular expression. Image fields: fewest pixels, heaviest file (KB, from the Range answer), your own expected ratio, wrong proportions as a last resort, above-the-maximum policy (smallest, never, largest), unreadable-size policy. General: bytes read per picture, time limit for pictures, decision log on/off and history length.
- Source pause: a source that failed or timed out N games in a row (default 5) is left alone for a while (default 10 minutes) instead of costing its time limit on every game of a long batch; it is flagged in the log, the notification and the diagnostics.

## 0.1.0

First version.

- Metadata source "Metadata Merge" for Playnite 10: union (or first non-empty) of the other sources of a field, in parallel, once per game.
- Fields: genres, developers, publishers, features, tags, links, series, platforms, regions, age ratings.
- Sources: the list Playnite has saved for the field (read from `config.json`, read only) or an own list; the official store is a source.
- Per field: on/off, mode, per-source cap, total cap, renaming rules. Loose comparison (case, accents, spacing, punctuation, company forms).
- Time limit per source, error isolation, one notification and log lines for failing or slow sources, statistics and diagnostics on the settings page.
- English and French.
