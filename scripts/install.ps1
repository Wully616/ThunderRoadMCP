[CmdletBinding()]
param(
    [string]$GameExecutablePath,
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'ThunderRoadMcp\host'),
    [string]$DotnetPath,
    [switch]$SkipBridge,
    [switch]$NoBridgeConfig,
    [switch]$NonInteractive,
    [switch]$Upgrade
)

$ErrorActionPreference = 'Stop'

function Stop-Install([string]$Message) { throw $Message }

function Test-AbsoluteWindowsPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    return ($Path -match '^[A-Za-z]:[\\/]' -or $Path -match '^\\\\[^\\]+\\[^\\]+(?:\\|$)')
}

function Test-Executable([string]$Path) {
    if (-not (Test-AbsoluteWindowsPath $Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Stop-Install "Game executable must be an existing absolute Windows path: $Path"
    }
}

function Test-BridgePort($Value) {
    # Windows PowerShell 5.1 parses JSON integers as Int32, PowerShell 7 as Int64; both are valid JSON numbers.
    if ($null -eq $Value -or $Value -isnot [ValueType] -or $Value -is [bool]) { return $false }
    try { $Number = [decimal]$Value } catch { return $false }
    return $Number -ge 1 -and $Number -le 65535 -and $Number -eq [math]::Truncate($Number)
}

function Format-InstallationCandidate($Installation, [int]$Number) {
    $path = [string]$Installation.executablePath
    $version = $null
    try { $version = (Get-Item -LiteralPath $path -ErrorAction Stop).VersionInfo.FileVersion } catch { $version = $null }
    $text = "$Number. $($Installation.store): $path"
    if (-not [string]::IsNullOrWhiteSpace($version)) { $text += " ($version)" }
    return $text
}

function Get-DotnetCapability([string]$Path) {
    $capability = [pscustomobject]@{
        Path = $Path
        SdkList = @()
        RuntimeList = @()
        Has10Sdk = $false
        Has10Runtime = $false
        Error = $null
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $capability.Error = 'not found'
        return $capability
    }
    $sdkExitCode = 1
    $runtimeExitCode = 1
    try {
        $capability.SdkList = @(& $Path --list-sdks 2>$null)
        $sdkExitCode = $LASTEXITCODE
        $capability.RuntimeList = @(& $Path --list-runtimes 2>$null)
        $runtimeExitCode = $LASTEXITCODE
    } catch {
        $capability.Error = $_.Exception.Message
        return $capability
    }
    if ($sdkExitCode -ne 0 -and $runtimeExitCode -ne 0) {
        $capability.Error = "dotnet probes exited $sdkExitCode and $runtimeExitCode"
        return $capability
    }
    $capability.Has10Sdk = $sdkExitCode -eq 0 -and @($capability.SdkList | Where-Object { [string]$_ -match '^10\.\d+\.\d+\s' }).Count -gt 0
    $capability.Has10Runtime = $runtimeExitCode -eq 0 -and @($capability.RuntimeList | Where-Object { [string]$_ -match '^Microsoft\.NETCore\.App 10\.\d+\.\d+\s' }).Count -gt 0
    return $capability
}

function Format-DotnetCapabilityReport($Capabilities) {
    $lines = New-Object 'System.Collections.Generic.List[string]'
    foreach ($capability in @($Capabilities)) {
        $sdkVersions = New-Object 'System.Collections.Generic.List[string]'
        foreach ($line in @($capability.SdkList)) {
            if ([string]$line -match '^\s*(\d+\.\d+\.\d+(?:-[^\s]+)?)\s') { $sdkVersions.Add($Matches[1]) }
        }
        $runtimeVersions = New-Object 'System.Collections.Generic.List[string]'
        foreach ($line in @($capability.RuntimeList)) {
            if ([string]$line -match '^Microsoft\.NETCore\.App\s+(\d+\.\d+\.\d+(?:-[^\s]+)?)\s') { $runtimeVersions.Add($Matches[1]) }
        }
        $sdkText = if ($sdkVersions.Count -gt 0) { $sdkVersions -join ', ' } else { 'none' }
        $runtimeText = if ($runtimeVersions.Count -gt 0) { $runtimeVersions -join ', ' } else { 'none' }
        $errorText = if ($capability.Error) { " ($($capability.Error))" } else { '' }
        $lines.Add("  $($capability.Path): .NET SDKs: $sdkText; NETCore runtimes: $runtimeText$errorText")
    }
    return ($lines -join [Environment]::NewLine)
}

function Get-RequiredProperty($Object, [string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { Stop-Install "Host discovery result is missing '$Name'." }
    return $property.Value
}

function Ensure-DirectoryPath([string]$Path, [System.Collections.Generic.List[string]]$CreatedDirectories) {
    $toCreate = New-Object 'System.Collections.Generic.List[string]'
    $current = $Path
    while ($current -and -not (Test-Path -LiteralPath $current -PathType Container)) {
        if (Test-Path -LiteralPath $current) { Stop-Install "Path exists but is not a directory: $current" }
        $toCreate.Add($current)
        $current = Split-Path -Parent $current
    }
    for ($i = $toCreate.Count - 1; $i -ge 0; $i--) {
        [void][IO.Directory]::CreateDirectory($toCreate[$i])
        $CreatedDirectories.Add($toCreate[$i])
    }
}

function Get-DirectoryInventory([string]$Directory) {
    $inventory = @{}
    foreach ($entry in Get-ChildItem -LiteralPath $Directory -Recurse -Force) {
        $relativePath = $entry.FullName.Substring($Directory.Length).TrimStart([char[]]@('\', '/'))
        if ($entry.PSIsContainer) { $inventory['D:' + $relativePath] = '' }
        else { $inventory['F:' + $relativePath] = (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash }
    }
    return ,$inventory
}

function Test-DirectoryInventory([string]$Directory, $Expected) {
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { return $false }
    $actual = Get-DirectoryInventory $Directory
    if ($actual.Count -ne $Expected.Count) { return $false }
    foreach ($path in $Expected.Keys) {
        if (-not $actual.ContainsKey($path) -or $actual[$path] -ne $Expected[$path]) { return $false }
    }
    return $true
}

function Write-StagedText([string]$Target, [string]$Text, [string]$Id, [System.Collections.Generic.List[string]]$CreatedDirectories) {
    $parent = Split-Path -Parent $Target
    Ensure-DirectoryPath $parent $CreatedDirectories
    $stage = Join-Path $parent ('.' + [IO.Path]::GetFileName($Target) + '.ThunderRoadMcp-' + $Id + '.tmp')
    $encoding = [System.Text.UTF8Encoding]::new($false)
    $stream = [IO.FileStream]::new($stage, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $writer = [IO.StreamWriter]::new($stream, $encoding)
        try { $writer.Write($Text) } finally { $writer.Dispose() }
    } catch {
        $stream.Dispose()
        throw
    }
    return [pscustomobject]@{ Target = $Target; Stage = $stage; Text = $Text; Committed = $false }
}

function Remove-OwnedFile($Configuration) {
    if (-not $Configuration.Committed -or -not (Test-Path -LiteralPath $Configuration.Target -PathType Leaf)) { return }
    if ([IO.File]::ReadAllText($Configuration.Target) -ceq $Configuration.Text) {
        [IO.File]::Delete($Configuration.Target)
    } else {
        Write-Warning "Preserving config changed after this installer created it: $($Configuration.Target)"
    }
}

function Test-PathWithin([string]$Path, [string]$Parent) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/') + '\'
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') + '\'
    return $fullPath.StartsWith($fullParent, [StringComparison]::OrdinalIgnoreCase)
}

function Test-DirectoryReparsePoint([string]$Path) {
    $current = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($Path))
    while ($current) {
        if ($current.Exists -and ($current.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return $true }
        $current = $current.Parent
    }
    return $false
}

function Assert-UpgradeProcessesStopped([string]$HostDirectory, [string]$GamePath) {
    $hostExe = Join-Path $HostDirectory 'ThunderRoadMcp.Host.exe'
    $hostDll = Join-Path $HostDirectory 'ThunderRoadMcp.Host.dll'
    $gameName = [IO.Path]::GetFileNameWithoutExtension($GamePath)
    $processes = @(Get-CimInstance Win32_Process -ErrorAction Stop)
    foreach ($process in $processes) {
        $name = [string]$process.Name
        $command = [string]$process.CommandLine
        $path = [string]$process.ExecutablePath
        $isHost = ($name -ieq 'ThunderRoadMcp.Host.exe' -and $path -and
            [string]::Equals([IO.Path]::GetFullPath($path), [IO.Path]::GetFullPath($hostExe), [StringComparison]::OrdinalIgnoreCase)) -or
            ($name -ieq 'dotnet.exe' -and $command -and
            $command.IndexOf($hostDll, [StringComparison]::OrdinalIgnoreCase) -ge 0)
        $isGame = $name -ieq ($gameName + '.exe') -and $path -and
            [string]::Equals([IO.Path]::GetFullPath($path), [IO.Path]::GetFullPath($GamePath), [StringComparison]::OrdinalIgnoreCase)
        if ($isHost -or $isGame) {
            $label = if ($isHost) { 'MCP host' } else { 'selected game' }
            Stop-Install "Stop or disconnect the $label process before upgrading. Detected PID $($process.ProcessId); no process was terminated."
        }
    }
}

$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$hostProject = Join-Path $sourceRoot 'src\ThunderRoadMcp.Host\ThunderRoadMcp.Host.csproj'
$packageScript = Join-Path $PSScriptRoot 'package-bridge.ps1'
$releaseHostDirectory = Join-Path $sourceRoot 'host'
$releaseHostExecutable = Join-Path $releaseHostDirectory 'ThunderRoadMcp.Host.exe'
$prebuiltHost = Test-Path -LiteralPath $releaseHostExecutable -PathType Leaf
$prebuiltBridgeZip = Join-Path $sourceRoot 'bridge\ThunderRoadMcp.zip'
$bridgeMetadataPath = Join-Path $sourceRoot 'bridge\versions.json'
$dotnetPathOverride = $null
if (-not [string]::IsNullOrWhiteSpace($DotnetPath)) {
    $expandedDotnetPath = [Environment]::ExpandEnvironmentVariables($DotnetPath.Trim().Trim('"'))
    if (Test-Path -LiteralPath $expandedDotnetPath -PathType Container) {
        $expandedDotnetPath = Join-Path $expandedDotnetPath 'dotnet.exe'
    }
    if (-not (Test-Path -LiteralPath $expandedDotnetPath -PathType Leaf)) {
        Stop-Install "-DotnetPath does not point to an existing dotnet executable: $DotnetPath. Pass the absolute path to dotnet.exe or omit -DotnetPath to search PATH, DOTNET_ROOT, %USERPROFILE%\.dotnet, and the Program Files dotnet locations. Install the stable .NET 10 SDK/runtime from https://dotnet.microsoft.com/download/dotnet/10.0."
    }
    $dotnetPathOverride = [IO.Path]::GetFullPath($expandedDotnetPath)
}

$candidatePaths = New-Object 'System.Collections.Generic.List[string]'
if ($dotnetPathOverride) {
    $candidatePaths.Add($dotnetPathOverride)
} else {
    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
        $candidatePaths.Add((Join-Path $env:DOTNET_ROOT 'dotnet.exe'))
    }
    $dotnetRootX86 = ${env:DOTNET_ROOT(x86)}
    if (-not [string]::IsNullOrWhiteSpace($dotnetRootX86)) {
        $candidatePaths.Add((Join-Path $dotnetRootX86 'dotnet.exe'))
    }
    foreach ($dotnetCommand in @(Get-Command dotnet -CommandType Application -All -ErrorAction SilentlyContinue)) {
        if ($null -ne $dotnetCommand -and -not [string]::IsNullOrWhiteSpace($dotnetCommand.Source)) {
            $candidatePaths.Add([string]$dotnetCommand.Source)
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $candidatePaths.Add((Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $candidatePaths.Add((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))
    }
    $programFilesX86 = ${env:ProgramFiles(x86)}
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $candidatePaths.Add((Join-Path $programFilesX86 'dotnet\dotnet.exe'))
    }
}

$seenDotnetPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$seenAttemptedPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$candidateExecutables = New-Object 'System.Collections.Generic.List[string]'
$attemptedDotnetPaths = New-Object 'System.Collections.Generic.List[string]'
foreach ($candidatePath in $candidatePaths) {
    $expandedCandidate = [Environment]::ExpandEnvironmentVariables([string]$candidatePath)
    try { $fullCandidate = [IO.Path]::GetFullPath($expandedCandidate) } catch { $fullCandidate = $expandedCandidate }
    if ($seenAttemptedPaths.Add($fullCandidate)) { $attemptedDotnetPaths.Add($fullCandidate) }
    if (-not (Test-Path -LiteralPath $fullCandidate -PathType Leaf)) { continue }
    if ($seenDotnetPaths.Add($fullCandidate)) { $candidateExecutables.Add($fullCandidate) }
}

if ($candidateExecutables.Count -eq 0) {
    $checkedText = if ($attemptedDotnetPaths.Count -gt 0) { $attemptedDotnetPaths -join '; ' } else { 'none' }
    if ($prebuiltHost) {
        Stop-Install ".NET 10 runtime (Microsoft.NETCore.App 10.x) is required to run the published MCP host. Install the stable .NET 10 SDK/runtime from https://dotnet.microsoft.com/download/dotnet/10.0. No dotnet executable was found. Checked: $checkedText"
    }
    Stop-Install ".NET 10 SDK and runtime (Microsoft.NETCore.App 10.x) are required to publish and run ThunderRoadMcp from source. Install the stable .NET 10 SDK/runtime from https://dotnet.microsoft.com/download/dotnet/10.0. No dotnet executable was found. Checked: $checkedText"
}

$dotnetCapabilities = @($candidateExecutables | ForEach-Object { Get-DotnetCapability $_ })
$dotnetReport = Format-DotnetCapabilityReport $dotnetCapabilities
$runtimeCandidates = @($dotnetCapabilities | Where-Object { $_.Has10Runtime })
$sdkCandidates = @($dotnetCapabilities | Where-Object { $_.Has10Sdk })
$bothCandidates = @($dotnetCapabilities | Where-Object { $_.Has10Sdk -and $_.Has10Runtime })

if ($prebuiltHost) {
    if ($runtimeCandidates.Count -eq 0) {
        Stop-Install ".NET 10 runtime (Microsoft.NETCore.App 10.x) is required to run the published MCP host during installation and afterward. Install the stable .NET 10 SDK/runtime from https://dotnet.microsoft.com/download/dotnet/10.0. Checked:`n$dotnetReport"
    }
    $dotnet = $runtimeCandidates[0].Path
    if ($bothCandidates.Count -gt 0) {
        $sdkDotnet = $bothCandidates[0].Path
        $sdkAvailable = $true
    } elseif ($sdkCandidates.Count -gt 0) {
        $sdkDotnet = $sdkCandidates[0].Path
        $sdkAvailable = $true
    } else {
        $sdkDotnet = $null
        $sdkAvailable = $false
    }
} else {
    if ($bothCandidates.Count -eq 0) {
        if ($sdkCandidates.Count -eq 0) {
            Stop-Install ".NET 10 SDK is required to publish ThunderRoadMcp from source. Install the stable .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0. Checked:`n$dotnetReport"
        }
        Stop-Install ".NET 10 SDK was found, but no dotnet executable also provides Microsoft.NETCore.App 10.x for the published host. Install a stable .NET 10 SDK that includes its runtime, or add the runtime, then rerun. Checked:`n$dotnetReport"
    }
    $dotnet = $bothCandidates[0].Path
    $sdkDotnet = $dotnet
    $sdkAvailable = $true
    if (-not (Test-Path -LiteralPath $hostProject -PathType Leaf)) { Stop-Install "Host project not found: $hostProject. Run this script from a source checkout." }
}

$installDirectory = [IO.Path]::GetFullPath($InstallDirectory)
if ($Upgrade) {
    if (-not (Test-Path -LiteralPath $installDirectory -PathType Container) -or
        (Test-DirectoryReparsePoint $installDirectory) -or
        (Test-PathWithin $installDirectory $sourceRoot) -or
        (Test-PathWithin $sourceRoot $installDirectory)) {
        Stop-Install "Upgrade requires an existing, non-reparse host directory separate from the source checkout: $installDirectory"
    }
    foreach ($requiredHostFile in @('ThunderRoadMcp.Host.exe', 'ThunderRoadMcp.Host.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $installDirectory $requiredHostFile) -PathType Leaf)) {
            Stop-Install "Not a ThunderRoadMcp host installation; missing $requiredHostFile in $installDirectory"
        }
    }
    foreach ($requiredHostFile in @('ThunderRoadMcp.Host.deps.json', 'ThunderRoadMcp.Host.runtimeconfig.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $installDirectory $requiredHostFile) -PathType Leaf)) {
            Stop-Install "Not a recognized ThunderRoadMcp host installation; missing $requiredHostFile in $installDirectory"
        }
    }
} elseif (Test-Path -LiteralPath $installDirectory) {
    Stop-Install "Host install directory already exists; refusing to overwrite it: $installDirectory`nChoose a new -InstallDirectory or move/remove this directory yourself after preserving anything you need."
}

$localConfigRoot = Join-Path $env:LOCALAPPDATA 'ThunderRoadMcp'
$launchConfigPath = if ($env:THUNDERROAD_MCP_LAUNCH_CONFIG) { [IO.Path]::GetFullPath($env:THUNDERROAD_MCP_LAUNCH_CONFIG) } else { Join-Path $localConfigRoot 'launch.json' }
$bridgeConfigPath = if ($env:THUNDERROAD_MCP_CONFIG) { [IO.Path]::GetFullPath($env:THUNDERROAD_MCP_CONFIG) } else { Join-Path $localConfigRoot 'bridge.json' }
foreach ($configPath in @($launchConfigPath, $bridgeConfigPath)) {
    if ((Test-Path -LiteralPath $configPath) -and -not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        Stop-Install "Config path exists but is not a file; preserving it: $configPath"
    }
}

$launchConfigExists = Test-Path -LiteralPath $launchConfigPath -PathType Leaf
$launchOptions = $null
if ($launchConfigExists) {
    try { $launchOptions = Get-Content -LiteralPath $launchConfigPath -Raw | ConvertFrom-Json }
    catch { Stop-Install "Existing launch config is invalid JSON; preserving it: $launchConfigPath ($($_.Exception.Message))" }
    if (-not (Test-AbsoluteWindowsPath ([string]$launchOptions.executablePath)) -or
        -not (Test-Path -LiteralPath $launchOptions.executablePath -PathType Leaf) -or
        $null -eq $launchOptions.arguments -or $launchOptions.arguments -isnot [array]) {
        Stop-Install "Existing launch config must contain an existing absolute executablePath and a string arguments array; preserving it: $launchConfigPath"
    }
    foreach ($argument in $launchOptions.arguments) {
        if ($argument -isnot [string]) { Stop-Install "Existing launch config arguments must all be strings; preserving it: $launchConfigPath" }
    }
    if ($GameExecutablePath) {
        Test-Executable $GameExecutablePath
        if (-not [string]::Equals([IO.Path]::GetFullPath($GameExecutablePath), [IO.Path]::GetFullPath([string]$launchOptions.executablePath), [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Install "-GameExecutablePath conflicts with the existing launch config; refusing to switch the selected game. Preserve/edit it yourself: $launchConfigPath"
        }
    }
}

$bridgeConfigExists = Test-Path -LiteralPath $bridgeConfigPath -PathType Leaf
$bridgeConfig = $null
$newBridgeConfig = $null
if ($bridgeConfigExists) {
    try { $bridgeConfig = Get-Content -LiteralPath $bridgeConfigPath -Raw | ConvertFrom-Json }
    catch { Stop-Install "Existing bridge config is invalid JSON; preserving it: $bridgeConfigPath ($($_.Exception.Message))" }
    if ($bridgeConfig.enabled -isnot [bool] -or
        $bridgeConfig.host -isnot [string] -or $bridgeConfig.host -cne '127.0.0.1' -or
        -not (Test-BridgePort $bridgeConfig.port) -or
        $bridgeConfig.token -isnot [string] -or $bridgeConfig.token -notmatch '\A[0-9a-fA-F]{64}\z') {
        Stop-Install "Existing bridge config must have boolean enabled, loopback host, valid port, and a 64-hex token; preserving it: $bridgeConfigPath"
    }
}

$tempRoot = $null
$createdHost = $false
$createdMod = $false
$completed = $false
$hostInventory = $null
$modInventory = $null
$modDirectory = $null
$bridgeReady = $false
$stagedModOnVolume = $null
$bridgeStagingRoot = $null
$bridgeZipPath = $null
$stagedConfigs = New-Object 'System.Collections.Generic.List[object]'
$createdDirectories = New-Object 'System.Collections.Generic.List[string]'
try {
    $parentDirectory = Split-Path -Parent $installDirectory
    Ensure-DirectoryPath $parentDirectory $createdDirectories
    $tempRoot = Join-Path $parentDirectory ('.ThunderRoadMcp-install-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($tempRoot)
    $stagedHost = Join-Path $tempRoot 'host'
    $bridgeOutput = Join-Path $tempRoot 'bridge-package'
    $diagnostics = Join-Path $tempRoot 'host-cli.stderr'

    if ($prebuiltHost) {
        Write-Host "Using the prebuilt host in $releaseHostDirectory ..."
        [void][IO.Directory]::CreateDirectory($stagedHost)
        Copy-Item -Path (Join-Path $releaseHostDirectory '*') -Destination $stagedHost -Recurse -Force
    } else {
        Write-Host 'Publishing MCP host with the .NET 10 SDK...'
        & $dotnet publish $hostProject -c Release -o $stagedHost
        if ($LASTEXITCODE -ne 0) { Stop-Install 'Host publish failed; no game mod or user config was changed.' }
    }
    $stagedHostExe = Join-Path $stagedHost 'ThunderRoadMcp.Host.exe'
    $stagedHostDll = Join-Path $stagedHost 'ThunderRoadMcp.Host.dll'
    if (-not (Test-Path -LiteralPath $stagedHostExe -PathType Leaf) -or
        -not (Test-Path -LiteralPath $stagedHostDll -PathType Leaf)) {
        Stop-Install "Host install source did not produce $stagedHostExe and $stagedHostDll"
    }

    $installations = @()
    if (-not $SkipBridge) {
        $discoveryOutput = & $stagedHostExe --list-installations 2> $diagnostics
        if ($LASTEXITCODE -ne 0) {
            $errorText = if (Test-Path -LiteralPath $diagnostics) { Get-Content -LiteralPath $diagnostics -Raw } else { '' }
            Stop-Install "Host game discovery failed: $errorText"
        }
        try { $discovery = ($discoveryOutput -join [Environment]::NewLine) | ConvertFrom-Json }
        catch { Stop-Install "Host discovery returned invalid JSON: $($discoveryOutput -join [Environment]::NewLine)" }
        $installations = @(Get-RequiredProperty $discovery 'installations')
    }

    $selectedPath = $null
    if ($GameExecutablePath) {
        Test-Executable $GameExecutablePath
        $selectedPath = [IO.Path]::GetFullPath($GameExecutablePath)
    }

    if ($SkipBridge) {
        Write-Host 'Skipping the bridge; installing the MCP host only.'
    } elseif ($installations.Count -eq 0 -and -not $selectedPath -and -not $launchOptions) {
        if ($Upgrade) { Stop-Install "Upgrade could not find a game matching the installed bridge. Supply -GameExecutablePath for the same game, or use -SkipBridge for a host-only upgrade." }
        Write-Warning "No Blade & Sorcery installation was detected. Installing the MCP host only; no bridge mod or bridge config will be created. Install the game (or pass -GameExecutablePath) and rerun with a new -InstallDirectory to add the bridge."
    } else {
        if (-not $selectedPath) {
            if ($launchOptions) {
                $selectedPath = [IO.Path]::GetFullPath([string]$launchOptions.executablePath)
            } elseif ($installations.Count -eq 1) {
                $selectedPath = [string](Get-RequiredProperty $installations[0] 'executablePath')
            } else {
                $orderedInstallations = @($installations | Sort-Object -Property store, executablePath)
                $candidateLines = New-Object 'System.Collections.Generic.List[string]'
                for ($i = 0; $i -lt $orderedInstallations.Count; $i++) {
                    $candidateLines.Add('  ' + (Format-InstallationCandidate $orderedInstallations[$i] ($i + 1)))
                }
                $candidateText = $candidateLines -join [Environment]::NewLine
                if ($NonInteractive -or [Console]::IsInputRedirected) {
                    Stop-Install "Multiple Blade & Sorcery installations were found; select one with -GameExecutablePath:`n$candidateText`nRerun interactively without -NonInteractive to choose from a numbered prompt."
                }
                Write-Host 'Multiple Blade & Sorcery installations were found:'
                foreach ($line in $candidateLines) { Write-Host $line }
                while ($true) {
                    $answer = Read-Host "Select installation [1-$($orderedInstallations.Count)] (blank cancels)"
                    if ([string]::IsNullOrWhiteSpace($answer)) {
                        Write-Host 'Installation cancelled; no game was selected and nothing was installed.'
                        return
                    }
                    $index = 0
                    if ([int]::TryParse($answer.Trim(), [ref]$index) -and $index -ge 1 -and $index -le $orderedInstallations.Count) {
                        $selectedPath = [IO.Path]::GetFullPath([string]$orderedInstallations[$index - 1].executablePath)
                        break
                    }
                    Write-Warning "Enter a number from 1 to $($orderedInstallations.Count), or leave the answer blank to cancel."
                }
            }
        }

        $selected = @($installations | Where-Object { [string]::Equals([IO.Path]::GetFullPath([string]$_.executablePath), $selectedPath, [StringComparison]::OrdinalIgnoreCase) })
        if ($selected.Count -gt 1) { Stop-Install "Host discovery returned duplicate entries for $selectedPath." }
        if ($selected.Count -eq 1) {
            $managedDirectory = [string](Get-RequiredProperty $selected[0] 'managedDirectory')
            $modsDirectory = [string](Get-RequiredProperty $selected[0] 'modsDirectory')
        } else {
            if ([IO.Path]::GetFileName($selectedPath) -ine 'BladeAndSorcery.exe') { Stop-Install "Explicit game path must name BladeAndSorcery.exe: $selectedPath" }
            $dataDirectory = Join-Path (Split-Path -Parent $selectedPath) 'BladeAndSorcery_Data'
            $managedDirectory = Join-Path $dataDirectory 'Managed'
            $modsDirectory = Join-Path $dataDirectory 'StreamingAssets\Mods'
        }
        if (-not (Test-Path -LiteralPath (Join-Path $managedDirectory 'ThunderRoad.dll') -PathType Leaf)) {
            Stop-Install "Game Managed assemblies are missing or invalid: $managedDirectory. Install/repair the game before building the bridge."
        }
        $streamingAssetsDirectory = Split-Path -Parent $modsDirectory
        if ($Upgrade -and ((Test-PathWithin $modsDirectory $sourceRoot) -or (Test-PathWithin $sourceRoot $modsDirectory) -or
            (Test-PathWithin $installDirectory $modsDirectory) -or (Test-PathWithin $modsDirectory $installDirectory) -or
            (Test-PathWithin $streamingAssetsDirectory $installDirectory) -or (Test-PathWithin $installDirectory $streamingAssetsDirectory) -or
            (Test-PathWithin $streamingAssetsDirectory $sourceRoot) -or (Test-PathWithin $sourceRoot $streamingAssetsDirectory))) {
            Stop-Install "Upgrade paths overlap the source checkout, host install, StreamingAssets, or Mods directory; refusing unsafe replacement: host=$installDirectory; mods=$modsDirectory"
        }
        $modDirectory = Join-Path $modsDirectory 'ThunderRoadMcp'
        if ($Upgrade -and ((Test-PathWithin $launchConfigPath $installDirectory) -or
            (Test-PathWithin $bridgeConfigPath $installDirectory) -or
            (Test-PathWithin $launchConfigPath $modDirectory) -or
            (Test-PathWithin $bridgeConfigPath $modDirectory))) {
            Stop-Install 'A launch or bridge config is inside a directory being replaced; move the config outside the host or mod directory first.'
        }
        if (Test-Path -LiteralPath $modDirectory) {
            if (-not $Upgrade -or -not (Test-Path -LiteralPath $modDirectory -PathType Container) -or
                (Test-DirectoryReparsePoint $modDirectory) -or
                -not (Test-Path -LiteralPath (Join-Path $modDirectory 'ThunderRoadMcp.Bridge.dll') -PathType Leaf) -or
                -not (Test-Path -LiteralPath (Join-Path $modDirectory 'manifest.json') -PathType Leaf)) {
                Stop-Install "Bridge destination is not a recognized ThunderRoadMcp installation; refusing to replace it: $modDirectory"
            }
            try { $existingModManifest = Get-Content -LiteralPath (Join-Path $modDirectory 'manifest.json') -Raw | ConvertFrom-Json }
            catch { Stop-Install "Existing bridge manifest is invalid; refusing to replace it: $modDirectory ($($_.Exception.Message))" }
            if ([string]$existingModManifest.Name -ne 'ThunderRoadMcp') {
                Stop-Install "Bridge destination manifest does not identify ThunderRoadMcp; refusing to replace it: $modDirectory"
            }
        } elseif ($Upgrade) {
            Stop-Install "Selected game has no existing ThunderRoadMcp bridge to upgrade: $modDirectory. Use -SkipBridge for a host-only upgrade."
        }

        if (Test-Path -LiteralPath $prebuiltBridgeZip -PathType Leaf) {
            Write-Host "Using the prebuilt bridge package in $prebuiltBridgeZip ..."
            if (Test-Path -LiteralPath $bridgeMetadataPath -PathType Leaf) {
                try {
                    $bridgeMetadata = Get-Content -LiteralPath $bridgeMetadataPath -Raw | ConvertFrom-Json
                    $bridgeGameVersion = [string]$bridgeMetadata.latest
                    if ([string]::IsNullOrWhiteSpace($bridgeGameVersion)) {
                        Write-Warning "Bridge version metadata has no 'latest' game version; continuing with the prebuilt bridge: $bridgeMetadataPath"
                    } else {
                        Write-Host "Using prebuilt bridge for game $bridgeGameVersion"
                    }
                } catch {
                    Write-Warning "Could not read bridge version metadata; continuing with the prebuilt bridge: $bridgeMetadataPath ($($_.Exception.Message))"
                }
            }
            $extractedBridge = Join-Path $bridgeOutput 'extracted'
            Expand-Archive -LiteralPath $prebuiltBridgeZip -DestinationPath $extractedBridge -Force
            if (-not (Test-Path -LiteralPath (Join-Path $extractedBridge 'ThunderRoadMcp.Bridge.dll') -PathType Leaf)) {
                Stop-Install "Prebuilt bridge package is missing ThunderRoadMcp.Bridge.dll: $prebuiltBridgeZip"
            }
            $bridgeZipPath = $prebuiltBridgeZip
            $modInventory = Get-DirectoryInventory $extractedBridge
            $bridgeReady = $true
        } elseif ($sdkAvailable) {
            if (-not (Test-Path -LiteralPath $packageScript -PathType Leaf)) {
                Stop-Install "Bridge packaging script not found: $packageScript. Use a release package or a complete source checkout."
            }
            Write-Host "Packaging bridge for $managedDirectory ..."
            & $packageScript -GameManagedDir $managedDirectory -OutputDirectory $bridgeOutput -DotnetPath $sdkDotnet
            $bridgeZipPath = Join-Path $bridgeOutput 'ThunderRoadMcp.zip'
            $packageDirectory = Join-Path $bridgeOutput 'ThunderRoadMcp'
            if (-not (Test-Path -LiteralPath $bridgeZipPath -PathType Leaf) -or -not (Test-Path -LiteralPath $packageDirectory -PathType Container)) {
                Stop-Install 'Bridge package was not created.'
            }
            $modInventory = Get-DirectoryInventory $packageDirectory
            $bridgeReady = $true
        } else {
            if ($Upgrade) {
                Stop-Install "Cannot upgrade the existing bridge: no prebuilt bridge package is present and no .NET 10 SDK was found to build one. Nothing was changed. Install the .NET 10 SDK or use a release package, or pass -SkipBridge for a host-only upgrade."
            }
            Write-Warning ".NET 10 SDK was not found, so the bridge was not built. Installing the MCP host only; no bridge mod or bridge config will be created. Install the .NET 10 SDK and rerun with a new -InstallDirectory to add the bridge."
        }
    }

    $newLaunchConfig = $null
    if ($launchOptions) {
        if ($selectedPath -and -not [string]::Equals([IO.Path]::GetFullPath([string]$launchOptions.executablePath), $selectedPath, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Install "Selected game conflicts with the existing launch config; preserving it: $launchConfigPath"
        }
    } elseif (-not $Upgrade -and $selectedPath -and ($GameExecutablePath -or $env:THUNDERROAD_MCP_LAUNCH_CONFIG)) {
        $newLaunchConfig = [ordered]@{ executablePath = $selectedPath; arguments = @() }
    }

    if (-not $Upgrade -and $bridgeReady -and -not $bridgeConfigExists -and -not $NoBridgeConfig) {
        $randomBytes = New-Object byte[] 32
        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($randomBytes) } finally { $rng.Dispose() }
        $token = [BitConverter]::ToString($randomBytes).Replace('-', '').ToLowerInvariant()
        $newBridgeConfig = [ordered]@{ enabled = $true; host = '127.0.0.1'; port = 47831; token = $token }
    }

    $configId = [Guid]::NewGuid().ToString('N')
    if ($newLaunchConfig) {
        if (Test-Path -LiteralPath $launchConfigPath) { Stop-Install "Launch config appeared during installation; preserving it: $launchConfigPath" }
        $stagedConfigs.Add((Write-StagedText $launchConfigPath ($newLaunchConfig | ConvertTo-Json -Depth 4) $configId $createdDirectories))
    }
    if ($newBridgeConfig) {
        if (Test-Path -LiteralPath $bridgeConfigPath) { Stop-Install "Bridge config appeared during installation; preserving it: $bridgeConfigPath" }
        $stagedConfigs.Add((Write-StagedText $bridgeConfigPath ($newBridgeConfig | ConvertTo-Json -Depth 4) $configId $createdDirectories))
    }

    $publishedExe = Join-Path $installDirectory 'ThunderRoadMcp.Host.exe'
    $agentServer = [ordered]@{ command = $publishedExe; args = @() }
    $environmentValues = [ordered]@{}
    if ($env:THUNDERROAD_MCP_CONFIG) { $environmentValues['THUNDERROAD_MCP_CONFIG'] = [IO.Path]::GetFullPath($env:THUNDERROAD_MCP_CONFIG) }
    if ($env:THUNDERROAD_MCP_LAUNCH_CONFIG) { $environmentValues['THUNDERROAD_MCP_LAUNCH_CONFIG'] = [IO.Path]::GetFullPath($env:THUNDERROAD_MCP_LAUNCH_CONFIG) }
    if ($environmentValues.Count -gt 0) { $agentServer['env'] = $environmentValues }
    $agentConfiguration = [ordered]@{ mcpServers = [ordered]@{ ThunderRoadMcp = $agentServer } }
    $agentJson = ConvertTo-Json -InputObject $agentConfiguration -Depth 8
    $existingAgentConfig = Join-Path $installDirectory 'mcp-config.json'
    if ($Upgrade -and (Test-Path -LiteralPath $existingAgentConfig -PathType Leaf)) {
        Copy-Item -LiteralPath $existingAgentConfig -Destination (Join-Path $stagedHost 'mcp-config.json') -Force
    } else {
        [IO.File]::WriteAllText((Join-Path $stagedHost 'mcp-config.json'), $agentJson, [System.Text.UTF8Encoding]::new($false))
    }
    $hostInventory = Get-DirectoryInventory $stagedHost

    if ($Upgrade) {
        if ($bridgeReady -and (Test-DirectoryReparsePoint $modsDirectory)) {
            Stop-Install "Mods directory or one of its parents is a reparse point; refusing unsafe upgrade: $modsDirectory"
        }
        Assert-UpgradeProcessesStopped $installDirectory $(if ($bridgeReady) { $selectedPath } else { $null })
        if ($bridgeReady) {
            $syntheticGame = Join-Path $tempRoot 'validation-game\BladeAndSorcery.exe'
            $syntheticMods = Join-Path $tempRoot 'validation-game\BladeAndSorcery_Data\StreamingAssets\Mods'
            [void][IO.Directory]::CreateDirectory($syntheticMods)
            [IO.File]::WriteAllText($syntheticGame, 'synthetic bridge validation target')
            $installOutput = & $stagedHostExe --install-bridge $bridgeZipPath --game-executable $syntheticGame 2> $diagnostics
            if ($LASTEXITCODE -ne 0) {
                $errorText = if (Test-Path -LiteralPath $diagnostics) { Get-Content -LiteralPath $diagnostics -Raw } else { '' }
                Stop-Install "Bridge package validation failed before upgrade: $errorText"
            }
            try { $installResult = ($installOutput -join [Environment]::NewLine) | ConvertFrom-Json }
            catch { Stop-Install "Host bridge installer returned invalid JSON: $($installOutput -join [Environment]::NewLine)" }
            $stagedMod = Join-Path $syntheticMods 'ThunderRoadMcp'
            if ($installResult.installed -ne $true -or
                -not [string]::Equals([IO.Path]::GetFullPath([string]$installResult.destination), [IO.Path]::GetFullPath($stagedMod), [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Install 'Host bridge installer did not confirm staged package validation.'
            }
            $modInventory = Get-DirectoryInventory $stagedMod
        }
    }

    if ($Upgrade) {
        $upgradeId = [Guid]::NewGuid().ToString('N')
        $hostBackupRoot = Join-Path (Split-Path -Parent $installDirectory) '.ThunderRoadMcp-backups'
        $hostBackup = Join-Path (Join-Path $hostBackupRoot $upgradeId) 'host'
        $bridgeBackup = $null
        $stagedModOnVolume = $null
        if ($bridgeReady) {
            $streamingAssets = Split-Path -Parent $modsDirectory
            if (-not (Test-Path -LiteralPath $streamingAssets -PathType Container) -or (Test-DirectoryReparsePoint $modsDirectory)) {
                Stop-Install "Mods or StreamingAssets directory is missing or reparse-backed; refusing unsafe bridge swap: $modsDirectory"
            }
            $bridgeBackup = Join-Path (Join-Path (Join-Path $streamingAssets '.ThunderRoadMcp-backups') $upgradeId) 'ThunderRoadMcp'
            $bridgeStagingRoot = Join-Path $streamingAssets ('.ThunderRoadMcp-upgrade-' + $upgradeId)
            $stagedModOnVolume = Join-Path (Join-Path $bridgeStagingRoot 'Mods') 'ThunderRoadMcp'
            [void][IO.Directory]::CreateDirectory($stagedModOnVolume)
            Copy-Item -Path (Join-Path $stagedMod '*') -Destination $stagedModOnVolume -Recurse
            if (-not (Test-DirectoryInventory $stagedModOnVolume $modInventory)) {
                Stop-Install 'Staged bridge copy failed validation; existing installations were not changed.'
            }
        }
        $backupBases = @((Join-Path (Split-Path -Parent $installDirectory) '.ThunderRoadMcp-backups'))
        if ($bridgeReady) { $backupBases += (Join-Path $streamingAssets '.ThunderRoadMcp-backups') }
        foreach ($backupBase in $backupBases) {
            if ((Test-Path -LiteralPath $backupBase) -and
                (-not (Test-Path -LiteralPath $backupBase -PathType Container) -or (Test-DirectoryReparsePoint $backupBase))) {
                Stop-Install "Backup path exists but is not a safe directory: $backupBase"
            }
        }
        $hostOldMoved = $false
        $bridgeOldMoved = $false
        $hostNewMoved = $false
        $bridgeNewMoved = $false
        try {
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $hostBackup))
            if (Test-Path -LiteralPath $hostBackup) { Stop-Install "Backup path already exists: $hostBackup" }
            [IO.Directory]::Move($installDirectory, $hostBackup)
            $hostOldMoved = $true
            if ($bridgeReady) {
                [void][IO.Directory]::CreateDirectory((Split-Path -Parent $bridgeBackup))
                if (Test-Path -LiteralPath $bridgeBackup) { Stop-Install "Backup path already exists: $bridgeBackup" }
                [IO.Directory]::Move($modDirectory, $bridgeBackup)
                $bridgeOldMoved = $true
            }
            [IO.Directory]::Move($stagedHost, $installDirectory)
            $hostNewMoved = $true
            if ($bridgeReady) {
                [IO.Directory]::Move($stagedModOnVolume, $modDirectory)
                $bridgeNewMoved = $true
            }
        } catch {
            $transactionError = $_.Exception.Message
            $rollbackErrors = New-Object 'System.Collections.Generic.List[string]'
            if ($bridgeNewMoved) {
                if (Test-DirectoryInventory $modDirectory $modInventory) {
                    try { [IO.Directory]::Move($modDirectory, $stagedModOnVolume) } catch { $rollbackErrors.Add("could not move new bridge from ${modDirectory}: $($_.Exception.Message)") }
                } else { $rollbackErrors.Add("new bridge changed at $modDirectory") }
            }
            if ($hostNewMoved) {
                if (Test-DirectoryInventory $installDirectory $hostInventory) {
                    try { [IO.Directory]::Move($installDirectory, $stagedHost) } catch { $rollbackErrors.Add("could not move new host from ${installDirectory}: $($_.Exception.Message)") }
                } else { $rollbackErrors.Add("new host changed at $installDirectory") }
            }
            if ($bridgeOldMoved) {
                if (-not (Test-Path -LiteralPath $modDirectory)) {
                    try { [IO.Directory]::Move($bridgeBackup, $modDirectory) } catch { $rollbackErrors.Add("could not restore bridge from ${bridgeBackup}: $($_.Exception.Message)") }
                } else { $rollbackErrors.Add("bridge destination occupied; original preserved at $bridgeBackup") }
            }
            if ($hostOldMoved) {
                if (-not (Test-Path -LiteralPath $installDirectory)) {
                    try { [IO.Directory]::Move($hostBackup, $installDirectory) } catch { $rollbackErrors.Add("could not restore host from ${hostBackup}: $($_.Exception.Message)") }
                } else { $rollbackErrors.Add("host destination occupied; original preserved at $hostBackup") }
            }
            if ($rollbackErrors.Count -gt 0) {
                Stop-Install "Upgrade failed: $transactionError. Rollback was blocked: $($rollbackErrors -join '; '). Recover from backups: host=$hostBackup; bridge=$bridgeBackup"
            }
            Stop-Install "Upgrade failed and both original components were restored: $transactionError"
        }
        $completed = $true
        Write-Host "Upgrade backups retained: host=$hostBackup$(if ($bridgeBackup) { '; bridge=' + $bridgeBackup } else { '' })"
        Write-Host "Preserved launch/bridge configuration paths: $launchConfigPath; $bridgeConfigPath"
        Write-Host "Installed host: $publishedExe"
        Write-Host "Copyable MCP config: $(Join-Path $installDirectory 'mcp-config.json')"
        Write-Host 'MCP stdio configuration preserved or generated; details not printed.'
        if ($bridgeReady) { Write-Host 'Bridge upgraded. Restart the game before using it.' }
        else { Write-Host 'Host upgraded only; bridge unchanged by -SkipBridge.' }
    } else {
        foreach ($configuration in $stagedConfigs) {
            if (Test-Path -LiteralPath $configuration.Target) { Stop-Install "Config appeared during installation; preserving it: $($configuration.Target)" }
        }
        if (Test-Path -LiteralPath $installDirectory) { Stop-Install "Host install directory appeared during installation; preserving it: $installDirectory" }
        [IO.Directory]::Move($stagedHost, $installDirectory)
        $createdHost = $true
        if ($bridgeReady) {
            Write-Host 'Installing bridge through the host safe-install path...'
            $installOutput = & $publishedExe --install-bridge $bridgeZipPath --game-executable $selectedPath 2> $diagnostics
            if ($LASTEXITCODE -ne 0) {
                $errorText = if (Test-Path -LiteralPath $diagnostics) { Get-Content -LiteralPath $diagnostics -Raw } else { '' }
                Stop-Install "Bridge installation failed: $errorText"
            }
            $createdMod = $true
            try { $installResult = ($installOutput -join [Environment]::NewLine) | ConvertFrom-Json }
            catch { Stop-Install "Host bridge installer returned invalid JSON: $($installOutput -join [Environment]::NewLine)" }
            if ($installResult.installed -ne $true -or
                -not [string]::Equals([IO.Path]::GetFullPath([string]$installResult.destination), [IO.Path]::GetFullPath($modDirectory), [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Install 'Host bridge installer did not confirm installation at the requested destination.'
            }
        }
        foreach ($configuration in $stagedConfigs) {
            [IO.File]::Move($configuration.Stage, $configuration.Target)
            $configuration.Committed = $true
        }
        $completed = $true
        if ($newLaunchConfig) { Write-Host "Selected game saved to $launchConfigPath" }
        if ($newBridgeConfig) { Write-Host "Bridge enabled; private config saved to $bridgeConfigPath (token not displayed)." }
        elseif ($bridgeReady -and $bridgeConfig -and -not $bridgeConfig.enabled) { Write-Warning "Existing bridge config is disabled and was preserved. Installer will not enable it: $bridgeConfigPath" }
        elseif ($bridgeReady -and $NoBridgeConfig -and -not $bridgeConfigExists) { Write-Host "Bridge installed but disabled because -NoBridgeConfig was passed. Create a config when you want to opt in: $bridgeConfigPath" }
        Write-Host "`nInstalled host: $publishedExe"
        Write-Host "Copyable MCP config saved to $(Join-Path $installDirectory 'mcp-config.json')"
        Write-Host 'MCP stdio configuration:'
        Write-Host $agentJson
        if ($bridgeReady) { Write-Host "`nRestart the game before using the bridge; installation does not launch or modify a running game." }
        else { Write-Host "`nThe bridge was not installed. Installation did not launch or modify the game." }
    }

} finally {
    if (-not $completed) {
        foreach ($configuration in $stagedConfigs) { Remove-OwnedFile $configuration }
        if ($createdMod -and (Test-DirectoryInventory $modDirectory $modInventory)) {
            Remove-Item -LiteralPath $modDirectory -Recurse -Force
        } elseif ($createdMod) {
            Write-Warning "Bridge destination changed after installation; preserving it during rollback: $modDirectory"
        }
        if ($createdHost -and (Test-DirectoryInventory $installDirectory $hostInventory)) {
            Remove-Item -LiteralPath $installDirectory -Recurse -Force
        } elseif ($createdHost) {
            Write-Warning "Host directory changed after installation; preserving it during rollback: $installDirectory"
        }
    }
    foreach ($configuration in $stagedConfigs) {
        if (Test-Path -LiteralPath $configuration.Stage -PathType Leaf) { Remove-Item -LiteralPath $configuration.Stage -Force }
    }
    if ($stagedModOnVolume -and (Test-Path -LiteralPath $stagedModOnVolume)) {
        if (Test-DirectoryInventory $stagedModOnVolume $modInventory) {
            Remove-Item -LiteralPath $stagedModOnVolume -Recurse -Force
        } else {
            Write-Warning "Preserving changed staged bridge data during cleanup: $stagedModOnVolume"
        }
    }
    if ($bridgeStagingRoot -and (Test-Path -LiteralPath $bridgeStagingRoot -PathType Container)) {
        foreach ($directory in @((Join-Path $bridgeStagingRoot 'Mods'), $bridgeStagingRoot)) {
            if ((Test-Path -LiteralPath $directory -PathType Container) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) {
                Remove-Item -LiteralPath $directory -Force
            }
        }
    }
    if ($tempRoot -and (Test-Path -LiteralPath $tempRoot)) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
    if (-not $completed) {
        for ($i = $createdDirectories.Count - 1; $i -ge 0; $i--) {
            $directory = $createdDirectories[$i]
            if ((Test-Path -LiteralPath $directory -PathType Container) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) {
                Remove-Item -LiteralPath $directory -Force
            }
        }
    }
}
