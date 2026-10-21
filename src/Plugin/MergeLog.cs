using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Playnite.SDK;
using MetadataMerge.Core;

namespace MetadataMerge
{
    /// <summary>
    /// Receives what the sources did: keeps per-source statistics (shown on the settings page), writes problems to the Playnite log
    /// and raises ONE notification, updated at most every few seconds, when sources failed or timed out.
    /// </summary>
    public sealed class MergeLog : IMergeObserver, IDisposable
    {
        public sealed class Stat
        {
            public string Name;
            public int Calls, Ok, Errors, Timeouts, Cancelled, Skipped, Pauses;
            /// <summary>Per field: how many values the source gave in total, and in how many games it gave at least one.</summary>
            public readonly Dictionary<Field, int> FieldValues = new Dictionary<Field, int>();
            public readonly Dictionary<Field, int> FieldGames = new Dictionary<Field, int>();
            public long TotalMs;
            public string LastError;
            public string LastGame;
        }

        private readonly ILogger logger;
        private readonly Func<MergeSettings> settings;
        private readonly Action<int, string> notify;
        private readonly object gate = new object();
        private readonly Dictionary<string, Stat> stats = new Dictionary<string, Stat>();
        private readonly HashSet<string> missing = new HashSet<string>();
        private readonly Timer debounce;
        private readonly int debounceMs;
        private readonly LinkedList<string> images = new LinkedList<string>();
        private int problems;
        private bool disposed;

        /// <param name="notify">Called (count of problems, summary text) when problems were seen; may be called from a worker thread.</param>
        public MergeLog(ILogger logger, Func<MergeSettings> settings, Action<int, string> notify, int debounceMs = 4000)
        {
            this.logger = logger;
            this.settings = settings;
            this.notify = notify;
            this.debounceMs = debounceMs;
            debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Fetched(SourceReport r)
        {
            bool problem = r.Outcome == Outcome.Error || r.Outcome == Outcome.Timeout || r.FieldProblems.Count > 0;
            if (r.Outcome == Outcome.Skipped) problem = false;
            lock (gate)
            {
                Stat s;
                if (!stats.TryGetValue(r.SourceId, out s)) stats[r.SourceId] = s = new Stat();
                s.Name = r.SourceName;
                s.Calls++;
                s.TotalMs += r.Milliseconds;
                s.LastGame = r.GameName;
                switch (r.Outcome)
                {
                    case Outcome.Ok: s.Ok++; break;
                    case Outcome.Error: s.Errors++; s.LastError = r.Error; break;
                    case Outcome.Timeout: s.Timeouts++; s.LastError = "timeout"; break;
                    case Outcome.Cancelled: s.Cancelled++; break;
                    case Outcome.Skipped: s.Skipped++; s.Calls--; break;
                }
                if (r.FieldProblems.Count > 0) { s.Errors++; s.LastError = r.FieldProblems[0]; }
                foreach (var kv in r.Counts)
                {
                    if (kv.Value <= 0) continue;
                    int v, g;
                    s.FieldValues.TryGetValue(kv.Key, out v); s.FieldValues[kv.Key] = v + kv.Value;
                    s.FieldGames.TryGetValue(kv.Key, out g); s.FieldGames[kv.Key] = g + 1;
                }
                if (problem) problems++;
            }
            WriteLog(r, problem);
            if (problem && settings().NotifyErrors) debounce.Change(debounceMs, Timeout.Infinite);
        }

        public void ImageDecided(ImageDecision d)
        {
            string line = d.ToString();
            lock (gate)
            {
                images.AddLast(line);
                int keep = Math.Max(1, settings().KeepDecisions);
                while (images.Count > keep) images.RemoveFirst();
            }
            Safe(() => { if (settings().LogImageDecisions) logger.Info("Metadata Merge: " + line); });
        }

        public void SourcePaused(string sourceId, string sourceName, int failures, int minutes)
        {
            lock (gate)
            {
                Stat s;
                if (!stats.TryGetValue(sourceId, out s)) stats[sourceId] = s = new Stat { Name = sourceName };
                s.Pauses++;
                problems++;
            }
            Safe(() => logger.Warn("Metadata Merge: " + sourceName + " failed " + failures + " games in a row: paused for " + minutes + " minute(s), skipped meanwhile."));
            if (settings().NotifyErrors) debounce.Change(debounceMs, Timeout.Infinite);
        }

        public void MissingSource(string sourceId)
        {
            bool first;
            lock (gate) first = missing.Add(sourceId);
            if (first) Safe(() => logger.Warn("Metadata Merge: source " + sourceId + " is in a field's list but is not installed (or not enabled); it is skipped."));
        }

        private void WriteLog(SourceReport r, bool problem)
        {
            string counts = string.Join(", ", r.Counts.OrderBy(c => c.Key).Select(c => c.Key + "=" + c.Value));
            string line = "Metadata Merge: " + r.SourceName + " for \"" + r.GameName + "\": " + r.Outcome + " in " + r.Milliseconds + " ms"
                + (counts.Length > 0 ? " (" + counts + ")" : "")
                + (r.Error != null ? " - " + r.Error : "")
                + (r.FieldProblems.Count > 0 ? " - " + string.Join("; ", r.FieldProblems) : "");
            Safe(() => { if (problem) logger.Warn(line); else if (settings().VerboseLog) logger.Info(line); });
        }

        private void Flush()
        {
            int count;
            string text;
            lock (gate)
            {
                if (disposed || problems == 0) return;
                count = problems;
                text = Summary(true);
            }
            Safe(() => notify(count, text));
        }

        /// <summary>One line per source that had a problem (for the notification).</summary>
        private string Summary(bool onlyProblems)
        {
            var parts = stats.Values.Where(s => !onlyProblems || s.Errors + s.Timeouts > 0)
                .Select(s => Loc.Format("LOCMM_StatProblems", s.Name, s.Errors, s.Timeouts));
            return string.Join("; ", parts);
        }

        /// <summary>The statistics since Playnite started, one line per source.</summary>
        public string Report()
        {
            lock (gate)
            {
                if (stats.Count == 0 && images.Count == 0) return "";
                var sb = new StringBuilder();
                foreach (var s in stats.Values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    sb.Append(s.Name).Append(": ").Append(s.Calls).Append(" call(s), ").Append(s.Ok).Append(" ok, ")
                      .Append(s.Errors).Append(" error(s), ").Append(s.Timeouts).Append(" timeout(s), ")
                      .Append(s.Pauses > 0 ? s.Pauses + " pause(s), " + s.Skipped + " game(s) skipped, " : "")
                      .Append(s.Calls > 0 ? s.TotalMs / s.Calls : 0).Append(" ms on average");
                    if (s.LastError != null) sb.Append(" - last problem: ").Append(s.LastError).Append(" (").Append(s.LastGame).Append(")");
                    sb.AppendLine();
                    if (s.FieldGames.Count > 0)
                        sb.AppendLine("    gave values for: " + string.Join(", ", s.FieldGames.OrderBy(kv => kv.Key).Select(kv => kv.Key + " " + s.FieldValues[kv.Key] + " value(s) in " + kv.Value + " game(s)")));
                }
                if (missing.Count > 0) sb.AppendLine("Not installed: " + string.Join(", ", missing));
                if (images.Count > 0)
                {
                    sb.AppendLine("Image decisions (last " + images.Count + "):");
                    foreach (var line in images) sb.AppendLine("  " + line);
                }
                return sb.ToString().TrimEnd();
            }
        }

        public int ProblemCount { get { lock (gate) return problems; } }

        public void Reset()
        {
            lock (gate) { stats.Clear(); missing.Clear(); images.Clear(); problems = 0; }
        }

        private static void Safe(Action a) { try { a(); } catch { } }

        public void Dispose()
        {
            lock (gate) disposed = true;
            debounce.Dispose();
        }
    }
}
