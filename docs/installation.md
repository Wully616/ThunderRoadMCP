# Installation

There are two install paths:

- **Release ZIP:** prebuilt host and bridge. Requires the .NET 10 runtime, not the SDK.
- **Source checkout:** builds the host and, when a game is found, the bridge. Requires the .NET 10 SDK and runtime.

Satisfy [Requirements](../README.md#requirements) first. Blade & Sorcery is needed for the bridge. Without the game, the installer can install the host only.

## Release install

Download and extract a release ZIP from the repository's GitHub releases. Open Windows PowerShell 5.1 in the extracted folder:

```powershell
.\scripts\install.ps1
```

If Windows blocks the script, use `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1` for that PowerShell process only. For a downloaded ZIP that Windows marks as blocked, `Unblock-File .\scripts\install.ps1` also clears the mark.

The release ZIP contains `host/` with the prebuilt host, `bridge/` with the prebuilt bridge and version metadata, and `scripts/`. The installer uses those prebuilt files and does not need the SDK. See [CI and release workflows](building.md#ci-and-release-workflows) for the full release layout.

- If a game is found, the installer installs the prebuilt bridge through the host's safe-install path.
- If no game is found, it warns and installs the host only. No bridge mod or bridge config is created.
- `-SkipBridge` skips bridge discovery and installation and installs the host only.
- When the bridge mod is installed and no bridge config exists, the installer creates an enabled loopback config with a new random 32-byte hex token. `-NoBridgeConfig` installs the mod but creates no config.
- Existing launch and bridge configs are preserved unchanged. An existing config is never enabled, disabled, or rewritten.

The installer prints the prebuilt bridge provenance, for example `Using prebuilt bridge for game 1.3.2`. See [prebuilt bridge compatibility](#prebuilt-bridge-compatibility).

## Source checkout install

Clone or download the source into a folder, then run the same command from the repository root:

```powershell
.\scripts\install.ps1
```

A source checkout requires the .NET 10 SDK. The installer publishes the host and, when a game is found, builds the bridge against that game's `Managed` assemblies. Without a game, it installs the host only. Source installs also require the .NET 10 runtime to run the host.

## Installer behavior

- Installs the host (prebuilt in a release ZIP, published from source) to `%LOCALAPPDATA%\ThunderRoadMcp\host` by default.
- Installs the bridge mod when a game is selected, through the host's safe-install path.
- Enables the bridge on a fresh install by creating an enabled `bridge.json` with a private token when the mod was installed and no config exists. `-NoBridgeConfig` installs the mod without creating a config, so the bridge stays disabled. Existing configs are preserved unchanged.
- Saves the ready-to-copy MCP stdio configuration as `mcp-config.json` in the host install directory.
- Does not launch the game or modify a running game.
- Refuses to overwrite an existing host directory or bridge mod, and preserves existing launch and bridge configs.

The bridge token is not printed. See [bridge configuration](usage.md#bridge-configuration) and [Security](../README.md#security). Restart the game after installing or changing the bridge mod.

## Game discovery and selection

At run time the host discovers Steam and Meta Horizon Link installations. The installer uses the staged host's `--list-installations` command to apply the same discovery and validation. `-SkipBridge` skips this discovery and installs the host only.

When no launch config selects a game:

- One detected installation is selected automatically.
- Zero detected installations produces a host-only warning, not an error. No bridge mod or bridge config is created.
- Multiple detected installations produce a numbered list sorted by store and executable path, including the executable file version when available, and a prompt to choose. Blank input cancels before any host, mod, or config is written. Pass `-GameExecutablePath` to select directly, or `-NonInteractive` to fail with the candidate list instead of prompting. Prompting is also skipped, with the same failure, when standard input is redirected (for example in CI).

```text
Multiple Blade & Sorcery installations were found:
  1. Steam: E:\SteamLibrary\steamapps\common\Blade & Sorcery\BladeAndSorcery.exe (1.3.2.47651)
  2. Meta: C:\Program Files\Oculus\Software\WarpFrog\Blade & Sorcery\BladeAndSorcery.exe
Select installation [1-2] (blank cancels):
```

Invalid answers re-prompt. `-GameExecutablePath` bypasses the prompt entirely, and `-NonInteractive` is intended for scripts and CI.

An explicit absolute custom path is accepted when it names `BladeAndSorcery.exe` and its sibling `BladeAndSorcery_Data\Managed` contains the game assemblies.

```powershell
.\scripts\install.ps1 -GameExecutablePath 'D:\Games\BladeAndSorcery\BladeAndSorcery.exe'
```

The host also exposes `game_installations` after installation. An existing `launch.json` is never silently switched. If `-GameExecutablePath` conflicts with it, the installer refuses and preserves the config.

Choose a different new host directory with `-InstallDirectory`:

```powershell
.\scripts\install.ps1 -InstallDirectory 'D:\Tools\ThunderRoadMcp\host'
```

The installer refuses an existing host directory. Move or remove it yourself after preserving anything you need, or use `upgrade.ps1` for an existing ThunderRoadMcp installation (see [Update](#update)).

## Default paths

| Purpose | Default path |
| --- | --- |
| Host install directory | `%LOCALAPPDATA%\ThunderRoadMcp\host` |
| Launch configuration | `%LOCALAPPDATA%\ThunderRoadMcp\launch.json` |
| Bridge configuration | `%LOCALAPPDATA%\ThunderRoadMcp\bridge.json` |
| Bridge mod | `<game>\BladeAndSorcery_Data\StreamingAssets\Mods\ThunderRoadMcp` |
| Generated MCP config | `<host install directory>\mcp-config.json` |

`THUNDERROAD_MCP_LAUNCH_CONFIG` overrides the launch config path. `THUNDERROAD_MCP_CONFIG` overrides the bridge config path. The host and game must use the same bridge config path.

A launch config selects a game explicitly:

```json
{
  "executablePath": "D:/Games/BladeAndSorcery/BladeAndSorcery.exe",
  "arguments": []
}
```

If no launch config exists, installation writes the selected executable path and an empty argument array to the default or overridden launch-config path. Selection may be explicit (`-GameExecutablePath`), the sole detected install, or an interactive choice. A host-only install without a selected game writes no launch config. Existing launch configs are preserved. Arguments are passed as an array, never through a shell.

## Prebuilt bridge compatibility

The tracked package is built against Blade & Sorcery 1.3.2. The release ZIP includes it as `bridge/ThunderRoadMcp.zip` with `bridge/versions.json`. The installer prints the latest tracked game version from that metadata.

Compatibility is informational. The installer does not compare or block against the locally detected game version. Newer game versions may require a new tracked package or a local source build. See [tracked bridge packages](building.md#tracked-bridge-packages).

## Generated stdio configuration

The installer prints the generated configuration and saves the same content to `mcp-config.json` in the host install directory. The server entry uses the absolute installed `ThunderRoadMcp.Host.exe` as `command` with `args: []`. It never prints the bridge token and never edits an agent configuration.

Add the server entry to your harness's own MCP configuration location and format. The host is a stdio MCP server; use the harness's documented stdio setup. The generic JSON shape and an example are in the [README quick start](../README.md#connect-an-agent).

`ThunderRoadMcp.Host.exe` is a framework-dependent apphost, not self-contained. It still requires the .NET 10 runtime, but it is launched directly instead of through `dotnet`.

If `THUNDERROAD_MCP_CONFIG` or `THUNDERROAD_MCP_LAUNCH_CONFIG` is set during installation, the generated configuration includes those environment overrides. A game launched by the host inherits its environment. For a game started separately, set the same bridge config path in the Windows user or system environment before launching it.

## Update

There is an in-place upgrader for an existing host installation and bridge mod. Before running it, stop the MCP host/client and close Blade & Sorcery; the upgrader refuses a running host or locked destination rather than terminating processes. Restart the game after upgrading its bridge.

Run from either a source checkout or an extracted release ZIP:

```powershell
.\scripts\upgrade.ps1
```

The default host path is `%LOCALAPPDATA%\ThunderRoadMcp\host`. Select a custom existing host and game when needed:

```powershell
.\scripts\upgrade.ps1 -InstallDirectory 'D:\Tools\ThunderRoadMcp\host' -GameExecutablePath 'D:\Games\BladeAndSorcery\BladeAndSorcery.exe'
```

Source upgrades require the .NET 10 SDK; release ZIP upgrades use the prebuilt host and bridge and require the .NET 10 runtime. `-DotnetPath` selects an SDK explicitly, `-NonInteractive` disables game-selection prompts, and `-SkipBridge` upgrades only the host and leaves the bridge mod unchanged. A full upgrade requires the selected game's existing ThunderRoadMcp mod. Set `THUNDERROAD_MCP_LAUNCH_CONFIG` and `THUNDERROAD_MCP_CONFIG` as needed to select custom config paths; existing configs are preserved byte-for-byte, including disabled bridge state and token. If a game is selected and no launch config exists, the selected executable path is persisted. Existing `mcp-config.json` and unrelated game mods/files are also retained.
`-NoBridgeConfig` is accepted for command-line symmetry with installation but never creates or changes config during an upgrade. Full upgrades retain an existing bridge config exactly as-is, including disabled state; if it is absent, it remains absent.

The upgrader validates inputs before replacement. Host backups are written to `.ThunderRoadMcp-backups` next to the host directory; bridge backups are written to `StreamingAssets\.ThunderRoadMcp-backups`, outside `Mods`. The command prints both retained backup paths. If replacement fails, it attempts to restore the old installation; if restoration is blocked, it reports that and leaves the backups for manual recovery. Retain the reported backup until the updated host and mod have been confirmed. Rollback cannot recover from external disk/device failure or user deletion of the backup.

## Uninstall

Stop or disconnect the MCP server and close the game. Remove the host directory shown by the installer and `ThunderRoadMcp` from the selected game's `BladeAndSorcery_Data\StreamingAssets\Mods` directory. Keep `launch.json` and `bridge.json` if you want to retain the selected game and token. Remove them manually only if you no longer need those settings. Remove any custom environment overrides from your agent configuration yourself.

## Installation troubleshooting

- **Missing .NET runtime:** release and source installs both need the .NET 10 runtime. Install it from the stable .NET 10 download and check `dotnet --list-runtimes`. The installer reports a missing runtime with a download link.
- **Missing .NET SDK:** only source checkout installs and local bridge builds need the SDK. A source checkout publishes the host and builds the bridge; it does not reuse `bin/` or `artifacts/`. Install the .NET 10 SDK, or use a release ZIP, which is the no-SDK path. The installer searches `PATH`, `DOTNET_ROOT`, `%USERPROFILE%\.dotnet`, and the Program Files dotnet locations and prints the candidates it checked on failure. Force a specific SDK with `-DotnetPath 'C:\Users\<you>\.dotnet\dotnet.exe'`.
- **No game found:** this is expected to produce a host-only install. Install Blade & Sorcery, then rerun with a new `-InstallDirectory`, or install `bridge/ThunderRoadMcp.zip` with `game_install_mod` and create a bridge config. `-SkipBridge` intentionally produces a host-only install.
- **`-NoBridgeConfig` installed the bridge but it is offline:** this is intentional. Create a bridge config as shown in [bridge configuration](usage.md#bridge-configuration) when you want to enable it.
- **An existing disabled `bridge.json` stays disabled:** the installer never enables or rewrites an existing config, even without `-NoBridgeConfig`. Edit or replace the file yourself if you intend to enable it.
- **Multiple game candidates:** pass `-GameExecutablePath` with an absolute `BladeAndSorcery.exe`. For a custom path, its sibling `BladeAndSorcery_Data\Managed` must contain the game assemblies.
- **Script is blocked:** use the single-process execution-policy fallback above or run `Unblock-File .\scripts\install.ps1`.
- **Destination already exists:** a fresh install (`install.ps1`) does not overwrite host or mod directories; use `upgrade.ps1` for an existing ThunderRoadMcp installation, or back up and remove the conflicting directory yourself before a fresh install.
- **Host does not start or has no tools:** use the absolute EXE `command` with empty `args` from `mcp-config.json`, confirm the .NET 10 runtime is installed, then restart or reconnect the agent.
- **Bridge is offline after installation:** check that `bridge.json` exists and is enabled. The installer creates it by default when it installs the bridge mod, but leaves it absent with `-NoBridgeConfig` and leaves an existing disabled config disabled. See [runtime troubleshooting](usage.md#runtime-troubleshooting).
