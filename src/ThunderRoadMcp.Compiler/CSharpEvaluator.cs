using System;
using System.Collections.Generic;
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
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(bridgeAssemblyPath) || !File.Exists(bridgeAssemblyPath))
                throw new FileNotFoundException("Bridge assembly not found.", bridgeAssemblyPath);
            string fullBridgeAssemblyPath = Path.GetFullPath(bridgeAssemblyPath);
            string source = "using System; using System.Linq; using System.Collections.Generic; using UnityEngine; using ThunderRoad; using ThunderRoadMcp.Bridge; " +
                "public static class __ThunderRoadMcpEvaluation { public static object Run() {\n#line 1 \"evaluation.cs\"\n" + code + "\n#line default\n} }";
            SyntaxTree syntax = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), cancellationToken: token);
            var compilation = CSharpCompilation.Create(
                "ThunderRoadMcpEvaluation_" + Guid.NewGuid().ToString("N"),
                new[] { syntax },
                References.GetOrAdd(fullBridgeAssemblyPath,
                    path => new Lazy<IReadOnlyList<MetadataReference>>(() => CreateReferences(path))).Value,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
            using (var image = new MemoryStream())
            {
                EmitResult emitted = compilation.Emit(image, cancellationToken: token);
                if (!emitted.Success)
                {
                    string diagnostics = string.Join(Environment.NewLine,
                        emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
                    throw new InvalidOperationException("C# compilation failed:" + Environment.NewLine + diagnostics);
                }
                token.ThrowIfCancellationRequested();
                Assembly assembly = Assembly.Load(image.ToArray());
                MethodInfo method = assembly.GetType("__ThunderRoadMcpEvaluation", true).GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                return (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), method);
            }
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
