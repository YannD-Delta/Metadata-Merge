using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MetadataMerge.Core
{
    /// <summary>Keys used to decide that two values are the same one. The first source's own spelling is what is kept.</summary>
    public static class Keys
    {
        private static readonly string[] CompanySuffixes =
        {
            "inc", "incorporated", "ltd", "limited", "llc", "llp", "corp", "corporation", "co", "company", "gmbh", "ag", "sa", "sas", "sarl",
            "srl", "spa", "bv", "nv", "ab", "as", "oy", "kk", "plc", "pty", "sro", "sp", "zoo"
        };

        /// <summary>Case, accents, spacing and common punctuation are ignored in loose mode ("Co-op", "Co-Op" and "Coop" are one value).</summary>
        public static string Name(string text, bool loose)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string s = Collapse(text.Normalize(NormalizationForm.FormKC)).ToLowerInvariant();
            if (!loose) return s;
            s = s.Replace("&", " and ");
            string folded = Fold(s);
            return folded.Length > 0 ? folded : s;
        }

        /// <summary>Like <see cref="Name"/> for a company: trailing legal forms ("Inc.", "Ltd", "GmbH") are ignored in loose mode.</summary>
        public static string Company(string text, bool loose)
        {
            if (!loose) return Name(text, false);
            if (string.IsNullOrWhiteSpace(text)) return "";
            var words = Collapse(text.Normalize(NormalizationForm.FormKC)).ToLowerInvariant().Replace("&", " and ").Split(' ').ToList();
            // peel suffixes one by one, never the last word
            while (words.Count > 1)
            {
                string last = Fold(words[words.Count - 1]);
                if (Array.IndexOf(CompanySuffixes, last) < 0) break;
                words.RemoveAt(words.Count - 1);
            }
            string key = Fold(string.Join(" ", words));
            return key.Length > 0 ? key : Name(text, true);
        }

        /// <summary>The same web page written in slightly different ways (http or https, www, trailing slash, fragment) gives one key.</summary>
        public static string Link(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            string s = url.Trim().ToLowerInvariant();
            int hash = s.IndexOf('#');
            if (hash >= 0) s = s.Substring(0, hash);
            foreach (string scheme in new[] { "https://", "http://" })
                if (s.StartsWith(scheme, StringComparison.Ordinal)) { s = s.Substring(scheme.Length); break; }
            if (s.StartsWith("www.", StringComparison.Ordinal)) s = s.Substring(4);
            return s.TrimEnd('/');
        }

        private static string Collapse(string text)
        {
            var sb = new StringBuilder();
            bool space = false;
            foreach (char c in text.Trim())
            {
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Letters and digits only, without accents.</summary>
        private static string Fold(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text.Normalize(NormalizationForm.FormD))
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Optional renaming table, one rule per line: <c>alias=canonical</c>. A rule with an empty right side drops the value
    /// ("Early Access=" removes it). Lines starting with # and blank lines are ignored. Matching uses the same loose key as the merge.
    /// </summary>
    public sealed class Synonyms
    {
        public static readonly Synonyms None = new Synonyms("", true, false);
        private readonly Dictionary<string, string> map = new Dictionary<string, string>();

        public Synonyms(string text, bool loose, bool company)
        {
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = company ? Keys.Company(line.Substring(0, eq), loose) : Keys.Name(line.Substring(0, eq), loose);
                if (key.Length > 0) map[key] = line.Substring(eq + 1).Trim();
            }
        }

        public int Count { get { return map.Count; } }

        /// <summary>The canonical text for the value, the value itself when no rule matches, or null when the value is dropped.</summary>
        public string Apply(string value, string key)
        {
            string target;
            if (!map.TryGetValue(key, out target)) return value;
            return target.Length == 0 ? null : target;
        }
    }
}
