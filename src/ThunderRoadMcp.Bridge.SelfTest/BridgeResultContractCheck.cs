using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ThunderRoadMcp.Bridge;

internal static class BridgeResultContractCheck
{
    public static void Run()
    {
        foreach (var depth in new[] { 20, 60, 61, 62 })
        {
            var json = BridgeRequestHandler.SerializeResult(NestedValue(depth));
            Assert(JObject.Parse(json) != null, "nested-" + depth + " bridge result serializes normally");
        }
        foreach (var depth in new[] { 63, 70 })
        {
            try
            {
                BridgeRequestHandler.SerializeResult(NestedValue(depth));
                throw new Exception("Expected a bridge result depth rejection at " + depth + ".");
            }
            catch (BridgeResultDepthException)
            {
                Assert(true, "nested-" + depth + " result is intercepted before host parsing");
            }
        }

        var handler = new BridgeRequestHandler(new BridgeConfig());
        var successful = (BridgeJsonResult)DispatchAndDrain(handler, EvaluationRequest(BuildSnippet(62)));
        Assert(JObject.Parse(successful.Json) != null, "evaluation with result depth 62 returns normal mirrored JSON");
        try
        {
            DispatchAndDrain(handler, EvaluationRequest(BuildSnippet(63)));
            throw new Exception("Expected the nested-63 evaluation to return a structured depth failure.");
        }
        catch (BridgeEvaluationException exception)
        {
            Assert(exception.errorCode == "evaluation.result_depth_exceeded" &&
                   exception.executionOutcome == "completed_result_unavailable",
                "nested-63 evaluation reports a parseable completed-result-unavailable failure");
            byte[] failureBytes = SerializeFailure(exception);
            var failureResponse = JObject.Parse(Encoding.UTF8.GetString(failureBytes).TrimEnd('\n'));
            Assert(failureBytes.Length <= BridgeRequestHandler.MaxLineBytes && failureBytes[failureBytes.Length - 1] == (byte)'\n' &&
                   (bool)failureResponse["success"] == false &&
                   (string)failureResponse["errorCode"] == "evaluation.result_depth_exceeded" &&
                   (string)failureResponse["executionOutcome"] == "completed_result_unavailable",
                "depth-63 bridge failure writer emits a small parseable structured response");
        }

        var oversized = BridgeServer.BuildRawSuccessResponse("depth-check",
            "\"" + new string('x', BridgeRequestHandler.MaxLineBytes) + "\"", null, evaluationResult: true);
        var error = JObject.Parse(Encoding.UTF8.GetString(oversized).TrimEnd('\n'));
        Assert(oversized.Length <= BridgeRequestHandler.MaxLineBytes && oversized[oversized.Length - 1] == (byte)'\n' &&
               (bool)error["success"] == false && (string)error["errorCode"] == "evaluation.result_unavailable" &&
               (string)error["executionOutcome"] == "completed_result_unavailable",
            "oversized evaluation envelope becomes a small parseable structured failure within the 64-KiB limit");

        VerifyRoslynDiagnostics(handler);
        VerifyWarningFloodDiagnostics(handler);
        VerifyEvaluationOutcomes(handler);
        VerifyPostStartCancellation(handler);
        VerifyPostInvocationCancellation(handler);
        VerifyBoundedCompilerDiagnostics(handler);
        VerifyClippedFailureMessage();
    }


    private static void VerifyRoslynDiagnostics(BridgeRequestHandler handler)
    {
        const string code = "#line 700 \"virtual.cs\"\nvar present = 1;\nreturn missingSymbol;";
        try
        {
            handler.DispatchAsync(EvaluationRequest(code), CancellationToken.None,
                BridgeRequestTimings.CreateForSelfTest()).GetAwaiter().GetResult();
            throw new Exception("Expected a structured Roslyn compiler diagnostic.");
        }
        catch (BridgeEvaluationException exception)
        {
            var items = JArray.FromObject(exception.diagnostics);
            var diagnostic = items.Children<JObject>().Single(item => (string)item["id"] == "CS0103");
            var span = (JObject)diagnostic["span"];
            Assert(exception.errorCode == "evaluation.compilation_failed" && exception.executionOutcome == "not_started" &&
                   exception.Message.IndexOf("CS0103", StringComparison.Ordinal) >= 0 &&
                   (string)diagnostic["severity"] == "error" && (int)diagnostic["line"] == 3 &&
                   (int)diagnostic["column"] == 8 && (int)span["start"] == code.IndexOf("missingSymbol", StringComparison.Ordinal) &&
                   (int)span["length"] == "missingSymbol".Length,
                "compiler errors preserve readable text and actual Roslyn ID, severity, message, and snippet coordinates");
        }
    }
    private static void VerifyWarningFloodDiagnostics(BridgeRequestHandler handler)
    {
        string code = string.Join("\n", Enumerable.Repeat("#warning repeated diagnostic", 25)) +
            "\nreturn missingAfterWarnings;";
        try
        {
            DispatchAndDrain(handler, EvaluationRequest(code));
            throw new Exception("Expected the warning-flood evaluation to fail compilation.");
        }
        catch (BridgeEvaluationException exception)
        {
            var diagnostics = JArray.FromObject(exception.diagnostics);
            int firstError = exception.Message.IndexOf("CS0103", StringComparison.Ordinal);
            int firstWarning = exception.Message.IndexOf("CS1030", StringComparison.Ordinal);
            int retainedRepeatedWarnings = diagnostics.Count(item => (string)item["id"] == "CS1030");
            int distinctWarningLines = diagnostics.Children<JObject>()
                .Where(item => (string)item["id"] == "CS1030")
                .Select(item => (int)item["line"])
                .Distinct()
                .Count();
            var allDiagnostics = exception.InnerException?.GetType().GetProperty("Diagnostics")
                ?.GetValue(exception.InnerException) as System.Collections.IEnumerable;
            int omittedCount = (allDiagnostics?.Cast<object>().Count() ?? 0) - diagnostics.Count;
            Assert(diagnostics.Count == 20 && (string)diagnostics[0]["id"] == "CS0103" &&
                   diagnostics.Count(item => (string)item["severity"] == "warning") == 19 &&
                   retainedRepeatedWarnings >= 2 && distinctWarningLines == retainedRepeatedWarnings &&
                   firstError >= 0 && firstWarning > firstError && exception.diagnosticsTruncated &&
                   exception.omittedDiagnosticCount == omittedCount && omittedCount >= 6,
                "late compiler errors rank ahead of warning floods while repeated warnings at distinct source locations remain and omissions are counted");
        }
    }


    private static void VerifyEvaluationOutcomes(BridgeRequestHandler handler)
    {
        BridgeEvaluationSideEffectProbe.Reset();
        try
        {
            DispatchAndDrain(handler, EvaluationRequest(
                "BridgeEvaluationSideEffectProbe.Increment(); return missingEvaluationProbe;"));
            throw new Exception("Expected the invalid evaluation to fail compilation.");
        }
        catch (BridgeEvaluationException exception)
        {
            Assert(exception.executionOutcome == "not_started" && BridgeEvaluationSideEffectProbe.Counter == 0,
                "compiler failure is not_started and leaves the evaluation side-effect counter untouched");
        }

        try
        {
            DispatchAndDrain(handler, EvaluationRequest(
                "BridgeEvaluationSideEffectProbe.Increment(); return new System.Threading.Tasks.TaskCompletionSource<int>().Task;"));
            throw new Exception("Expected the unsupported Task result to fail serialization.");
        }
        catch (BridgeEvaluationException exception)
        {
            Assert(exception.executionOutcome == "completed_result_unavailable" &&
                   exception.errorCode == "evaluation.result_unavailable" &&
                   BridgeEvaluationSideEffectProbe.Counter == 1,
                "side effect before result serialization failure is reported completed exactly once");
        }

        try
        {
            DispatchAndDrain(handler, EvaluationRequest(
                "BridgeEvaluationSideEffectProbe.Increment(); throw new System.InvalidOperationException(\"post-start failure\");"));
            throw new Exception("Expected the post-start evaluation failure.");
        }
        catch (BridgeEvaluationException exception)
        {
            var failureResponse = JObject.Parse(Encoding.UTF8.GetString(SerializeFailure(exception)).TrimEnd('\n'));
            Assert(exception.executionOutcome == "unknown" && exception.errorCode == "evaluation.invocation_failed" &&
                   BridgeEvaluationSideEffectProbe.Counter == 2 &&
                   exception.Message.IndexOf("System.InvalidOperationException", StringComparison.Ordinal) >= 0 &&
                   (string)failureResponse["error"] == exception.Message &&
                   (string)failureResponse["errorCode"] == "evaluation.invocation_failed" &&
                   (string)failureResponse["executionOutcome"] == "unknown",
                "post-start runtime failure preserves bounded exception details and unknown outcome in the bridge response");
        }
    }

    private static void VerifyPostStartCancellation(BridgeRequestHandler handler)
    {
        using var cancellation = new CancellationTokenSource();
        BridgeEvaluationSideEffectProbe.Reset();
        var cancelAfterStart = Task.Run(() =>
        {
            while (BridgeEvaluationSideEffectProbe.Counter == 0 && !cancellation.IsCancellationRequested)
                Thread.Sleep(1);
            if (BridgeEvaluationSideEffectProbe.Counter > 0)
                cancellation.Cancel();
        });
        try
        {
            try
            {
                DispatchAndDrain(handler, EvaluationRequest(
                    "BridgeEvaluationSideEffectProbe.Increment(); System.Threading.Thread.Sleep(250); return 7;"),
                    cancellation.Token);
                throw new Exception("Expected cancellation after invocation to report an unknown outcome.");
            }
            catch (BridgeEvaluationException exception)
            {
                Assert(exception.errorCode == "evaluation.outcome_unknown" &&
                       exception.executionOutcome == "unknown" &&
                       BridgeEvaluationSideEffectProbe.Counter == 1,
                    "post-start cancellation reports unknown after one side effect");
            }
        }
        finally
        {
            cancellation.Cancel();
            cancelAfterStart.GetAwaiter().GetResult();
        }
    }

    private static void VerifyPostInvocationCancellation(BridgeRequestHandler handler)
    {
        using var cancellation = new CancellationTokenSource();
        BridgeEvaluationSerializationProbe.Reset();
        var cancelDuringSerialization = Task.Run(() =>
        {
            while (!BridgeEvaluationSerializationProbe.SerializationStarted && !cancellation.IsCancellationRequested)
                Thread.Sleep(1);
            if (BridgeEvaluationSerializationProbe.SerializationStarted)
                cancellation.Cancel();
        });
        try
        {
            try
            {
                DispatchAndDrain(handler, EvaluationRequest("return new BridgeEvaluationSerializationProbe();"),
                    cancellation.Token);
                throw new Exception("Expected cancellation during result serialization.");
            }
            catch (BridgeEvaluationException exception)
            {
                var failureResponse = JObject.Parse(Encoding.UTF8.GetString(SerializeFailure(exception)).TrimEnd('\n'));
                Assert(exception.errorCode == "evaluation.result_unavailable" &&
                       exception.executionOutcome == "completed_result_unavailable" &&
                       BridgeEvaluationSerializationProbe.SerializationStarted &&
                       (string)failureResponse["errorCode"] == "evaluation.result_unavailable" &&
                       (string)failureResponse["executionOutcome"] == "completed_result_unavailable",
                    "cancellation during serialization preserves the completed-but-unavailable outcome in the bridge response");
            }
        }
        finally
        {
            cancellation.Cancel();
            cancelDuringSerialization.GetAwaiter().GetResult();
        }
    }

    private static void VerifyBoundedCompilerDiagnostics(BridgeRequestHandler handler)
    {
        string longSymbol = new string('x', 3000);
        try
        {
            DispatchAndDrain(handler, EvaluationRequest("return " + longSymbol + ";"));
            throw new Exception("Expected the long unresolved symbol to fail compilation.");
        }
        catch (BridgeEvaluationException exception)
        {
            var diagnostics = JArray.FromObject(exception.diagnostics);
            var diagnostic = diagnostics.Children<JObject>().Single(item => (string)item["id"] == "CS0103");
            byte[] responseBytes = SerializeFailure(exception);
            var response = JObject.Parse(Encoding.UTF8.GetString(responseBytes).TrimEnd('\n'));
            var responseDiagnostic = ((JArray)response["diagnostics"]).Children<JObject>()
                .Single(item => (string)item["id"] == "CS0103");
            Assert(((string)diagnostic["message"]).Length == 256 &&
                   (bool)diagnostic["messageTruncated"] &&
                   ((string)responseDiagnostic["message"]).Length == 256 &&
                   (bool)responseDiagnostic["messageTruncated"] &&
                   (bool)response["diagnosticsTruncated"] == false &&
                   (int)response["omittedDiagnosticCount"] == 0 &&
                   responseBytes.Length <= BridgeRequestHandler.MaxLineBytes &&
                   responseBytes[responseBytes.Length - 1] == (byte)'\n',
                "long Roslyn messages carry truthful per-message truncation metadata within the bounded bridge envelope");
        }
    }

    private static void VerifyClippedFailureMessage()
    {
        var failure = new BridgeEvaluationException("evaluation.compilation_failed", new string('x', 5000),
            "not_started");
        byte[] responseBytes = SerializeFailure(failure);
        var response = JObject.Parse(Encoding.UTF8.GetString(responseBytes).TrimEnd('\n'));
        Assert(((string)response["error"]).Length == 4096 && (bool)response["messageTruncated"] &&
               responseBytes.Length <= BridgeRequestHandler.MaxLineBytes &&
               responseBytes[responseBytes.Length - 1] == (byte)'\n',
            "bridge error clipping is explicitly reported and remains within the bounded wire payload");
    }

    private static byte[] SerializeFailure(BridgeEvaluationException exception)
    {
        using var stream = new MemoryStream();
        using var client = new TcpClient();
        BridgeServer.WriteFailure(stream, "depth-check", exception, null, client).GetAwaiter().GetResult();
        return stream.ToArray();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception("Bridge result contract check failed: " + message);
    }

    private static object NestedValue(int depth)
    {
        JToken value = new JObject { ["leaf"] = true };
        for (var level = 1; level < depth; level++)
            value = new JObject { ["child"] = value };
        return value;
    }

    private static string BuildSnippet(int depth) =>
        "object value = new { leaf = true }; for (int i = 1; i < " + depth + "; i++) value = new { child = value }; return value;";

    private static BridgeRequest EvaluationRequest(string code) => new BridgeRequest
    {
        method = "evaluate",
        parameters = JObject.FromObject(new { code, timeoutMs = 30000 })
    };

    private static object DispatchAndDrain(BridgeRequestHandler handler, BridgeRequest request, CancellationToken cancellationToken = default)
    {
        var dispatch = handler.DispatchAsync(request, cancellationToken, BridgeRequestTimings.CreateForSelfTest());
        var deadline = Stopwatch.StartNew();
        while (!dispatch.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            MainThreadQueue.Drain();
            Thread.Sleep(1);
        }
        if (!dispatch.IsCompleted)
            throw new TimeoutException("Bridge self-test dispatch did not complete.");
        return dispatch.GetAwaiter().GetResult();
    }

}

public static class BridgeEvaluationSideEffectProbe
{
    private static int counter;

    public static int Counter => Volatile.Read(ref counter);
    public static int Increment() => Interlocked.Increment(ref counter);
    public static void Reset() => Interlocked.Exchange(ref counter, 0);
}

public sealed class BridgeEvaluationSerializationProbe
{
    private static int serializationStarted;

    public static bool SerializationStarted => Volatile.Read(ref serializationStarted) != 0;
    public int Result
    {
        get
        {
            Interlocked.Exchange(ref serializationStarted, 1);
            Thread.Sleep(250);
            return 7;
        }
    }
    public static void Reset() => Interlocked.Exchange(ref serializationStarted, 0);
}
