using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MetadataMerge.Core
{
    /// <summary>The list fields whose stored values the clean-up pass can fix (links and images are not lists of database items).</summary>
    public static class CleanFields
    {
        public static readonly Field[] All =
        {
            Field.Genres, Field.Developers, Field.Publishers, Field.Features, Field.Tags, Field.Series, Field.Platforms, Field.Regions, Field.AgeRatings
        };

        /// <summary>Developers and publishers are the same database list ("Companies"): an item can be both.</summary>
        public static string Collection(Field f) { return f == Field.Developers || f == Field.Publishers ? "Companies" : f.ToString(); }
    }

    public sealed class LibItem
    {
        public Guid Id;
        public string Name;
        /// <summary>Never deleted by the pass (platforms and regions can be referenced by emulators): games are moved off it, the item stays.</summary>
        public bool Protected;
    }

    public sealed class LibGame
    {
        public Guid Id;
        public string Name;
        public readonly Dictionary<Field, List<Guid>> Ids = new Dictionary<Field, List<Guid>>();
    }

    /// <summary>The stored values the pass works on: the database lists and the lists of ids each game holds. No Playnite types.</summary>
    public sealed class LibrarySnapshot
    {
        public readonly Dictionary<string, List<LibItem>> Collections = new Dictionary<string, List<LibItem>>();
        public readonly List<LibGame> Games = new List<LibGame>();
    }

    public enum CleanKind { Renamed, Merged, Dropped }

    public sealed class ItemChange
    {
        public Field Field;
        public Guid ItemId;
        public string OldName;
        public CleanKind Kind;
        /// <summary>The name it becomes (rename), or the name of the item it is folded into (merge); null for a drop.</summary>
        public string NewName;
        public int Games;
    }

    public sealed class FieldReport
    {
        public Field Field;
        public int Renamed, Merged, Dropped, GamesAffected;
        public readonly List<ItemChange> Changes = new List<ItemChange>();
        public bool Any { get { return Renamed + Merged + Dropped > 0; } }
    }

    public sealed class GameEdit
    {
        public Guid GameId;
        public readonly Dictionary<Field, List<Guid>> NewIds = new Dictionary<Field, List<Guid>>();
        /// <summary>The lists before the pass (for the undo journal).</summary>
        public readonly Dictionary<Field, List<Guid>> OldIds = new Dictionary<Field, List<Guid>>();
    }

    public sealed class CreateOp { public string Collection; public Field Field; public string Name; public Guid Placeholder; }
    public sealed class RenameOp { public string Collection; public Guid ItemId; public string OldName; public string NewName; }
    public sealed class RemoveOp { public string Collection; public Guid ItemId; public string Name; }

    /// <summary>What a pass would do, computed without touching anything.</summary>
    public sealed class CleanPlan
    {
        public int GameCount;
        public readonly Dictionary<Field, FieldReport> Fields = new Dictionary<Field, FieldReport>();
        public readonly List<CreateOp> Creates = new List<CreateOp>();
        public readonly List<RenameOp> Renames = new List<RenameOp>();
        public readonly List<GameEdit> GameEdits = new List<GameEdit>();
        public readonly List<RemoveOp> Removes = new List<RemoveOp>();
        /// <summary>Values that matched a rule but could not be handled (a rule loop, an unreadable expression).</summary>
        public readonly List<string> Warnings = new List<string>();

        public bool IsEmpty { get { return Creates.Count == 0 && Renames.Count == 0 && GameEdits.Count == 0 && Removes.Count == 0; } }

        public int Total(Func<FieldReport, int> pick) { return Fields.Values.Sum(pick); }

        /// <summary>Counts only: the confirmed plan and the one computed at write time must agree.</summary>
        public string Signature()
        {
            return string.Join(";", Fields.OrderBy(kv => kv.Key).Select(kv => kv.Key + ":" + kv.Value.Renamed + "/" + kv.Value.Merged + "/" + kv.Value.Dropped + "/" + kv.Value.GamesAffected))
                + "|" + Creates.Count + "," + Renames.Count + "," + GameEdits.Count + "," + Removes.Count;
        }

        public string Render(int samples = 12)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Metadata Merge: library clean-up report");
            sb.AppendLine("Games in the library: " + GameCount);
            if (IsEmpty) { sb.AppendLine("Nothing to change: no stored value matches a renaming rule or a drop expression."); return sb.ToString().TrimEnd(); }
            sb.AppendLine("Only values that match a renaming rule or a drop expression are touched; every other value is left exactly as it is.");
            foreach (var f in CleanFields.All)
            {
                FieldReport r;
                if (!Fields.TryGetValue(f, out r) || !r.Any) continue;
                sb.AppendLine();
                sb.AppendLine(f + ": " + r.Renamed + " value(s) renamed, " + r.Merged + " merged into another, " + r.Dropped + " dropped, " + r.GamesAffected + " game(s) affected");
                foreach (var c in r.Changes.OrderByDescending(x => x.Games).ThenBy(x => x.OldName, StringComparer.CurrentCultureIgnoreCase).Take(samples))
                {
                    string arrow = c.Kind == CleanKind.Dropped ? "dropped" : c.Kind == CleanKind.Merged ? "merged into \"" + c.NewName + "\"" : "renamed \"" + c.NewName + "\"";
                    sb.AppendLine("    \"" + c.OldName + "\" -> " + arrow + " (" + c.Games + " game(s))");
                }
                if (r.Changes.Count > samples) sb.AppendLine("    ... and " + (r.Changes.Count - samples) + " more");
            }
            var biggest = Fields.Values.SelectMany(r => r.Changes).OrderByDescending(c => c.Games).Take(5).ToList();
            if (biggest.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Biggest items changed:");
                foreach (var c in biggest) sb.AppendLine("    " + c.Field + " \"" + c.OldName + "\": " + c.Games + " game(s), " + c.Kind.ToString().ToLowerInvariant());
            }
            sb.AppendLine();
            sb.AppendLine("Total: " + Total(r => r.Renamed) + " renamed, " + Total(r => r.Merged) + " merged, " + Total(r => r.Dropped) + " dropped; "
                + GameEdits.Count + " game(s) get a new list; " + Removes.Count + " emptied item(s) deleted.");
            foreach (var w in Warnings) sb.AppendLine("WARNING: " + w);
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>The renaming rules and the drop expression of one field, as one function from a stored name to what becomes of it.</summary>
    public sealed class CleanRules
    {
        public readonly Synonyms Synonyms;
        public readonly Regex Exclude;
        private readonly bool loose;
        private readonly bool company;
        public readonly string Problem;

        public CleanRules(MergeSettings settings, Field f)
        {
            var fs = settings.For(f);
            loose = settings.LooseMatching;
            company = Fields.IsCompany(f);
            Synonyms = new Synonyms(fs.Synonyms, loose, company);
            if (!string.IsNullOrWhiteSpace(fs.ExcludeRegex))
            {
                try { Exclude = new Regex(fs.ExcludeRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
                catch (ArgumentException ex) { Problem = "the drop expression is not valid (" + ex.Message + ")"; }
            }
        }

        public bool HasAny { get { return Synonyms.Count > 0 || Exclude != null; } }

        public string Key(string name) { return company ? Keys.Company(name, loose) : Keys.Name(name, loose); }

        /// <summary>The name a stored value becomes; the same text when no rule touches it; null when it is dropped. Rules are followed to the end so a second pass finds nothing.</summary>
        public string Resolve(string name, out bool loop)
        {
            loop = false;
            if (string.IsNullOrWhiteSpace(name)) return name;
            if (Exclude != null && Exclude.IsMatch(name)) return null;
            string cur = name;
            var seen = new HashSet<string> { cur };
            for (int step = 0; step < 10; step++)
            {
                string next = Synonyms.Apply(cur, Key(cur));
                if (next == null) return null;
                if (string.Equals(next, cur, StringComparison.Ordinal)) break;
                cur = next;
                if (Exclude != null && Exclude.IsMatch(cur)) return null;
                if (!seen.Add(cur)) { loop = true; break; }
                if (step == 9) loop = true;
            }
            return cur;
        }
    }

    /// <summary>Computes the plan (no writes) from a snapshot of the stored values.</summary>
    public static class CleanPlanner
    {
        public static CleanPlan Plan(LibrarySnapshot snap, MergeSettings settings)
        {
            var plan = new CleanPlan { GameCount = snap.Games.Count };

            // working state: names of the items, the ids each game holds, items already deleted
            var names = new Dictionary<string, Dictionary<Guid, string>>();
            var protectedIds = new HashSet<Guid>();
            foreach (var kv in snap.Collections)
            {
                names[kv.Key] = kv.Value.ToDictionary(i => i.Id, i => i.Name ?? "");
                foreach (var i in kv.Value) if (i.Protected) protectedIds.Add(i.Id);
            }
            var work = new Dictionary<Guid, Dictionary<Field, List<Guid>>>();
            foreach (var g in snap.Games)
                work[g.Id] = CleanFields.All.ToDictionary(f => f, f => g.Ids.ContainsKey(f) && g.Ids[f] != null ? new List<Guid>(g.Ids[f]) : new List<Guid>());
            var original = snap.Games.ToDictionary(g => g.Id, g => CleanFields.All.ToDictionary(f => f, f => g.Ids.ContainsKey(f) && g.Ids[f] != null ? new List<Guid>(g.Ids[f]) : new List<Guid>()));
            var removeCandidates = new Dictionary<string, HashSet<Guid>>();
            var renamed = new Dictionary<Guid, string>();    // final name of renamed items
            var placeholders = new HashSet<Guid>();

            foreach (var field in CleanFields.All)
            {
                var report = new FieldReport { Field = field };
                plan.Fields[field] = report;
                var rules = new CleanRules(settings, field);
                if (rules.Problem != null) plan.Warnings.Add(field + ": " + rules.Problem + "; the expression is ignored.");
                if (!rules.HasAny) continue;
                string coll = CleanFields.Collection(field);
                Dictionary<Guid, string> items;
                if (!names.TryGetValue(coll, out items)) continue;

                // how many games use each item in this field, and in the other field of the same list
                var usage = new Dictionary<Guid, int>();
                var elsewhere = new Dictionary<Guid, int>();
                foreach (var g in snap.Games)
                {
                    foreach (var id in work[g.Id][field]) { int n; usage.TryGetValue(id, out n); usage[id] = n + 1; }
                    if (coll == "Companies")
                    {
                        var other = field == Field.Developers ? Field.Publishers : Field.Developers;
                        foreach (var id in work[g.Id][other]) { int n; elsewhere.TryGetValue(id, out n); elsewhere[id] = n + 1; }
                    }
                }
                Func<Guid, int> used = id => { int n; return usage.TryGetValue(id, out n) ? n : 0; };
                Func<Guid, int> usedElsewhere = id => { int n; return elsewhere.TryGetValue(id, out n) ? n : 0; };

                var map = new Dictionary<Guid, Guid>();      // item -> where its games go (Guid.Empty = dropped)
                var outcome = new Dictionary<Guid, string>(); // item -> new name, null = dropped (only touched items)
                foreach (var kv in items.ToList())
                {
                    bool loop;
                    string resolved = rules.Resolve(kv.Value, out loop);
                    if (loop) plan.Warnings.Add(field + ": the rules for \"" + kv.Value + "\" loop; the last step is used.");
                    if (resolved == null) outcome[kv.Key] = null;
                    else if (!string.Equals(resolved, kv.Value, StringComparison.Ordinal)) outcome[kv.Key] = resolved;
                }

                // drops
                foreach (var kv in outcome.Where(o => o.Value == null).ToList())
                {
                    map[kv.Key] = Guid.Empty;
                    report.Dropped++;
                    report.Changes.Add(new ItemChange { Field = field, ItemId = kv.Key, OldName = items[kv.Key], Kind = CleanKind.Dropped, Games = used(kv.Key) });
                    Candidate(removeCandidates, coll, kv.Key);
                }

                // renames, grouped by what the new name is (same key = the same value)
                var groups = outcome.Where(o => o.Value != null).GroupBy(o => rules.Key(o.Value)).OrderBy(gr => gr.Key, StringComparer.Ordinal).ToList();
                foreach (var grp in groups)
                {
                    var sources = grp.Select(x => x.Key).OrderByDescending(used).ThenBy(id => items[id], StringComparer.Ordinal).ToList();
                    string canonical = outcome[sources[0]];
                    // an untouched item that already is the target (same key, no rule moves it)
                    var existing = items.Where(i => !outcome.ContainsKey(i.Key) && rules.Key(i.Value) == grp.Key && !map.ContainsKey(i.Key)).Select(i => i.Key)
                        .OrderByDescending(used).ThenBy(id => items[id], StringComparer.Ordinal).ToList();
                    Guid survivor;
                    if (existing.Count > 0)
                    {
                        survivor = existing[0];
                        if (!string.Equals(items[survivor], canonical, StringComparison.Ordinal) && usedElsewhere(survivor) == 0)
                        {
                            report.Renamed++;
                            report.Changes.Add(new ItemChange { Field = field, ItemId = survivor, OldName = items[survivor], Kind = CleanKind.Renamed, NewName = canonical, Games = used(survivor) });
                            Rename(plan, names, renamed, coll, survivor, canonical, items);
                        }
                        foreach (var s in sources)
                        {
                            map[s] = survivor; report.Merged++;
                            report.Changes.Add(new ItemChange { Field = field, ItemId = s, OldName = items[s], Kind = CleanKind.Merged, NewName = items[survivor], Games = used(s) });
                            Candidate(removeCandidates, coll, s);
                        }
                    }
                    else
                    {
                        var inPlace = sources.Where(s => usedElsewhere(s) == 0).Cast<Guid?>().FirstOrDefault();
                        if (inPlace.HasValue)
                        {
                            survivor = inPlace.Value;
                            string oldName = items[survivor];
                            Rename(plan, names, renamed, coll, survivor, canonical, items);
                            report.Renamed++;
                            report.Changes.Add(new ItemChange { Field = field, ItemId = survivor, OldName = oldName, Kind = CleanKind.Renamed, NewName = canonical, Games = used(survivor) });
                        }
                        else
                        {
                            // every source is also used by the other company field: a new item takes the games of this field, the old ones stay for the other
                            survivor = Guid.NewGuid();
                            placeholders.Add(survivor);
                            plan.Creates.Add(new CreateOp { Collection = coll, Field = field, Name = canonical, Placeholder = survivor });
                            names[coll][survivor] = canonical;
                            report.Renamed++;
                            report.Changes.Add(new ItemChange { Field = field, ItemId = sources[0], OldName = items[sources[0]], Kind = CleanKind.Renamed, NewName = canonical, Games = used(sources[0]) });
                        }
                        foreach (var s in sources)
                        {
                            if (s == survivor) continue;
                            map[s] = survivor; report.Merged++;
                            report.Changes.Add(new ItemChange { Field = field, ItemId = s, OldName = items[s], Kind = CleanKind.Merged, NewName = canonical, Games = used(s) });
                            Candidate(removeCandidates, coll, s);
                        }
                        if (placeholders.Contains(survivor)) foreach (var s in sources) Candidate(removeCandidates, coll, s);
                    }
                }

                // the games of this field: ids mapped, dropped ones removed, duplicates folded
                var affected = new HashSet<Guid>();
                var touchedItems = new HashSet<Guid>(map.Keys.Concat(renamed.Keys));
                foreach (var g in snap.Games)
                {
                    var old = work[g.Id][field];
                    if (old.Count == 0) continue;
                    if (old.Any(id => touchedItems.Contains(id))) affected.Add(g.Id);
                    var fresh = new List<Guid>();
                    foreach (var id in old)
                    {
                        Guid to;
                        Guid target = map.TryGetValue(id, out to) ? to : id;
                        if (target == Guid.Empty) continue;
                        if (!fresh.Contains(target)) fresh.Add(target);
                    }
                    work[g.Id][field] = fresh;
                }
                report.GamesAffected = affected.Count;
            }

            // game edits: the final list of every field whose list changed
            foreach (var g in snap.Games)
            {
                GameEdit edit = null;
                foreach (var f in CleanFields.All)
                {
                    var before = original[g.Id][f];
                    var after = work[g.Id][f];
                    if (before.SequenceEqual(after)) continue;
                    if (edit == null) edit = new GameEdit { GameId = g.Id };
                    edit.NewIds[f] = after;
                    edit.OldIds[f] = before;
                }
                if (edit != null) plan.GameEdits.Add(edit);
            }

            // emptied items are deleted (never a protected one, never one that something still uses)
            var stillUsed = new HashSet<Guid>(work.Values.SelectMany(d => d.Values).SelectMany(l => l));
            foreach (var kv in removeCandidates)
                foreach (var id in kv.Value)
                {
                    if (stillUsed.Contains(id) || protectedIds.Contains(id) || placeholders.Contains(id)) continue;
                    if (renamed.ContainsKey(id)) continue;
                    string name;
                    if (!snap.Collections[kv.Key].Any(i => i.Id == id)) continue;
                    name = snap.Collections[kv.Key].First(i => i.Id == id).Name;
                    plan.Removes.Add(new RemoveOp { Collection = kv.Key, ItemId = id, Name = name });
                }
            return plan;
        }

        private static void Candidate(Dictionary<string, HashSet<Guid>> c, string coll, Guid id)
        {
            HashSet<Guid> set;
            if (!c.TryGetValue(coll, out set)) c[coll] = set = new HashSet<Guid>();
            set.Add(id);
        }

        private static void Rename(CleanPlan plan, Dictionary<string, Dictionary<Guid, string>> names, Dictionary<Guid, string> renamed, string coll, Guid id, string newName, Dictionary<Guid, string> items)
        {
            plan.Renames.Add(new RenameOp { Collection = coll, ItemId = id, OldName = items[id], NewName = newName });
            names[coll][id] = newName;
            items[id] = newName;
            renamed[id] = newName;
        }
    }

    /// <summary>Where a plan is written. The plugin implements it on the Playnite database; the tests on a plain in-memory library.</summary>
    public interface ILibraryWriter
    {
        void RenameItem(string collection, Guid id, string newName);
        /// <summary>Creates the item (or returns the one that already has this name) and gives its id.</summary>
        Guid CreateItem(string collection, Field field, string name);
        void SetGameIds(Guid gameId, Field field, List<Guid> ids);
        void RemoveItem(string collection, Guid id);
        /// <summary>Called once at the end: everything is applied in one buffered update.</summary>
        void Commit();
    }

    /// <summary>What the undo needs on top of writing: to look at the library as it is now, and to bring a deleted item back with its old id.</summary>
    public interface ILibraryAccess : ILibraryWriter
    {
        bool HasItem(string collection, Guid id);
        /// <summary>The ids a game holds for the field now; null when the game no longer exists.</summary>
        List<Guid> GameIds(Guid gameId, Field field);
        /// <summary>True when any game still uses the item (in any field).</summary>
        bool InUse(string collection, Guid id);
        void RestoreItem(string collection, Guid id, string name);
        /// <summary>The id of the item with exactly this name, or null.</summary>
        Guid? FindItem(string collection, string name);
    }

    public sealed class CleanResult
    {
        public int Created, Renamed, GamesChanged, Removed;
        public CleanJournal Journal;
        public override string ToString() { return Renamed + " renamed, " + Created + " created, " + GamesChanged + " games updated, " + Removed + " emptied items deleted"; }
    }

    public static class CleanRunner
    {
        /// <summary>
        /// Order: new items, renames, the games' lists, then the emptied items are deleted. The undo journal (the state before, then what was
        /// written) is handed to <paramref name="saveJournal"/> BEFORE the first write and again at the end.
        /// </summary>
        public static CleanResult Apply(CleanPlan plan, ILibraryWriter w, Action<CleanJournal> saveJournal = null)
        {
            var result = new CleanResult { Journal = CleanJournal.FromPlan(plan) };
            if (saveJournal != null) saveJournal(result.Journal);
            var real = new Dictionary<Guid, Guid>();
            foreach (var c in plan.Creates)
            {
                Guid id = w.CreateItem(c.Collection, c.Field, c.Name);
                real[c.Placeholder] = id;
                result.Journal.CreatedItems.Add(new JournalItem { Collection = c.Collection, Id = id, Name = c.Name });
                result.Created++;
            }
            foreach (var r in plan.Renames) { w.RenameItem(r.Collection, r.ItemId, r.NewName); result.Renamed++; }
            foreach (var e in plan.GameEdits)
            {
                var jg = result.Journal.Games.First(g => g.Id == e.GameId);
                foreach (var kv in e.NewIds)
                {
                    var ids = kv.Value.Select(id => { Guid r; return real.TryGetValue(id, out r) ? r : id; }).Distinct().ToList();
                    w.SetGameIds(e.GameId, kv.Key, ids);
                    jg.Fields.First(f => f.Field == kv.Key.ToString()).After = new List<Guid>(ids);
                }
                result.GamesChanged++;
            }
            foreach (var r in plan.Removes) { w.RemoveItem(r.Collection, r.ItemId); result.Removed++; }
            w.Commit();
            result.Journal.Done = true;
            if (saveJournal != null) saveJournal(result.Journal);
            return result;
        }
    }
}

