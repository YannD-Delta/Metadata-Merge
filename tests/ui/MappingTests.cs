using System;
using System.Collections.Generic;
using System.Linq;
using MetadataMerge;
using MetadataMerge.Core;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

public static class MappingTests
{
    public static void Properties_round_trip_through_items()
    {
        var id = Guid.NewGuid();
        var props = new MetadataProperty[] { new MetadataNameProperty("Action"), new MetadataIdProperty(id), new MetadataSpecProperty("pc_windows") };
        var items = Mapping.FromProperties(props);
        Assert.Equal(3, items.Count);
        var back = Mapping.ToProperties(items);
        Assert.Equal("Action", ((MetadataNameProperty)back[0]).Name);
        Assert.Equal(id, ((MetadataIdProperty)back[1]).Id);
        Assert.Equal("pc_windows", ((MetadataSpecProperty)back[2]).Id);
    }

    public static void Blank_names_and_nulls_are_dropped()
    {
        var items = Mapping.FromProperties(new MetadataProperty[] { null, new MetadataNameProperty("ok") });
        Assert.Equal("ok", Fx.Join(items));
        Assert.Equal(0, Mapping.FromProperties(null).Count);
        Assert.Equal(0, Mapping.FromLinks(null).Count);
    }

    public static void Links_round_trip_and_blank_addresses_are_dropped()
    {
        var items = Mapping.FromLinks(new[] { new Link("Wiki", "https://w.example.com"), new Link("Empty", ""), null });
        Assert.Equal(1, items.Count);
        var back = Mapping.ToLinks(items);
        Assert.Equal("Wiki", back[0].Name);
        Assert.Equal("https://w.example.com", back[0].Url);
    }

    public static void Every_field_has_its_own_sdk_field_and_a_config_name()
    {
        var sdk = new HashSet<MetadataField>();
        var cfg = new HashSet<string>();
        foreach (var f in Fields.All) { Assert.True(sdk.Add(Mapping.SdkField(f)), f.ToString()); Assert.True(cfg.Add(Fields.ConfigName(f)), f.ToString()); }
        Assert.Equal(13, sdk.Count);
    }

    public static void Image_files_map_to_candidates_and_back_with_the_sources_own_object()
    {
        var byUrl = new MetadataFile("https://img.example.com/cover.jpg");
        var items = Mapping.FromFile(byUrl);
        Assert.Equal(1, items.Count);
        Assert.Equal(ItemKind.Image, items[0].Kind);
        Assert.Equal("https://img.example.com/cover.jpg", items[0].Value);
        Assert.True(ReferenceEquals(Mapping.ToFile(items), byUrl));
        var inMemory = new MetadataFile("c.png", new byte[] { 1, 2, 3 });
        var memItems = Mapping.FromFile(inMemory);
        Assert.Equal(3, memItems[0].Content.Length);
        Assert.True(ReferenceEquals(Mapping.ToFile(memItems), inMemory));
        Assert.Equal(0, Mapping.FromFile(null).Count);
        Assert.Equal(0, Mapping.FromFile(new MetadataFile()).Count);
        Assert.True(Mapping.ToFile(new List<Item>()) == null);
        Assert.Equal("https://x.example.com/a.png", Mapping.ToFile(new[] { Item.Image("https://x.example.com/a.png") }).Path);
    }

    public static void The_token_setter_is_reachable_so_providers_see_the_timeout()
    {
        var cts = new System.Threading.CancellationTokenSource();
        var args = Mapping.ArgsFor(cts.Token);
        cts.Cancel();
        Assert.True(args.CancelToken.IsCancellationRequested);
    }

    public static void Store_metadata_maps_every_field()
    {
        var gm = new GameMetadata
        {
            Genres = new HashSet<MetadataProperty> { new MetadataNameProperty("g") },
            Developers = new HashSet<MetadataProperty> { new MetadataNameProperty("d") },
            Publishers = new HashSet<MetadataProperty> { new MetadataNameProperty("p") },
            Features = new HashSet<MetadataProperty> { new MetadataNameProperty("f") },
            Tags = new HashSet<MetadataProperty> { new MetadataNameProperty("t") },
            Series = new HashSet<MetadataProperty> { new MetadataNameProperty("s") },
            Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") },
            Regions = new HashSet<MetadataProperty> { new MetadataNameProperty("r") },
            AgeRatings = new HashSet<MetadataProperty> { new MetadataNameProperty("a") },
            Links = new List<Link> { new Link("l", "https://l.example.com") },
            CoverImage = new MetadataFile("https://c.example.com/c.jpg"),
            BackgroundImage = new MetadataFile("https://c.example.com/b.jpg"),
            Icon = new MetadataFile("https://c.example.com/i.png")
        };
        foreach (var f in Fields.All) Assert.Equal(1, Mapping.FromStore(gm, f).Count, f.ToString());
        Assert.Equal(0, Mapping.FromStore(null, Field.Genres).Count);
        Assert.Equal(0, Mapping.FromStore(new GameMetadata(), Field.Tags).Count);
    }
}
