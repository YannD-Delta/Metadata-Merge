using System;
using System.Collections.Generic;
using System.Linq;
using MetadataMerge.Core;

public static class ImageChooserTests
{
    private static ImageCandidate C(string name, int rank, int w, int h, ImageFormat f = ImageFormat.Jpeg)
    {
        return new ImageCandidate { SourceId = name, SourceName = name, Rank = rank, Item = Item.Image("http://x/" + name), Info = w > 0 ? new ImageInfo { Width = w, Height = h, Format = f } : null };
    }

    private static string Win(Field f, ImageSettings s, params ImageCandidate[] c)
    {
        var d = ImageChooser.Choose(f, "Game", c.ToList(), s);
        return d.Winner == null ? "(none)" : d.Winner.SourceName;
    }

    private static ImageSettings Cover() { return ImageSettings.DefaultsFor(Field.CoverImage); }
    private static ImageSettings Back() { return ImageSettings.DefaultsFor(Field.BackgroundImage); }

    public static void The_largest_image_within_the_cap_wins()
    {
        Assert.Equal("b", Win(Field.CoverImage, Cover(), C("a", 0, 600, 900), C("b", 1, 800, 1200), C("c", 2, 400, 600)));
    }

    public static void The_wrong_aspect_is_rejected_before_the_size_is_compared()
    {
        // a huge landscape picture is not a cover
        Assert.Equal("a", Win(Field.CoverImage, Cover(), C("big", 0, 1600, 900), C("a", 1, 600, 900)));
        Assert.Equal("a", Win(Field.BackgroundImage, Back(), C("a", 0, 1920, 1080), C("square", 1, 3000, 3000)));
        Assert.Equal("a", Win(Field.Icon, ImageSettings.DefaultsFor(Field.Icon), C("a", 0, 128, 128), C("wide", 1, 500, 200)));
    }

    public static void The_aspect_tolerance_is_a_setting()
    {
        var s = Back();   // 16:9 = 1.78; 1.5 is 15.6 % away
        Assert.Equal("(none)", Win(Field.BackgroundImage, s, C("a", 0, 1800, 1200)));
        s.AspectTolerancePercent = 20;
        Assert.Equal("a", Win(Field.BackgroundImage, s, C("a", 0, 1800, 1200)));
        s.AspectTolerancePercent = 15;
        Assert.Equal("a", Win(Field.BackgroundImage, s, C("a", 0, 1920, 1200)));   // 1.6 is 10 % away
    }

    public static void Thumbnails_below_the_minimum_are_ignored()
    {
        Assert.Equal("b", Win(Field.CoverImage, Cover(), C("a", 0, 200, 300), C("b", 1, 400, 600)));
        Assert.Equal("(none)", Win(Field.CoverImage, Cover(), C("a", 0, 200, 300)));
    }

    public static void Images_above_the_cap_lose_to_any_image_within_it()
    {
        // no 8K background while a 1080p one exists
        Assert.Equal("small", Win(Field.BackgroundImage, Back(), C("huge", 0, 7680, 4320), C("small", 1, 1920, 1080)));
        Assert.Equal("a", Win(Field.CoverImage, Cover(), C("a", 0, 600, 900), C("b", 1, 2000, 3000)));
    }

    public static void When_everything_is_above_the_cap_the_smallest_is_used()
    {
        Assert.Equal("b", Win(Field.BackgroundImage, Back(), C("a", 0, 7680, 4320), C("b", 1, 5120, 2880), C("c", 2, 6400, 3600)));
    }

    public static void A_cap_of_zero_means_no_cap()
    {
        var s = Back(); s.MaxWidth = 0; s.MaxHeight = 0;
        Assert.Equal("huge", Win(Field.BackgroundImage, s, C("huge", 0, 7680, 4320), C("small", 1, 1920, 1080)));
    }

    public static void An_image_exactly_at_the_cap_is_within_it()
    {
        Assert.Equal("a", Win(Field.BackgroundImage, Back(), C("a", 0, 3840, 2160), C("small", 1, 1920, 1080)));
    }

    public static void Jpeg_is_preferred_to_png_of_the_same_size()
    {
        Assert.Equal("jpg", Win(Field.BackgroundImage, Back(), C("png", 0, 3840, 2160, ImageFormat.Png), C("jpg", 1, 3840, 2160, ImageFormat.Jpeg)));
        Assert.Equal("jpg", Win(Field.BackgroundImage, Back(), C("webp", 0, 3840, 2160, ImageFormat.WebP), C("png", 1, 3840, 2160, ImageFormat.Png), C("jpg", 2, 3840, 2160, ImageFormat.Jpeg)));
    }

    public static void Jpeg_wins_when_the_sizes_are_comparable_but_not_when_it_is_much_smaller()
    {
        // 3840x2160 png against a 3500x1969 jpeg (17 % fewer pixels): comparable limit is 15 %
        Assert.Equal("png", Win(Field.BackgroundImage, Back(), C("png", 0, 3840, 2160, ImageFormat.Png), C("jpg", 1, 3500, 1969)));
        Assert.Equal("jpg", Win(Field.BackgroundImage, Back(), C("png", 0, 3840, 2160, ImageFormat.Png), C("jpg", 1, 3700, 2081)));
        var s = Back(); s.ComparablePercent = 0;
        Assert.Equal("png", Win(Field.BackgroundImage, s, C("png", 0, 3840, 2160, ImageFormat.Png), C("jpg", 1, 3700, 2081)));
    }

    public static void The_format_preference_is_a_setting()
    {
        var s = Back(); s.FormatPreference = "webp, png";
        Assert.Equal("webp", Win(Field.BackgroundImage, s, C("jpg", 0, 3840, 2160), C("webp", 1, 3840, 2160, ImageFormat.WebP), C("png", 2, 3840, 2160, ImageFormat.Png)));
        s.FormatPreference = "";
        Assert.Equal("jpg", Win(Field.BackgroundImage, s, C("jpg", 0, 3840, 2160), C("png", 1, 3840, 2160, ImageFormat.Png)));   // none listed: source order decides
    }

    public static void The_source_order_breaks_the_ties()
    {
        Assert.Equal("first", Win(Field.CoverImage, Cover(), C("first", 0, 600, 900), C("second", 1, 600, 900)));
        Assert.Equal("first", Win(Field.CoverImage, Cover(), C("second", 1, 600, 900), C("first", 0, 600, 900)));
    }

    public static void When_no_size_can_be_read_the_first_candidate_is_used()
    {
        Assert.Equal("a", Win(Field.CoverImage, Cover(), C("b", 1, 0, 0), C("a", 0, 0, 0)));
    }

    public static void A_measured_acceptable_image_beats_unknown_ones()
    {
        Assert.Equal("m", Win(Field.CoverImage, Cover(), C("u", 0, 0, 0), C("m", 1, 600, 900)));
    }

    public static void When_every_measured_image_is_rejected_the_first_unknown_one_is_tried()
    {
        var d = ImageChooser.Choose(Field.CoverImage, "G", new List<ImageCandidate> { C("bad", 0, 1600, 900), C("u2", 2, 0, 0), C("u1", 1, 0, 0) }, Cover());
        Assert.Equal("u1", d.Winner.SourceName);
        Assert.True(d.Reason.Contains("rejected"), d.Reason);
        Assert.Equal("(none)", Win(Field.CoverImage, Cover(), C("bad", 0, 1600, 900)));
    }

    public static void No_candidate_means_no_winner()
    {
        var d = ImageChooser.Choose(Field.Icon, "G", new List<ImageCandidate>(), ImageSettings.DefaultsFor(Field.Icon));
        Assert.True(d.Winner == null);
        Assert.True(d.Reason.Length > 0);
    }

    public static void Every_candidate_gets_a_verdict_and_the_decision_says_why()
    {
        var c = new[] { C("huge", 0, 7680, 4320), C("wide", 1, 3000, 3000), C("ok", 2, 1920, 1080), C("tiny", 3, 300, 100), C("none", 4, 0, 0) };
        var d = ImageChooser.Choose(Field.BackgroundImage, "Game", c.ToList(), Back());
        Assert.Equal("ok", d.Winner.SourceName);
        Assert.True(c[0].Verdict.Contains("above the cap"), c[0].Verdict);
        Assert.True(c[1].Verdict.Contains("ratio"), c[1].Verdict);
        Assert.Equal("chosen", c[2].Verdict);
        Assert.True(c[3].Verdict.Contains("smaller than"), c[3].Verdict);
        Assert.True(c[4].Verdict.Contains("unknown"), c[4].Verdict);
        string text = d.ToString();
        Assert.True(text.Contains("Game BackgroundImage: ok 1920x1080 JPEG") && text.Contains("largest within the cap"), text);
    }

    public static void Defaults_follow_the_owners_rules()
    {
        var cover = ImageSettings.DefaultsFor(Field.CoverImage);
        Assert.True(cover.MaxWidth == 1000 && cover.MaxHeight == 1500 && cover.MinWidth == 300);
        var back = ImageSettings.DefaultsFor(Field.BackgroundImage);
        Assert.True(back.MaxWidth == 3840 && back.MaxHeight == 2160);
        Assert.Equal("jpeg,png,webp,gif,ico,bmp", back.FormatPreference);
        Assert.Equal(2.0 / 3.0, Fields.ExpectedAspect(Field.CoverImage));
        Assert.Equal(1.0, Fields.ExpectedAspect(Field.Icon));
    }

    public static void Clamp_keeps_numbers_sane()
    {
        var s = new ImageSettings { MinWidth = -5, MaxWidth = 99999, AspectTolerancePercent = 500, ComparablePercent = -1, FormatPreference = null };
        s.Clamp();
        Assert.True(s.MinWidth == 0 && s.MaxWidth == 20000 && s.AspectTolerancePercent == 100 && s.ComparablePercent == 0 && s.FormatPreference == "");
    }
}
