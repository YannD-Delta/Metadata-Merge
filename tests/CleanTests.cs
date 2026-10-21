using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MetadataMerge.Core;

/// <summary>A small in-memory library: the database lists, the games' id lists, and a writer that records what was done.</summary>
public sealed class FakeLib : ILibraryAccess
{
    public readonly Dictionary<string, List<LibItem>> Lists = new Dictionary<string, List<LibItem>>();
    public readonly List<LibGame> Games = new List<LibGame>();
    public int Commits;

    public LibItem Add(string collection, string name, bool isProtected = false)
    {
        List<LibItem> list;
        if (!Lists.TryGetValue(collection, out list)) Lists[collection] = list = new List<LibItem>();
        var item = new LibItem { Id = Guid.NewGuid(), Name = name, Protected = isProtected };
        list.Add(item);
        return item;
    }

    public LibGame Game(string name, params KeyValuePair<Field, LibItem[]>[] lists)
    {
        var g = new LibGame { Id = Guid.NewGuid(), Name = name };
        foreach (var kv in lists) g.Ids[kv.Key] = kv.Value.Select(i => i.Id).ToList();
        Games.Add(g);
        return g;
    }

    public static KeyValuePair<Field, LibItem[]> L(Field f, params LibItem[] items) { return new KeyValuePair<Field, LibItem[]>(f, items); }

    public LibrarySnapshot Snapshot()
    {
        var s = new LibrarySnapshot();
        foreach (var kv in Lists) s.Collections[kv.Key] = kv.Value.Select(i => new LibItem { Id = i.Id, Name = i.Name, Protected = i.Protected }).ToList();
        foreach (var g in Games)
        {
            var c = new LibGame { Id = g.Id, Name = g.Name };
            foreach (var kv in g.Ids) c.Ids[kv.Key] = new List<Guid>(kv.Value);
            s.Games.Add(c);
        }
        return s;
    }

    public string NameOf(string collection, Guid id) { var i = Lists[collection].FirstOrDefault(x => x.Id == id); return i == null ? null : i.Name; }

    public List<string> Values(LibGame g, Field f)
    {
        return (g.Ids.ContainsKey(f) ? g.Ids[f] : new List<Guid>()).Select(id => NameOf(CleanFields.Collection(f), id) ?? "?missing?").ToList();
    }

    public List<string> Names(string collection) { return Lists[collection].Select(i => i.Name).ToList(); }

    // ---- the writer
    public void RenameItem(string collection, Guid id, string newName) { Lists[collection].First(i => i.Id == id).Name = newName; }
    public Guid CreateItem(string collection, Field field, string name)
    {
        var existing = Lists[collection].FirstOrDefault(i => i.Name == name);
        return existing != null ? existing.Id : Add(collection, name).Id;
    }
    public void SetGameIds(Guid gameId, Field field, List<Guid> ids) { Games.First(g => g.Id == gameId).Ids[field] = ids; }
    public void RemoveItem(string collection, Guid id) { Lists[collection].RemoveAll(i => i.Id == id); }
    public void Commit() { Commits++; }

    // ---- what the undo needs
    public Guid? FindItem(string collection, string name) { List<LibItem> l; var i = Lists.TryGetValue(collection, out l) ? l.FirstOrDefault(x => x.Name == name) : null; return i == null ? (Guid?)null : i.Id; }
    public bool HasItem(string collection, Guid id) { List<LibItem> l; return Lists.TryGetValue(collection, out l) && l.Any(i => i.Id == id); }
    public List<Guid> GameIds(Guid gameId, Field field) { var g = Games.FirstOrDefault(x => x.Id == gameId); return g == null ? null : (g.Ids.ContainsKey(field) ? new List<Guid>(g.Ids[field]) : new List<Guid>()); }
    public bool InUse(string collection, Guid id) { return Games.Any(g => g.Ids.Values.Any(l => l.Contains(id))); }
    public void RestoreItem(string collection, Guid id, string name)
    {
        List<LibItem> list;
        if (!Lists.TryGetValue(collection, out list)) Lists[collection] = list = new List<LibItem>();
        list.Add(new LibItem { Id = id, Name = name });
    }
}

public static class CleanTests
{
    private static FakeLib Lib() { return new FakeLib(); }

    private static MergeSettings Rules(Action<MergeSettings> set)
    {
        var s = new MergeSettings();
        set(s);
        return s;
    }

    private static CleanResult Run(FakeLib lib, MergeSettings s)
    {
        return CleanRunner.Apply(CleanPlanner.Plan(lib.Snapshot(), s), lib);
    }

    private static string J(IEnumerable<string> v) { return string.Join("|", v); }

    public static void A_value_with_a_rule_is_renamed_in_place_and_keeps_its_games()
    {
        var lib = Lib();
        var fr = lib.Add("Genres", "Aventure");
        var g1 = lib.Game("A", FakeLib.L(Field.Genres, fr));
        var g2 = lib.Game("B", FakeLib.L(Field.Genres, fr));
        var id = fr.Id;
        var s = Rules(x => x.For(Field.Genres).Synonyms = "Aventure=Adventure");
        var plan = CleanPlanner.Plan(lib.Snapshot(), s);
        Assert.Equal(1, plan.Fields[Field.Genres].Renamed); Assert.Equal(0, plan.Fields[Field.Genres].Merged);
        Assert.Equal(2, plan.Fields[Field.Genres].GamesAffected);
        CleanRunner.Apply(plan, lib);
        Assert.Equal("Adventure", J(lib.Names("Genres")));
        Assert.Equal(id, lib.Games[0].Ids[Field.Genres][0]);   // same item, renamed
        Assert.Equal(1, lib.Commits);
    }

    public static void A_value_that_becomes_an_existing_one_is_merged_and_the_empty_item_deleted()
    {
        var lib = Lib();
        var fr = lib.Add("Genres", "Aventure"); var en = lib.Add("Genres", "Adventure");
        lib.Game("A", FakeLib.L(Field.Genres, fr));
        lib.Game("B", FakeLib.L(Field.Genres, en));
        lib.Game("Both", FakeLib.L(Field.Genres, fr, en, lib.Add("Genres", "Indie")));
        var s = Rules(x => x.For(Field.Genres).Synonyms = "Aventure=Adventure");
        var plan = CleanPlanner.Plan(lib.Snapshot(), s);
        Assert.Equal(1, plan.Fields[Field.Genres].Merged); Assert.Equal(0, plan.Fields[Field.Genres].Renamed);
        CleanRunner.Apply(plan, lib);
        Assert.Equal("Adventure|Indie", J(lib.Names("Genres")));
        Assert.Equal("Adventure", J(lib.Values(lib.Games[0], Field.Genres)));
        Assert.Equal("Adventure|Indie", J(lib.Values(lib.Games[2], Field.Genres)));   // the game that had both has the value once
    }

    public static void A_dropped_value_leaves_its_games_and_the_library()
    {
        var lib = Lib();
        var early = lib.Add("Features", "Early Access"); var solo = lib.Add("Features", "Solo");
        lib.Game("A", FakeLib.L(Field.Features, early, solo));
        lib.Game("B", FakeLib.L(Field.Features, early));
        var unused = lib.Add("Features", "early access");   // not used by any game: the database list is cleaned too
        var s = Rules(x => x.For(Field.Features).ExcludeRegex = "^early access$");
        var plan = CleanPlanner.Plan(lib.Snapshot(), s);
        Assert.Equal(2, plan.Fields[Field.Features].Dropped);
        CleanRunner.Apply(plan, lib);
        Assert.Equal("Solo", J(lib.Names("Features")));
        Assert.Equal("Solo", J(lib.Values(lib.Games[0], Field.Features)));
        Assert.Equal(0, lib.Games[1].Ids[Field.Features].Count);
    }

    public static void Values_written_by_other_add_ons_and_anything_without_a_rule_stay_exactly_as_they_are()
    {
        var lib = Lib();
        string[] tags = { "[UPS] No Music", "[HLTB] 12 h", "[GGDeals] -50 %", "[Install Drive] E:", "[SSV] 1", "[Lien manquant]", "Jeu solo", "Genres: Monde ouvert", "[POV] 1re personne", "[Style] Realistic" };
        var tagItems = tags.Select(t => lib.Add("Tags", t)).ToList();
        string[] feats = { "Wand", "Wand disponible", "Nexus Mods", "Solo", "Singleplayer", "Co-Op Extras: FREE" };
        var featItems = feats.Select(t => lib.Add("Features", t)).ToList();
        lib.Game("G1", FakeLib.L(Field.Tags, tagItems.ToArray()), FakeLib.L(Field.Features, featItems.ToArray()));
        lib.Game("G2", FakeLib.L(Field.Tags, tagItems.Take(3).ToArray()), FakeLib.L(Field.Features, featItems.Take(3).ToArray()));
        var before = lib.Snapshot();
        var s = Rules(x =>
        {
            x.For(Field.Tags).ExcludeRegex = @"^\[(POV|Style)\].*$";
            x.For(Field.Features).Synonyms = "Singleplayer=Solo";
            x.For(Field.Features).ExcludeRegex = "^Co-Op Extras:.*$";
        });
        Run(lib, s);
        // only the three matching values changed
        Assert.Equal(J(tags.Where(t => !t.StartsWith("[POV]") && !t.StartsWith("[Style]"))), J(lib.Names("Tags")));
        Assert.Equal("Wand|Wand disponible|Nexus Mods|Solo", J(lib.Names("Features")));
        foreach (var keep in new[] { "[UPS] No Music", "[HLTB] 12 h", "[GGDeals] -50 %", "[Install Drive] E:", "[SSV] 1", "[Lien manquant]", "Jeu solo", "Genres: Monde ouvert" })
            Assert.True(lib.Values(lib.Games[0], Field.Tags).Contains(keep), keep + " must survive");
        Assert.Equal("Wand|Wand disponible|Nexus Mods|Solo", J(lib.Values(lib.Games[0], Field.Features)));
        Assert.Equal("[UPS] No Music|[HLTB] 12 h|[GGDeals] -50 %", J(lib.Values(lib.Games[1], Field.Tags)));
        // the ids of the surviving values did not change
        Assert.True(before.Collections["Tags"].Take(8).All(i => lib.Lists["Tags"].Any(x => x.Id == i.Id && x.Name == i.Name)));
    }

    public static void A_second_run_changes_nothing()
    {
        var lib = Lib();
        var a = lib.Add("Genres", "Aventure"); var b = lib.Add("Genres", "Adventure"); var c = lib.Add("Genres", "Jeux de rôles"); var d = lib.Add("Genres", "RPG");
        var t = lib.Add("Tags", "[POV] x"); var t2 = lib.Add("Tags", "Jeu solo");
        lib.Game("A", FakeLib.L(Field.Genres, a, c), FakeLib.L(Field.Tags, t, t2));
        lib.Game("B", FakeLib.L(Field.Genres, b, d));
        var s = Rules(x => { x.For(Field.Genres).Synonyms = "Aventure=Adventure\nJeux de rôles=Role-playing (RPG)\nRPG=Role-playing (RPG)"; x.For(Field.Tags).ExcludeRegex = "^\\[POV\\]"; });
        var plan1 = CleanPlanner.Plan(lib.Snapshot(), s);
        Assert.False(plan1.IsEmpty);
        CleanRunner.Apply(plan1, lib);
        Assert.True(CleanPlanner.Plan(lib.Snapshot(), s).IsEmpty, "the second plan must be empty");
        Assert.Equal("Adventure|Role-playing (RPG)", J(lib.Names("Genres")));
    }

    public static void Rule_chains_are_followed_to_the_end_in_one_pass_and_loops_are_reported()
    {
        var lib = Lib();
        var a = lib.Add("Genres", "A");
        lib.Game("G", FakeLib.L(Field.Genres, a));
        var s = Rules(x => x.For(Field.Genres).Synonyms = "A=B\nB=C");
        Run(lib, s);
        Assert.Equal("C", J(lib.Names("Genres")));
        Assert.True(CleanPlanner.Plan(lib.Snapshot(), s).IsEmpty);

        var loop = Lib(); var x1 = loop.Add("Genres", "X"); loop.Game("G", FakeLib.L(Field.Genres, x1));
        var plan = CleanPlanner.Plan(loop.Snapshot(), Rules(x => x.For(Field.Genres).Synonyms = "X=Y\nY=X"));
        Assert.True(plan.Warnings.Any(w => w.Contains("loop")), string.Join(";", plan.Warnings));
    }

    public static void An_existing_item_with_the_target_spelling_of_another_case_is_renamed_to_the_rule_wording()
    {
        var lib = Lib();
        var fr = lib.Add("Genres", "Jeux de rôles"); var en = lib.Add("Genres", "Role-playing (rpg)");
        lib.Game("A", FakeLib.L(Field.Genres, fr)); lib.Game("B", FakeLib.L(Field.Genres, en));
        Run(lib, Rules(x => x.For(Field.Genres).Synonyms = "Jeux de rôles=Role-playing (RPG)"));
        Assert.Equal("Role-playing (RPG)", J(lib.Names("Genres")));
        Assert.Equal(1, lib.Lists["Genres"].Count);
    }

    public static void Developers_and_publishers_share_the_company_list_and_a_company_used_by_both_is_not_renamed_under_the_other()
    {
        var lib = Lib();
        var ea = lib.Add("Companies", "EA");
        lib.Game("G", FakeLib.L(Field.Developers, ea), FakeLib.L(Field.Publishers, ea));
        lib.Game("H", FakeLib.L(Field.Developers, ea));
        Run(lib, Rules(x => x.For(Field.Developers).Synonyms = "EA=Electronic Arts"));
        Assert.Equal("Electronic Arts", J(lib.Values(lib.Games[0], Field.Developers)));
        Assert.Equal("EA", J(lib.Values(lib.Games[0], Field.Publishers)));   // the publisher field has no rule: untouched
        Assert.Equal("Electronic Arts", J(lib.Values(lib.Games[1], Field.Developers)));
        Assert.Equal("EA|Electronic Arts", J(lib.Names("Companies").OrderBy(n => n, StringComparer.Ordinal).ToList()).Replace("Electronic Arts|EA", "EA|Electronic Arts"));
    }

    public static void A_company_used_only_as_a_developer_is_renamed_in_place_and_one_used_in_both_fields_with_both_rules_ends_up_right()
    {
        var lib = Lib();
        var wb = lib.Add("Companies", "WB Games");
        lib.Game("G", FakeLib.L(Field.Developers, wb));
        Run(lib, Rules(x => x.For(Field.Developers).Synonyms = "WB Games=Warner Bros. Interactive Entertainment"));
        Assert.Equal("Warner Bros. Interactive Entertainment", J(lib.Names("Companies")));

        var lib2 = Lib();
        var wb2 = lib2.Add("Companies", "WB Games");
        lib2.Game("G", FakeLib.L(Field.Developers, wb2), FakeLib.L(Field.Publishers, wb2));
        var both = Rules(x => { x.For(Field.Developers).Synonyms = "WB Games=Warner"; x.For(Field.Publishers).Synonyms = "WB Games=Warner"; });
        Run(lib2, both);
        Assert.Equal("Warner", J(lib2.Values(lib2.Games[0], Field.Developers)));
        Assert.Equal("Warner", J(lib2.Values(lib2.Games[0], Field.Publishers)));
        Assert.Equal("Warner", J(lib2.Names("Companies")));
        Assert.True(CleanPlanner.Plan(lib2.Snapshot(), both).IsEmpty);
    }

    public static void A_protected_item_keeps_existing_but_its_games_move()
    {
        var lib = Lib();
        var pcOld = lib.Add("Platforms", "PC Windows", true); var pc = lib.Add("Platforms", "PC (Windows)", true);
        lib.Game("G", FakeLib.L(Field.Platforms, pcOld));
        Run(lib, Rules(x => x.For(Field.Platforms).Synonyms = "PC Windows=PC (Windows)"));
        Assert.Equal("PC (Windows)", J(lib.Values(lib.Games[0], Field.Platforms)));
        Assert.Equal(2, lib.Lists["Platforms"].Count);   // an emulator may still refer to it
    }

    public static void Nothing_to_do_without_rules_and_a_bad_expression_is_ignored_with_a_warning()
    {
        var lib = Lib();
        var t = lib.Add("Tags", "x"); lib.Game("G", FakeLib.L(Field.Tags, t));
        Assert.True(CleanPlanner.Plan(lib.Snapshot(), new MergeSettings()).IsEmpty);
        var s = Rules(x => x.For(Field.Tags).ExcludeRegex = "([");
        var plan = CleanPlanner.Plan(lib.Snapshot(), s);
        Assert.True(plan.IsEmpty);
        Assert.True(plan.Warnings.Any(w => w.Contains("not valid")));
    }

    public static void The_order_of_a_games_other_values_is_kept()
    {
        var lib = Lib();
        var a = lib.Add("Tags", "a"); var b = lib.Add("Tags", "B-old"); var c = lib.Add("Tags", "c");
        lib.Game("G", FakeLib.L(Field.Tags, a, b, c));
        Run(lib, Rules(x => x.For(Field.Tags).Synonyms = "B-old=b"));
        Assert.Equal("a|b|c", J(lib.Values(lib.Games[0], Field.Tags)));
    }

    public static void The_report_says_what_would_happen_per_field_with_samples_and_the_biggest_items()
    {
        var lib = Lib();
        var fr = lib.Add("Genres", "Aventure"); var en = lib.Add("Genres", "Adventure"); var early = lib.Add("Features", "Early Access");
        for (int i = 0; i < 5; i++) lib.Game("G" + i, FakeLib.L(Field.Genres, fr), FakeLib.L(Field.Features, early));
        lib.Game("E", FakeLib.L(Field.Genres, en));
        var plan = CleanPlanner.Plan(lib.Snapshot(), Rules(x => { x.For(Field.Genres).Synonyms = "Aventure=Adventure"; x.For(Field.Features).ExcludeRegex = "^early"; }));
        string text = plan.Render();
        Assert.True(text.Contains("Games in the library: 6"), text);
        Assert.True(text.Contains("Genres: 0 value(s) renamed, 1 merged into another, 0 dropped, 5 game(s) affected"), text);
        Assert.True(text.Contains("\"Aventure\" -> merged into \"Adventure\" (5 game(s))"), text);
        Assert.True(text.Contains("Features: 0 value(s) renamed, 0 merged into another, 1 dropped, 5 game(s) affected"), text);
        Assert.True(text.Contains("Biggest items changed:"), text);
        Assert.True(text.Contains("every other value is left exactly as it is"), text);
        Assert.True(CleanPlanner.Plan(Lib().Snapshot(), new MergeSettings()).Render().Contains("Nothing to change"));
        Assert.Equal(plan.Signature(), CleanPlanner.Plan(lib.Snapshot(), Rules(x => { x.For(Field.Genres).Synonyms = "Aventure=Adventure"; x.For(Field.Features).ExcludeRegex = "^early"; })).Signature());
    }

    public static void The_loose_comparison_applies_to_the_stored_values_too()
    {
        var lib = Lib();
        var a = lib.Add("Features", "Single-player");
        lib.Game("G", FakeLib.L(Field.Features, a));
        Run(lib, Rules(x => x.For(Field.Features).Synonyms = "Singleplayer=Solo"));
        Assert.Equal("Solo", J(lib.Names("Features")));
        var strict = Lib(); var b = strict.Add("Features", "Single-player"); strict.Game("G", FakeLib.L(Field.Features, b));
        Run(strict, Rules(x => { x.LooseMatching = false; x.For(Field.Features).Synonyms = "Singleplayer=Solo"; }));
        Assert.Equal("Single-player", J(strict.Names("Features")));
    }

    public static void A_whole_library_is_planned_fast_and_a_second_pass_is_empty()
    {
        var lib = Lib();
        var rnd = new Random(7);
        var genres = new[] { "Aventure", "Adventure", "Indie", "Indépendant", "Action", "RPG", "Role-playing (RPG)" }.Select(n => lib.Add("Genres", n)).ToList();
        var tags = Enumerable.Range(0, 1000).Select(i => lib.Add("Tags", i % 97 == 0 ? "[POV] t" + i : i % 5 == 0 ? "[UPS] t" + i : "tag " + i)).ToList();
        var devs = Enumerable.Range(0, 800).Select(i => lib.Add("Companies", i % 50 == 0 ? "Aspyr" : i % 51 == 0 ? "Aspyr Media" : "Dev " + i)).ToList();
        for (int i = 0; i < 1835; i++)
            lib.Game("G" + i,
                FakeLib.L(Field.Genres, Enumerable.Range(0, 3).Select(k => genres[rnd.Next(genres.Count)]).Distinct().ToArray()),
                FakeLib.L(Field.Tags, Enumerable.Range(0, 40).Select(k => tags[rnd.Next(tags.Count)]).Distinct().ToArray()),
                FakeLib.L(Field.Developers, devs[rnd.Next(devs.Count)]), FakeLib.L(Field.Publishers, devs[rnd.Next(devs.Count)]));
        var s = Rules(x =>
        {
            x.For(Field.Genres).Synonyms = "Aventure=Adventure\nIndépendant=Indie\nRPG=Role-playing (RPG)";
            x.For(Field.Tags).ExcludeRegex = "^\\[(POV|Style)\\].*$";
            x.For(Field.Developers).Synonyms = "Aspyr=Aspyr Media"; x.For(Field.Publishers).Synonyms = "Aspyr=Aspyr Media";
        });
        var clock = Stopwatch.StartNew();
        var plan = CleanPlanner.Plan(lib.Snapshot(), s);
        long planMs = clock.ElapsedMilliseconds;
        Assert.True(planMs < 5000, "planning took " + planMs + " ms");
        CleanRunner.Apply(plan, lib);
        Assert.True(CleanPlanner.Plan(lib.Snapshot(), s).IsEmpty, "second pass");
        Assert.False(lib.Lists["Tags"].Any(i => i.Name.StartsWith("[POV]")));
        Assert.True(lib.Lists["Tags"].Count(i => i.Name.StartsWith("[UPS]")) > 100, "the [UPS] tags are all still there");
        Assert.False(lib.Lists["Genres"].Any(i => i.Name == "Aventure" || i.Name == "RPG" || i.Name == "Indépendant"));
    }

    // ---- undo

    private static FakeLib Messy(out MergeSettings s)
    {
        var lib = Lib();
        var fr = lib.Add("Genres", "Aventure"); var en = lib.Add("Genres", "Adventure"); var ind = lib.Add("Genres", "Indépendant");
        var pov = lib.Add("Tags", "[POV] x"); var ups = lib.Add("Tags", "[UPS] No Music");
        var sp = lib.Add("Features", "Singleplayer"); var wand = lib.Add("Features", "Wand");
        var ea = lib.Add("Companies", "EA");
        lib.Game("A", FakeLib.L(Field.Genres, fr, ind), FakeLib.L(Field.Tags, pov, ups), FakeLib.L(Field.Features, sp, wand), FakeLib.L(Field.Developers, ea), FakeLib.L(Field.Publishers, ea));
        lib.Game("B", FakeLib.L(Field.Genres, en), FakeLib.L(Field.Tags, ups), FakeLib.L(Field.Features, wand));
        s = Rules(x =>
        {
            x.For(Field.Genres).Synonyms = "Aventure=Adventure\nIndépendant=Indie";
            x.For(Field.Tags).ExcludeRegex = "^\\[POV\\]";
            x.For(Field.Features).Synonyms = "Singleplayer=Solo";
            x.For(Field.Developers).Synonyms = "EA=Electronic Arts";
        });
        return lib;
    }

    private static string State(FakeLib lib)
    {
        return string.Join(" / ", lib.Lists.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + string.Join(",", kv.Value.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal))))
            + " || " + string.Join(" / ", lib.Games.Select(g => g.Name + ":" + string.Join(";", g.Ids.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + string.Join(",", lib.Values(g, kv.Key))))));
    }

    public static void The_pass_can_be_undone_and_the_library_is_exactly_as_before()
    {
        MergeSettings s;
        var lib = Messy(out s);
        string before = State(lib);
        var ids = lib.Lists.ToDictionary(kv => kv.Key, kv => kv.Value.Select(i => i.Id).OrderBy(g => g).ToList());
        var result = CleanRunner.Apply(CleanPlanner.Plan(lib.Snapshot(), s), lib);
        Assert.True(State(lib) != before, "the pass changed something");
        var undo = CleanUndo.Apply(result.Journal, lib);
        Assert.Equal(before, State(lib));
        // same ids too: the deleted items came back with their old ids
        foreach (var kv in ids) Assert.True(lib.Lists[kv.Key].Select(i => i.Id).OrderBy(g => g).SequenceEqual(kv.Value), kv.Key + " ids");
        Assert.True(undo.ItemsRestored > 0 && undo.GamesRestored == 1, undo.ToString());   // only game A was changed by the pass
    }

    public static void The_journal_is_saved_before_the_first_write_and_again_at_the_end_and_survives_a_json_round_trip()
    {
        MergeSettings s;
        var lib = Messy(out s);
        var saved = new List<CleanJournal>();
        var lastBefore = new List<string>();
        var result = CleanRunner.Apply(CleanPlanner.Plan(lib.Snapshot(), s), lib, j => { saved.Add(j); lastBefore.Add(State(lib)); });
        Assert.Equal(2, saved.Count);
        Assert.True(lastBefore[0].Contains("Aventure"), "the first save happens before anything is written");
        Assert.True(saved[1].Done);
        var again = CleanJournal.FromJson(result.Journal.ToJson());
        Assert.Equal(result.Journal.Games.Count, again.Games.Count);
        Assert.Equal(result.Journal.Removed.Count, again.Removed.Count);
        Assert.True(again.Done && again.Games[0].Fields[0].Before.Count > 0);
        var undo = CleanUndo.Apply(again, lib);   // the deserialised journal works the same
        Assert.True(State(lib).Contains("Aventure"));
    }

    public static void An_interrupted_pass_can_still_be_undone_from_the_journal_saved_before_it_started()
    {
        MergeSettings s;
        var lib = Messy(out s);
        string before = State(lib);
        var plan = CleanPlanner.Plan(lib.Snapshot(), s);
        CleanJournal first = null;
        try { CleanRunner.Apply(plan, new Exploding(lib), j => { if (first == null) first = CleanJournal.FromJson(j.ToJson()); }); }
        catch (InvalidOperationException) { }
        Assert.True(first != null && !first.Done);
        CleanUndo.Apply(first, lib);
        Assert.Equal(before, State(lib));
    }

    private sealed class Exploding : ILibraryWriter
    {
        private readonly FakeLib inner; private int writes;
        public Exploding(FakeLib inner) { this.inner = inner; }
        private void Tick() { if (++writes == 3) throw new InvalidOperationException("disk full"); }
        public void RenameItem(string c, Guid id, string n) { Tick(); inner.RenameItem(c, id, n); }
        public Guid CreateItem(string c, Field f, string n) { Tick(); return inner.CreateItem(c, f, n); }
        public void SetGameIds(Guid g, Field f, List<Guid> ids) { Tick(); inner.SetGameIds(g, f, ids); }
        public void RemoveItem(string c, Guid id) { Tick(); inner.RemoveItem(c, id); }
        public void Commit() { inner.Commit(); }
    }

    public static void A_game_edited_after_the_pass_keeps_its_edit_when_the_pass_is_undone()
    {
        MergeSettings s;
        var lib = Messy(out s);
        var result = CleanRunner.Apply(CleanPlanner.Plan(lib.Snapshot(), s), lib);
        var extra = lib.Add("Genres", "Roguelike");
        lib.Games[0].Ids[Field.Genres].Add(extra.Id);   // the owner edited the genres of game A after the pass
        Assert.Equal(1, CleanUndo.EditedSince(result.Journal, lib));
        var undo = CleanUndo.Apply(result.Journal, lib);
        Assert.True(lib.Values(lib.Games[0], Field.Genres).Contains("Roguelike"), "the later edit survives");
        Assert.False(lib.Values(lib.Games[0], Field.Genres).Contains("Aventure"), "the genres list of that game was left alone");
        Assert.True(lib.Values(lib.Games[0], Field.Tags).Contains("[POV] x") && lib.Values(lib.Games[0], Field.Features).Contains("Singleplayer"), "its other lists are back to before");
        Assert.Equal(1, undo.FieldsSkipped);
    }

    public static void An_item_created_by_the_pass_goes_away_when_nothing_uses_it_after_the_undo()
    {
        var lib = Lib();
        var ea = lib.Add("Companies", "EA");
        lib.Game("G", FakeLib.L(Field.Developers, ea), FakeLib.L(Field.Publishers, ea));
        var s = Rules(x => x.For(Field.Developers).Synonyms = "EA=Electronic Arts");
        var result = CleanRunner.Apply(CleanPlanner.Plan(lib.Snapshot(), s), lib);
        Assert.Equal(1, result.Created);
        Assert.Equal(2, lib.Lists["Companies"].Count);
        CleanUndo.Apply(result.Journal, lib);
        Assert.Equal("EA", J(lib.Names("Companies")));
        Assert.Equal("EA", J(lib.Values(lib.Games[0], Field.Developers)));
    }

    public static void The_defaults_have_no_cap_at_all()
    {
        var s = new MergeSettings();
        foreach (var f in Fields.All) { Assert.Equal(0, s.For(f).PerSourceCap, f + " per source"); Assert.Equal(0, s.For(f).TotalCap, f + " total"); Assert.Equal(0, s.For(f).MinAgreeing, f + " agreement"); Assert.Equal(0, s.For(f).MaxValueLength, f + " length"); }
        Assert.Equal(0, s.OptionsFor(Field.Tags).TotalCap); Assert.Equal(0, s.OptionsFor(Field.Tags).PerSourceCap);
    }
}

