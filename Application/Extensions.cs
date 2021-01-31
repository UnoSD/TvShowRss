using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace TvShowRss
{
    static class Extensions
    {
        internal static Task<T[]> WhenAll<T>(this IEnumerable<Task<T>> tasks) => Task.WhenAll(tasks);

        internal static bool In<T>(this T value, params T[] options) => options.Any(v => v.Equals(value));
        
        internal static IActionResult ToRssOkResult(this string data) =>
            new ContentResult
            {
                ContentType = "xml/rss",
                Content = data,
                StatusCode = (int?)HttpStatusCode.OK
            };

        internal static async Task<TOut> Map<TIn, TOut>(this Task<TIn> task, Func<TIn, TOut> func) => 
            func(await task.ConfigureAwait(false));
        
        internal static async Task<TOut> Bind<TIn, TOut>(this Task<TIn> task, Func<TIn, Task<TOut>> func) => 
            await func(await task.ConfigureAwait(false)).ConfigureAwait(false);

        internal static string Join(this IEnumerable<string> values) =>
            string.Join('\n', values);        
    }
}