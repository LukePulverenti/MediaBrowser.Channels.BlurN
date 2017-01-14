﻿using MediaBrowser.Controller.Channels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using System.Threading;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Channels.BlurN.Helpers;
using MediaBrowser.Model.Notifications;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System.Reflection;

namespace MediaBrowser.Channels.BlurN.Channels
{
    public class Channel : IChannel
    {
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
                return "";
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
                return ChannelParentalRating.Adult;
            }
        }

        public InternalChannelFeatures GetChannelFeatures()
        {
            var sortfields = new List<ChannelItemSortField>();
            sortfields.Add(ChannelItemSortField.DateCreated);
            sortfields.Add(ChannelItemSortField.CommunityRating);
            sortfields.Add(ChannelItemSortField.PremiereDate);
            return new InternalChannelFeatures
            {
                ContentTypes = new List<ChannelMediaContentType>
                {
                    ChannelMediaContentType.Movie
                },

                MediaTypes = new List<ChannelMediaType>
                {
                    ChannelMediaType.Video
                },
                MaxPageSize = 20,
                DefaultSortFields = sortfields,
                SupportsContentDownloading = false,
                SupportsSortOrderToggle = true
            };
        }

        private Task<DynamicImageResponse> GetImage(ImageType type, string imageFormat)
        {
            var path = String.Format("{0}.Images.{1}.{2}", GetType().Namespace, type, imageFormat);
            return Task.FromResult(new DynamicImageResponse
            {
                Format = ImageFormat.Jpg,
                HasImage = true,
                Stream = GetType().GetTypeInfo().Assembly.GetManifestResourceStream(path)
            });
        }

        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            switch (type)
            {
                case ImageType.Thumb:
                    return GetImage(type, "jpg");
                default:
                    throw new ArgumentException("Unsupported image type: " + type);
            }
        }

        public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            bool debug = Plugin.Instance.Configuration.EnableDebugLogging;

            if (debug)
                Plugin.Logger.Debug("Entered BlurN channel list");

            var items = Plugin.Instance.Configuration.Items;

            if (debug)
                Plugin.Logger.Debug("Retrieved items");

            if (items == null)
            {
                if (debug)
                    Plugin.Logger.Debug("Items is null, set to new list");
                items = new OMDBList();
                Plugin.Instance.SaveConfiguration();
            }

            int totalcount = items.List.Count;

            ChannelItemResult result = new ChannelItemResult() { TotalRecordCount = totalcount };

            if (debug)
                Plugin.Logger.Debug("Set total record count ({0})", totalcount);

            for (int i = totalcount - 1; i >= 0; i--)
            {
                OMDB omdb = items.List[i];

                Plugin.Logger.Debug("Showing movie '{0}' to BlurN channel list", omdb.Title);

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

                result.Items.Add(new ChannelItemInfo
                {
                    Id = omdb.ImdbId,
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
                });

                Plugin.Logger.Debug("Added movie '{0}' to BlurN channel list", omdb.Title);
            }

            return Task.FromResult(result);
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
    }
}
