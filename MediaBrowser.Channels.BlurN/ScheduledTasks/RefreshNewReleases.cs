﻿using MediaBrowser.Channels.BlurN.Helpers;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Notifications;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MediaBrowser.Channels.BlurN.ScheduledTasks
{
    class RefreshNewReleases : IScheduledTask
    {
        private readonly IApplicationHost _appHost;
        private readonly IJsonSerializer _json;
        private readonly IApplicationPaths _appPaths;
        private readonly IFileSystem _fileSystem;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IServerConfigurationManager _serverConfigurationManager;
        private readonly IHttpClient _httpClient;

        private const string bluRayReleaseUri = "http://www.blu-ray.com/rss/newreleasesfeed.xml";
        private const string baseOmdbApiUri = "https://private.omdbapi.com";

        public RefreshNewReleases(IUserManager userManager, IHttpClient httpClient, IApplicationHost appHost, IJsonSerializer json, IApplicationPaths appPaths, IFileSystem fileSystem, ILibraryManager libraryManager, IServerConfigurationManager serverConfigurationManager)
        {
            _userManager = userManager;
            _httpClient = httpClient;
            _appHost = appHost;
            _json = json;
            _appPaths = appPaths;
            _fileSystem = fileSystem;
            _libraryManager = libraryManager;
            _serverConfigurationManager = serverConfigurationManager;
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
                return "Checks for new Blu-Ray releases that match the filters in the settings and adds them to the BlurN channel.";
            }
        }

        public string Key
        {
            get
            {
                return "BlurNRefreshNewReleases";
            }
        }

        public string Name
        {
            get
            {
                return "Refresh new releases";
            }
        }

        public int Order
        {
            get
            {
                throw new NotImplementedException();
            }
        }

        public async virtual Task<BlurNItem> ParseOMDB(string url, DateTime bluRayReleaseDate, CancellationToken cancellationToken)
        {
            //string result = "";
            try
            {
                Plugin.DebugLogger($"Getting {url}");
                using (var result = await _httpClient.Get(new HttpRequestOptions()
                {
                    Url = url,
                    CancellationToken = cancellationToken,
                    BufferContent = true,
                    EnableDefaultUserAgent = true,
                    TimeoutMs = 15000,
                    EnableHttpCompression = true,
                    DecompressionMethod = CompressionMethod.Gzip
                }).ConfigureAwait(false))
                {
                    Plugin.DebugLogger($"Got {url}");
                    XDocument doc = XDocument.Load(result);
                    XElement root = doc.Root;
                    if (root.Elements().First().Name.ToString() == "movie")
                    {
                        var entry = doc.Root.Element("movie");

                        int year = 0;
                        Int32.TryParse(entry.Attribute("year").Value, out year);

                        decimal imdbRating = 0;
                        decimal.TryParse(entry.Attribute("imdbRating").Value, NumberStyles.Any, new CultureInfo("en-US"), out imdbRating);

                        int imdbVotes = 0;
                        Int32.TryParse(entry.Attribute("imdbVotes").Value.Replace(",", ""), out imdbVotes);

                        DateTime released = DateTime.MinValue;
                        if (entry.Attribute("released").Value != "N/A")
                            released = ParseDate(entry.Attribute("released").Value);

                        return new BlurNItem()
                        {
                            BluRayReleaseDate = bluRayReleaseDate,
                            Actors = WebUtility.HtmlDecode(entry.Attribute("actors").Value),
                            Awards = WebUtility.HtmlDecode(entry.Attribute("awards").Value),
                            Country = WebUtility.HtmlDecode(entry.Attribute("country").Value),
                            Director = WebUtility.HtmlDecode(entry.Attribute("director").Value),
                            Genre = entry.Attribute("genre").Value,
                            ImdbId = entry.Attribute("imdbID").Value,
                            Language = WebUtility.HtmlDecode(entry.Attribute("language").Value),
                            Metascore = entry.Attribute("metascore").Value,
                            Plot = WebUtility.HtmlDecode(entry.Attribute("plot").Value),
                            Poster = WebUtility.HtmlDecode(entry.Attribute("poster").Value),
                            Rated = entry.Attribute("rated").Value,
                            Runtime = entry.Attribute("runtime").Value,
                            RuntimeTicks = BlurNItem.ConvertRuntimeToTicks(entry.Attribute("runtime").Value),
                            Type = entry.Attribute("type").Value,
                            Writer = WebUtility.HtmlDecode(entry.Attribute("writer").Value),
                            Title = WebUtility.HtmlDecode(entry.Attribute("title").Value),
                            Year = year,
                            ImdbRating = imdbRating,
                            ImdbVotes = imdbVotes,
                            Released = released
                        };
                    }
                    else
                        Plugin.DebugLogger($"Received an error from {url} - {root.Elements().First().Value}");
                    return new BlurNItem();
                }
            }
            catch (Exception ex)
            {
                Plugin.DebugLogger($"Caught error {ex.Message}");
                return null;
            }
        }


        private DateTime ParseDate(string date)
        {
            DateTime result;
            if (DateTime.TryParse(date, out result))
                return result;
            else
                return DateTime.MinValue;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Tracking.Track(_httpClient, _appHost, _serverConfigurationManager, "start", "refresh", cancellationToken).ConfigureAwait(false);

            progress.Report(2d);

            var items = (await GetBluRayReleaseItems(cancellationToken).ConfigureAwait(false)).List;

            progress.Report(6d);

            var config = await BlurNTasks.CheckIfResetDatabaseRequested(cancellationToken, _json, _appPaths, _fileSystem).ConfigureAwait(false);

            progress.Report(8d);

            string dataPath = Path.Combine(_appPaths.PluginConfigurationsPath, "MediaBrowser.Channels.BlurN.Data.json");

            ConvertPostersFromW640ToOriginal(config, dataPath);

            Plugin.DebugLogger($"Found {items.Count} items in feed");

            DateTime lastPublishDate = config.LastPublishDate;
            DateTime minAge = DateTime.Today.AddDays(0 - config.Age);
            DateTime newPublishDate = items[0].PublishDate;
            Dictionary<string, BaseItem> libDict = (config.AddItemsAlreadyInLibrary) ? Library.BuildLibraryDictionary(cancellationToken, _libraryManager, new InternalItemsQuery()
            {
                HasAnyProviderId = new[] { "Imdb" },
                //SourceTypes = new SourceType[] { SourceType.Library }
            }) : new Dictionary<string, BaseItem>();
            cancellationToken.ThrowIfCancellationRequested();

            var insertList = new BlurNItems();
            var failedList = new FailedBlurNList();
            var existingData = new List<BlurNItem>();

            //var finalItems = items.Where(i => i.PublishDate > lastPublishDate).GroupBy(x => new { x.Title, x.PublishDate }).Select(g => g.First()).Reverse().ToList();
            var finalItems = items.GroupBy(x => new { x.Title, x.PublishDate }).Select(g => g.First()).Reverse().ToList();

            string failedDataPath = AddPreviouslyFailedItemsToFinalItems(finalItems);

            Plugin.DebugLogger($"Checking {finalItems.Count} new items");

            var genreExcludeList = GetGenreExcludeList(config);

            progress.Report(10d);

            bool itemAdded = false;

            if (_fileSystem.FileExists(dataPath))
            {
                existingData = _json.DeserializeFromFile<List<BlurNItem>>(dataPath);

                if (config.ChannelRefreshCount < 6)
                {
                    if (config.ChannelRefreshCount < 4)
                        existingData = existingData.GroupBy(p => p.ImdbId).Select(g => g.First()).ToList();
                    config.ChannelRefreshCount = 6;
                    Plugin.Instance.SaveConfiguration();
                }
            }

            for (int i = 0; i < finalItems.Count(); i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(10d + (86d * (Convert.ToDouble(i + 1) / Convert.ToDouble(finalItems.Count()))));

                Item item = finalItems[i];
                int year = 0;

                if (item.Link == "Failed")  // previously failed item
                    year = Convert.ToInt32(item.Content);
                else // new item
                {
                    Regex rgx = new Regex(@"\| (\d{4}) \|", RegexOptions.IgnoreCase);
                    MatchCollection matches = rgx.Matches(item.Content);
                    if (matches.Count > 0)
                    {
                        Match match = matches[matches.Count - 1];
                        Group group = match.Groups[match.Groups.Count - 1];
                        year = Convert.ToInt32(group.Value);
                    }
                }

                string url;
                url = BuildOMDbApiUrl(item, year, false);
                BlurNItem blurNItem = await ParseOMDB(url, item.PublishDate, cancellationToken).ConfigureAwait(false);
                if (blurNItem != null && string.IsNullOrEmpty(blurNItem.ImdbId) && (item.Title.EndsWith(" 3D") || item.Title.EndsWith(" 4K")) && year > 0)
                {
                    url = BuildOMDbApiUrl(item, year, true);
                    blurNItem = await ParseOMDB(url, item.PublishDate, cancellationToken).ConfigureAwait(false);
                }

                if (blurNItem == null)
                {
                    Plugin.DebugLogger($"Adding {item.Title} ({year}) to failed list");
                    failedList.List.Add(new FailedBlurNItem() { Title = item.Title, Year = year });
                }
                else if (!string.IsNullOrEmpty(blurNItem.ImdbId) && (insertList.List.Any(x => x.ImdbId == blurNItem.ImdbId) || existingData.Select(d => d.ImdbId).Contains(blurNItem.ImdbId)))
                    Plugin.DebugLogger($"{blurNItem.ImdbId} is a duplicate, skipped.");
                else if (!string.IsNullOrEmpty(blurNItem.ImdbId) && !config.AddItemsAlreadyInLibrary && libDict.ContainsKey(blurNItem.ImdbId))
                    Plugin.DebugLogger($"{blurNItem.ImdbId} is already in the library, skipped.");
                else if (!string.IsNullOrEmpty(blurNItem.ImdbId) && config.HidePlayedMovies && libDict.ContainsKey(blurNItem.ImdbId) && _userManager.Users.All(x => libDict[blurNItem.ImdbId].IsPlayed(x)))
                    Plugin.DebugLogger($"{blurNItem.ImdbId} is played by all users, skipped.");
                else if (blurNItem.Type != "movie")
                    Plugin.DebugLogger($"{blurNItem.Title} is not of type 'movie', skipped.");
                else if (genreExcludeList.Contains(blurNItem.FirstGenre))
                    Plugin.DebugLogger($"{blurNItem.Title} has first genre '{blurNItem.FirstGenre}' which is not whitelisted, skipped.");
                else if (blurNItem.ImdbRating < config.MinimumIMDBRating)
                    Plugin.DebugLogger($"{blurNItem.Title} has an IMDb rating of {blurNItem.ImdbRating} which is lower than the minimum setting of {config.MinimumIMDBRating}, skipped.");
                else if (blurNItem.ImdbVotes < config.MinimumIMDBVotes)
                    Plugin.DebugLogger($"{blurNItem.Title} has a total of {blurNItem.ImdbVotes} IMDb votes which is lower than the minimum setting of {config.MinimumIMDBVotes} votes, skipped.");
                else if (blurNItem.Released < minAge)
                    Plugin.DebugLogger($"{blurNItem.Title} was released on {blurNItem.Released.ToString("yyyy-MM-dd")} which is older than the setting of {config.Age} days, skipped.");
                else // passed all filters, adding
                {
                    await UpdateContentWithTmdbData(cancellationToken, blurNItem).ConfigureAwait(false);

                    insertList.List.Add(blurNItem);

                    await Plugin.NotificationManager.SendNotification(new NotificationRequest()
                    {
                        Date = DateTime.Now,
                        Level = NotificationLevel.Normal,
                        NotificationType = BlurNNotificationType.NewRelease,
                        Url = blurNItem.ImdbUrl,
                        Name = string.Format("[BlurN] New movie released: {0}", blurNItem.Title),
                        Description = string.Format("Year: {0}, IMDb Rating: {1} ({2} votes)", blurNItem.Year, blurNItem.ImdbRating, blurNItem.ImdbVotes.ToString("#,##0"))

                    }, cancellationToken).ConfigureAwait(false);

                    Plugin.DebugLogger($"Adding {blurNItem.Title} to the BlurN channel.");

                    itemAdded = true;
                }
            }

            if (existingData != null)
            {
                //insertList.List = insertList.List.Where(x => !existingData.Select(d => d.ImdbId).Contains(x.ImdbId)).ToList();

                foreach (BlurNItem blurNItem in existingData.Where(o => !o.TmdbId.HasValue))
                    await UpdateContentWithTmdbData(cancellationToken, blurNItem).ConfigureAwait(false);

                insertList.List.AddRange(existingData);
            }

            insertList.List = insertList.List.OrderByDescending(i => i.BluRayReleaseDate).ThenByDescending(i => i.ImdbRating).ThenByDescending(i => i.ImdbVotes).ThenByDescending(i => i.Metascore).ThenBy(i => i.Title).ToList();

            config.LastPublishDate = newPublishDate;
            if (config.DataVersion == "0" || itemAdded)
                config.DataVersion = DateTime.Now.ToString("yyyyMMddHHmmss");

            Plugin.Instance.SaveConfiguration();

            Plugin.DebugLogger($"Configuration saved. MediaBrowser.Channels.BlurN.Data.json path is {dataPath}");

            progress.Report(97d);

            _json.SerializeToFile(insertList.List, dataPath);
            _json.SerializeToFile(failedList.List, failedDataPath);

            Plugin.DebugLogger($"JSON files saved to {dataPath}");

            progress.Report(98d);

            await Tracking.Track(_httpClient, _appHost, _serverConfigurationManager, "end", "refresh", cancellationToken).ConfigureAwait(false);

            progress.Report(100);
            return;
        }

        private static List<string> GetGenreExcludeList(Configuration.PluginConfiguration config)
        {
            var excludedList = new List<string>();
            if (!config.Action) { excludedList.Add("Action"); }
            if (!config.Adventure) { excludedList.Add("Adventure"); }
            if (!config.Animation) { excludedList.Add("Animation"); }
            if (!config.Biography) { excludedList.Add("Biography"); }
            if (!config.Comedy) { excludedList.Add("Comedy"); }
            if (!config.Crime) { excludedList.Add("Crime"); }
            if (!config.Drama) { excludedList.Add("Drama"); }
            if (!config.Family) { excludedList.Add("Family"); }
            if (!config.Fantasy) { excludedList.Add("Fantasy"); }
            if (!config.FilmNoir) { excludedList.Add("Film-Noir"); }
            if (!config.History) { excludedList.Add("History"); }
            if (!config.Horror) { excludedList.Add("Horror"); }
            if (!config.Music) { excludedList.Add("Music"); }
            if (!config.Musical) { excludedList.Add("Musical"); }
            if (!config.Mystery) { excludedList.Add("Mystery"); }
            if (!config.Romance) { excludedList.Add("Romance"); }
            if (!config.SciFi) { excludedList.Add("Sci-Fi"); }
            if (!config.Sport) { excludedList.Add("Sport"); }
            if (!config.Thriller) { excludedList.Add("Thriller"); }
            if (!config.War) { excludedList.Add("War"); }
            if (!config.Western) { excludedList.Add("Western"); }
            return excludedList;
        }

        private static string BuildOMDbApiUrl(Item item, int year, bool removeLast3Chars)
        {
            return $"{baseOmdbApiUri}/?t={((removeLast3Chars) ? WebUtility.UrlEncode(WebUtility.HtmlDecode(item.Title).Remove(item.Title.Length - 3)) : WebUtility.UrlEncode(WebUtility.HtmlDecode(item.Title)))}{((year > 0) ? "&y=" + year.ToString() : "")}&plot=short&r=xml&apikey=fe53f97e";
        }

        private void ConvertPostersFromW640ToOriginal(Configuration.PluginConfiguration config, string dataPath)
        {
            if (config.ChannelRefreshCount < 3 && _fileSystem.FileExists(dataPath))
            {
                // Convert posters from w640 to original
                var existingData = _json.DeserializeFromFile<List<BlurNItem>>(dataPath);

                if (existingData != null)
                {
                    foreach (BlurNItem blurNItem in existingData.Where(o => o.TmdbId.HasValue))
                        blurNItem.Poster = blurNItem.Poster.Replace("/w640/", "/original/");

                    _json.SerializeToFile(existingData, dataPath);
                }

                config.ChannelRefreshCount = 3;
                Plugin.Instance.SaveConfiguration();
            }
        }

        private async Task UpdateContentWithTmdbData(CancellationToken cancellationToken, BlurNItem blurNItem)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                TmdbMovieSearchResult tmdbMovie = null;
                bool needEnglishLanguagePoster = false;
                bool needEnglishLanguageTitle = true;
                using (var tmdbContent = await _httpClient.Get(new HttpRequestOptions()
                {
                    Url = $"https://api.themoviedb.org/3/find/{blurNItem.ImdbId}?api_key=3e97b8d1c00a0f2fe72054febe695276&external_source=imdb_id" + ((Plugin.Instance.Configuration.UseInterfaceLanguage) ? $"&language={_serverConfigurationManager.Configuration.UICulture}&include_image_language=en,null" : ""),
                    CancellationToken = cancellationToken,
                    BufferContent = false,
                    EnableDefaultUserAgent = true,
                    AcceptHeader = "application/json,image/*",
                    EnableHttpCompression = true,
                    DecompressionMethod = CompressionMethod.Gzip
                }).ConfigureAwait(false))
                {
                    var tmdb = _json.DeserializeFromStream<TmdbMovieFindResult>(tmdbContent);
                    tmdbMovie = tmdb.movie_results.First();
                    blurNItem.Poster = $"https://image.tmdb.org/t/p/original{tmdbMovie.poster_path}";
                    blurNItem.TmdbId = tmdbMovie.id;

                    needEnglishLanguagePoster = !(!Plugin.Instance.Configuration.UseInterfaceLanguage || string.IsNullOrWhiteSpace(tmdbMovie.original_language) || tmdbMovie.original_language == "en" || _serverConfigurationManager.Configuration.UICulture.StartsWith("en") || _serverConfigurationManager.Configuration.UICulture.StartsWith(tmdbMovie.original_language));

                    if (!needEnglishLanguagePoster || tmdbMovie.original_title != tmdbMovie.title)
                    {
                        if (!string.IsNullOrWhiteSpace(tmdbMovie.title))
                        {
                            blurNItem.Title = tmdbMovie.title;
                            needEnglishLanguageTitle = false;
                        }
                        if (!string.IsNullOrWhiteSpace(tmdbMovie.overview))
                            blurNItem.Plot = tmdbMovie.overview;
                    }
                }

                if (needEnglishLanguagePoster)
                {
                    // Get English poster instead
                    using (var tmdbContent = await _httpClient.Get(new HttpRequestOptions()
                    {
                        Url = $"https://api.themoviedb.org/3/find/{blurNItem.ImdbId}?api_key=3e97b8d1c00a0f2fe72054febe695276&external_source=imdb_id&language=en&include_image_language=null",
                        CancellationToken = cancellationToken,
                        BufferContent = false,
                        EnableDefaultUserAgent = true,
                        AcceptHeader = "application/json,image/*",
                        EnableHttpCompression = true,
                        DecompressionMethod = CompressionMethod.Gzip
                    }).ConfigureAwait(false))
                    {
                        var tmdb = _json.DeserializeFromStream<TmdbMovieFindResult>(tmdbContent);
                        tmdbMovie = tmdb.movie_results.First();
                        blurNItem.Poster = $"https://image.tmdb.org/t/p/original{tmdbMovie.poster_path}";

                        if (needEnglishLanguageTitle)
                        {
                            if (!string.IsNullOrWhiteSpace(tmdbMovie.title))
                                blurNItem.Title = tmdbMovie.title;
                            if (!string.IsNullOrWhiteSpace(tmdbMovie.overview))
                                blurNItem.Plot = tmdbMovie.overview;
                        }
                    }
                }
            }
            catch
            { }
        }

        private string AddPreviouslyFailedItemsToFinalItems(List<Item> finalItems)
        {
            string failedDataPath = Path.Combine(_appPaths.PluginConfigurationsPath, "MediaBrowser.Channels.BlurN.Failed.json");

            if (_fileSystem.FileExists(failedDataPath))
            {
                var existingFailedList = _json.DeserializeFromFile<List<FailedBlurNItem>>(failedDataPath);

                if (existingFailedList != null)
                {
                    foreach (FailedBlurNItem failedItem in existingFailedList)
                    {
                        finalItems.Add(new Item() { Link = "Failed", Content = failedItem.Year.ToString(), Title = failedItem.Title });
                    }
                }
            }

            return failedDataPath;
        }

        private async Task<ItemList> GetBluRayReleaseItems(CancellationToken cancellationToken)
        {
            using (var bluRayReleaseContent = await _httpClient.Get(new HttpRequestOptions()
            {
                Url = bluRayReleaseUri,
                CancellationToken = cancellationToken,
                BufferContent = true,
                EnableDefaultUserAgent = true,
                EnableHttpCompression = true,
                DecompressionMethod = CompressionMethod.Gzip
            }).ConfigureAwait(false))
            {
                XDocument doc = XDocument.Load(bluRayReleaseContent);

                var entries = from item in doc.Root.Descendants().First(i => i.Name.LocalName == "channel").Elements().Where(i => i.Name.LocalName == "item")
                              select new Item
                              {
                                  FeedType = FeedType.RSS,
                                  Content = WebUtility.HtmlDecode(item.Elements().First(i => i.Name.LocalName == "description").Value),
                                  Link = WebUtility.HtmlDecode(item.Elements().First(i => i.Name.LocalName == "link").Value),
                                  PublishDate = ParseDate(item.Elements().First(i => i.Name.LocalName == "pubDate").Value),
                                  Title = WebUtility.HtmlDecode(item.Elements().First(i => i.Name.LocalName == "title").Value.Replace(" 4K (Blu-ray)", "").Replace(" (Blu-ray)", ""))
                              };

                ItemList items = new ItemList() { List = entries.ToList() };
                return items;
            }
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Until we can vary these default triggers per server and MBT, we need something that makes sense for both
            return new[] { 
            
                // At startup
                //new TaskTriggerInfo {Type = TaskTriggerInfo.TriggerStartup},

                // Every so often
                new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerInterval, IntervalTicks = TimeSpan.FromHours(4).Ticks}
            };
        }
    }
}