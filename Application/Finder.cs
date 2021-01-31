using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TraktNet.Objects.Get.Episodes;
using TraktNet.Objects.Get.Seasons;
using static TvShowRss.Storage;
using static TvShowRss.TraktClientFactory;
using static TvShowRss.ConfigKeys;

namespace TvShowRss
{
    static class Finder
    {
        internal static async Task<IEnumerable<Episode>> FindLatestEpisodeByDateAsync(DateTime fromDate, string dbFile)
        {
            var seasons = 
                await GetAll<Series>(dbFile, s => s.PartitionKey == "Series" && 
                                             s.IsRunning).Select(GetLastSeason)
                                                         .WhenAll();

            var seasonImageLinkCache =
                new ConcurrentDictionary<string, Task<string>>();
            
            return await 
                seasons.Where(s => s.season != null) // Upcoming tv series will come up as null
                       .Select(s => (s.name, episodes: GetLatestEpisodes(s.season, fromDate), s.tmdbId))
                       .Where(s => s.episodes.Any())
                       .SelectMany(s => s.episodes.Select(async te =>
                       {
                           var imageLinkAsync = 
                               s.tmdbId.HasValue && te.SeasonNumber.HasValue ?
                               await GetImageLinkAsync(s.tmdbId.Value, te.SeasonNumber.Value, te.Number).ConfigureAwait(false) :
                               string.Empty;

                           var seasonImageLink = 
                               await seasonImageLinkCache.GetOrAdd($"{s.tmdbId}{te.SeasonNumber}", _ =>
                                        s.tmdbId.HasValue && te.SeasonNumber.HasValue ?
                                        GetImageLinkAsync(s.tmdbId.Value, te.SeasonNumber.Value) :
                                        Task.FromResult(string.Empty))
                                   .ConfigureAwait(false);
                           
                           return new Episode
                           {
                               ShowName = s.name,
                               Title = te.Title,
                               Season = te.SeasonNumber ?? 0,
                               Number = te.Number ?? 0,
                               Link = imageLinkAsync,
                               Date = te.FirstAired ?? DateTime.MinValue,
                               ImageLink = imageLinkAsync,
                               SeasonImageLink = seasonImageLink
                           };
                       }))
                       .WhenAll();
        }

        static readonly Lazy<HttpClient> HttpClient = new Lazy<HttpClient>();

        static string TmdbApiUrl(uint tmdbId, int season, int? episode) => 
            $"https://api.themoviedb.org/3/tv/{tmdbId}/season/{season}" +
            (episode.HasValue ? $"/episode/{episode.Value}" : "") +
            $"?api_key={Config.GetValue(TmdbApiKey)}";

        static Task<string> GetTmdbDataAsync(uint tmdbId, int season, int? episode) =>
            HttpClient.Value
                      .GetAsync(TmdbApiUrl(tmdbId, season, episode))
                      .Bind(r => r.IsSuccessStatusCode ?
                                 r.Content.ReadAsStringAsync() :
                                 Task.FromResult(string.Empty));

        static JObject TryParseJson(string json)
        {
            try
            {
                return JObject.Parse(json);
            }
            catch
            {
                return new JObject();
            }
        }
        
        static Task<string> GetImageLinkAsync(uint tmdbId, int season, int? episode = null) =>
            GetTmdbDataAsync(tmdbId, season, episode)
                .Map(TryParseJson)
                .Map(j => j[episode.HasValue ? 
                            "still_path" :
                            "poster_path"]?.ToString())
                .Map(p => p is null ? "" : "http://image.tmdb.org/t/p/original" + p);
        
        static ITraktSeason LastSeason(Task<IEnumerable<ITraktSeason>> task) => 
            task.Result
                .Where(season => season.FirstAired.HasValue)
                .OrderBy(season => season.Number ?? 0)
                .LastOrDefault();

        static async Task<(string name, uint? tmdbId, ITraktSeason season)> GetLastSeason(Series series) => 
            (series.Name,
             await TraktClient.Shows.GetShowAsync(series.Id.ToString()).Map(x => x.Value.Ids.Tmdb),
             await TraktClient.GetSeasonsAsync(series.Id.ToString())
                              .ContinueWith(LastSeason));

        static IReadOnlyCollection<ITraktEpisode> GetLatestEpisodes(ITraktSeason season, DateTime fromDate) =>
            season.Episodes
                  .Where(e => e.FirstAired >= fromDate &&
                              e.FirstAired <= DateTime.UtcNow)
                  .ToList();
    }
}