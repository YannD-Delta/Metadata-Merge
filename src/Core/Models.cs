using System;
using System.Collections.Generic;
using System.Linq;

namespace MetadataMerge.Core
{
    /// <summary>The metadata fields the extension can merge. The order is the order of the settings page.</summary>
    public enum Field { Genres, Developers, Publishers, Features, Tags, Links, Series, Platforms, Regions, AgeRatings, CoverImage, BackgroundImage, Icon }

    /// <summary>Union: every source adds its values. FirstNonEmpty: the first source (in priority order) that has a value wins, like Playnite itself.</summary>
    public enum MergeMode { Union = 0, FirstNonEmpty = 1 }

    /// <summary>Where the sources of a field come from: the list Playnite has for that field (its config.json), or the list kept in the extension settings.</summary>
    public enum SourceMode { Playnite = 0, Custom = 1 }

    /// <summary>The shapes a Playnite metadata value can have: a name, a database id, a platform specification id, a link (name and address) or an image (a file the source offers).</summary>
    public enum ItemKind { Name, Id, Spec, Link, Image }

    /// <summary>One metadata value, independent of the Playnite SDK types so the merge logic can be tested alone.</summary>
    public sealed class Item
    {
        public ItemKind Kind;
        /// <summary>The name, the database id (as text), the platform specification id, or the link address.</summary>
        public string Value;
        /// <summary>The link name (links only).</summary>
        public string Label;
        /// <summary>Images: the address or local path the source gave (also in Value), the bytes when the source gave them, and the file name.</summary>
        public byte[] Content;
        public string FileName;
        /// <summary>The source's own object, given back untouched to Playnite (images: the metadata file).</summary>
        public object Tag;

        public static Item Name(string name) { return new Item { Kind = ItemKind.Name, Value = name }; }
        public static Item Id(Guid id) { return new Item { Kind = ItemKind.Id, Value = id.ToString("D") }; }
        public static Item Spec(string id) { return new Item { Kind = ItemKind.Spec, Value = id }; }
        public static Item Link(string name, string url) { return new Item { Kind = ItemKind.Link, Value = url, Label = name }; }
        /// <summary>An image candidate: an address or local path, and/or the bytes.</summary>
        public static Item Image(string pathOrUrl, byte[] content = null, string fileName = null, object tag = null)
        {
            return new Item { Kind = ItemKind.Image, Value = pathOrUrl, Content = content, FileName = fileName, Tag = tag };
        }

        public override string ToString()
        {
            if (Kind == ItemKind.Image) return "image " + (Value ?? FileName ?? (Content != null ? Content.Length + " bytes" : ""));
            return Kind == ItemKind.Link ? "link " + Label + " " + Value : Kind.ToString().ToLowerInvariant() + " " + Value;
        }
    }

    public static class Fields
    {
        public static readonly Field[] All = (Field[])Enum.GetValues(typeof(Field));

        /// <summary>The property name Playnite uses for the field in config.json, MetadataSettings.</summary>
        public static string ConfigName(Field f)
        {
            switch (f)
            {
                case Field.Genres: return "Genre";
                case Field.Developers: return "Developer";
                case Field.Publishers: return "Publisher";
                case Field.Features: return "Feature";
                case Field.Tags: return "Tag";
                case Field.Links: return "Links";
                case Field.Series: return "Series";
                case Field.Platforms: return "Platform";
                case Field.Regions: return "Region";
                case Field.AgeRatings: return "AgeRating";
                case Field.CoverImage: return "CoverImage";
                case Field.BackgroundImage: return "BackgroundImage";
                case Field.Icon: return "Icon";
            }
            throw new ArgumentOutOfRangeException("f");
        }

        public static bool IsCompany(Field f) { return f == Field.Developers || f == Field.Publishers; }

        public static bool IsImage(Field f) { return f == Field.CoverImage || f == Field.BackgroundImage || f == Field.Icon; }

        /// <summary>The list fields the owner asked for are on by default. Regions, age ratings and the image fields are opt-in.</summary>
        public static bool DefaultEnabled(Field f) { return f != Field.Regions && f != Field.AgeRatings && !IsImage(f); }

        /// <summary>The width / height an image of the field should have: cover about 2:3 portrait, background 16:9, icon square.</summary>
        public static double ExpectedAspect(Field f)
        {
            switch (f)
            {
                case Field.CoverImage: return 2.0 / 3.0;
                case Field.BackgroundImage: return 16.0 / 9.0;
                case Field.Icon: return 1.0;
            }
            return 1.0;
        }

        public static bool TryParse(string name, out Field f) { return Enum.TryParse(name, true, out f); }
    }

    /// <summary>The game being processed, with an opaque handle the Playnite glue gives back to its own sources.</summary>
    public sealed class GameRef
    {
        public Guid Id;
        public string Name;
        public object Tag;
        public override string ToString() { return Name ?? Id.ToString(); }
    }

    public sealed class FetchProblem
    {
        public Field? Field;
        public string Message;
    }

    /// <summary>What one source answered for one game: the values per field, and the problems met on the way (a field that failed does not discard the others).</summary>
    public sealed class FetchResult
    {
        public readonly Dictionary<Field, List<Item>> Values = new Dictionary<Field, List<Item>>();
        public readonly List<FetchProblem> Problems = new List<FetchProblem>();
    }

    /// <summary>A metadata source (a Playnite metadata plugin or the library's own store). Cheap to hold; the work happens in the session.</summary>
    public interface ISource
    {
        string Id { get; }
        string Name { get; }
        /// <summary>Opens a session for one game. May be slow (it can create the plugin's provider); it is called on a worker thread.</summary>
        ISourceSession Open(GameRef game);
    }

    public interface ISourceSession : IDisposable
    {
        /// <summary>Asks the source for these fields. Never needs to throw for a single field: put it in the result Problems.</summary>
        FetchResult Fetch(ICollection<Field> fields, System.Threading.CancellationToken cancel);
    }

    public enum Outcome { Ok, Error, Timeout, Cancelled, Skipped }

    /// <summary>What was decided for an image field of one game: the candidates with what was measured, the winner and why.</summary>
    public sealed class ImageDecision
    {
        public Field Field;
        public string GameName;
        public ImageCandidate Winner;
        public string Reason;
        public List<ImageCandidate> Candidates = new List<ImageCandidate>();

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(GameName).Append(" ").Append(Field).Append(": ")
              .Append(Winner == null ? "none" : Winner.SourceName + " " + Winner.Describe()).Append(" - ").Append(Reason);
            foreach (var c in Candidates) sb.Append(" | ").Append(c.SourceName).Append(" ").Append(c.Describe()).Append(": ").Append(c.Verdict);
            return sb.ToString();
        }
    }

    /// <summary>What happened with one source for one game, for the statistics, the log and the notification.</summary>
    public sealed class SourceReport
    {
        public string SourceId;
        public string SourceName;
        public string GameName;
        public Outcome Outcome;
        public long Milliseconds;
        public string Error;
        public List<string> FieldProblems = new List<string>();
        public Dictionary<Field, int> Counts = new Dictionary<Field, int>();
    }

    /// <summary>Receives what happens during a merge. The Playnite glue logs it and keeps the statistics.</summary>
    public interface IMergeObserver
    {
        void Fetched(SourceReport report);
        /// <summary>A configured source is not installed (or has no metadata to give).</summary>
        void MissingSource(string sourceId);
        /// <summary>An image field was decided (candidates measured by their file header, one winner).</summary>
        void ImageDecided(ImageDecision decision);
        /// <summary>A source failed too many games in a row and is paused for a while.</summary>
        void SourcePaused(string sourceId, string sourceName, int failures, int minutes);
    }

    public sealed class NullObserver : IMergeObserver
    {
        public static readonly NullObserver Instance = new NullObserver();
        public void Fetched(SourceReport report) { }
        public void MissingSource(string sourceId) { }
        public void ImageDecided(ImageDecision decision) { }
        public void SourcePaused(string sourceId, string sourceName, int failures, int minutes) { }
    }
}
