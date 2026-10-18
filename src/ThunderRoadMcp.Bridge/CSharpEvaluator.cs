using System;
using System.Threading;

namespace ThunderRoadMcp.Bridge
{
    internal static class CSharpEvaluator
    {
        public static void Configure(string payloadDirectory)
        {
            RoslynDependencyResolver.Configure(payloadDirectory);
        }

        public static Func<object> Compile(string code, CancellationToken token)
        {
            return RoslynDependencyResolver.Compile(code, token);
        }
    }
}
