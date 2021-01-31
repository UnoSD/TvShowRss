using System;
using System.Collections.Generic;
using System.Linq;

namespace TvShowRss
{
    static class RssFeed
    {
        static string ToRssFeed(this string items, string title, string link) => $@"
<rss version=""2.0"" xmlns:content=""http://purl.org/rss/1.0/modules/content/"">
    <channel>
        <title>{title}</title>
        <description>TV shows episodes RSS feed</description>
        <link>{link}</link>
        <ttl>240</ttl>
        {items}
    </channel>
</rss>
";

        static string RssItem(string title, string description, string link, string guid, DateTime date, string episodeImageLink, string seasonImageLink) => $@"
<item>
    <title>{title}</title>
    <description>{description}</description>
    <link>{link}</link>
    <guid isPermaLink=""false"">{guid}</guid>
    <pubDate>{date:ddd, dd MMM yyyy HH:mm:ss K}</pubDate>
    <content:encoded>
        <![CDATA[             
            <img src=""{episodeImageLink}""/>
            <img src=""{seasonImageLink}""/>
        ]]>
    </content:encoded>
</item>
";

        public static string ToRssFeed(this IEnumerable<Episode> episodes) => 
            episodes.ToRssItems()
                .Join()
                .Replace("\n", "\n        ")
                .ToRssFeed("TV shows", "https://github.com/UnoSD/TvShowRss");

        static IEnumerable<string> ToRssItems(this IEnumerable<Episode> episodes) =>
            episodes.Select(episode => RssItem($"{episode.ShowName} {episode.Season}x{episode.Number}",
                episode.Title,
                episode.Link,
                episode.Link,
                episode.Date,
                episode.ImageLink,
                episode.SeasonImageLink));
    }
}