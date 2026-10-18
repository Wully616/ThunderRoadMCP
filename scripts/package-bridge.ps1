param(
    [Parameter(Mandatory = $true)]
    [string]$GameManagedDir,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
$GameManagedDir = [IO.Path]::GetFullPath($GameManagedDir)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $dotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $dotnetCommand) {
        throw '.NET 10 SDK is required to package the bridge: dotnet was not found on PATH. Pass -DotnetPath or install the stable .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0.'
    }
    $DotnetPath = [IO.Path]::GetFullPath($dotnetCommand.Source)
} else {
    if (Test-Path -LiteralPath $DotnetPath -PathType Container) {
        $DotnetPath = Join-Path $DotnetPath 'dotnet.exe'
    }
    if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
        throw "DotnetPath does not point to an existing dotnet executable: $DotnetPath"
    }
    $DotnetPath = [IO.Path]::GetFullPath($DotnetPath)
}

if (-not (Test-Path (Join-Path $GameManagedDir 'ThunderRoad.dll'))) {
    throw "ThunderRoad.dll not found under GameManagedDir: $GameManagedDir"
}
if (Test-Path $OutputDirectory) {
    throw "OutputDirectory already exists; choose a new isolated directory: $OutputDirectory"
}

$bridgeProject = Join-Path $PSScriptRoot '..\src\ThunderRoadMcp.Bridge\ThunderRoadMcp.Bridge.csproj'
$compilerProject = Join-Path $PSScriptRoot '..\src\ThunderRoadMcp.Compiler\ThunderRoadMcp.Compiler.csproj'
$package = Join-Path $OutputDirectory 'ThunderRoadMcp'
$bridgePublish = Join-Path $OutputDirectory 'bridge-publish'
$compilerPublish = Join-Path $OutputDirectory 'compiler-publish'
$runtime = Join-Path $package 'runtime'
$archive = Join-Path $OutputDirectory 'ThunderRoadMcp.zip'

New-Item $OutputDirectory -ItemType Directory | Out-Null
& $DotnetPath publish $bridgeProject -c Release -p:GameManagedDir="$GameManagedDir" -o $bridgePublish
if ($LASTEXITCODE -ne 0) { throw 'Bridge publish failed.' }
& $DotnetPath publish $compilerProject -c Release -p:GameManagedDir="$GameManagedDir" -o $compilerPublish
if ($LASTEXITCODE -ne 0) { throw 'Compiler publish failed.' }

New-Item $package -ItemType Directory -Force | Out-Null
New-Item $runtime -ItemType Directory -Force | Out-Null
$bridgeDlls = @(Get-ChildItem $bridgePublish -File -Filter '*.dll')
if ($bridgeDlls.Count -ne 1 -or $bridgeDlls[0].Name -ne 'ThunderRoadMcp.Bridge.dll') {
    throw 'Bridge publish must contain only ThunderRoadMcp.Bridge.dll; game and compiler dependencies are not mod-loader assemblies.'
}
Copy-Item $bridgeDlls[0].FullName $package
Copy-Item (Join-Path $bridgePublish 'manifest.json') $package

foreach ($file in Get-ChildItem $compilerPublish -File -Filter '*.dll') {
    if ($file.Name -match '^(Unity|ThunderRoad(?!Mcp)|Assembly-CSharp|Newtonsoft\.Json).*\.dll$') {
        throw "Refusing to package a game dependency: $($file.Name)"
    }
    Copy-Item $file.FullName (Join-Path $runtime ($file.Name + '.payload'))
}
if (@(Get-ChildItem $package -Recurse -File -Filter '*.dll').Count -ne 1) {
    throw 'The mod-loader recursive DLL scan must see only ThunderRoadMcp.Bridge.dll.'
}

Compress-Archive -Path (Join-Path $package '*') -DestinationPath $archive
Write-Output "Created $archive"
