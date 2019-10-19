using System;

namespace TvShowRss
{
    class Episode
    {
        internal string ShowName { get; set; }
        internal int Season { get; set; }
        internal int Number { get; set; }
        internal string Title { get; set; }
        internal string Link { get; set; }
        internal DateTime Date { get; set; }
    }
}