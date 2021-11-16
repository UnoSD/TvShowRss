using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using static TvShowRss.ConfigKeys;

namespace TvShowRss
{
    public static class AddShow
    {
        [FunctionName(nameof(AddShow))]
        public static Task<IActionResult> Run
        (
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")]
            HttpRequest req,
            ILogger log
        ) => Adder.AddSeriesToCheckAsync(Config.GetValue(TableConnectionString), req.Query["id"], log)
                  .Map(x => (IActionResult)new OkObjectResult(x));
    }
}