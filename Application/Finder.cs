using System;
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

            return seasons.Where(s => s.season != null) // Upcoming tv series will come up as null
                          .Select(s => (s.name, episodes: GetLatestEpisodes(s.season, fromDate), s.tmdbId))
                          .Where(s => s.episodes.Any())
                          .SelectMany(s => s.episodes.Select(te => new Episode
                          {
                              ShowName = s.name,
                              Title = te.Title,
                              Season = te.SeasonNumber ?? 0,
                              Number = te.Number ?? 0,
                              Link = GetEpisodeImageLink(s.tmdbId, te.SeasonNumber, te.Number),
                              Date = te.FirstAired ?? DateTime.MinValue,
                              ImageLink = GetEpisodeImageLink(s.tmdbId, te.SeasonNumber, te.Number),
                              SeasonImageLink = GetSeasonImageLink(s.tmdbId, te.SeasonNumber)
                          }));
        }

        static readonly Lazy<HttpClient> HttpClient = new Lazy<HttpClient>();

        static string GetPosterPath(uint tmdbId, int? season) =>
            JObject.Parse(
            HttpClient.Value
                .GetStringAsync($"https://api.themoviedb.org/3/tv/{tmdbId}/season/{season}?api_key={Config.GetValue(TmdbApiKey)}")
                .GetAwaiter()
                .GetResult())
                ["poster_path"]!.ToString();
        
        static string GetPosterPath(uint tmdbId, int? season, int? episode) =>
            JObject.Parse(
            HttpClient.Value
                .GetStringAsync($"https://api.themoviedb.org/3/tv/{tmdbId}/season/{season}/episode/{episode}?api_key={Config.GetValue(TmdbApiKey)}")
                .GetAwaiter()
                .GetResult())
                ["still_path"]!.ToString();
        
        static string GetSeasonImageLink(uint? tmdbId, int? season) =>
            tmdbId.HasValue ? "http://image.tmdb.org/t/p/original" + GetPosterPath(tmdbId.Value, season) : "";

        static string GetEpisodeImageLink(uint? tmdbId, int? season, int? episode) =>
            tmdbId.HasValue ? "http://image.tmdb.org/t/p/original" + GetPosterPath(tmdbId.Value, season, episode) : "";

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