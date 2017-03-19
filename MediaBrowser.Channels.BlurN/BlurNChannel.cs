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
using MediaBrowser.Common;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Common.Net;

namespace MediaBrowser.Channels.BlurN
{
    public class BlurNChannel : IChannel, IIndexableChannel, ISupportsLatestMedia, IHasCacheKey
    {
        private readonly IApplicationHost _appHost;
        private readonly IServerConfigurationManager _serverConfigurationManager;
        private readonly IJsonSerializer _json;
        private readonly IApplicationPaths _appPaths;
        private readonly IFileSystem _fileSystem;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IHttpClient _httpClient;

        public BlurNChannel(IHttpClient httpClient, IApplicationHost appHost, IServerConfigurationManager serverConfigurationManager, IMediaSourceManager mediaSourceManager, IUserManager userManager, ILibraryManager libraryManager, IJsonSerializer json, IApplicationPaths appPaths, IFileSystem fileSystem, IUserDataManager userDataManager)
        {
            _httpClient = httpClient;
            _appHost = appHost;
            _serverConfigurationManager = serverConfigurationManager;
            _mediaSourceManager = mediaSourceManager;
            _json = json;
            _appPaths = appPaths;
            _fileSystem = fileSystem;
            _userManager = userManager;
            _libraryManager = libraryManager;
            _userDataManager = userDataManager;
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
            var sortfields = new List<ChannelItemSortField>();
            sortfields.Add(ChannelItemSortField.Name);
            sortfields.Add(ChannelItemSortField.DateCreated);
            sortfields.Add(ChannelItemSortField.CommunityRating);
            sortfields.Add(ChannelItemSortField.PremiereDate);
            sortfields.Add(ChannelItemSortField.Runtime);
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
                DefaultSortFields = sortfields,
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
            Tracking.Track(_httpClient, _appHost, _serverConfigurationManager, "start", "viewchannel", cancellationToken);
            return await GetItems(true, query, cancellationToken).ConfigureAwait(false);
            Tracking.Track(_httpClient, _appHost, _serverConfigurationManager, "end", "viewchannel", cancellationToken);
        }

        public async Task<ChannelItemResult> GetItems(bool inChannel, InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var config = await BlurNTasks.CheckIfResetDatabaseRequested(cancellationToken, _json, _appPaths, _fileSystem).ConfigureAwait(false);

            bool debug = config.EnableDebugLogging;

            if (inChannel && debug)
                Plugin.Logger.Debug("[BlurN] Entered BlurN channel list");

            User user = _userManager.GetUserById(query.UserId);

            Dictionary<string, BaseItem> libDict = Library.BuildLibraryDictionary(cancellationToken, _libraryManager, new InternalItemsQuery() { HasImdbId = true, User = user, SourceTypes = new SourceType[] { SourceType.Library } });

            if (debug)
                Plugin.Logger.Debug("[BlurN] Found {0} items in movies library", libDict.Count);

            BlurNItems items = new BlurNItems();
            string dataPath = Path.Combine(_appPaths.PluginConfigurationsPath, "MediaBrowser.Channels.BlurN.Data.json");

            if (_fileSystem.FileExists(dataPath))
                items.List = _json.DeserializeFromFile<List<BlurNItem>>(dataPath);

            if (inChannel && debug)
                Plugin.Logger.Debug("[BlurN] Retrieved items");

            if (items == null)
            {
                if (inChannel && debug)
                    Plugin.Logger.Debug("[BlurN] Items is null, set to new list");
                items = new BlurNItems();
                Plugin.Instance.SaveConfiguration();
            }

            for (int i = 0; i < items.List.Count; i++)
            {
                BlurNItem blurNItem = items.List[i];

                BaseItem libraryItem;
                bool foundInLibrary = libDict.TryGetValue(blurNItem.ImdbId, out libraryItem);
                if (foundInLibrary)
                {
                    if (config.HidePlayedMovies && libraryItem.IsPlayed(user))
                    {
                        items.List.RemoveAt(i);
                        i--;
                        if (inChannel && debug)
                            Plugin.Logger.Debug("[BlurN] Hiding movie '{0}' from BlurN channel list as watched by user", blurNItem.Title);
                    }
                    else
                    {
                        blurNItem.LibraryItem = libraryItem;
                    }
                }
            }

            switch (query.SortBy)
            {
                case ChannelItemSortField.Name:
                    if (query.SortDescending)
                        items.List.OrderByDescending(i => i.Title);
                    else
                        items.List.OrderBy(i => i.Title);
                    break;
                case ChannelItemSortField.DateCreated:
                    if (query.SortDescending)
                        items.List.OrderByDescending(i => i.BluRayReleaseDate);
                    else
                        items.List.OrderBy(i => i.BluRayReleaseDate);
                    break;
                case ChannelItemSortField.CommunityRating:
                    if (query.SortDescending)
                        items.List.OrderByDescending(i => i.ImdbRating).ThenByDescending(i => i.ImdbVotes);
                    else
                        items.List.OrderBy(i => i.ImdbRating).ThenBy(i => i.ImdbVotes);
                    break;
                case ChannelItemSortField.PremiereDate:
                    if (query.SortDescending)
                        items.List.OrderByDescending(i => i.Released);
                    else
                        items.List.OrderBy(i => i.Released);
                    break;
                case ChannelItemSortField.Runtime:
                    if (query.SortDescending)
                        items.List.OrderByDescending(i => (string.IsNullOrEmpty(i.Runtime) || (i.Runtime == "N/A")) ? null : (long?)TimeSpan.FromMinutes(Convert.ToInt32(i.Runtime.Split(' ')[0])).Ticks);
                    else
                        items.List.OrderBy(i => (string.IsNullOrEmpty(i.Runtime) || (i.Runtime == "N/A")) ? null : (long?)TimeSpan.FromMinutes(Convert.ToInt32(i.Runtime.Split(' ')[0])).Ticks);
                    break;
                default:
                    if (query.SortDescending)
                        items.List.Reverse();
                    break;
            }

            BlurNItems showList = new BlurNItems();
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
                BlurNItem blurNItem = showList.List[i];

                if (inChannel && debug)
                    Plugin.Logger.Debug("[BlurN] Showing movie '{0}' to BlurN channel list", blurNItem.Title);

                List<string> directors = (string.IsNullOrEmpty(blurNItem.Director)) ? new List<string>() : blurNItem.Director.Replace(", ", ",").Split(',').ToList();
                List<string> writers = (string.IsNullOrEmpty(blurNItem.Writer)) ? new List<string>() : blurNItem.Writer.Replace(", ", ",").Split(',').ToList();
                List<string> actors = (string.IsNullOrEmpty(blurNItem.Actors)) ? new List<string>() : blurNItem.Actors.Replace(", ", ",").Split(',').ToList();

                List<PersonInfo> people = new List<PersonInfo>();
                foreach (string director in directors)
                    people.Add(new PersonInfo() { Name = director, Role = "Director" });
                foreach (string writer in writers)
                    people.Add(new PersonInfo() { Name = writer, Role = "Writer" });
                foreach (string actor in actors)
                    people.Add(new PersonInfo() { Name = actor, Role = "Actor" });

                List<string> genres = (string.IsNullOrEmpty(blurNItem.Genre)) ? new List<string>() : blurNItem.Genre.Replace(", ", ",").Split(',').ToList();

                var cii = new ChannelItemInfo()
                {
                    Id = config.ChannelRefreshCount.ToString() + "-" + blurNItem.ImdbId,
                    IndexNumber = i,
                    CommunityRating = (float)blurNItem.ImdbRating,
                    ContentType = ChannelMediaContentType.Movie,
                    DateCreated = blurNItem.BluRayReleaseDate,
                    Genres = genres,
                    ImageUrl = (blurNItem.Poster == "N/A") ? null : blurNItem.Poster,
                    MediaType = ChannelMediaType.Video,
                    Name = blurNItem.Title,
                    OfficialRating = (blurNItem.Rated == "N/A") ? null : blurNItem.Rated,
                    Overview = (blurNItem.Plot == "N/A") ? null : blurNItem.Plot,
                    People = people,
                    PremiereDate = blurNItem.Released,
                    ProductionYear = blurNItem.Released.Year,
                    RunTimeTicks = (string.IsNullOrEmpty(blurNItem.Runtime) || (blurNItem.Runtime == "N/A")) ? null : (long?)TimeSpan.FromMinutes(Convert.ToInt32(blurNItem.Runtime.Split(' ')[0])).Ticks,
                    Type = ChannelItemType.Media,
                    IsInfiniteStream = false
                };

                cii.SetProviderId(MetadataProviders.Imdb, blurNItem.ImdbId);
                if (blurNItem.TmdbId.HasValue)
                    cii.SetProviderId(MetadataProviders.Tmdb, blurNItem.TmdbId.Value.ToString());

                if (blurNItem.LibraryItem != null)
                {
                    var mediaStreams = _mediaSourceManager.GetMediaStreams(blurNItem.LibraryItem.Id).ToList();

                    var audioStream = mediaStreams.FirstOrDefault(ms => ms.Type == MediaStreamType.Audio && ms.IsDefault);
                    var videoStream = mediaStreams.FirstOrDefault(ms => ms.Type == MediaStreamType.Video && ms.IsDefault);

                    ChannelMediaInfo cmi = new ChannelMediaInfo()
                    {
                        Path = _libraryManager.GetPathAfterNetworkSubstitution(blurNItem.LibraryItem.Path, blurNItem.LibraryItem),
                        Container = blurNItem.LibraryItem.Container,
                        RunTimeTicks = blurNItem.LibraryItem.RunTimeTicks,
                        SupportsDirectPlay = true,
                        Id = blurNItem.LibraryItem.Id.ToString(),
                        Protocol = Model.MediaInfo.MediaProtocol.File
                    };

                    if (audioStream != null)
                    {
                        cmi.AudioBitrate = audioStream.BitRate;
                        cmi.AudioChannels = audioStream.Channels;
                        cmi.AudioCodec = audioStream.Codec;
                        cmi.AudioSampleRate = audioStream.SampleRate;
                    }
                    if (videoStream != null)
                    {
                        cmi.Framerate = videoStream.RealFrameRate;
                        cmi.Height = videoStream.Height;
                        cmi.IsAnamorphic = videoStream.IsAnamorphic;
                        cmi.VideoBitrate = videoStream.BitRate;
                        cmi.VideoCodec = videoStream.Codec;
                        if (videoStream.Level.HasValue)
                            cmi.VideoLevel = (float)videoStream.Level.Value;
                        cmi.VideoProfile = videoStream.Profile;
                        cmi.Width = videoStream.Width;
                    }
                    if (debug)
                        Plugin.Logger.Debug("[BlurN] Linked movie {0} to library. Path: {1}, Substituted Path: {2}", blurNItem.Title, blurNItem.LibraryItem.Path, cmi.Path);
                    cii.MediaSources = new List<ChannelMediaInfo>() { cmi };
                }

                result.Items.Add(cii);

                if (inChannel && debug)
                    Plugin.Logger.Debug("[BlurN] Added movie '{0}' to BlurN channel list", blurNItem.Title);
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
