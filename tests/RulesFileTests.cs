using System.Collections.Generic;
using System.Linq;
using MetadataMerge.Core;

public static class RulesFileTests
{
    private const string Sample = @"# rules made from a library
[Genres]
Aventure=Adventure
Indépendant=Indie
# comment inside
[features]
Singleplayer=Solo
♾️ Infini ♾️=
[Features: drop]
^Co-Op Extras:.*$
^#Category_.*$
[Tags : DROP]
^\[(POV|Style)\].*$
[Developers]
EA=Electronic Arts
";

    public static void A_file_gives_rules_and_drop_expressions_per_field()
    {
        var problems = new List<string>();
        var b = RulesFile.Parse(Sample, problems);
        Assert.Equal(0, problems.Count, string.Join(";", problems));
        Assert.Equal(4, b.Count);
        Assert.Equal("Aventure=Adventure\nIndépendant=Indie", b[Field.Genres].Synonyms);
        Assert.Equal(2, b[Field.Genres].RuleCount);
        Assert.Equal("Singleplayer=Solo\n♾️ Infini ♾️=", b[Field.Features].Synonyms);
        Assert.Equal("(?:^Co-Op Extras:.*$)|(?:^#Category_.*$)", b[Field.Features].Exclude);   // several lines are alternatives
        Assert.Equal(@"^\[(POV|Style)\].*$", b[Field.Tags].Exclude);
        Assert.True(b[Field.Tags].Synonyms == null);
    }

    public static void Applying_a_file_replaces_only_what_it_holds()
    {
        var s = new MergeSettings();
        s.For(Field.Genres).Synonyms = "old=old"; s.For(Field.Genres).ExcludeRegex = "^keep$";
        s.For(Field.Series).Synonyms = "untouched=untouched";
        var p = new List<string>();
        int n = RulesFile.Apply(RulesFile.Parse(Sample, p), s);
        Assert.Equal(4, n);
        Assert.Equal("Aventure=Adventure\nIndépendant=Indie", s.For(Field.Genres).Synonyms);
        Assert.Equal("^keep$", s.For(Field.Genres).ExcludeRegex);   // the file had no drop section for genres
        Assert.Equal("untouched=untouched", s.For(Field.Series).Synonyms);
        Assert.True(new CleanRules(s, Field.Features).Exclude.IsMatch("Co-Op Extras: FREE") && new CleanRules(s, Field.Features).Exclude.IsMatch("#Category_x"));
    }

    public static void Problems_are_reported_and_the_rest_is_still_read()
    {
        var p = new List<string>();
        var b = RulesFile.Parse("orphan=rule\n[Nonsense]\na=b\n[CoverImage]\nx=y\n[Tags: drop]\n([\n[Tags]\nnoequals\nok=fine\n", p);
        Assert.Equal(1, b.Count);
        Assert.Equal("ok=fine", b[Field.Tags].Synonyms);
        Assert.True(p.Count >= 5, string.Join(";", p));
        Assert.True(p.Any(x => x.Contains("before any")) && p.Any(x => x.Contains("not a list field")) && p.Any(x => x.Contains("not a valid expression")) && p.Any(x => x.Contains("alias=name")));
    }

    public static void Exporting_then_importing_gives_the_same_rules()
    {
        var s = new MergeSettings();
        var p = new List<string>();
        RulesFile.Apply(RulesFile.Parse(Sample, p), s);
        string text = RulesFile.Render(s);
        var again = new MergeSettings();
        RulesFile.Apply(RulesFile.Parse(text, p), again);
        foreach (var f in new[] { Field.Genres, Field.Features, Field.Tags, Field.Developers })
        {
            Assert.Equal(s.For(f).Synonyms, again.For(f).Synonyms);
            Assert.Equal(s.For(f).ExcludeRegex, again.For(f).ExcludeRegex);
        }
        Assert.Equal(0, p.Count);
    }

    public static void Windows_line_endings_and_blank_input_are_fine()
    {
        var p = new List<string>();
        Assert.Equal(1, RulesFile.Parse("[Genres]\r\nA=B\r\n", p).Count);
        Assert.Equal(0, RulesFile.Parse("", p).Count);
        Assert.Equal(0, RulesFile.Parse(null, p).Count);
        Assert.Equal(0, p.Count);
    }
}
