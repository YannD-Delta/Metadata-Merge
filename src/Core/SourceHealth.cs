using System;
using System.Collections.Generic;

namespace MetadataMerge.Core
{
    /// <summary>
    /// Remembers, across games, which sources keep failing. A source that failed or timed out N games in a row is paused for a while, so a source that
    /// is down does not cost its full time limit on every game of a long batch. The first success resets the count.
    /// </summary>
    public sealed class SourceHealth
    {
        private sealed class Entry { public int Fails; public DateTime PausedUntil; }

        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private readonly Func<DateTime> clock;

        public SourceHealth(Func<DateTime> clock = null) { this.clock = clock ?? (() => DateTime.UtcNow); }

        public bool IsPaused(string id)
        {
            lock (entries)
            {
                Entry e;
                return entries.TryGetValue(id, out e) && e.PausedUntil > clock();
            }
        }

        public void Success(string id)
        {
            lock (entries) { Entry e; if (entries.TryGetValue(id, out e)) { e.Fails = 0; e.PausedUntil = DateTime.MinValue; } }
        }

        /// <summary>Counts a failure; true when this one pauses the source (threshold reached, 0 = the breaker is off).</summary>
        public bool Failure(string id, int threshold, TimeSpan pause)
        {
            if (threshold <= 0) return false;
            lock (entries)
            {
                Entry e;
                if (!entries.TryGetValue(id, out e)) entries[id] = e = new Entry();
                e.Fails++;
                if (e.Fails < threshold) return false;
                bool wasPaused = e.PausedUntil > clock();
                e.PausedUntil = clock() + pause;
                return !wasPaused;
            }
        }

        public int Failures(string id) { lock (entries) { Entry e; return entries.TryGetValue(id, out e) ? e.Fails : 0; } }

        public void Reset() { lock (entries) entries.Clear(); }
    }
}
