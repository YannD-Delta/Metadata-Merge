using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using MetadataMerge;
using MetadataMerge.Core;
using Playnite.SDK.Plugins;

public static class SettingsViewTests
{
    private sealed class Env : IDisposable
    {
        public FakeApi.Rig Rig = new FakeApi.Rig();
        public FakeMeta A = new FakeMeta("Alpha");
        public FakeMeta B = new FakeMeta("Beta");
        public MetadataMergePlugin Plugin;

        public Env()
        {
            Rig.Plugins.Add(A); Rig.Plugins.Add(B);
            Rig.WriteConfig("{ \"MetadataSettings\": { \"Genre\": { \"Import\": true, \"Sources\": [\"" + MetadataMergePlugin.PluginId.ToString("D") + "\", \"" + A.Id + "\", \"" + PlayniteConfig.StoreId + "\"] } } }");
            Plugin = new MetadataMergePlugin(Rig.Api());
        }

        public MetadataMergeSettings Settings { get { return (MetadataMergeSettings)Plugin.GetSettings(false); } }
        public void Dispose() { Plugin.Log.Dispose(); Rig.Cleanup(); }
    }

    private static UserControl Load(object context)
    {
        using (var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("MetadataMerge.Plugin.Views.Settings.xaml"))
        {
            var view = (UserControl)XamlReader.Load(stream);
            view.DataContext = context;
            return view;
        }
    }

    /// <summary>The view is not in a window, so IsVisible is always false: follow the Visibility of the element and its parents.</summary>
    private static bool Shown(DependencyObject d)
    {
        while (d != null)
        {
            var ui = d as UIElement;
            if (ui != null && ui.Visibility != Visibility.Visible) return false;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return true;
    }

    private static void ExpandAll(UserControl view)
    {
        foreach (var e in UiHost.Descendants(view).OfType<Expander>().ToList()) e.IsExpanded = true;
    }

    public static void The_page_loads_and_every_binding_resolves_with_all_fields_unfolded()
    {
        using (var env = new Env())
            UiHost.Run(() =>
            {
                UiHost.ClearErrors();
                var settings = env.Settings;
                settings.BeginEdit();
                var view = Load(settings);
                UiHost.Layout(view, 800, 6000);
                ExpandAll(view);
                UiHost.Layout(view, 800, 9000);
                var texts = UiHost.Texts(view);
                Assert.True(texts.Contains("Genres") && texts.Contains("Age ratings"), "field titles: " + string.Join(";", texts.Take(20)));
                Assert.True(texts.Any(t => t.StartsWith("Union: add the values")), "mode combo shows the selected choice");
                Assert.True(texts.Any(t => t.Contains("[Metadata Merge] > Alpha > Official store")), "the saved Playnite list is shown: " + string.Join(";", texts.Where(t => t.Contains(">"))));
                Assert.True(texts.Any(t => t.Contains("(no list found")), "a field without a saved list says so");
                var checks = UiHost.Descendants(view).OfType<CheckBox>().Select(c => c.Content as string).Where(c => c != null).ToList();
                Assert.True(checks.Any(c => c.StartsWith("Ask every source once per game")), "general checkboxes: " + string.Join(";", checks));
                Assert.Equal("", UiHost.Errors(), "binding errors");
            });
    }

    public static void The_page_follows_the_language()
    {
        using (var env = new Env())
            UiHost.Run(() =>
            {
                UiHost.UseLanguage("fr_FR");
                try
                {
                    var settings = env.Settings;
                    var view = Load(settings);
                    UiHost.Layout(view, 800, 6000);
                    ExpandAll(view);
                    UiHost.Layout(view, 800, 9000);
                    var texts = UiHost.Texts(view);
                    Assert.True(texts.Contains("Développeurs") && texts.Contains("Fonctionnalités"), string.Join(";", texts.Take(24)));
                    Assert.True(texts.Any(t => t.StartsWith("Union : ajouter")), "combo");
                    Assert.True(texts.Any(t => t.Contains("Magasin officiel")), "the official store is named in the language");
                }
                finally { UiHost.UseLanguage("en_US"); }
            });
    }

    public static void Every_string_key_exists_in_both_languages()
    {
        string repo = UiHost.Repo;
        Func<string, HashSet<string>> keys = name =>
        {
            var text = System.IO.File.ReadAllText(System.IO.Path.Combine(repo, "src", "Localization", name + ".xaml"));
            return new HashSet<string>(System.Text.RegularExpressions.Regex.Matches(text, "x:Key=\"(LOCMM_[A-Za-z_]+)\"").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value));
        };
        var en = keys("en_US");
        var fr = keys("fr_FR");
        Assert.Equal(0, en.Except(fr).Count(), "missing in fr_FR: " + string.Join(",", en.Except(fr)));
        Assert.Equal(0, fr.Except(en).Count(), "missing in en_US: " + string.Join(",", fr.Except(en)));
        // every key the code or the views use exists
        var used = new HashSet<string>();
        foreach (var file in System.IO.Directory.GetFiles(System.IO.Path.Combine(repo, "src"), "*.*", System.IO.SearchOption.AllDirectories).Where(f => f.EndsWith(".cs") || f.EndsWith("Settings.xaml")))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(System.IO.File.ReadAllText(file), "LOCMM_[A-Za-z_]+"))
                if (!m.Value.EndsWith("_")) used.Add(m.Value);
        Assert.Equal(0, used.Except(en).Count(), "used but not defined: " + string.Join(",", used.Except(en)));
        foreach (var f in Fields.All) Assert.True(en.Contains("LOCMM_Field_" + f), "title of " + f);
    }

    public static void Rows_list_the_installed_sources_and_the_selection_follows_the_row_order()
    {
        using (var env = new Env())
            UiHost.Run(() =>
            {
                var settings = env.Settings;
                var genres = settings.FieldModels.Single(m => m.Field == Field.Genres);
                Assert.Equal("Official store,Alpha,Beta", string.Join(",", genres.Rows.Select(r => r.Name)));
                Assert.Equal(0, genres.Data.CustomSources.Count);
                string a = env.A.Id.ToString("D"), b = env.B.Id.ToString("D");
                genres.Rows[2].Selected = true;     // Beta
                genres.Rows[1].Selected = true;     // Alpha: the order is the one of the rows, not of the clicks
                Assert.Equal(a + "," + b, string.Join(",", genres.Data.CustomSources));
                genres.Move(genres.Rows[2], -1);    // Beta above Alpha
                Assert.Equal("Official store,Beta,Alpha", string.Join(",", genres.Rows.Select(r => r.Name)));
                Assert.Equal(b + "," + a, string.Join(",", genres.Data.CustomSources));
                genres.Move(genres.Rows[0], -1);    // nothing above the first row
                Assert.Equal("Official store", genres.Rows[0].Name);
            });
    }

    public static void Saved_own_lists_come_back_in_order_with_the_rest_below_and_missing_plugins_flagged()
    {
        using (var env = new Env())
            UiHost.Run(() =>
            {
                string ghost = Guid.NewGuid().ToString("D");
                env.Plugin.Settings.For(Field.Tags).CustomSources = new List<string> { env.B.Id.ToString("D"), ghost, env.A.Id.ToString("D") };
                var settings = env.Settings;
                settings.Data = settings.Data;   // rebuild the view models
                var tags = settings.FieldModels.Single(m => m.Field == Field.Tags);
                Assert.Equal("Beta," + ghost + ",Alpha,Official store", string.Join(",", tags.Rows.Select(r => r.Name)));
                Assert.True(tags.Rows[1].Display.Contains("not installed"), tags.Rows[1].Display);
                Assert.True(tags.Rows.Take(3).All(r => r.Selected) && !tags.Rows[3].Selected);
                tags.Rows[1].Selected = false;
                Assert.False(tags.Data.CustomSources.Contains(ghost), "unticking a missing plugin drops it");
            });
    }

    public static void Image_fields_show_their_size_rules_and_write_them_to_the_data()
    {
        using (var env = new Env())
            UiHost.Run(() =>
            {
                var cover = env.Settings.FieldModels.Single(m => m.Field == Field.CoverImage);
                Assert.True(cover.IsImage);
                Assert.False(cover.Enabled);
                Assert.Equal("off", cover.Summary);
                cover.Enabled = true;
                Assert.Equal(1000, cover.ImgMaxWidth); Assert.Equal(1500, cover.ImgMaxHeight); Assert.Equal(300, cover.ImgMinWidth);
                cover.ImgMaxWidth = 800; cover.ImgMaxHeight = 1200; cover.ImgTolerance = 20; cover.ImgComparable = 10; cover.ImgFormats = "png,jpeg";
                var data = env.Plugin.Settings.ImageFor(Field.CoverImage);
                Assert.Equal(800, data.MaxWidth); Assert.Equal(1200, data.MaxHeight); Assert.Equal(20, data.AspectTolerancePercent);
                Assert.Equal(10, data.ComparablePercent); Assert.Equal("png,jpeg", data.FormatPreference);
                Assert.True(cover.Summary.Contains("800x1200"), cover.Summary);
                cover.ImgTolerance = 500;
                Assert.Equal(100, data.AspectTolerancePercent);
                Assert.False(env.Settings.FieldModels.Single(m => m.Field == Field.Genres).IsImage);
            });
    }

    public static void The_extra_list_parameters_write_to_the_data()
    {
        using (var env = new Env())
            UiHost.Run(() =>
            {
                var tags = env.Settings.FieldModels.Single(m => m.Field == Field.Tags);
                tags.MinAgreeing = 2; tags.MaxValueLength = 25; tags.ExcludeRegex = "^early"; tags.Sort = SortMode.MostAgreed; tags.Case = CaseMode.Title;
                var d = env.Plugin.Settings.For(Field.Tags);
                Assert.Equal(2, d.MinAgreeing); Assert.Equal(25, d.MaxValueLength); Assert.Equal("^early", d.ExcludeRegex);
                Assert.Equal(SortMode.MostAgreed, d.Sort); Assert.Equal(CaseMode.Title, d.Case);
                Assert.Equal(4, tags.SortChoices.Count + 1); Assert.Equal(4, tags.CaseChoices.Count);
                tags.MinAgreeing = -3;
                Assert.Equal(0, d.MinAgreeing);
            });
    }

    public static void The_extra_image_parameters_write_to_the_data()
    {
        using (var env = new Env())
            UiHost.Run(() =>
            {
                var back = env.Settings.FieldModels.Single(m => m.Field == Field.BackgroundImage);
                back.ImgMinPixels = 2000000; back.ImgMaxFileKb = 4000; back.ImgExpectedRatio = "21:9"; back.ImgStrictAspect = false;
                back.ImgAboveCap = AboveCapMode.Reject; back.ImgUnknownSize = UnknownSizeMode.Reject;
                var d = env.Plugin.Settings.ImageFor(Field.BackgroundImage);
                Assert.Equal(2000000, d.MinPixels); Assert.Equal(4000, d.MaxFileKb); Assert.Equal("21:9", d.ExpectedRatio);
                Assert.False(d.StrictAspect); Assert.Equal(AboveCapMode.Reject, d.AboveCap); Assert.Equal(UnknownSizeMode.Reject, d.UnknownSize);
                Assert.Equal(3, back.AboveCapChoices.Count); Assert.Equal(2, back.UnknownSizeChoices.Count);
                var json = Json.ToJson(env.Settings);
                var back2 = Json.FromJson<MetadataMergeSettings>(json);
                Assert.Equal("21:9", back2.Data.ImageFor(Field.BackgroundImage).ExpectedRatio);
                Assert.Equal(AboveCapMode.Reject, back2.Data.ImageFor(Field.BackgroundImage).AboveCap);
                Assert.Equal(5, back2.Data.BreakerFailures);
                Assert.Equal(64, back2.Data.ImageProbeKb);
            });
    }

    public static void A_rules_file_is_imported_into_the_fields_and_exported_back()
    {
        using (var env = new Env())
            UiHost.Run(() =>
            {
                var settings = env.Settings;
                string msg = settings.ApplyRulesText("[Genres]\nAventure=Adventure\nbroken line\n[Tags: drop]\n^\\[POV\\]\n");
                Assert.True(msg.Contains("2 field(s) updated, 1 renaming rule(s), 1 drop expression(s)"), msg);
                Assert.True(msg.Contains("Lines that were ignored") && msg.Contains("broken line"), msg);
                var genres = settings.FieldModels.Single(m => m.Field == Field.Genres);
                Assert.Equal("Aventure=Adventure", genres.Synonyms);
                Assert.Equal("^\\[POV\\]", settings.FieldModels.Single(m => m.Field == Field.Tags).ExcludeRegex);
                Assert.True(RulesFile.Render(env.Plugin.Settings).Contains("[Genres]"));
            });
    }

    public static void The_image_controls_are_shown_for_image_fields_only()
    {
        using (var env = new Env())
            UiHost.Run(() =>
            {
                UiHost.ClearErrors();
                var settings = env.Settings;
                var view = Load(settings);
                UiHost.Layout(view, 800, 6000);
                ExpandAll(view);
                UiHost.Layout(view, 800, 12000);
                var visible = UiHost.Descendants(view).OfType<TextBlock>().Where(Shown).Select(t => t.Text).ToList();
                // three image fields show the size rules, the ten list fields do not
                Assert.Equal(3, visible.Count(t => t.StartsWith("Smallest accepted size")), "image panels: " + visible.Count(t => t.StartsWith("Smallest accepted size")));
                Assert.Equal(10, visible.Count(t => t.StartsWith("How to combine the sources")), "list panels");
                Assert.Equal("", UiHost.Errors(), "binding errors");
            });
    }

    public static void Editing_the_options_writes_to_the_data_and_the_summary_follows()
    {
        using (var env = new Env())
            UiHost.Run(() =>
            {
                var tags = env.Settings.FieldModels.Single(m => m.Field == Field.Tags);
                Assert.Equal("Union - Playnite's list", tags.Summary);
                tags.Mode = MergeMode.FirstNonEmpty;
                tags.PerSourceCap = 5;
                tags.TotalCap = 12;
                Assert.Equal(MergeMode.FirstNonEmpty, env.Plugin.Settings.For(Field.Tags).Mode);
                Assert.Equal(5, env.Plugin.Settings.For(Field.Tags).PerSourceCap);
                Assert.True(tags.Summary.Contains("5 per source") && tags.Summary.Contains("12 at most"), tags.Summary);
                tags.Enabled = false;
                Assert.Equal("off", tags.Summary);
                env.Settings.Timeout = "45";
                Assert.Equal(45, env.Plugin.Settings.TimeoutSeconds);
                env.Settings.Timeout = "abc";
                Assert.Equal(45, env.Plugin.Settings.TimeoutSeconds);
            });
    }

    public static void Cancelling_restores_the_options()
    {
        using (var env = new Env())
            UiHost.Run(() =>
            {
                var settings = env.Settings;
                settings.BeginEdit();
                settings.FieldModels.Single(m => m.Field == Field.Genres).Mode = MergeMode.FirstNonEmpty;
                settings.Data.TimeoutSeconds = 99;
                settings.CancelEdit();
                Assert.Equal(MergeMode.Union, env.Plugin.Settings.For(Field.Genres).Mode);
                Assert.Equal(30, env.Plugin.Settings.TimeoutSeconds);
            });
    }

    public static void Invalid_numbers_are_refused_by_verify()
    {
        using (var env = new Env())
        {
            var settings = env.Settings;
            List<string> errors;
            Assert.True(settings.VerifySettings(out errors));
            settings.Data.TimeoutSeconds = 0;
            Assert.False(settings.VerifySettings(out errors));
            Assert.True(errors.Count == 1);
        }
    }

    public static void Settings_round_trip_through_the_playnite_serializer_and_only_the_options_are_saved()
    {
        using (var env = new Env())
        {
            var s = env.Settings;
            s.Data.TimeoutSeconds = 12;
            s.Data.For(Field.Genres).Synonyms = "a=b";
            s.Data.For(Field.Genres).CustomSources.Add("x");
            s.Data.For(Field.Genres).Mode = MergeMode.FirstNonEmpty;
            string json = Json.ToJson(s);
            Assert.False(json.Contains("FieldModels") || json.Contains("Diagnostics") || json.Contains("Rows"), json);
            var back = Json.FromJson<MetadataMergeSettings>(json);
            Assert.Equal(12, back.Data.TimeoutSeconds);
            Assert.Equal("a=b", back.Data.For(Field.Genres).Synonyms);
            Assert.Equal(MergeMode.FirstNonEmpty, back.Data.For(Field.Genres).Mode);
            Assert.Equal("x", back.Data.For(Field.Genres).CustomSources.Single());
            Assert.Equal(Fields.All.Length, back.Data.Fields.Count);
            Assert.Equal(1000, back.Data.ImageFor(Field.CoverImage).MaxWidth);
        }
    }

    public static void An_old_or_hand_edited_settings_file_still_loads()
    {
        string json = "{ \"Data\": { \"TimeoutSeconds\": 7, \"Unknown\": 1, \"Fields\": { \"Tags\": { \"Mode\": 1 }, \"Bogus\": { \"Enabled\": false } } } }";
        var back = Json.FromJson<MetadataMergeSettings>(json);
        back.Data.Normalize();
        Assert.Equal(7, back.Data.TimeoutSeconds);
        Assert.Equal(MergeMode.FirstNonEmpty, back.Data.For(Field.Tags).Mode);
        Assert.True(back.Data.IsEnabled(Field.Genres));
        Assert.False(back.Data.Fields.ContainsKey("Bogus"));
        Assert.Equal(Fields.All.Length, back.Data.Fields.Count);
    }

    public static void Diagnostics_text_shows_up_on_refresh()
    {
        using (var env = new Env())
            UiHost.Run(() =>
            {
                var s = env.Settings;
                s.RefreshDiagnostics();
                Assert.True(s.Diagnostics.Contains("Metadata Merge diagnostics") && s.Diagnostics.Contains("Alpha"), s.Diagnostics);
                Assert.True(s.Diagnostics.Contains("Genres: Union, Alpha > Official store"), s.Diagnostics);
            });
    }
}
