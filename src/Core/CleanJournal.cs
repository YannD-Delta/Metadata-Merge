using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace MetadataMerge.Core
{
    public sealed class JournalItem { public string Collection; public Guid Id; public string Name; public string OldName; }

    public sealed class JournalField { public string Field; public List<Guid> Before = new List<Guid>(); public List<Guid> After = new List<Guid>(); }

    public sealed class JournalGame { public Guid Id; public List<JournalField> Fields = new List<JournalField>(); }

    /// <summary>
    /// Everything a clean-up pass changed, as it was before and as it was written, so the pass can be undone while Playnite runs (Playnite keeps
    /// its database files locked, so they cannot be copied). Written before the first write and again at the end.
    /// </summary>
    public sealed class CleanJournal
    {
        public int Version = 1;
        public string When;
        public bool Done;
        public List<JournalItem> Renamed = new List<JournalItem>();
        public List<JournalItem> Removed = new List<JournalItem>();
        public List<JournalItem> CreatedItems = new List<JournalItem>();
        /// <summary>The items the pass was going to create (known before the first write: an interrupted pass leaves them behind).</summary>
        public List<JournalItem> PlannedCreates = new List<JournalItem>();
        public List<JournalGame> Games = new List<JournalGame>();


        public static CleanJournal FromPlan(CleanPlan plan)
        {
            var j = new CleanJournal { When = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            foreach (var r in plan.Renames) j.Renamed.Add(new JournalItem { Collection = r.Collection, Id = r.ItemId, Name = r.NewName, OldName = r.OldName });
            foreach (var c in plan.Creates) j.PlannedCreates.Add(new JournalItem { Collection = c.Collection, Id = Guid.Empty, Name = c.Name });
            foreach (var r in plan.Removes) j.Removed.Add(new JournalItem { Collection = r.Collection, Id = r.ItemId, Name = r.Name });
            foreach (var e in plan.GameEdits)
            {
                var g = new JournalGame { Id = e.GameId };
                foreach (var kv in e.OldIds) g.Fields.Add(new JournalField { Field = kv.Key.ToString(), Before = new List<Guid>(kv.Value) });
                j.Games.Add(g);
            }
            return j;
        }

        public string ToJson() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(this); }

        public static CleanJournal FromJson(string json) { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<CleanJournal>(json); }
    }

    public sealed class UndoResult
    {
        public int ItemsRestored, ItemsRenamed, GamesRestored, FieldsSkipped, CreatedDeleted;
        public override string ToString()
        {
            return ItemsRestored + " deleted item(s) brought back, " + ItemsRenamed + " renamed back, " + GamesRestored + " game(s) restored, " + CreatedDeleted + " created item(s) removed"
                + (FieldsSkipped > 0 ? ", " + FieldsSkipped + " list(s) left alone because they were edited since" : "");
        }
    }

    public static class CleanUndo
    {
        /// <summary>How many game lists were edited since the pass (they would be left alone).</summary>
        public static int EditedSince(CleanJournal j, ILibraryAccess lib)
        {
            int n = 0;
            foreach (var g in j.Games)
            {
                foreach (var f in g.Fields)
                {
                    Field field;
                    if (!Enum.TryParse(f.Field, out field)) continue;
                    var cur = lib.GameIds(g.Id, field);
                    if (cur != null && j.Done && !cur.SequenceEqual(f.After)) n++;
                }
            }
            return n;
        }

        /// <summary>Brings back the deleted items (same ids), renames back, restores the games' lists, then deletes what the pass created if nothing uses it.</summary>
        public static UndoResult Apply(CleanJournal j, ILibraryAccess lib)
        {
            var result = new UndoResult();
            foreach (var r in j.Removed)
                if (!lib.HasItem(r.Collection, r.Id)) { lib.RestoreItem(r.Collection, r.Id, r.Name); result.ItemsRestored++; }
            foreach (var r in j.Renamed)
                if (lib.HasItem(r.Collection, r.Id)) { lib.RenameItem(r.Collection, r.Id, r.OldName); result.ItemsRenamed++; }
            foreach (var g in j.Games)
            {
                bool any = false;
                foreach (var f in g.Fields)
                {
                    Field field;
                    if (!Enum.TryParse(f.Field, out field)) continue;
                    var cur = lib.GameIds(g.Id, field);
                    if (cur == null) break;   // the game was deleted since
                    if (j.Done && !cur.SequenceEqual(f.After)) { result.FieldsSkipped++; continue; }   // edited since: left alone
                    lib.SetGameIds(g.Id, field, new List<Guid>(f.Before));
                    any = true;
                }
                if (any) result.GamesRestored++;
            }
            lib.Commit();
            var created = j.CreatedItems.Select(c => new KeyValuePair<string, Guid?>(c.Collection, c.Id)).ToList();
            // a pass that was interrupted did not record the ids it created: they are found again by name
            foreach (var p in j.PlannedCreates)
                if (!j.CreatedItems.Any(c => c.Collection == p.Collection && c.Name == p.Name)) created.Add(new KeyValuePair<string, Guid?>(p.Collection, lib.FindItem(p.Collection, p.Name)));
            foreach (var c in created)
                if (c.Value.HasValue && lib.HasItem(c.Key, c.Value.Value) && !lib.InUse(c.Key, c.Value.Value)) { lib.RemoveItem(c.Key, c.Value.Value); result.CreatedDeleted++; }
            lib.Commit();
            return result;
        }
    }
}

