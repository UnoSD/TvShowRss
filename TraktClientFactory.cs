using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TraktApiSharp;
using TraktApiSharp.Enums;
using TraktApiSharp.Exceptions;
using TraktApiSharp.Objects.Get.Shows;
using TraktApiSharp.Objects.Get.Shows.Seasons;
using TraktApiSharp.Requests.Params;
using static TvShowRss.ConfigurationProvider;

namespace TvShowRss
{
    static class TraktClientFactory
    {
        const string ConfigurationKeyClientId = "TraktClientId";
        const string ConfigurationKeyClientSecret = "TraktClientSecret";

        static readonly TraktExtendedInfo Info = new TraktExtendedInfo
        {
            Episodes = true,
            Full = true,
            Images = false,
            Metadata = true,
            NoSeasons = false
        };

        static readonly Lazy<TraktClient> Client =
            new Lazy<TraktClient>(() => 
                new TraktClient(Configuration.Value[ConfigurationKeyClientId], 
                                Configuration.Value[ConfigurationKeyClientSecret]));

        internal static TraktClient TraktClient => Client.Value;

        static async Task<TraktShow> GetShowAsync(this TraktClient client, string serie)
        {
            try
            {
                return await client.Shows.GetShowAsync(serie, Info);
            }
            catch (TraktShowNotFoundException)
            {
                return new TraktShow { Ids = new TraktShowIds { Slug = serie, Trakt = 0 } };
            }
        }

        internal static Task<IEnumerable<TraktSeason>> GetSeasonsAsync(this TraktClient client, string serie) =>
            client.Seasons.GetAllSeasonsAsync(serie, Info);

        internal static Task<IReadOnlyCollection<TraktShow>> FindShowByIdAsync(this TraktClient client, string name) =>
            client.Search
                  .GetTextQueryResultsAsync(TraktSearchResultType.Show, 
                                            name, 
                                            TraktSearchField.Aliases)
                  .ContinueWith(result => (IReadOnlyCollection<TraktShow>)result.Result
                                                                                .Items
                                                                                .Select(item => item.Show)
                                                                                .ToList())
                  .WithIdResultAsync(client, name);

        static async Task<IReadOnlyCollection<TraktShow>> WithIdResultAsync
        (
            this Task<IReadOnlyCollection<TraktShow>> showsTask,
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