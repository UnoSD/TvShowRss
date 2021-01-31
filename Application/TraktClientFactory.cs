using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TraktNet;
using TraktNet.Enums;
using TraktNet.Exceptions;
using TraktNet.Objects.Get.Seasons;
using TraktNet.Objects.Get.Shows;
using TraktNet.Requests.Parameters;
using static TvShowRss.ConfigKeys;

namespace TvShowRss
{
    static class TraktClientFactory
    {
        static readonly TraktExtendedInfo Info = new TraktExtendedInfo
        {
            Episodes = true,
            Full = true,
            GuestStars = false,
            Metadata = true,
            NoSeasons = false,
        };

        static readonly Lazy<TraktClient> Client =
            new Lazy<TraktClient>(() => 
                new TraktClient(Config.GetValue(TraktClientId), 
                                Config.GetValue(TraktClientSecret)));

        internal static TraktClient TraktClient => Client.Value;

        static async Task<ITraktShow> GetShowAsync(this TraktClient client, string serie)
        {
            try
            {
                var response = await client.Shows.GetShowAsync(serie, Info);
                
                return response.Value;
            }
            catch (TraktShowNotFoundException)
            {
                return new TraktShow { Ids = new TraktShowIds { Slug = serie, Trakt = 0 } };
            }
        }

        internal static async Task<IEnumerable<ITraktSeason>> GetSeasonsAsync(this TraktClient client, string serie)
        {
            var response = await client.Seasons.GetAllSeasonsAsync(serie, Info);
            
            return response.Value;
        }

        internal static Task<IReadOnlyCollection<ITraktShow>> FindShowByIdAsync(this TraktClient client, string name) =>
            client.Search
                  .GetTextQueryResultsAsync(TraktSearchResultType.Show, 
                                            name, 
                                            TraktSearchField.Aliases)
                  .ContinueWith(result => (IReadOnlyCollection<ITraktShow>)result.Result
                                                                                 .Value
                                                                                 .Select(item => item.Show)
                                                                                 .ToList())
                  .WithIdResultAsync(client, name);

        static async Task<IReadOnlyCollection<ITraktShow>> WithIdResultAsync
        (
            this Task<IReadOnlyCollection<ITraktShow>> showsTask,
            TraktClient client,
            string name
        )
        {
            var results = await showsTask;
            
            if (results.Count >= 1) return results;
            
            var show = await client.GetShowAsync(name);
            
            return show.Ids.Trakt != 0 ? new[] { show } : new TraktShow[0];
        }
    }
}