using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MetadataMerge.Core;

/// <summary>A source with canned values, optional delay, failures and call counters.</summary>
public sealed class FakeSource : ISource
{
    public string Id { get; private set; }
    public string Name { get; private set; }
    public readonly Dictionary<Field, Item[]> Data = new Dictionary<Field, Item[]>();
    public int DelayMs;
    public bool ThrowOnOpen;
    public bool ThrowOnFetch;
    public bool IgnoreCancel;
    public Field? ProblemField;
    public int Opens;
    public int Fetches;
    public int Disposes;
    public readonly List<string> FetchedFields = new List<string>();
    private readonly object gate = new object();

    public FakeSource(string name, string id = null) { Name = name; Id = id ?? Guid.NewGuid().ToString("D"); }

    public FakeSource With(Field f, params string[] names) { Data[f] = names.Select(Item.Name).ToArray(); return this; }
    public FakeSource WithLinks(params string[] pairs)
    {
        var list = new List<Item>();
        for (int i = 0; i + 1 < pairs.Length; i += 2) list.Add(Item.Link(pairs[i], pairs[i + 1]));
        Data[Field.Links] = list.ToArray();
        return this;
    }

    public FakeSource WithImage(Field f, string url)
    {
        Data[f] = new[] { Item.Image(url, null, null, "tag:" + url) };
        return this;
    }

    public ISourceSession Open(GameRef game)
    {
        lock (gate) Opens++;
        if (ThrowOnOpen) throw new InvalidOperationException("open failed");
        return new Session(this);
    }

    private sealed class Session : ISourceSession
    {
        private readonly FakeSource owner;
        public Session(FakeSource owner) { this.owner = owner; }

        public FetchResult Fetch(ICollection<Field> fields, CancellationToken cancel)
        {
            lock (owner.gate) { owner.Fetches++; owner.FetchedFields.Add(string.Join("+", fields.OrderBy(f => f).Select(f => f.ToString()))); }
            if (owner.DelayMs > 0)
            {
                if (owner.IgnoreCancel) Thread.Sleep(owner.DelayMs);
                else cancel.WaitHandle.WaitOne(owner.DelayMs);
            }
            if (owner.ThrowOnFetch) throw new InvalidOperationException("fetch failed");
            var result = new FetchResult();
            foreach (var f in fields)
            {
                if (owner.ProblemField == f) { result.Problems.Add(new FetchProblem { Field = f, Message = "getter failed" }); continue; }
                Item[] items;
                if (owner.Data.TryGetValue(f, out items)) result.Values[f] = items.ToList();
            }
            return result;
        }

        public void Dispose() { lock (owner.gate) owner.Disposes++; }
    }
}

public sealed class RecordingObserver : IMergeObserver
{
    public readonly List<SourceReport> Reports = new List<SourceReport>();
    public readonly List<string> Missing = new List<string>();
    public void Fetched(SourceReport report) { lock (Reports) Reports.Add(report); }
    public void MissingSource(string sourceId) { lock (Missing) Missing.Add(sourceId); }
    public readonly List<ImageDecision> Images = new List<ImageDecision>();
    public void ImageDecided(ImageDecision d) { lock (Images) Images.Add(d); }
    public readonly List<string> Paused = new List<string>();
    public void SourcePaused(string sourceId, string sourceName, int failures, int minutes) { lock (Paused) Paused.Add(sourceName); }
}

public static class Fx
{
    public static List<string> Names(IEnumerable<Item> items) { return items.Select(i => i.Value).ToList(); }

    public static string Join(IEnumerable<Item> items) { return string.Join("|", Names(items)); }

    public static GameMerge Merge(MergeSettings settings, Func<Field, IList<ISource>> sources, IMergeObserver observer = null)
    {
        return new GameMerge(new GameRef { Id = Guid.NewGuid(), Name = "Test Game" }, settings, sources, observer);
    }

    public static GameMerge Merge(MergeSettings settings, IMergeObserver observer, params FakeSource[] sources)
    {
        var list = sources.Cast<ISource>().ToList();
        return Merge(settings, f => list, observer);
    }
}

/// <summary>An image reader with canned sizes per address (or a failure, or a long wait).</summary>
public sealed class FakeReader : IImageReader
{
    public readonly Dictionary<string, ImageInfo> Sizes = new Dictionary<string, ImageInfo>();
    public readonly HashSet<string> Fails = new HashSet<string>();
    public readonly Dictionary<string, int> DelaysMs = new Dictionary<string, int>();
    public readonly List<string> Reads = new List<string>();

    public FakeReader Size(string url, int w, int h, ImageFormat f = ImageFormat.Jpeg)
    {
        Sizes[url] = new ImageInfo { Width = w, Height = h, Format = f };
        return this;
    }

    public ImageInfo Read(Item image, CancellationToken cancel)
    {
        lock (Reads) Reads.Add(image.Value);
        int delay;
        if (DelaysMs.TryGetValue(image.Value, out delay)) cancel.WaitHandle.WaitOne(delay);
        cancel.ThrowIfCancellationRequested();
        if (Fails.Contains(image.Value)) throw new System.IO.InvalidDataException("not an image");
        ImageInfo info;
        return Sizes.TryGetValue(image.Value, out info) ? info : null;
    }
}
