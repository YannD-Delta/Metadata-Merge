using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace MetadataMerge.Core
{
    /// <summary>
    /// The per-field source order Playnite keeps in its config.json (MetadataSettings.&lt;Field&gt;.Sources, a list of plugin ids; the empty id
    /// is the library's own store). The SDK does not expose it, so the file is read, read-only. The dialog's current choice is only
    /// in that file once it was saved as the default.
    /// </summary>
    public static class PlayniteConfig
    {
        public const string StoreId = "00000000-0000-0000-0000-000000000000";

        public static string NormalizeId(string id)
        {
            Guid g;
            return Guid.TryParse((id ?? "").Trim(), out g) ? g.ToString("D") : (id ?? "").Trim().ToLowerInvariant();
        }

        /// <summary>The lists per field; a field that is missing from the file is missing from the result. Throws on text that is not JSON.</summary>
        public static Dictionary<Field, List<string>> ParseSources(string json)
        {
            var result = new Dictionary<Field, List<string>>();
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 };
            var root = serializer.DeserializeObject(json) as IDictionary<string, object>;
            if (root == null) return result;
            var settings = Get(root, "MetadataSettings") as IDictionary<string, object>;
            if (settings == null) return result;
            foreach (var f in Fields.All)
            {
                var entry = Get(settings, Fields.ConfigName(f)) as IDictionary<string, object>;
                if (entry == null) continue;
                var list = new List<string>();
                var sources = Get(entry, "Sources") as IEnumerable;
                if (sources != null)
                    foreach (object o in sources)
                    {
                        string id = o as string;
                        if (!string.IsNullOrWhiteSpace(id)) list.Add(NormalizeId(id));
                    }
                result[f] = list;
            }
            return result;
        }

        private static object Get(IDictionary<string, object> d, string key)
        {
            foreach (var kv in d)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }
    }

    /// <summary>Reads the config file again only when it changed. A file caught half written, or missing, keeps the last good answer.</summary>
    public sealed class ConfigReader
    {
        private readonly Func<string> path;
        private readonly object gate = new object();
        private DateTime stamp;
        private long length = -1;
        private string stampedPath;
        private Dictionary<Field, List<string>> last = new Dictionary<Field, List<string>>();

        public ConfigReader(Func<string> path) { this.path = path; }

        /// <summary>Why the last read failed, or null.</summary>
        public string LastError { get; private set; }

        public Dictionary<Field, List<string>> Get()
        {
            lock (gate)
            {
                string p;
                try { p = path(); } catch (Exception ex) { LastError = ex.Message; return last; }
                try
                {
                    if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) { LastError = "config file not found"; return last; }
                    var info = new FileInfo(p);
                    if (p == stampedPath && info.LastWriteTimeUtc == stamp && info.Length == length) return last;
                    string text;
                    using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fs))
                        text = reader.ReadToEnd();
                    last = PlayniteConfig.ParseSources(text);
                    stamp = info.LastWriteTimeUtc; length = info.Length; stampedPath = p;
                    LastError = null;
                }
                catch (Exception ex) { LastError = ex.Message; }
                return last;
            }
        }
    }

    /// <summary>Which sources to merge for a field, in priority order.</summary>
    public static class SourceResolver
    {
        /// <summary>
        /// Custom: the extension's own list. Playnite: the sources that follow this extension in Playnite's list for the field (all the others
        /// when the extension is not in the saved list: the dialog's choice was not saved); when that gives nothing, the own list.
        /// This extension itself is never a source.
        /// </summary>
        public static List<string> Resolve(FieldSettings fs, List<string> playniteList, string selfId)
        {
            string self = PlayniteConfig.NormalizeId(selfId);
            List<string> ids = null;
            if (fs.Sources == SourceMode.Playnite && playniteList != null)
            {
                var list = playniteList.Select(PlayniteConfig.NormalizeId).ToList();
                int at = list.IndexOf(self);
                ids = at >= 0 ? list.Skip(at + 1).ToList() : list;
            }
            if (ids == null || ids.Count == 0) ids = (fs.CustomSources ?? new List<string>()).Select(PlayniteConfig.NormalizeId).ToList();
            var seen = new HashSet<string>();
            return ids.Where(i => i.Length > 0 && i != self && seen.Add(i)).ToList();
        }
    }
}
