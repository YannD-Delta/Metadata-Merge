using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MetadataMerge.Core
{
    /// <summary>
    /// A plain text file of renaming rules and drop expressions for several fields at once, so a rule set (say one made from a whole library) is
    /// imported in one go instead of pasted field by field.
    /// <code>
    /// # comment
    /// [Genres]            one "alias=name" rule per line (empty right side drops the value)
    /// Aventure=Adventure
    /// [Features: drop]    one regular expression per line (several lines are alternatives)
    /// ^Co-Op Extras:.*$
    /// </code>
    /// </summary>
    public static class RulesFile
    {
        public sealed class Block
        {
            public Field Field;
            /// <summary>The renaming rules (null: the file has no such section for the field, the current ones are kept).</summary>
            public string Synonyms;
            /// <summary>The drop expression (null: not in the file).</summary>
            public string Exclude;
            public int RuleCount;
        }

        private static readonly Regex Header = new Regex(@"^\[\s*([A-Za-z]+)\s*(?::\s*(drop|exclude))?\s*\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static Dictionary<Field, Block> Parse(string text, List<string> problems)
        {
            var blocks = new Dictionary<Field, Block>();
            Field current = default(Field);
            bool inSection = false, drop = false, skip = false;
            var rules = new Dictionary<Field, List<string>>();
            var drops = new Dictionary<Field, List<string>>();
            int lineNo = 0;
            foreach (string raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                lineNo++;
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var m = Header.Match(line);
                if (m.Success)
                {
                    Field f;
                    if (!Enum.TryParse(m.Groups[1].Value, true, out f) || Fields.IsImage(f) || f == Field.Links)
                    {
                        problems.Add("line " + lineNo + ": \"" + line + "\" is not a list field; its lines are ignored");
                        skip = true; inSection = false;
                        continue;
                    }
                    current = f; inSection = true; skip = false; drop = m.Groups[2].Success;
                    if (!blocks.ContainsKey(f)) blocks[f] = new Block { Field = f };
                    continue;
                }
                if (skip) continue;
                if (!inSection) { problems.Add("line " + lineNo + ": a rule before any [Field] header is ignored"); continue; }
                if (drop)
                {
                    try { new Regex(line); }
                    catch (ArgumentException ex) { problems.Add("line " + lineNo + ": not a valid expression (" + ex.Message + ")"); continue; }
                    List<string> l;
                    if (!drops.TryGetValue(current, out l)) drops[current] = l = new List<string>();
                    l.Add(line);
                }
                else
                {
                    if (line.IndexOf('=') <= 0) { problems.Add("line " + lineNo + ": \"" + line + "\" is not alias=name"); continue; }
                    List<string> l;
                    if (!rules.TryGetValue(current, out l)) rules[current] = l = new List<string>();
                    l.Add(line);
                }
            }
            foreach (var f in rules.Keys) { blocks[f].Synonyms = string.Join("\n", rules[f]); blocks[f].RuleCount = rules[f].Count; }
            foreach (var f in drops.Keys) blocks[f].Exclude = drops[f].Count == 1 ? drops[f][0] : string.Join("|", drops[f].Select(x => "(?:" + x + ")"));
            return blocks.Where(kv => kv.Value.Synonyms != null || kv.Value.Exclude != null).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        /// <summary>Writes the rules into the settings (only for the fields and parts the file holds). Returns how many fields changed.</summary>
        public static int Apply(Dictionary<Field, Block> blocks, MergeSettings settings)
        {
            foreach (var b in blocks.Values)
            {
                var fs = settings.For(b.Field);
                if (b.Synonyms != null) fs.Synonyms = b.Synonyms;
                if (b.Exclude != null) fs.ExcludeRegex = b.Exclude;
            }
            return blocks.Count;
        }

        public static string Render(MergeSettings settings)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Metadata Merge rules. [Field] = renaming rules (alias=name), [Field: drop] = names to drop (regular expression).");
            foreach (var f in Fields.All)
            {
                if (Fields.IsImage(f) || f == Field.Links) continue;
                var fs = settings.For(f);
                if (!string.IsNullOrWhiteSpace(fs.Synonyms))
                {
                    sb.AppendLine().AppendLine("[" + f + "]");
                    foreach (var l in fs.Synonyms.Replace("\r\n", "\n").Split('\n')) if (l.Trim().Length > 0) sb.AppendLine(l.TrimEnd());
                }
                if (!string.IsNullOrWhiteSpace(fs.ExcludeRegex)) sb.AppendLine().AppendLine("[" + f + ": drop]").AppendLine(fs.ExcludeRegex);
            }
            return sb.ToString();
        }
    }
}
