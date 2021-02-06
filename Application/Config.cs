using System;

namespace TvShowRss
{
    static class Config
    {
        public static string GetValue(string key) =>
            Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Process);

        public static int GetInt(string key) =>
            int.Parse(GetValue(key));
    }
}