using System;
using System.Collections.Generic;
using System.Linq;

namespace MetadataMerge.Core
{
    /// <summary>How one field is merged. Plain object: Playnite serialises it as part of the plugin settings.</summary>
    public class FieldSettings
    {
        /// <summary>Off: the extension does not offer the field, Playnite goes on to the next source of its list.</summary>
        public bool Enabled { get; set; } = true;
        public MergeMode Mode { get; set; } = MergeMode.Union;
        public SourceMode Sources { get; set; } = SourceMode.Playnite;
        /// <summary>Source ids (plugin ids, the empty id is the library's own store), in priority order. Used when Sources is Custom, and as the fallback when the Playnite list gives nothing.</summary>
        public List<string> CustomSources { get; set; } = new List<string>();
        public int PerSourceCap { get; set; }
        public int TotalCap { get; set; }
        /// <summary>One "alias=canonical" rule per line.</summary>
        public string Synonyms { get; set; } = "";
        /// <summary>Union only: a value is kept only when at least this many sources gave it (0 or 1 = keep all).</summary>
        public int MinAgreeing { get; set; }
        public SortMode Sort { get; set; } = SortMode.SourceOrder;
        public CaseMode Case { get; set; } = CaseMode.Keep;
        /// <summary>Names longer than this are dropped (0 = no limit).</summary>
        public int MaxValueLength { get; set; }
        /// <summary>A regular expression: names that match are dropped (empty = none).</summary>
        public string ExcludeRegex { get; set; } = "";
        /// <summary>Image fields only (cover, background, icon): the size rules. Null for the other fields.</summary>
        public ImageSettings Image { get; set; }

        public FieldSettings Clone()
        {
            var c = (FieldSettings)MemberwiseClone();
            c.CustomSources = new List<string>(CustomSources ?? new List<string>());
            c.Image = Image == null ? null : Image.Clone();
            return c;
        }
    }

    /// <summary>Every option of the extension, with its default.</summary>
    public class MergeSettings
    {
        /// <summary>Time a source gets to answer for one game; a late source is left out of that game, the others are kept.</summary>
        public int TimeoutSeconds { get; set; } = 30;
        /// <summary>One round for all the enabled fields at the first request of a game (the sources are asked once per game). Off: one round per requested field.</summary>
        public bool Prefetch { get; set; } = true;
        /// <summary>Ignore case, accents, spacing, punctuation and company legal forms when comparing values.</summary>
        public bool LooseMatching { get; set; } = true;
        /// <summary>Show a notification when a source failed or timed out.</summary>
        public bool NotifyErrors { get; set; } = true;
        /// <summary>Write one log line per game and source (otherwise only problems are logged).</summary>
        public bool VerboseLog { get; set; }
        /// <summary>Full path of the Playnite config.json to read the per-field source order from; empty = the one of the running Playnite.</summary>
        public string ConfigPath { get; set; } = "";
        /// <summary>Header bytes read to measure an image (the first request); a second, wider request is made only when the size is further on.</summary>
        public int ImageProbeKb { get; set; } = 64;
        /// <summary>Time limit for measuring the images of one game (0 = the general time limit).</summary>
        public int ImageProbeTimeoutSeconds { get; set; }
        /// <summary>Write the image decisions to the Playnite log.</summary>
        public bool LogImageDecisions { get; set; } = true;
        /// <summary>How many image decisions the Diagnostics box keeps.</summary>
        public int KeepDecisions { get; set; } = 40;
        /// <summary>A source that failed or timed out this many games in a row is paused (0 = never), so a source that is down does not slow a whole batch.</summary>
        public int BreakerFailures { get; set; } = 5;
        /// <summary>How long a paused source is left alone, in minutes.</summary>
        public int BreakerPauseMinutes { get; set; } = 10;
        public Dictionary<string, FieldSettings> Fields { get; set; } = new Dictionary<string, FieldSettings>();

        public MergeSettings() { Normalize(); }

        /// <summary>Every field has an entry, with sane numbers (a file written by an older version, or by hand, may miss some or hold unknown names).</summary>
        public void Normalize()
        {
            var source = Fields ?? new Dictionary<string, FieldSettings>();
            var fixedFields = new Dictionary<string, FieldSettings>();
            foreach (var f in Core.Fields.All)
            {
                string name = f.ToString();
                var s = source.Where(kv => kv.Value != null && string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Value).FirstOrDefault() ?? new FieldSettings { Enabled = Core.Fields.DefaultEnabled(f) };
                if (s.CustomSources == null) s.CustomSources = new List<string>();
                if (s.Synonyms == null) s.Synonyms = "";
                if (s.PerSourceCap < 0) s.PerSourceCap = 0;
                if (s.TotalCap < 0) s.TotalCap = 0;
                if (s.MinAgreeing < 0) s.MinAgreeing = 0;
                if (s.MaxValueLength < 0) s.MaxValueLength = 0;
                if (s.ExcludeRegex == null) s.ExcludeRegex = "";
                if (Core.Fields.IsImage(f)) { if (s.Image == null) s.Image = ImageSettings.DefaultsFor(f); s.Image.Clamp(); }
                else s.Image = null;
                fixedFields[name] = s;
            }
            Fields = fixedFields;
        }

        public FieldSettings For(Field f)
        {
            FieldSettings s;
            if (!Fields.TryGetValue(f.ToString(), out s) || s == null) { Normalize(); s = Fields[f.ToString()]; }
            return s;
        }

        public bool IsEnabled(Field f) { return For(f).Enabled; }

        /// <summary>The size rules of an image field.</summary>
        public ImageSettings ImageFor(Field f)
        {
            var s = For(f);
            if (s.Image == null) { s.Image = ImageSettings.DefaultsFor(f); s.Image.Clamp(); }
            return s.Image;
        }

        public int Timeout() { return Math.Max(1, Math.Min(600, TimeoutSeconds)); }

        public int ImageTimeout() { return ImageProbeTimeoutSeconds > 0 ? Math.Min(600, ImageProbeTimeoutSeconds) : Timeout(); }

        public MergeOptions OptionsFor(Field f)
        {
            var s = For(f);
            bool company = Core.Fields.IsCompany(f);
            System.Text.RegularExpressions.Regex exclude = null;
            if (!string.IsNullOrWhiteSpace(s.ExcludeRegex))
            {
                try { exclude = new System.Text.RegularExpressions.Regex(s.ExcludeRegex, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
                catch (ArgumentException) { }
            }
            return new MergeOptions
            {
                MinAgreeing = s.MinAgreeing, Sort = s.Sort, Case = s.Case, MaxValueLength = s.MaxValueLength, Exclude = exclude,
                Mode = s.Mode,
                PerSourceCap = s.PerSourceCap,
                TotalCap = s.TotalCap,
                Loose = LooseMatching,
                Company = company,
                Synonyms = new Synonyms(s.Synonyms, LooseMatching, company)
            };
        }

        public List<string> Problems()
        {
            var p = new List<string>();
            if (TimeoutSeconds < 1 || TimeoutSeconds > 600) p.Add("TimeoutSeconds must be between 1 and 600");
            if (ImageProbeKb < 8 || ImageProbeKb > 512) p.Add("ImageProbeKb must be between 8 and 512");
            if (ImageProbeTimeoutSeconds < 0 || ImageProbeTimeoutSeconds > 600) p.Add("ImageProbeTimeoutSeconds must be between 0 and 600");
            if (KeepDecisions < 1 || KeepDecisions > 500) p.Add("KeepDecisions must be between 1 and 500");
            if (BreakerFailures < 0 || BreakerFailures > 1000) p.Add("BreakerFailures must be between 0 and 1000");
            if (BreakerPauseMinutes < 1 || BreakerPauseMinutes > 1440) p.Add("BreakerPauseMinutes must be between 1 and 1440");
            foreach (var f in Core.Fields.All)
            {
                var s = For(f);
                if (s.PerSourceCap < 0 || s.PerSourceCap > 1000) p.Add(f + ": PerSourceCap must be between 0 and 1000");
                if (s.TotalCap < 0 || s.TotalCap > 1000) p.Add(f + ": TotalCap must be between 0 and 1000");
                if (s.MinAgreeing < 0 || s.MinAgreeing > 20) p.Add(f + ": MinAgreeing must be between 0 and 20");
                if (s.MaxValueLength < 0 || s.MaxValueLength > 500) p.Add(f + ": MaxValueLength must be between 0 and 500");
                if (!string.IsNullOrWhiteSpace(s.ExcludeRegex))
                {
                    try { new System.Text.RegularExpressions.Regex(s.ExcludeRegex); }
                    catch (ArgumentException ex) { p.Add(f + ": ExcludeRegex: " + ex.Message); }
                }
                if (s.Image != null)
                {
                    string ratio = s.Image.ExpectedRatio;
                    double parsed;
                    if (!string.IsNullOrWhiteSpace(ratio) && !ImageSettings.TryParseRatio(ratio, out parsed)) p.Add(f + ": ExpectedRatio must look like 2:3, 16:9 or 0.67");
                }
            }
            return p;
        }

        public MergeSettings Clone()
        {
            var c = (MergeSettings)MemberwiseClone();
            c.Fields = Fields.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
            return c;
        }
    }
}
