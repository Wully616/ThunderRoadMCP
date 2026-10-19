[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BridgeDirectory,
    [Parameter(Mandatory = $true)]
    [string]$GameManagedDirectory
)

$ErrorActionPreference = 'Stop'
$bridgeRoot = [IO.Path]::GetFullPath($BridgeDirectory)
$managed = [IO.Path]::GetFullPath($GameManagedDirectory)
$bridgePath = Join-Path $bridgeRoot 'ThunderRoadMcp.Bridge.dll'
$runtimePath = Join-Path $bridgeRoot 'runtime'

foreach ($path in @($bridgePath, (Join-Path $managed 'ThunderRoad.dll'))) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required evaluator test file not found: $path"
    }
}
if (-not (Test-Path -LiteralPath $runtimePath -PathType Container)) {
    throw "Roslyn payload directory not found: $runtimePath"
}

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
public static class GameAssemblyResolver
{
    public static string ManagedDirectory;
    public static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string path = Path.Combine(ManagedDirectory, new AssemblyName(args.Name).Name + ".dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }
}
'@
$resolveMethod = [GameAssemblyResolver].GetMethod('Resolve')
$resolveHandler = [Delegate]::CreateDelegate([System.ResolveEventHandler], $resolveMethod)
[GameAssemblyResolver]::ManagedDirectory = $managed
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolveHandler)

[Reflection.Assembly]::LoadFrom((Join-Path $managed 'ThunderRoad.dll')) | Out-Null
$bridge = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($bridgePath))
$bridgeLocations = @($bridge.Location, $bridge.ManifestModule.FullyQualifiedName)
$hasFileLocation = $false
foreach ($location in $bridgeLocations) {
    if (-not [string]::IsNullOrWhiteSpace($location) -and $location -ne '<Unknown>' -and [IO.File]::Exists($location)) {
        $hasFileLocation = $true
        break
    }
}
if ($hasFileLocation) {
    throw 'The test bridge assembly unexpectedly has a file-backed load location.'
}

$flags = [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Static
$evaluator = $bridge.GetType('ThunderRoadMcp.Bridge.CSharpEvaluator', $true)
$configureArgs = [Array]::CreateInstance([object], 1)
$configureArgs.SetValue([string]$runtimePath, 0)
$evaluator.GetMethod('Configure', $flags).Invoke($null, $configureArgs) | Out-Null
$compile = $evaluator.GetMethod('Compile', $flags, $null, [Type[]]@([string], [Threading.CancellationToken]), $null)

$basicArgs = [Array]::CreateInstance([object], 2)
$basicArgs.SetValue('return 1;', 0)
$basicArgs.SetValue([Threading.CancellationToken]::None, 1)
$basic = $compile.Invoke($null, $basicArgs)
if ($basic.Invoke() -ne 1) {
    throw 'Basic evaluation returned an unexpected result.'
}
$runtimeArgs = [Array]::CreateInstance([object], 2)
$runtimeArgs.SetValue('return typeof(McpRuntime).FullName;', 0)
$runtimeArgs.SetValue([Threading.CancellationToken]::None, 1)
$runtimeType = $compile.Invoke($null, $runtimeArgs)
if ($runtimeType.Invoke() -ne 'ThunderRoadMcp.Bridge.McpRuntime') {
    throw 'The evaluator could not resolve McpRuntime from its own bridge assembly.'
}

Write-Output 'Pathless bridge evaluator check passed.'
