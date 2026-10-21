using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using MetadataMerge.Core;

namespace MetadataMerge
{
    /// <summary>
    /// A metadata source that merges the other sources. Put it first in the source list of a field in Playnite's "Download metadata"
    /// dialog: it then asks the other sources of that field in parallel and returns the union of what they found.
    /// It writes nothing itself: Playnite applies what a provider returns.
    /// </summary>
    public class MetadataMergePlugin : MetadataPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string NotificationId = "MetadataMerge.Problems";
        private readonly MetadataMergeSettings settings;
        private readonly SourceCatalog catalog;
        private readonly ConfigReader config;
        private readonly MergeLog log;
        private readonly SourceHealth health = new SourceHealth();
        private readonly CleanService clean;

        /// <summary>Wait before the problem notification is shown (problems that follow each other are grouped); changed by the tests.</summary>
        public static int NotifyDelayMs = 4000;

        public static readonly Guid PluginId = Guid.Parse("b8e2f4a7-3c19-4d65-9a0b-6f7d1e2c5a84");
        public override Guid Id { get; } = PluginId;
        public override string Name { get { return "Metadata Merge"; } }

        /// <summary>All the fields the extension can merge: Playnite offers it as a source for these in the metadata dialog.</summary>
        public override List<MetadataField> SupportedFields { get; } = Fields.All.Select(Mapping.SdkField).ToList();

        public MetadataMergePlugin(IPlayniteAPI api) : base(api)
        {
            Properties = new MetadataPluginProperties { HasSettings = true };
            catalog = new SourceCatalog(() => PlayniteApi.Addons.Plugins, Id, () => Loc.Get("LOCMM_OfficialStore"));
            config = new ConfigReader(ConfigFilePath);
            log = new MergeLog(logger, () => settings.Data, OnProblems, NotifyDelayMs);
            settings = new MetadataMergeSettings(this, catalog, config, log);
            clean = new CleanService(api, () => settings.Data, Application.Current == null ? null : Application.Current.Dispatcher, GetPluginUserDataPath());
        }

        public override ISettings GetSettings(bool firstRunSettings) { return settings; }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            var view = Views.Load("Plugin.Views.Settings.xaml");
            view.DataContext = settings;
            return view;
        }

        public override OnDemandMetadataProvider GetMetadataProvider(MetadataRequestOptions options)
        {
            return new MergeProvider(this, options);
        }

        public MergeSettings Settings { get { return settings.Data; } }
        public MergeLog Log { get { return log; } }
        public SourceHealth Health { get { return health; } }
        public CleanService Clean { get { return clean; } }

        // ---- library clean-up: only from here (menu, settings button), never by itself

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            string section = "@Metadata Merge";
            yield return new MainMenuItem { MenuSection = section, Description = Loc.Get("LOCMM_MenuClean"), Action = a => clean.RunInteractive() };
            yield return new MainMenuItem { MenuSection = section, Description = Loc.Get("LOCMM_MenuUndo"), Action = a => clean.UndoInteractive() };
            yield return new MainMenuItem { MenuSection = section, Description = Loc.Get("LOCMM_MenuReports"), Action = a => OpenFolder(clean.ReportDir) };
        }

        public void OpenFolder(string path)
        {
            try { Directory.CreateDirectory(path); System.Diagnostics.Process.Start(path); }
            catch (Exception ex) { logger.Warn(ex, "Metadata Merge: cannot open " + path); }
        }

        /// <summary>For the testbed (called through reflection by a dev-only plugin): the pass without dialogs.</summary>
        public string CleanForTest(bool apply)
        {
            string journal;
            string report = clean.RunWithoutDialogs(apply, out journal);
            return report + (journal != null ? Environment.NewLine + "JOURNAL: " + journal : "");
        }

        public string UndoForTest() { return clean.UndoWithoutDialogs(); }
        public SourceCatalog Catalog { get { return catalog; } }

        /// <summary>The config.json to read the per-field source order from (read only).</summary>
        private string ConfigFilePath()
        {
            string own = (settings == null ? null : settings.Data.ConfigPath) ?? "";
            if (own.Trim().Length > 0) return own.Trim();
            string dir = PlayniteApi.Paths.ConfigurationPath;
            if (string.IsNullOrWhiteSpace(dir)) return null;
            return File.Exists(dir) ? dir : Path.Combine(dir, "config.json");
        }

        /// <summary>The sources to merge for a field, in priority order (installed ones only; a missing one is reported once).</summary>
        public IList<ISource> SourcesFor(Field f)
        {
            List<string> playniteList = null;
            config.Get().TryGetValue(f, out playniteList);
            var sources = new List<ISource>();
            foreach (var id in SourceResolver.Resolve(settings.Data.For(f), playniteList, Id.ToString("D")))
            {
                var source = catalog.Find(id);
                if (source == null) log.MissingSource(id); else sources.Add(source);
            }
            return sources;
        }

        /// <summary>The fields offered right now (the enabled ones).</summary>
        public List<MetadataField> EnabledFields()
        {
            return Fields.All.Where(f => settings.Data.IsEnabled(f)).Select(Mapping.SdkField).ToList();
        }

        public GameMerge NewMerge(Game game)
        {
            return new GameMerge(new GameRef { Id = game.Id, Name = game.Name, Tag = game }, settings.Data, SourcesFor, log, null, health);
        }

        /// <summary>Called by the settings object after Playnite saved it.</summary>
        public void OnSettingsSaved() { }

        // ---- the one notification

        private void OnProblems(int count, string summary)
        {
            try
            {
                PlayniteApi.Notifications.Remove(NotificationId);
                PlayniteApi.Notifications.Add(new NotificationMessage(NotificationId,
                    Loc.Format("LOCMM_NotifProblems", count, summary), NotificationType.Error,
                    new Action(() => PlayniteApi.MainView.OpenPluginSettings(Id))));
            }
            catch (Exception ex) { logger.Error(ex, "Metadata Merge: cannot show the notification"); }
        }

        /// <summary>A text a user can paste in a bug report: sources installed, order per field, config reading, statistics.</summary>
        public string Diagnostics()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Metadata Merge diagnostics");
            string path = null;
            try { path = ConfigFilePath(); } catch (Exception ex) { sb.AppendLine("config path: " + ex.Message); }
            var lists = config.Get();
            sb.AppendLine("config: " + (config.LastError == null ? "read" : "problem: " + config.LastError));
            sb.AppendLine("installed sources:");
            foreach (var s in catalog.Installed()) sb.AppendLine("  " + s.Name + " (" + s.Id + ")");
            sb.AppendLine("per field (as merged):");
            foreach (var f in Fields.All)
            {
                var fs = settings.Data.For(f);
                string ids = string.Join(" > ", SourcesFor(f).Select(s => s.Name));
                sb.AppendLine("  " + f + ": " + (fs.Enabled ? fs.Mode + ", " : "off, ") + (ids.Length > 0 ? ids : "(no source)"));
            }
            foreach (var src in catalog.Installed().Where(x => health.IsPaused(x.Id))) sb.AppendLine("PAUSED after repeated failures: " + src.Name);
            string stats = log.Report();
            sb.AppendLine("statistics since Playnite started:");
            sb.AppendLine(stats.Length > 0 ? stats : "  (none yet)");
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>The provider Playnite creates for ONE game: it answers the merged fields from a per-game snapshot.</summary>
    public sealed class MergeProvider : OnDemandMetadataProvider
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly MetadataMergePlugin plugin;
        private readonly MetadataRequestOptions options;
        private GameMerge merge;
        private readonly object gate = new object();

        public MergeProvider(MetadataMergePlugin plugin, MetadataRequestOptions options)
        {
            this.plugin = plugin;
            this.options = options;
        }

        public override List<MetadataField> AvailableFields { get { return plugin.EnabledFields(); } }

        private List<Item> Get(Field f, GetMetadataFieldArgs args)
        {
            try
            {
                GameMerge m;
                lock (gate) { if (merge == null) merge = plugin.NewMerge(options.GameData); m = merge; }
                var items = m.Get(f, args == null ? CancellationToken.None : args.CancelToken);
                if (plugin.Settings.VerboseLog) logger.Info("Metadata Merge: " + options.GameData.Name + " " + f + " = " + items.Count + " value(s) (" + m.Describe(f) + ")");
                return items;
            }
            catch (Exception ex)
            {
                // never let a bug here break the download of the other fields or the other sources
                logger.Error(ex, "Metadata Merge: " + f + " failed");
                return new List<Item>();
            }
        }

        private static IEnumerable<MetadataProperty> Props(List<Item> items) { return items.Count == 0 ? null : Mapping.ToProperties(items); }

        public override IEnumerable<MetadataProperty> GetGenres(GetMetadataFieldArgs args) { return Props(Get(Field.Genres, args)); }
        public override IEnumerable<MetadataProperty> GetDevelopers(GetMetadataFieldArgs args) { return Props(Get(Field.Developers, args)); }
        public override IEnumerable<MetadataProperty> GetPublishers(GetMetadataFieldArgs args) { return Props(Get(Field.Publishers, args)); }
        public override IEnumerable<MetadataProperty> GetFeatures(GetMetadataFieldArgs args) { return Props(Get(Field.Features, args)); }
        public override IEnumerable<MetadataProperty> GetTags(GetMetadataFieldArgs args) { return Props(Get(Field.Tags, args)); }
        public override IEnumerable<MetadataProperty> GetSeries(GetMetadataFieldArgs args) { return Props(Get(Field.Series, args)); }
        public override IEnumerable<MetadataProperty> GetPlatforms(GetMetadataFieldArgs args) { return Props(Get(Field.Platforms, args)); }
        public override IEnumerable<MetadataProperty> GetRegions(GetMetadataFieldArgs args) { return Props(Get(Field.Regions, args)); }
        public override IEnumerable<MetadataProperty> GetAgeRatings(GetMetadataFieldArgs args) { return Props(Get(Field.AgeRatings, args)); }

        public override MetadataFile GetCoverImage(GetMetadataFieldArgs args) { return Mapping.ToFile(Get(Field.CoverImage, args)); }
        public override MetadataFile GetBackgroundImage(GetMetadataFieldArgs args) { return Mapping.ToFile(Get(Field.BackgroundImage, args)); }
        public override MetadataFile GetIcon(GetMetadataFieldArgs args) { return Mapping.ToFile(Get(Field.Icon, args)); }

        public override IEnumerable<Link> GetLinks(GetMetadataFieldArgs args)
        {
            var links = Mapping.ToLinks(Get(Field.Links, args));
            return links.Count == 0 ? null : links;
        }

        public override void Dispose()
        {
            lock (gate) { if (merge != null) merge.Dispose(); merge = null; }
            base.Dispose();
        }
    }

    internal static class Views
    {
        /// <summary>XAML embedded in the dll, loaded at runtime (csc cannot compile BAML); no code-behind.</summary>
        public static UserControl Load(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MetadataMerge." + name))
            {
                if (stream == null) throw new FileNotFoundException("Embedded view not found: " + name);
                return (UserControl)XamlReader.Load(stream);
            }
        }
    }
}
