using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using MetadataMerge.Core;

/// <summary>The extra parameters: agreement, sorting, case, length, exclusion, image rules, the breaker.</summary>
public static class ParamsTests
{
    private static SourceItems Src(string name, params string[] values)
    {
        return new SourceItems { SourceId = name, SourceName = name, Items = values.Select(Item.Name).ToList() };
    }

    private static string Run(MergeOptions o, params SourceItems[] sources) { return Fx.Join(MergeEngine.Merge(sources, o)); }

    // ---- list fields

    public static void A_value_must_be_given_by_enough_sources_when_agreement_is_asked()
    {
        var o = new MergeOptions { MinAgreeing = 2 };
        Assert.Equal("Co-op|RPG", Run(o, Src("a", "Co-op", "RPG", "Rare"), Src("b", "rpg", "Stealth", "co-op"), Src("c", "Co-Op")));
        Assert.Equal("Co-op", Run(new MergeOptions { MinAgreeing = 3 }, Src("a", "Co-op", "RPG"), Src("b", "rpg", "co-op"), Src("c", "Co-Op")));
    }

    public static void A_source_counts_once_even_when_it_repeats_a_value()
    {
        Assert.Equal("", Run(new MergeOptions { MinAgreeing = 2 }, Src("a", "x", "X", "x"), Src("b", "y")));
    }

    public static void Agreement_is_ignored_for_first_non_empty()
    {
        Assert.Equal("x|y", Run(new MergeOptions { MinAgreeing = 3, Mode = MergeMode.FirstNonEmpty }, Src("a", "x", "y")));
    }

    public static void Sorting_alphabetically_or_by_agreement()
    {
        var sources = new[] { Src("a", "Zeta", "Alpha", "Mid"), Src("b", "mid", "Beta"), Src("c", "MID", "Beta") };
        Assert.Equal("Zeta|Alpha|Mid|Beta", Run(new MergeOptions(), sources));
        Assert.Equal("Alpha|Beta|Mid|Zeta", Run(new MergeOptions { Sort = SortMode.Alphabetical }, sources));
        Assert.Equal("Mid|Beta|Zeta|Alpha", Run(new MergeOptions { Sort = SortMode.MostAgreed }, sources));
    }

    public static void Links_sort_by_their_name()
    {
        var a = new SourceItems { Items = new List<Item> { Item.Link("Zed", "https://z.example.com"), Item.Link("Abe", "https://a.example.com") } };
        var merged = MergeEngine.Merge(new[] { a }, new MergeOptions { Sort = SortMode.Alphabetical });
        Assert.Equal("Abe", merged[0].Label);
    }

    public static void The_cap_is_applied_after_sorting()
    {
        Assert.Equal("Alpha|Beta", Run(new MergeOptions { Sort = SortMode.Alphabetical, TotalCap = 2 }, Src("a", "Zeta", "Alpha", "Beta")));
    }

    public static void Case_can_be_forced()
    {
        Assert.Equal("open world|rpg", Run(new MergeOptions { Case = CaseMode.Lower }, Src("a", "Open World", "RPG")));
        Assert.Equal("OPEN WORLD|RPG", Run(new MergeOptions { Case = CaseMode.Upper }, Src("a", "Open World", "rpg")));
        Assert.Equal("Open World|RPG|iPhone|Co-op", Run(new MergeOptions { Case = CaseMode.Title }, Src("a", "open world", "RPG", "iPhone", "co-op")));
        Assert.Equal("Open World", Run(new MergeOptions { Case = CaseMode.Title }, Src("a", "open world", "Open World")));   // still one value
    }

    public static void Long_values_and_excluded_values_are_dropped()
    {
        Assert.Equal("short|ok", Run(new MergeOptions { MaxValueLength = 5 }, Src("a", "short", "much too long to be a tag", "ok")));
        var o = new MergeOptions { Exclude = new Regex("^(early access|.*bundle.*)$", RegexOptions.IgnoreCase) };
        Assert.Equal("Indie", Run(o, Src("a", "Early Access", "Indie", "Super Bundle Pack")));
    }

    public static void Settings_turn_into_merge_options_and_a_bad_expression_is_reported_not_thrown()
    {
        var s = new MergeSettings();
        var fs = s.For(Field.Tags);
        fs.MinAgreeing = 2; fs.Sort = SortMode.MostAgreed; fs.Case = CaseMode.Title; fs.MaxValueLength = 30; fs.ExcludeRegex = "^early";
        var o = s.OptionsFor(Field.Tags);
        Assert.Equal(2, o.MinAgreeing); Assert.Equal(SortMode.MostAgreed, o.Sort); Assert.Equal(CaseMode.Title, o.Case); Assert.Equal(30, o.MaxValueLength);
        Assert.True(o.Exclude != null && o.Exclude.IsMatch("Early Access"));
        fs.ExcludeRegex = "([";
        Assert.True(s.OptionsFor(Field.Tags).Exclude == null);
        Assert.True(s.Problems().Any(p => p.Contains("ExcludeRegex")));
    }

    public static void New_settings_are_clamped_and_checked()
    {
        var s = new MergeSettings { ImageProbeKb = 2, KeepDecisions = 0, BreakerPauseMinutes = 0 };
        Assert.Equal(3, s.Problems().Count);
        s = new MergeSettings();
        s.For(Field.Genres).MinAgreeing = -4; s.For(Field.Genres).MaxValueLength = -1; s.For(Field.Genres).ExcludeRegex = null;
        s.Normalize();
        Assert.Equal(0, s.For(Field.Genres).MinAgreeing); Assert.Equal(0, s.For(Field.Genres).MaxValueLength); Assert.Equal("", s.For(Field.Genres).ExcludeRegex);
        Assert.Equal(0, s.Problems().Count);
        s.ImageFor(Field.CoverImage).ExpectedRatio = "banana";
        Assert.True(s.Problems().Any(p => p.Contains("ExpectedRatio")));
        Assert.Equal(30, s.ImageTimeout());
        s.ImageProbeTimeoutSeconds = 7;
        Assert.Equal(7, s.ImageTimeout());
    }

    // ---- image rules

    private static ImageCandidate C(string name, int rank, int w, int h, ImageFormat f = ImageFormat.Jpeg, long bytes = 0)
    {
        return new ImageCandidate { SourceId = name, SourceName = name, Rank = rank, Item = Item.Image("http://x/" + name), Info = w > 0 ? new ImageInfo { Width = w, Height = h, Format = f, FileBytes = bytes } : null };
    }

    private static string Win(Field f, ImageSettings s, params ImageCandidate[] c)
    {
        var d = ImageChooser.Choose(f, "Game", c.ToList(), s);
        return d.Winner == null ? "(none)" : d.Winner.SourceName;
    }

    public static void The_expected_ratio_can_be_changed()
    {
        var s = ImageSettings.DefaultsFor(Field.CoverImage);
        Assert.Equal("(none)", Win(Field.CoverImage, s, C("square", 0, 1000, 1000)));
        s.ExpectedRatio = "1:1";
        Assert.Equal("square", Win(Field.CoverImage, s, C("square", 0, 1000, 1000)));
        s.ExpectedRatio = "nonsense";   // an invalid value falls back to the default of the field
        Assert.Equal("(none)", Win(Field.CoverImage, s, C("square", 0, 1000, 1000)));
    }

    public static void Ratios_are_read_in_several_notations()
    {
        double r;
        Assert.True(ImageSettings.TryParseRatio("2:3", out r)); Assert.Equal(2.0 / 3.0, r);
        Assert.True(ImageSettings.TryParseRatio("16/9", out r)); Assert.Equal(16.0 / 9.0, r);
        Assert.True(ImageSettings.TryParseRatio("0,67", out r)); Assert.Equal(0.67, r);
        Assert.True(ImageSettings.TryParseRatio(" 1.5 ", out r)); Assert.Equal(1.5, r);
        Assert.True(ImageSettings.TryParseRatio("600x900", out r)); Assert.Equal(600.0 / 900.0, r);
        Assert.False(ImageSettings.TryParseRatio("", out r)); Assert.False(ImageSettings.TryParseRatio("0:3", out r)); Assert.False(ImageSettings.TryParseRatio("-1", out r));
    }

    public static void A_loose_aspect_only_uses_wrong_proportions_when_nothing_has_the_right_ones()
    {
        var s = ImageSettings.DefaultsFor(Field.CoverImage); s.StrictAspect = false;
        Assert.Equal("land", Win(Field.CoverImage, s, C("land", 0, 1600, 900)));
        Assert.Equal("cover", Win(Field.CoverImage, s, C("land", 0, 1000, 600), C("cover", 1, 600, 900)));   // the right proportions come first even when smaller
    }

    public static void Pixel_count_and_file_size_limits()
    {
        var s = ImageSettings.DefaultsFor(Field.BackgroundImage);
        s.MinPixels = 3000000;
        Assert.Equal("big", Win(Field.BackgroundImage, s, C("small", 0, 1920, 1080), C("big", 1, 2560, 1440)));
        s.MinPixels = 0; s.MaxFileKb = 3000;
        Assert.Equal("light", Win(Field.BackgroundImage, s, C("heavy", 0, 3840, 2160, ImageFormat.Png, 9000000), C("light", 1, 3000, 1688, ImageFormat.Jpeg, 800000)));
        Assert.Equal("unknown", Win(Field.BackgroundImage, s, C("unknown", 0, 3840, 2160, ImageFormat.Png, 0)));   // size of the file not known: kept
        var d = ImageChooser.Choose(Field.BackgroundImage, "G", new List<ImageCandidate> { C("heavy", 0, 3840, 2160, ImageFormat.Png, 9000000) }, s);
        Assert.True(d.Candidates[0].Verdict.Contains("file of 8789 KB"), d.Candidates[0].Verdict);
    }

    public static void Above_the_cap_can_be_refused_or_ignored()
    {
        var s = ImageSettings.DefaultsFor(Field.BackgroundImage);
        s.AboveCap = AboveCapMode.Reject;
        Assert.Equal("(none)", Win(Field.BackgroundImage, s, C("huge", 0, 7680, 4320)));
        Assert.Equal("ok", Win(Field.BackgroundImage, s, C("huge", 0, 7680, 4320), C("ok", 1, 1920, 1080)));
        s.AboveCap = AboveCapMode.Largest;
        Assert.Equal("huge", Win(Field.BackgroundImage, s, C("huge", 0, 7680, 4320), C("ok", 1, 1920, 1080)));
    }

    public static void Unknown_sizes_can_be_refused()
    {
        var s = ImageSettings.DefaultsFor(Field.CoverImage);
        Assert.Equal("a", Win(Field.CoverImage, s, C("a", 0, 0, 0)));
        s.UnknownSize = UnknownSizeMode.Reject;
        Assert.Equal("(none)", Win(Field.CoverImage, s, C("a", 0, 0, 0)));
        Assert.Equal("m", Win(Field.CoverImage, s, C("a", 0, 0, 0), C("m", 1, 600, 900)));
    }

    public static void The_reader_reports_the_size_of_the_whole_file()
    {
        string png = System.IO.Path.Combine(ImageHeaderTests.DataDir, "png-120x80.png");
        var reader = new ImageReader(1000);
        Assert.Equal(new System.IO.FileInfo(png).Length, reader.Read(Item.Image(png), CancellationToken.None).FileBytes);
        var bytes = ImageHeaderTests.File_("png-60x90.png");
        Assert.Equal(bytes.Length, reader.Read(Item.Image(null, bytes, "x"), CancellationToken.None).FileBytes);
    }

    // ---- the breaker

    public static void A_source_is_paused_after_enough_failures_in_a_row_and_comes_back()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var health = new SourceHealth(() => now);
        Assert.False(health.Failure("s", 3, TimeSpan.FromMinutes(10)));
        Assert.False(health.Failure("s", 3, TimeSpan.FromMinutes(10)));
        Assert.False(health.IsPaused("s"));
        Assert.True(health.Failure("s", 3, TimeSpan.FromMinutes(10)));   // third: paused, reported once
        Assert.True(health.IsPaused("s"));
        Assert.False(health.Failure("s", 3, TimeSpan.FromMinutes(10)));  // already paused
        now = now.AddMinutes(11);
        Assert.False(health.IsPaused("s"));
        Assert.True(health.Failure("s", 3, TimeSpan.FromMinutes(10)));   // one more failure after the pause: paused again at once
        health.Success("s");
        Assert.False(health.IsPaused("s")); Assert.Equal(0, health.Failures("s"));
    }

    public static void A_success_resets_the_count_and_a_threshold_of_zero_turns_the_breaker_off()
    {
        var health = new SourceHealth();
        health.Failure("s", 3, TimeSpan.FromMinutes(1)); health.Failure("s", 3, TimeSpan.FromMinutes(1));
        health.Success("s");
        Assert.False(health.Failure("s", 3, TimeSpan.FromMinutes(1)));
        for (int i = 0; i < 20; i++) Assert.False(health.Failure("off", 0, TimeSpan.FromMinutes(1)));
        Assert.False(health.IsPaused("off"));
    }

    public static void A_source_that_keeps_failing_stops_being_asked_over_a_long_batch()
    {
        var bad = new FakeSource("Bad") { ThrowOnFetch = true };
        var good = new FakeSource("Good").With(Field.Genres, "g");
        var s = new MergeSettings { TimeoutSeconds = 5, BreakerFailures = 3, BreakerPauseMinutes = 10 };
        var health = new SourceHealth();
        var obs = new RecordingObserver();
        var list = new List<ISource> { bad, good };
        for (int game = 0; game < 8; game++)
            using (var m = new GameMerge(new GameRef { Id = Guid.NewGuid(), Name = "G" + game }, s, f => list, obs, null, health))
                Assert.Equal("g", Fx.Join(m.Get(Field.Genres, CancellationToken.None)));
        Assert.Equal(3, bad.Fetches);                       // asked for the first 3 games only
        Assert.Equal(8, good.Fetches);                      // the others are never held back
        Assert.Equal(1, obs.Paused.Count);
        Assert.Equal(5, obs.Reports.Count(r => r.SourceName == "Bad" && r.Outcome == Outcome.Skipped));
    }

    public static void The_breaker_can_be_switched_off()
    {
        var bad = new FakeSource("Bad") { ThrowOnFetch = true };
        var s = new MergeSettings { TimeoutSeconds = 5, BreakerFailures = 0 };
        var health = new SourceHealth();
        var list = new List<ISource> { bad };
        for (int game = 0; game < 6; game++)
            using (var m = new GameMerge(new GameRef { Id = Guid.NewGuid(), Name = "G" }, s, f => list, null, null, health)) m.Get(Field.Genres, CancellationToken.None);
        Assert.Equal(6, bad.Fetches);
    }
}
