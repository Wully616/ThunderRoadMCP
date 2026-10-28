using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace ThunderRoadMcp.Bridge
{
    internal static class RoslynDependencyResolver
    {
        private const string CompilerAssemblyName = "ThunderRoadMcp.Compiler";
        private const string UnsafeAssemblyName = "System.Runtime.CompilerServices.Unsafe";
        private const string BuffersAssemblyName = "System.Buffers";
        private const string VectorsAssemblyName = "System.Numerics.Vectors";
        private static readonly HashSet<string> PayloadAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            CompilerAssemblyName,
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
        private static readonly object Gate = new object();
        private static string bridgeAssemblyPath;
        private static readonly HashSet<Assembly> OwnedAssemblies = new HashSet<Assembly>();
        private static string payloadDirectory;
        private static Assembly compilerAssembly;
        private static readonly Dictionary<string, Assembly> PayloadAssemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        private static Func<string, string, CancellationToken, Action<string, double>, Func<object>> compileEntryPoint;
        private static Func<string, string, CancellationToken, Action<string, double>, object> validateEntryPoint;
        private static bool loadingCompiler;
        private static ResolveEventHandler resolveHandler;

        public static void Configure(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                throw new DirectoryNotFoundException("Roslyn payload directory not found: " + directory);

            lock (Gate)
            {
                string fullPath = Path.GetFullPath(directory);
                if (payloadDirectory != null && !string.Equals(payloadDirectory, fullPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Roslyn payload directory is already configured.");
                string modRoot = Directory.GetParent(fullPath)?.FullName;
                if (modRoot == null)
                    throw new InvalidOperationException("Roslyn payload directory must be inside the mod root.");
                string bridgePath = Path.Combine(modRoot, "ThunderRoadMcp.Bridge.dll");
                if (!File.Exists(bridgePath))
                    throw new FileNotFoundException("Bridge assembly not found beside the Roslyn payload directory.", bridgePath);
                payloadDirectory = fullPath;
                bridgeAssemblyPath = bridgePath;
                if (resolveHandler == null)
                {
                    resolveHandler = ResolveDependency;
                    AppDomain.CurrentDomain.AssemblyResolve += resolveHandler;
                }
            }
        }

        public static Func<object> Compile(string code, CancellationToken token, Action<string, double> recordTiming)
        {
            LoadCompiler();
            return compileEntryPoint(code, bridgeAssemblyPath, token, recordTiming);
        }

        public static object Validate(string code, CancellationToken token, Action<string, double> recordTiming)
        {
            LoadCompiler();
            return validateEntryPoint(code, bridgeAssemblyPath, token, recordTiming);
        }

        private static Assembly LoadCompiler()
        {
            lock (Gate)
            {
                while (compilerAssembly == null && loadingCompiler) Monitor.Wait(Gate);
                if (compilerAssembly != null) return compilerAssembly;
                if (payloadDirectory == null)
                    throw new InvalidOperationException("Roslyn dependency resolver is not configured.");
                loadingCompiler = true;
            }

            try
            {
                string path = Path.Combine(payloadDirectory, CompilerAssemblyName + ".dll.payload");
                if (!File.Exists(path)) throw new FileNotFoundException("Roslyn compiler payload not found.", path);
                Assembly loaded = Assembly.Load(File.ReadAllBytes(path));
                if (!string.Equals(loaded.GetName().Name, CompilerAssemblyName, StringComparison.Ordinal))
                    throw new FileLoadException("Unexpected Roslyn compiler payload identity.", path);
                Type evaluator = loaded.GetType(CompilerAssemblyName + ".CSharpEvaluator", true);
                Type[] timedArguments = { typeof(string), typeof(string), typeof(CancellationToken), typeof(Action<string, double>) };
                MethodInfo compileMethod = evaluator.GetMethod("Compile", BindingFlags.Public | BindingFlags.Static, null, timedArguments, null);
                MethodInfo validateMethod = evaluator.GetMethod("Validate", BindingFlags.Public | BindingFlags.Static, null, timedArguments, null);
                if (compileMethod == null)
                    throw new MissingMethodException(CompilerAssemblyName, "CSharpEvaluator.Compile(code, bridgeAssemblyPath, token, recordTiming)");
                if (validateMethod == null)
                    throw new MissingMethodException(CompilerAssemblyName, "CSharpEvaluator.Validate(code, bridgeAssemblyPath, token, recordTiming)");
                var compile = (Func<string, string, CancellationToken, Action<string, double>, Func<object>>)Delegate.CreateDelegate(
                    typeof(Func<string, string, CancellationToken, Action<string, double>, Func<object>>), compileMethod);
                var validate = (Func<string, string, CancellationToken, Action<string, double>, object>)Delegate.CreateDelegate(
                    typeof(Func<string, string, CancellationToken, Action<string, double>, object>), validateMethod);
                lock (Gate)
                {
                    if (compilerAssembly == null)
                    {
                        compilerAssembly = loaded;
                        compileEntryPoint = compile;
                        validateEntryPoint = validate;
                        PayloadAssemblies[CompilerAssemblyName] = loaded;
                        OwnedAssemblies.Add(loaded);
                    }
                    loadingCompiler = false;
                    Monitor.PulseAll(Gate);
                    return compilerAssembly;
                }
            }
            catch
            {
                lock (Gate)
                {
                    loadingCompiler = false;
                    Monitor.PulseAll(Gate);
                }
                throw;
            }
        }

        private static Assembly ResolveDependency(object sender, ResolveEventArgs args)
        {
            Assembly requester = args.RequestingAssembly;
            if (requester == null) return null;

            string directory;
            lock (Gate)
            {
                string requesterName = requester.GetName().Name;
                bool compilerLoading = loadingCompiler && string.Equals(requesterName, CompilerAssemblyName, StringComparison.Ordinal);
                if (!compilerLoading && !OwnedAssemblies.Contains(requester)) return null;
                directory = payloadDirectory;
            }

            AssemblyName requested = new AssemblyName(args.Name);
            if (!PayloadAssemblyNames.Contains(requested.Name)) return null;

            lock (Gate)
            {
                if (PayloadAssemblies.TryGetValue(requested.Name, out Assembly existing))
                {
                    AssemblyName identity = existing.GetName();
                    return SameToken(identity.GetPublicKeyToken(), requested.GetPublicKeyToken()) &&
                        IsSupportedVersion(requested, identity) ? existing : null;
                }
            }

            string path = Path.Combine(directory, requested.Name + ".dll.payload");
            if (!File.Exists(path)) return null;

            AssemblyName candidate = AssemblyName.GetAssemblyName(path);
            if (!string.Equals(candidate.Name, requested.Name, StringComparison.OrdinalIgnoreCase) ||
                !SameToken(candidate.GetPublicKeyToken(), requested.GetPublicKeyToken()) ||
                !IsSupportedVersion(requested, candidate))
                return null;

            Assembly loaded = Assembly.Load(File.ReadAllBytes(path));
            AssemblyName actual = loaded.GetName();
            if (!string.Equals(actual.Name, candidate.Name, StringComparison.OrdinalIgnoreCase) ||
                !SameToken(actual.GetPublicKeyToken(), candidate.GetPublicKeyToken()) ||
                !Equals(actual.Version, candidate.Version))
                throw new FileLoadException("Loaded Roslyn dependency identity did not match its payload.", path);

            lock (Gate)
            {
                if (PayloadAssemblies.TryGetValue(actual.Name, out Assembly existing))
                    return existing;
                PayloadAssemblies.Add(actual.Name, loaded);
                OwnedAssemblies.Add(loaded);
            }
            return loaded;
        }

        private static bool IsSupportedVersion(AssemblyName requested, AssemblyName candidate)
        {
            if (Equals(requested.Version, candidate.Version)) return true;
            return string.Equals(requested.Name, UnsafeAssemblyName, StringComparison.OrdinalIgnoreCase) &&
                    Equals(requested.Version, new Version(4, 0, 4, 1)) &&
                    Equals(candidate.Version, new Version(6, 0, 0, 0)) ||
                string.Equals(requested.Name, BuffersAssemblyName, StringComparison.OrdinalIgnoreCase) &&
                    requested.Version <= new Version(4, 0, 3, 0) &&
                    Equals(candidate.Version, new Version(4, 0, 3, 0)) ||
                string.Equals(requested.Name, VectorsAssemblyName, StringComparison.OrdinalIgnoreCase) &&
                    Equals(requested.Version, new Version(4, 0, 0, 0)) &&
                    Equals(candidate.Version, new Version(4, 1, 4, 0));
        }

        private static bool SameToken(byte[] left, byte[] right)
        {
            if (left == null || right == null) return left == null && right == null;
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }
    }
}
