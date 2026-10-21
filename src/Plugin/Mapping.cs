using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using MetadataMerge.Core;

namespace MetadataMerge
{
    /// <summary>Playnite SDK values to and from the plain items of the core, and the field tables (one place knows which getter is which field).</summary>
    public static class Mapping
    {
        /// <summary>The SDK's own field id for a merged field (the platform, region and age rating ones are singular there).</summary>
        public static MetadataField SdkField(Field f)
        {
            switch (f)
            {
                case Field.Genres: return MetadataField.Genres;
                case Field.Developers: return MetadataField.Developers;
                case Field.Publishers: return MetadataField.Publishers;
                case Field.Features: return MetadataField.Features;
                case Field.Tags: return MetadataField.Tags;
                case Field.Links: return MetadataField.Links;
                case Field.Series: return MetadataField.Series;
                case Field.Platforms: return MetadataField.Platform;
                case Field.Regions: return MetadataField.Region;
                case Field.AgeRatings: return MetadataField.AgeRating;
                case Field.CoverImage: return MetadataField.CoverImage;
                case Field.BackgroundImage: return MetadataField.BackgroundImage;
                case Field.Icon: return MetadataField.Icon;
            }
            throw new ArgumentOutOfRangeException("f");
        }

        private static readonly System.Reflection.MethodInfo TokenSetter =
            typeof(GetMetadataFieldArgs).GetProperty("CancelToken").GetSetMethod(true);

        /// <summary>
        /// The arguments for another provider's getter. The SDK only lets Playnite itself set the cancellation token (its setter is not public),
        /// so it is set through reflection; if that ever stops working the provider just gets a token that is never cancelled.
        /// </summary>
        public static GetMetadataFieldArgs ArgsFor(System.Threading.CancellationToken cancel)
        {
            var args = new GetMetadataFieldArgs();
            try { if (TokenSetter != null) TokenSetter.Invoke(args, new object[] { cancel }); } catch { }
            return args;
        }

        public static List<Item> FromProperties(IEnumerable<MetadataProperty> properties)
        {
            var list = new List<Item>();
            if (properties == null) return list;
            foreach (var p in properties)
            {
                var name = p as MetadataNameProperty;
                if (name != null) { if (!string.IsNullOrWhiteSpace(name.Name)) list.Add(Item.Name(name.Name)); continue; }
                var id = p as MetadataIdProperty;
                if (id != null) { list.Add(Item.Id(id.Id)); continue; }
                var spec = p as MetadataSpecProperty;
                if (spec != null && !string.IsNullOrWhiteSpace(spec.Id)) list.Add(Item.Spec(spec.Id));
            }
            return list;
        }

        /// <summary>An image a source offers: its address or local path and/or its bytes. The source's own file object rides along and is what Playnite gets back.</summary>
        public static List<Item> FromFile(MetadataFile file)
        {
            var list = new List<Item>();
            if (file == null) return list;
            bool hasBytes = file.Content != null && file.Content.Length > 0;
            if (!hasBytes && string.IsNullOrWhiteSpace(file.Path)) return list;
            list.Add(Item.Image(file.Path, hasBytes ? file.Content : null, file.FileName, file));
            return list;
        }

        public static MetadataFile ToFile(IEnumerable<Item> items)
        {
            var item = items.FirstOrDefault(i => i.Kind == ItemKind.Image);
            if (item == null) return null;
            return item.Tag as MetadataFile ?? (item.Content != null ? new MetadataFile(item.FileName ?? "image", item.Content) : new MetadataFile(item.Value));
        }

        public static List<Item> FromLinks(IEnumerable<Link> links)
        {
            var list = new List<Item>();
            if (links == null) return list;
            foreach (var l in links)
                if (l != null && !string.IsNullOrWhiteSpace(l.Url)) list.Add(Item.Link(l.Name, l.Url));
            return list;
        }

        public static List<MetadataProperty> ToProperties(IEnumerable<Item> items)
        {
            var list = new List<MetadataProperty>();
            foreach (var i in items)
            {
                switch (i.Kind)
                {
                    case ItemKind.Name: list.Add(new MetadataNameProperty(i.Value)); break;
                    case ItemKind.Id: list.Add(new MetadataIdProperty(Guid.Parse(i.Value))); break;
                    case ItemKind.Spec: list.Add(new MetadataSpecProperty(i.Value)); break;
                }
            }
            return list;
        }

        public static List<Link> ToLinks(IEnumerable<Item> items)
        {
            return items.Where(i => i.Kind == ItemKind.Link).Select(i => new Link(i.Label ?? "", i.Value)).ToList();
        }

        /// <summary>What a plugin's provider answers for a field, through the getter of that field.</summary>
        public static List<Item> Ask(OnDemandMetadataProvider provider, Field f, GetMetadataFieldArgs args)
        {
            switch (f)
            {
                case Field.Genres: return FromProperties(provider.GetGenres(args));
                case Field.Developers: return FromProperties(provider.GetDevelopers(args));
                case Field.Publishers: return FromProperties(provider.GetPublishers(args));
                case Field.Features: return FromProperties(provider.GetFeatures(args));
                case Field.Tags: return FromProperties(provider.GetTags(args));
                case Field.Links: return FromLinks(provider.GetLinks(args));
                case Field.Series: return FromProperties(provider.GetSeries(args));
                case Field.Platforms: return FromProperties(provider.GetPlatforms(args));
                case Field.Regions: return FromProperties(provider.GetRegions(args));
                case Field.AgeRatings: return FromProperties(provider.GetAgeRatings(args));
                case Field.CoverImage: return FromFile(provider.GetCoverImage(args));
                case Field.BackgroundImage: return FromFile(provider.GetBackgroundImage(args));
                case Field.Icon: return FromFile(provider.GetIcon(args));
            }
            return new List<Item>();
        }

        /// <summary>What a library's own metadata (the "official store") holds for a field.</summary>
        public static List<Item> FromStore(GameMetadata data, Field f)
        {
            if (data == null) return new List<Item>();
            switch (f)
            {
                case Field.Genres: return FromProperties(data.Genres);
                case Field.Developers: return FromProperties(data.Developers);
                case Field.Publishers: return FromProperties(data.Publishers);
                case Field.Features: return FromProperties(data.Features);
                case Field.Tags: return FromProperties(data.Tags);
                case Field.Links: return FromLinks(data.Links);
                case Field.Series: return FromProperties(data.Series);
                case Field.Platforms: return FromProperties(data.Platforms);
                case Field.Regions: return FromProperties(data.Regions);
                case Field.AgeRatings: return FromProperties(data.AgeRatings);
                case Field.CoverImage: return FromFile(data.CoverImage);
                case Field.BackgroundImage: return FromFile(data.BackgroundImage);
                case Field.Icon: return FromFile(data.Icon);
            }
            return new List<Item>();
        }
    }
}
