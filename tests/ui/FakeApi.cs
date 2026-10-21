using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

/// <summary>
/// A stand-in for the parts of IPlayniteAPI the extension uses (Addons.Plugins, Paths, Notifications, MainView), built with RealProxy because
/// the SDK interfaces are large. Anything else throws, so a new dependency on the API shows up in a test.
/// </summary>
public static class FakeApi
{
    public sealed class Rig
    {
        public readonly List<Plugin> Plugins = new List<Plugin>();
        public readonly string ConfigDir = Path.Combine(Path.GetTempPath(), "mm-cfg-" + Guid.NewGuid().ToString("N"));
        public readonly string DataDir = Path.Combine(Path.GetTempPath(), "mm-data-" + Guid.NewGuid().ToString("N"));
        public readonly List<NotificationMessage> Notifications = new List<NotificationMessage>();
        public readonly List<string> Removed = new List<string>();
        public readonly List<Guid> OpenedSettings = new List<Guid>();

        public Rig()
        {
            Directory.CreateDirectory(ConfigDir);
            Directory.CreateDirectory(DataDir);
        }

        public string ConfigFile { get { return Path.Combine(ConfigDir, "config.json"); } }
        public void WriteConfig(string json) { File.WriteAllText(ConfigFile, json); File.SetLastWriteTimeUtc(ConfigFile, DateTime.UtcNow.AddSeconds(++stamp)); }
        private int stamp;

        public void Cleanup()
        {
            foreach (var d in new[] { ConfigDir, DataDir }) { try { Directory.Delete(d, true); } catch { } }
        }

        public IPlayniteAPI Api()
        {
            var addons = Make<IAddons>(call => call.MethodName == "get_Plugins" ? (object)Plugins : null);
            var paths = Make<IPlaynitePathsAPI>(call =>
            {
                if (call.MethodName == "get_ConfigurationPath") return ConfigDir;
                if (call.MethodName == "get_ExtensionsDataPath") return DataDir;
                return null;
            });
            var notifications = Make<INotificationsAPI>(call =>
            {
                lock (Notifications)
                {
                    if (call.MethodName == "Add" && call.Args[0] is NotificationMessage) Notifications.Add((NotificationMessage)call.Args[0]);
                    if (call.MethodName == "Remove") { Removed.Add((string)call.Args[0]); Notifications.RemoveAll(n => n.Id == (string)call.Args[0]); }
                }
                return null;
            });
            var main = Make<IMainViewAPI>(call =>
            {
                if (call.MethodName == "OpenPluginSettings") { OpenedSettings.Add((Guid)call.Args[0]); return true; }
                return null;
            });
            return Make<IPlayniteAPI>(call =>
            {
                switch (call.MethodName)
                {
                    case "get_Addons": return addons;
                    case "get_Paths": return paths;
                    case "get_Notifications": return notifications;
                    case "get_MainView": return main;
                }
                throw new NotSupportedException("FakeApi: " + call.MethodName);
            });
        }
    }

    private sealed class Proxy : RealProxy
    {
        private readonly Func<IMethodCallMessage, object> handler;
        public Proxy(Type iface, Func<IMethodCallMessage, object> handler) : base(iface) { this.handler = handler; }

        public override IMessage Invoke(IMessage msg)
        {
            var call = (IMethodCallMessage)msg;
            object result = handler(call);
            var ret = ((MethodInfo)call.MethodBase).ReturnType;
            if (result == null && ret.IsValueType && ret != typeof(void)) result = Activator.CreateInstance(ret);
            return new ReturnMessage(result, null, 0, call.LogicalCallContext, call);
        }
    }

    private static T Make<T>(Func<IMethodCallMessage, object> handler)
    {
        return (T)new Proxy(typeof(T), handler).GetTransparentProxy();
    }
}

/// <summary>A metadata plugin of the real SDK base class with canned answers, a call log and a few ways to misbehave.</summary>
public class FakeMeta : MetadataPlugin
{
    private readonly Guid id;
    private readonly string name;
    public readonly Dictionary<MetadataField, List<MetadataProperty>> Props = new Dictionary<MetadataField, List<MetadataProperty>>();
    public List<Link> Links;
    public readonly Dictionary<MetadataField, MetadataFile> Files = new Dictionary<MetadataField, MetadataFile>();
    public readonly HashSet<MetadataField> Throws = new HashSet<MetadataField>();
    public int DelayMs;
    public bool IgnoreCancel;
    public bool ThrowOnCreate;
    public int Created;
    public int Disposed;
    public bool LastRequestWasBackground;
    public readonly List<string> Calls = new List<string>();
    public bool SawCancelledToken;

    public FakeMeta(string name, Guid? id = null) : base(null)
    {
        this.name = name;
        this.id = id ?? Guid.NewGuid();
    }

    public override Guid Id { get { return id; } }
    public override string Name { get { return name; } }
    public override List<MetadataField> SupportedFields { get { return Props.Keys.Concat(Files.Keys).Concat(Links != null ? new[] { MetadataField.Links } : new MetadataField[0]).Distinct().ToList(); } }

    public FakeMeta Names(MetadataField f, params string[] names)
    {
        Props[f] = names.Select(n => (MetadataProperty)new MetadataNameProperty(n)).ToList();
        return this;
    }

    public override OnDemandMetadataProvider GetMetadataProvider(MetadataRequestOptions options)
    {
        lock (Calls) { Created++; LastRequestWasBackground = options.IsBackgroundDownload; }
        if (ThrowOnCreate) throw new InvalidOperationException("provider could not be created");
        return new Provider(this);
    }

    private sealed class Provider : OnDemandMetadataProvider
    {
        private readonly FakeMeta owner;
        public Provider(FakeMeta owner) { this.owner = owner; }

        public override List<MetadataField> AvailableFields { get { return owner.SupportedFields; } }

        private IEnumerable<MetadataProperty> Get(MetadataField f, GetMetadataFieldArgs args)
        {
            lock (owner.Calls) owner.Calls.Add(f.ToString());
            if (owner.DelayMs > 0)
            {
                if (owner.IgnoreCancel) Thread.Sleep(owner.DelayMs);
                else args.CancelToken.WaitHandle.WaitOne(owner.DelayMs);
                if (args.CancelToken.IsCancellationRequested) owner.SawCancelledToken = true;
            }
            if (owner.Throws.Contains(f)) throw new InvalidOperationException(f + " exploded");
            List<MetadataProperty> list;
            return owner.Props.TryGetValue(f, out list) ? list : null;
        }

        public override IEnumerable<MetadataProperty> GetGenres(GetMetadataFieldArgs args) { return Get(MetadataField.Genres, args); }
        public override IEnumerable<MetadataProperty> GetDevelopers(GetMetadataFieldArgs args) { return Get(MetadataField.Developers, args); }
        public override IEnumerable<MetadataProperty> GetPublishers(GetMetadataFieldArgs args) { return Get(MetadataField.Publishers, args); }
        public override IEnumerable<MetadataProperty> GetFeatures(GetMetadataFieldArgs args) { return Get(MetadataField.Features, args); }
        public override IEnumerable<MetadataProperty> GetTags(GetMetadataFieldArgs args) { return Get(MetadataField.Tags, args); }
        public override IEnumerable<MetadataProperty> GetSeries(GetMetadataFieldArgs args) { return Get(MetadataField.Series, args); }
        public override IEnumerable<MetadataProperty> GetPlatforms(GetMetadataFieldArgs args) { return Get(MetadataField.Platform, args); }
        public override IEnumerable<MetadataProperty> GetRegions(GetMetadataFieldArgs args) { return Get(MetadataField.Region, args); }
        public override IEnumerable<MetadataProperty> GetAgeRatings(GetMetadataFieldArgs args) { return Get(MetadataField.AgeRating, args); }
        private MetadataFile File(MetadataField f)
        {
            lock (owner.Calls) owner.Calls.Add(f.ToString());
            MetadataFile file;
            return owner.Files.TryGetValue(f, out file) ? file : null;
        }

        public override MetadataFile GetCoverImage(GetMetadataFieldArgs args) { return File(MetadataField.CoverImage); }
        public override MetadataFile GetBackgroundImage(GetMetadataFieldArgs args) { return File(MetadataField.BackgroundImage); }
        public override MetadataFile GetIcon(GetMetadataFieldArgs args) { return File(MetadataField.Icon); }

        public override IEnumerable<Link> GetLinks(GetMetadataFieldArgs args)
        {
            lock (owner.Calls) owner.Calls.Add("Links");
            if (owner.Throws.Contains(MetadataField.Links)) throw new InvalidOperationException("Links exploded");
            return owner.Links;
        }

        public override void Dispose() { lock (owner.Calls) owner.Disposed++; }
    }
}

/// <summary>A library plugin whose own ("official store") metadata is canned.</summary>
public class FakeLibrary : LibraryPlugin
{
    private readonly Guid id = Guid.NewGuid();
    public readonly GameMetadata Store = new GameMetadata();
    public int Downloads;

    public FakeLibrary() : base(null) { }
    public override Guid Id { get { return id; } }
    public override string Name { get { return "Fake library"; } }
    public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args) { return new GameMetadata[0]; }
    public override LibraryMetadataProvider GetMetadataDownloader() { Downloads++; return new Downloader(this); }

    private sealed class Downloader : LibraryMetadataProvider
    {
        private readonly FakeLibrary owner;
        public Downloader(FakeLibrary owner) { this.owner = owner; }
        public override GameMetadata GetMetadata(Game game) { return owner.Store; }
    }
}
