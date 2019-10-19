using System;

namespace TvShowRss
{
    static class Config
    {
        public static string GetValue(string key) =>
            Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Process);
    }
}