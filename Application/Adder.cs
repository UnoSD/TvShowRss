using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TraktNet.Enums;
using static TvShowRss.Storage;
using static TvShowRss.TraktClientFactory;

namespace TvShowRss
{
    static class Adder
    {
        internal static async Task<string> AddSeriesToCheckAsync(string dbFile, string name, ILogger log)
        {
            var entity = 
                await TraktClient.FindShowByIdAsync(name)
                                 .Map(shows => shows.First())
                                 .Map(show => new Series
                                 {
                                     Id = show.Ids.Trakt,
                                     Name = show.Title,
                                     IsRunning = !show.Status.In(TraktShowStatus.Canceled,
                                                                 TraktShowStatus.Ended)
                                 });
            
            log.LogInformation(entity.Name);

            Save(dbFile, entity);

            return entity.Name;
        }
    }
}