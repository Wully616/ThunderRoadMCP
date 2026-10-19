using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using ThunderRoad;

namespace ThunderRoadMcp.Compiler
{
    public static class CSharpEvaluator
    {
        private static readonly HashSet<string> CompilerDependencyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ThunderRoadMcp.Compiler",
            "Microsoft.CodeAnalysis",
            "Microsoft.CodeAnalysis.CSharp",
            "System.Buffers",
            "System.Collections.Immutable",
            "System.Memory",
            "System.Numerics.Vectors",
            "System.Reflection.Metadata",
            "System.Runtime.CompilerServices.Unsafe",
            "System.Text.Encoding.CodePages",
            "System.Threading.Tasks.Extensions",
        };
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<IReadOnlyList<MetadataReference>>> References =
            new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<IReadOnlyList<MetadataReference>>>(StringComparer.OrdinalIgnoreCase);

        public static Func<object> Compile(string code, string bridgeAssemblyPath, CancellationToken token)
        {
            return Compile(code, bridgeAssemblyPath, token, null);
        }

        public static Func<object> Compile(string code, string bridgeAssemblyPath, CancellationToken token,
            Action<string, double> recordTiming)
        {
            CSharpCompilation compilation = CreateCompilation(code, bridgeAssemblyPath, token, recordTiming);
            using (MemoryStream image = Emit(compilation, token, recordTiming))
            {
                token.ThrowIfCancellationRequested();
                Assembly assembly = Measure("assemblyLoadMs", recordTiming, () => Assembly.Load(image.ToArray()));
                MethodInfo method = assembly.GetType("__ThunderRoadMcpEvaluation", true).GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                return (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), method);
            }
        }

        /// <summary>Compile the evaluator wrapper and emit it without loading or executing its assembly.</summary>
        public static void Validate(string code, string bridgeAssemblyPath, CancellationToken token)
        {
            Validate(code, bridgeAssemblyPath, token, null);
        }

        public static void Validate(string code, string bridgeAssemblyPath, CancellationToken token,
            Action<string, double> recordTiming)
        {
            CSharpCompilation compilation = CreateCompilation(code, bridgeAssemblyPath, token, recordTiming);
            using (Emit(compilation, token, recordTiming)) { }
        }

        private static CSharpCompilation CreateCompilation(string code, string bridgeAssemblyPath, CancellationToken token,
            Action<string, double> recordTiming)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(bridgeAssemblyPath) || !File.Exists(bridgeAssemblyPath))
                throw new FileNotFoundException("Bridge assembly not found.", bridgeAssemblyPath);
            string fullBridgeAssemblyPath = Path.GetFullPath(bridgeAssemblyPath);
            const string prefix = "using System; using System.Linq; using System.Collections.Generic; using UnityEngine; using ThunderRoad; using ThunderRoadMcp.Bridge; " +
                "public static class __ThunderRoadMcpEvaluation { public static object Run() {\n#line 1 \"evaluation.cs\"\n";
            const string suffix = "\n#line default\n} }";
            string source = prefix + code + suffix;
            SyntaxTree syntax = Measure("parseMs", recordTiming,
                () => CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), cancellationToken: token));
            IReadOnlyList<MetadataReference> references = Measure("referenceInitializationMs", recordTiming,
                () => References.GetOrAdd(fullBridgeAssemblyPath,
                    path => new Lazy<IReadOnlyList<MetadataReference>>(() => CreateReferences(path))).Value);
            return CSharpCompilation.Create(
                "ThunderRoadMcpEvaluation_" + Guid.NewGuid().ToString("N"),
                new[] { syntax },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        }

        private static MemoryStream Emit(CSharpCompilation compilation, CancellationToken token, Action<string, double> recordTiming)
        {
            var image = new MemoryStream();
            try
            {
                EmitResult emitted = Measure("emitMs", recordTiming, () => compilation.Emit(image, cancellationToken: token));
                if (!emitted.Success)
                {
                    string diagnostics = string.Join(Environment.NewLine,
                        emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
                    throw new InvalidOperationException("C# compilation failed:" + Environment.NewLine + diagnostics);
                }
                token.ThrowIfCancellationRequested();
                return image;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        private static T Measure<T>(string name, Action<string, double> recordTiming, Func<T> operation)
        {
            if (recordTiming == null) return operation();
            Stopwatch elapsed = Stopwatch.StartNew();
            try { return operation(); }
            finally { recordTiming(name, elapsed.Elapsed.TotalMilliseconds); }
        }

        private static IReadOnlyList<MetadataReference> CreateReferences(string bridgeAssemblyPath)
        {
            string[] paths = GetReferencePaths(bridgeAssemblyPath);
            var references = new List<MetadataReference>(paths.Length);
            references.Add(MetadataReference.CreateFromFile(bridgeAssemblyPath));
            foreach (string path in paths)
            {
                if (string.Equals(path, bridgeAssemblyPath, StringComparison.OrdinalIgnoreCase)) continue;
                try { references.Add(MetadataReference.CreateFromFile(path)); }
                catch (BadImageFormatException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
            }
            if (references.Count == 0) throw new InvalidOperationException("No runtime metadata references are available.");
            return references;
        }

        internal static string[] GetReferencePaths(string bridgeAssemblyPath)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            paths.Add(Path.GetFullPath(bridgeAssemblyPath));
            var managedAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string managed = Path.GetDirectoryName(typeof(ThunderScript).Assembly.Location);
            if (!string.IsNullOrEmpty(managed) && Directory.Exists(managed))
            {
                foreach (string path in Directory.EnumerateFiles(managed, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    paths.Add(Path.GetFullPath(path));
                    managedAssemblyNames.Add(Path.GetFileNameWithoutExtension(path));
                }
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                try
                {
                    string name = assembly.GetName().Name;
                    if (CompilerDependencyNames.Contains(name) || managedAssemblyNames.Contains(name) || IsUnityEngineAssemblyName(name)) continue;
                    string location = assembly.ManifestModule.FullyQualifiedName;
                    if (!string.IsNullOrEmpty(location) && !string.Equals(location, "<Unknown>", StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(location)) paths.Add(Path.GetFullPath(location));
                }
                catch (NotSupportedException) { }
                catch (FileNotFoundException) { }
            }

            return paths.ToArray();
        }
        internal static bool IsUnityEngineAssemblyName(string name)
        {
            return string.Equals(name, "UnityEngine", StringComparison.OrdinalIgnoreCase) ||
                (name != null && name.StartsWith("UnityEngine.", StringComparison.OrdinalIgnoreCase));
        }
    }
}
