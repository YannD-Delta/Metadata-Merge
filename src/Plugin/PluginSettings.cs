using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Playnite.SDK;
using Playnite.SDK.Data;
using MetadataMerge.Core;

namespace MetadataMerge
{
    public class Choice
    {
        public object Value { get; set; }
        public string Label { get; set; }
    }

    /// <summary>One source in a field's own list: ticked = merged, the row order is the priority.</summary>
    public class SourceRow : ObservableObject
    {
        private readonly FieldViewModel owner;
        private bool selected;

        public SourceRow(FieldViewModel owner, string id, string name, bool installed, bool selected)
        {
            this.owner = owner;
            Id = id;
            Name = name;
            Installed = installed;
            this.selected = selected;
            UpCommand = new RelayCommand(() => owner.Move(this, -1));
            DownCommand = new RelayCommand(() => owner.Move(this, 1));
        }

        public string Id { get; private set; }
        public string Name { get; private set; }
        public bool Installed { get; private set; }
        /// <summary>The name, with a note when the plugin is not installed.</summary>
        public string Display { get { return Installed ? Name : Name + " " + Loc.Get("LOCMM_NotInstalled"); } }

        public bool Selected
        {
            get { return selected; }
            set { selected = value; OnPropertyChanged(); owner.RowsChanged(); }
        }

        [DontSerialize] public RelayCommand UpCommand { get; private set; }
        [DontSerialize] public RelayCommand DownCommand { get; private set; }
    }

    /// <summary>The settings of one field as the page shows it; edits go straight into the plain <see cref="FieldSettings"/>.</summary>
    public class FieldViewModel : ObservableObject
    {
        private readonly MetadataMergeSettings parent;
        private bool loading;

        public FieldViewModel(MetadataMergeSettings parent, Field field)
        {
            this.parent = parent;
            Field = field;
            Rows = new ObservableCollection<SourceRow>();
            Refresh();
        }

        public Field Field { get; private set; }
        public bool IsImage { get { return Fields.IsImage(Field); } }
        public FieldSettings Data { get { return parent.Data.For(Field); } }
        public string Title { get { return Loc.Get("LOCMM_Field_" + Field); } }
        public ObservableCollection<SourceRow> Rows { get; private set; }

        public bool Enabled
        {
            get { return Data.Enabled; }
            set { Data.Enabled = value; OnPropertyChanged(); OnPropertyChanged("Summary"); }
        }

        public MergeMode Mode
        {
            get { return Data.Mode; }
            set { Data.Mode = value; OnPropertyChanged(); OnPropertyChanged("Summary"); }
        }

        public SourceMode Sources
        {
            get { return Data.Sources; }
            set { Data.Sources = value; OnPropertyChanged(); OnPropertyChanged("Summary"); OnPropertyChanged("PlayniteList"); }
        }

        public int PerSourceCap { get { return Data.PerSourceCap; } set { Data.PerSourceCap = Math.Max(0, value); OnPropertyChanged(); OnPropertyChanged("Summary"); } }
        public int TotalCap { get { return Data.TotalCap; } set { Data.TotalCap = Math.Max(0, value); OnPropertyChanged(); OnPropertyChanged("Summary"); } }
        public string Synonyms { get { return Data.Synonyms; } set { Data.Synonyms = value ?? ""; OnPropertyChanged(); } }

        private ImageSettings Img { get { return parent.Data.ImageFor(Field); } }
        public int MinAgreeing { get { return Data.MinAgreeing; } set { Data.MinAgreeing = Math.Max(0, value); OnPropertyChanged(); OnPropertyChanged("Summary"); } }
        public int MaxValueLength { get { return Data.MaxValueLength; } set { Data.MaxValueLength = Math.Max(0, value); OnPropertyChanged(); } }
        public string ExcludeRegex { get { return Data.ExcludeRegex; } set { Data.ExcludeRegex = value ?? ""; OnPropertyChanged(); } }
        public SortMode Sort { get { return Data.Sort; } set { Data.Sort = value; OnPropertyChanged(); } }
        public CaseMode Case { get { return Data.Case; } set { Data.Case = value; OnPropertyChanged(); } }

        public List<Choice> SortChoices
        {
            get
            {
                return new List<Choice>
                {
                    new Choice { Value = SortMode.SourceOrder, Label = Loc.Get("LOCMM_SortSource") },
                    new Choice { Value = SortMode.Alphabetical, Label = Loc.Get("LOCMM_SortAlpha") },
                    new Choice { Value = SortMode.MostAgreed, Label = Loc.Get("LOCMM_SortAgreed") }
                };
            }
        }

        public List<Choice> CaseChoices
        {
            get
            {
                return new List<Choice>
                {
                    new Choice { Value = CaseMode.Keep, Label = Loc.Get("LOCMM_CaseKeep") },
                    new Choice { Value = CaseMode.Title, Label = Loc.Get("LOCMM_CaseTitle") },
                    new Choice { Value = CaseMode.Lower, Label = Loc.Get("LOCMM_CaseLower") },
                    new Choice { Value = CaseMode.Upper, Label = Loc.Get("LOCMM_CaseUpper") }
                };
            }
        }

        public int ImgMinPixels { get { return Img.MinPixels; } set { Img.MinPixels = Math.Max(0, value); OnPropertyChanged(); } }
        public int ImgMaxFileKb { get { return Img.MaxFileKb; } set { Img.MaxFileKb = Math.Max(0, value); OnPropertyChanged(); } }
        public string ImgExpectedRatio { get { return Img.ExpectedRatio; } set { Img.ExpectedRatio = value ?? ""; OnPropertyChanged(); } }
        public bool ImgStrictAspect { get { return Img.StrictAspect; } set { Img.StrictAspect = value; OnPropertyChanged(); } }
        public AboveCapMode ImgAboveCap { get { return Img.AboveCap; } set { Img.AboveCap = value; OnPropertyChanged(); } }
        public UnknownSizeMode ImgUnknownSize { get { return Img.UnknownSize; } set { Img.UnknownSize = value; OnPropertyChanged(); } }

        public List<Choice> AboveCapChoices
        {
            get
            {
                return new List<Choice>
                {
                    new Choice { Value = AboveCapMode.Smallest, Label = Loc.Get("LOCMM_AboveSmallest") },
                    new Choice { Value = AboveCapMode.Reject, Label = Loc.Get("LOCMM_AboveReject") },
                    new Choice { Value = AboveCapMode.Largest, Label = Loc.Get("LOCMM_AboveLargest") }
                };
            }
        }

        public List<Choice> UnknownSizeChoices
        {
            get
            {
                return new List<Choice>
                {
                    new Choice { Value = UnknownSizeMode.UseFirst, Label = Loc.Get("LOCMM_UnknownFirst") },
                    new Choice { Value = UnknownSizeMode.Reject, Label = Loc.Get("LOCMM_UnknownReject") }
                };
            }
        }

        public int ImgMinWidth { get { return Img.MinWidth; } set { Img.MinWidth = Math.Max(0, value); OnPropertyChanged(); } }
        public int ImgMinHeight { get { return Img.MinHeight; } set { Img.MinHeight = Math.Max(0, value); OnPropertyChanged(); } }
        public int ImgMaxWidth { get { return Img.MaxWidth; } set { Img.MaxWidth = Math.Max(0, value); OnPropertyChanged(); OnPropertyChanged("Summary"); } }
        public int ImgMaxHeight { get { return Img.MaxHeight; } set { Img.MaxHeight = Math.Max(0, value); OnPropertyChanged(); OnPropertyChanged("Summary"); } }
        public int ImgTolerance { get { return Img.AspectTolerancePercent; } set { Img.AspectTolerancePercent = Math.Max(0, Math.Min(100, value)); OnPropertyChanged(); } }
        public int ImgComparable { get { return Img.ComparablePercent; } set { Img.ComparablePercent = Math.Max(0, Math.Min(100, value)); OnPropertyChanged(); } }
        public string ImgFormats { get { return Img.FormatPreference; } set { Img.FormatPreference = value ?? ""; OnPropertyChanged(); } }

        public List<Choice> ModeChoices
        {
            get
            {
                return new List<Choice>
                {
                    new Choice { Value = MergeMode.Union, Label = Loc.Get("LOCMM_ModeUnion") },
                    new Choice { Value = MergeMode.FirstNonEmpty, Label = Loc.Get("LOCMM_ModeFirst") }
                };
            }
        }

        public List<Choice> SourceChoices
        {
            get
            {
                return new List<Choice>
                {
                    new Choice { Value = SourceMode.Playnite, Label = Loc.Get("LOCMM_SourcesPlaynite") },
                    new Choice { Value = SourceMode.Custom, Label = Loc.Get("LOCMM_SourcesCustom") }
                };
            }
        }

        /// <summary>The list Playnite has for the field right now (read from its config.json), as this extension will use it.</summary>
        public string PlayniteList
        {
            get
            {
                var lists = parent.PlayniteLists();
                List<string> list;
                if (!lists.TryGetValue(Field, out list) || list.Count == 0) return Loc.Get("LOCMM_ListNone");
                var names = parent.Names();
                string self = parent.SelfId;
                bool inList = list.Select(PlayniteConfig.NormalizeId).Contains(self);
                string text = string.Join(" > ", list.Select(PlayniteConfig.NormalizeId).Select(id =>
                    id == self ? "[Metadata Merge]" : names.ContainsKey(id) ? names[id] : id));
                return inList ? text : text + "  (" + Loc.Get("LOCMM_ListNotFirst") + ")";
            }
        }

        public string Summary
        {
            get
            {
                if (!Enabled) return Loc.Get("LOCMM_Off");
                if (IsImage)
                    return Loc.Format("LOCMM_SumImage", ImgMaxWidth == 0 && ImgMaxHeight == 0 ? "-" : ImgMaxWidth + "x" + ImgMaxHeight)
                        + " - " + (Sources == SourceMode.Playnite ? Loc.Get("LOCMM_SumPlaynite") : Loc.Get("LOCMM_SumCustom"));
                var parts = new List<string>
                {
                    Mode == MergeMode.Union ? Loc.Get("LOCMM_SumUnion") : Loc.Get("LOCMM_SumFirst"),
                    Sources == SourceMode.Playnite ? Loc.Get("LOCMM_SumPlaynite") : Loc.Get("LOCMM_SumCustom")
                };
                if (PerSourceCap > 0) parts.Add(Loc.Format("LOCMM_SummaryPerSource", PerSourceCap));
                if (TotalCap > 0) parts.Add(Loc.Format("LOCMM_SummaryTotal", TotalCap));
                return string.Join(" - ", parts);
            }
        }

        /// <summary>Rebuilds the source rows from the saved own list and the installed sources: ticked ones first in saved order, then the others.</summary>
        public void Refresh()
        {
            loading = true;
            try
            {
                Rows.Clear();
                var installed = parent.InstalledSources();
                var saved = Data.CustomSources.Select(PlayniteConfig.NormalizeId).Distinct().ToList();
                foreach (var id in saved)
                {
                    var info = installed.FirstOrDefault(i => i.Id == id);
                    Rows.Add(new SourceRow(this, id, info != null ? info.Name : id, info != null, true));
                }
                foreach (var info in installed)
                    if (!saved.Contains(info.Id)) Rows.Add(new SourceRow(this, info.Id, info.Name, true, false));
            }
            finally { loading = false; }
            OnPropertyChanged("PlayniteList");
            OnPropertyChanged("Summary");
        }

        internal void Move(SourceRow row, int delta)
        {
            int at = Rows.IndexOf(row);
            int to = at + delta;
            if (at < 0 || to < 0 || to >= Rows.Count) return;
            Rows.Move(at, to);
            RowsChanged();
        }

        internal void RowsChanged()
        {
            if (loading) return;
            Data.CustomSources = Rows.Where(r => r.Selected).Select(r => r.Id).ToList();
        }
    }

    /// <summary>What Playnite saves and shows in Add-ons, Extension settings. The options themselves are the plain <see cref="MergeSettings"/>.</summary>
    public class MetadataMergeSettings : ObservableObject, ISettings
    {
        private readonly MetadataMergePlugin plugin;
        private readonly SourceCatalog catalog;
        private readonly ConfigReader config;
        private readonly MergeLog log;
        private MergeSettings data = new MergeSettings();
        private MergeSettings backup;
        private List<FieldViewModel> fieldModels;
        private string diagnostics = "";

        public MergeSettings Data
        {
            get { return data; }
            set { data = value ?? new MergeSettings(); data.Normalize(); fieldModels = null; OnPropertyChanged(); OnPropertyChanged("FieldModels"); }
        }

        /// <summary>Used by the serializer.</summary>
        public MetadataMergeSettings() { }

        public MetadataMergeSettings(MetadataMergePlugin plugin, SourceCatalog catalog, ConfigReader config, MergeLog log)
        {
            this.plugin = plugin;
            this.catalog = catalog;
            this.config = config;
            this.log = log;
            var saved = plugin.LoadPluginSettings<MetadataMergeSettings>();
            if (saved != null && saved.Data != null) { data = saved.Data; data.Normalize(); }
            RefreshCommand = new RelayCommand(RefreshDiagnostics);
            CopyCommand = new RelayCommand(() => { RefreshDiagnostics(); try { Clipboard.SetText(diagnostics); } catch { } });
            ResetStatsCommand = new RelayCommand(() => { log.Reset(); RefreshDiagnostics(); });
            CleanCommand = new RelayCommand(() => plugin.Clean.RunInteractive());
            UndoCommand = new RelayCommand(() => plugin.Clean.UndoInteractive());
            ImportRulesCommand = new RelayCommand(ImportRules);
            ExportRulesCommand = new RelayCommand(ExportRules);
            OpenReportsCommand = new RelayCommand(() => plugin.OpenFolder(plugin.Clean.ReportDir));
        }

        [DontSerialize] public RelayCommand RefreshCommand { get; private set; }
        [DontSerialize] public RelayCommand CopyCommand { get; private set; }
        [DontSerialize] public RelayCommand ResetStatsCommand { get; private set; }
        [DontSerialize] public RelayCommand CleanCommand { get; private set; }
        [DontSerialize] public RelayCommand UndoCommand { get; private set; }
        [DontSerialize] public RelayCommand ImportRulesCommand { get; private set; }
        [DontSerialize] public RelayCommand ExportRulesCommand { get; private set; }

        /// <summary>Reads a rules file (see <see cref="RulesFile"/>) into the settings and tells what it did. Returns the message.</summary>
        public string ApplyRulesText(string text)
        {
            var problems = new List<string>();
            var blocks = RulesFile.Parse(text, problems);
            int rules = blocks.Values.Sum(b => b.RuleCount);
            int drops = blocks.Values.Count(b => b.Exclude != null);
            RulesFile.Apply(blocks, data);
            data.Normalize();
            fieldModels = null;
            OnPropertyChanged("FieldModels");
            string msg = Loc.Format("LOCMM_RulesImported", blocks.Count, rules, drops);
            if (problems.Count > 0) msg += Environment.NewLine + Environment.NewLine + Loc.Get("LOCMM_RulesProblems") + Environment.NewLine + string.Join(Environment.NewLine, problems.Take(15));
            return msg;
        }

        private void ImportRules()
        {
            string path = plugin.PlayniteApi.Dialogs.SelectFile("Metadata Merge rules|*.txt;*.rules|All files|*.*");
            if (string.IsNullOrEmpty(path)) return;
            try { plugin.PlayniteApi.Dialogs.ShowMessage(ApplyRulesText(System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8))); }
            catch (Exception ex) { plugin.PlayniteApi.Dialogs.ShowErrorMessage(ex.Message); }
        }

        private void ExportRules()
        {
            string path = plugin.PlayniteApi.Dialogs.SaveFile("Metadata Merge rules|*.txt");
            if (string.IsNullOrEmpty(path)) return;
            try { System.IO.File.WriteAllText(path, RulesFile.Render(data), System.Text.Encoding.UTF8); }
            catch (Exception ex) { plugin.PlayniteApi.Dialogs.ShowErrorMessage(ex.Message); }
        }
        [DontSerialize] public RelayCommand OpenReportsCommand { get; private set; }

        internal string SelfId { get { return plugin == null ? "" : plugin.Id.ToString("D"); } }
        internal Dictionary<Field, List<string>> PlayniteLists() { return config == null ? new Dictionary<Field, List<string>>() : config.Get(); }
        internal List<SourceInfo> InstalledSources() { return catalog == null ? new List<SourceInfo>() : catalog.Installed(); }
        internal Dictionary<string, string> Names()
        {
            var d = new Dictionary<string, string>();
            foreach (var s in InstalledSources()) d[s.Id] = s.Name;
            return d;
        }

        [DontSerialize]
        public List<FieldViewModel> FieldModels
        {
            get { return fieldModels ?? (fieldModels = Fields.All.Select(f => new FieldViewModel(this, f)).ToList()); }
        }

        // ---- general options (wrappers: the options object itself does not notify)

        [DontSerialize]
        public string Timeout
        {
            get { return data.TimeoutSeconds.ToString(); }
            set { int n; if (int.TryParse(value, out n)) data.TimeoutSeconds = n; OnPropertyChanged(); }
        }

        [DontSerialize]
        public string Diagnostics
        {
            get { return diagnostics; }
            private set { diagnostics = value; OnPropertyChanged(); }
        }

        public void RefreshDiagnostics()
        {
            foreach (var m in FieldModels) m.Refresh();
            Diagnostics = plugin == null ? "" : plugin.Diagnostics();
        }

        public void BeginEdit() { backup = data.Clone(); RefreshDiagnostics(); }

        public void CancelEdit()
        {
            if (backup != null) Data = backup;
        }

        public void EndEdit()
        {
            if (plugin == null) return;
            data.Normalize();
            plugin.SavePluginSettings(this);
            plugin.OnSettingsSaved();
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = data.Problems();
            return errors.Count == 0;
        }
    }

    /// <summary>Strings of the Localization folder (Playnite merges the file of the current language over en_US).</summary>
    public static class Loc
    {
        public static string Get(string key)
        {
            try
            {
                string s = ResourceProvider.GetString(key);
                return string.IsNullOrEmpty(s) ? key : s;
            }
            catch { return key; }
        }

        public static string Format(string key, params object[] args)
        {
            try { return string.Format(Get(key), args); }
            catch { return Get(key); }
        }
    }
}
