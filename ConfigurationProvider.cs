using System;
using Microsoft.Extensions.Configuration;

namespace TvShowRss
{
    static class ConfigurationProvider
    {
        internal static Lazy<IConfigurationRoot> Configuration =>
            new Lazy<IConfigurationRoot>(() =>
                new ConfigurationBuilder().AddEnvironmentVariables()
                                          .Build());
    }
}