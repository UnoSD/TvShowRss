using System.Threading.Tasks;
using Pulumi;

namespace TvShowRss
{
    static class Program
    {
        static Task<int> Main() => Deployment.RunAsync<MyStack>();
    }
}
