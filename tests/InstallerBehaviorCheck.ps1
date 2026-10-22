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

function Invoke-Upgrader($Game, [string]$HostPath, [string]$UpgradeScript = (Join-Path $sourceRoot 'scripts\upgrade.ps1'), [switch]$SkipBridge, [switch]$NoBridgeConfig, [string]$DotnetPath, [hashtable]$EnvironmentOverride = @{}, [string]$PowerShellPath) {
    $environment = @{}
    if ($Game) {
        $environment.THUNDERROAD_MCP_CONFIG = Join-Path (Split-Path -Parent $HostPath) 'upgrade-bridge.json'
        $environment.THUNDERROAD_MCP_LAUNCH_CONFIG = Join-Path (Split-Path -Parent $HostPath) 'upgrade-launch.json'
    }
    foreach ($key in $EnvironmentOverride.Keys) { $environment[$key] = [string]$EnvironmentOverride[$key] }
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $UpgradeScript, '-InstallDirectory', $HostPath)
    if ($Game) { $arguments += @('-GameExecutablePath', $Game.Executable) }
    if ($DotnetPath) { $arguments += @('-DotnetPath', $DotnetPath) }
    if ($SkipBridge) { $arguments += '-SkipBridge' }
    if ($NoBridgeConfig) { $arguments += '-NoBridgeConfig' }
    $powershell = if ($PowerShellPath) { $PowerShellPath } else { Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe' }
    Invoke-Process $powershell $arguments $environment
}

function Test-ByteEqual([byte[]]$Left, [byte[]]$Right) {
    return [Convert]::ToBase64String($Left) -ceq [Convert]::ToBase64String($Right)
}

function Assert-InstalledReferencePayload([string]$HostDirectory) {
    foreach ($relativePath in @(
        'docs\game-guide.md',
        'docs\api-discovery.md',
        'skills\blade-and-sorcery\SKILL.md',
        'skills\blade-and-sorcery\references\unity-2021.3.md'
    )) {
        $installedPath = Join-Path $HostDirectory $relativePath
        $sourcePath = Join-Path $sourceRoot $relativePath
        if (-not (Test-Path -LiteralPath $installedPath -PathType Leaf) -or
            -not (Test-ByteEqual ([IO.File]::ReadAllBytes($installedPath)) ([IO.File]::ReadAllBytes($sourcePath)))) {
            throw "Installed host did not preserve '$relativePath'."
        }
    }
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
    Assert-InstalledReferencePayload $hostA

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
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'docs') -Destination $releaseRoot -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'skills') -Destination $releaseRoot -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'scripts\upgrade.ps1') -Destination $releaseScripts
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
    Assert-InstalledReferencePayload $hostRelease

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

    $releaseMcpPath = Join-Path $hostRelease 'mcp-config.json'
    $releaseLaunchPath = Join-Path $configRelease 'launch.json'
    $releaseBridgePath = Join-Path $configRelease 'bridge.json'
    $savedReleaseMcp = [IO.File]::ReadAllBytes($releaseMcpPath)
    $savedReleaseLaunch = [IO.File]::ReadAllBytes($releaseLaunchPath)
    $savedReleaseBridge = [IO.File]::ReadAllBytes($releaseBridgePath)
    Set-Content -LiteralPath (Join-Path $hostRelease 'old-host-only.bin') -Value 'obsolete'
    Set-Content -LiteralPath (Join-Path $releaseMod 'old-mod-only.bin') -Value 'obsolete'
    $releaseOtherMod = Join-Path $gameRelease.Mods 'OtherMod'
    New-Item -ItemType Directory -Path $releaseOtherMod | Out-Null
    Set-Content -LiteralPath (Join-Path $releaseOtherMod 'keep.txt') -Value 'keep'
    $releaseUpgrade = Invoke-Upgrader $gameRelease $hostRelease -UpgradeScript (Join-Path $releaseScripts 'upgrade.ps1') -EnvironmentOverride @{
        THUNDERROAD_MCP_CONFIG = $releaseBridgePath
        THUNDERROAD_MCP_LAUNCH_CONFIG = $releaseLaunchPath
    }
    if ($releaseUpgrade.ExitCode -ne 0) { throw "Release upgrade failed: $($releaseUpgrade.Output)`n$($releaseUpgrade.Error)" }
    if (($releaseUpgrade.Output + $releaseUpgrade.Error).Contains([string]$releaseConfig.token)) {
        throw 'Release upgrade printed the bridge token.'
    }
    if ((Test-Path -LiteralPath (Join-Path $hostRelease 'old-host-only.bin')) -or
        (Test-Path -LiteralPath (Join-Path $releaseMod 'old-mod-only.bin')) -or
        -not (Test-ByteEqual $savedReleaseMcp ([IO.File]::ReadAllBytes($releaseMcpPath))) -or
        -not (Test-ByteEqual $savedReleaseLaunch ([IO.File]::ReadAllBytes($releaseLaunchPath))) -or
        -not (Test-ByteEqual $savedReleaseBridge ([IO.File]::ReadAllBytes($releaseBridgePath))) -or
        (Get-Content -LiteralPath (Join-Path $releaseOtherMod 'keep.txt') -Raw).Trim() -ne 'keep' -or
        @(Get-ChildItem -LiteralPath $gameRelease.Mods -Directory | Where-Object { $_.Name -match 'backup|rollback' }).Count -gt 0) {
        throw 'Release upgrade retained obsolete files, changed configs/unrelated mods, or exposed a backup to game mod discovery.'
    }
    Test-GeneratedStdioServer ((Get-Content -LiteralPath $releaseMcpPath -Raw | ConvertFrom-Json).mcpServers.ThunderRoadMcp)

    $pwshCommand = Get-Command pwsh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pwshCommand) {
        $pwshUpgrade = Invoke-Upgrader $gameRelease $hostRelease -UpgradeScript (Join-Path $releaseScripts 'upgrade.ps1') -PowerShellPath $pwshCommand.Source -EnvironmentOverride @{
            THUNDERROAD_MCP_CONFIG = $releaseBridgePath
            THUNDERROAD_MCP_LAUNCH_CONFIG = $releaseLaunchPath
        }
        if ($pwshUpgrade.ExitCode -ne 0 -or
            ($pwshUpgrade.Output + $pwshUpgrade.Error).Contains([string]$releaseConfig.token) -or
            -not (Test-ByteEqual $savedReleaseBridge ([IO.File]::ReadAllBytes($releaseBridgePath)))) {
            throw "PowerShell 7 upgrade rejected or changed a valid existing bridge config: $($pwshUpgrade.Output)`n$($pwshUpgrade.Error)"
        }
    } else {
        Write-Warning 'Skipping PowerShell 7 bridge config upgrade check: pwsh was not found on PATH.'
    }

    $sourceDrive = [IO.Path]::GetPathRoot($sourceRoot)
    $temporaryDrive = [IO.Path]::GetPathRoot([IO.Path]::GetTempPath())
    if (-not [string]::Equals($sourceDrive, $temporaryDrive, [StringComparison]::OrdinalIgnoreCase)) {
        $crossVolumeRoot = Join-Path $sourceDrive ('ThunderRoadMcp-upgrade-check-' + [Guid]::NewGuid().ToString('N'))
        if (Test-Path -LiteralPath $crossVolumeRoot) { throw "Cross-volume fixture path unexpectedly exists: $crossVolumeRoot" }
        New-Item -ItemType Directory -Path $crossVolumeRoot | Out-Null
        try {
            $crossGame = New-Game (Join-Path $crossVolumeRoot 'synthetic-game')
            $crossHost = Join-Path $root 'cross-volume-host'
            $crossConfig = Join-Path $root 'cross-volume-config'
            $crossInstall = Invoke-Installer $crossGame $crossHost $crossConfig -DotnetPath $testDotnet
            if ($crossInstall.ExitCode -ne 0) { throw "Cross-volume source install failed: $($crossInstall.Output)`n$($crossInstall.Error)" }
            $crossMcpPath = Join-Path $crossHost 'mcp-config.json'
            $crossLaunchPath = Join-Path $crossConfig 'launch.json'
            $crossBridgeConfigPath = Join-Path $crossConfig 'bridge.json'
            $crossSavedMcp = [IO.File]::ReadAllBytes($crossMcpPath)
            $crossSavedLaunch = [IO.File]::ReadAllBytes($crossLaunchPath)
            $crossSavedBridge = [IO.File]::ReadAllBytes($crossBridgeConfigPath)
            Set-Content -LiteralPath (Join-Path $crossHost 'old-host-only.bin') -Value 'obsolete'
            Set-Content -LiteralPath (Join-Path $crossGame.Mods 'ThunderRoadMcp\old-mod-only.bin') -Value 'obsolete'
            $crossOtherMod = Join-Path $crossGame.Mods 'AnotherMod'
            New-Item -ItemType Directory -Path $crossOtherMod | Out-Null
            Set-Content -LiteralPath (Join-Path $crossOtherMod 'keep.txt') -Value 'keep'
            $crossUpgrade = Invoke-Upgrader $crossGame $crossHost -UpgradeScript (Join-Path $releaseScripts 'upgrade.ps1') -EnvironmentOverride @{
                THUNDERROAD_MCP_CONFIG = $crossBridgeConfigPath
                THUNDERROAD_MCP_LAUNCH_CONFIG = $crossLaunchPath
            }
            if ($crossUpgrade.ExitCode -ne 0) { throw "Cross-volume release upgrade failed: $($crossUpgrade.Output)`n$($crossUpgrade.Error)" }
            if ((Test-Path -LiteralPath (Join-Path $crossHost 'old-host-only.bin')) -or
                (Test-Path -LiteralPath (Join-Path $crossGame.Mods 'ThunderRoadMcp\old-mod-only.bin')) -or
                -not (Test-ByteEqual $crossSavedMcp ([IO.File]::ReadAllBytes($crossMcpPath))) -or
                -not (Test-ByteEqual $crossSavedLaunch ([IO.File]::ReadAllBytes($crossLaunchPath))) -or
                -not (Test-ByteEqual $crossSavedBridge ([IO.File]::ReadAllBytes($crossBridgeConfigPath))) -or
                (Get-Content -LiteralPath (Join-Path $crossOtherMod 'keep.txt') -Raw).Trim() -ne 'keep' -or
                @(Get-ChildItem -LiteralPath $crossGame.Mods -Directory | Where-Object { $_.Name -match 'backup|rollback' }).Count -gt 0) {
                throw 'Cross-volume upgrade changed unrelated/config data, retained obsolete outputs, or put backups in the game mod root.'
            }
            Test-GeneratedStdioServer ((Get-Content -LiteralPath $crossMcpPath -Raw | ConvertFrom-Json).mcpServers.ThunderRoadMcp)
        } finally {
            Remove-Item -LiteralPath $crossVolumeRoot -Recurse -Force
        }
    } else {
        Write-Warning 'Skipping cross-volume upgrade check: source and temporary paths are on the same volume.'
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

    $hostBMcpPath = Join-Path $hostB 'mcp-config.json'
    $savedHostBMcp = [IO.File]::ReadAllBytes($hostBMcpPath)
    $savedLaunchBytes = [IO.File]::ReadAllBytes($launchBPath)
    $savedBridgeBytes = [IO.File]::ReadAllBytes($bridgeBPath)
    Set-Content -LiteralPath (Join-Path $hostB 'old-host-only.bin') -Value 'obsolete'
    Set-Content -LiteralPath (Join-Path $gameB.Mods 'ThunderRoadMcp\old-mod-only.bin') -Value 'obsolete'
    $otherMod = Join-Path $gameB.Mods 'UnrelatedMod'
    New-Item -ItemType Directory -Path $otherMod | Out-Null
    Set-Content -LiteralPath (Join-Path $otherMod 'user.txt') -Value 'untouched'
    $sourceUpgrade = Invoke-Upgrader $gameB $hostB -DotnetPath $testDotnet -NoBridgeConfig -EnvironmentOverride @{
        THUNDERROAD_MCP_CONFIG = $bridgeBPath
        THUNDERROAD_MCP_LAUNCH_CONFIG = $launchBPath
    }
    if ($sourceUpgrade.ExitCode -ne 0) { throw "Source upgrade failed: $($sourceUpgrade.Output)`n$($sourceUpgrade.Error)" }
    if (($sourceUpgrade.Output + $sourceUpgrade.Error).Contains('b' * 64)) {
        throw 'Source upgrade printed the preserved bridge token.'
    }
    if ((Test-Path -LiteralPath (Join-Path $hostB 'old-host-only.bin')) -or
        (Test-Path -LiteralPath (Join-Path $gameB.Mods 'ThunderRoadMcp\old-mod-only.bin')) -or
        -not (Test-ByteEqual $savedHostBMcp ([IO.File]::ReadAllBytes($hostBMcpPath))) -or
        -not (Test-ByteEqual $savedLaunchBytes ([IO.File]::ReadAllBytes($launchBPath))) -or
        -not (Test-ByteEqual $savedBridgeBytes ([IO.File]::ReadAllBytes($bridgeBPath))) -or
        (Get-Content -LiteralPath (Join-Path $otherMod 'user.txt') -Raw).Trim() -ne 'untouched' -or
        @(Get-ChildItem -LiteralPath $gameB.Mods -Directory | Where-Object { $_.Name -match 'backup|rollback' }).Count -gt 0) {
        throw 'Source upgrade retained obsolete files, changed configs/unrelated mods, or exposed a backup to game mod discovery.'
    }
    Test-GeneratedStdioServer ((Get-Content -LiteralPath $hostBMcpPath -Raw | ConvertFrom-Json).mcpServers.ThunderRoadMcp)

    $hostNoConfigMcp = Join-Path $hostNoConfig 'mcp-config.json'
    $savedNoConfigMcp = [IO.File]::ReadAllBytes($hostNoConfigMcp)
    $noBridgeConfigPath = Join-Path $configNoConfig 'bridge.json'
    $noLaunchConfigPath = Join-Path $configNoConfig 'launch.json'
    $savedNoConfigLaunch = [IO.File]::ReadAllBytes($noLaunchConfigPath)
    $absentConfigUpgrade = Invoke-Upgrader $gameNoConfig $hostNoConfig -DotnetPath $testDotnet -EnvironmentOverride @{
        THUNDERROAD_MCP_CONFIG = $noBridgeConfigPath
        THUNDERROAD_MCP_LAUNCH_CONFIG = $noLaunchConfigPath
    }
    if ($absentConfigUpgrade.ExitCode -ne 0 -or (Test-Path -LiteralPath $noBridgeConfigPath) -or
        -not (Test-ByteEqual $savedNoConfigMcp ([IO.File]::ReadAllBytes($hostNoConfigMcp))) -or
        -not (Test-ByteEqual $savedNoConfigLaunch ([IO.File]::ReadAllBytes($noLaunchConfigPath)))) {
        throw "Upgrade changed absent config state or existing host/launch config: $($absentConfigUpgrade.Output)`n$($absentConfigUpgrade.Error)"
    }

    $hostSkipMcp = Join-Path $hostSkip 'mcp-config.json'
    $savedSkipMcp = [IO.File]::ReadAllBytes($hostSkipMcp)
    Set-Content -LiteralPath (Join-Path $hostSkip 'old-host-only.bin') -Value 'obsolete'
    $hostOnlyUpgrade = Invoke-Upgrader $null $hostSkip -SkipBridge -DotnetPath $testDotnet -EnvironmentOverride @{
        THUNDERROAD_MCP_CONFIG = (Join-Path $configSkip 'bridge.json')
        THUNDERROAD_MCP_LAUNCH_CONFIG = (Join-Path $configSkip 'launch.json')
    }
    if ($hostOnlyUpgrade.ExitCode -ne 0 -or
        (Test-Path -LiteralPath (Join-Path $hostSkip 'old-host-only.bin')) -or
        -not (Test-ByteEqual $savedSkipMcp ([IO.File]::ReadAllBytes($hostSkipMcp))) -or
        (Test-Path -LiteralPath (Join-Path $gameSkip.Mods 'ThunderRoadMcp'))) {
        throw "Host-only upgrade failed, modified MCP config, or installed a bridge: $($hostOnlyUpgrade.Output)`n$($hostOnlyUpgrade.Error)"
    }

    $hostSkipDll = Join-Path $hostSkip 'ThunderRoadMcp.Host.dll'
    $hostSkipBytes = [IO.File]::ReadAllBytes($hostSkipDll)
    $missingModUpgrade = Invoke-Upgrader $gameSkip $hostSkip -DotnetPath $testDotnet
    if ($missingModUpgrade.ExitCode -eq 0 -or
        -not (Test-ByteEqual $hostSkipBytes ([IO.File]::ReadAllBytes($hostSkipDll))) -or
        (Test-Path -LiteralPath (Join-Path $gameSkip.Mods 'ThunderRoadMcp'))) {
        throw 'Full upgrade without the existing bridge mod was not refused without changing the host or game.'
    }

    $badRelease = Join-Path $root 'invalid-release'
    $badReleaseScripts = Join-Path $badRelease 'scripts'
    $badReleaseHost = Join-Path $badRelease 'host'
    $badReleaseBridge = Join-Path $badRelease 'bridge'
    New-Item -ItemType Directory -Path $badReleaseScripts, $badReleaseHost, $badReleaseBridge | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'scripts\upgrade.ps1') -Destination $badReleaseScripts
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'scripts\install.ps1') -Destination $badReleaseScripts
    Copy-Item -Path (Join-Path $hostRelease '*') -Destination $badReleaseHost -Recurse
    Copy-Item -LiteralPath (Join-Path $releaseBridge 'versions.json') -Destination $badReleaseBridge
    Set-Content -LiteralPath (Join-Path $badReleaseBridge 'ThunderRoadMcp.zip') -Value 'not a package'
    $releaseHostBeforeInvalid = [IO.File]::ReadAllBytes($releaseDll)
    $releaseBridgeBeforeInvalid = [IO.File]::ReadAllBytes((Join-Path $releaseMod 'ThunderRoadMcp.Bridge.dll'))
    $invalidPackageUpgrade = Invoke-Upgrader $gameRelease $hostRelease -UpgradeScript (Join-Path $badReleaseScripts 'upgrade.ps1') -EnvironmentOverride @{
        THUNDERROAD_MCP_CONFIG = $releaseBridgePath
        THUNDERROAD_MCP_LAUNCH_CONFIG = $releaseLaunchPath
    }
    if ($invalidPackageUpgrade.ExitCode -eq 0 -or
        -not (Test-ByteEqual $releaseHostBeforeInvalid ([IO.File]::ReadAllBytes($releaseDll))) -or
        -not (Test-ByteEqual $releaseBridgeBeforeInvalid ([IO.File]::ReadAllBytes((Join-Path $releaseMod 'ThunderRoadMcp.Bridge.dll'))))) {
        throw 'Invalid release package was not rejected before changing the existing host/mod.'
    }

    $mismatchedGameUpgrade = Invoke-Upgrader $gameB $hostRelease -UpgradeScript (Join-Path $releaseScripts 'upgrade.ps1') -EnvironmentOverride @{
        THUNDERROAD_MCP_CONFIG = $releaseBridgePath
        THUNDERROAD_MCP_LAUNCH_CONFIG = $releaseLaunchPath
    }
    if ($mismatchedGameUpgrade.ExitCode -eq 0 -or
        -not (Test-Path -LiteralPath $releaseDll) -or
        -not (Test-Path -LiteralPath (Join-Path $releaseMod 'ThunderRoadMcp.Bridge.dll'))) {
        throw 'Mismatched explicit game selection was not refused without removing the existing host/mod.'
    }

    $runningHostStart = New-Object Diagnostics.ProcessStartInfo
    $runningHostStart.FileName = $releaseExe
    $runningHostStart.WorkingDirectory = $hostRelease
    $runningHostStart.UseShellExecute = $false
    $runningHostStart.CreateNoWindow = $true
    $runningHostStart.RedirectStandardInput = $true
    $runningHostStart.RedirectStandardOutput = $true
    $runningHostStart.RedirectStandardError = $true
    $runningHostStart.EnvironmentVariables['THUNDERROAD_MCP_CONFIG'] = $releaseBridgePath
    $runningHostStart.EnvironmentVariables['THUNDERROAD_MCP_LAUNCH_CONFIG'] = $releaseLaunchPath
    $runningHostProcess = New-Object Diagnostics.Process
    $runningHostProcess.StartInfo = $runningHostStart
    [void]$runningHostProcess.Start()
    try {
        Start-Sleep -Milliseconds 500
        if ($runningHostProcess.HasExited) { throw 'Isolated host exited before the running-host upgrade refusal check.' }
        $runningUpgrade = Invoke-Upgrader $gameRelease $hostRelease -UpgradeScript (Join-Path $releaseScripts 'upgrade.ps1') -EnvironmentOverride @{
            THUNDERROAD_MCP_CONFIG = $releaseBridgePath
            THUNDERROAD_MCP_LAUNCH_CONFIG = $releaseLaunchPath
        }
    } finally {
        if (-not $runningHostProcess.HasExited) { $runningHostProcess.Kill() }
        [void]$runningHostProcess.WaitForExit(5000)
        $runningHostProcess.Dispose()
    }
    if ($runningUpgrade.ExitCode -eq 0 -or -not (Test-Path -LiteralPath $releaseDll)) {
        throw 'Upgrade did not refuse the running isolated host with the old host still present.'
    }

    $lockedBridgePath = Join-Path $releaseMod 'ThunderRoadMcp.Bridge.dll'
    $lockedBridgeBytes = [IO.File]::ReadAllBytes($lockedBridgePath)
    Set-Content -LiteralPath (Join-Path $hostRelease 'rollback-sentinel.txt') -Value 'original host data' -Encoding ASCII
    $lockedBridgeStream = [IO.File]::Open($lockedBridgePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        $lockedUpgrade = Invoke-Upgrader $gameRelease $hostRelease -UpgradeScript (Join-Path $releaseScripts 'upgrade.ps1') -EnvironmentOverride @{
            THUNDERROAD_MCP_CONFIG = $releaseBridgePath
            THUNDERROAD_MCP_LAUNCH_CONFIG = $releaseLaunchPath
        }
    } finally {
        $lockedBridgeStream.Dispose()
    }
    if ($lockedUpgrade.ExitCode -eq 0 -or
        -not (Test-ByteEqual $lockedBridgeBytes ([IO.File]::ReadAllBytes($lockedBridgePath))) -or
        -not (Test-ByteEqual $savedReleaseMcp ([IO.File]::ReadAllBytes($releaseMcpPath))) -or
        (Get-Content -LiteralPath (Join-Path $hostRelease 'rollback-sentinel.txt') -Raw).Trim() -ne 'original host data' -or
        -not (Test-Path -LiteralPath $releaseDll)) {
        throw 'Upgrade did not refuse a locked bridge file or restore the original host/mod data.'
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

    Write-Output 'PASS: isolated source and release installs/upgrades; byte-preserved configs; replaced host/mod outputs; host-only and missing-mod behavior; actual game Managed references; MCP initialize/tools/list; invalid package, running host, locked files, and mismatched games refused; unrelated mods preserved.'
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
