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
            return Compile(code, token, null);
        }

        public static Func<object> Compile(string code, CancellationToken token, BridgeRequestTimings timings)
        {
            Action<string, double> recordTiming = timings != null && timings.IsEnabled ? (Action<string, double>)timings.Add : null;
            return RoslynDependencyResolver.Compile(code, token, recordTiming);
        }

        public static object Validate(string code, CancellationToken token)
        {
            return Validate(code, token, null);
        }

        public static object Validate(string code, CancellationToken token, BridgeRequestTimings timings)
        {
            Action<string, double> recordTiming = timings != null && timings.IsEnabled ? (Action<string, double>)timings.Add : null;
            return RoslynDependencyResolver.Validate(code, token, recordTiming);
        }
    }
}
