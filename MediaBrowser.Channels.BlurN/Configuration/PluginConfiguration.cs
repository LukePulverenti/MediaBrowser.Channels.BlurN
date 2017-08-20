﻿using System;
using System.Reflection;
using MediaBrowser.Model.Plugins;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Collections.Generic;

namespace MediaBrowser.Channels.BlurN.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public decimal MinimumIMDBRating { get; set; }
        public int MinimumIMDBVotes { get; set; }
        public int Age { get; set; }
        public DateTime LastPublishDate { get; set; }
        public Boolean AddItemsAlreadyInLibrary { get; set; }
        public Boolean HidePlayedMovies { get; set; }
        public Boolean EnableDebugLogging { get; set; }
        public int ChannelRefreshCount { get; set; }
        public string InstallationID { get; set; }
        public bool Action { get; set; }
        public bool Adventure { get; set; }
        public bool Animation { get; set; }
        public bool Biography { get; set; }
        public bool Comedy { get; set; }
        public bool Crime { get; set; }
        public bool Drama { get; set; }
        public bool Family { get; set; }
        public bool Fantasy { get; set; }
        public bool FilmNoir { get; set; }
        public bool History { get; set; }
        public bool Horror { get; set; }
        public bool Music { get; set; }
        public bool Musical { get; set; }
        public bool Mystery { get; set; }
        public bool Romance { get; set; }
        public bool SciFi { get; set; }
        public bool Sport { get; set; }
        public bool Thriller { get; set; }
        public bool War { get; set; }
        public bool Western { get; set; }
        public string BlurNVersion { get { return typeof(PluginConfiguration).GetTypeInfo().Assembly.GetName().Version.ToString(); } }
        public string BlurNLatestVersion { get { return GetLatestVersion().Result; } }

        private string Branch
        {
            get
            {
                if (BlurNVersion.EndsWith("26"))
                    return "Pre3.2.27.0";
                if (BlurNVersion.EndsWith("27"))
                    return "3.2.27.0";
                return "master";
            }
        }

        private string LatestVersionURI
        {
            get { return $"https://raw.githubusercontent.com/MarkCiliaVincenti/MediaBrowser.Channels.BlurN/{Branch}/MediaBrowser.Channels.BlurN/latestversion.txt"; }
        }

        private async Task<string> GetLatestVersion()
        {
            try
            {
                using (var _httpClient = new HttpClient())
                    using (var response = await _httpClient.GetAsync(LatestVersionURI, CancellationToken.None).ConfigureAwait(false))
                        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch
            {
                return "could not be retrieved";
            }
        }

        public PluginConfiguration()
        {
            ChannelRefreshCount = 1;
            MinimumIMDBRating = 6.8m;
            MinimumIMDBVotes = 1000;
            Age = 730;
            LastPublishDate = DateTime.MinValue;
            AddItemsAlreadyInLibrary = true;
            HidePlayedMovies = true;
            EnableDebugLogging = false;
            InstallationID = Guid.NewGuid().ToString();
            Action = true;
            Adventure = true;
            Animation = true;
            Biography = true;
            Comedy = true;
            Crime = true;
            Drama = true;
            Family = true;
            Fantasy = true;
            FilmNoir = true;
            History = true;
            Horror = true;
            Music = true;
            Musical = true;
            Mystery = true;
            Romance = true;
            SciFi = true;
            Sport = true;
            Thriller = true;
            War = true;
            Western = true;
        }
    }
}
