using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MetadataMerge.Core
{
    /// <summary>
    /// The merge of ONE game. Playnite asks a provider field by field; this object asks each source once per game (all the enabled fields in
    /// one round by default), in parallel, with a time limit and error isolation, and answers every field from that snapshot.
    /// Create one per game (the provider Playnite creates for the game owns it) and dispose it at the end.
    /// </summary>
    public sealed class GameMerge : IDisposable
    {
        private sealed class Loaded
        {
            public readonly HashSet<Field> Fields = new HashSet<Field>();
            public readonly Dictionary<Field, List<Item>> Values = new Dictionary<Field, List<Item>>();
        }

        private sealed class Job
        {
            public ISource Source;
            public List<Field> Fields;
            public Task<SourceReport> Task;
            public Dictionary<Field, List<Item>> Values;
        }

        private readonly GameRef game;
        private readonly MergeSettings settings;
        private readonly Func<Field, IList<ISource>> sourcesFor;
        private readonly IMergeObserver observer;
        private readonly SourceHealth health;
        private readonly IImageReader imageReader;
        private readonly Dictionary<Field, List<Item>> imageChoices = new Dictionary<Field, List<Item>>();
        private readonly object gate = new object();
        private readonly Dictionary<string, Loaded> loaded = new Dictionary<string, Loaded>();
        private readonly Dictionary<string, ISourceSession> sessions = new Dictionary<string, ISourceSession>();
        private readonly HashSet<string> dead = new HashSet<string>();
        private bool disposed;

        public GameMerge(GameRef game, MergeSettings settings, Func<Field, IList<ISource>> sourcesFor, IMergeObserver observer, IImageReader imageReader = null, SourceHealth health = null)
        {
            this.health = health;
            this.imageReader = imageReader ?? new ImageReader(settings.ImageTimeout() * 1000, settings.ImageProbeKb);
            this.game = game;
            this.settings = settings;
            this.sourcesFor = sourcesFor;
            this.observer = observer ?? NullObserver.Instance;
        }

        /// <summary>The merged values of a field. Never throws because of a source; empty when nothing was found.</summary>
        public List<Item> Get(Field field, CancellationToken cancel)
        {
            lock (gate)
            {
                if (disposed || !settings.IsEnabled(field)) return new List<Item>();
                var sources = sourcesFor(field) ?? new List<ISource>();
                Load(field, cancel);
                if (Fields.IsImage(field)) return ChooseImage(field, sources, cancel);
                var ordered = new List<SourceItems>();
                foreach (var s in sources)
                {
                    Loaded l;
                    List<Item> items;
                    if (!loaded.TryGetValue(s.Id, out l) || !l.Values.TryGetValue(field, out items)) continue;
                    ordered.Add(new SourceItems { SourceId = s.Id, SourceName = s.Name, Items = items });
                }
                return MergeEngine.Merge(ordered, settings.OptionsFor(field));
            }
        }

        // ---- images: the candidates are measured by their file header, one is returned (the others are never downloaded)

        private List<Item> ChooseImage(Field field, IList<ISource> sources, CancellationToken cancel)
        {
            List<Item> chosen;
            if (imageChoices.TryGetValue(field, out chosen)) return chosen;
            var candidates = new List<ImageCandidate>();
            int rank = 0;
            foreach (var s in sources)
            {
                Loaded l;
                List<Item> items;
                if (loaded.TryGetValue(s.Id, out l) && l.Values.TryGetValue(field, out items))
                {
                    var image = items.FirstOrDefault(i => i != null && i.Kind == ItemKind.Image);
                    if (image != null) candidates.Add(new ImageCandidate { SourceId = s.Id, SourceName = s.Name, Rank = rank, Item = image });
                }
                rank++;
            }
            Measure(candidates, cancel);
            var decision = ImageChooser.Choose(field, game.Name, candidates, settings.ImageFor(field));
            try { observer.ImageDecided(decision); } catch { }
            chosen = decision.Winner == null ? new List<Item>() : new List<Item> { decision.Winner.Item };
            imageChoices[field] = chosen;
            return chosen;
        }

        /// <summary>Reads the size of every candidate at the same time, with the time limit; a candidate that fails or is late stays "size unknown".</summary>
        private void Measure(List<ImageCandidate> candidates, CancellationToken cancel)
        {
            if (candidates.Count == 0) return;
            int timeoutMs = settings.ImageTimeout() * 1000;
            var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(timeoutMs);
            var token = limit.Token;
            var tasks = candidates.Select(c => Task.Run(() =>
            {
                try { c.Info = imageReader.Read(c.Item, token); if (c.Info == null) c.Problem = "unreadable"; }
                catch (OperationCanceledException) { c.Problem = "timeout"; }
                catch (Exception ex) { c.Problem = ex.Message; }
            })).ToArray();
            try { Task.WaitAll(tasks, timeoutMs, cancel); }
            catch (OperationCanceledException) { }
            catch (AggregateException) { }
            limit.Cancel();
            foreach (var c in candidates)
                if (c.Info == null && c.Problem == null) c.Problem = "timeout";
            if (tasks.All(t => t.IsCompleted)) limit.Dispose();
        }

        /// <summary>Which sources contributed to the field and how many values each gave (for the log).</summary>
        public string Describe(Field field)
        {
            lock (gate)
            {
                var parts = new List<string>();
                foreach (var s in sourcesFor(field) ?? new List<ISource>())
                {
                    Loaded l;
                    List<Item> items;
                    if (loaded.TryGetValue(s.Id, out l) && l.Values.TryGetValue(field, out items)) parts.Add(s.Name + "=" + items.Count);
                    else if (dead.Contains(s.Id)) parts.Add(s.Name + "=failed");
                }
                return string.Join(", ", parts);
            }
        }

        // ---- the fetch round

        private void Load(Field field, CancellationToken cancel)
        {
            // fields to fetch now: all the enabled ones at the first request (prefetch), else only the requested one
            var wanted = settings.Prefetch ? Fields.All.Where(f => settings.IsEnabled(f)).ToList() : new List<Field> { field };
            if (!wanted.Contains(field)) wanted.Add(field);
            var perSource = new Dictionary<string, Job>();
            foreach (var f in wanted)
            {
                foreach (var s in sourcesFor(f) ?? new List<ISource>())
                {
                    if (dead.Contains(s.Id)) continue;
                    if (health != null && health.IsPaused(s.Id))
                    {
                        // paused after repeated failures: not asked for this game, reported as skipped
                        dead.Add(s.Id);
                        try { observer.Fetched(new SourceReport { SourceId = s.Id, SourceName = s.Name, GameName = game.Name, Outcome = Outcome.Skipped, Error = "paused after repeated failures" }); } catch { }
                        continue;
                    }
                    Loaded l;
                    if (loaded.TryGetValue(s.Id, out l) && l.Fields.Contains(f)) continue;
                    Job job;
                    if (!perSource.TryGetValue(s.Id, out job)) perSource[s.Id] = job = new Job { Source = s, Fields = new List<Field>() };
                    if (!job.Fields.Contains(f)) job.Fields.Add(f);
                }
            }
            if (perSource.Count == 0) return;

            int timeoutMs = settings.Timeout() * 1000;
            var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            {
                limit.CancelAfter(timeoutMs);
                var token = limit.Token;
                var jobs = perSource.Values.ToList();
                foreach (var job in jobs)
                {
                    var j = job;
                    j.Task = Task.Run(() => RunSource(j, token));
                }
                try { Task.WaitAll(jobs.Select(j => (Task)j.Task).ToArray(), timeoutMs, cancel); }
                catch (OperationCanceledException) { }
                catch (AggregateException) { }
                // whatever is still running is abandoned: it cannot hold the others (nor the UI) any longer
                limit.Cancel();
                foreach (var job in jobs) Collect(job, cancel);
                // an abandoned source may still be reading the token: only release it when nobody can
                if (jobs.All(j => j.Task.IsCompleted)) limit.Dispose();
            }
        }

        private SourceReport RunSource(Job job, CancellationToken token)
        {
            var report = new SourceReport { SourceId = job.Source.Id, SourceName = job.Source.Name, GameName = game.Name, Outcome = Outcome.Ok };
            var clock = Stopwatch.StartNew();
            ISourceSession session = null;
            try
            {
                lock (sessions) { sessions.TryGetValue(job.Source.Id, out session); }
                if (session == null)
                {
                    session = job.Source.Open(game);
                    lock (sessions)
                    {
                        if (disposed) { session.Dispose(); session = null; }
                        else sessions[job.Source.Id] = session;
                    }
                }
                if (session == null) { report.Outcome = Outcome.Cancelled; return report; }
                var result = session.Fetch(job.Fields, token);
                foreach (var problem in result.Problems) report.FieldProblems.Add((problem.Field.HasValue ? problem.Field.Value + ": " : "") + problem.Message);
                var values = new Dictionary<Field, List<Item>>();
                foreach (var f in job.Fields)
                {
                    List<Item> items;
                    values[f] = result.Values.TryGetValue(f, out items) && items != null ? items : new List<Item>();
                    report.Counts[f] = values[f].Count;
                }
                job.Values = values;
                if (token.IsCancellationRequested) report.Outcome = Outcome.Timeout;
            }
            catch (OperationCanceledException) { report.Outcome = token.IsCancellationRequested ? Outcome.Timeout : Outcome.Cancelled; }
            catch (Exception ex) { report.Outcome = Outcome.Error; report.Error = ex.GetType().Name + ": " + ex.Message; }
            report.Milliseconds = clock.ElapsedMilliseconds;
            return report;
        }

        private void Collect(Job job, CancellationToken cancel)
        {
            SourceReport report;
            if (job.Task.IsCompleted && !job.Task.IsFaulted && !job.Task.IsCanceled)
            {
                report = job.Task.Result;
            }
            else
            {
                // not finished in time: the source is left out of this game, its session is released when it finally returns
                report = new SourceReport { SourceId = job.Source.Id, SourceName = job.Source.Name, GameName = game.Name, Outcome = cancel.IsCancellationRequested ? Outcome.Cancelled : Outcome.Timeout, Milliseconds = settings.Timeout() * 1000 };
                if (job.Task.IsFaulted) { report.Outcome = Outcome.Error; report.Error = job.Task.Exception.GetBaseException().Message; }
                var id = job.Source.Id;
                job.Task.ContinueWith(t => ReleaseSession(id), TaskContinuationOptions.ExecuteSynchronously);
                job.Values = null;
            }

            bool usable = job.Values != null && report.Outcome == Outcome.Ok;
            Loaded l;
            if (!loaded.TryGetValue(job.Source.Id, out l)) loaded[job.Source.Id] = l = new Loaded();
            foreach (var f in job.Fields) l.Fields.Add(f);   // never asked again for this game, even when it failed
            if (usable)
                foreach (var kv in job.Values) l.Values[kv.Key] = kv.Value;
            else
                dead.Add(job.Source.Id);
            if (health != null)
            {
                if (report.Outcome == Outcome.Ok) health.Success(job.Source.Id);
                else if (report.Outcome == Outcome.Error || report.Outcome == Outcome.Timeout)
                {
                    int minutes = Math.Max(1, settings.BreakerPauseMinutes);
                    if (health.Failure(job.Source.Id, settings.BreakerFailures, TimeSpan.FromMinutes(minutes)))
                        try { observer.SourcePaused(job.Source.Id, job.Source.Name, settings.BreakerFailures, minutes); } catch { }
                }
            }
            try { observer.Fetched(report); } catch { }
        }

        private void ReleaseSession(string id)
        {
            ISourceSession s;
            lock (sessions)
            {
                if (!sessions.TryGetValue(id, out s)) return;
                sessions.Remove(id);
            }
            try { s.Dispose(); } catch { }
        }

        public void Dispose()
        {
            List<ISourceSession> toDispose;
            lock (sessions)
            {
                disposed = true;
                toDispose = sessions.Values.ToList();
                sessions.Clear();
            }
            foreach (var s in toDispose) { try { s.Dispose(); } catch { } }
        }
    }
}
