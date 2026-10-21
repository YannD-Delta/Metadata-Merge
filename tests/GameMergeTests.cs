using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using MetadataMerge.Core;

public static class GameMergeTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static MergeSettings Settings(int timeout = 10, bool prefetch = true)
    {
        return new MergeSettings { TimeoutSeconds = timeout, Prefetch = prefetch };
    }

    public static void UnionOfSeveralSourcesInPriorityOrder()
    {
        var a = new FakeSource("A").With(Field.Genres, "Action", "RPG");
        var b = new FakeSource("B").With(Field.Genres, "rpg", "Indie");
        using (var m = Fx.Merge(Settings(), null, a, b))
            Assert.Equal("Action|RPG|Indie", Fx.Join(m.Get(Field.Genres, None)));
    }

    public static void EachSourceIsOpenedAndAskedOncePerGameForAllTheFields()
    {
        var a = new FakeSource("A").With(Field.Genres, "g").With(Field.Tags, "t").With(Field.Developers, "d");
        var b = new FakeSource("B").With(Field.Genres, "h");
        using (var m = Fx.Merge(Settings(), null, a, b))
        {
            foreach (var f in new[] { Field.Genres, Field.Tags, Field.Developers, Field.Features, Field.Links, Field.Series })
                m.Get(f, None);
            m.Get(Field.Genres, None);
        }
        Assert.Equal(1, a.Opens); Assert.Equal(1, a.Fetches);
        Assert.Equal(1, b.Opens); Assert.Equal(1, b.Fetches);
        Assert.Equal(1, a.Disposes); Assert.Equal(1, b.Disposes);
        // regions and age ratings are off by default: not asked
        Assert.True(!a.FetchedFields[0].Contains("Regions") && !a.FetchedFields[0].Contains("AgeRatings"), a.FetchedFields[0]);
    }

    public static void WithoutPrefetchEachFieldIsOneRoundButTheSessionIsKept()
    {
        var a = new FakeSource("A").With(Field.Genres, "g").With(Field.Tags, "t");
        using (var m = Fx.Merge(Settings(prefetch: false), null, a))
        {
            Assert.Equal("g", Fx.Join(m.Get(Field.Genres, None)));
            Assert.Equal("t", Fx.Join(m.Get(Field.Tags, None)));
            m.Get(Field.Tags, None);
        }
        Assert.Equal(1, a.Opens);
        Assert.Equal(2, a.Fetches);
        Assert.Equal("Genres", a.FetchedFields[0]);
        Assert.Equal("Tags", a.FetchedFields[1]);
    }

    public static void SourcesAreAskedInParallel()
    {
        var sources = Enumerable.Range(1, 4).Select(i => new FakeSource("S" + i) { DelayMs = 500 }.With(Field.Genres, "g" + i)).ToArray();
        var clock = Stopwatch.StartNew();
        using (var m = Fx.Merge(Settings(), null, sources))
            Assert.Equal("g1|g2|g3|g4", Fx.Join(m.Get(Field.Genres, None)));
        Assert.True(clock.ElapsedMilliseconds < 1500, "took " + clock.ElapsedMilliseconds + " ms, four 500 ms sources in parallel");
    }

    public static void ASlowSourceIsLeftOutAndTheOthersAreKept()
    {
        var slow = new FakeSource("Slow") { DelayMs = 20000 }.With(Field.Genres, "slow");
        var fast = new FakeSource("Fast").With(Field.Genres, "fast");
        var obs = new RecordingObserver();
        var clock = Stopwatch.StartNew();
        using (var m = Fx.Merge(Settings(timeout: 1), obs, slow, fast))
            Assert.Equal("fast", Fx.Join(m.Get(Field.Genres, None)));
        Assert.True(clock.ElapsedMilliseconds < 5000, "took " + clock.ElapsedMilliseconds + " ms");
        Assert.Equal(Outcome.Timeout, obs.Reports.Single(r => r.SourceName == "Slow").Outcome);
        Assert.Equal(Outcome.Ok, obs.Reports.Single(r => r.SourceName == "Fast").Outcome);
    }

    public static void ASourceThatIgnoresCancellationCannotHoldTheOthers()
    {
        var stuck = new FakeSource("Stuck") { DelayMs = 4000, IgnoreCancel = true }.With(Field.Genres, "stuck");
        var fast = new FakeSource("Fast").With(Field.Genres, "fast");
        var clock = Stopwatch.StartNew();
        using (var m = Fx.Merge(Settings(timeout: 1), null, stuck, fast))
            Assert.Equal("fast", Fx.Join(m.Get(Field.Genres, None)));
        Assert.True(clock.ElapsedMilliseconds < 3000, "took " + clock.ElapsedMilliseconds + " ms");
    }

    public static void ALateSourcesSessionIsReleasedWhenItFinallyReturns()
    {
        var late = new FakeSource("Late") { DelayMs = 1500, IgnoreCancel = true }.With(Field.Genres, "x");
        using (var m = Fx.Merge(Settings(timeout: 1), null, late))
            Assert.Equal("", Fx.Join(m.Get(Field.Genres, None)));
        for (int i = 0; i < 40 && late.Disposes == 0; i++) Thread.Sleep(100);
        Assert.Equal(1, late.Disposes);
    }

    public static void AFailingSourceNeverBreaksTheOthers()
    {
        var bad = new FakeSource("Bad") { ThrowOnFetch = true };
        var bad2 = new FakeSource("BadOpen") { ThrowOnOpen = true };
        var good = new FakeSource("Good").With(Field.Tags, "t");
        var obs = new RecordingObserver();
        using (var m = Fx.Merge(Settings(), obs, bad, bad2, good))
            Assert.Equal("t", Fx.Join(m.Get(Field.Tags, None)));
        Assert.Equal(Outcome.Error, obs.Reports.Single(r => r.SourceName == "Bad").Outcome);
        Assert.True(obs.Reports.Single(r => r.SourceName == "Bad").Error.Contains("fetch failed"));
        Assert.Equal(Outcome.Error, obs.Reports.Single(r => r.SourceName == "BadOpen").Outcome);
        Assert.Equal(Outcome.Ok, obs.Reports.Single(r => r.SourceName == "Good").Outcome);
    }

    public static void AFieldThatFailsInASourceKeepsTheOtherFieldsOfThatSource()
    {
        var a = new FakeSource("A") { ProblemField = Field.Genres }.With(Field.Genres, "g").With(Field.Tags, "t");
        var obs = new RecordingObserver();
        using (var m = Fx.Merge(Settings(), obs, a))
        {
            Assert.Equal("", Fx.Join(m.Get(Field.Genres, None)));
            Assert.Equal("t", Fx.Join(m.Get(Field.Tags, None)));
        }
        Assert.True(obs.Reports[0].FieldProblems.Single().Contains("getter failed"));
    }

    public static void ADeadSourceIsNotAskedAgainForTheSameGame()
    {
        var bad = new FakeSource("Bad") { ThrowOnFetch = true };
        using (var m = Fx.Merge(Settings(prefetch: false), null, bad))
        {
            m.Get(Field.Genres, None);
            m.Get(Field.Tags, None);
            m.Get(Field.Features, None);
        }
        Assert.Equal(1, bad.Fetches);
    }

    public static void ADisabledFieldAsksNobody()
    {
        var a = new FakeSource("A").With(Field.Genres, "g");
        var s = Settings();
        s.For(Field.Genres).Enabled = false;
        using (var m = Fx.Merge(s, null, a))
            Assert.Equal("", Fx.Join(m.Get(Field.Genres, None)));
        Assert.Equal(0, a.Fetches);
    }

    public static void FirstNonEmptyModeStillUsesTheSourcesInPriorityOrder()
    {
        var a = new FakeSource("A");
        var b = new FakeSource("B").With(Field.Series, "second");
        var c = new FakeSource("C").With(Field.Series, "third");
        var s = Settings();
        s.For(Field.Series).Mode = MergeMode.FirstNonEmpty;
        using (var m = Fx.Merge(s, null, a, b, c))
            Assert.Equal("second", Fx.Join(m.Get(Field.Series, None)));
    }

    public static void EachFieldUsesItsOwnSourceList()
    {
        var a = new FakeSource("A").With(Field.Tags, "ta").With(Field.Genres, "ga");
        var b = new FakeSource("B").With(Field.Tags, "tb").With(Field.Genres, "gb");
        Func<Field, IList<ISource>> lists = f => f == Field.Tags ? new List<ISource> { a } : new List<ISource> { a, b };
        using (var m = Fx.Merge(Settings(), lists))
        {
            Assert.Equal("ta", Fx.Join(m.Get(Field.Tags, None)));
            Assert.Equal("ga|gb", Fx.Join(m.Get(Field.Genres, None)));
        }
        Assert.Equal(1, b.Opens);
    }

    public static void ReportsCarryTheCountsPerField()
    {
        var a = new FakeSource("A").With(Field.Genres, "g1", "g2").With(Field.Tags, "t");
        var obs = new RecordingObserver();
        using (var m = Fx.Merge(Settings(), obs, a))
            m.Get(Field.Genres, None);
        var r = obs.Reports.Single();
        Assert.Equal(2, r.Counts[Field.Genres]);
        Assert.Equal(1, r.Counts[Field.Tags]);
        Assert.Equal("Test Game", r.GameName);
    }

    public static void CancellingStopsTheWaitAndReturnsWhatIsKnown()
    {
        var slow = new FakeSource("Slow") { DelayMs = 20000 }.With(Field.Genres, "slow");
        var fast = new FakeSource("Fast").With(Field.Genres, "fast");
        var cts = new CancellationTokenSource();
        cts.CancelAfter(300);
        var clock = Stopwatch.StartNew();
        using (var m = Fx.Merge(Settings(timeout: 60), null, slow, fast))
            Assert.Equal("fast", Fx.Join(m.Get(Field.Genres, cts.Token)));
        Assert.True(clock.ElapsedMilliseconds < 5000, "took " + clock.ElapsedMilliseconds + " ms");
    }

    public static void SynonymsAndCapsApplyToTheMergedResult()
    {
        var a = new FakeSource("A").With(Field.Genres, "Role-playing (RPG)", "Action", "Indie");
        var b = new FakeSource("B").With(Field.Genres, "RPG", "Puzzle");
        var s = Settings();
        var fs = s.For(Field.Genres);
        fs.Synonyms = "Role-playing (RPG)=RPG";
        fs.TotalCap = 3;
        using (var m = Fx.Merge(s, null, a, b))
            Assert.Equal("RPG|Action|Indie", Fx.Join(m.Get(Field.Genres, None)));
    }

    public static void ADisposedMergeAnswersNothing()
    {
        var a = new FakeSource("A").With(Field.Genres, "g");
        var m = Fx.Merge(Settings(), null, a);
        m.Dispose();
        Assert.Equal(0, m.Get(Field.Genres, None).Count);
        Assert.Equal(0, a.Opens);
    }

    public static void ManyGamesInParallelDoNotShareState()
    {
        var a = new FakeSource("A").With(Field.Genres, "g").With(Field.Tags, "t");
        var failures = new List<string>();
        var threads = Enumerable.Range(0, 16).Select(i => new Thread(() =>
        {
            try
            {
                using (var m = Fx.Merge(Settings(), null, a))
                    if (Fx.Join(m.Get(Field.Genres, None)) != "g" || Fx.Join(m.Get(Field.Tags, None)) != "t") lock (failures) failures.Add("wrong answer");
            }
            catch (Exception ex) { lock (failures) failures.Add(ex.Message); }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.Equal(0, failures.Count, string.Join(";", failures));
        Assert.Equal(16, a.Opens);
        Assert.Equal(16, a.Disposes);
    }
}
