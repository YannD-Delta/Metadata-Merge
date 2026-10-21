using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;
using MetadataMerge.Core;

namespace MetadataMerge
{
    /// <summary>The Playnite database as a plain snapshot for the clean-up planner.</summary>
    public static class PlayniteLibrary
    {
        public static LibrarySnapshot Snapshot(IPlayniteAPI api)
        {
            var db = api.Database;
            var s = new LibrarySnapshot();
            s.Collections["Genres"] = db.Genres.Select(i => new LibItem { Id = i.Id, Name = i.Name }).ToList();
            s.Collections["Companies"] = db.Companies.Select(i => new LibItem { Id = i.Id, Name = i.Name }).ToList();
            s.Collections["Tags"] = db.Tags.Select(i => new LibItem { Id = i.Id, Name = i.Name }).ToList();
            s.Collections["Features"] = db.Features.Select(i => new LibItem { Id = i.Id, Name = i.Name }).ToList();
            s.Collections["Series"] = db.Series.Select(i => new LibItem { Id = i.Id, Name = i.Name }).ToList();
            s.Collections["AgeRatings"] = db.AgeRatings.Select(i => new LibItem { Id = i.Id, Name = i.Name }).ToList();
            // platforms and regions can be referenced by emulators: they are never deleted, only renamed or emptied
            s.Collections["Platforms"] = db.Platforms.Select(i => new LibItem { Id = i.Id, Name = i.Name, Protected = true }).ToList();
            s.Collections["Regions"] = db.Regions.Select(i => new LibItem { Id = i.Id, Name = i.Name, Protected = true }).ToList();
            foreach (var g in db.Games)
            {
                var lg = new LibGame { Id = g.Id, Name = g.Name };
                foreach (var f in CleanFields.All) lg.Ids[f] = new List<Guid>(GameIds(g, f) ?? new List<Guid>());
                s.Games.Add(lg);
            }
            return s;
        }

        public static List<Guid> GameIds(Game g, Field f)
        {
            switch (f)
            {
                case Field.Genres: return g.GenreIds;
                case Field.Developers: return g.DeveloperIds;
                case Field.Publishers: return g.PublisherIds;
                case Field.Features: return g.FeatureIds;
                case Field.Tags: return g.TagIds;
                case Field.Series: return g.SeriesIds;
                case Field.Platforms: return g.PlatformIds;
                case Field.Regions: return g.RegionIds;
                case Field.AgeRatings: return g.AgeRatingIds;
            }
            return null;
        }

        public static void SetGameIds(Game g, Field f, List<Guid> ids)
        {
            switch (f)
            {
                case Field.Genres: g.GenreIds = ids; break;
                case Field.Developers: g.DeveloperIds = ids; break;
                case Field.Publishers: g.PublisherIds = ids; break;
                case Field.Features: g.FeatureIds = ids; break;
                case Field.Tags: g.TagIds = ids; break;
                case Field.Series: g.SeriesIds = ids; break;
                case Field.Platforms: g.PlatformIds = ids; break;
                case Field.Regions: g.RegionIds = ids; break;
                case Field.AgeRatings: g.AgeRatingIds = ids; break;
            }
        }
    }

    /// <summary>Writes a plan (or an undo) on the Playnite database. Run it inside one buffered database update, on the UI thread.</summary>
    public sealed class PlayniteWriter : ILibraryAccess
    {
        private readonly IPlayniteAPI api;
        private readonly Dictionary<Guid, Game> touched = new Dictionary<Guid, Game>();
        private HashSet<Guid> used;

        public PlayniteWriter(IPlayniteAPI api) { this.api = api; }

        private static void Rename<T>(IItemCollection<T> c, Guid id, string name) where T : DatabaseObject
        {
            var item = c.Get(id);
            if (item == null) return;
            item.Name = name;
            c.Update(item);
        }

        private static Guid Create<T>(IItemCollection<T> c, string name) where T : DatabaseObject
        {
            var existing = c.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));
            return existing != null ? existing.Id : c.Add(name).Id;
        }

        private static void Restore<T>(IItemCollection<T> c, Guid id, string name, Func<string, T> make) where T : DatabaseObject
        {
            var item = make(name);
            item.Id = id;
            c.Add(new List<T> { item });
        }

        public void RenameItem(string collection, Guid id, string newName)
        {
            var db = api.Database;
            switch (collection)
            {
                case "Genres": Rename(db.Genres, id, newName); break;
                case "Companies": Rename(db.Companies, id, newName); break;
                case "Tags": Rename(db.Tags, id, newName); break;
                case "Features": Rename(db.Features, id, newName); break;
                case "Series": Rename(db.Series, id, newName); break;
                case "AgeRatings": Rename(db.AgeRatings, id, newName); break;
                case "Platforms": Rename(db.Platforms, id, newName); break;
                case "Regions": Rename(db.Regions, id, newName); break;
            }
        }

        public Guid CreateItem(string collection, Field field, string name)
        {
            var db = api.Database;
            switch (collection)
            {
                case "Genres": return Create(db.Genres, name);
                case "Companies": return Create(db.Companies, name);
                case "Tags": return Create(db.Tags, name);
                case "Features": return Create(db.Features, name);
                case "Series": return Create(db.Series, name);
                case "AgeRatings": return Create(db.AgeRatings, name);
                case "Platforms": return Create(db.Platforms, name);
                case "Regions": return Create(db.Regions, name);
            }
            throw new InvalidOperationException("unknown list " + collection);
        }

        public void RestoreItem(string collection, Guid id, string name)
        {
            var db = api.Database;
            switch (collection)
            {
                case "Genres": Restore(db.Genres, id, name, n => new Genre(n)); break;
                case "Companies": Restore(db.Companies, id, name, n => new Company(n)); break;
                case "Tags": Restore(db.Tags, id, name, n => new Tag(n)); break;
                case "Features": Restore(db.Features, id, name, n => new GameFeature(n)); break;
                case "Series": Restore(db.Series, id, name, n => new Series(n)); break;
                case "AgeRatings": Restore(db.AgeRatings, id, name, n => new AgeRating(n)); break;
                case "Platforms": Restore(db.Platforms, id, name, n => new Platform(n)); break;
                case "Regions": Restore(db.Regions, id, name, n => new Region(n)); break;
            }
        }

        public Guid? FindItem(string collection, string name)
        {
            var db = api.Database;
            switch (collection)
            {
                case "Genres": return db.Genres.Where(x => x.Name == name).Select(x => (Guid?)x.Id).FirstOrDefault();
                case "Companies": return db.Companies.Where(x => x.Name == name).Select(x => (Guid?)x.Id).FirstOrDefault();
                case "Tags": return db.Tags.Where(x => x.Name == name).Select(x => (Guid?)x.Id).FirstOrDefault();
                case "Features": return db.Features.Where(x => x.Name == name).Select(x => (Guid?)x.Id).FirstOrDefault();
                case "Series": return db.Series.Where(x => x.Name == name).Select(x => (Guid?)x.Id).FirstOrDefault();
                case "AgeRatings": return db.AgeRatings.Where(x => x.Name == name).Select(x => (Guid?)x.Id).FirstOrDefault();
                case "Platforms": return db.Platforms.Where(x => x.Name == name).Select(x => (Guid?)x.Id).FirstOrDefault();
                case "Regions": return db.Regions.Where(x => x.Name == name).Select(x => (Guid?)x.Id).FirstOrDefault();
            }
            return null;
        }

        public bool HasItem(string collection, Guid id)
        {
            var db = api.Database;
            switch (collection)
            {
                case "Genres": return db.Genres.Get(id) != null;
                case "Companies": return db.Companies.Get(id) != null;
                case "Tags": return db.Tags.Get(id) != null;
                case "Features": return db.Features.Get(id) != null;
                case "Series": return db.Series.Get(id) != null;
                case "AgeRatings": return db.AgeRatings.Get(id) != null;
                case "Platforms": return db.Platforms.Get(id) != null;
                case "Regions": return db.Regions.Get(id) != null;
            }
            return false;
        }

        private Game GameFor(Guid id)
        {
            Game g;
            if (touched.TryGetValue(id, out g)) return g;
            return api.Database.Games.Get(id);
        }

        public List<Guid> GameIds(Guid gameId, Field field)
        {
            var g = GameFor(gameId);
            return g == null ? null : new List<Guid>(PlayniteLibrary.GameIds(g, field) ?? new List<Guid>());
        }

        public void SetGameIds(Guid gameId, Field field, List<Guid> ids)
        {
            Game g;
            if (!touched.TryGetValue(gameId, out g))
            {
                g = api.Database.Games.Get(gameId);
                if (g == null) return;
                touched[gameId] = g;
            }
            PlayniteLibrary.SetGameIds(g, field, ids);
            used = null;
        }

        public bool InUse(string collection, Guid id)
        {
            if (used == null)
            {
                used = new HashSet<Guid>();
                foreach (var g in api.Database.Games)
                    foreach (var f in CleanFields.All)
                        foreach (var i in PlayniteLibrary.GameIds(g, f) ?? new List<Guid>()) used.Add(i);
            }
            return used.Contains(id);
        }

        public void RemoveItem(string collection, Guid id)
        {
            var db = api.Database;
            switch (collection)
            {
                case "Genres": db.Genres.Remove(id); break;
                case "Companies": db.Companies.Remove(id); break;
                case "Tags": db.Tags.Remove(id); break;
                case "Features": db.Features.Remove(id); break;
                case "Series": db.Series.Remove(id); break;
                case "AgeRatings": db.AgeRatings.Remove(id); break;
                case "Platforms": db.Platforms.Remove(id); break;
                case "Regions": db.Regions.Remove(id); break;
            }
        }

        public void Commit()
        {
            if (touched.Count > 0) api.Database.Games.Update(touched.Values.ToList());
            touched.Clear();
            used = null;
        }
    }

    /// <summary>
    /// The clean-up pass: a dry run that only reports, then (on a confirmation) one buffered database update, with an undo journal written before
    /// the first write. It never runs by itself: only from the settings button or the main menu, and it asks first. Undo works while Playnite
    /// runs (Playnite keeps its database files locked, so they cannot be copied).
    /// </summary>
    public sealed class CleanService
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IPlayniteAPI api;
        private readonly Func<MergeSettings> settings;
        private readonly Dispatcher ui;
        private readonly string dataDir;
        private int running;

        public CleanService(IPlayniteAPI api, Func<MergeSettings> settings, Dispatcher ui, string dataDir)
        {
            this.api = api;
            this.settings = settings;
            this.ui = ui;
            this.dataDir = dataDir;
        }

        public string JournalDir { get { return Path.Combine(dataDir, "clean-journals"); } }
        public string ReportDir { get { return Path.Combine(dataDir, "clean-reports"); } }

        private T OnUi<T>(Func<T> f) { return ui == null || ui.CheckAccess() ? f() : (T)ui.Invoke(f); }

        public CleanPlan Plan()
        {
            var snapshot = OnUi(() => PlayniteLibrary.Snapshot(api));
            return CleanPlanner.Plan(snapshot, settings());
        }

        public string SaveReport(CleanPlan plan, string kind)
        {
            Directory.CreateDirectory(ReportDir);
            string path = Path.Combine(ReportDir, "clean-" + kind + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            File.WriteAllText(path, plan.Render(500), Encoding.UTF8);
            return path;
        }

        /// <summary>The newest journal of a pass that was written, or null.</summary>
        public string LastJournalPath()
        {
            if (!Directory.Exists(JournalDir)) return null;
            return Directory.GetFiles(JournalDir, "clean-*.json").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
        }

        /// <summary>
        /// Writes the plan in one buffered update. The plan is computed again on the live library first: if it is not the one that was confirmed,
        /// nothing is written. The undo journal is saved before the first write (an interrupted pass can still be undone).
        /// </summary>
        public CleanResult Apply(CleanPlan confirmed, out string journalPath)
        {
            Directory.CreateDirectory(JournalDir);
            string path = Path.Combine(JournalDir, "clean-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            journalPath = path;
            var result = OnUi(() =>
            {
                var snapshot = PlayniteLibrary.Snapshot(api);
                var fresh = CleanPlanner.Plan(snapshot, settings());
                if (fresh.Signature() != confirmed.Signature()) throw new InvalidOperationException(Loc.Get("LOCMM_CleanChanged"));
                var writer = new PlayniteWriter(api);
                using (api.Database.BufferedUpdate())
                    return CleanRunner.Apply(fresh, writer, j => File.WriteAllText(path, j.ToJson(), Encoding.UTF8));
            });
            logger.Info("Metadata Merge: clean-up applied (" + result + "); undo journal " + path);
            return result;
        }

        /// <summary>Puts the library back as the journal says (the games edited since are left alone).</summary>
        public UndoResult Undo(string journalPath)
        {
            var journal = CleanJournal.FromJson(File.ReadAllText(journalPath, Encoding.UTF8));
            var result = OnUi(() =>
            {
                var writer = new PlayniteWriter(api);
                using (api.Database.BufferedUpdate())
                    return CleanUndo.Apply(journal, writer);
            });
            File.Move(journalPath, journalPath + ".undone");
            logger.Info("Metadata Merge: clean-up undone (" + result + ")");
            return result;
        }

        /// <summary>For the testbed and the tests: no dialog. Returns the report; with apply, also does the pass.</summary>
        public string RunWithoutDialogs(bool apply, out string journalPath)
        {
            journalPath = null;
            var plan = Plan();
            string text = plan.Render(500);
            if (apply && !plan.IsEmpty)
            {
                var result = Apply(plan, out journalPath);
                text += Environment.NewLine + "APPLIED: " + result;
            }
            return text;
        }

        public string UndoWithoutDialogs()
        {
            string path = LastJournalPath();
            if (path == null) return "NO JOURNAL";
            return "UNDONE: " + Undo(path);
        }

        // ---- the interactive pass

        public void RunInteractive() { Start(Interactive); }
        public void UndoInteractive() { Start(InteractiveUndo); }

        private void Start(Action action)
        {
            if (System.Threading.Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
            Task.Run(() =>
            {
                try { action(); }
                catch (Exception ex)
                {
                    logger.Error(ex, "Metadata Merge: clean-up failed");
                    Show(Loc.Format("LOCMM_CleanFailed", ex.Message), MessageBoxImage.Error, null);
                }
                finally { System.Threading.Interlocked.Exchange(ref running, 0); }
            });
        }

        private MessageBoxOption Show(string text, MessageBoxImage icon, List<MessageBoxOption> options)
        {
            return OnUi(() => options == null
                ? ShowSimple(text, icon)
                : api.Dialogs.ShowMessage(text, Loc.Get("LOCMM_CleanTitle"), icon, options));
        }

        private MessageBoxOption ShowSimple(string text, MessageBoxImage icon)
        {
            api.Dialogs.ShowMessage(text, Loc.Get("LOCMM_CleanTitle"), MessageBoxButton.OK, icon);
            return null;
        }

        private void Interactive()
        {
            var plan = Plan();
            string report = SaveReport(plan, "dry-run");
            if (plan.IsEmpty)
            {
                Show(Loc.Get("LOCMM_CleanNothing") + Environment.NewLine + Environment.NewLine + report, MessageBoxImage.Information, null);
                return;
            }
            string summary = string.Join(Environment.NewLine, plan.Render(4).Split('\n').Take(40).Select(l => l.TrimEnd()));
            var apply = new MessageBoxOption(Loc.Get("LOCMM_CleanApply"), false, false);
            var open = new MessageBoxOption(Loc.Get("LOCMM_CleanOpenReport"), false, false);
            var cancel = new MessageBoxOption(Loc.Get("LOCMM_CleanCancel"), true, true);
            while (true)
            {
                string text = Loc.Get("LOCMM_CleanDry") + Environment.NewLine + Environment.NewLine + summary + Environment.NewLine + Environment.NewLine
                    + Loc.Format("LOCMM_CleanReportAt", report) + Environment.NewLine + Loc.Get("LOCMM_CleanUndoNote") + Environment.NewLine + Environment.NewLine + Loc.Get("LOCMM_CleanAsk");
                var choice = Show(text, MessageBoxImage.Question, new List<MessageBoxOption> { apply, open, cancel });
                if (choice == null || choice.IsCancel || choice.Title == cancel.Title) return;
                if (choice.Title == open.Title) { try { Process.Start(report); } catch (Exception ex) { logger.Warn(ex, "Metadata Merge: cannot open the report"); } continue; }
                break;
            }
            string journal;
            var result = Apply(plan, out journal);
            SaveReport(plan, "applied");
            OnUi<object>(() =>
            {
                api.Notifications.Add(new NotificationMessage("MetadataMerge.Clean", Loc.Format("LOCMM_CleanDone", result.ToString()), NotificationType.Info));
                return null;
            });
        }

        private void InteractiveUndo()
        {
            string path = LastJournalPath();
            if (path == null) { Show(Loc.Get("LOCMM_UndoNone"), MessageBoxImage.Information, null); return; }
            var journal = CleanJournal.FromJson(File.ReadAllText(path, Encoding.UTF8));
            int games = journal.Games.Count, items = journal.Renamed.Count + journal.Removed.Count;
            var yes = new MessageBoxOption(Loc.Get("LOCMM_UndoApply"), false, false);
            var no = new MessageBoxOption(Loc.Get("LOCMM_CleanCancel"), true, true);
            var choice = Show(Loc.Format("LOCMM_UndoAsk", journal.When, items, games), MessageBoxImage.Question, new List<MessageBoxOption> { yes, no });
            if (choice == null || choice.IsCancel || choice.Title == no.Title) return;
            var result = Undo(path);
            OnUi<object>(() =>
            {
                api.Notifications.Add(new NotificationMessage("MetadataMerge.Clean", Loc.Format("LOCMM_UndoDone", result.ToString()), NotificationType.Info));
                return null;
            });
        }
    }
}
