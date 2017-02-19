﻿using MediaBrowser.Controller.Channels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using System.Threading;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Channels.BlurN.Helpers;
using MediaBrowser.Controller.Entities;
using System.Reflection;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.IO;
using System.IO;

namespace MediaBrowser.Channels.BlurN
{
    public class BlurNChannel : IChannel, IIndexableChannel, ISupportsLatestMedia, IHasCacheKey
    {
        private readonly IJsonSerializer _json;
        private readonly IApplicationPaths _appPaths;
        private readonly IFileSystem _fileSystem;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;

        public BlurNChannel(IUserManager userManager, ILibraryManager libraryManager, IJsonSerializer json, IApplicationPaths appPaths, IFileSystem fileSystem)
        {
            _json = json;
            _appPaths = appPaths;
            _fileSystem = fileSystem;
            _userManager = userManager;
            _libraryManager = libraryManager;
        }


        public string DataVersion
        {
            get
            {
                return Plugin.Instance.Configuration.LastPublishDate.ToString("yyyyMMdd");
            }
        }

        public string Description
        {
            get
            {
                return "Channel for new Blu-Ray releases that match filters in the settings";
            }
        }

        public string HomePageUrl
        {
            get
            {
                return "https://github.com/MarkCiliaVincenti/MediaBrowser.Channels.BlurN";
            }
        }

        public string Name
        {
            get
            {
                return "BlurN";
            }
        }

        public ChannelParentalRating ParentalRating
        {
            get
            {
                return ChannelParentalRating.UsR;
            }
        }

        public InternalChannelFeatures GetChannelFeatures()
        {
            //var sortfields = new List<ChannelItemSortField>();
            //sortfields.Add(ChannelItemSortField.DateCreated);
            //sortfields.Add(ChannelItemSortField.CommunityRating);
            //sortfields.Add(ChannelItemSortField.PremiereDate);
            return new InternalChannelFeatures
            {
                ContentTypes = new List<ChannelMediaContentType>
                {
                    ChannelMediaContentType.Movie
                },
                AutoRefreshLevels = 3,

                MediaTypes = new List<ChannelMediaType>
                {
                    ChannelMediaType.Video
                },
                MaxPageSize = 100,
                //DefaultSortFields = sortfields,
                SupportsContentDownloading = false,
                SupportsSortOrderToggle = false
            };
        }

        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            switch (type)
            {
                case ImageType.Thumb:
                    {
                        var path = GetType().Namespace + ".Images." + type.ToString().ToLower() + ".png";

                        return Task.FromResult(new DynamicImageResponse
                        {
                            Format = ImageFormat.Png,
                            HasImage = true,
                            Stream = GetType().GetTypeInfo().Assembly.GetManifestResourceStream(path)
                        });
                    }
                default:
                    throw new ArgumentException("Unsupported image type: " + type);
            }
        }

        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await GetItems(true, query, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ChannelItemResult> GetItems(bool inChannel, InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var config = await BlurNTasks.CheckIfResetDatabaseRequested(cancellationToken, _json, _appPaths, _fileSystem).ConfigureAwait(false);

            bool debug = config.EnableDebugLogging;

            if (inChannel && debug)
                Plugin.Logger.Debug("[BlurN] Entered BlurN channel list");

            User user = _userManager.GetUserById(query.UserId);

            Dictionary<string, BaseItem> libDict = Library.BuildLibraryDictionary(cancellationToken, _libraryManager, new InternalItemsQuery() { HasImdbId = true, User = user, IsPlayed = true, SourceTypes = new SourceType[] { SourceType.Library } });

            OMDBList items = new OMDBList();
            if (config.Items.List.Count > 0)
                items.List = config.Items.List;
            else
            {
                string dataPath = Path.Combine(_appPaths.PluginConfigurationsPath, "MediaBrowser.Channels.BlurN.Data.json");

                if (_fileSystem.FileExists(dataPath))
                    items.List = _json.DeserializeFromFile<List<OMDB>>(dataPath);
            }

            if (inChannel && debug)
                Plugin.Logger.Debug("[BlurN] Retrieved items");

            if (items == null)
            {
                if (inChannel && debug)
                    Plugin.Logger.Debug("[BlurN] Items is null, set to new list");
                items = new OMDBList();
                Plugin.Instance.SaveConfiguration();
            }

            if (query.SortDescending)
                items.List.Reverse();


            OMDBList showList = new OMDBList();
            if (query.StartIndex.HasValue && query.Limit.HasValue && query.Limit.Value > 0)
            {
                int index = query.StartIndex.Value;
                int limit = query.Limit.Value;

                if (items.List.Count < index + limit)
                    limit = items.List.Count - index;

                showList.List = items.List.GetRange(index, limit);
                if (inChannel && debug)
                    Plugin.Logger.Debug("[BlurN] Showing range with index {0} and limit {1}", index, limit);
            }
            else
            {
                showList.List = items.List;
                if (inChannel && debug)
                    Plugin.Logger.Debug("[BlurN] Showing full list");
            }

            ChannelItemResult result = new ChannelItemResult() { TotalRecordCount = items.List.Count };

            for (int i = 0; i < showList.List.Count; i++)
            {
                OMDB omdb = showList.List[i];

                if (libDict.ContainsKey(omdb.ImdbId))
                {
                    result.TotalRecordCount--;

                    if (inChannel && debug)
                        Plugin.Logger.Debug("[BlurN] Hiding movie '{0}' from BlurN channel list as watched by user", omdb.Title);
                }
                else
                {
                    if (inChannel && debug)
                        Plugin.Logger.Debug("[BlurN] Showing movie '{0}' to BlurN channel list", omdb.Title);

                    List<string> directors = (string.IsNullOrEmpty(omdb.Director)) ? new List<string>() : omdb.Director.Replace(", ", ",").Split(',').ToList();
                    List<string> writers = (string.IsNullOrEmpty(omdb.Writer)) ? new List<string>() : omdb.Writer.Replace(", ", ",").Split(',').ToList();
                    List<string> actors = (string.IsNullOrEmpty(omdb.Actors)) ? new List<string>() : omdb.Actors.Replace(", ", ",").Split(',').ToList();

                    List<PersonInfo> people = new List<PersonInfo>();
                    foreach (string director in directors)
                        people.Add(new PersonInfo() { Name = director, Role = "Director" });
                    foreach (string writer in writers)
                        people.Add(new PersonInfo() { Name = writer, Role = "Writer" });
                    foreach (string actor in actors)
                        people.Add(new PersonInfo() { Name = actor, Role = "Actor" });

                    List<string> genres = (string.IsNullOrEmpty(omdb.Genre)) ? new List<string>() : omdb.Genre.Replace(", ", ",").Split(',').ToList();

                    var cii = new ChannelItemInfo()
                    {
                        Id = config.ChannelRefreshCount.ToString() + "-" + omdb.ImdbId,
                        IndexNumber = i,
                        CommunityRating = (float)omdb.ImdbRating,
                        ContentType = ChannelMediaContentType.Movie,
                        DateCreated = omdb.BluRayReleaseDate,
                        Genres = genres,
                        ImageUrl = (omdb.Poster == "N/A") ? null : omdb.Poster,
                        MediaType = ChannelMediaType.Video,
                        Name = omdb.Title,
                        OfficialRating = (omdb.Rated == "N/A") ? null : omdb.Rated,
                        Overview = (omdb.Plot == "N/A") ? null : omdb.Plot,
                        People = people,
                        PremiereDate = omdb.Released,
                        ProductionYear = omdb.Released.Year,
                        RunTimeTicks = (string.IsNullOrEmpty(omdb.Runtime) || (omdb.Runtime == "N/A")) ? null : (long?)TimeSpan.FromMinutes(Convert.ToInt32(omdb.Runtime.Split(' ')[0])).Ticks,
                        Type = ChannelItemType.Media,
                        IsInfiniteStream = false
                    };

                    cii.SetProviderId(MetadataProviders.Imdb, omdb.ImdbId);
                    if (omdb.TmdbId.HasValue)
                        cii.SetProviderId(MetadataProviders.Tmdb, omdb.TmdbId.Value.ToString());

                    result.Items.Add(cii);

                    if (inChannel && debug)
                        Plugin.Logger.Debug("[BlurN] Added movie '{0}' to BlurN channel list", omdb.Title);
                }
            }

            if (inChannel && debug)
                Plugin.Logger.Debug("[BlurN] Set total record count ({0})", (int)result.TotalRecordCount);

            return result;
        }

        public IEnumerable<ImageType> GetSupportedChannelImages()
        {
            return new List<ImageType>
            {
                ImageType.Thumb
            };
        }

        public bool IsEnabledFor(string userId)
        {
            return true;
        }

        public string GetCacheKey(string userId)
        {
            return DataVersion;
        }

        public async Task<ChannelItemResult> GetAllMedia(InternalAllChannelMediaQuery query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channelItemResult = await GetItems(false, new InternalChannelItemQuery { UserId = query.UserId }, cancellationToken).ConfigureAwait(false);

            if (query.ContentTypes.Length > 0)
            {
                channelItemResult.Items = channelItemResult.Items.Where(i => query.ContentTypes.Contains(i.ContentType)).ToList();
            }
            if (query.ExtraTypes.Length > 0)
            {
                channelItemResult.Items = channelItemResult.Items.Where(i => query.ExtraTypes.Contains(i.ExtraType)).ToList();
            }
            if (query.TrailerTypes.Length > 0)
            {
                channelItemResult.Items = channelItemResult.Items.Where(i => i.TrailerTypes.Any(t => query.TrailerTypes.Contains(t))).ToList();
            }

            channelItemResult.TotalRecordCount = channelItemResult.Items.Count;
            return channelItemResult;
        }

        public async Task<IEnumerable<ChannelItemInfo>> GetLatestMedia(ChannelLatestMediaSearch request, CancellationToken cancellationToken)
        {
            var result = await GetChannelItems(new InternalChannelItemQuery
            {
                SortBy = ChannelItemSortField.DateCreated,
                SortDescending = true,
                UserId = request.UserId,
                StartIndex = 0,
                Limit = 6
            }, cancellationToken).ConfigureAwait(false);

            return result.Items;
        }
    }
}
