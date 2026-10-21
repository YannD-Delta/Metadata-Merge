using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using MetadataMerge.Core;

/// <summary>The image fields through GameMerge: candidates from the sources, measured (fake reader), one winner, nothing else returned.</summary>
public static class ImageMergeTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static MergeSettings On(params Field[] fields)
    {
        var s = new MergeSettings { TimeoutSeconds = 5 };
        foreach (var f in fields) s.For(f).Enabled = true;
        return s;
    }

    private static GameMerge Merge(MergeSettings s, FakeReader reader, IMergeObserver obs, params FakeSource[] sources)
    {
        var list = sources.Cast<ISource>().ToList();
        return new GameMerge(new GameRef { Id = Guid.NewGuid(), Name = "Test Game" }, s, f => list, obs, reader);
    }

    public static void The_image_fields_are_off_by_default()
    {
        var s = new MergeSettings();
        Assert.False(s.IsEnabled(Field.CoverImage)); Assert.False(s.IsEnabled(Field.BackgroundImage)); Assert.False(s.IsEnabled(Field.Icon));
        var a = new FakeSource("A").WithImage(Field.CoverImage, "http://a/c");
        using (var m = Merge(s, new FakeReader(), null, a))
            Assert.Equal(0, m.Get(Field.CoverImage, None).Count);
        Assert.Equal(0, a.Fetches);   // not even asked
    }

    public static void The_best_candidate_is_returned_alone_with_the_sources_own_object()
    {
        var a = new FakeSource("A").WithImage(Field.CoverImage, "http://a/c");
        var b = new FakeSource("B").WithImage(Field.CoverImage, "http://b/c");
        var c = new FakeSource("C").WithImage(Field.CoverImage, "http://c/c");
        var reader = new FakeReader().Size("http://a/c", 600, 900).Size("http://b/c", 1000, 1500).Size("http://c/c", 3000, 4500);
        var obs = new RecordingObserver();
        using (var m = Merge(On(Field.CoverImage), reader, obs, a, b, c))
        {
            var result = m.Get(Field.CoverImage, None);
            Assert.Equal(1, result.Count);
            Assert.Equal("http://b/c", result[0].Value);
            Assert.Equal("tag:http://b/c", (string)result[0].Tag);
        }
        var d = obs.Images.Single();
        Assert.Equal("B", d.Winner.SourceName);
        Assert.Equal(3, d.Candidates.Count);
    }

    public static void Each_source_is_asked_once_and_each_image_measured_once_per_game()
    {
        var a = new FakeSource("A").WithImage(Field.CoverImage, "http://a/c").WithImage(Field.BackgroundImage, "http://a/b");
        var b = new FakeSource("B").WithImage(Field.CoverImage, "http://b/c").WithImage(Field.BackgroundImage, "http://b/b");
        var reader = new FakeReader().Size("http://a/c", 600, 900).Size("http://b/c", 800, 1200).Size("http://a/b", 1920, 1080).Size("http://b/b", 3840, 2160);
        var obs = new RecordingObserver();
        using (var m = Merge(On(Field.CoverImage, Field.BackgroundImage), reader, obs, a, b))
        {
            m.Get(Field.CoverImage, None); m.Get(Field.BackgroundImage, None); m.Get(Field.CoverImage, None); m.Get(Field.CoverImage, None);
        }
        Assert.Equal(1, a.Opens); Assert.Equal(1, a.Fetches);
        Assert.Equal(4, reader.Reads.Count);
        Assert.Equal(2, obs.Images.Count);
    }

    public static void The_candidates_are_measured_in_parallel()
    {
        var sources = Enumerable.Range(1, 4).Select(i => new FakeSource("S" + i).WithImage(Field.BackgroundImage, "http://s/" + i)).ToArray();
        var reader = new FakeReader();
        for (int i = 1; i <= 4; i++) { reader.Size("http://s/" + i, 1920, 1080); reader.DelaysMs["http://s/" + i] = 500; }
        var clock = Stopwatch.StartNew();
        using (var m = Merge(On(Field.BackgroundImage), reader, null, sources))
            Assert.Equal(1, m.Get(Field.BackgroundImage, None).Count);
        Assert.True(clock.ElapsedMilliseconds < 1500, "took " + clock.ElapsedMilliseconds);
    }

    public static void A_candidate_that_cannot_be_read_never_blocks_the_others()
    {
        var a = new FakeSource("A").WithImage(Field.Icon, "http://a/i");   // a web page, not an image
        var b = new FakeSource("B").WithImage(Field.Icon, "http://b/i");
        var reader = new FakeReader().Size("http://b/i", 256, 256);
        reader.Fails.Add("http://a/i");
        var obs = new RecordingObserver();
        using (var m = Merge(On(Field.Icon), reader, obs, a, b))
            Assert.Equal("http://b/i", m.Get(Field.Icon, None).Single().Value);
        Assert.True(obs.Images.Single().Candidates[0].Verdict.Contains("size unknown"), obs.Images.Single().Candidates[0].Verdict);
    }

    public static void A_candidate_that_is_too_slow_is_left_unmeasured_and_the_limit_is_respected()
    {
        var a = new FakeSource("A").WithImage(Field.CoverImage, "http://a/c");
        var b = new FakeSource("B").WithImage(Field.CoverImage, "http://b/c");
        var reader = new FakeReader().Size("http://a/c", 600, 900).Size("http://b/c", 800, 1200);
        reader.DelaysMs["http://b/c"] = 30000;
        var s = On(Field.CoverImage); s.TimeoutSeconds = 1;
        var clock = Stopwatch.StartNew();
        using (var m = Merge(s, reader, null, a, b))
            Assert.Equal("http://a/c", m.Get(Field.CoverImage, None).Single().Value);
        Assert.True(clock.ElapsedMilliseconds < 5000, "took " + clock.ElapsedMilliseconds);
    }

    public static void When_no_size_can_be_read_the_first_source_wins()
    {
        var a = new FakeSource("A").WithImage(Field.CoverImage, "http://a/c");
        var b = new FakeSource("B").WithImage(Field.CoverImage, "http://b/c");
        using (var m = Merge(On(Field.CoverImage), new FakeReader(), null, a, b))
            Assert.Equal("http://a/c", m.Get(Field.CoverImage, None).Single().Value);
    }

    public static void Sources_without_an_image_and_a_failing_source_are_just_skipped()
    {
        var a = new FakeSource("A");
        var bad = new FakeSource("Bad") { ThrowOnFetch = true };
        var c = new FakeSource("C").WithImage(Field.CoverImage, "http://c/c");
        var reader = new FakeReader().Size("http://c/c", 600, 900);
        using (var m = Merge(On(Field.CoverImage), reader, null, a, bad, c))
            Assert.Equal("http://c/c", m.Get(Field.CoverImage, None).Single().Value);
    }

    public static void Nothing_acceptable_gives_nothing_so_playnite_goes_on_to_its_next_source()
    {
        var a = new FakeSource("A").WithImage(Field.CoverImage, "http://a/c");
        var reader = new FakeReader().Size("http://a/c", 1600, 900);   // landscape: not a cover
        using (var m = Merge(On(Field.CoverImage), reader, null, a))
            Assert.Equal(0, m.Get(Field.CoverImage, None).Count);
    }

    public static void The_size_rules_come_from_the_settings_of_the_field()
    {
        var a = new FakeSource("A").WithImage(Field.BackgroundImage, "http://a/b");
        var b = new FakeSource("B").WithImage(Field.BackgroundImage, "http://b/b");
        var reader = new FakeReader().Size("http://a/b", 3840, 2160).Size("http://b/b", 7680, 4320);
        var s = On(Field.BackgroundImage);
        using (var m = Merge(s, reader, null, a, b)) Assert.Equal("http://a/b", m.Get(Field.BackgroundImage, None).Single().Value);
        s.ImageFor(Field.BackgroundImage).MaxWidth = 0; s.ImageFor(Field.BackgroundImage).MaxHeight = 0;
        using (var m = Merge(s, reader, null, a, b)) Assert.Equal("http://b/b", m.Get(Field.BackgroundImage, None).Single().Value);
    }

    public static void List_fields_and_image_fields_share_the_one_round_per_source()
    {
        var a = new FakeSource("A").With(Field.Genres, "g").WithImage(Field.CoverImage, "http://a/c");
        var reader = new FakeReader().Size("http://a/c", 600, 900);
        using (var m = Merge(On(Field.CoverImage), reader, null, a))
        {
            Assert.Equal("g", Fx.Join(m.Get(Field.Genres, None)));
            Assert.Equal(1, m.Get(Field.CoverImage, None).Count);
        }
        Assert.Equal(1, a.Fetches);
    }

    public static void Image_settings_have_defaults_per_field_and_survive_clone_and_normalize()
    {
        var s = new MergeSettings();
        Assert.Equal(1000, s.ImageFor(Field.CoverImage).MaxWidth);
        Assert.Equal(3840, s.ImageFor(Field.BackgroundImage).MaxWidth);
        Assert.Equal(512, s.ImageFor(Field.Icon).MaxWidth);
        Assert.True(s.For(Field.Genres).Image == null);
        var c = s.Clone();
        c.ImageFor(Field.CoverImage).MaxWidth = 700;
        Assert.Equal(1000, s.ImageFor(Field.CoverImage).MaxWidth);
        s.For(Field.Icon).Image = null;
        s.For(Field.Genres).Image = ImageSettings.DefaultsFor(Field.Icon);
        s.Normalize();
        Assert.Equal(512, s.ImageFor(Field.Icon).MaxWidth);
        Assert.True(s.For(Field.Genres).Image == null);
    }
}
