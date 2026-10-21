using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using MetadataMerge;
using MetadataMerge.Core;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

/// <summary>The plugin class end to end: real SDK base classes (metadata plugin, provider, library plugin), a fake API around them, no Playnite process.</summary>
public static class PluginTests
{
    private static readonly string Self = MetadataMergePlugin.PluginId.ToString("D");

    private sealed class Setup : IDisposable
    {
        public FakeApi.Rig Rig = new FakeApi.Rig();
        public FakeMeta A = new FakeMeta("Alpha");
        public FakeMeta B = new FakeMeta("Beta");
        public FakeLibrary Library = new FakeLibrary();
        public MetadataMergePlugin Plugin;
        public Game Game;

        public Setup(Action<Setup> arrange = null, string config = null, bool create = true)
        {
            UiHost.Run(() => { });   // starts the host application: the strings come from its resources, like in Playnite
            Rig.Plugins.Add(A); Rig.Plugins.Add(B); Rig.Plugins.Add(Library);
            Game = new Game("Test Game") { PluginId = Library.Id };
            if (arrange != null) arrange(this);
            if (config != null) Rig.WriteConfig(config);
            if (create) Plugin = new MetadataMergePlugin(Rig.Api());
        }

        public OnDemandMetadataProvider Provider() { return Plugin.GetMetadataProvider(new MetadataRequestOptions(Game, false)); }
        public string Id(FakeMeta m) { return m.Id.ToString("D"); }
        public void Dispose() { if (Plugin != null) Plugin.Log.Dispose(); Rig.Cleanup(); }
    }

    private static string Config(params string[] fieldAndSources)
    {
        // "Genre:self,a,b" ...
        var parts = fieldAndSources.Select(x =>
        {
            int colon = x.IndexOf(':');
            var ids = x.Substring(colon + 1).Split(',').Where(i => i.Length > 0).Select(i => "\"" + i + "\"");
            return "\"" + x.Substring(0, colon) + "\": { \"Import\": true, \"Sources\": [" + string.Join(",", ids) + "] }";
        });
        return "{ \"Language\": \"en_US\", \"MetadataSettings\": { \"SkipExistingValues\": false, " + string.Join(", ", parts) + " } }";
    }

    private static string Names(IEnumerable<MetadataProperty> props)
    {
        return props == null ? "(null)" : string.Join("|", props.OfType<MetadataNameProperty>().Select(p => p.Name));
    }

    private static readonly string Store = PlayniteConfig.StoreId;

    public static void The_extension_is_a_metadata_plugin_offering_every_mergeable_field()
    {
        using (var s = new Setup())
        {
            Assert.Equal("Metadata Merge", s.Plugin.Name);
            Assert.Equal(13, s.Plugin.SupportedFields.Count);
            foreach (var f in new[] { MetadataField.Genres, MetadataField.Developers, MetadataField.Publishers, MetadataField.Features, MetadataField.Tags, MetadataField.Links, MetadataField.Series, MetadataField.Platform, MetadataField.Region, MetadataField.AgeRating, MetadataField.CoverImage, MetadataField.BackgroundImage, MetadataField.Icon })
                Assert.True(s.Plugin.SupportedFields.Contains(f), f.ToString());
            Assert.False(s.Plugin.SupportedFields.Contains(MetadataField.Description));
            Assert.False(s.Plugin.SupportedFields.Contains(MetadataField.Name));
            Assert.True(s.Plugin.Properties.HasSettings);
        }
    }

    public static void The_available_fields_are_the_enabled_ones()
    {
        using (var s = new Setup())
        {
            using (var p = s.Provider())
            {
                Assert.Equal(8, p.AvailableFields.Count);
                Assert.False(p.AvailableFields.Contains(MetadataField.Region));
                s.Plugin.Settings.For(Field.Tags).Enabled = false;
                s.Plugin.Settings.For(Field.Regions).Enabled = true;
                Assert.False(p.AvailableFields.Contains(MetadataField.Tags));
                Assert.True(p.AvailableFields.Contains(MetadataField.Region));
            }
        }
    }

    public static void The_provider_merges_the_sources_that_follow_it_in_the_playnite_list()
    {
        using (var s = new Setup(x =>
        {
            x.A.Names(MetadataField.Genres, "Action", "RPG").Names(MetadataField.Tags, "ta");
            x.B.Names(MetadataField.Genres, "rpg", "Indie");
            x.Library.Store.Genres = new HashSet<MetadataProperty> { new MetadataNameProperty("Shooter"), new MetadataNameProperty("action") };
        }, null, false))
        {
            s.Rig.WriteConfig(Config("Genre:" + Self + "," + s.Id(s.A) + "," + s.Id(s.B) + "," + Store, "Tag:" + Self + "," + s.Id(s.A)));
            s.Plugin = new MetadataMergePlugin(s.Rig.Api());
            using (var p = s.Provider())
            {
                Assert.Equal("Action|RPG|Indie|Shooter", Names(p.GetGenres(new GetMetadataFieldArgs())));
                Assert.Equal("ta", Names(p.GetTags(new GetMetadataFieldArgs())));
            }
        }
    }

    public static void Each_plugin_is_asked_once_per_game_for_all_the_fields_it_is_listed_for()
    {
        using (var s = new Setup(x =>
        {
            x.A.Names(MetadataField.Genres, "g").Names(MetadataField.Tags, "t").Names(MetadataField.Developers, "d");
        }, null, false))
        {
            string list = Self + "," + s.Id(s.A);
            s.Rig.WriteConfig(Config("Genre:" + list, "Tag:" + list, "Developer:" + list, "Feature:" + list, "Links:" + list));
            s.Plugin = new MetadataMergePlugin(s.Rig.Api());
            using (var p = s.Provider())
            {
                var args = new GetMetadataFieldArgs();
                p.GetGenres(args); p.GetTags(args); p.GetDevelopers(args); p.GetFeatures(args); p.GetLinks(args); p.GetGenres(args);
            }
            Assert.Equal(1, s.A.Created);
            Assert.Equal(1, s.A.Disposed);
            Assert.Equal(1, s.A.Calls.Count(c => c == "Genres"));
            Assert.Equal(1, s.A.Calls.Count(c => c == "Tags"));
            Assert.True(s.A.LastRequestWasBackground, "sources are asked in background mode: they never pop a dialog from a worker thread");
        }
    }

    public static void Each_field_has_its_own_source_list()
    {
        using (var s = new Setup(x =>
        {
            x.A.Names(MetadataField.Tags, "ta").Names(MetadataField.Developers, "da");
            x.B.Names(MetadataField.Tags, "tb").Names(MetadataField.Developers, "db");
        }, null, false))
        {
            s.Rig.WriteConfig(Config("Tag:" + Self + "," + s.Id(s.A), "Developer:" + Self + "," + s.Id(s.B) + "," + s.Id(s.A)));
            s.Plugin = new MetadataMergePlugin(s.Rig.Api());
            using (var p = s.Provider())
            {
                Assert.Equal("ta", Names(p.GetTags(new GetMetadataFieldArgs())));
                Assert.Equal("db|da", Names(p.GetDevelopers(new GetMetadataFieldArgs())));
            }
        }
    }

    public static void A_source_that_fails_on_one_field_keeps_its_other_fields()
    {
        using (var s = new Setup(x =>
        {
            x.A.Names(MetadataField.Genres, "g").Names(MetadataField.Tags, "t");
            x.A.Throws.Add(MetadataField.Genres);
        }, null, false))
        {
            string list = Self + "," + s.Id(s.A);
            s.Rig.WriteConfig(Config("Genre:" + list, "Tag:" + list));
            s.Plugin = new MetadataMergePlugin(s.Rig.Api());
            using (var p = s.Provider())
            {
                Assert.Equal("(null)", Names(p.GetGenres(new GetMetadataFieldArgs())));
                Assert.Equal("t", Names(p.GetTags(new GetMetadataFieldArgs())));
            }
            Assert.True(s.Plugin.Log.Report().Contains("Alpha"), s.Plugin.Log.Report());
            Assert.True(s.Plugin.Log.Report().Contains("exploded"), s.Plugin.Log.Report());
        }
    }

    public static void A_broken_a_slow_and_a_good_source_together_give_the_good_one_and_a_notification()
    {
        int old = MetadataMergePlugin.NotifyDelayMs;
        MetadataMergePlugin.NotifyDelayMs = 150;
        try
        {
            using (var s = new Setup(x =>
            {
                x.A.ThrowOnCreate = true;
                x.B.DelayMs = 30000; x.B.Names(MetadataField.Genres, "slow");
                x.Library.Store.Genres = new HashSet<MetadataProperty> { new MetadataNameProperty("Store genre") };
            }, null, false))
            {
                s.Rig.WriteConfig(Config("Genre:" + Self + "," + s.Id(s.A) + "," + s.Id(s.B) + "," + Store));
                s.Plugin = new MetadataMergePlugin(s.Rig.Api());
                s.Plugin.Settings.TimeoutSeconds = 1;
                var clock = Stopwatch.StartNew();
                using (var p = s.Provider())
                    Assert.Equal("Store genre", Names(p.GetGenres(new GetMetadataFieldArgs())));
                Assert.True(clock.ElapsedMilliseconds < 6000, "took " + clock.ElapsedMilliseconds + " ms");
                for (int i = 0; i < 50 && s.Rig.Notifications.Count == 0; i++) Thread.Sleep(100);
                Assert.Equal(1, s.Rig.Notifications.Count);
                var n = s.Rig.Notifications[0];
                Assert.Equal(NotificationType.Error, n.Type);
                Assert.True(n.Text.Contains("Alpha") && n.Text.Contains("Beta"), n.Text);
                n.ActivationAction();
                Assert.Equal(MetadataMergePlugin.PluginId, s.Rig.OpenedSettings.Single());
            }
        }
        finally { MetadataMergePlugin.NotifyDelayMs = old; }
    }

    public static void Notifications_can_be_switched_off()
    {
        int old = MetadataMergePlugin.NotifyDelayMs;
        MetadataMergePlugin.NotifyDelayMs = 50;
        try
        {
            using (var s = new Setup(x => x.A.ThrowOnCreate = true, null, false))
            {
                s.Rig.WriteConfig(Config("Genre:" + Self + "," + s.Id(s.A)));
                s.Plugin = new MetadataMergePlugin(s.Rig.Api());
                s.Plugin.Settings.NotifyErrors = false;
                using (var p = s.Provider()) p.GetGenres(new GetMetadataFieldArgs());
                Thread.Sleep(400);
                Assert.Equal(0, s.Rig.Notifications.Count);
                Assert.Equal(1, s.Plugin.Log.ProblemCount);
            }
        }
        finally { MetadataMergePlugin.NotifyDelayMs = old; }
    }

    public static void A_source_in_the_list_that_is_not_installed_is_skipped_and_reported_once()
    {
        using (var s = new Setup(x => x.A.Names(MetadataField.Genres, "g"), null, false))
        {
            string ghost = Guid.NewGuid().ToString("D");
            s.Rig.WriteConfig(Config("Genre:" + Self + "," + ghost + "," + s.Id(s.A)));
            s.Plugin = new MetadataMergePlugin(s.Rig.Api());
            using (var p = s.Provider())
            {
                Assert.Equal("g", Names(p.GetGenres(new GetMetadataFieldArgs())));
                p.GetGenres(new GetMetadataFieldArgs());
            }
            Assert.True(s.Plugin.Log.Report().Contains("Not installed: " + ghost), s.Plugin.Log.Report());
        }
    }

    public static void The_extension_never_asks_itself()
    {
        using (var s = new Setup(null, null, false))
        {
            s.Rig.WriteConfig(Config("Genre:" + Self));
            s.Plugin = new MetadataMergePlugin(s.Rig.Api());
            using (var p = s.Provider())
                Assert.Equal("(null)", Names(p.GetGenres(new GetMetadataFieldArgs())));
        }
    }

    public static void Without_a_config_file_the_own_list_is_used()
    {
        using (var s = new Setup(x => { x.A.Names(MetadataField.Genres, "ga"); x.B.Names(MetadataField.Genres, "gb"); }, null, true))
        {
            var fs = s.Plugin.Settings.For(Field.Genres);
            fs.Sources = SourceMode.Custom;
            fs.CustomSources = new List<string> { s.Id(s.B), s.Id(s.A) };
            using (var p = s.Provider())
                Assert.Equal("gb|ga", Names(p.GetGenres(new GetMetadataFieldArgs())));
        }
    }

    public static void The_config_is_read_again_when_playnite_saves_a_new_list()
    {
        using (var s = new Setup(x => { x.A.Names(MetadataField.Genres, "ga"); x.B.Names(MetadataField.Genres, "gb"); }, null, false))
        {
            s.Rig.WriteConfig(Config("Genre:" + Self + "," + s.Id(s.A)));
            s.Plugin = new MetadataMergePlugin(s.Rig.Api());
            using (var p = s.Provider()) Assert.Equal("ga", Names(p.GetGenres(new GetMetadataFieldArgs())));
            s.Rig.WriteConfig(Config("Genre:" + Self + "," + s.Id(s.B) + "," + s.Id(s.A)));
            using (var p = s.Provider()) Assert.Equal("gb|ga", Names(p.GetGenres(new GetMetadataFieldArgs())));
        }
    }

    public static void The_official_store_of_a_game_without_library_gives_nothing_and_breaks_nothing()
    {
        using (var s = new Setup(x =>
        {
            x.A.Names(MetadataField.Genres, "ga");
            x.Library.Store.Genres = new HashSet<MetadataProperty> { new MetadataNameProperty("store") };
            x.Game.PluginId = Guid.NewGuid();
        }, null, false))
        {
            s.Rig.WriteConfig(Config("Genre:" + Self + "," + Store + "," + s.Id(s.A)));
            s.Plugin = new MetadataMergePlugin(s.Rig.Api());
            using (var p = s.Provider())
                Assert.Equal("ga", Names(p.GetGenres(new GetMetadataFieldArgs())));
            Assert.Equal(0, s.Library.Downloads);
        }
    }

    public static void The_official_store_is_asked_once_for_all_fields()
    {
        using (var s = new Setup(x =>
        {
            x.Library.Store.Genres = new HashSet<MetadataProperty> { new MetadataNameProperty("g") };
            x.Library.Store.Developers = new HashSet<MetadataProperty> { new MetadataNameProperty("d") };
            x.Library.Store.Links = new List<Link> { new Link("Store page", "https://store.example.com/1") };
        }, null, false))
        {
            string list = Self + "," + Store;
            s.Rig.WriteConfig(Config("Genre:" + list, "Developer:" + list, "Links:" + list));
            s.Plugin = new MetadataMergePlugin(s.Rig.Api());
            using (var p = s.Provider())
            {
                Assert.Equal("g", Names(p.GetGenres(new GetMetadataFieldArgs())));
                Assert.Equal("d", Names(p.GetDevelopers(new GetMetadataFieldArgs())));
                Assert.Equal("Store page", p.GetLinks(new GetMetadataFieldArgs()).Single().Name);
            }
            Assert.Equal(1, s.Library.Downloads);
        }
    }

    public static void Links_are_merged_and_deduped_by_address()
    {
        using (var s = new Setup(x =>
        {
            x.A.Links = new List<Link> { new Link("Wiki", "https://wiki.example.com/Game"), new Link("Site", "http://game.example.com/") };
            x.B.Links = new List<Link> { new Link("PCGamingWiki", "http://www.wiki.example.com/Game/"), new Link("Steam", "https://store.example.com/app/1") };
        }, null, false))
        {
            s.Rig.WriteConfig(Config("Links:" + Self + "," + s.Id(s.A) + "," + s.Id(s.B)));
            s.Plugin = new MetadataMergePlugin(s.Rig.Api());
            using (var p = s.Provider())
            {
                var links = p.GetLinks(new GetMetadataFieldArgs()).ToList();
                Assert.Equal(3, links.Count);
                Assert.Equal("Wiki", links[0].Name);
                Assert.Equal("Steam", links[2].Name);
            }
        }
    }

    public static void Ids_and_specs_come_back_as_the_same_kind_of_property()
    {
        using (var s = new Setup(x =>
        {
            x.A.Props[MetadataField.Platform] = new List<MetadataProperty> { new MetadataSpecProperty("pc_windows") };
            x.B.Props[MetadataField.Platform] = new List<MetadataProperty> { new MetadataSpecProperty("PC_Windows"), new MetadataSpecProperty("xbox360") };
        }, null, false))
        {
            s.Rig.WriteConfig(Config("Platform:" + Self + "," + s.Id(s.A) + "," + s.Id(s.B)));
            s.Plugin = new MetadataMergePlugin(s.Rig.Api());
            using (var p = s.Provider())
            {
                var props = p.GetPlatforms(new GetMetadataFieldArgs()).ToList();
                Assert.Equal(2, props.Count);
                Assert.True(props.All(x => x is MetadataSpecProperty));
                Assert.Equal("pc_windows", ((MetadataSpecProperty)props[0]).Id);
            }
        }
    }

    public static void The_cancellation_token_reaches_the_other_providers()
    {
        using (var s = new Setup(x => { x.A.DelayMs = 20000; x.A.Names(MetadataField.Genres, "slow"); }, null, false))
        {
            s.Rig.WriteConfig(Config("Genre:" + Self + "," + s.Id(s.A)));
            s.Plugin = new MetadataMergePlugin(s.Rig.Api());
            s.Plugin.Settings.TimeoutSeconds = 1;
            var clock = Stopwatch.StartNew();
            using (var p = s.Provider()) p.GetGenres(new GetMetadataFieldArgs());
            Assert.True(clock.ElapsedMilliseconds < 5000, "took " + clock.ElapsedMilliseconds);
            for (int i = 0; i < 30 && !s.A.SawCancelledToken; i++) Thread.Sleep(100);
            Assert.True(s.A.SawCancelledToken, "the provider's wait was cut by the timeout token");
        }
    }

    private static string Img(string name) { return System.IO.Path.Combine(ImageHeaderTests.DataDir, name); }

    private static void NoMinimums(MergeSettings s)
    {
        foreach (var f in new[] { Field.CoverImage, Field.BackgroundImage, Field.Icon }) { s.ImageFor(f).MinWidth = 0; s.ImageFor(f).MinHeight = 0; s.For(f).Enabled = true; }
    }

    public static void The_image_fields_are_off_until_the_owner_turns_them_on()
    {
        using (var s = new Setup(x => x.A.Files[MetadataField.CoverImage] = new MetadataFile(Img("png-60x90.png")), null, false))
        {
            s.Rig.WriteConfig(Config("CoverImage:" + Self + "," + s.Id(s.A)));
            s.Plugin = new MetadataMergePlugin(s.Rig.Api());
            using (var p = s.Provider())
            {
                Assert.False(p.AvailableFields.Contains(MetadataField.CoverImage));
                Assert.True(p.GetCoverImage(new GetMetadataFieldArgs()) == null);
            }
            Assert.False(s.A.Calls.Contains("CoverImage"), "not even asked");
        }
    }

    public static void The_best_image_is_chosen_by_size_and_proportion_and_only_that_file_goes_back_to_playnite()
    {
        using (var s = new Setup(null, null, false))
        {
            var wrongRatio = new MetadataFile(Img("png-120x80.png"));                                       // 1.5: not a cover
            var cover = new MetadataFile("cover.png", System.IO.File.ReadAllBytes(Img("png-60x90.png")));  // 2:3, held in memory
            var landscapeOnDisk = new MetadataFile(Img("jpeg-200x100.jpg"));                               // 2:1
            s.A.Files[MetadataField.CoverImage] = wrongRatio;
            s.B.Files[MetadataField.CoverImage] = cover;
            s.Library.Store.CoverImage = landscapeOnDisk;
            s.Rig.WriteConfig(Config("CoverImage:" + Self + "," + s.Id(s.A) + "," + s.Id(s.B) + "," + Store));
            s.Plugin = new MetadataMergePlugin(s.Rig.Api());
            NoMinimums(s.Plugin.Settings);
            using (var p = s.Provider())
            {
                Assert.True(p.AvailableFields.Contains(MetadataField.CoverImage));
                var result = p.GetCoverImage(new GetMetadataFieldArgs());
                Assert.True(ReferenceEquals(result, cover), "the winner is the source's own file object");
            }
            string d = s.Plugin.Diagnostics();
            Assert.True(d.Contains("Image decisions") && d.Contains("Test Game CoverImage: Beta 60x90 PNG"), d);
            Assert.True(d.Contains("Alpha 120x80 PNG: rejected: ratio 1.50"), d);
        }
    }

    public static void The_background_rules_prefer_the_jpeg_and_ignore_the_wrong_proportions()
    {
        using (var s = new Setup(null, null, false))
        {
            s.A.Files[MetadataField.BackgroundImage] = new MetadataFile(Img("png-120x80.png"));           // 1.5 rejected
            s.B.Files[MetadataField.BackgroundImage] = new MetadataFile(Img("gif-64x48.gif"));            // 1.33 rejected
            var jpg = new MetadataFile(Img("jpeg-200x100.jpg"));                                          // 2:1, within 15 % of 16:9? 12.5 % yes
            s.Library.Store.BackgroundImage = jpg;
            s.Rig.WriteConfig(Config("BackgroundImage:" + Self + "," + s.Id(s.A) + "," + s.Id(s.B) + "," + Store));
            s.Plugin = new MetadataMergePlugin(s.Rig.Api());
            NoMinimums(s.Plugin.Settings);
            using (var p = s.Provider())
                Assert.True(ReferenceEquals(p.GetBackgroundImage(new GetMetadataFieldArgs()), jpg));
        }
    }

    public static void Nothing_acceptable_gives_null_so_playnite_continues_with_its_own_list()
    {
        using (var s = new Setup(x => x.A.Files[MetadataField.Icon] = new MetadataFile(Img("png-120x80.png")), null, false))
        {
            s.Rig.WriteConfig(Config("Icon:" + Self + "," + s.Id(s.A)));
            s.Plugin = new MetadataMergePlugin(s.Rig.Api());
            NoMinimums(s.Plugin.Settings);
            using (var p = s.Provider())
                Assert.True(p.GetIcon(new GetMetadataFieldArgs()) == null);   // not square
        }
    }

    public static void Images_and_lists_come_from_the_same_single_round_of_each_source()
    {
        using (var s = new Setup(x =>
        {
            x.A.Names(MetadataField.Genres, "g");
            x.A.Files[MetadataField.CoverImage] = new MetadataFile(Img("png-60x90.png"));
        }, null, false))
        {
            string list = Self + "," + s.Id(s.A);
            s.Rig.WriteConfig(Config("Genre:" + list, "CoverImage:" + list));
            s.Plugin = new MetadataMergePlugin(s.Rig.Api());
            NoMinimums(s.Plugin.Settings);
            using (var p = s.Provider())
            {
                p.GetGenres(new GetMetadataFieldArgs());
                p.GetCoverImage(new GetMetadataFieldArgs());
            }
            Assert.Equal(1, s.A.Created);
            Assert.Equal(1, s.A.Calls.Count(c => c == "CoverImage"));
        }
    }

    public static void The_statistics_say_which_source_really_returns_values_for_which_field()
    {
        using (var s = new Setup(x =>
        {
            x.A.Names(MetadataField.Tags, "t1", "t2").Names(MetadataField.Genres, "g");
            x.B.Names(MetadataField.Genres, "g2");
        }, null, false))
        {
            string list = Self + "," + s.Id(s.A) + "," + s.Id(s.B);
            s.Rig.WriteConfig(Config("Genre:" + list, "Tag:" + list));
            s.Plugin = new MetadataMergePlugin(s.Rig.Api());
            for (int game = 0; game < 3; game++)
                using (var p = s.Provider()) { p.GetGenres(new GetMetadataFieldArgs()); p.GetTags(new GetMetadataFieldArgs()); }
            string d = s.Plugin.Log.Report();
            Assert.True(d.Contains("Alpha:") && d.Contains("Tags 6 value(s) in 3 game(s)") && d.Contains("Genres 3 value(s) in 3 game(s)"), d);
            Assert.True(d.Contains("Beta:") && d.Contains("Genres 3 value(s) in 3 game(s)"), d);
            int beta = d.IndexOf("Beta:");
            Assert.False(d.Substring(beta).Contains("Tags"), "Beta gave no tags: " + d.Substring(beta));
        }
    }

    public static void Diagnostics_list_sources_orders_and_statistics()
    {
        using (var s = new Setup(x => x.A.Names(MetadataField.Genres, "g"), null, false))
        {
            s.Rig.WriteConfig(Config("Genre:" + Self + "," + s.Id(s.A) + "," + Store));
            s.Plugin = new MetadataMergePlugin(s.Rig.Api());
            using (var p = s.Provider()) p.GetGenres(new GetMetadataFieldArgs());
            string d = s.Plugin.Diagnostics();
            Assert.True(d.Contains("Alpha (" + s.Id(s.A) + ")"), d);
            Assert.True(d.Contains("Genres: Union, Alpha > Official store"), d);
            Assert.True(d.Contains("Alpha: 1 call(s)"), d);
            Assert.True(d.Contains("config: read"), d);
        }
    }
}
