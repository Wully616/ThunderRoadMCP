[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GameManagedDir
)

$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$installer = Join-Path $sourceRoot 'scripts\install.ps1'
$GameManagedDir = [IO.Path]::GetFullPath($GameManagedDir)
if (-not (Test-Path -LiteralPath (Join-Path $GameManagedDir 'ThunderRoad.dll') -PathType Leaf)) {
    throw "Pass the installed game's Managed directory containing ThunderRoad.dll: $GameManagedDir"
}

function Get-TestDotnetPath {
    $candidates = New-Object 'System.Collections.Generic.List[string]'
    foreach ($command in @(Get-Command dotnet -CommandType Application -All -ErrorAction SilentlyContinue)) {
        if ($null -ne $command -and -not [string]::IsNullOrWhiteSpace($command.Source)) {
            $candidates.Add([string]$command.Source)
        }
    }
    $userDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $userDotnet -PathType Leaf) { $candidates.Add($userDotnet) }
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in $candidates) {
        $full = [IO.Path]::GetFullPath($candidate)
        if (-not $seen.Add($full)) { continue }
        $sdks = @(& $full --list-sdks 2>$null)
        if ($LASTEXITCODE -ne 0 -or @($sdks | Where-Object { [string]$_ -match '^10\.\d+\.\d+\s' }).Count -eq 0) { continue }
        $runtimes = @(& $full --list-runtimes 2>$null)
        if ($LASTEXITCODE -eq 0 -and @($runtimes | Where-Object { [string]$_ -match '^Microsoft\.NETCore\.App 10\.\d+\.\d+\s' }).Count -gt 0) {
            return $full
        }
    }
    return $null
}

$testDotnet = Get-TestDotnetPath
if (-not $testDotnet) {
    throw 'No .NET 10 SDK with a matching Microsoft.NETCore.App 10.x runtime was found for the installer check. Install the stable .NET 10 SDK or run this check with a 10.x dotnet on PATH.'
}

function Quote-Argument([string]$Value) {
    '"' + $Value.Replace('"', '\"') + '"'
}

function Invoke-Process([string]$FileName, [string[]]$Arguments, [hashtable]$Environment = @{}) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $FileName
    $start.Arguments = (($Arguments | ForEach-Object { Quote-Argument $_ }) -join ' ')
    $start.WorkingDirectory = $sourceRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($key in $Environment.Keys) { $start.EnvironmentVariables[$key] = [string]$Environment[$key] }
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $stdout.Result; Error = $stderr.Result }
}

function Test-GeneratedStdioServer($ServerConfiguration) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = [string]$ServerConfiguration.command
    $start.Arguments = (($ServerConfiguration.args | ForEach-Object { Quote-Argument ([string]$_) }) -join ' ')
    $start.WorkingDirectory = $sourceRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    foreach ($property in $ServerConfiguration.env.PSObject.Properties) {
        $start.EnvironmentVariables[$property.Name] = [string]$property.Value
    }
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    [void]$process.Start()
    try {
        $writer = $process.StandardInput
        $writer.AutoFlush = $true
        $init = [ordered]@{
            jsonrpc = '2.0'; id = 1; method = 'initialize'
            params = @{
                protocolVersion = '2025-03-26'; capabilities = @{}
                clientInfo = @{ name = 'InstallerBehaviorCheck'; version = '1' }
            }
        } | ConvertTo-Json -Compress -Depth 8
        $writer.WriteLine($init)
        $initTask = $process.StandardOutput.ReadLineAsync()
        if (-not $initTask.Wait(30000)) { throw 'Generated stdio command did not answer initialize within 30 seconds.' }
        $initResponse = $initTask.Result | ConvertFrom-Json
        if ($initResponse.id -ne 1 -or $initResponse.result.serverInfo.name -ne 'ThunderRoadMcp') {
            throw 'Generated stdio command returned an invalid initialize response.'
        }

        $writer.WriteLine((@{ jsonrpc = '2.0'; method = 'notifications/initialized'; params = @{} } | ConvertTo-Json -Compress))
        $writer.WriteLine((@{ jsonrpc = '2.0'; id = 2; method = 'tools/list'; params = @{} } | ConvertTo-Json -Compress))
        $toolsTask = $process.StandardOutput.ReadLineAsync()
        if (-not $toolsTask.Wait(30000)) { throw 'Generated stdio command did not answer tools/list within 30 seconds.' }
        $toolsResponse = $toolsTask.Result | ConvertFrom-Json
        if ($toolsResponse.id -ne 2 -or
            -not ($toolsResponse.result.tools.name -contains 'game_status') -or
            -not ($toolsResponse.result.tools.name -contains 'api_assemblies') -or
            -not ($toolsResponse.result.tools.name -contains 'catalog_search')) {
            throw 'Generated stdio command did not expose the expected MCP tools.'
        }
        $writer.Close()
        if (-not $process.WaitForExit(10000)) { throw 'Generated stdio command did not exit after stdin closed.' }
    } finally {
        if (-not $process.HasExited) { $process.Kill() }
        $process.Dispose()
    }
}

function New-Game([string]$Root, [switch]$WithoutMods) {
    $gameRoot = Join-Path $Root 'game'
    $data = Join-Path $gameRoot 'BladeAndSorcery_Data'
    $managed = Join-Path $data 'Managed'
    $mods = Join-Path $data 'StreamingAssets\Mods'
    New-Item -ItemType Directory -Path $managed -Force | Out-Null
    if (-not $WithoutMods) { New-Item -ItemType Directory -Path $mods -Force | Out-Null }
    foreach ($name in @('ThunderRoad.dll', 'UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'Newtonsoft.Json.dll')) {
        $source = Join-Path $GameManagedDir $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Required game reference is missing: $source" }
        Copy-Item -LiteralPath $source -Destination $managed
    }
    $executable = Join-Path $gameRoot 'BladeAndSorcery.exe'
    Set-Content -LiteralPath $executable -Value 'isolated installer fixture' -Encoding ASCII
    [pscustomobject]@{ Executable = $executable; Managed = $managed; Mods = $mods }
}


function Invoke-Installer($Game, [string]$HostPath, [string]$ConfigRoot, [switch]$SkipBridge, [switch]$NoBridgeConfig, [string]$PathOverride, [hashtable]$EnvironmentOverride = @{}, [string]$DotnetPath, [string]$InstallerPath = $installer) {
    New-Item -ItemType Directory -Path $ConfigRoot -Force | Out-Null
    $environment = @{
        THUNDERROAD_MCP_CONFIG = Join-Path $ConfigRoot 'bridge.json'
        THUNDERROAD_MCP_LAUNCH_CONFIG = Join-Path $ConfigRoot 'launch.json'
    }
    if ($PathOverride) { $environment.PATH = $PathOverride }
    foreach ($key in $EnvironmentOverride.Keys) { $environment[$key] = [string]$EnvironmentOverride[$key] }
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $InstallerPath,
        '-InstallDirectory', $HostPath)
    if ($Game) { $arguments += @('-GameExecutablePath', $Game.Executable) }
    if ($DotnetPath) { $arguments += @('-DotnetPath', $DotnetPath) }
    if ($NoBridgeConfig) { $arguments += '-NoBridgeConfig' }
    if ($SkipBridge) { $arguments += '-SkipBridge' }
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    Invoke-Process $powershell $arguments $environment
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('ThunderRoadMcp-installer-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $gameA = New-Game (Join-Path $root 'enabled') -WithoutMods
    if (Test-Path -LiteralPath $gameA.Mods) { throw 'Fresh synthetic game unexpectedly had a Mods directory.' }
    $hostA = Join-Path $root 'enabled-host'
    $configA = Join-Path $root 'enabled-config'
    $success = Invoke-Installer $gameA $hostA $configA
    if ($success.ExitCode -ne 0) { throw "Real install failed: $($success.Output)`n$($success.Error)" }
    $hostExe = Join-Path $hostA 'ThunderRoadMcp.Host.exe'
    $hostDll = Join-Path $hostA 'ThunderRoadMcp.Host.dll'
    $installedMod = Join-Path $gameA.Mods 'ThunderRoadMcp'
    if (-not (Test-Path -LiteralPath $hostExe -PathType Leaf) -or
        -not (Test-Path -LiteralPath $hostDll -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $installedMod 'ThunderRoadMcp.Bridge.dll') -PathType Leaf)) {
        throw 'Real install did not publish the host and deploy the bridge into the isolated game tree.'
    }
    $bridgeConfigPath = Join-Path $configA 'bridge.json'
    $bridgeConfig = Get-Content -LiteralPath $bridgeConfigPath -Raw | ConvertFrom-Json
    if ($bridgeConfig.enabled -ne $true -or $bridgeConfig.host -ne '127.0.0.1' -or [string]$bridgeConfig.token -notmatch '^[0-9a-f]{64}$') {
        throw 'Default install did not create an enabled loopback config with a 32-byte hex token.'
    }
    if ($success.Output.Contains([string]$bridgeConfig.token)) { throw 'Installer printed the bridge token.' }
    if (-not $success.Output.Contains('Bridge enabled') -or
        -not $success.Output.Contains([IO.Path]::GetFullPath($bridgeConfigPath))) {
        throw 'Default install did not report the enabled bridge and its config path.'
    }
    $launchConfig = Get-Content -LiteralPath (Join-Path $configA 'launch.json') -Raw | ConvertFrom-Json
    if ([IO.Path]::GetFullPath($launchConfig.executablePath) -ne [IO.Path]::GetFullPath($gameA.Executable)) {
        throw 'Explicit game path was not saved in the isolated launch config.'
    }
    $agentConfigPath = Join-Path $hostA 'mcp-config.json'
    if (-not (Test-Path -LiteralPath $agentConfigPath -PathType Leaf)) { throw 'Installer did not save its copyable MCP config.' }
    $agentConfig = Get-Content -LiteralPath $agentConfigPath -Raw | ConvertFrom-Json
    $agentServer = $agentConfig.mcpServers.ThunderRoadMcp
    if ([IO.Path]::GetFullPath([string]$agentServer.command) -ne [IO.Path]::GetFullPath($hostExe) -or
        @($agentServer.args).Count -ne 0 -or
        -not $success.Output.Contains('"mcpServers"') -or
        -not $success.Output.Contains([IO.Path]::GetFullPath($hostExe)) -or
        [IO.Path]::GetFullPath($agentServer.env.THUNDERROAD_MCP_CONFIG) -ne [IO.Path]::GetFullPath((Join-Path $configA 'bridge.json')) -or
        [IO.Path]::GetFullPath($agentServer.env.THUNDERROAD_MCP_LAUNCH_CONFIG) -ne [IO.Path]::GetFullPath((Join-Path $configA 'launch.json'))) {
        throw 'Saved and printed MCP configs do not use the absolute installed host EXE, empty args, and required environment overrides.'
    }
    Test-GeneratedStdioServer $agentServer

    $gameNoConfig = New-Game (Join-Path $root 'no-bridge-config') -WithoutMods
    $hostNoConfig = Join-Path $root 'no-bridge-config-host'
    $configNoConfig = Join-Path $root 'no-bridge-config-files'
    $noConfig = Invoke-Installer $gameNoConfig $hostNoConfig $configNoConfig -NoBridgeConfig
    if ($noConfig.ExitCode -ne 0) { throw "Install with -NoBridgeConfig failed: $($noConfig.Output)`n$($noConfig.Error)" }
    if (-not (Test-Path -LiteralPath (Join-Path $gameNoConfig.Mods 'ThunderRoadMcp\ThunderRoadMcp.Bridge.dll') -PathType Leaf)) {
        throw '-NoBridgeConfig install did not install the bridge mod.'
    }
    if (Test-Path -LiteralPath (Join-Path $configNoConfig 'bridge.json')) {
        throw '-NoBridgeConfig install created a bridge config.'
    }
    if (-not $noConfig.Output.Contains('Bridge installed but disabled')) {
        throw '-NoBridgeConfig install did not report the bridge as installed but disabled.'
    }

    $gameSkip = New-Game (Join-Path $root 'skip-bridge') -WithoutMods
    $hostSkip = Join-Path $root 'skip-bridge-host'
    $configSkip = Join-Path $root 'skip-bridge-config'
    $skip = Invoke-Installer $gameSkip $hostSkip $configSkip -SkipBridge
    if ($skip.ExitCode -ne 0) { throw "Host-only install failed: $($skip.Output)`n$($skip.Error)" }
    $skipExe = Join-Path $hostSkip 'ThunderRoadMcp.Host.exe'
    $skipDll = Join-Path $hostSkip 'ThunderRoadMcp.Host.dll'
    $skipAgentPath = Join-Path $hostSkip 'mcp-config.json'
    if (-not (Test-Path -LiteralPath $skipExe -PathType Leaf) -or
        -not (Test-Path -LiteralPath $skipDll -PathType Leaf) -or
        -not (Test-Path -LiteralPath $skipAgentPath -PathType Leaf)) {
        throw 'Host-only install did not install the host EXE, DLL, and MCP config.'
    }
    $skipAgent = Get-Content -LiteralPath $skipAgentPath -Raw | ConvertFrom-Json
    $skipServer = $skipAgent.mcpServers.ThunderRoadMcp
    if ([IO.Path]::GetFullPath([string]$skipServer.command) -ne [IO.Path]::GetFullPath($skipExe) -or
        @($skipServer.args).Count -ne 0) {
        throw 'Host-only MCP config does not use the absolute installed EXE with empty args.'
    }
    if ((Test-Path -LiteralPath (Join-Path $gameSkip.Mods 'ThunderRoadMcp')) -or
        (Test-Path -LiteralPath (Join-Path $configSkip 'bridge.json'))) {
        throw 'Host-only install created a bridge mod or bridge config despite -SkipBridge.'
    }
    if (-not $skip.Output.Contains('Skipping the bridge') -or
        -not $skip.Output.Contains('MCP stdio configuration') -or
        $skip.Output.Contains('VS Code')) {
        throw 'Host-only install did not report the generic host-only MCP stdio configuration message.'
    }

    $machineDotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    $userDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    $userDotnetHas10 = $false
    if (Test-Path -LiteralPath $userDotnet -PathType Leaf) {
        $userDotnetSdks = @(& $userDotnet --list-sdks 2>$null)
        $userDotnetHas10 = $LASTEXITCODE -eq 0 -and @($userDotnetSdks | Where-Object { [string]$_ -match '^10\.\d+\.\d+\s' }).Count -gt 0
    }
    if ((Test-Path -LiteralPath $machineDotnet -PathType Leaf) -and $userDotnetHas10) {
        $hostDiscover = Join-Path $root 'discover-host'
        $configDiscover = Join-Path $root 'discover-config'
        $discover = Invoke-Installer $null $hostDiscover $configDiscover -SkipBridge -PathOverride (Split-Path -Parent $machineDotnet)
        if ($discover.ExitCode -ne 0) {
            throw "User-local SDK discovery from a machine-wide-only PATH failed: $($discover.Output)`n$($discover.Error)"
        }
        $discoverExe = Join-Path $hostDiscover 'ThunderRoadMcp.Host.exe'
        $discoverDll = Join-Path $hostDiscover 'ThunderRoadMcp.Host.dll'
        $discoverAgent = Join-Path $hostDiscover 'mcp-config.json'
        if (-not (Test-Path -LiteralPath $discoverExe -PathType Leaf) -or
            -not (Test-Path -LiteralPath $discoverDll -PathType Leaf) -or
            -not (Test-Path -LiteralPath $discoverAgent -PathType Leaf)) {
            throw 'User-local SDK discovery install did not produce the host EXE, DLL, and MCP config.'
        }
    } else {
        Write-Warning 'Skipping user-local SDK discovery check: the machine-wide dotnet or %USERPROFILE%\.dotnet 10.x SDK is absent.'
    }

    $prebuiltBridgeOutput = Join-Path $root 'prebuilt-bridge'
    & (Join-Path $sourceRoot 'scripts\package-bridge.ps1') -GameManagedDir $GameManagedDir -OutputDirectory $prebuiltBridgeOutput -DotnetPath $testDotnet
    $prebuiltBridgeZip = Join-Path $prebuiltBridgeOutput 'ThunderRoadMcp.zip'
    if (-not (Test-Path -LiteralPath $prebuiltBridgeZip -PathType Leaf)) { throw 'Prebuilt bridge package was not created.' }
    $releaseRoot = Join-Path $root 'release-layout'
    $releaseScripts = Join-Path $releaseRoot 'scripts'
    $releaseHost = Join-Path $releaseRoot 'host'
    $releaseBridge = Join-Path $releaseRoot 'bridge'
    New-Item -ItemType Directory -Path $releaseScripts, $releaseHost, $releaseBridge -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'scripts\install.ps1') -Destination $releaseScripts
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'scripts\package-bridge.ps1') -Destination $releaseScripts
    Copy-Item -Path (Join-Path $hostA '*') -Destination $releaseHost -Recurse
    Copy-Item -LiteralPath $prebuiltBridgeZip -Destination (Join-Path $releaseBridge 'ThunderRoadMcp.zip')
    $releaseMetadata = [ordered]@{
        latest = '1.3.2'
        versions = @([ordered]@{ gameVersion = '1.3.2'; package = 'ThunderRoadMcp-1.3.2.zip' })
    }
    $releaseMetadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $releaseBridge 'versions.json') -Encoding UTF8
    $releaseInstaller = Join-Path $releaseScripts 'install.ps1'
    $gameRelease = New-Game (Join-Path $root 'release-game')
    $hostRelease = Join-Path $root 'release-host'
    $configRelease = Join-Path $root 'release-config'
    $release = Invoke-Installer $gameRelease $hostRelease $configRelease -InstallerPath $releaseInstaller
    if ($release.ExitCode -ne 0) { throw "Release-layout install failed: $($release.Output)`n$($release.Error)" }
    $releaseExe = Join-Path $hostRelease 'ThunderRoadMcp.Host.exe'
    $releaseDll = Join-Path $hostRelease 'ThunderRoadMcp.Host.dll'
    $releaseMod = Join-Path $gameRelease.Mods 'ThunderRoadMcp'
    if (-not (Test-Path -LiteralPath $releaseExe -PathType Leaf) -or
        -not (Test-Path -LiteralPath $releaseDll -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $releaseMod 'ThunderRoadMcp.Bridge.dll') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $releaseMod 'manifest.json') -PathType Leaf)) {
        throw 'Release-layout install did not use the prebuilt host and bridge.'
    }
    $releaseConfig = Get-Content -LiteralPath (Join-Path $configRelease 'bridge.json') -Raw | ConvertFrom-Json
    if ($releaseConfig.enabled -ne $true -or [string]$releaseConfig.token -notmatch '^[0-9a-f]{64}$') {
        throw 'Release-layout install did not create an enabled bridge config by default.'
    }
    if ($release.Output.Contains([string]$releaseConfig.token)) { throw 'Release-layout installer printed the bridge token.' }
    $releaseAgent = Get-Content -LiteralPath (Join-Path $hostRelease 'mcp-config.json') -Raw | ConvertFrom-Json
    if ([IO.Path]::GetFullPath([string]$releaseAgent.mcpServers.ThunderRoadMcp.command) -ne [IO.Path]::GetFullPath($releaseExe) -or
        @($releaseAgent.mcpServers.ThunderRoadMcp.args).Count -ne 0) {
        throw 'Release-layout MCP config does not use the absolute installed EXE with empty args.'
    }
    if (-not $release.Output.Contains('Using the prebuilt host') -or
        -not $release.Output.Contains('Using the prebuilt bridge package') -or
        -not $release.Output.Contains('Using prebuilt bridge for game 1.3.2') -or
        -not $release.Output.Contains('MCP stdio configuration')) {
        throw 'Release-layout install did not report its prebuilt host and bridge sources, game version provenance, and the generic MCP stdio label.'
    }

    $gameB = New-Game (Join-Path $root 'preserve-config')
    $hostB = Join-Path $root 'preserve-host'
    $configB = Join-Path $root 'preserve-config-files'
    New-Item -ItemType Directory -Path $configB | Out-Null
    $launchBPath = Join-Path $configB 'launch.json'
    $bridgeBPath = Join-Path $configB 'bridge.json'
    $launchBText = ConvertTo-Json -InputObject @{ executablePath = $gameB.Executable; arguments = @() }
    $bridgeBText = '{"enabled":false,"host":"127.0.0.1","port":47831,"token":"' + ('b' * 64) + '"}'
    Set-Content -LiteralPath $launchBPath -Value $launchBText -Encoding UTF8
    Set-Content -LiteralPath $bridgeBPath -Value $bridgeBText -Encoding UTF8
    $savedLaunch = Get-Content -LiteralPath $launchBPath -Raw
    $savedBridge = Get-Content -LiteralPath $bridgeBPath -Raw
    $preserved = Invoke-Installer $gameB $hostB $configB
    if ($preserved.ExitCode -ne 0) { throw "Install with existing configs failed: $($preserved.Output)`n$($preserved.Error)" }
    if ((Get-Content -LiteralPath $launchBPath -Raw) -ne $savedLaunch -or
        (Get-Content -LiteralPath $bridgeBPath -Raw) -ne $savedBridge -or
        (Get-Content -LiteralPath $bridgeBPath -Raw | ConvertFrom-Json).enabled -ne $false) {
        throw 'Installer changed an existing launch config or enabled an existing disabled bridge config.'
    }
    if (-not ($preserved.Output + $preserved.Error).Contains('disabled and was preserved')) {
        throw 'Installer did not warn that the existing disabled bridge config remains disabled.'
    }

    $gameC = New-Game (Join-Path $root 'occupied-host')
    $hostC = Join-Path $root 'occupied-host-directory'
    New-Item -ItemType Directory -Path $hostC | Out-Null
    $hostSentinel = Join-Path $hostC 'user-data.txt'
    Set-Content -LiteralPath $hostSentinel -Value 'preserve this' -Encoding ASCII
    $configC = Join-Path $root 'occupied-host-config'
    $occupiedHost = Invoke-Installer $gameC $hostC $configC
    if ($occupiedHost.ExitCode -eq 0 -or (Get-Content -LiteralPath $hostSentinel -Raw).Trim() -ne 'preserve this' -or
        (Test-Path -LiteralPath (Join-Path $gameC.Mods 'ThunderRoadMcp')) -or
        (Test-Path -LiteralPath (Join-Path $configC 'bridge.json'))) {
        throw 'Occupied host path was not refused before changing host, mod, or config data.'
    }

    $gameD = New-Game (Join-Path $root 'occupied-mod')
    $hostD = Join-Path $root 'occupied-mod-host'
    $configD = Join-Path $root 'occupied-mod-config'
    $occupiedMod = Join-Path $gameD.Mods 'ThunderRoadMcp'
    New-Item -ItemType Directory -Path $occupiedMod | Out-Null
    $modSentinel = Join-Path $occupiedMod 'user-data.txt'
    Set-Content -LiteralPath $modSentinel -Value 'preserve this mod' -Encoding ASCII
    $occupied = Invoke-Installer $gameD $hostD $configD
    if ($occupied.ExitCode -eq 0 -or (Get-Content -LiteralPath $modSentinel -Raw).Trim() -ne 'preserve this mod' -or
        (Test-Path -LiteralPath $hostD) -or (Test-Path -LiteralPath (Join-Path $configD 'bridge.json'))) {
        throw 'Occupied mod destination was not refused without changing existing data.'
    }

    $gameE = New-Game (Join-Path $root 'missing-sdk')
    $hostE = Join-Path $root 'missing-sdk-host'
    $configE = Join-Path $root 'missing-sdk-config'
    $emptyRoot = Join-Path $root 'empty-dotnet'
    New-Item -ItemType Directory -Path $emptyRoot | Out-Null
    $sdkSentinel = Join-Path $gameE.Mods 'keep.txt'
    Set-Content -LiteralPath $sdkSentinel -Value 'preserve game files' -Encoding ASCII
    $missingSdk = Invoke-Installer $gameE $hostE $configE -PathOverride $emptyRoot -EnvironmentOverride @{
        USERPROFILE = $emptyRoot
        ProgramFiles = $emptyRoot
        'ProgramFiles(x86)' = $emptyRoot
        DOTNET_ROOT = $emptyRoot
        'DOTNET_ROOT(x86)' = $emptyRoot
    }
    $combinedError = $missingSdk.Output + $missingSdk.Error
    if ($missingSdk.ExitCode -eq 0 -or
        $combinedError -notmatch 'dotnet\.microsoft\.com/download/dotnet/10\.0' -or
        $combinedError -notmatch 'SDK|runtime' -or
        (Test-Path -LiteralPath $hostE) -or
        (Test-Path -LiteralPath (Join-Path $configE 'bridge.json')) -or
        (Test-Path -LiteralPath (Join-Path $gameE.Mods 'ThunderRoadMcp')) -or
        (Get-Content -LiteralPath $sdkSentinel -Raw).Trim() -ne 'preserve game files') {
        throw "Missing-dependency path lacked a stable-download reference or changed data: $combinedError"
    }

    $invalidHost = Join-Path $root 'invalid-dotnet-host'
    $invalidConfig = Join-Path $root 'invalid-dotnet-config'
    $invalid = Invoke-Installer $null $invalidHost $invalidConfig -SkipBridge -DotnetPath (Join-Path $root 'missing-dotnet.exe')
    $invalidText = $invalid.Output + $invalid.Error
    if ($invalid.ExitCode -eq 0 -or $invalidText -notmatch 'DotnetPath|dotnet executable' -or
        (Test-Path -LiteralPath $invalidHost) -or
        (Test-Path -LiteralPath (Join-Path $invalidConfig 'bridge.json'))) {
        throw "Invalid -DotnetPath did not fail cleanly before side effects: $invalidText"
    }

    Write-Output 'PASS: source and release-layout isolated installs with actual game Managed references; bridge enabled by default and disabled with -NoBridgeConfig; host-only -SkipBridge; user-local SDK discovery and invalid -DotnetPath handling; generated stdio initialize/tools/list; existing configs, occupied destinations, and missing dependencies preserve user data.'
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
