using System;
using System.Collections.Generic;
using System.Linq;

namespace MetadataMerge.Core
{
    /// <summary>What one source gave for a field, in the source's own order.</summary>
    public sealed class SourceItems
    {
        public string SourceId;
        public string SourceName;
        public List<Item> Items = new List<Item>();
    }

    /// <summary>How the merged values are ordered.</summary>
    public enum SortMode { SourceOrder = 0, Alphabetical = 1, MostAgreed = 2 }

    /// <summary>How the text of a name is written: as the source wrote it, lower case, upper case, or each lower-case word capitalised.</summary>
    public enum CaseMode { Keep = 0, Lower = 1, Upper = 2, Title = 3 }

    public sealed class MergeOptions
    {
        public MergeMode Mode = MergeMode.Union;
        /// <summary>Largest number of values taken from one source (after removing its own duplicates); 0 = no limit.</summary>
        public int PerSourceCap;
        /// <summary>Largest number of values in the result; 0 = no limit.</summary>
        public int TotalCap;
        public bool Loose = true;
        public bool Company;
        public Synonyms Synonyms = Synonyms.None;
        /// <summary>A value is kept only when at least this many sources gave it (union only); 0 or 1 = every value.</summary>
        public int MinAgreeing;
        public SortMode Sort = SortMode.SourceOrder;
        public CaseMode Case = CaseMode.Keep;
        /// <summary>Names longer than this are dropped (junk); 0 = no limit.</summary>
        public int MaxValueLength;
        /// <summary>Names matching this expression are dropped; null = none.</summary>
        public System.Text.RegularExpressions.Regex Exclude;
    }

    /// <summary>The pure merge: sources in priority order in, one list out. No Playnite, no I/O.</summary>
    public static class MergeEngine
    {
        public static List<Item> Merge(IEnumerable<SourceItems> sources, MergeOptions o)
        {
            var result = new List<Item>();
            var keys = new List<string>();
            var counts = new Dictionary<string, int>();
            var seen = new HashSet<string>();
            foreach (var source in sources)
            {
                var own = Prepare(source.Items, o);
                if (own.Count == 0) continue;
                if (o.PerSourceCap > 0 && own.Count > o.PerSourceCap) own = own.Take(o.PerSourceCap).ToList();
                if (o.Mode == MergeMode.FirstNonEmpty)
                {
                    result = own.Select(p => p.Value).ToList();
                    keys = own.Select(p => p.Key).ToList();
                    foreach (var k in keys) counts[k] = 1;
                    break;
                }
                foreach (var pair in own)
                {
                    int n;
                    counts.TryGetValue(pair.Key, out n);
                    counts[pair.Key] = n + 1;
                    if (seen.Add(pair.Key)) { result.Add(pair.Value); keys.Add(pair.Key); }
                }
            }
            if (o.Mode == MergeMode.Union && o.MinAgreeing > 1)
            {
                var keep = Enumerable.Range(0, result.Count).Where(i => counts[keys[i]] >= o.MinAgreeing).ToList();
                result = keep.Select(i => result[i]).ToList();
                keys = keep.Select(i => keys[i]).ToList();
            }
            if (o.Sort != SortMode.SourceOrder && result.Count > 1)
            {
                var order = Enumerable.Range(0, result.Count);
                order = o.Sort == SortMode.Alphabetical
                    ? order.OrderBy(i => result[i].Kind == ItemKind.Link ? result[i].Label ?? result[i].Value : result[i].Value, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i)
                    : order.OrderByDescending(i => counts[keys[i]]).ThenBy(i => i);
                result = order.Select(i => result[i]).ToList();
            }
            if (o.TotalCap > 0 && result.Count > o.TotalCap) result = result.Take(o.TotalCap).ToList();
            return result;
        }

        /// <summary>The source's values, renamed by the synonym table, without empty ones and without its own duplicates.</summary>
        private static List<KeyValuePair<string, Item>> Prepare(IEnumerable<Item> items, MergeOptions o)
        {
            var list = new List<KeyValuePair<string, Item>>();
            var seen = new HashSet<string>();
            foreach (var item in items ?? Enumerable.Empty<Item>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Value)) continue;
                var shaped = Shape(item, o);
                if (shaped == null) continue;
                string key = KeyOf(shaped, o);
                if (key.Length == 0 || !seen.Add(key)) continue;
                list.Add(new KeyValuePair<string, Item>(key, shaped));
            }
            return list;
        }

        /// <summary>Applies the synonym table to a name; the other kinds are left as they are. Null = dropped.</summary>
        private static Item Shape(Item item, MergeOptions o)
        {
            if (item.Kind != ItemKind.Name) return item;
            string text = item.Value.Trim();
            string key = o.Company ? Keys.Company(text, o.Loose) : Keys.Name(text, o.Loose);
            string canonical = o.Synonyms.Apply(text, key);
            if (canonical == null) return null;
            canonical = Recase(canonical, o.Case);
            if (o.MaxValueLength > 0 && canonical.Length > o.MaxValueLength) return null;
            if (o.Exclude != null && o.Exclude.IsMatch(canonical)) return null;
            return Item.Name(canonical);
        }

        public static string Recase(string text, CaseMode mode)
        {
            switch (mode)
            {
                case CaseMode.Lower: return text.ToLower(System.Globalization.CultureInfo.CurrentCulture);
                case CaseMode.Upper: return text.ToUpper(System.Globalization.CultureInfo.CurrentCulture);
                case CaseMode.Title:
                    // only the words written entirely in lower case are capitalised: "open world" -> "Open World", "RPG" and "iPhone" stay
                    return string.Join(" ", text.Split(' ').Select(w => w.Length > 0 && w == w.ToLower(System.Globalization.CultureInfo.CurrentCulture) && char.IsLetter(w[0]) ? char.ToUpper(w[0], System.Globalization.CultureInfo.CurrentCulture) + w.Substring(1) : w));
            }
            return text;
        }

        private static string KeyOf(Item item, MergeOptions o)
        {
            string k;
            switch (item.Kind)
            {
                case ItemKind.Name: k = o.Company ? Keys.Company(item.Value, o.Loose) : Keys.Name(item.Value, o.Loose); return k.Length == 0 ? "" : "n:" + k;
                case ItemKind.Id: return "i:" + item.Value.Trim().ToLowerInvariant();
                case ItemKind.Spec: k = item.Value.Trim().ToLowerInvariant(); return k.Length == 0 ? "" : "s:" + k;
                case ItemKind.Link: k = Keys.Link(item.Value); return k.Length == 0 ? "" : "l:" + k;
            }
            return "";
        }
    }
}
