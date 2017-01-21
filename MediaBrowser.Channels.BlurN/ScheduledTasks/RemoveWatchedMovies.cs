﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Channels.BlurN.Helpers;
using MediaBrowser.Model.Notifications;
using System.Xml.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Common.Configuration;
using System.IO;
using MediaBrowser.Model.IO;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Channels.BlurN.ScheduledTasks
{
    class RemoveWatchedMovies : IScheduledTask
    {
        private readonly IJsonSerializer _json;
        private readonly IApplicationPaths _appPaths;
        private readonly IFileSystem _fileSystem;
        private readonly ILibraryManager _libraryManager;

        public RemoveWatchedMovies(IJsonSerializer json, IApplicationPaths appPaths, IFileSystem fileSystem, ILibraryManager libraryManager)
        {
            _json = json;
            _appPaths = appPaths;
            _fileSystem = fileSystem;
            _libraryManager = libraryManager;
        }


        public string Category
        {
            get
            {
                return "BlurN";
            }
        }

        public string Description
        {
            get
            {
                return "Remove movies from the BlurN channel which exist in your library and are marked as watched.";
            }
        }

        public string Key
        {
            get
            {
                return "BlurNRemoveWatchedMovies";
            }
        }

        public string Name
        {
            get
            {
                return "Remove watched movies";
            }
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var config = Plugin.Instance.Configuration;
            bool debug = config.EnableDebugLogging;

            IEnumerable<BaseItem> library;
            Dictionary<string, BaseItem> libDict = new Dictionary<string, BaseItem>();

            if (!config.AddItemsAlreadyInLibrary)
            {
                library = _libraryManager.GetItemList(new InternalItemsQuery() { HasImdbId = true, IsPlayed = true, SourceTypes = new SourceType[] { SourceType.Library } });

                foreach (BaseItem libItem in library)
                {
                    string libIMDbId = libItem.GetProviderId(MetadataProviders.Imdb);
                    if (!libDict.ContainsKey(libIMDbId))
                        libDict.Add(libIMDbId, libItem);
                }
            }

            if (libDict.Count > 0)
            {
                string dataPath = Path.Combine(_appPaths.PluginConfigurationsPath, "MediaBrowser.Channels.BlurN.Data.json");

                if (_fileSystem.FileExists(dataPath))
                {
                    var existingData = _json.DeserializeFromFile<List<OMDB>>(dataPath);

                    if (existingData != null)
                    {
                        bool removedItems = false;
                        for (int ci = 0; ci < existingData.Count; ci++)
                        {
                            OMDB channelItem = existingData[ci];
                            BaseItem libraryItem = libDict.FirstOrDefault(i => i.Key == channelItem.ImdbId).Value;
                            if (libraryItem != default(BaseItem))
                            {
                                existingData.RemoveAt(ci);
                                ci--;
                                removedItems = true;

                                if (debug)
                                    Plugin.Logger.Debug("Removing watched movie " + libraryItem.OriginalTitle + " from BlurN channel");
                            }
                        }

                        if (removedItems)
                        {
                            if (debug)
                                Plugin.Logger.Debug("Saving updated BlurN database");

                            _json.SerializeToFile(existingData, dataPath);
                            config.ChannelRefreshCount++;
                            Plugin.Instance.SaveConfiguration();
                        }
                    }
                }
            }

            progress.Report(100);
            return;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Until we can vary these default triggers per server and MBT, we need something that makes sense for both
            return new[] { 
            
                // At startup
                //new TaskTriggerInfo {Type = TaskTriggerInfo.TriggerStartup},

                // Every so often
                new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerInterval, IntervalTicks = TimeSpan.FromHours(12).Ticks}
            };
        }
    }
}
