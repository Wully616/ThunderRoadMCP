[CmdletBinding()]
param(
    [string]$GameExecutablePath,
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'ThunderRoadMcp\host'),
    [string]$DotnetPath,
    [switch]$SkipBridge,
    [switch]$NoBridgeConfig,
    [switch]$NonInteractive
)

$arguments = @{
    InstallDirectory = $InstallDirectory
    Upgrade = $true
}
foreach ($name in @('GameExecutablePath', 'DotnetPath', 'SkipBridge', 'NoBridgeConfig', 'NonInteractive')) {
    $value = Get-Variable -Name $name -ValueOnly
    if ($value -is [System.Management.Automation.SwitchParameter]) {
        if ($value) { $arguments[$name] = $true }
    } elseif (-not [string]::IsNullOrWhiteSpace([string]$value)) {
        $arguments[$name] = $value
    }
}
& (Join-Path $PSScriptRoot 'install.ps1') @arguments
