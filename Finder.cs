using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TraktApiSharp.Objects.Get.Shows.Episodes;
using TraktApiSharp.Objects.Get.Shows.Seasons;
using static TvShowRss.Storage;
using static TvShowRss.TraktClientFactory;

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
                          .Select(s => (s.name, episodes: GetLatestEpisodes(s.season, fromDate)))
                          .Where(s => s.episodes.Any())
                          .SelectMany(s => s.episodes.Select(te => new Episode
                          {
                              ShowName = s.name,
                              Title = te.Title,
                              Season = te.SeasonNumber ?? 0,
                              Number = te.Number ?? 0,
                              Link = $"https://{te.Ids.Trakt}/{te.SeasonNumber}/{te.Number}",
                              Date = te.FirstAired ?? DateTime.MinValue
                          }));
        }

        static TraktSeason LastSeason(Task<IEnumerable<TraktSeason>> task) => 
            task.Result
                .Where(season => season.FirstAired.HasValue)
                .OrderBy(season => season.Number ?? 0)
                .LastOrDefault();

        static async Task<(string name, TraktSeason season)> GetLastSeason(Series series) => 
            (series.Name, 
             await TraktClient.GetSeasonsAsync(series.Id.ToString())
                              .ContinueWith(LastSeason));

        static IReadOnlyCollection<TraktEpisode> GetLatestEpisodes(TraktSeason season, DateTime fromDate) =>
            season.Episodes
                  .Where(e => e.FirstAired >= fromDate &&
                              e.FirstAired <= DateTime.UtcNow)
                  .ToList();
    }
}