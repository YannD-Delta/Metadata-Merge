using System.Collections.Generic;
using System.Linq;
using MetadataMerge.Core;

public static class MergeEngineTests
{
    private static SourceItems Src(string name, params string[] values)
    {
        return new SourceItems { SourceId = name, SourceName = name, Items = values.Select(Item.Name).ToList() };
    }

    private static string Run(MergeOptions o, params SourceItems[] sources) { return Fx.Join(MergeEngine.Merge(sources, o)); }

    public static void UnionKeepsSourcePriorityOrder()
    {
        Assert.Equal("Action|RPG|Shooter|Indie", Run(new MergeOptions(), Src("a", "Action", "RPG"), Src("b", "Shooter", "RPG", "Indie")));
    }

    public static void UnionDedupesIgnoringCaseAndKeepsTheFirstSpelling()
    {
        Assert.Equal("Open World|Co-op", Run(new MergeOptions(), Src("a", "Open World", "Co-op"), Src("b", "open  world", "Co-Op", "OPEN WORLD")));
    }

    public static void StrictModeKeepsNearDuplicates()
    {
        Assert.Equal("Co-op|Coop", Run(new MergeOptions { Loose = false }, Src("a", "Co-op"), Src("b", "Coop")));
    }

    public static void PerSourceCapTakesTheFirstValuesOfEachSource()
    {
        Assert.Equal("a1|a2|b1|b2", Run(new MergeOptions { PerSourceCap = 2 }, Src("a", "a1", "a2", "a3"), Src("b", "b1", "b2", "b3")));
    }

    public static void PerSourceCapIsAppliedAfterTheSourcesOwnDuplicates()
    {
        Assert.Equal("a|b", Run(new MergeOptions { PerSourceCap = 2 }, Src("s", "a", "A", "b", "c")));
    }

    public static void TotalCapCutsTheResult()
    {
        Assert.Equal("a|b|c", Run(new MergeOptions { TotalCap = 3 }, Src("s", "a", "b"), Src("t", "c", "d")));
    }

    public static void FirstNonEmptyTakesTheFirstSourceWithAValue()
    {
        Assert.Equal("x|y", Run(new MergeOptions { Mode = MergeMode.FirstNonEmpty }, Src("a"), Src("b", " ", ""), Src("c", "x", "y"), Src("d", "z")));
    }

    public static void FirstNonEmptyHonoursTheCap()
    {
        Assert.Equal("x", Run(new MergeOptions { Mode = MergeMode.FirstNonEmpty, PerSourceCap = 1 }, Src("c", "x", "y")));
    }

    public static void NothingInNothingOut()
    {
        Assert.Equal("", Run(new MergeOptions()));
        Assert.Equal("", Run(new MergeOptions(), Src("a"), Src("b")));
    }

    public static void BlankAndNullValuesAreSkipped()
    {
        var items = new SourceItems { Items = new List<Item> { null, Item.Name(" "), Item.Name("ok"), new Item { Kind = ItemKind.Name, Value = null } } };
        Assert.Equal("ok", Fx.Join(MergeEngine.Merge(new[] { items }, new MergeOptions())));
    }

    public static void SynonymsMergeAliasesAcrossSources()
    {
        var o = new MergeOptions { Synonyms = new Synonyms("Role-playing (RPG)=RPG\nFPS=Shooter", true, false) };
        Assert.Equal("RPG|Shooter", Run(o, Src("a", "Role-playing (RPG)", "FPS"), Src("b", "rpg", "Shooter")));
    }

    public static void SynonymsCanDropValues()
    {
        var o = new MergeOptions { Synonyms = new Synonyms("Early Access=", true, false) };
        Assert.Equal("Indie", Run(o, Src("a", "Early Access", "Indie")));
    }

    public static void CompanyMergingTreatsLegalFormsAsTheSameCompany()
    {
        Assert.Equal("Valve", Run(new MergeOptions { Company = true }, Src("a", "Valve"), Src("b", "Valve Corporation")));
    }

    public static void LinksAreDedupedByAddress()
    {
        var a = new SourceItems { Items = new List<Item> { Item.Link("Wiki", "https://wiki.example.com/Game"), Item.Link("Site", "http://game.example.com/") } };
        var b = new SourceItems { Items = new List<Item> { Item.Link("PCGamingWiki", "http://www.wiki.example.com/Game/"), Item.Link("Store", "https://store.example.com/1") } };
        var merged = MergeEngine.Merge(new[] { a, b }, new MergeOptions());
        Assert.Equal(3, merged.Count);
        Assert.Equal("Wiki", merged[0].Label);
        Assert.Equal("Store", merged[2].Label);
    }

    public static void IdsAndSpecsAreComparedByTheirOwnValue()
    {
        var g = System.Guid.NewGuid();
        var a = new SourceItems { Items = new List<Item> { Item.Id(g), Item.Spec("pc_windows"), Item.Name("pc_windows") } };
        var b = new SourceItems { Items = new List<Item> { Item.Id(g), Item.Spec("PC_Windows"), Item.Spec("xbox360") } };
        var merged = MergeEngine.Merge(new[] { a, b }, new MergeOptions());
        Assert.Equal(4, merged.Count);   // id, spec pc_windows, name pc_windows (a name is not a spec), spec xbox360
    }

    public static void TheInputIsNeverModified()
    {
        var a = Src("a", "x", "X");
        MergeEngine.Merge(new[] { a }, new MergeOptions());
        Assert.Equal(2, a.Items.Count);
    }
}
