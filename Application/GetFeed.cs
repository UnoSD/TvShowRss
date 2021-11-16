using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using static TvShowRss.ConfigKeys;

namespace TvShowRss
{
    public static class GetFeed
    {
        static readonly DateTime FromDate = DateTime.Now.AddDays(-1 * Config.GetInt(CheckDays));

        [FunctionName(nameof(GetFeed))]
        public static Task<IActionResult> Run
        (
            [HttpTrigger(AuthorizationLevel.Anonymous, "get")]
            HttpRequest req
        ) => Finder.FindLatestEpisodeByDateAsync(FromDate, Config.GetValue(TableConnectionString), Config.GetValue(TmdbApiKey))
                   .Map(episodes => episodes.ToRssFeed())
                   .Map(feed => feed.ToRssOkResult());
    }
}