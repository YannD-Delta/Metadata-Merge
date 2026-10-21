using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using MetadataMerge.Core;

namespace MetadataMerge
{
    /// <summary>A metadata plugin installed in Playnite, asked through its own provider.</summary>
    public sealed class PluginSource : ISource
    {
        private readonly MetadataPlugin plugin;
        public PluginSource(MetadataPlugin plugin) { this.plugin = plugin; }

        public string Id { get { return plugin.Id.ToString("D"); } }
        public string Name { get { return SourceCatalog.NameOf(plugin); } }

        public ISourceSession Open(GameRef game)
        {
            // the plugin's provider is created once per game here and used for every field of the game
            var provider = plugin.GetMetadataProvider(new MetadataRequestOptions((Game)game.Tag, true));
            return new Session(provider);
        }

        private sealed class Session : ISourceSession
        {
            private readonly OnDemandMetadataProvider provider;
            public Session(OnDemandMetadataProvider provider) { this.provider = provider; }

            public FetchResult Fetch(ICollection<Field> fields, CancellationToken cancel)
            {
                var result = new FetchResult();
                if (provider == null) return result;
                List<MetadataField> available;
                try { available = provider.AvailableFields ?? new List<MetadataField>(); }
                catch (Exception ex) { result.Problems.Add(new FetchProblem { Message = "AvailableFields: " + ex.Message }); return result; }
                foreach (var f in fields)
                {
                    if (cancel.IsCancellationRequested) break;
                    if (!available.Contains(Mapping.SdkField(f))) continue;
                    try { result.Values[f] = Mapping.Ask(provider, f, Mapping.ArgsFor(cancel)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { result.Problems.Add(new FetchProblem { Field = f, Message = ex.GetType().Name + ": " + ex.Message }); }
                }
                return result;
            }

            public void Dispose() { if (provider != null) provider.Dispose(); }
        }
    }

    /// <summary>The "Official store" source: the metadata the game's own library plugin gives (Playnite's source with the empty id).</summary>
    public sealed class StoreSource : ISource
    {
        private readonly Func<IEnumerable<Plugin>> plugins;
        private readonly Func<string> name;
        public StoreSource(Func<IEnumerable<Plugin>> plugins, Func<string> name) { this.plugins = plugins; this.name = name; }

        public string Id { get { return PlayniteConfig.StoreId; } }
        public string Name { get { return name(); } }

        public ISourceSession Open(GameRef game)
        {
            var g = (Game)game.Tag;
            var library = plugins().OfType<LibraryPlugin>().FirstOrDefault(l => l.Id == g.PluginId);
            return new Session(library, g);
        }

        private sealed class Session : ISourceSession
        {
            private readonly LibraryPlugin library;
            private readonly Game game;
            private LibraryMetadataProvider downloader;
            public Session(LibraryPlugin library, Game game) { this.library = library; this.game = game; }

            public FetchResult Fetch(ICollection<Field> fields, CancellationToken cancel)
            {
                var result = new FetchResult();
                if (library == null) return result;   // a game of no library (added by hand) has no store data
                downloader = downloader ?? library.GetMetadataDownloader();
                if (downloader == null) return result;
                var data = downloader.GetMetadata(game);
                foreach (var f in fields) result.Values[f] = Mapping.FromStore(data, f);
                return result;
            }

            public void Dispose() { if (downloader != null) downloader.Dispose(); }
        }
    }

    public sealed class SourceInfo
    {
        public string Id;
        public string Name;
        /// <summary>The metadata fields the plugin says it supports.</summary>
        public List<MetadataField> Fields = new List<MetadataField>();
    }

    /// <summary>The sources installed in this Playnite, found through the SDK's plugin list. This extension itself is left out.</summary>
    public sealed class SourceCatalog
    {
        private readonly Func<IEnumerable<Plugin>> plugins;
        private readonly Guid self;
        private readonly Func<string> storeName;

        /// <param name="storeName">The display name of the official store source (localised, so read when needed).</param>
        public SourceCatalog(Func<IEnumerable<Plugin>> plugins, Guid self, Func<string> storeName)
        {
            this.plugins = plugins;
            this.self = self;
            this.storeName = storeName;
        }

        public static string NameOf(MetadataPlugin plugin)
        {
            try { if (!string.IsNullOrWhiteSpace(plugin.Name)) return plugin.Name; } catch { }
            return plugin.GetType().Name;
        }

        private IEnumerable<MetadataPlugin> Plugins()
        {
            IEnumerable<Plugin> all;
            try { all = plugins() ?? Enumerable.Empty<Plugin>(); } catch { return Enumerable.Empty<MetadataPlugin>(); }
            return all.OfType<MetadataPlugin>().Where(p => p.Id != self).ToList();
        }

        public ISource Find(string id)
        {
            string norm = PlayniteConfig.NormalizeId(id);
            if (norm == PlayniteConfig.StoreId) return new StoreSource(plugins, storeName);
            var hit = Plugins().FirstOrDefault(p => p.Id.ToString("D") == norm);
            return hit == null ? null : new PluginSource(hit);
        }

        /// <summary>The official store first, then every installed metadata plugin by name.</summary>
        public List<SourceInfo> Installed()
        {
            var list = new List<SourceInfo> { new SourceInfo { Id = PlayniteConfig.StoreId, Name = storeName() } };
            foreach (var p in Plugins().OrderBy(NameOf, StringComparer.CurrentCultureIgnoreCase))
            {
                var info = new SourceInfo { Id = p.Id.ToString("D"), Name = NameOf(p) };
                try { info.Fields = p.SupportedFields ?? new List<MetadataField>(); } catch { }
                list.Add(info);
            }
            return list;
        }
    }
}
