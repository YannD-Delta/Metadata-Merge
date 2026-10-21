using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MetadataMerge.Core;

public static class ConfigTests
{
    private const string Self = "b8e2f4a7-3c19-4d65-9a0b-6f7d1e2c5a84";
    private const string Igdb = "ac3a1b2c-0000-4000-8000-000000000001";
    private const string Pcgw = "ac3a1b2c-0000-4000-8000-000000000002";
    private const string Steam = "ac3a1b2c-0000-4000-8000-000000000003";

    private static string Json(string self = Self)
    {
        return "{ \"Version\": 6, \"Language\": \"fr_FR\", \"MetadataSettings\": {"
            + " \"GamesSource\": 0, \"SkipExistingValues\": false,"
            + " \"Genre\": { \"Import\": true, \"Sources\": [\"" + self + "\", \"" + Igdb.ToUpperInvariant() + "\", \"" + Pcgw + "\", \"00000000-0000-0000-0000-000000000000\"] },"
            + " \"Tag\": { \"Import\": true, \"Sources\": [\"" + Pcgw + "\", \"00000000-0000-0000-0000-000000000000\"] },"
            + " \"Links\": { \"Import\": false, \"Sources\": [] },"
            + " \"Platform\": { \"Import\": true }"
            + " }, \"Other\": [1, 2, 3] }";
    }

    public static void ParsesTheSourceListsPerField()
    {
        var lists = PlayniteConfig.ParseSources(Json());
        Assert.Equal(4, lists[Field.Genres].Count);
        Assert.Equal(Self, lists[Field.Genres][0]);
        Assert.Equal(Igdb, lists[Field.Genres][1]);   // ids come back lower-case
        Assert.Equal(PlayniteConfig.StoreId, lists[Field.Genres][3]);
        Assert.Equal(2, lists[Field.Tags].Count);
        Assert.Equal(0, lists[Field.Links].Count);
    }

    public static void AFieldWithoutASourcesListOrMissingIsLeftOut()
    {
        var lists = PlayniteConfig.ParseSources(Json());
        Assert.Equal(0, lists[Field.Platforms].Count);
        Assert.False(lists.ContainsKey(Field.Developers));
    }

    public static void AFileWithoutMetadataSettingsGivesNothing()
    {
        Assert.Equal(0, PlayniteConfig.ParseSources("{ \"Language\": \"en_US\" }").Count);
        Assert.Equal(0, PlayniteConfig.ParseSources("[]").Count);
    }

    public static void TextThatIsNotJsonThrows()
    {
        Assert.Throws<ArgumentException>(() => PlayniteConfig.ParseSources("{ not json"));
    }

    public static void ReaderCachesUntilTheFileChanges()
    {
        string path = Temp();
        try
        {
            File.WriteAllText(path, Json());
            int reads = 0;
            var reader = new ConfigReader(() => { reads++; return path; });
            Assert.Equal(4, reader.Get()[Field.Genres].Count);
            Assert.Equal(4, reader.Get()[Field.Genres].Count);
            File.WriteAllText(path, Json().Replace("\"" + Pcgw + "\", \"00000000", "\"00000000"));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal(3, reader.Get()[Field.Genres].Count);
            Assert.Equal(null, reader.LastError);
        }
        finally { File.Delete(path); }
    }

    public static void ReaderKeepsTheLastGoodAnswerWhenTheFileIsBrokenOrGone()
    {
        string path = Temp();
        try
        {
            File.WriteAllText(path, Json());
            var reader = new ConfigReader(() => path);
            Assert.Equal(4, reader.Get()[Field.Genres].Count);
            File.WriteAllText(path, "{ half written");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(2));
            Assert.Equal(4, reader.Get()[Field.Genres].Count);
            Assert.True(reader.LastError != null);
            File.Delete(path);
            Assert.Equal(4, reader.Get()[Field.Genres].Count);
        }
        finally { File.Delete(path); }
    }

    public static void ReaderToleratesAMissingPath()
    {
        var reader = new ConfigReader(() => null);
        Assert.Equal(0, reader.Get().Count);
        var thrower = new ConfigReader(() => { throw new InvalidOperationException("no api"); });
        Assert.Equal(0, thrower.Get().Count);
        Assert.Equal("no api", thrower.LastError);
    }

    // ---- which sources to merge

    private static string Join(IEnumerable<string> ids) { return string.Join(",", ids.Select(i => i.Substring(0, 4) == "0000" ? "store" : i.Substring(i.Length - 1))); }

    public static void PlayniteModeTakesTheSourcesAfterTheExtension()
    {
        var list = new List<string> { Pcgw, Self, Igdb, PlayniteConfig.StoreId };
        Assert.Equal("1,store", Join(SourceResolver.Resolve(new FieldSettings(), list, Self)));
    }

    public static void PlayniteModeTakesAllTheOthersWhenTheExtensionIsNotInTheSavedList()
    {
        var list = new List<string> { Igdb, Pcgw };
        Assert.Equal("1,2", Join(SourceResolver.Resolve(new FieldSettings(), list, Self)));
    }

    public static void TheExtensionIsNeverItsOwnSource()
    {
        var fs = new FieldSettings { Sources = SourceMode.Custom, CustomSources = new List<string> { Self.ToUpperInvariant(), Igdb } };
        Assert.Equal("1", Join(SourceResolver.Resolve(fs, null, Self)));
    }

    public static void CustomModeIgnoresTheConfig()
    {
        var fs = new FieldSettings { Sources = SourceMode.Custom, CustomSources = new List<string> { Steam, Pcgw, Steam } };
        Assert.Equal("3,2", Join(SourceResolver.Resolve(fs, new List<string> { Igdb }, Self)));
    }

    public static void PlayniteModeFallsBackToTheOwnListWhenThePlayniteListGivesNothing()
    {
        var fs = new FieldSettings { CustomSources = new List<string> { Igdb } };
        Assert.Equal("1", Join(SourceResolver.Resolve(fs, new List<string> { Self }, Self)));
        Assert.Equal("1", Join(SourceResolver.Resolve(fs, null, Self)));
        Assert.Equal("", Join(SourceResolver.Resolve(new FieldSettings(), null, Self)));
    }

    private static string Temp() { return Path.Combine(Path.GetTempPath(), "mm-config-" + Guid.NewGuid().ToString("N") + ".json"); }
}

public static class SettingsTests
{
    public static void OptionalFieldsAreOffByDefaultTheOthersOn()
    {
        var s = new MergeSettings();
        foreach (var f in new[] { Field.Genres, Field.Developers, Field.Publishers, Field.Features, Field.Tags, Field.Links, Field.Series, Field.Platforms })
            Assert.True(s.IsEnabled(f), f + " should be on");
        Assert.False(s.IsEnabled(Field.Regions));
        Assert.False(s.IsEnabled(Field.AgeRatings));
    }

    public static void NormalizeFillsMissingFieldsDropsUnknownOnesAndFixesCase()
    {
        var s = new MergeSettings();
        s.Fields = new Dictionary<string, FieldSettings>
        {
            { "genres", new FieldSettings { PerSourceCap = 4, CustomSources = null, Synonyms = null } },
            { "Nonsense", new FieldSettings() }
        };
        s.Normalize();
        Assert.Equal(Fields.All.Length, s.Fields.Count);
        Assert.False(s.Fields.ContainsKey("Nonsense"));
        Assert.Equal(4, s.For(Field.Genres).PerSourceCap);
        Assert.True(s.For(Field.Genres).CustomSources != null && s.For(Field.Genres).Synonyms == "");
        Assert.True(s.IsEnabled(Field.Tags));
    }

    public static void NormalizeClampsNegativeCaps()
    {
        var s = new MergeSettings();
        s.For(Field.Tags).PerSourceCap = -3;
        s.For(Field.Tags).TotalCap = -1;
        s.Normalize();
        Assert.Equal(0, s.For(Field.Tags).PerSourceCap);
        Assert.Equal(0, s.For(Field.Tags).TotalCap);
    }

    public static void CloneIsDeep()
    {
        var s = new MergeSettings();
        s.For(Field.Tags).CustomSources.Add("x");
        var c = s.Clone();
        c.For(Field.Tags).CustomSources.Add("y");
        c.For(Field.Tags).Enabled = false;
        Assert.Equal(1, s.For(Field.Tags).CustomSources.Count);
        Assert.True(s.IsEnabled(Field.Tags));
    }

    public static void ProblemsFlagBadNumbers()
    {
        var s = new MergeSettings();
        Assert.Equal(0, s.Problems().Count);
        s.TimeoutSeconds = 0;
        s.For(Field.Genres).TotalCap = 5000;
        Assert.Equal(2, s.Problems().Count);
    }

    public static void TimeoutIsClamped()
    {
        Assert.Equal(1, new MergeSettings { TimeoutSeconds = -5 }.Timeout());
        Assert.Equal(600, new MergeSettings { TimeoutSeconds = 99999 }.Timeout());
    }

    public static void OptionsCarryTheFieldSettings()
    {
        var s = new MergeSettings { LooseMatching = false };
        var fs = s.For(Field.Developers);
        fs.Mode = MergeMode.FirstNonEmpty; fs.PerSourceCap = 2; fs.TotalCap = 3; fs.Synonyms = "a=b";
        var o = s.OptionsFor(Field.Developers);
        Assert.Equal(MergeMode.FirstNonEmpty, o.Mode);
        Assert.Equal(2, o.PerSourceCap);
        Assert.Equal(3, o.TotalCap);
        Assert.False(o.Loose);
        Assert.True(o.Company);
        Assert.False(s.OptionsFor(Field.Genres).Company);
        Assert.Equal(1, o.Synonyms.Count);
    }
}
